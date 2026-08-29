using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// The export screen. Choosing a destination is a windowing operation and belongs here.
/// </summary>
public partial class ExportWindow : Window
{
    public ExportWindow()
    {
        InitializeComponent();

        this.FindControl<Button>("ExportButton")!.Click += async (_, _) => await ExportAsync();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private ExportViewModel? ViewModel => DataContext as ExportViewModel;

    private async Task ExportAsync()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.ExportAsPackage)
        {
            IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Title,
                SuggestedFileName = viewModel.SuggestedFileName,
                DefaultExtension = "ovppkg",
                FileTypeChoices =
                [
                    new FilePickerFileType("OpenVpnPilot package") { Patterns = ["*.ovppkg"] },
                ],
            });

            if (target?.TryGetLocalPath() is { } path)
            {
                await viewModel.WritePackageAsync(path);
            }

            return;
        }

        // One file per profile, so the destination is a directory rather than a file.
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = Title, AllowMultiple = false });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } directory)
        {
            await viewModel.WriteConfigurationsAsync(directory);
        }
    }
}
