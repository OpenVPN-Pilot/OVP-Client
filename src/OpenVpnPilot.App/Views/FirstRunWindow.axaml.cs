using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

public partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing the question without answering is deciding later, which keeps the profiles on this
        // computer. While the application restarts into a server there is nothing left to decide.
        (DataContext as FirstRunViewModel)?.Abandon();
        base.OnClosed(e);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
