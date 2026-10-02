using System.Globalization;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// How a state of the server is coloured.
/// </summary>
public enum ServerStatusTone
{
    /// <summary>Grey: nothing known yet.</summary>
    Neutral,

    /// <summary>Green: online and synchronised.</summary>
    Good,

    /// <summary>Blue: synchronising.</summary>
    Busy,

    /// <summary>Amber: offline, degraded, or changes waiting.</summary>
    Warning,

    /// <summary>Red: something only the person or the operator can put right.</summary>
    Problem,
}

/// <summary>
/// Puts the server's state into the compact words of the status bar, the tray and the menu.
/// </summary>
/// <remarks>
/// One place, so the three never describe the same moment differently. Pure: the time is handed in.
/// </remarks>
public static class ServerStatusText
{
    private const string Separator = " · ";

    public static ServerStatusTone Tone(SyncState state) => state switch
    {
        SyncState.Synchronised => ServerStatusTone.Good,
        SyncState.Synchronising => ServerStatusTone.Busy,
        SyncState.Offline or SyncState.Degraded or SyncState.ChangesWaiting => ServerStatusTone.Warning,
        SyncState.SignInRequired or SyncState.ClientOutdated or SyncState.ClockWrong or SyncState.CertificateUntrusted
            or SyncState.SettingsUnreadable => ServerStatusTone.Problem,
        _ => ServerStatusTone.Neutral,
    };

    /// <summary>
    /// The line of the status bar, such as <c>pilot.example.com · 23 ms · synced 2 min ago · 3 changes waiting</c>.
    /// </summary>
    public static string Compact(ILocalizer localizer, ServerStatusSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(snapshot);

        SyncState state = snapshot.State;
        List<string> parts = [snapshot.Host];

        if (snapshot.Reachability is { Reachable: true, Latency: { } latency } && state is not SyncState.Offline)
        {
            parts.Add(localizer.Translate("statusBar.latency", ((long)Math.Round(latency.TotalMilliseconds)).ToString(CultureInfo.CurrentCulture)));
        }

        string? phrase = State(localizer, state);

        if (phrase is not null)
        {
            parts.Add(phrase);
        }

        if (state is SyncState.Synchronised or SyncState.ChangesWaiting or SyncState.Degraded)
        {
            parts.Add(Synced(localizer, snapshot.Sync.LastPullAt, now));
        }

        if (snapshot.Sync.PendingChanges > 0)
        {
            parts.Add(localizer.Translate("statusBar.waiting", snapshot.Sync.PendingChanges));
        }

        if (snapshot.Sync.DroppedChanges > 0)
        {
            parts.Add(localizer.Translate("statusBar.dropped", snapshot.Sync.DroppedChanges));
        }

        return string.Join(Separator, parts);
    }

    /// <summary>
    /// The words for a state on its own, or null for a state that needs none.
    /// </summary>
    public static string? State(ILocalizer localizer, SyncState state)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        return state switch
        {
            SyncState.Synchronising => localizer["statusBar.synchronising"],
            SyncState.Offline => localizer["statusBar.offline"],
            SyncState.Degraded => localizer["statusBar.degraded"],
            SyncState.SignInRequired => localizer["statusBar.signInRequired"],
            SyncState.ClientOutdated => localizer["statusBar.clientOutdated"],
            SyncState.ClockWrong => localizer["statusBar.clockWrong"],
            SyncState.CertificateUntrusted => localizer["statusBar.certificateUntrusted"],
            SyncState.SettingsUnreadable => localizer["statusBar.settingsUnreadable"],
            _ => null,
        };
    }

    /// <summary>
    /// When the last pull completed, relative to now.
    /// </summary>
    public static string Synced(ILocalizer localizer, DateTimeOffset? lastPull, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        if (lastPull is not { } at)
        {
            return localizer["statusBar.neverSynced"];
        }

        TimeSpan ago = now - at;

        if (ago < TimeSpan.FromMinutes(1))
        {
            return localizer["statusBar.syncedJustNow"];
        }

        if (ago < TimeSpan.FromHours(1))
        {
            return localizer.Translate("statusBar.syncedMinutes", (int)ago.TotalMinutes);
        }

        return ago < TimeSpan.FromDays(1)
            ? localizer.Translate("statusBar.syncedHours", (int)ago.TotalHours)
            : localizer.Translate("statusBar.syncedDays", (int)ago.TotalDays);
    }

    /// <summary>
    /// The tooltip: address and versions, the person, the cursor, the times and the last error.
    /// </summary>
    public static string Details(ILocalizer localizer, ServerStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(snapshot);

        string unknown = localizer["common.unknown"];
        string never = localizer["common.never"];
        SyncStatus sync = snapshot.Sync;

        List<string> lines =
        [
            localizer.Translate("statusBar.detailServer", snapshot.Address.GetLeftPart(UriPartial.Authority)),
            localizer.Translate(
                "statusBar.detailVersion",
                snapshot.Reachability.Info?.Version ?? unknown,
                snapshot.Reachability.Info?.ApiVersion ?? unknown),
            snapshot.User is { } user
                ? localizer.Translate("statusBar.detailUser", user.DisplayName ?? user.Username, ServerMessages.Role(localizer, user.Role))
                : localizer["statusBar.detailNoUser"],
            localizer.Translate("statusBar.detailCursor", sync.Cursor?.ToString(CultureInfo.InvariantCulture) ?? never),
            localizer.Translate("statusBar.detailPull", Time(sync.LastPullAt) ?? never),
            localizer.Translate("statusBar.detailPush", Time(sync.LastPushAt) ?? never),
        ];

        if (sync.LastErrorCode is { } code)
        {
            lines.Add(localizer.Translate("statusBar.detailError", code, sync.LastRequestId ?? unknown));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string? Time(DateTimeOffset? at) =>
        at?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
