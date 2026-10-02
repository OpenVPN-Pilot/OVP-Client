using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

public partial class FirstSyncWindow : Window
{
    public FirstSyncWindow()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is FirstSyncViewModel model)
        {
            await model.StartAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing the window goes on to the main window, which synchronises in the background as on
        // any start. Leaving for this computer ends the application instead.
        (DataContext as FirstSyncViewModel)?.Abandon();
        base.OnClosed(e);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
