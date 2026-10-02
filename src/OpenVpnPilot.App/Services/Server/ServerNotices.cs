using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Something the server did to this copy that the person should hear about, although nothing on
/// screen asked for it.
/// </summary>
/// <remarks>
/// The status bar shows the latest one; <see cref="ServerNoticeText"/> words it. Raised on the
/// thread of the synchronisation, so whoever shows it moves to the interface thread first.
/// </remarks>
public interface IServerNotices
{
    public event EventHandler<ServerNotice>? Raised;

    /// <summary>
    /// The latest notice, or null when there has been none in this run.
    /// </summary>
    public ServerNotice? Latest { get; }

    public void Raise(ServerNotice notice);
}

/// <summary>
/// What a notice is about.
/// </summary>
public enum ServerNoticeKind
{
    /// <summary>
    /// Profiles were deleted on the server while a tunnel to them was up. The tunnels were ended
    /// first, then the profiles were removed.
    /// </summary>
    ConnectedProfilesRemoved,
}

/// <param name="ProfileNames">The names of the profiles concerned, as this copy knew them.</param>
public sealed record ServerNotice(ServerNoticeKind Kind, IReadOnlyList<string> ProfileNames, DateTimeOffset At);

public sealed class ServerNotices : IServerNotices
{
    private readonly Lock gate = new();
    private ServerNotice? latest;

    public event EventHandler<ServerNotice>? Raised;

    public ServerNotice? Latest
    {
        get
        {
            lock (gate)
            {
                return latest;
            }
        }
    }

    public void Raise(ServerNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        lock (gate)
        {
            latest = notice;
        }

        Raised?.Invoke(this, notice);
    }
}

/// <summary>
/// Puts a notice into words.
/// </summary>
public static class ServerNoticeText
{
    public static string Describe(ILocalizer localizer, ServerNotice notice)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(notice);

        return notice.Kind switch
        {
            ServerNoticeKind.ConnectedProfilesRemoved => notice.ProfileNames.Count == 1
                ? localizer.Translate("shared.connectedRemovedOne", notice.ProfileNames[0])
                : localizer.Translate("shared.connectedRemovedMany", notice.ProfileNames.Count),
            _ => string.Empty,
        };
    }
}

/// <summary>
/// Ends the tunnels of profiles the server deleted, before their rows go.
/// </summary>
/// <remarks>
/// A tunnel whose profile is gone cannot be shown, stopped from the list or found in the history, so
/// it is ended first rather than left running unseen.
/// </remarks>
public interface IRemovedProfileTunnels
{
    /// <summary>
    /// Ends the tunnels among the given profiles that are up or on their way up.
    /// </summary>
    /// <returns>The profiles whose tunnel was ended.</returns>
    public Task<IReadOnlyList<Guid>> DisconnectAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken);
}

/// <summary>
/// Ends them through the connection manager.
/// </summary>
internal sealed class ConnectionManagerRemovedTunnels : IRemovedProfileTunnels
{
    private readonly ConnectionManager connections;

    public ConnectionManagerRemovedTunnels(ConnectionManager connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        this.connections = connections;
    }

    public async Task<IReadOnlyList<Guid>> DisconnectAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileIds);

        List<Guid> ended = [];

        foreach (Guid profileId in profileIds.Where(connections.IsActive))
        {
            await connections.DisconnectAsync(profileId, cancellationToken);
            ended.Add(profileId);
        }

        return ended;
    }
}
