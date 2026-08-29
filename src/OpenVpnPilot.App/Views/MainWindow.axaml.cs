using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// The main window. Configurations dropped on it are handed to the import wizard.
/// </summary>
/// <remarks>
/// Dropping is the short path to importing now that the wizard is opened from the settings screen
/// rather than from the header. The window does not import anything itself: it reports what was
/// dropped and whoever coordinates the windows decides where that goes.
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    /// <summary>
    /// Raised with the local paths of whatever was dropped on the window.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }

        List<string> paths = items
            .Select(item => item.TryGetLocalPath())
            .OfType<string>()
            .ToList();

        if (paths.Count > 0)
        {
            FilesDropped?.Invoke(this, paths);
        }
    }
}
