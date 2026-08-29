using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// The main window. Configurations dropped on it are handed to the import wizard, and a profile
/// dragged onto a tag is handed to whoever assigns tags.
/// </summary>
/// <remarks>
/// Dropping is the short path to importing now that the wizard is opened from the settings screen
/// rather than from the header. The window imports nothing and tags nothing itself: it reports what
/// was dropped where, and whoever coordinates the windows decides what that means.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>
    /// The profiles being dragged, carried inside the process only.
    /// </summary>
    /// <remarks>
    /// An in process format never reaches the platform clipboard, so a set of identifiers stays a
    /// set of identifiers rather than being flattened into text that another application could pick
    /// up and that this one would have to parse back.
    /// </remarks>
    private static readonly DataFormat<DraggedProfiles> ProfileFormat =
        DataFormat.CreateInProcessFormat<DraggedProfiles>("openvpnpilot/profiles");

    /// <summary>
    /// How far the pointer has to travel before a press becomes a drag rather than a click.
    /// </summary>
    private const double DragThreshold = 6;

    private Point pressedAt;
    private Guid pressedProfile;

    /// <summary>
    /// The press a drag would start from.
    /// </summary>
    /// <remarks>
    /// Avalonia begins a drag from the event that triggered it, and the trigger is a press. The
    /// decision to drag is only made once the pointer has moved far enough to mean it, so the press
    /// is kept until then: starting one the moment a button goes down would make every click on a
    /// row a drag and nothing would ever be merely selected.
    /// </remarks>
    private PointerPressedEventArgs? pressedArgs;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        AddHandler(PointerPressedEvent, OnPointerPressedForDrag, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedForDrag, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Raised with the local paths of whatever was dropped on the window.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    /// <summary>
    /// Raised when profiles were dropped on a tag in the sidebar.
    /// </summary>
    public event EventHandler<ProfileTagDrop>? ProfilesDroppedOnTag;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnPointerPressedForDrag(object? sender, PointerPressedEventArgs e)
    {
        pressedProfile = Guid.Empty;
        pressedArgs = null;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || ProfileUnder(e.Source) is not { } profile)
        {
            return;
        }

        pressedAt = e.GetPosition(this);
        pressedProfile = profile.Id;
        pressedArgs = e;
    }

    private async void OnPointerMovedForDrag(object? sender, PointerEventArgs e)
    {
        // An event handler is the one place an async void is unavoidable. Nothing below throws past
        // this point: the drag either starts or it does not.
        if (pressedProfile == Guid.Empty
            || pressedArgs is not { } pressed
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || ViewModel is not { } viewModel)
        {
            return;
        }

        Vector moved = e.GetPosition(this) - pressedAt;

        if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
        {
            return;
        }

        IReadOnlyList<Guid> dragging = viewModel.ProfilesToDrag(pressedProfile);
        pressedProfile = Guid.Empty;
        pressedArgs = null;

        DataTransfer transfer = new();
        transfer.Add(DataTransferItem.Create(ProfileFormat, new DraggedProfiles(dragging)));

        await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Link);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            return;
        }

        // A profile can only be dropped on a tag, so anywhere else says so by refusing.
        e.DragEffects = e.DataTransfer.TryGetValue(ProfileFormat) is not null && TagUnder(e.Source) is not null
            ? DragDropEffects.Link
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(ProfileFormat) is { } dragged)
        {
            if (TagUnder(e.Source) is { } tag)
            {
                ProfilesDroppedOnTag?.Invoke(this, new ProfileTagDrop(dragged.ProfileIds, tag));
            }

            return;
        }

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

    /// <summary>
    /// The profile row the event came from, or null when it came from anywhere else.
    /// </summary>
    private static ProfileItemViewModel? ProfileUnder(object? source) =>
        DataContextOf<ProfileItemViewModel>(source);

    /// <summary>
    /// The tag name the event came from, or null when the target is not a tag entry.
    /// </summary>
    private static string? TagUnder(object? source) =>
        DataContextOf<SidebarFilterViewModel>(source) is { Kind: SidebarFilterKind.Tag } filter
            ? filter.TagName
            : null;

    /// <summary>
    /// Walks up from whatever the pointer hit until something carries the wanted view model.
    /// </summary>
    /// <remarks>
    /// A pointer lands on a text block or a border inside a row, never on the row itself, so the
    /// data context has to be looked for rather than read off the source.
    /// </remarks>
    private static T? DataContextOf<T>(object? source)
        where T : class
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is StyledElement { DataContext: T found })
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>
/// The profiles a drag is carrying.
/// </summary>
public sealed record DraggedProfiles(IReadOnlyList<Guid> ProfileIds);

/// <summary>
/// Profiles that were dropped on a tag.
/// </summary>
public sealed record ProfileTagDrop(IReadOnlyList<Guid> ProfileIds, string TagName);
