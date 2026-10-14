using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Writes a history row for every tunnel, from the moment it comes up to the moment it ends.
/// </summary>
/// <remarks>
/// Recording starts at <see cref="VpnConnectionState.Connected"/> rather than at the connection
/// attempt. A history of attempts that never established anything would bury the sessions that
/// matter, and the failure is already reported in the interface while it happens.
///
/// The byte counters arrive continuously and only the final values are worth storing, so the latest
/// are held in memory and written once when the session closes. Writing every tick would put a
/// database round trip on a one second timer for each tunnel.
///
/// Statuses are handled strictly in the order they arrived. Handling them concurrently would let the
/// status that closes a session overtake the last counter update, and the session would be recorded
/// as having carried less than it did.
///
/// A session is the time a tunnel carried traffic, so it also ends when OpenVPN starts reconnecting,
/// and the reconnected tunnel is a session of its own. OpenVPN's counters run on across such a
/// restart rather than starting from zero, measured against the lab, so the session that follows
/// records what was counted since the one before it ended, not the running total.
///
/// A tunnel found dead after a sleep did not end when that was noticed. It ended when the computer
/// went to sleep, so a session that ends shortly after a wake, for any reason but the user's, is
/// recorded as ending then. Otherwise the history reports hours of connection that never carried a
/// byte, which is what showed it as connected in the first place.
/// </remarks>
public sealed class SessionRecorder : IAsyncDisposable
{
    /// <summary>
    /// How long after a wake an ending is still taken to be the sleep's doing.
    /// </summary>
    /// <remarks>
    /// Long enough for the check after waking and the restart it leads to, and for an OpenVPN
    /// keepalive to time out on its own; short enough that a drop with another cause later on keeps
    /// its own time.
    /// </remarks>
    private static readonly TimeSpan AfterWake = TimeSpan.FromMinutes(3);

    private readonly ConnectionManager connections;
    private readonly ISessionStore sessions;
    private readonly IWakeMonitor wake;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SessionRecorder> logger;

    private readonly ConcurrentDictionary<Guid, OpenSession> open = new();

    /// <summary>
    /// The counters a session ended at when OpenVPN went on to reconnect in the same process, which
    /// is where the next session of that profile starts counting from.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (long Received, long Sent)> carried = new();

    private readonly Channel<QueueItem> pending =
        Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource lifetime = new();
    private Task? pump;
    private bool disposed;

    public SessionRecorder(
        ConnectionManager connections,
        ISessionStore sessions,
        IWakeMonitor wake,
        TimeProvider timeProvider,
        ILogger<SessionRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(wake);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.connections = connections;
        this.sessions = sessions;
        this.wake = wake;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public void Attach()
    {
        pump ??= Task.Run(() => PumpAsync(lifetime.Token), CancellationToken.None);
        connections.StatusChanged += OnStatusChanged;
    }

    /// <summary>
    /// Closes anything still open, which is what the application does on its way out.
    /// </summary>
    public async Task CloseOpenSessionsAsync(
        SessionEndReason reason,
        CancellationToken cancellationToken = default)
    {
        // Everything already queued is applied first, so the counters written here are the last ones
        // the tunnels reported rather than whatever happened to have been processed so far.
        await DrainAsync(cancellationToken);

        foreach (Guid profileId in open.Keys.ToList())
        {
            if (open.TryRemove(profileId, out OpenSession? session))
            {
                await CloseAsync(session, reason, detail: null, endedAt: null, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Waits until everything queued so far has been handled.
    /// </summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource marker = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A marker travels through the same queue, so it arrives after everything ahead of it.
        if (!pending.Writer.TryWrite(new QueueItem(null, marker)))
        {
            return;
        }

        await marker.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged change) =>
        pending.Writer.TryWrite(new QueueItem(change, null));

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (QueueItem item in pending.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.Marker is { } marker)
                {
                    marker.TrySetResult();
                    continue;
                }

                if (item.Change is { } change)
                {
                    await RecordAsync(change);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RecordAsync(ConnectionStatusChanged change)
    {
        try
        {
            VpnConnectionStatus status = change.Status;

            open.TryGetValue(change.ProfileId, out OpenSession? existing);

            // The status that reports the end of a connection carries no counters, because nothing
            // is flowing any more. Copying it would record every session as having moved nothing,
            // so the last values seen while the tunnel was up are the ones kept.
            if (existing is not null && !EndsSession(status.State))
            {
                existing.BytesReceived = status.BytesReceived;
                existing.BytesSent = status.BytesSent;
            }

            switch (status.State)
            {
                case VpnConnectionState.Connected when existing is null:
                    await BeginAsync(change.ProfileId, status);
                    break;

                case VpnConnectionState state when EndsSession(state)
                    && open.TryRemove(change.ProfileId, out OpenSession? finished):
                    if (state == VpnConnectionState.Reconnecting)
                    {
                        carried[change.ProfileId] = (finished.BytesReceived, finished.BytesSent);
                    }
                    else
                    {
                        carried.TryRemove(change.ProfileId, out _);
                    }

                    await CloseAsync(
                        finished,
                        MapReason(status),
                        status.Message,
                        EndedBySleep(finished, status),
                        CancellationToken.None);
                    break;

                default:
                    break;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // History is a record, not a dependency. Losing a row must not disturb the tunnel.
            SessionRecorderLog.RecordFailed(logger, change.ProfileId, exception);
        }
    }

    private async Task BeginAsync(Guid profileId, VpnConnectionStatus status)
    {
        Guid sessionId = await sessions.BeginAsync(
            profileId,
            status.LocalAddress,
            status.ServerAddress,
            status.ServerPort);

        // Counters below where the last session ended belong to a new process, which counts from
        // zero, so nothing is carried into it.
        (long Received, long Sent) baseline =
            carried.TryRemove(profileId, out (long Received, long Sent) previous)
            && status.BytesReceived >= previous.Received
            && status.BytesSent >= previous.Sent
                ? previous
                : (0, 0);

        open[profileId] = new OpenSession(sessionId, status.ConnectedSince ?? timeProvider.GetUtcNow(), baseline)
        {
            BytesReceived = status.BytesReceived,
            BytesSent = status.BytesSent,
        };
    }

    private async Task CloseAsync(
        OpenSession session,
        SessionEndReason reason,
        string? detail,
        DateTimeOffset? endedAt,
        CancellationToken cancellationToken)
    {
        await sessions.EndAsync(
            session.Id,
            reason,
            Math.Max(0, session.BytesReceived - session.Baseline.Received),
            Math.Max(0, session.BytesSent - session.Baseline.Sent),
            detail,
            endedAt,
            cancellationToken);
    }

    /// <summary>
    /// When the computer went to sleep, if that is when this session really ended.
    /// </summary>
    private DateTimeOffset? EndedBySleep(OpenSession session, VpnConnectionStatus status)
    {
        if (status.Failure == VpnFailureKind.UserRequested
            || wake.LastWake is not { SuspendedAt: { } suspended, IsLongSleep: true } lastWake)
        {
            return null;
        }

        bool sleptDuringSession = suspended > session.StartedAt;
        bool endedJustAfterWaking = timeProvider.GetUtcNow() - lastWake.ResumedAt <= AfterWake;

        return sleptDuringSession && endedJustAfterWaking ? suspended : null;
    }

    private static bool EndsSession(VpnConnectionState state) =>
        state is VpnConnectionState.Disconnected or VpnConnectionState.Failed or VpnConnectionState.Reconnecting;

    private static SessionEndReason MapReason(VpnConnectionStatus status) => status.Failure switch
    {
        // OpenVPN restarting the tunnel is the connection being lost, whoever asked for the restart.
        _ when status.State == VpnConnectionState.Reconnecting => SessionEndReason.ConnectionLost,
        VpnFailureKind.UserRequested => SessionEndReason.UserRequested,
        VpnFailureKind.Authentication => SessionEndReason.AuthenticationFailed,
        VpnFailureKind.ConnectionLost => SessionEndReason.ConnectionLost,
        VpnFailureKind.LaunchRefused or VpnFailureKind.Fatal => SessionEndReason.Error,

        // A clean stop with nothing to report is a disconnect the user asked for.
        _ => status.State == VpnConnectionState.Disconnected
            ? SessionEndReason.UserRequested
            : SessionEndReason.Error,
    };

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connections.StatusChanged -= OnStatusChanged;
        pending.Writer.TryComplete();

        await lifetime.CancelAsync();

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

        lifetime.Dispose();
    }

    /// <summary>
    /// One entry of the ordered queue: either a status to apply, or a marker to complete.
    /// </summary>
    /// <remarks>
    /// The marker exists so a caller can wait for everything queued ahead of it without the queue
    /// having to expose its own progress.
    /// </remarks>
    private readonly record struct QueueItem(ConnectionStatusChanged? Change, TaskCompletionSource? Marker);

    /// <summary>
    /// A session that has been opened in the database and not yet closed.
    /// </summary>
    private sealed class OpenSession
    {
        public OpenSession(Guid id, DateTimeOffset startedAt, (long Received, long Sent) baseline)
        {
            Id = id;
            StartedAt = startedAt;
            Baseline = baseline;
        }

        public Guid Id { get; }

        public DateTimeOffset StartedAt { get; }

        /// <summary>
        /// The counters this session started from, which the totals it records leave out.
        /// </summary>
        public (long Received, long Sent) Baseline { get; }

        public long BytesReceived { get; set; }

        public long BytesSent { get; set; }
    }
}
