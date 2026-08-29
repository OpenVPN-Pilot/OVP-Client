using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
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

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private QuickSwitcherViewModel? ViewModel => DataContext as QuickSwitcherViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Activate();
        this.FindControl<TextBox>("QueryBox")?.Focus();
    }

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
                ViewModel?.AcceptCommand.Execute(null);
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
