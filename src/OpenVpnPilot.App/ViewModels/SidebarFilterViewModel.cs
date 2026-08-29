using CommunityToolkit.Mvvm.ComponentModel;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// One entry in the sidebar, with the number of profiles it currently covers.
/// </summary>
/// <remarks>
/// The built in filters take their label from the catalogue and follow a language change, while a
/// folder or a tag is named by the user and is shown exactly as written.
/// </remarks>
public sealed partial class SidebarFilterViewModel : ViewModelBase
{
    private readonly ILocalizer? localizer;
    private readonly string? labelKey;
    private readonly string? fixedLabel;

    private SidebarFilterViewModel(
        SidebarFilterKind kind,
        ILocalizer? localizer,
        string? labelKey,
        string? fixedLabel,
        Guid? folderId,
        string? tagName)
    {
        Kind = kind;
        this.localizer = localizer;
        this.labelKey = labelKey;
        this.fixedLabel = fixedLabel;
        FolderId = folderId;
        TagName = tagName;
    }

    public SidebarFilterKind Kind { get; }

    /// <summary>
    /// Set for a folder entry. Null for every other kind.
    /// </summary>
    public Guid? FolderId { get; }

    /// <summary>
    /// Set for a tag entry. Null for every other kind.
    /// </summary>
    public string? TagName { get; }

    public string Label => labelKey is not null && localizer is not null
        ? localizer[labelKey]
        : fixedLabel ?? string.Empty;

    [ObservableProperty]
    public partial int Count { get; set; }

    /// <summary>
    /// True for entries the user can rename or remove, which is only folders.
    /// </summary>
    public bool IsFolder => Kind == SidebarFilterKind.Folder;

    public static SidebarFilterViewModel ForBuiltIn(
        SidebarFilterKind kind,
        string labelKey,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        return new SidebarFilterViewModel(kind, localizer, labelKey, fixedLabel: null, folderId: null, tagName: null);
    }

    public static SidebarFilterViewModel ForFolder(Guid folderId, string name) =>
        new(SidebarFilterKind.Folder, localizer: null, labelKey: null, name, folderId, tagName: null);

    public static SidebarFilterViewModel ForTag(string name) =>
        new(SidebarFilterKind.Tag, localizer: null, labelKey: null, name, folderId: null, name);

    /// <summary>
    /// Re-reads the label, which a language change requires for the built in entries.
    /// </summary>
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(Label));
}

public enum SidebarFilterKind
{
    All,
    Active,
    Favourites,
    Recent,
    Folder,
    Tag,
}
