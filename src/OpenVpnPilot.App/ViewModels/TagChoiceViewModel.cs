using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// One tag offered as a way to tick every profile that carries it.
/// </summary>
public sealed partial class TagChoiceViewModel : ViewModelBase
{
    public TagChoiceViewModel(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }

    public int Count { get; }

    public string Label => $"{Name} ({Count.ToString(CultureInfo.CurrentCulture)})";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
