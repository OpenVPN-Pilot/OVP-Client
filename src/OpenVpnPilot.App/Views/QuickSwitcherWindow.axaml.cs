using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// The borderless palette. Typing filters, the arrow keys move, return connects.
/// </summary>
/// <remarks>
/// The keyboard is handled here rather than through key bindings because the arrow keys have to move
/// the list while the focus stays in the text box. Losing the window to another application closes
/// the palette: it is a transient surface, not a window to manage.
/// </remarks>
public partial class QuickSwitcherWindow : Window
{
    public QuickSwitcherWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => Close();
    }

    /// <summary>
    /// Raised when the palette was moved to another display, carrying its identity.
    /// </summary>
    /// <remarks>
    /// The window moves itself, because placing a window is a windowing operation, and reports the
    /// choice rather than writing it. What is remembered and where is not the window's business.
    /// </remarks>
    public event EventHandler<string>? ScreenChosen;

    /// <summary>
    /// The display the palette should open on, or null to use the one the pointer is on.
    /// </summary>
    public string? PreferredScreen { get; set; }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private QuickSwitcherViewModel? ViewModel => DataContext as QuickSwitcherViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        PlaceOn(ScreenPlacement.Match(Displays, PreferredScreen) ?? Current);

        Activate();
        this.FindControl<TextBox>("QueryBox")?.Focus();
    }

    /// <summary>
    /// Moves the palette one display along and remembers where it went.
    /// </summary>
    private void MoveToAdjacentScreen(int direction)
    {
        if (ScreenPlacement.Step(Displays, Current, direction) is not { } target)
        {
            return;
        }

        PlaceOn(target);
        ScreenChosen?.Invoke(this, ScreenPlacement.Identify(target));
    }

    /// <summary>
    /// The displays, read once through the framework and reasoned about afterwards.
    /// </summary>
    private IReadOnlyList<ScreenInfo> Displays => ScreenInfo.From(Screens.All);

    /// <summary>
    /// The display the palette is on, when the platform can say.
    /// </summary>
    /// <remarks>
    /// The primary display is the fallback rather than nothing, because the window is placed
    /// manually: without an answer here it would open in the top left corner of the desktop.
    /// </remarks>
    private ScreenInfo? Current
    {
        get
        {
            Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            return screen is null ? null : ScreenInfo.From(screen);
        }
    }

    private void PlaceOn(ScreenInfo? screen)
    {
        if (screen is { } target)
        {
            ScreenPlacement.CentreOn(this, target);
        }
    }

    /// <summary>
    /// The modifier the platform uses for application shortcuts.
    /// </summary>
    private static KeyModifiers CommandModifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        switch (e.Key)
        {
            case Key.Escape:
                ViewModel?.DismissCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Enter:
                // Return connects and leaves you where you were, which is the whole point of a
                // palette. The command modifier and return says you want to watch it happen: control
                // on Windows, command on macOS, as the platform defines it for every shortcut.
                if (e.KeyModifiers.HasFlag(CommandModifier))
                {
                    ViewModel?.AcceptAndShowCommand.Execute(null);
                }
                else
                {
                    ViewModel?.AcceptCommand.Execute(null);
                }

                e.Handled = true;
                return;

            case Key.Space when ViewModel?.IsDisconnecting == true:
                // Only where choosing several is the point. In the connect palette a space belongs
                // to whatever is being typed.
                ViewModel.ToggleTick();
                e.Handled = true;
                return;

            // Alt and an arrow moves the palette between displays. The arrows on their own move the
            // selection, which is why the modifier is checked before them rather than after.
            case Key.Left or Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                MoveToAdjacentScreen(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
                return;

            case Key.Down:
                ViewModel?.MoveSelection(1);
                e.Handled = true;
                return;

            case Key.Up:
                ViewModel?.MoveSelection(-1);
                e.Handled = true;
                return;

            case Key.PageDown:
                ViewModel?.MoveSelection(8);
                e.Handled = true;
                return;

            case Key.PageUp:
                ViewModel?.MoveSelection(-8);
                e.Handled = true;
                return;

            default:
                base.OnKeyDown(e);
                return;
        }
    }
}
