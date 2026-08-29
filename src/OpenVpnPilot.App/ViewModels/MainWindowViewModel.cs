using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The main window: the profile list, the current filter and the detail panel.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IProfileStore store;
    private readonly ConnectionManager connections;
    private readonly ProfileNameCache nameCache;
    private readonly ISettingsService settings;
    private readonly ILocalizer localizer;
    private readonly TimeProvider timeProvider;

    private readonly List<ProfileItemViewModel> allProfiles = [];
    private readonly Dictionary<Guid, ProfileItemViewModel> byId = [];
    private readonly Dictionary<Guid, string> folderNames = [];
    private readonly DispatcherTimer uptimeTimer;
    private bool disposed;

    public MainWindowViewModel(
        IProfileStore store,
        ConnectionManager connections,
        ProfileNameCache nameCache,
        ISettingsService settings,
        ILocalizer localizer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(nameCache);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.store = store;
        this.connections = connections;
        this.nameCache = nameCache;
        this.settings = settings;
        this.localizer = localizer;
        this.timeProvider = timeProvider;

        connections.StatusChanged += OnConnectionStatusChanged;
        localizer.LanguageChanged += OnLanguageChanged;

        Filters =
        [
            SidebarFilterViewModel.ForBuiltIn(SidebarFilterKind.All, "nav.all", localizer),
            SidebarFilterViewModel.ForBuiltIn(SidebarFilterKind.Active, "nav.active", localizer),
            SidebarFilterViewModel.ForBuiltIn(SidebarFilterKind.Favourites, "nav.favourites", localizer),
            SidebarFilterViewModel.ForBuiltIn(SidebarFilterKind.Recent, "nav.recent", localizer),
        ];

        // A connected row shows its uptime, which has to advance on its own because nothing in the
        // connection state changes while it does.
        uptimeTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => TickUptime());
    }

    /// <summary>
    /// Raised when a screen has to be opened. The window owns the dialogs, not the view model.
    /// </summary>
    public event EventHandler<AppScreen>? ScreenRequested;

    /// <summary>
    /// The profiles currently shown, after the search term and the sidebar filter are applied.
    /// </summary>
    public ObservableCollection<ProfileItemViewModel> VisibleProfiles { get; } = [];

    public ObservableCollection<SidebarFilterViewModel> Filters { get; }

    public ObservableCollection<SidebarFilterViewModel> FolderFilters { get; } = [];

    public ObservableCollection<SidebarFilterViewModel> TagFilters { get; } = [];

    public bool HasFolders => FolderFilters.Count > 0;

    public bool HasTags => TagFilters.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ProfileItemViewModel? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string SearchTerm { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewFolderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCreatingFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderSelected))]
    public partial SidebarFilterViewModel? SelectedFilter { get; set; }

    /// <summary>
    /// True when the sidebar selection is a folder, which is what the folder actions apply to.
    /// </summary>
    public bool IsFolderSelected => SelectedFilter?.Kind == SidebarFilterKind.Folder;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSummary))]
    [NotifyPropertyChangedFor(nameof(HasActiveConnections))]
    public partial int ActiveCount { get; set; }

    public bool HasSelection => SelectedProfile is not null;

    public bool HasActiveConnections => ActiveCount > 0;

    /// <summary>
    /// Total received across every active tunnel, for the status bar.
    /// </summary>
    /// <remarks>
    /// Someone running several tunnels wants one figure for the machine, not a sum they compute by
    /// clicking through each connection in turn.
    /// </remarks>
    [ObservableProperty]
    public partial long TotalBytesReceived { get; set; }

    [ObservableProperty]
    public partial long TotalBytesSent { get; set; }

    /// <summary>
    /// Heading of the placeholder shown when the list is empty. The wording distinguishes an empty
    /// library from a search that matched nothing, because the two need different actions.
    /// </summary>
    public string EmptyStateTitle => allProfiles.Count == 0
        ? localizer["empty.noProfilesTitle"]
        : localizer["empty.noMatchTitle"];

    public string EmptyStateDetail => allProfiles.Count == 0
        ? localizer["empty.noProfilesDetail"]
        : localizer["empty.noMatchDetail"];

    /// <summary>
    /// Short summary of the active connections for the status bar.
    /// </summary>
    public string ActiveSummary => ActiveCount == 1
        ? localizer["status.connectionSingular"]
        : localizer.Translate("status.connectionPlural", ActiveCount);

    public bool HasProfiles => allProfiles.Count > 0;

    /// <summary>
    /// Every profile currently loaded, for screens that need the whole set rather than the filtered one.
    /// </summary>
    public IReadOnlyList<ProfileItemViewModel> AllProfiles => allProfiles;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;

        try
        {
            IReadOnlyList<Profile> profiles = await store.GetProfilesAsync(cancellationToken);
            IReadOnlyList<Folder> folders = await store.GetFoldersAsync(cancellationToken);
            IReadOnlyList<TagSummary> tags = await store.GetTagsAsync(cancellationToken);
            IReadOnlyDictionary<Guid, IReadOnlyList<string>> profileTags =
                await store.GetProfileTagsAsync(cancellationToken);

            allProfiles.Clear();
            byId.Clear();
            folderNames.Clear();

            foreach (Folder folder in folders)
            {
                folderNames[folder.Id] = folder.Name;
            }

            foreach (Profile profile in profiles)
            {
                profileTags.TryGetValue(profile.Id, out IReadOnlyList<string>? assigned);

                ProfileItemViewModel item = new(profile, localizer, assigned)
                {
                    Status = connections.GetStatus(profile.Id),
                    FolderName = profile.FolderId is { } id && folderNames.TryGetValue(id, out string? name)
                        ? name
                        : null,
                };

                allProfiles.Add(item);
                byId[profile.Id] = item;
            }

            // The credential prompt and the notifications read names from here, so it stays in step.
            nameCache.Replace(allProfiles.Select(
                profile => new KeyValuePair<Guid, string>(profile.Id, profile.Name)));

            RebuildSidebar(folders, tags);

            SelectedFilter ??= Filters[0];
            UpdateFilterCounts();
            ApplyFilter();

            StatusMessage = RestingStatusMessage;

            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateDetail));

            ActiveCount = connections.ActiveCount;
            uptimeTimer.IsEnabled = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Executes a shortcut action. Called on the user interface thread.
    /// </summary>
    public async Task ExecuteHotkeyActionAsync(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);

        if (HotkeyActions.FavouriteSlotOf(actionId) is { } slot)
        {
            await ConnectSlotAsync(slot);
            return;
        }

        switch (actionId)
        {
            case HotkeyActions.ToggleQuickSwitcher:
                ScreenRequested?.Invoke(this, AppScreen.QuickSwitcher);
                break;

            case HotkeyActions.ShowMainWindow:
                ScreenRequested?.Invoke(this, AppScreen.MainWindow);
                break;

            case HotkeyActions.ConnectLastUsed:
                await ConnectLastUsedAsync();
                break;

            case HotkeyActions.ReconnectActive:
                await ReconnectActiveAsync();
                break;

            case HotkeyActions.DisconnectActive:
                await DisconnectActiveAsync();
                break;

            case HotkeyActions.DisconnectAll:
                await DisconnectAllAsync();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Connects a profile by identifier, which is how the quick switcher and the shortcuts reach one.
    /// </summary>
    public async Task ConnectByIdAsync(Guid profileId)
    {
        if (byId.TryGetValue(profileId, out ProfileItemViewModel? profile))
        {
            await ConnectAsync(profile);
        }
    }

    public void SelectProfile(Guid profileId)
    {
        if (byId.TryGetValue(profileId, out ProfileItemViewModel? profile))
        {
            SelectedFilter = Filters[0];
            SearchTerm = string.Empty;
            SelectedProfile = VisibleProfiles.FirstOrDefault(item => item.Id == profileId) ?? profile;
        }
    }

    partial void OnSearchTermChanged(string value) => ApplyFilter();

    partial void OnSelectedFilterChanged(SidebarFilterViewModel? value) => ApplyFilter();

    private void RebuildSidebar(IReadOnlyList<Folder> folders, IReadOnlyList<TagSummary> tags)
    {
        Guid? selectedFolder = SelectedFilter?.FolderId;
        string? selectedTag = SelectedFilter?.TagName;

        FolderFilters.Clear();
        foreach (Folder folder in folders)
        {
            FolderFilters.Add(SidebarFilterViewModel.ForFolder(folder.Id, folder.Name));
        }

        TagFilters.Clear();
        foreach (TagSummary tag in tags)
        {
            TagFilters.Add(SidebarFilterViewModel.ForTag(tag.Name));
        }

        // A rebuild replaces the instances, so a selection has to be re-established by identity.
        if (selectedFolder is { } folderId)
        {
            SelectedFilter = FolderFilters.FirstOrDefault(filter => filter.FolderId == folderId) ?? Filters[0];
        }
        else if (selectedTag is not null)
        {
            SelectedFilter = TagFilters.FirstOrDefault(filter => filter.TagName == selectedTag) ?? Filters[0];
        }

        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(HasTags));
    }

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

        foreach (SidebarFilterViewModel folder in FolderFilters)
        {
            folder.Count = allProfiles.Count(profile => profile.FolderId == folder.FolderId);
        }

        foreach (SidebarFilterViewModel tag in TagFilters)
        {
            tag.Count = allProfiles.Count(profile =>
                profile.Tags.Contains(tag.TagName!, StringComparer.OrdinalIgnoreCase));
        }
    }

    private void ApplyFilter()
    {
        string term = SearchTerm.Trim().ToLowerInvariant();

        IEnumerable<ProfileItemViewModel> matches = allProfiles.Where(profile => profile.Matches(term));

        SidebarFilterViewModel? filter = SelectedFilter;

        matches = (filter?.Kind ?? SidebarFilterKind.All) switch
        {
            SidebarFilterKind.Active => matches.Where(profile => !profile.IsIdle),
            SidebarFilterKind.Favourites => matches.Where(profile => profile.IsFavourite),
            SidebarFilterKind.Recent => matches
                .Where(profile => profile.LastConnectedAt is not null)
                .OrderByDescending(profile => profile.LastConnectedAt),
            SidebarFilterKind.Folder => matches.Where(profile => profile.FolderId == filter!.FolderId),
            SidebarFilterKind.Tag => matches.Where(profile =>
                profile.Tags.Contains(filter!.TagName!, StringComparer.OrdinalIgnoreCase)),
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
            StatusMessage = localizer.Translate("status.configurationUnreadable", profile.Name);
            return;
        }

        try
        {
            VpnConnectionStatus status = await connections.ConnectAsync(
                profile.Id,
                configuration,
                RouteProtectionFor(profile));

            if (status.State == VpnConnectionState.Failed)
            {
                StatusMessage = localizer.Translate("status.profileFailed", profile.Name, status.Message);
                return;
            }

            profile.MarkConnected(timeProvider.GetUtcNow());
            await store.RecordConnectionAsync(profile.Id);
            StatusMessage = localizer.Translate("status.connecting", profile.Name);
        }
        catch (ManagementUnavailableException exception)
        {
            StatusMessage = localizer.Translate("status.profileFailed", profile.Name, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = localizer.Translate("status.profileFailed", profile.Name, exception.Message);
        }
    }

    /// <summary>
    /// The pull filters that stop a server from taking over the host routing table and DNS.
    /// </summary>
    /// <remarks>
    /// A profile may override the application wide setting, because a tunnel meant to carry all
    /// traffic needs the pushed default route while one that reaches a single network must not take
    /// the host's routing with it.
    /// </remarks>
    private string[] RouteProtectionFor(ProfileItemViewModel profile) =>
        profile.ProtectRoutes ?? settings.Current.Connections.ProtectRoutes
            ? RouteProtection
            : [];

    private static readonly string[] RouteProtection =
    [
        "--pull-filter ignore \"redirect-gateway\"",
        "--pull-filter ignore \"dhcp-option\"",
        "--pull-filter ignore \"block-outside-dns\"",
    ];

    [RelayCommand]
    private async Task DisconnectAsync(ProfileItemViewModel? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            return;
        }

        await connections.DisconnectAsync(profile.Id);
        StatusMessage = localizer.Translate("status.disconnected", profile.Name);
    }

    [RelayCommand]
    private async Task DisconnectAllAsync()
    {
        await connections.DisconnectAllAsync();
        StatusMessage = localizer["status.allStopped"];
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

        if (!profile.IsFavourite)
        {
            profile.FavouriteSlot = null;
        }

        await store.SetFavouriteAsync(profile.Id, profile.IsFavourite);
        UpdateFilterCounts();
        ApplyFilter();
    }

    /// <summary>
    /// Connects every profile filed under the selected folder that is not already up.
    /// </summary>
    [RelayCommand]
    private async Task ConnectFolderAsync()
    {
        if (SelectedFilter is not { Kind: SidebarFilterKind.Folder, FolderId: { } folderId })
        {
            return;
        }

        foreach (ProfileItemViewModel profile in allProfiles
            .Where(profile => profile.FolderId == folderId && profile.IsIdle)
            .ToList())
        {
            await ConnectAsync(profile);
        }
    }

    /// <summary>
    /// Creates a folder and files the selected profile under it in one step.
    /// </summary>
    /// <remarks>
    /// Creating an empty folder and then moving something into it is two operations for what is
    /// almost always one intent, so the selected profile follows the new folder when there is one.
    /// </remarks>
    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        string name = NewFolderName.Trim();

        if (name.Length == 0)
        {
            return;
        }

        Guid folderId = await store.CreateFolderAsync(name, parentId: null);

        if (SelectedProfile is { } profile)
        {
            await store.MoveProfileAsync(profile.Id, folderId);
        }

        NewFolderName = string.Empty;
        IsCreatingFolder = false;

        await LoadAsync();

        SelectedFilter = FolderFilters.FirstOrDefault(filter => filter.FolderId == folderId)
            ?? SelectedFilter;
    }

    [RelayCommand]
    private void BeginCreateFolder()
    {
        NewFolderName = string.Empty;
        IsCreatingFolder = true;
    }

    [RelayCommand]
    private void CancelCreateFolder()
    {
        NewFolderName = string.Empty;
        IsCreatingFolder = false;
    }

    /// <summary>
    /// Moves the selected profile into the folder that is currently selected in the sidebar, or out
    /// of any folder when a library filter is selected instead.
    /// </summary>
    [RelayCommand]
    private async Task FileSelectedProfileAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        await store.MoveProfileAsync(profile.Id, SelectedFilter?.FolderId);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteFolderAsync()
    {
        if (SelectedFilter is not { Kind: SidebarFilterKind.Folder, FolderId: { } folderId })
        {
            return;
        }

        // The profiles are only filed here, so removing the folder must not remove them.
        await store.DeleteFolderAsync(folderId);

        SelectedFilter = Filters[0];
        await LoadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void OpenSettings() => ScreenRequested?.Invoke(this, AppScreen.Settings);

    [RelayCommand]
    private void OpenHistory() => ScreenRequested?.Invoke(this, AppScreen.History);

    [RelayCommand]
    private void OpenImport() => ScreenRequested?.Invoke(this, AppScreen.Import);

    [RelayCommand]
    private void OpenQuickSwitcher() => ScreenRequested?.Invoke(this, AppScreen.QuickSwitcher);

    [RelayCommand]
    private void OpenProfileEditor()
    {
        if (SelectedProfile is not null)
        {
            ScreenRequested?.Invoke(this, AppScreen.ProfileEditor);
        }
    }

    private async Task ConnectSlotAsync(int slot)
    {
        Guid? profileId = await store.GetProfileInSlotAsync(slot);

        if (profileId is { } id)
        {
            await ConnectByIdAsync(id);
        }
    }

    private async Task ConnectLastUsedAsync()
    {
        Guid? profileId = await store.GetLastConnectedAsync();

        if (profileId is { } id)
        {
            await ConnectByIdAsync(id);
        }
    }

    private async Task ReconnectActiveAsync()
    {
        // Reconnecting the most recently started tunnel is the useful reading of "the active one"
        // when several are up, because that is the one the user was last working with.
        ProfileItemViewModel? active = allProfiles
            .Where(profile => !profile.IsIdle)
            .OrderByDescending(profile => profile.Status.ConnectedSince ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (active is null)
        {
            return;
        }

        await connections.DisconnectAsync(active.Id);
        await ConnectAsync(active);
    }

    private async Task DisconnectActiveAsync()
    {
        ProfileItemViewModel? active = allProfiles
            .Where(profile => !profile.IsIdle)
            .OrderByDescending(profile => profile.Status.ConnectedSince ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (active is not null)
        {
            await DisconnectAsync(active);
        }
    }

    /// <summary>
    /// Recomputes the aggregate throughput across the tunnels that are up.
    /// </summary>
    private void UpdateTotals()
    {
        long received = 0;
        long sent = 0;

        foreach (ProfileItemViewModel profile in allProfiles)
        {
            if (profile.IsConnected)
            {
                received += profile.Status.BytesReceived;
                sent += profile.Status.BytesSent;
            }
        }

        TotalBytesReceived = received;
        TotalBytesSent = sent;
    }

    /// <summary>
    /// What the status bar says when nothing has just happened.
    /// </summary>
    private string RestingStatusMessage => allProfiles.Count == 0
        ? localizer["status.libraryEmpty"]
        : localizer.Translate("status.profileCount", allProfiles.Count);

    private void TickUptime()
    {
        foreach (ProfileItemViewModel profile in allProfiles)
        {
            profile.RefreshUptime();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        foreach (SidebarFilterViewModel filter in Filters)
        {
            filter.RefreshLocalizedText();
        }

        foreach (ProfileItemViewModel profile in allProfiles)
        {
            profile.RefreshLocalizedText();
        }

        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDetail));
        OnPropertyChanged(nameof(ActiveSummary));

        // The status bar holds a sentence rather than a key, so it cannot re-translate itself.
        // Whatever it was reporting has been read by now, and the resting text is correct again.
        StatusMessage = RestingStatusMessage;
    });

    private string DescribeTransition(ProfileItemViewModel profile, VpnConnectionStatus status) =>
        status.State switch
        {
            VpnConnectionState.Connected => localizer.Translate("status.profileConnected", profile.Name),
            VpnConnectionState.Reconnecting =>
                localizer.Translate("status.profileReconnecting", profile.Name, status.Message).TrimEnd(),
            VpnConnectionState.Failed =>
                localizer.Translate("status.profileFailed", profile.Name, status.Message),
            VpnConnectionState.Disconnected =>
                localizer.Translate("status.profileDisconnected", profile.Name),
            _ => localizer.Translate("status.profileBusy", profile.Name, profile.StatusLabel),
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
            UpdateTotals();

            UpdateFilterCounts();

            if (SelectedFilter?.Kind == SidebarFilterKind.Active)
            {
                ApplyFilter();
            }
        });
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        uptimeTimer.IsEnabled = false;
        connections.StatusChanged -= OnConnectionStatusChanged;
        localizer.LanguageChanged -= OnLanguageChanged;
    }
}

/// <summary>
/// The screens the main window can open on the view model's behalf.
/// </summary>
public enum AppScreen
{
    MainWindow,
    QuickSwitcher,
    Settings,
    History,
    Import,
    ProfileEditor,
}
