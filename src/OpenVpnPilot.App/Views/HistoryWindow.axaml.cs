using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// The connection history, with an export that has to go through the platform file picker.
/// </summary>
/// <remarks>
/// The picker belongs here rather than in the view model: choosing a destination is a windowing
/// operation, and a view model that opens dialogs cannot be tested.
/// </remarks>
public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("ExportButton")!.Click += async (_, _) => await ExportAsync();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async Task ExportAsync()
    {
        if (DataContext is not HistoryViewModel viewModel)
        {
            return;
        }

        IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Title,
            SuggestedFileName = "openvpnpilot-history.csv",
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
            ],
        });

        if (target?.TryGetLocalPath() is { } path)
        {
            await viewModel.ExportCsvAsync(path);
        }
    }
}
