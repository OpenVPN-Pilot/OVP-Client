using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Keeps a tray icon in step with the connection state and offers the common actions there.
/// </summary>
/// <remarks>
/// The tray is the fastest route to a connection for someone who keeps the window closed, which is
/// how the stock client is normally used. Closing the window hides it rather than exiting, so the
/// tunnels keep running and the icon stays the way back in.
/// </remarks>
public sealed class TrayIconController : IDisposable
{
    private readonly ConnectionManager connections;
    private TrayIcon? icon;
    private NativeMenuItem? disconnectAllItem;
    private bool disposed;

    public TrayIconController(ConnectionManager connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        this.connections = connections;
    }

    public void Attach(Application application, IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(desktop);

        NativeMenuItem showItem = new("Show window");
        showItem.Click += (_, _) => ShowWindow(desktop);

        disconnectAllItem = new NativeMenuItem("Disconnect all") { IsEnabled = false };
        disconnectAllItem.Click += async (_, _) => await connections.DisconnectAllAsync();

        NativeMenuItem quitItem = new("Quit");
        quitItem.Click += (_, _) => desktop.Shutdown();

        NativeMenu menu = [showItem, disconnectAllItem, new NativeMenuItemSeparator(), quitItem];

        icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OpenVpnPilot/Assets/openvpnpilot.ico"))),
            ToolTipText = "OpenVpnPilot",
            Menu = menu,
        };

        icon.Clicked += (_, _) => ShowWindow(desktop);

        TrayIcon.SetIcons(application, [icon]);

        connections.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged change)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int active = connections.ActiveCount;

            if (disconnectAllItem is not null)
            {
                disconnectAllItem.IsEnabled = active > 0;
            }

            if (icon is not null)
            {
                icon.ToolTipText = active == 0
                    ? "OpenVpnPilot"
                    : $"OpenVpnPilot - {active} connection(s) active";
            }
        });
    }

    private static void ShowWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Window? window = desktop.MainWindow;
        if (window is null)
        {
            return;
        }

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connections.StatusChanged -= OnStatusChanged;
        icon?.Dispose();
        icon = null;
    }
}
