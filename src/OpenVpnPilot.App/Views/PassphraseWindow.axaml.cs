using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// Asks for the passphrase of the shared library.
/// </summary>
public partial class PassphraseWindow : Window
{
    public PassphraseWindow()
    {
        InitializeComponent();

        Opened += (_, _) => this.FindControl<TextBox>("PassphraseBox")?.Focus();
    }

    /// <summary>
    /// Shows a prompt over another window and reports whether a passphrase was accepted.
    /// </summary>
    public static async Task<bool> AskAsync(Window owner, PassphrasePromptViewModel prompt)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(prompt);

        PassphraseWindow window = new() { DataContext = prompt };
        bool accepted = false;

        void OnClosed(object? sender, bool result)
        {
            accepted = result;
            window.Close();
        }

        prompt.Closed += OnClosed;

        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            prompt.Closed -= OnClosed;
        }

        return accepted;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
