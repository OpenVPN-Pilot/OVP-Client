using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services.Storage;

/// <summary>
/// How many tunnels this process holds, connected or on the way there.
/// </summary>
/// <remarks>
/// What switching the store needs to know and nothing more. It is an interface of its own so the
/// refusal can be tested without bringing a tunnel up.
/// </remarks>
public interface IActiveTunnels
{
    public int Count { get; }
}

/// <summary>
/// Counts the connections the connection manager holds.
/// </summary>
internal sealed class ConnectionManagerTunnels : IActiveTunnels
{
    private readonly ConnectionManager connections;

    public ConnectionManagerTunnels(ConnectionManager connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        this.connections = connections;
    }

    public int Count => connections.ActiveCount;
}
