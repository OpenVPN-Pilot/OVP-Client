namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// The application's presence in the operating system notification area.
/// </summary>
/// <remarks>
/// The tray icon and the notifications belong together because both are drawn from the same entry:
/// a balloon is attached to an icon, not shown on its own. Splitting them would mean two icons in
/// the notification area, one of which exists only to carry messages.
///
/// The menu is described rather than built, so the presentation layer decides the wording and the
/// platform decides how a menu is drawn.
/// </remarks>
public interface ISystemTrayIcon : IDisposable
{
    /// <summary>
    /// False when no notification area is available, in which case the application must not depend
    /// on the tray as its only way back to the window.
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// Places the icon in the notification area. Must be called from the user interface thread.
    /// </summary>
    public void Show(string tooltip);

    /// <summary>
    /// Replaces the hover text, for example with the number of active tunnels.
    /// </summary>
    public void SetTooltip(string tooltip);

    /// <summary>
    /// Replaces the context menu. Called again whenever the entries or their wording change.
    /// </summary>
    public void SetMenu(IReadOnlyList<TrayMenuEntry> entries);

    /// <summary>
    /// Raised when the icon itself is activated, which is how the window is brought back.
    /// </summary>
    public event EventHandler? Activated;

    /// <summary>
    /// Raised with the identifier of the menu entry the user chose.
    /// </summary>
    public event EventHandler<string>? MenuItemInvoked;
}

/// <summary>
/// One entry of the tray menu.
/// </summary>
/// <param name="Id">Identifies the entry when it is invoked. Empty for a separator.</param>
/// <param name="Label">The text shown, already localized.</param>
/// <param name="IsEnabled">False draws the entry greyed out rather than hiding it.</param>
/// <param name="IsSeparator">True draws a dividing line and ignores the other fields.</param>
/// <param name="IsChecked">
/// True draws the entry ticked, for one that stands for a setting that is on rather than for an
/// action. Invoking it is still reported the same way; what it means is the caller's.
/// </param>
public sealed record TrayMenuEntry(
    string Id,
    string Label,
    bool IsEnabled = true,
    bool IsSeparator = false,
    bool IsChecked = false)
{
    public static TrayMenuEntry Separator { get; } =
        new(string.Empty, string.Empty, IsEnabled: false, IsSeparator: true);
}
