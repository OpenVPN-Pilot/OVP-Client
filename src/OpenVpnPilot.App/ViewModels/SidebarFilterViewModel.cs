using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// One entry in the sidebar, with the number of profiles it currently covers.
/// </summary>
public sealed partial class SidebarFilterViewModel : ViewModelBase
{
    public SidebarFilterViewModel(SidebarFilterKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public SidebarFilterKind Kind { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial int Count { get; set; }
}

public enum SidebarFilterKind
{
    All,
    Active,
    Favourites,
    Recent,
}
