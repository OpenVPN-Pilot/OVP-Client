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

    private readonly TimeProvider time;
    private string? serverLine;
    private bool disposed;

    public TrayIconController(
        ISystemTrayIcon tray,
        ConnectionManager connections,
        ILocalizer localizer,
        ISettingsService settings,
        IApplicationMenu? applicationMenu = null,
        IDockPresence? dock = null,
        IServerStatusSource? serverStatus = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(settings);

        this.tray = tray;
        this.connections = connections;
        this.localizer = localizer;
        this.settings = settings;
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

        tray.Show(localizer["tray.tooltipIdle"]);
        RebuildMenu();

        tray.Activated += OnActivated;
        tray.MenuItemInvoked += OnMenuItemInvoked;

        connections.StateChanged += OnStateChanged;
        localizer.LanguageChanged += OnLanguageChanged;
        settings.Changed += OnSettingsChanged;

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
        Dispatcher.UIThread.Post(RebuildMenu);

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

    private void UpdateTooltip()
    {
        int active = connections.ActiveCount;

        tray.SetTooltip(active == 0
            ? localizer["tray.tooltipIdle"]
            : localizer.Translate("tray.tooltipActive", active));
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
