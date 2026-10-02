using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenVpnPilot.App.Views;

public partial class ServerSignInWindow : Window
{
    public ServerSignInWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
