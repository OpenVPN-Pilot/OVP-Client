using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

public partial class CredentialPromptWindow : Window
{
    public CredentialPromptWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing with the title bar counts as cancelling, so the connection is not left waiting.
        (DataContext as CredentialPromptViewModel)?.Abandon();
        base.OnClosed(e);
    }
}
