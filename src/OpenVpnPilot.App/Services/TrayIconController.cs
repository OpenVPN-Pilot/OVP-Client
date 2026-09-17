using Avalonia.Threading;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
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
    public const string QuitAction = "quit";

    private readonly ISystemTrayIcon tray;
    private readonly ConnectionManager connections;
    private readonly ILocalizer localizer;

    /// <summary>
    /// The platform's own panel about the application, where there is one.
    /// </summary>
    /// <remarks>
    /// On macOS this entry is the only way to that panel. The application menu that would otherwise
    /// carry it is never shown, because an application that stays out of the Dock has no menu bar of
    /// its own. A platform without such a panel does not offer the entry.
    /// </remarks>
    private readonly IApplicationMenu? applicationMenu;

    private bool disposed;

    public TrayIconController(
        ISystemTrayIcon tray,
        ConnectionManager connections,
        ILocalizer localizer,
        IApplicationMenu? applicationMenu = null)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(localizer);

        this.tray = tray;
        this.connections = connections;
        this.localizer = localizer;
        this.applicationMenu = applicationMenu;
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
            new TrayMenuEntry(SettingsAction, localizer["tray.settings"]),
        ];

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

        tray.Activated -= OnActivated;
        tray.MenuItemInvoked -= OnMenuItemInvoked;
        connections.StateChanged -= OnStateChanged;
        localizer.LanguageChanged -= OnLanguageChanged;
        tray.Dispose();
    }
}
