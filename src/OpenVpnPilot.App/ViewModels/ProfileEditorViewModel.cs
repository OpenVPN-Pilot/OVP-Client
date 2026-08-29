using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// Edits everything about a profile except its configuration text.
/// </summary>
/// <remarks>
/// The configuration itself is deliberately not editable here. It was inlined at import time and
/// carries the certificates, so an accidental edit would break a working profile with no way back.
/// Re-importing the file is the supported way to change it.
/// </remarks>
public sealed partial class ProfileEditorViewModel : ViewModelBase
{
    private readonly IProfileStore store;
    private readonly ILocalizer localizer;
    private readonly Guid profileId;

    public ProfileEditorViewModel(
        IProfileStore store,
        ILocalizer localizer,
        ProfileItemViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(profile);

        this.store = store;
        this.localizer = localizer;

        profileId = profile.Id;
        Name = profile.Name;
        Endpoint = profile.Endpoint;
        Notes = profile.Notes ?? string.Empty;
        TagsInput = string.Join(", ", profile.Tags);
        SelectedSlot = profile.FavouriteSlot ?? 0;
        IsFavourite = profile.IsFavourite;

        RouteProtectionChoices =
        [
            new RouteProtectionChoice(null, localizer["editor.routeInherit"]),
            new RouteProtectionChoice(true, localizer["editor.routeProtect"]),
            new RouteProtectionChoice(false, localizer["editor.routeAllow"]),
        ];

        SelectedRouteProtection = RouteProtectionChoices
            .First(choice => choice.Value == profile.ProtectRoutes);
    }

    /// <summary>
    /// Raised when the editor is finished with, with true when something was changed.
    /// </summary>
    public event EventHandler<bool>? Closed;

    /// <summary>
    /// Raised when the user asked for the profile to be removed.
    /// </summary>
    public event EventHandler<Guid>? DeleteRequested;

    public string Endpoint { get; }

    public ObservableCollection<RouteProtectionChoice> RouteProtectionChoices { get; }

    /// <summary>
    /// Zero means no slot. The rest bind the profile to the matching connect shortcut.
    /// </summary>
    public IReadOnlyList<int> Slots { get; } =
        [0, .. Enumerable.Range(1, HotkeyActions.MaximumFavouriteSlot)];

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial string TagsInput { get; set; }

    [ObservableProperty]
    public partial RouteProtectionChoice? SelectedRouteProtection { get; set; }

    [ObservableProperty]
    public partial int SelectedSlot { get; set; }

    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// True while the delete confirmation is showing, so a single click cannot destroy a profile.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; set; }

    public string SlotLabel(int slot) => slot == 0 ? localizer["common.none"] : slot.ToString();

    public string TagsHelp => localizer["tags.help"];

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = localizer["editor.nameRequired"];
            return;
        }

        await store.RenameProfileAsync(profileId, Name);
        await store.SetProfileNotesAsync(profileId, Notes);
        await store.SetRouteProtectionAsync(profileId, SelectedRouteProtection?.Value);
        await store.SetFavouriteAsync(profileId, IsFavourite || SelectedSlot > 0);

        await store.SetProfileTagsAsync(
            profileId,
            TagsInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // The slot is set last, because assigning one also makes the profile a favourite and would
        // otherwise be undone by the favourite update above.
        await store.SetFavouriteSlotAsync(profileId, SelectedSlot == 0 ? null : SelectedSlot);

        Closed?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    [RelayCommand]
    private void RequestDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        await store.DeleteProfileAsync(profileId);
        DeleteRequested?.Invoke(this, profileId);
        Closed?.Invoke(this, true);
    }
}

/// <summary>
/// One entry in the route protection picker. A null value follows the application wide setting.
/// </summary>
public sealed record RouteProtectionChoice(bool? Value, string Name);
