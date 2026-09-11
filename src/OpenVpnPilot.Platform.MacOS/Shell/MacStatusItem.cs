using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// The application's entry in the menu bar.
/// </summary>
/// <remarks>
/// Avalonia's tray icon is a status item on macOS, so this only has to describe it. The image is a
/// template: black shapes on transparency that the menu bar tints for light and dark mode and for a
/// highlighted item, which is what every other entry up there does. A coloured image would be the one
/// entry that stays the same colour when everything around it changes.
///
/// Clicking a status item opens its menu rather than raising an event, so the way back to the window
/// is the first entry of that menu instead of a click on the icon.
///
/// The menu is one object for the life of the icon and only its entries are replaced. Avalonia's
/// exporter on macOS keeps the menu it was first given and refuses a different one, which it reports
/// by throwing from the property setter; that is how the first build ended at startup.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacStatusItem : ISystemTrayIcon
{
    private const string IconResource = "avares://OpenVpnPilot.Platform.MacOS/Assets/status-item.png";

    private readonly NativeMenu menu = [];
    private TrayIcon? icon;
    private bool disposed;

    public bool IsAvailable => !disposed;

    /// <summary>
    /// Never raised: a status item shows its menu when clicked.
    /// </summary>
    public event EventHandler? Activated
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? MenuItemInvoked;

    public void Show(string tooltip)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (icon is not null)
        {
            icon.ToolTipText = tooltip;
            return;
        }

        using Stream image = AssetLoader.Open(new Uri(IconResource));

        icon = new TrayIcon
        {
            Icon = new WindowIcon(image),
            ToolTipText = tooltip,
            Menu = menu,
        };

        MacOSProperties.SetIsTemplateIcon(icon, true);
        icon.IsVisible = true;
    }

    public void SetTooltip(string tooltip)
    {
        ArgumentNullException.ThrowIfNull(tooltip);

        if (icon is not null)
        {
            icon.ToolTipText = tooltip;
        }
    }

    public void SetMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (icon is null)
        {
            return;
        }

        menu.Items.Clear();

        foreach (TrayMenuEntry entry in entries)
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }

            NativeMenuItem item = new(entry.Label) { IsEnabled = entry.IsEnabled };
            string id = entry.Id;
            item.Click += (_, _) => MenuItemInvoked?.Invoke(this, id);
            menu.Items.Add(item);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (icon is not null)
        {
            icon.IsVisible = false;
            icon.Dispose();
            icon = null;
        }
    }
}
