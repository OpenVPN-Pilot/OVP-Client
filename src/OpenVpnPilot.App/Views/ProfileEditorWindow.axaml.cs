using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// Edits one profile: what the application keeps about it, and its configuration.
/// </summary>
public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow()
    {
        InitializeComponent();

        // The name is what is most often changed, and with the focus there a rename is typing and
        // return, which is what the editor saves on.
        Opened += (_, _) => this.FindControl<TextBox>("NameBox")?.Focus();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
