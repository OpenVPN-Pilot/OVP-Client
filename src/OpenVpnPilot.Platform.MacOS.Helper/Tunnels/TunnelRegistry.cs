using System.Runtime.Versioning;

namespace OpenVpnPilot.Platform.MacOS.Helper.Tunnels;

/// <summary>
/// Every tunnel the helper is running, with who owns it.
/// </summary>
/// <remarks>
/// A tunnel belongs to the account that started it and to the session it was started in. Only that
/// account may end it or see it, so two people on the same Mac cannot stop each other's tunnels, and
/// the session is what ends it when the application that asked for it goes away.
///
/// The limits are there so no account can make a root process start OpenVPN until the machine runs
/// out of tunnel devices or memory.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class TunnelRegistry
{
    public const int MaximumTunnels = 64;

    public const int MaximumTunnelsPerAccount = 32;

    private readonly Lock gate = new();
    private readonly Dictionary<int, Tunnel> tunnels = [];

    public int Count
    {
        get
        {
            lock (gate)
            {
                return tunnels.Count;
            }
        }
    }

    /// <summary>
    /// Reserves room for one more tunnel of an account.
    /// </summary>
    /// <returns>Null when there is room, otherwise the limit that was reached.</returns>
    public string? CheckRoom(uint userId)
    {
        lock (gate)
        {
            if (tunnels.Count >= MaximumTunnels)
            {
                return $"The helper runs no more than {MaximumTunnels} tunnels at once.";
            }

            return tunnels.Values.Count(tunnel => tunnel.OwnerUserId == userId) >= MaximumTunnelsPerAccount
                ? $"One account runs no more than {MaximumTunnelsPerAccount} tunnels at once."
                : null;
        }
    }

    public void Add(Tunnel tunnel)
    {
        ArgumentNullException.ThrowIfNull(tunnel);

        lock (gate)
        {
            tunnels[tunnel.Process.ProcessId] = tunnel;
        }
    }

    public void Remove(Tunnel tunnel)
    {
        ArgumentNullException.ThrowIfNull(tunnel);

        lock (gate)
        {
            tunnels.Remove(tunnel.Process.ProcessId);
        }
    }

    /// <summary>
    /// A tunnel of the given account, or null when there is none with that process.
    /// </summary>
    public Tunnel? Find(int processId, uint userId)
    {
        lock (gate)
        {
            return tunnels.TryGetValue(processId, out Tunnel? tunnel) && (tunnel.OwnerUserId == userId || userId == 0)
                ? tunnel
                : null;
        }
    }

    public IReadOnlyList<Tunnel> OwnedBy(uint userId)
    {
        lock (gate)
        {
            return [.. tunnels.Values.Where(tunnel => tunnel.OwnerUserId == userId || userId == 0)];
        }
    }

    public IReadOnlyList<Tunnel> InSession(Guid sessionId)
    {
        lock (gate)
        {
            return [.. tunnels.Values.Where(tunnel => tunnel.SessionId == sessionId)];
        }
    }

    public IReadOnlySet<int> ProcessIds()
    {
        lock (gate)
        {
            return tunnels.Keys.ToHashSet();
        }
    }
}

/// <summary>
/// One running OpenVPN process and what the helper keeps about it.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed record Tunnel(
    SpawnedProcess Process,
    uint OwnerUserId,
    Guid SessionId,
    string Directory,
    DateTimeOffset StartedAt);
