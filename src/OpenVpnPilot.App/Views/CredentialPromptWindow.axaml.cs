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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The focus goes to the first field the user still has to fill in, which for a stored
        // credential answering a one time code is the code, not the user name.
        if (DataContext is not CredentialPromptViewModel viewModel)
        {
            return;
        }

        Control? target = viewModel switch
        {
            { HasChallenge: true } => this.FindControl<TextBox>("ChallengeBox"),
            { NeedsUsername: true, Username.Length: 0 } => this.FindControl<TextBox>("UsernameBox"),
            _ => this.FindControl<TextBox>("PasswordBox"),
        };

        target?.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing with the title bar counts as cancelling, so the connection is not left waiting.
        (DataContext as CredentialPromptViewModel)?.Abandon();
        base.OnClosed(e);
    }
}
