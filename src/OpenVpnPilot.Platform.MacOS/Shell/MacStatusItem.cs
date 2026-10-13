using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
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
/// entry that stays the same colour when everything around it changes, so it is drawn only when the
/// person asked for the state in colour, and the template is what null brings back.
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
    private TrayIconColour? colour;

    /// <summary>
    /// The dots beside menu entries, one per colour, because the menu is rebuilt with every change of
    /// a connection's state and the same few colours come back each time.
    /// </summary>
    private readonly Dictionary<TrayIconColour, Bitmap> badges = [];

    /// <summary>
    /// The size a dot is drawn at: sixteen points, which is a menu's own image size, on a display
    /// that doubles.
    /// </summary>
    private const int BadgePixels = 32;
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

        icon = new TrayIcon
        {
            ToolTipText = tooltip,
            Menu = menu,
        };

        Draw(icon);
        icon.IsVisible = true;
    }

    public void SetColour(TrayIconColour? colour)
    {
        if (this.colour == colour)
        {
            return;
        }

        this.colour = colour;

        if (icon is not null)
        {
            Draw(icon);
        }
    }

    /// <summary>
    /// Gives the status item the template, or the template recoloured.
    /// </summary>
    /// <remarks>
    /// The flag is set before the image, so no image is ever shown under the other one's flag.
    /// </remarks>
    private void Draw(TrayIcon target)
    {
        using Stream image = AssetLoader.Open(new Uri(IconResource));

        if (colour is not { } chosen)
        {
            MacOSProperties.SetIsTemplateIcon(target, true);
            target.Icon = new WindowIcon(image);
            return;
        }

        using Bitmap template = new(image);

        // The decoder decides the layout. Both orders it uses keep alpha last, so red and blue are
        // swapped for the one that starts with red rather than converting the image.
        bool redFirst = template.Format == PixelFormat.Rgba8888;
        TrayIconColour ordered = redFirst ? chosen with { Red = chosen.Blue, Blue = chosen.Red } : chosen;
        bool premultiplied = template.AlphaFormat != AlphaFormat.Unpremul;

        WriteableBitmap tinted = new(
            template.PixelSize,
            template.Dpi,
            redFirst ? PixelFormat.Rgba8888 : PixelFormat.Bgra8888,
            premultiplied ? AlphaFormat.Premul : AlphaFormat.Unpremul);

        using (ILockedFramebuffer frame = tinted.Lock())
        {
            int length = frame.RowBytes * frame.Size.Height;
            template.CopyPixels(new PixelRect(template.PixelSize), frame.Address, length, frame.RowBytes);

            unsafe
            {
                TrayIconTint.Apply(new Span<byte>((void*)frame.Address, length), ordered, premultiplied);
            }
        }

        MacOSProperties.SetIsTemplateIcon(target, false);
        target.Icon = new WindowIcon(tinted);
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

            NativeMenuItem item = new(entry.Label)
            {
                IsEnabled = entry.IsEnabled,
                ToggleType = entry.IsChecked ? MenuItemToggleType.CheckBox : MenuItemToggleType.None,
                IsChecked = entry.IsChecked,
            };
            if (entry.Badge is { } badge)
            {
                item.Icon = Badge(badge);
            }

            string id = entry.Id;
            item.Click += (_, _) => MenuItemInvoked?.Invoke(this, id);
            menu.Items.Add(item);
        }
    }

    private Bitmap Badge(TrayIconColour badge)
    {
        if (badges.TryGetValue(badge, out Bitmap? existing))
        {
            return existing;
        }

        byte[] pixels = TrayBadge.Render(BadgePixels, badge, premultiplied: true);

        // Twice the resolution a point has, so the dot is sixteen points across and sharp.
        WriteableBitmap image = new(
            new PixelSize(BadgePixels, BadgePixels),
            new Vector(144, 144),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (ILockedFramebuffer frame = image.Lock())
        {
            for (int row = 0; row < BadgePixels; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    pixels,
                    row * BadgePixels * 4,
                    frame.Address + (row * frame.RowBytes),
                    BadgePixels * 4);
            }
        }

        badges[badge] = image;
        return image;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        foreach (Bitmap badge in badges.Values)
        {
            badge.Dispose();
        }

        badges.Clear();

        if (icon is not null)
        {
            icon.IsVisible = false;
            icon.Dispose();
            icon = null;
        }
    }
}
