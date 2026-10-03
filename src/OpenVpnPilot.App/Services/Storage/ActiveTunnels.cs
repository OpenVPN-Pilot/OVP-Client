using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services.Storage;

/// <summary>
/// The tunnels this process holds, connected or on the way there.
/// </summary>
/// <remarks>
/// What switching the store and wiping a server's copy need to know and do, and nothing more. It is
/// an interface of its own so both can be tested without bringing a tunnel up.
/// </remarks>
public interface IActiveTunnels
{
    public int Count { get; }

    /// <summary>
    /// Ends every tunnel, and returns once what they reported on the way down has been recorded.
    /// </summary>
    /// <remarks>
    /// Waiting for the history as well is the point: whoever asks next is about to remove the
    /// database those records are written to.
    /// </remarks>
    public Task DisconnectAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Counts and ends the connections the connection manager holds.
/// </summary>
internal sealed class ConnectionManagerTunnels : IActiveTunnels
{
    private readonly ConnectionManager connections;
    private readonly SessionRecorder sessions;

    public ConnectionManagerTunnels(ConnectionManager connections, SessionRecorder sessions)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(sessions);

        this.connections = connections;
        this.sessions = sessions;
    }

    public int Count => connections.ActiveCount;

    public async Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        await connections.DisconnectAllAsync(cancellationToken);
        await sessions.CloseOpenSessionsAsync(SessionEndReason.ApplicationClosed, cancellationToken);
    }
}
