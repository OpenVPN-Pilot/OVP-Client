using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// The live log. Scrolling and the file picker are windowing operations, so they live here.
/// </summary>
public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("SaveButton")!.Click += async (_, _) => await SaveAsync();
        this.FindControl<Button>("OpenFolderButton")!.Click += async (_, _) => await OpenFolderAsync();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is LogViewModel model)
            {
                model.ScrolledToEnd += OnScrolledToEnd;
            }
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private LogViewModel? ViewModel => DataContext as LogViewModel;

    /// <summary>
    /// Follows the newest line.
    /// </summary>
    /// <remarks>
    /// Posted at background priority rather than run straight away: the row that was just added has
    /// not been laid out yet when the view model reports it, so scrolling to the end here would
    /// scroll to where the end was a moment ago and stay one line short for ever.
    /// </remarks>
    private void OnScrolledToEnd(object? sender, EventArgs e) => Dispatcher.UIThread.Post(
        () => this.FindControl<ScrollViewer>("Scroller")?.ScrollToEnd(),
        DispatcherPriority.Background);

    private async Task OpenFolderAsync()
    {
        if (ViewModel is not { } model || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }

        await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(model.DirectoryPath));
    }

    private async Task SaveAsync()
    {
        if (ViewModel is not { } model)
        {
            return;
        }

        IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Title,
            SuggestedFileName = "openvpnpilot.log",
            DefaultExtension = "log",
            FileTypeChoices =
            [
                new FilePickerFileType("Log") { Patterns = ["*.log"] },
            ],
        });

        if (target?.TryGetLocalPath() is { } path)
        {
            await model.SaveAsync(path);
        }
    }
}
