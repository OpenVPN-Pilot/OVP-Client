using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Vpn;

namespace OpenVpnPilot.OpenVpn.Runtime;

/// <summary>
/// Runs and tracks every active tunnel.
/// </summary>
/// <remarks>
/// Each profile gets its own supervisor, its own process and its own management port, so several
/// tunnels can be connected at the same time without interfering with one another.
///
/// A connection is held only while it is running. One that ends by itself is retired as soon as it
/// reports it, because an entry that outlives its tunnel makes the profile look busy, makes a
/// reconnect think it is still up, and makes connecting again fail as a duplicate.
/// </remarks>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly IOpenVpnLauncher launcher;
    private readonly IManagementChannelFactory channelFactory;
    private readonly ICredentialProvider credentialProvider;
    private readonly IProfileMaterializer materializer;
    private readonly IPortAllocator portAllocator;
    private readonly ILogger<ConnectionManager> logger;

    private readonly ConcurrentDictionary<Guid, ActiveConnection> active = new();
    private bool disposed;

    public ConnectionManager(
        IOpenVpnLauncher launcher,
        IManagementChannelFactory channelFactory,
        ICredentialProvider credentialProvider,
        IProfileMaterializer materializer,
        IPortAllocator? portAllocator = null,
        ILogger<ConnectionManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(channelFactory);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ArgumentNullException.ThrowIfNull(materializer);

        this.launcher = launcher;
        this.channelFactory = channelFactory;
        this.credentialProvider = credentialProvider;
        this.materializer = materializer;
        this.portAllocator = portAllocator ?? new LoopbackPortAllocator();
        this.logger = logger ?? NullLogger<ConnectionManager>.Instance;
    }

    /// <summary>
    /// Raised whenever any connection's status changes, including throughput updates.
    /// </summary>
    public event EventHandler<ConnectionStatusChanged>? StatusChanged;

    /// <summary>
    /// Raised only when a connection's lifecycle state itself changes.
    /// </summary>
    /// <remarks>
    /// Throughput updates arrive every second per tunnel. Anything that reacts to transitions, such
    /// as notifications or the history, must listen here rather than to every telemetry tick.
    /// </remarks>
    public event EventHandler<ConnectionStatusChanged>? StateChanged;

    /// <summary>
    /// Profiles that currently have a process running.
    /// </summary>
    public IReadOnlyCollection<Guid> ActiveProfiles => active.Keys.ToList();

    public int ActiveCount => active.Count;

    public VpnConnectionStatus GetStatus(Guid profileId) =>
        active.TryGetValue(profileId, out ActiveConnection? connection)
            ? connection.Supervisor.Status
            : VpnConnectionStatus.Disconnected;

    public bool IsActive(Guid profileId) => active.ContainsKey(profileId);

    /// <summary>
    /// Records a round trip measurement against one connection.
    /// </summary>
    /// <remarks>
    /// Measuring is not the supervisor's job: it owns the process and the management session, and a
    /// network probe has nothing to do with either. Whoever measures reports the result here so it
    /// travels with the rest of the connection's telemetry.
    /// </remarks>
    public void ReportPing(Guid profileId, double? milliseconds)
    {
        if (active.TryGetValue(profileId, out ActiveConnection? connection))
        {
            connection.Supervisor.ReportPing(milliseconds);
        }
    }

    /// <summary>
    /// The address worth measuring for one connection, or null when there is nothing to measure.
    /// </summary>
    public string? GetPingTarget(Guid profileId)
    {
        if (!active.TryGetValue(profileId, out ActiveConnection? connection))
        {
            return null;
        }

        VpnConnectionStatus status = connection.Supervisor.Status;

        // The far end of the tunnel is the meaningful target. The server's public address would be
        // measured over the ordinary route and would say nothing about the tunnel.
        return status.State == VpnConnectionState.Connected
            ? status.Gateway ?? status.LocalAddress
            : null;
    }

    /// <summary>
    /// Starts a tunnel for the given profile. The configuration is written to a private file for the
    /// lifetime of the connection and removed afterwards.
    /// </summary>
    public async Task<VpnConnectionStatus> ConnectAsync(
        Guid profileId,
        string configuration,
        IReadOnlyList<string>? additionalOptions = null,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);

        if (active.ContainsKey(profileId))
        {
            throw new InvalidOperationException($"Profile {profileId} is already connected.");
        }

        MaterialisedProfile materialised = await materializer.MaterialiseAsync(
            profileId,
            configuration,
            cancellationToken);

        ConnectionSupervisor supervisor = new(
            launcher,
            channelFactory,
            credentialProvider);

        ActiveConnection connection = new(supervisor, materialised);

        void Forward(object? sender, VpnConnectionStatus status) =>
            StatusChanged?.Invoke(this, new ConnectionStatusChanged(profileId, status));

        void ForwardState(object? sender, VpnConnectionStatus status)
        {
            // Retiring before the event is raised means whoever handles it, in particular the retry
            // logic, already sees a profile that is no longer connected.
            if (HasEndedByItself(status))
            {
                Retire(profileId);
            }

            StateChanged?.Invoke(this, new ConnectionStatusChanged(profileId, status));
        }

        supervisor.StatusChanged += Forward;
        supervisor.StateChanged += ForwardState;

        connection.Unsubscribe = () =>
        {
            supervisor.StatusChanged -= Forward;
            supervisor.StateChanged -= ForwardState;
        };

        if (!active.TryAdd(profileId, connection))
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException($"Profile {profileId} is already connected.");
        }

        try
        {
            VpnConnectionStatus status = await supervisor.ConnectAsync(
                new ConnectionRequest(
                    profileId,
                    materialised.Path,
                    materialised.Directory,
                    portAllocator.Reserve(),
                    additionalOptions ?? [],
                    ConnectTimeout: connectTimeout),
                cancellationToken);

            if (status.State == VpnConnectionState.Failed)
            {
                await RemoveAsync(profileId);
            }

            return status;
        }
        catch
        {
            await RemoveAsync(profileId);
            throw;
        }
    }

    public async Task DisconnectAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!active.TryGetValue(profileId, out ActiveConnection? connection))
        {
            return;
        }

        try
        {
            await connection.Supervisor.DisconnectAsync(cancellationToken);
        }
        finally
        {
            // The entry goes whatever the supervisor made of the request. Leaving it behind because
            // stopping went badly is how a tunnel ends up permanently showing as disconnecting.
            await RemoveAsync(profileId);
        }
    }

    /// <summary>
    /// Stops every tunnel. Used when the application closes.
    /// </summary>
    public async Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (Guid profileId in active.Keys.ToList())
        {
            try
            {
                await DisconnectAsync(profileId, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                // One tunnel refusing to stop must not prevent the others from stopping.
                ConnectionManagerLog.DisconnectFailed(logger, profileId, exception);
            }
        }
    }

    /// <summary>
    /// True when a status means the tunnel is over without anyone having asked for it.
    /// </summary>
    /// <remarks>
    /// A disconnect the user asked for is removed by the call that asked for it. Removing it here as
    /// well would dispose the supervisor from underneath that call while it is still finishing.
    /// </remarks>
    private static bool HasEndedByItself(VpnConnectionStatus status) =>
        status.State == VpnConnectionState.Failed
        || (status.State == VpnConnectionState.Disconnected
            && status.Failure != VpnFailureKind.UserRequested);

    /// <summary>
    /// Drops a connection that ended on its own and disposes it away from the pump that reported it.
    /// </summary>
    /// <remarks>
    /// The report arrives on the supervisor's own message pump, and disposing a supervisor waits for
    /// that pump to finish. Awaiting the disposal here would wait for the thread running this code.
    /// The entry is therefore removed straight away and the disposal is left to run on its own.
    /// </remarks>
    private void Retire(Guid profileId)
    {
        if (!active.TryRemove(profileId, out ActiveConnection? connection))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                // Deliberately everything. This runs with nobody waiting for it, and the last thing
                // it does is end the OpenVPN process; an exception escaping here is a tunnel that
                // nothing is left to stop, which is worse than any exception it could be.
                ConnectionManagerLog.DisconnectFailed(logger, profileId, exception);
            }
        });
    }

    private async Task RemoveAsync(Guid profileId)
    {
        if (active.TryRemove(profileId, out ActiveConnection? connection))
        {
            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await DisconnectAllAsync();
    }

    private sealed class ActiveConnection : IAsyncDisposable
    {
        public ActiveConnection(ConnectionSupervisor supervisor, MaterialisedProfile materialised)
        {
            Supervisor = supervisor;
            Materialised = materialised;
        }

        public ConnectionSupervisor Supervisor { get; }

        public MaterialisedProfile Materialised { get; }

        public Action? Unsubscribe { get; set; }

        public async ValueTask DisposeAsync()
        {
            Unsubscribe?.Invoke();
            await Supervisor.DisposeAsync();
            await Materialised.DisposeAsync();
        }
    }
}

/// <summary>
/// Reports a status change for one profile.
/// </summary>
public sealed record ConnectionStatusChanged(Guid ProfileId, VpnConnectionStatus Status);

/// <summary>
/// Hands out free local ports for management interfaces.
/// </summary>
public interface IPortAllocator
{
    public int Reserve();
}

/// <summary>
/// Reserves a port by letting the operating system pick a free one on the loopback interface.
/// </summary>
/// <remarks>
/// The listener is closed again immediately, so there is a brief window in which another process
/// could claim the same port. OpenVPN reports that as a startup failure rather than misbehaving, and
/// the alternative, holding the port open, would prevent OpenVPN from binding it at all.
/// </remarks>
public sealed class LoopbackPortAllocator : IPortAllocator
{
    public int Reserve()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
