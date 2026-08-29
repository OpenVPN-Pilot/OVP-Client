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
/// </remarks>
public sealed class SessionRecorder : IAsyncDisposable
{
    private readonly ConnectionManager connections;
    private readonly ISessionStore sessions;
    private readonly ILogger<SessionRecorder> logger;

    private readonly ConcurrentDictionary<Guid, OpenSession> open = new();

    private readonly Channel<QueueItem> pending =
        Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource lifetime = new();
    private Task? pump;
    private bool disposed;

    public SessionRecorder(
        ConnectionManager connections,
        ISessionStore sessions,
        ILogger<SessionRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logger);

        this.connections = connections;
        this.sessions = sessions;
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
                await CloseAsync(session, reason, detail: null, cancellationToken);
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
            if (existing is not null && !IsTerminal(status.State))
            {
                existing.BytesReceived = status.BytesReceived;
                existing.BytesSent = status.BytesSent;
            }

            switch (status.State)
            {
                case VpnConnectionState.Connected when existing is null:
                    await BeginAsync(change.ProfileId, status);
                    break;

                case VpnConnectionState state when IsTerminal(state)
                    && open.TryRemove(change.ProfileId, out OpenSession? finished):
                    await CloseAsync(finished, MapReason(status), status.Message, CancellationToken.None);
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

        open[profileId] = new OpenSession(sessionId)
        {
            BytesReceived = status.BytesReceived,
            BytesSent = status.BytesSent,
        };
    }

    private async Task CloseAsync(
        OpenSession session,
        SessionEndReason reason,
        string? detail,
        CancellationToken cancellationToken)
    {
        await sessions.EndAsync(
            session.Id,
            reason,
            session.BytesReceived,
            session.BytesSent,
            detail,
            cancellationToken);
    }

    private static bool IsTerminal(VpnConnectionState state) =>
        state is VpnConnectionState.Disconnected or VpnConnectionState.Failed;

    private static SessionEndReason MapReason(VpnConnectionStatus status) => status.Failure switch
    {
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
        public OpenSession(Guid id) => Id = id;

        public Guid Id { get; }

        public long BytesReceived { get; set; }

        public long BytesSent { get; set; }
    }
}
