using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// The import wizard. Sources arrive from the file pickers or by being dropped on the window.
/// </summary>
public partial class ImportWindow : Window
{
    public ImportWindow()
    {
        InitializeComponent();

        this.FindControl<Button>("PickFilesButton")!.Click += async (_, _) => await PickFilesAsync();
        this.FindControl<Button>("PickFolderButton")!.Click += async (_, _) => await PickFolderAsync();
        this.FindControl<Button>("PickArchiveButton")!.Click += async (_, _) => await PickArchiveAsync();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private ImportViewModel? ViewModel => DataContext as ImportViewModel;

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // An event handler is the one place an async void is unavoidable, and the work below cannot
        // throw past this point because the view model reports its own failures.
        IEnumerable<IStorageItem>? items = e.DataTransfer.TryGetFiles();

        if (items is null || ViewModel is null)
        {
            return;
        }

        List<string> paths = items
            .Select(item => item.TryGetLocalPath())
            .OfType<string>()
            .ToList();

        if (paths.Count > 0)
        {
            await ViewModel.ExamineAsync(paths);
        }
    }

    private async Task PickFilesAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("OpenVPN") { Patterns = ["*.ovpn"] },
                ],
            });

        await ExamineAsync(files.Select(file => file.TryGetLocalPath()));
    }

    private async Task PickFolderAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false });

        await ExamineAsync(folders.Select(folder => folder.TryGetLocalPath()));
    }

    private async Task PickArchiveAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("ZIP") { Patterns = ["*.zip"] },
                ],
            });

        await ExamineAsync(files.Select(file => file.TryGetLocalPath()));
    }

    private async Task ExamineAsync(IEnumerable<string?> paths)
    {
        List<string> resolved = paths.OfType<string>().ToList();

        if (resolved.Count > 0 && ViewModel is not null)
        {
            await ViewModel.ExamineAsync(resolved);
        }
    }
}
