using Avalonia.Threading;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Keeps the notification area entry in step with the connection state and offers the common actions.
/// </summary>
/// <remarks>
/// The tray is the fastest route to a connection for someone who keeps the window closed, which is
/// how the stock client is normally used. Closing the window hides it rather than exiting, so the
/// tunnels keep running and the icon stays the way back in.
///
/// The menu is rebuilt whenever the language or the active count changes, because the entries carry
/// both translated wording and an enabled state that depends on what is running.
/// </remarks>
public sealed class TrayIconController : IDisposable
{
    public const string ShowWindowAction = "show";
    public const string QuickSwitcherAction = "switcher";
    public const string SettingsAction = "settings";
    public const string DisconnectAllAction = "disconnect-all";
    public const string AboutAction = "about";
    public const string ShowInDockAction = "show-in-dock";
    public const string QuitAction = "quit";
    public const string SyncNowAction = "sync-now";

    /// <summary>
    /// The line that says how the server stands. Never invoked: it is drawn greyed out.
    /// </summary>
    public const string ServerStateEntry = "server-state";

    private readonly ISystemTrayIcon tray;
    private readonly ConnectionManager connections;
    private readonly ILocalizer localizer;
    private readonly ISettingsService settings;

    /// <summary>
    /// The platform's list of running applications, on a platform that keeps one apart from windows.
    /// </summary>
    /// <remarks>
    /// Offered here as well as in the settings because it is a decision about where the application
    /// is, and the menu bar entry is where somebody who has just noticed the Dock icon is looking.
    /// </remarks>
    private readonly IDockPresence? dock;

    /// <summary>
    /// The platform's own panel about the application, where there is one.
    /// </summary>
    /// <remarks>
    /// On macOS this entry is the only way to that panel. The application menu that would otherwise
    /// carry it is never shown, because an application that stays out of the Dock has no menu bar of
    /// its own. A platform without such a panel does not offer the entry.
    /// </remarks>
    private readonly IApplicationMenu? applicationMenu;

    /// <summary>
    /// The server, in Server mode, whose state the menu names and which "Sync now" asks.
    /// </summary>
    private readonly IServerStatusSource? serverStatus;

    /// <summary>
    /// The one state the icon's colour stands for.
    /// </summary>
    private readonly TrayStatusMonitor status;

    private readonly TimeProvider time;
    private string? serverLine;

    /// <summary>
    /// What the icon was last told, so a report that changes nothing redraws nothing.
    /// </summary>
    private TrayIconColour? appliedColour;
    private bool colourApplied;
    private bool disposed;

    public TrayIconController(
        ISystemTrayIcon tray,
        ConnectionManager connections,
        ILocalizer localizer,
        ISettingsService settings,
        TrayStatusMonitor status,
        IApplicationMenu? applicationMenu = null,
        IDockPresence? dock = null,
        IServerStatusSource? serverStatus = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(status);

        this.tray = tray;
        this.connections = connections;
        this.localizer = localizer;
        this.settings = settings;
        this.status = status;
        this.applicationMenu = applicationMenu;
        this.dock = dock;
        this.serverStatus = serverStatus;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised when the icon itself is activated, which is the way back to the window.
    /// </summary>
    public event EventHandler? ShowWindowRequested;

    /// <summary>
    /// Raised with the identifier of the menu entry the user chose.
    /// </summary>
    public event EventHandler<string>? MenuActionRequested;

    /// <summary>
    /// Places the icon. Must be called from the user interface thread, because the icon is driven by
    /// a window whose messages the application's own loop dispatches.
    /// </summary>
    public void Attach()
    {
        if (!tray.IsAvailable)
        {
            return;
        }

        status.Attach();

        // Before the icon appears, so it does not appear in one colour and turn into another.
        ApplyColour();
        tray.Show(Tooltip());
        RebuildMenu();

        tray.Activated += OnActivated;
        tray.MenuItemInvoked += OnMenuItemInvoked;

        connections.StateChanged += OnStateChanged;
        localizer.LanguageChanged += OnLanguageChanged;
        settings.Changed += OnSettingsChanged;
        status.Changed += OnStatusChanged;

        if (serverStatus is not null)
        {
            serverStatus.Changed += OnServerStatusChanged;
        }
    }

    /// <summary>
    /// Rebuilds only when the line would read differently, because the status changes every time the
    /// server is asked how quickly it answers.
    /// </summary>
    private void OnServerStatusChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.Equals(serverLine, ServerLine(), StringComparison.Ordinal))
            {
                RebuildMenu();
            }
        });

    private string? ServerLine() => serverStatus is null
        ? null
        : ServerStatusText.Compact(localizer, serverStatus.Current, time.GetUtcNow());

    private void OnSettingsChanged(object? sender, PilotSettings changed) =>
        Dispatcher.UIThread.Post(() =>
        {
            RebuildMenu();
            ApplyColour();
        });

    private void OnStatusChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            ApplyColour();
            UpdateTooltip();
        });

    /// <summary>
    /// Draws the icon in the colour of the most pressing state that is not switched off, or as the
    /// platform does when the feature is off, every state that applies is switched off, or the one
    /// that decides has no colour or one that cannot be read.
    /// </summary>
    private void ApplyColour()
    {
        TrayIconColour? colour = Colour(settings.Current.Appearance.TrayIcon, status.Applicable);

        if (colourApplied && colour == appliedColour)
        {
            return;
        }

        tray.SetColour(colour);
        appliedColour = colour;
        colourApplied = true;
    }

    private void OnActivated(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => ShowWindowRequested?.Invoke(this, EventArgs.Empty));

    private void OnMenuItemInvoked(object? sender, string actionId) =>
        Dispatcher.UIThread.Post(() => MenuActionRequested?.Invoke(this, actionId));

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            RebuildMenu();
            UpdateTooltip();
        });

    private void OnStateChanged(object? sender, ConnectionStatusChanged change) =>
        Dispatcher.UIThread.Post(() =>
        {
            RebuildMenu();
            UpdateTooltip();
        });

    /// <summary>
    /// The colour the icon is drawn in, null for the platform's own drawing.
    /// </summary>
    public static TrayIconColour? Colour(TrayIconSettings preferences, IReadOnlyList<TrayStatus> applicable)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(applicable);

        if (!preferences.ShowStatus)
        {
            return null;
        }

        foreach (TrayStatus state in applicable)
        {
            if (preferences.IsOff(state))
            {
                continue;
            }

            return TrayIconColour.TryParse(preferences.Get(state), out TrayIconColour chosen) ? chosen : null;
        }

        return null;
    }

    private void UpdateTooltip() => tray.SetTooltip(Tooltip());

    /// <summary>
    /// Says in words what the colour says, for whoever cannot tell the colours apart, and names the
    /// state whether or not the icon is coloured.
    /// </summary>
    private string Tooltip()
    {
        int active = connections.ActiveCount;

        string text = active == 0
            ? localizer["tray.tooltipIdle"]
            : localizer.Translate("tray.tooltipActive", active);

        TrayStatus current = status.Current;

        return current is TrayStatus.IdleLocal or TrayStatus.Connected or TrayStatus.ConnectedMultiple
            ? text
            : localizer.Translate("tray.tooltipStatus", text, localizer[TrayStatusNames.Key(current)]);
    }

    private void RebuildMenu()
    {
        bool anyActive = connections.ActiveCount > 0;

        List<TrayMenuEntry> entries =
        [
            new TrayMenuEntry(ShowWindowAction, localizer["tray.show"]),
            new TrayMenuEntry(QuickSwitcherAction, localizer["tray.quickSwitcher"]),
            TrayMenuEntry.Separator,
            new TrayMenuEntry(DisconnectAllAction, localizer["tray.disconnectAll"], anyActive),
            TrayMenuEntry.Separator,
        ];

        serverLine = ServerLine();

        if (serverStatus is not null && serverLine is not null)
        {
            ServerStatusSnapshot snapshot = serverStatus.Current;

            entries.Add(new TrayMenuEntry(ServerStateEntry, serverLine, IsEnabled: false));
            entries.Add(new TrayMenuEntry(
                SyncNowAction,
                localizer["tray.syncNow"],
                snapshot.SignedIn && snapshot.State != SyncState.Synchronising));
            entries.Add(TrayMenuEntry.Separator);
        }

        if (dock is not null)
        {
            entries.Add(new TrayMenuEntry(
                ShowInDockAction,
                localizer["tray.showInDock"],
                IsChecked: settings.Current.General.ShowInDock));
        }

        entries.Add(new TrayMenuEntry(SettingsAction, localizer["tray.settings"]));

        if (applicationMenu is not null)
        {
            entries.Add(new TrayMenuEntry(AboutAction, localizer["tray.about"]));
        }

        entries.Add(new TrayMenuEntry(QuitAction, localizer["tray.quit"]));

        tray.SetMenu(entries);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        settings.Changed -= OnSettingsChanged;
        status.Changed -= OnStatusChanged;

        if (serverStatus is not null)
        {
            serverStatus.Changed -= OnServerStatusChanged;
        }

        tray.Activated -= OnActivated;
        tray.MenuItemInvoked -= OnMenuItemInvoked;
        connections.StateChanged -= OnStateChanged;
        localizer.LanguageChanged -= OnLanguageChanged;
        tray.Dispose();
    }
}
