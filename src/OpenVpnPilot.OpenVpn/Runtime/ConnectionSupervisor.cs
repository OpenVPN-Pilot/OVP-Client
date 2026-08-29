using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>
    /// How long a signalled process is given to exit before it is terminated.
    /// </summary>
    private static readonly TimeSpan ProcessExitGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What OpenVPN reports as the reason for restarting when it could not apply what was pushed.
    /// </summary>
    private const string PushRefusedReason = "process-push-msg-failed";

    /// <summary>
    /// How long the management interface is given to answer the stop signal.
    /// </summary>
    /// <remarks>
    /// A disconnect must finish whatever the far end does. Without a bound, a process that has
    /// stopped reading its socket leaves the tunnel showing as disconnecting forever, and every
    /// later attempt to stop it waits behind this one.
    /// </remarks>
    private static readonly TimeSpan SignalGrace = TimeSpan.FromSeconds(5);

    private ManagementClient? client;
    private CancellationTokenSource? session;
    private Task? pump;
    private VpnConnectionStatus status = VpnConnectionStatus.Disconnected;
    private readonly HashSet<string> rejectedRealms = new(StringComparer.Ordinal);

    /// <summary>
    /// Set when the attempt cannot succeed however many times OpenVPN tries it again.
    /// </summary>
    private bool abandoned;

    // A dynamic challenge arrives with the refusal of one attempt and is answered in the next, so
    // it has to outlive the attempt that raised it.
    private readonly Dictionary<string, DynamicChallenge> pendingChallenges = new(StringComparer.Ordinal);
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
            pendingChallenges.Clear();
            abandoned = false;
            Publish(status with
            {
                State = VpnConnectionState.Launching,
                Message = string.Empty,
                Failure = VpnFailureKind.None,
                PushedRoutes = [],
                PushedDnsServers = [],
                Gateway = null,
                ServerRequestedDefaultRoute = false,
                ServerRequestedCompression = false,
                PingMilliseconds = null,
                PingFailed = false,
            });

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
                    Failure = VpnFailureKind.LaunchRefused,
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

            if (request.ConnectTimeout is { } limit && limit > TimeSpan.Zero)
            {
                _ = Task.Run(() => AbandonIfNotConnectedAsync(limit, session.Token), CancellationToken.None);
            }

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
    /// <remarks>
    /// This always ends with the connection reported as disconnected, whatever the far end does.
    /// The signal is a courtesy: it asks OpenVPN to shut down cleanly, and everything that can go
    /// wrong with it means the process is already gone or is no longer listening. Letting any of
    /// that escape would leave the tunnel showing as disconnecting with no way back, which is
    /// exactly what stopping several tunnels at once used to produce.
    /// </remarks>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await transition.WaitAsync(cancellationToken);
        try
        {
            if (client is null)
            {
                // Nothing is running. Anything but disconnected would be a state left behind by a
                // previous attempt, so it is corrected here rather than left on screen.
                if (status.State != VpnConnectionState.Disconnected)
                {
                    Publish(VpnConnectionStatus.Disconnected with { Failure = VpnFailureKind.UserRequested });
                }

                return;
            }

            Publish(status with { State = VpnConnectionState.Disconnecting });

            try
            {
                using CancellationTokenSource signal =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                signal.CancelAfter(SignalGrace);

                await client.SignalAsync("SIGTERM", signal.Token);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or IOException or ObjectDisposedException)
            {
                // The process already exited, which is the outcome the signal was asking for.
                ConnectionSupervisorLog.SignalNotDelivered(logger, exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The signal was never answered. Tearing down below ends the process anyway.
                ConnectionSupervisorLog.SignalNotAnswered(logger);
            }
            finally
            {
                await TearDownAsync();
                Publish(VpnConnectionStatus.Disconnected with { Failure = VpnFailureKind.UserRequested });
            }
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

            // A failure that has already been diagnosed keeps its own wording. The channel closing
            // is what stopping the process looks like from here, and reporting that instead would
            // replace the reason with its consequence.
            if (status.State != VpnConnectionState.Failed)
            {
                Publish(status with
                {
                    State = VpnConnectionState.Failed,
                    Message = exception.Message,
                    Failure = VpnFailureKind.ConnectionLost,
                });
            }
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

                // OpenVPN restarts on its own and would go round this loop for as long as it is
                // left running. Reporting a failure without ending the attempt only renames what
                // is happening; the process has to be told to stop.
                if (abandoned)
                {
                    await StopAbandonedAttemptAsync(cancellationToken);
                }

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
                // A dynamic challenge is reported as a refusal, but it is a request for a code
                // rather than a wrong password, so the retry warning is not shown for it.
                if (rejection.Challenge is { } challenge)
                {
                    pendingChallenges[rejection.Realm] = challenge;
                    ConnectionSupervisorLog.ChallengeReceived(logger, rejection.Realm);
                }
                else
                {
                    rejectedRealms.Add(rejection.Realm);
                    ConnectionSupervisorLog.CredentialsRejected(logger, rejection.Realm);
                }

                break;

            case FatalMessage fatal:
                // A failure the client already diagnosed keeps its own wording. OpenVPN reports the
                // shutdown that follows as a fatal error, which would otherwise replace the useful
                // explanation with an internal one.
                if (status.State != VpnConnectionState.Failed)
                {
                    Publish(status with
                    {
                        State = VpnConnectionState.Failed,
                        Message = fatal.Text,
                        Failure = VpnFailureKind.Fatal,
                    });
                }

                break;

            case LogMessage log:
                ApplyPushedOptions(log);
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
                    Failure = VpnFailureKind.None,
                });
                break;

            case "RECONNECTING":
                // A client that refused the pushed options will refuse them again, every time, as
                // fast as it can reconnect. Left alone it flickers between authenticating and
                // failing for good, which reads as a fault in the client rather than in what the
                // server asked for. There is nothing to wait for, so it is called what it is.
                if (state.Description == PushRefusedReason && status.ServerRequestedCompression)
                {
                    abandoned = true;

                    Publish(status with
                    {
                        State = VpnConnectionState.Failed,
                        ConnectedSince = null,
                        Message = "The server pushed a compression setting this client cannot apply, "
                            + "so it refused every option the server sent.",
                        Failure = VpnFailureKind.Unsupported,
                    });

                    break;
                }

                Publish(status with
                {
                    State = VpnConnectionState.Reconnecting,
                    ConnectedSince = null,
                    Message = state.Description ?? string.Empty,
                });
                break;

            case "EXITING":
                // An attempt that was abandoned is exiting because it was told to, and the reason it
                // was told to is the one worth keeping.
                if (abandoned)
                {
                    break;
                }

                // A process that exits while a disconnect is in flight is doing what it was asked.
                // Any other exit is the tunnel going away on its own.
                Publish(status with
                {
                    State = VpnConnectionState.Disconnected,
                    ConnectedSince = null,
                    Failure = status.State == VpnConnectionState.Disconnecting
                        ? VpnFailureKind.UserRequested
                        : VpnFailureKind.ConnectionLost,
                });
                break;

            case "AUTH":
                Publish(status with { State = VpnConnectionState.Authenticating });
                break;

            case "ASSIGN_IP":
                Publish(status with { LocalAddress = state.LocalAddress ?? status.LocalAddress });
                break;

            default:
                // An abandoned attempt keeps its verdict. OpenVPN carries on announcing the states
                // of the next try until it is stopped, and each of those would otherwise paint over
                // the one thing worth reading.
                if (!abandoned
                    && status.State is not (VpnConnectionState.Connected or VpnConnectionState.Reconnecting))
                {
                    Publish(status with { State = VpnConnectionState.Connecting });
                }

                break;
        }
    }

    /// <summary>
    /// Gives up on a tunnel that has not come up in time.
    /// </summary>
    /// <remarks>
    /// OpenVPN retries by itself for as long as it is left running, and a tunnel that cannot get an
    /// adapter or reach its server never reports anything terminal. Without this, the profile shows
    /// that it is connecting for as long as the application lives and its process is never cleaned
    /// up, because nothing ever decides the attempt is over.
    /// </remarks>
    private async Task AbandonIfNotConnectedAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(limit, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The session ended first, which is every ordinary outcome.
            return;
        }

        if (status.State is VpnConnectionState.Connected or VpnConnectionState.Disconnected
            or VpnConnectionState.Failed)
        {
            return;
        }

        abandoned = true;

        Publish(status with
        {
            State = VpnConnectionState.Failed,
            ConnectedSince = null,
            Message = $"The tunnel did not come up within {limit.TotalSeconds:0} seconds.",
            Failure = VpnFailureKind.ConnectionLost,
        });

        await StopAbandonedAttemptAsync(cancellationToken);
    }

    /// <summary>
    /// Ends an attempt that cannot succeed.
    /// </summary>
    /// <remarks>
    /// Best effort by design. The process may already be on its way out, and the verdict has been
    /// published either way; whoever owns this connection retires it and tears the process down.
    /// </remarks>
    private async Task StopAbandonedAttemptAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            await client.SignalAsync("SIGTERM", cancellationToken);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or IOException or ObjectDisposedException)
        {
            ConnectionSupervisorLog.SignalNotDelivered(logger, exception);
        }
    }

    private async Task AnswerCredentialRequestAsync(
        ConnectionRequest request,
        PasswordRequestMessage message,
        CancellationToken cancellationToken)
    {
        Publish(status with { State = VpnConnectionState.Authenticating });

        pendingChallenges.Remove(message.Realm, out DynamicChallenge? dynamicChallenge);

        CredentialRequest credentialRequest = new(
            request.ProfileId,
            message.Realm,
            message.NeedsUsername,
            rejectedRealms.Contains(message.Realm),
            DescribeChallenge(message.Challenge, dynamicChallenge));

        VpnCredentials? credentials = await credentialProvider.RequestAsync(credentialRequest, cancellationToken);

        if (credentials is null)
        {
            ConnectionSupervisorLog.CredentialsUnavailable(logger, message.Realm);
            Publish(status with
            {
                State = VpnConnectionState.Failed,
                // "Not available" reads as a fault in the client. Nothing was supplied is what
                // actually happened, and it covers both the prompt being dismissed and there being
                // nothing stored to answer with.
                Message = credentialRequest.IsRetry
                    ? $"The credentials for '{message.Realm}' were rejected by the server."
                    : $"No credentials were supplied for '{message.Realm}'.",
                Failure = VpnFailureKind.Authentication,
            });

            await client!.SignalAsync("SIGTERM", cancellationToken);
            return;
        }

        await client!.SendCredentialsAsync(
            message.Realm,
            message.NeedsUsername ? credentials.Username ?? dynamicChallenge?.Username : null,
            EncodePassword(credentials, message.Challenge, dynamicChallenge),
            cancellationToken);
    }

    /// <summary>
    /// Records what the server asked the client to apply.
    /// </summary>
    /// <remarks>
    /// The management interface has no command that reports pushed options, so the log stream is the
    /// only place they appear.
    /// </remarks>
    private void ApplyPushedOptions(LogMessage log)
    {
        if (PushReplyParser.Parse(log.Text) is not { } pushed)
        {
            return;
        }

        Publish(status with
        {
            PushedRoutes = pushed.Routes,
            PushedDnsServers = pushed.DnsServers,
            Gateway = pushed.Gateway,
            ServerRequestedDefaultRoute = pushed.RedirectsDefaultRoute,
            ServerRequestedCompression = pushed.RequestsCompression,
        });
    }

    /// <summary>
    /// Records a round trip measurement taken by whatever is monitoring this connection.
    /// </summary>
    public void ReportPing(double? milliseconds)
    {
        Publish(status with
        {
            PingMilliseconds = milliseconds,
            PingFailed = milliseconds is null,
        });
    }

    /// <summary>
    /// Turns the protocol level challenge into the neutral form the credential provider understands.
    /// </summary>
    private static CredentialChallenge? DescribeChallenge(
        StaticChallenge? staticChallenge,
        DynamicChallenge? dynamicChallenge)
    {
        if (dynamicChallenge is not null)
        {
            return new CredentialChallenge(dynamicChallenge.Text, dynamicChallenge.Echo, IsDynamic: true);
        }

        return staticChallenge is null
            ? null
            : new CredentialChallenge(staticChallenge.Text, staticChallenge.Echo, IsDynamic: false);
    }

    /// <summary>
    /// Packs the answer into the password field, which is where OpenVPN carries a challenge response.
    /// </summary>
    private static string EncodePassword(
        VpnCredentials credentials,
        StaticChallenge? staticChallenge,
        DynamicChallenge? dynamicChallenge)
    {
        if (dynamicChallenge is not null)
        {
            return ChallengeEncoding.ForDynamicChallenge(
                dynamicChallenge.StateId,
                credentials.ChallengeResponse ?? string.Empty);
        }

        if (staticChallenge is not null && credentials.ChallengeResponse is { } response)
        {
            return ChallengeEncoding.ForStaticChallenge(credentials.Password, response);
        }

        return credentials.Password;
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

        int? processId = ProcessId;

        try
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }
        finally
        {
            // The process outlives everything else here, so ending it cannot be left to depend on
            // the tidying that comes before it. Disposing a client with a command still in flight
            // can throw, and a throw here used to leave a tunnel running with nothing left to stop
            // it: sixty of them accumulated in eight minutes of a connection retrying.
            session?.Dispose();
            session = null;
            pump = null;
            client = null;
            ProcessId = null;

            if (processId is { } id)
            {
                await EnsureProcessExitedAsync(id);
            }
        }
    }

    /// <summary>
    /// Confirms the OpenVPN process actually ended, and ends it if it did not.
    /// </summary>
    /// <remarks>
    /// A signal is a request, not a guarantee. With auth-retry set to interact, a process whose
    /// credentials were refused keeps waiting for new ones instead of exiting, which would leave an
    /// orphaned tunnel behind.
    ///
    /// This is a best effort backstop, not a guarantee of its own. The process was created by the
    /// interactive service, so querying or terminating it can be refused with access denied. That is
    /// reported and accepted rather than propagated: a tunnel that outlives a disconnect is a fault
    /// worth logging, but it must never take the application down with it.
    /// </remarks>
    private async Task EnsureProcessExitedAsync(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);

            try
            {
                using CancellationTokenSource grace = new(ProcessExitGrace);
                await process.WaitForExitAsync(grace.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                // The signal was ignored, so the process is ended below.
            }

            ConnectionSupervisorLog.ProcessDidNotExit(logger, processId);
            process.Kill(entireProcessTree: false);
        }
        catch (ArgumentException)
        {
            // Already gone, which is the normal outcome.
        }
        catch (InvalidOperationException)
        {
            // It exited while being inspected.
        }
        catch (Win32Exception exception)
        {
            // The service created the process, so this client may not be allowed to query or end it.
            ConnectionSupervisorLog.ProcessCheckDenied(logger, processId, exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await TearDownAsync();

        // The semaphore is deliberately not disposed. A connection that ended on its own is
        // retired while a disconnect the user asked for may still be releasing it, and disposing it
        // underneath that call would turn a tidy shutdown into an exception.
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
/// <param name="ConnectTimeout">
/// How long the tunnel is given to reach connected before the attempt is abandoned. OpenVPN retries
/// for as long as it is left running, so without a bound a tunnel that cannot come up holds a
/// process and reports that it is connecting for as long as the application lives.
/// </param>
public sealed record ConnectionRequest(
    Guid ProfileId,
    string ConfigurationPath,
    string WorkingDirectory,
    int ManagementPort,
    IReadOnlyList<string> AdditionalOptions,
    string? LogPath = null,
    TimeSpan? ConnectTimeout = null);
