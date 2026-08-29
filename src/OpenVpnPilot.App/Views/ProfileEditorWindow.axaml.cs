using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// Edits the metadata of one profile: its name, where it is filed, its tags and its shortcut slot.
/// </summary>
public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
