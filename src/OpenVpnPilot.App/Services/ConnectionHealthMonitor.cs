using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Checks that a tunnel reported as connected still carries anything, and restarts one that does
/// not.
/// </summary>
/// <remarks>
/// OpenVPN's own judgement of a tunnel is only as good as its configuration. With a keepalive it
/// gives up on a silent far end after a minute or two; without one it never does, and a tunnel whose
/// server stopped hearing from it, as happens to every tunnel while a laptop sleeps, stays connected
/// for as long as the process lives. That is the tunnel this looks for.
///
/// What counts as evidence of life is something arriving through the tunnel: the received byte
/// count going up, which covers OpenVPN's own keepalive packets as well as traffic, or the tunnel
/// gateway answering a round trip, which is measured through the tunnel. The measurement is asked
/// for whenever a check needs it, so an idle tunnel is given something to answer.
///
/// Three occasions lead to a check, and they differ in what it takes to restart:
///
/// - After a long sleep a tunnel has to show that it is alive within a few seconds, and is
///   restarted if it cannot. Nothing about a tunnel that slept for minutes can be assumed, and a
///   restart costs a few seconds, which a stale tunnel costs every time something is sent.
/// - After a short pause, a network change, or while running, a tunnel is restarted only when it
///   shows that it is dead: its gateway answered before, answers no longer, and nothing has arrived
///   since. A tunnel that never had a gateway that answers can show neither, and is left to OpenVPN
///   rather than restarted on a guess, every time the network changes.
/// - A tunnel younger than a few seconds is not judged after a network change at all, because its
///   own coming up is a network change.
///
/// A restart is OpenVPN's soft restart, and at most one per tunnel in a while, so a tunnel that
/// cannot be helped this way is not restarted in a loop; the supervisor's limit on restarts then
/// hands it to the retry logic.
/// </remarks>
public sealed class ConnectionHealthMonitor : IAsyncDisposable
{
    /// <summary>
    /// How many failed round trips in a row, against a gateway that answered before, it takes to
    /// suspect a tunnel while it runs.
    /// </summary>
    internal const int FailuresBeforeStall = 3;

    private readonly ConnectionManager connections;
    private readonly ITunnelProbe probe;
    private readonly IWakeMonitor wake;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ConnectionHealthMonitor> logger;

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, TunnelHealth> health = [];
    private readonly Dictionary<Guid, DateTimeOffset> lastRestart = [];
    private readonly List<Task> running = [];
    private readonly CancellationTokenSource lifetime = new();

    private bool verifying;
    private Check? queued;
    private bool attached;
    private bool disposed;

    public ConnectionHealthMonitor(
        ConnectionManager connections,
        ITunnelProbe probe,
        IWakeMonitor wake,
        TimeProvider timeProvider,
        ILogger<ConnectionHealthMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(wake);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.connections = connections;
        this.probe = probe;
        this.wake = wake;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// How long a tunnel has to stay silent, with its gateway not answering, to be restarted while
    /// it runs.
    /// </summary>
    public TimeSpan StallWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a tunnel is given to show that it is alive once a check began.
    /// </summary>
    public TimeSpan VerificationWindow { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often the evidence is looked at during a check.
    /// </summary>
    public TimeSpan VerificationStep { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a tunnel must have been up before a network change is a reason to judge it.
    /// </summary>
    public TimeSpan SettlingTime { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The shortest time between two restarts of the same tunnel.
    /// </summary>
    public TimeSpan RestartCooldown { get; init; } = TimeSpan.FromMinutes(2);

    public void Attach()
    {
        lock (gate)
        {
            if (attached)
            {
                return;
            }

            attached = true;
        }

        connections.StatusChanged += OnStatusChanged;
        probe.Measured += OnMeasured;
        wake.Woke += OnWoke;
        wake.NetworkChanged += OnNetworkChanged;
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged change)
    {
        VpnConnectionStatus status = change.Status;
        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (gate)
        {
            if (status.State != VpnConnectionState.Connected)
            {
                health.Remove(change.ProfileId);
                return;
            }

            if (!health.TryGetValue(change.ProfileId, out TunnelHealth? tunnel) || tunnel.ConnectedSince != status.ConnectedSince)
            {
                health[change.ProfileId] = new TunnelHealth(status.ConnectedSince, now, status.BytesReceived);
                return;
            }

            if (status.BytesReceived > tunnel.BytesReceived)
            {
                tunnel.LastInboundAt = now;
            }

            tunnel.BytesReceived = status.BytesReceived;
        }
    }

    private void OnMeasured(object? sender, TunnelProbeResult result)
    {
        // Only the gateway is reached through the tunnel. The server's own address answers over the
        // ordinary route, so whether it answers says nothing about the tunnel.
        if (result.Kind != PingTargetKind.TunnelGateway)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        bool stalled;

        lock (gate)
        {
            if (!health.TryGetValue(result.ProfileId, out TunnelHealth? tunnel))
            {
                return;
            }

            if (result.Answered)
            {
                tunnel.GatewayAnswered = true;
                tunnel.LastGatewayReplyAt = now;
                tunnel.ConsecutiveFailures = 0;
                return;
            }

            tunnel.ConsecutiveFailures++;

            stalled = !verifying
                && tunnel.GatewayAnswered
                && tunnel.ConsecutiveFailures >= FailuresBeforeStall
                && now - tunnel.LastInboundAt >= StallWindow
                && now - tunnel.ObservedSince >= SettlingTime;
        }

        if (stalled)
        {
            HealthLog.Stalled(logger, result.ProfileId, result.Target ?? string.Empty);
            Track(RestartAsync(result.ProfileId, VpnStatusReasonCode.TunnelUnresponsive));
        }
    }

    private void OnWoke(object? sender, WakeEvent woke) => Begin(new Check(woke));

    private void OnNetworkChanged(object? sender, EventArgs e) => Begin(new Check(null));

    /// <summary>
    /// Starts a check, or queues it behind the one under way. A wake waiting in the queue is never
    /// replaced by a network change, because what it may restart is the larger set.
    /// </summary>
    private void Begin(Check check)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (verifying)
            {
                if (queued is null || queued.Wake is null || check.Wake is not null)
                {
                    queued = check;
                }

                return;
            }

            verifying = true;
        }

        Track(Task.Run(() => VerifyAsync(check, lifetime.Token), CancellationToken.None));
    }

    private async Task VerifyAsync(Check check, CancellationToken cancellationToken)
    {
        try
        {
            await VerifyOnceAsync(check, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            Check? next;

            lock (gate)
            {
                next = queued;
                queued = null;
                verifying = next is not null && !disposed;
            }

            if (next is not null && !cancellationToken.IsCancellationRequested)
            {
                Track(Task.Run(() => VerifyAsync(next, cancellationToken), CancellationToken.None));
            }
        }
    }

    private async Task VerifyOnceAsync(Check check, CancellationToken cancellationToken)
    {
        DateTimeOffset since = timeProvider.GetUtcNow();
        List<Guid> pending;

        lock (gate)
        {
            pending = [.. health
                .Where(entry => check.Wake is not null || since - entry.Value.ObservedSince >= SettlingTime)
                .Select(entry => entry.Key)];
        }

        if (pending.Count == 0)
        {
            return;
        }

        HealthLog.Checking(logger, pending.Count, check.Wake is null ? "the network changed" : "the computer woke");
        probe.RequestMeasurement();

        int steps = 0;

        while (pending.Count > 0 && timeProvider.GetUtcNow() - since < VerificationWindow)
        {
            await Task.Delay(VerificationStep, timeProvider, cancellationToken);

            lock (gate)
            {
                pending.RemoveAll(profileId => Settled(profileId, since));
            }

            // Asked for again now and then, because the first round may have gone out before the
            // network was back.
            if (++steps % 5 == 0)
            {
                probe.RequestMeasurement();
            }
        }

        foreach (Guid profileId in pending)
        {
            bool gatewayAnswered;

            lock (gate)
            {
                if (!health.TryGetValue(profileId, out TunnelHealth? tunnel))
                {
                    continue;
                }

                gatewayAnswered = tunnel.GatewayAnswered;
            }

            if (check.Wake is { IsLongSleep: true })
            {
                HealthLog.NoSignAfterSleep(logger, profileId, VerificationWindow.TotalSeconds);
                await RestartAsync(profileId, VpnStatusReasonCode.TunnelRestartedAfterSleep);
            }
            else if (gatewayAnswered)
            {
                HealthLog.GatewaySilent(logger, profileId, VerificationWindow.TotalSeconds);
                await RestartAsync(profileId, VpnStatusReasonCode.TunnelUnresponsive);
            }
            else
            {
                HealthLog.Unverifiable(logger, profileId);
            }
        }
    }

    /// <summary>
    /// True when a tunnel no longer needs looking at: it showed a sign of life since the check
    /// began, or it is no longer connected and therefore in somebody else's hands.
    /// </summary>
    private bool Settled(Guid profileId, DateTimeOffset since)
    {
        if (!health.TryGetValue(profileId, out TunnelHealth? tunnel))
        {
            return true;
        }

        if (tunnel.LastInboundAt > since || tunnel.LastGatewayReplyAt > since)
        {
            HealthLog.Alive(logger, profileId);
            return true;
        }

        return false;
    }

    private async Task RestartAsync(Guid profileId, VpnStatusReasonCode reason)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (gate)
        {
            if (lastRestart.TryGetValue(profileId, out DateTimeOffset previous) && now - previous < RestartCooldown)
            {
                HealthLog.RestartHeldBack(logger, profileId);
                return;
            }

            lastRestart[profileId] = now;
        }

        try
        {
            bool restarted = await connections.RestartAsync(
                profileId,
                new VpnStatusReason { Code = reason },
                lifetime.Token);

            if (restarted)
            {
                HealthLog.Restarted(logger, profileId, reason);
            }
            else
            {
                HealthLog.RestartNotPossible(logger, profileId);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Shutting down; the tunnels are being stopped anyway.
        }
    }

    private void Track(Task task)
    {
        lock (gate)
        {
            running.RemoveAll(item => item.IsCompleted);
            running.Add(task);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] outstanding;

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            outstanding = [.. running];
        }

        connections.StatusChanged -= OnStatusChanged;
        probe.Measured -= OnMeasured;
        wake.Woke -= OnWoke;
        wake.NetworkChanged -= OnNetworkChanged;

        await lifetime.CancelAsync();

        try
        {
            await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // A restart that is still waiting for OpenVPN is ended by the tunnels being stopped.
            HealthLog.ShutdownIncomplete(logger);
        }

        lifetime.Dispose();
    }

    /// <summary>
    /// One check: after a wake, or after a network change when there is no wake.
    /// </summary>
    private sealed record Check(WakeEvent? Wake);

    /// <summary>
    /// What is known about one connected tunnel.
    /// </summary>
    private sealed class TunnelHealth
    {
        public TunnelHealth(DateTimeOffset? connectedSince, DateTimeOffset now, long bytesReceived)
        {
            ConnectedSince = connectedSince;
            ObservedSince = now;
            LastInboundAt = now;
            BytesReceived = bytesReceived;
        }

        /// <summary>
        /// The connection this belongs to, so a reconnect starts a fresh record.
        /// </summary>
        public DateTimeOffset? ConnectedSince { get; }

        /// <summary>
        /// When this monitor first saw the tunnel connected, by its own clock.
        /// </summary>
        public DateTimeOffset ObservedSince { get; }

        public long BytesReceived { get; set; }

        public DateTimeOffset LastInboundAt { get; set; }

        public bool GatewayAnswered { get; set; }

        public DateTimeOffset? LastGatewayReplyAt { get; set; }

        public int ConsecutiveFailures { get; set; }
    }
}

/// <summary>
/// Source generated log messages for <see cref="ConnectionHealthMonitor"/>.
/// </summary>
internal static partial class HealthLog
{
    [LoggerMessage(EventId = 3310, Level = LogLevel.Information, Message = "Checking {Count} tunnel(s) because {Cause}.")]
    public static partial void Checking(ILogger logger, int count, string cause);

    [LoggerMessage(EventId = 3311, Level = LogLevel.Information, Message = "Profile {ProfileId} is carrying traffic.")]
    public static partial void Alive(ILogger logger, Guid profileId);

    [LoggerMessage(
        EventId = 3312,
        Level = LogLevel.Warning,
        Message = "Profile {ProfileId} showed no sign of life within {Seconds} seconds of the computer waking; restarting it.")]
    public static partial void NoSignAfterSleep(ILogger logger, Guid profileId, double seconds);

    [LoggerMessage(
        EventId = 3313,
        Level = LogLevel.Warning,
        Message = "The tunnel gateway of profile {ProfileId} answered before and did not answer within {Seconds} seconds, and nothing arrived through the tunnel; restarting it.")]
    public static partial void GatewaySilent(ILogger logger, Guid profileId, double seconds);

    [LoggerMessage(
        EventId = 3314,
        Level = LogLevel.Information,
        Message = "Profile {ProfileId} could not be checked: nothing arrived and its gateway has never answered a round trip. It is left to OpenVPN.")]
    public static partial void Unverifiable(ILogger logger, Guid profileId);

    [LoggerMessage(
        EventId = 3315,
        Level = LogLevel.Warning,
        Message = "Profile {ProfileId} stopped carrying anything: its gateway {Gateway} no longer answers and nothing has arrived; restarting it.")]
    public static partial void Stalled(ILogger logger, Guid profileId, string gateway);

    [LoggerMessage(EventId = 3316, Level = LogLevel.Information, Message = "Asked OpenVPN to restart profile {ProfileId} ({Reason}).")]
    public static partial void Restarted(ILogger logger, Guid profileId, VpnStatusReasonCode reason);

    [LoggerMessage(
        EventId = 3317,
        Level = LogLevel.Information,
        Message = "Profile {ProfileId} was not restarted: it is no longer connected or OpenVPN did not accept the request.")]
    public static partial void RestartNotPossible(ILogger logger, Guid profileId);

    [LoggerMessage(
        EventId = 3318,
        Level = LogLevel.Information,
        Message = "Profile {ProfileId} was restarted a moment ago and is not restarted again yet.")]
    public static partial void RestartHeldBack(ILogger logger, Guid profileId);

    [LoggerMessage(EventId = 3319, Level = LogLevel.Warning, Message = "A tunnel check was still running when the application closed.")]
    public static partial void ShutdownIncomplete(ILogger logger);
}
