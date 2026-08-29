using System.Collections.Concurrent;
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
/// The byte counters arrive continuously and the final values are the ones worth keeping, so the
/// latest are held in memory and written once when the session closes. Writing every tick would put
/// a database round trip on a one second timer for each tunnel.
/// </remarks>
public sealed class SessionRecorder : IDisposable
{
    private readonly ConnectionManager connections;
    private readonly ISessionStore sessions;
    private readonly ILogger<SessionRecorder> logger;

    private readonly ConcurrentDictionary<Guid, OpenSession> open = new();
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

    public void Attach() => connections.StatusChanged += OnStatusChanged;

    /// <summary>
    /// Closes anything still open, which is what the application does on its way out.
    /// </summary>
    public async Task CloseOpenSessionsAsync(
        SessionEndReason reason,
        CancellationToken cancellationToken = default)
    {
        foreach (Guid profileId in open.Keys.ToList())
        {
            if (open.TryRemove(profileId, out OpenSession? session))
            {
                await CloseAsync(session, reason, detail: null, cancellationToken);
            }
        }
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged change)
    {
        // The supervisor raises this from its own pump. Recording is fire and forget on purpose:
        // a slow database write must never hold up the connection state machine.
        _ = RecordAsync(change);
    }

    private async Task RecordAsync(ConnectionStatusChanged change)
    {
        try
        {
            VpnConnectionStatus status = change.Status;

            if (open.TryGetValue(change.ProfileId, out OpenSession? existing))
            {
                existing.BytesReceived = status.BytesReceived;
                existing.BytesSent = status.BytesSent;
            }

            switch (status.State)
            {
                case VpnConnectionState.Connected when existing is null:
                    await BeginAsync(change.ProfileId, status);
                    break;

                case VpnConnectionState.Disconnected or VpnConnectionState.Failed
                    when open.TryRemove(change.ProfileId, out OpenSession? finished):
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

        OpenSession session = new(sessionId)
        {
            BytesReceived = status.BytesReceived,
            BytesSent = status.BytesSent,
        };

        if (!open.TryAdd(profileId, session))
        {
            // Two Connected notifications raced. The second row would never be closed, so it is
            // closed here instead of being left running forever.
            await sessions.EndAsync(sessionId, SessionEndReason.Error, 0, 0, "Duplicate session record.");
        }
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connections.StatusChanged -= OnStatusChanged;
    }

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
