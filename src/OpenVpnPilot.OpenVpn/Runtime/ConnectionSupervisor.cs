using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Management;

namespace OpenVpnPilot.OpenVpn.Runtime;

/// <summary>
/// Owns the lifecycle of a single tunnel: the process, its management session and its telemetry.
/// </summary>
/// <remarks>
/// One instance drives one profile. Several may run at once, each with its own process and its own
/// management port, which is what allows more than one tunnel to be connected simultaneously.
/// </remarks>
public sealed class ConnectionSupervisor : IAsyncDisposable
{
    private readonly IOpenVpnLauncher launcher;
    private readonly IManagementChannelFactory channelFactory;
    private readonly ICredentialProvider credentialProvider;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ConnectionSupervisor> logger;
    private readonly SemaphoreSlim transition = new(1, 1);

    private ManagementClient? client;
    private CancellationTokenSource? session;
    private Task? pump;
    private VpnConnectionStatus status = VpnConnectionStatus.Disconnected;
    private readonly HashSet<string> rejectedRealms = new(StringComparer.Ordinal);
    private bool disposed;

    public ConnectionSupervisor(
        IOpenVpnLauncher launcher,
        IManagementChannelFactory channelFactory,
        ICredentialProvider credentialProvider,
        TimeProvider? timeProvider = null,
        ILogger<ConnectionSupervisor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(channelFactory);
        ArgumentNullException.ThrowIfNull(credentialProvider);

        this.launcher = launcher;
        this.channelFactory = channelFactory;
        this.credentialProvider = credentialProvider;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<ConnectionSupervisor>.Instance;
    }

    /// <summary>
    /// Raised whenever any part of the status changes, including the throughput counters. Suitable
    /// for a live view. Handlers run on the caller's thread, so a user interface must marshal to its
    /// own dispatcher.
    /// </summary>
    public event EventHandler<VpnConnectionStatus>? StatusChanged;

    /// <summary>
    /// Raised only when the lifecycle state itself changes, so that callers which care about
    /// transitions are not woken by every telemetry tick.
    /// </summary>
    public event EventHandler<VpnConnectionStatus>? StateChanged;

    /// <summary>
    /// Raised for every log line OpenVPN emits while connected.
    /// </summary>
    public event EventHandler<LogMessage>? LogReceived;

    public VpnConnectionStatus Status => status;

    public int? ProcessId { get; private set; }

    /// <summary>
    /// Starts the tunnel and returns once it is connected or has failed.
    /// </summary>
    public async Task<VpnConnectionStatus> ConnectAsync(
        ConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);

        await transition.WaitAsync(cancellationToken);
        try
        {
            if (status.State is not (VpnConnectionState.Disconnected or VpnConnectionState.Failed))
            {
                throw new InvalidOperationException($"The connection is already {status.State}.");
            }

            rejectedRealms.Clear();
            Publish(status with { State = VpnConnectionState.Launching, Message = string.Empty });

            string managementPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

            OpenVpnLaunchResult launch = await launcher.LaunchAsync(
                new OpenVpnLaunchRequest(
                    request.ConfigurationPath,
                    request.WorkingDirectory,
                    request.ManagementPort,
                    managementPassword,
                    request.AdditionalOptions,
                    request.LogPath),
                cancellationToken);

            if (!launch.Succeeded)
            {
                Publish(VpnConnectionStatus.Disconnected with
                {
                    State = VpnConnectionState.Failed,
                    Message = launch.Message,
                });

                return status;
            }

            ProcessId = launch.ProcessId;

            Stream channel = await channelFactory.ConnectAsync(request.ManagementPort, cancellationToken);
            client = new ManagementClient(channel);

            await client.StartAsync(managementPassword, cancellationToken);
            await client.OpenSessionAsync(cancellationToken: cancellationToken);

            Publish(status with { State = VpnConnectionState.Connecting });

            session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pump = Task.Run(() => PumpAsync(request, session.Token), CancellationToken.None);

            return status;
        }
        finally
        {
            transition.Release();
        }
    }

    /// <summary>
    /// Stops the tunnel. Safe to call when nothing is running.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await transition.WaitAsync(cancellationToken);
        try
        {
            if (client is null)
            {
                return;
            }

            Publish(status with { State = VpnConnectionState.Disconnecting });

            try
            {
                await client.SignalAsync("SIGTERM", cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The process already exited, which is the outcome the signal was asking for.
            }

            await TearDownAsync();
            Publish(VpnConnectionStatus.Disconnected);
        }
        finally
        {
            transition.Release();
        }
    }

    private async Task PumpAsync(ConnectionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ManagementMessage message in client!.Notifications.ReadAllAsync(cancellationToken))
            {
                await HandleAsync(request, message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect requested.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            ConnectionSupervisorLog.PumpFailed(logger, exception);
            Publish(status with { State = VpnConnectionState.Failed, Message = exception.Message });
        }
    }

    private async Task HandleAsync(
        ConnectionRequest request,
        ManagementMessage message,
        CancellationToken cancellationToken)
    {
        switch (message)
        {
            case HoldMessage:
                // OpenVPN ignores a release sent before it reports being held, so it happens here.
                await client!.ReleaseHoldAsync(cancellationToken);
                break;

            case StateMessage state:
                ApplyState(state);
                break;

            case ByteCountMessage counters:
                Publish(status with
                {
                    BytesReceived = counters.BytesIn,
                    BytesSent = counters.BytesOut,
                });
                break;

            case PasswordRequestMessage credentials:
                await AnswerCredentialRequestAsync(request, credentials, cancellationToken);
                break;

            case PasswordVerificationFailedMessage rejection:
                rejectedRealms.Add(rejection.Realm);
                ConnectionSupervisorLog.CredentialsRejected(logger, rejection.Realm);
                break;

            case FatalMessage fatal:
                // A failure the client already diagnosed keeps its own wording. OpenVPN reports the
                // shutdown that follows as a fatal error, which would otherwise replace the useful
                // explanation with an internal one.
                if (status.State != VpnConnectionState.Failed)
                {
                    Publish(status with { State = VpnConnectionState.Failed, Message = fatal.Text });
                }

                break;

            case LogMessage log:
                LogReceived?.Invoke(this, log);
                break;

            default:
                break;
        }
    }

    // OpenVPN reports many intermediate states. Only the ones the user acts on are surfaced.
    private void ApplyState(StateMessage state)
    {
        switch (state.Name)
        {
            case "CONNECTED":
                Publish(status with
                {
                    State = VpnConnectionState.Connected,
                    ConnectedSince = timeProvider.GetUtcNow(),
                    LocalAddress = state.LocalAddress,
                    ServerAddress = state.RemoteAddress,
                    ServerPort = state.RemotePort,
                    Message = string.Empty,
                });
                break;

            case "RECONNECTING":
                Publish(status with
                {
                    State = VpnConnectionState.Reconnecting,
                    ConnectedSince = null,
                    Message = state.Description ?? string.Empty,
                });
                break;

            case "EXITING":
                Publish(status with { State = VpnConnectionState.Disconnected, ConnectedSince = null });
                break;

            case "AUTH":
                Publish(status with { State = VpnConnectionState.Authenticating });
                break;

            case "ASSIGN_IP":
                Publish(status with { LocalAddress = state.LocalAddress ?? status.LocalAddress });
                break;

            default:
                if (status.State is not (VpnConnectionState.Connected or VpnConnectionState.Reconnecting))
                {
                    Publish(status with { State = VpnConnectionState.Connecting });
                }

                break;
        }
    }

    private async Task AnswerCredentialRequestAsync(
        ConnectionRequest request,
        PasswordRequestMessage message,
        CancellationToken cancellationToken)
    {
        Publish(status with { State = VpnConnectionState.Authenticating });

        CredentialRequest credentialRequest = new(
            request.ProfileId,
            message.Realm,
            message.NeedsUsername,
            rejectedRealms.Contains(message.Realm));

        VpnCredentials? credentials = await credentialProvider.RequestAsync(credentialRequest, cancellationToken);

        if (credentials is null)
        {
            ConnectionSupervisorLog.CredentialsUnavailable(logger, message.Realm);
            Publish(status with
            {
                State = VpnConnectionState.Failed,
                Message = credentialRequest.IsRetry
                    ? $"The credentials for '{message.Realm}' were rejected by the server."
                    : $"No credentials are available for '{message.Realm}'.",
            });

            await client!.SignalAsync("SIGTERM", cancellationToken);
            return;
        }

        await client!.SendCredentialsAsync(
            message.Realm,
            message.NeedsUsername ? credentials.Username : null,
            credentials.Password,
            cancellationToken);
    }

    private void Publish(VpnConnectionStatus next)
    {
        if (next == status)
        {
            return;
        }

        bool stateChanged = next.State != status.State;
        status = next;

        StatusChanged?.Invoke(this, next);

        if (stateChanged)
        {
            StateChanged?.Invoke(this, next);
        }
    }

    private async Task TearDownAsync()
    {
        if (session is not null)
        {
            await session.CancelAsync();
        }

        if (pump is not null)
        {
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting down.
            }
        }

        if (client is not null)
        {
            await client.DisposeAsync();
        }

        session?.Dispose();
        session = null;
        pump = null;
        client = null;
        ProcessId = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await TearDownAsync();
        transition.Dispose();
    }
}

/// <summary>
/// What a supervisor needs in order to start one tunnel.
/// </summary>
/// <param name="ProfileId">Identifies the profile in credential requests and session records.</param>
/// <param name="ConfigurationPath">The materialised configuration file.</param>
/// <param name="WorkingDirectory">The directory the process starts in.</param>
/// <param name="ManagementPort">A free loopback port reserved by the caller.</param>
/// <param name="AdditionalOptions">Extra command line options, such as protective pull filters.</param>
/// <param name="LogPath">Optional OpenVPN log file.</param>
public sealed record ConnectionRequest(
    Guid ProfileId,
    string ConfigurationPath,
    string WorkingDirectory,
    int ManagementPort,
    IReadOnlyList<string> AdditionalOptions,
    string? LogPath = null);
