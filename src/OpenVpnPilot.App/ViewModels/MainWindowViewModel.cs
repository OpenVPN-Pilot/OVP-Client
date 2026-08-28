using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The main window: the profile list, the current filter and the detail panel.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>
    /// Pull filters that stop a server from redirecting the host's traffic or DNS. Applied until the
    /// application offers a per profile setting for it.
    /// </summary>
    private static readonly string[] RouteProtection =
    [
        "--pull-filter ignore \"redirect-gateway\"",
        "--pull-filter ignore \"dhcp-option\"",
        "--pull-filter ignore \"block-outside-dns\"",
    ];

    private readonly IProfileStore store;
    private readonly ConnectionManager connections;
    private readonly ProfileNameCache nameCache;
    private readonly TimeProvider timeProvider;
    private readonly List<ProfileItemViewModel> allProfiles = [];
    private readonly Dictionary<Guid, ProfileItemViewModel> byId = [];

    public MainWindowViewModel(
        IProfileStore store,
        ConnectionManager connections,
        ProfileNameCache nameCache,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(nameCache);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.store = store;
        this.connections = connections;
        this.nameCache = nameCache;
        this.timeProvider = timeProvider;

        connections.StatusChanged += OnConnectionStatusChanged;
    }

    /// <summary>
    /// The profiles currently shown, after the search term and the sidebar filter are applied.
    /// </summary>
    public ObservableCollection<ProfileItemViewModel> VisibleProfiles { get; } = [];

    public ObservableCollection<SidebarFilterViewModel> Filters { get; } =
    [
        new SidebarFilterViewModel(SidebarFilterKind.All, "All profiles"),
        new SidebarFilterViewModel(SidebarFilterKind.Active, "Active"),
        new SidebarFilterViewModel(SidebarFilterKind.Favourites, "Favourites"),
        new SidebarFilterViewModel(SidebarFilterKind.Recent, "Recent"),
    ];

    [ObservableProperty]
    public partial ObservableCollection<Folder> Folders { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ProfileItemViewModel? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string SearchTerm { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SidebarFilterViewModel? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSummary))]
    public partial int ActiveCount { get; set; }

    public bool HasSelection => SelectedProfile is not null;

    /// <summary>
    /// Heading of the placeholder shown when the list is empty. The wording distinguishes an empty
    /// library from a search that matched nothing, because the two need different actions.
    /// </summary>
    public string EmptyStateTitle => allProfiles.Count == 0
        ? "No profiles yet"
        : "Nothing matches";

    public string EmptyStateDetail => allProfiles.Count == 0
        ? "Import .ovpn files with the ovp import command to fill the library."
        : "Try a different search term, or pick another entry in the sidebar.";

    /// <summary>
    /// Short summary of the active connections for the status bar.
    /// </summary>
    public string ActiveSummary => ActiveCount == 1 ? "1 connection" : $"{ActiveCount} connections";

    public bool HasProfiles => allProfiles.Count > 0;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;

        try
        {
            IReadOnlyList<Profile> profiles = await store.GetProfilesAsync(cancellationToken);
            IReadOnlyList<Folder> folders = await store.GetFoldersAsync(cancellationToken);

            allProfiles.Clear();
            byId.Clear();

            foreach (Profile profile in profiles)
            {
                ProfileItemViewModel item = new(profile)
                {
                    Status = connections.GetStatus(profile.Id),
                };

                allProfiles.Add(item);
                byId[profile.Id] = item;
            }

            // The credential prompt reads names from here, so it stays in step with the list.
            nameCache.Replace(allProfiles.Select(
                profile => new KeyValuePair<Guid, string>(profile.Id, profile.Name)));

            Folders = new ObservableCollection<Folder>(folders);
            SelectedFilter ??= Filters[0];
            UpdateFilterCounts();
            ApplyFilter();

            StatusMessage = allProfiles.Count == 0
                ? "No profiles yet. Import .ovpn files to get started."
                : $"{allProfiles.Count} profile(s)";

            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateDetail));
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTermChanged(string value) => ApplyFilter();

    partial void OnSelectedFilterChanged(SidebarFilterViewModel? value) => ApplyFilter();

    /// <summary>
    /// Refreshes the badge next to each sidebar entry.
    /// </summary>
    private void UpdateFilterCounts()
    {
        foreach (SidebarFilterViewModel filter in Filters)
        {
            filter.Count = filter.Kind switch
            {
                SidebarFilterKind.All => allProfiles.Count,
                SidebarFilterKind.Active => allProfiles.Count(profile => !profile.IsIdle),
                SidebarFilterKind.Favourites => allProfiles.Count(profile => profile.IsFavourite),
                SidebarFilterKind.Recent => allProfiles.Count(profile => profile.LastConnectedAt is not null),
                _ => 0,
            };
        }
    }

    private void ApplyFilter()
    {
        string term = SearchTerm.Trim().ToLowerInvariant();

        IEnumerable<ProfileItemViewModel> matches = allProfiles.Where(profile => profile.Matches(term));

        matches = (SelectedFilter?.Kind ?? SidebarFilterKind.All) switch
        {
            SidebarFilterKind.Active => matches.Where(profile => !profile.IsIdle),
            SidebarFilterKind.Favourites => matches.Where(profile => profile.IsFavourite),
            SidebarFilterKind.Recent => matches
                .Where(profile => profile.LastConnectedAt is not null)
                .OrderByDescending(profile => profile.LastConnectedAt),
            _ => matches,
        };

        Guid? selectedId = SelectedProfile?.Id;

        VisibleProfiles.Clear();
        foreach (ProfileItemViewModel profile in matches)
        {
            VisibleProfiles.Add(profile);
        }

        // Keep the selection when the filtered set still contains it.
        SelectedProfile = selectedId is { } id
            ? VisibleProfiles.FirstOrDefault(profile => profile.Id == id)
            : SelectedProfile;
    }

    [RelayCommand]
    private async Task ConnectAsync(ProfileItemViewModel? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null || !profile.IsIdle)
        {
            return;
        }

        string? configuration = await store.GetConfigurationAsync(profile.Id);
        if (configuration is null)
        {
            StatusMessage = $"The configuration for '{profile.Name}' could not be read.";
            return;
        }

        try
        {
            VpnConnectionStatus status = await connections.ConnectAsync(
                profile.Id,
                configuration,
                RouteProtection);

            if (status.State == VpnConnectionState.Failed)
            {
                StatusMessage = $"{profile.Name}: {status.Message}";
                return;
            }

            profile.MarkConnected(timeProvider.GetUtcNow());
            await store.RecordConnectionAsync(profile.Id);
            StatusMessage = $"Connecting to {profile.Name}.";
        }
        catch (ManagementUnavailableException exception)
        {
            StatusMessage = $"{profile.Name}: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = $"{profile.Name}: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync(ProfileItemViewModel? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            return;
        }

        await connections.DisconnectAsync(profile.Id);
        StatusMessage = $"Disconnected {profile.Name}.";
    }

    [RelayCommand]
    private async Task DisconnectAllAsync()
    {
        await connections.DisconnectAllAsync();
        StatusMessage = "All connections stopped.";
    }

    [RelayCommand]
    private async Task ToggleFavouriteAsync(ProfileItemViewModel? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            return;
        }

        profile.IsFavourite = !profile.IsFavourite;
        await store.SetFavouriteAsync(profile.Id, profile.IsFavourite);
        UpdateFilterCounts();
        ApplyFilter();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private static string DescribeTransition(ProfileItemViewModel profile, VpnConnectionStatus status) =>
        status.State switch
        {
            VpnConnectionState.Connected => $"{profile.Name} is connected.",
            VpnConnectionState.Reconnecting => $"{profile.Name} is reconnecting. {status.Message}".TrimEnd(),
            VpnConnectionState.Failed => $"{profile.Name}: {status.Message}",
            VpnConnectionState.Disconnected => $"{profile.Name} is disconnected.",
            _ => $"{profile.Name}: {status.State.ToString().ToLowerInvariant()}.",
        };

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusChanged change)
    {
        // The supervisor raises this from its own pump, so the update is marshalled to the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (byId.TryGetValue(change.ProfileId, out ProfileItemViewModel? profile))
            {
                bool stateChanged = profile.Status.State != change.Status.State;
                profile.Status = change.Status;

                if (stateChanged)
                {
                    StatusMessage = DescribeTransition(profile, change.Status);
                }
            }

            ActiveCount = connections.ActiveCount;

            UpdateFilterCounts();

            if (SelectedFilter?.Kind == SidebarFilterKind.Active)
            {
                ApplyFilter();
            }
        });
    }
}
