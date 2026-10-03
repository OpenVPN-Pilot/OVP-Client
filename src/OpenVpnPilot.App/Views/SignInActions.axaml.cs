using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenVpnPilot.App.Views;

public partial class SignInActions : UserControl
{
    public SignInActions()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
