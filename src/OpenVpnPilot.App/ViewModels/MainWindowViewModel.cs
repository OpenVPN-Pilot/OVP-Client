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
    /// <summary>
    /// Where OpenVPN Community is obtained. Named in the interface because a user told that
    /// something is missing and not told where to get it has been given half an answer.
    /// </summary>
    public const string OpenVpnDownloadUrl = "https://openvpn.net/community-downloads/";

    private readonly IProfileStore store;
    private readonly ConnectionManager connections;
    private readonly ProfileNameCache nameCache;
    private readonly ISettingsService settings;
    private readonly ISecretStore secrets;
    private readonly EnvironmentGate environment;
    private readonly ILocalizer localizer;
    private readonly TimeProvider timeProvider;

    private readonly List<ProfileItemViewModel> allProfiles = [];
    private readonly Dictionary<Guid, ProfileItemViewModel> byId = [];
    private readonly DispatcherTimer uptimeTimer;
    private bool disposed;

    public MainWindowViewModel(
        IProfileStore store,
        ConnectionManager connections,
        ProfileNameCache nameCache,
        ISettingsService settings,
        ISecretStore secrets,
        EnvironmentGate environment,
        ILocalizer localizer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(nameCache);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.store = store;
        this.connections = connections;
        this.nameCache = nameCache;
        this.settings = settings;
        this.secrets = secrets;
        this.environment = environment;
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
            SidebarFilterViewModel.ForBuiltIn(SidebarFilterKind.New, "nav.new", localizer),
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

    public ObservableCollection<SidebarFilterViewModel> TagFilters { get; } = [];

    public bool HasTags => TagFilters.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ProfileItemViewModel? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string SearchTerm { get; set; } = string.Empty;

    /// <summary>
    /// True while every row offers a checkbox and the actions apply to what is ticked.
    /// </summary>
    /// <remarks>
    /// Off by default. Connecting twenty profiles at once is worth having and worth asking for; a
    /// list that is always in a mode where a click might mean something else is not.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    public partial bool IsSelecting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    [NotifyPropertyChangedFor(nameof(HasTicked))]
    public partial int TickedCount { get; set; }

    /// <summary>
    /// Shown after asking to delete, so that removing twenty profiles takes two decisions.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; set; }

    /// <summary>
    /// True when at least one row is ticked, which is what the selection actions need.
    /// </summary>
    public bool HasTicked => TickedCount > 0;

    public string SelectionSummary => localizer.Translate("select.summary", TickedCount);

    public string DeleteConfirmation => localizer.Translate("select.confirmDelete", TickedCount);

    /// <summary>
    /// The filter the list is showing.
    /// </summary>
    /// <remarks>
    /// The sidebar is two lists, the built in entries and the tags, and only one selection exists
    /// between them. Binding both lists to this directly looked right and was not: choosing a tag
    /// left the built in list holding a selected item it did not contain, and it answered by writing
    /// its own selection back. Both entries then looked chosen and the filter was neither.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewSelected))]
    public partial SidebarFilterViewModel? SelectedFilter { get; set; }

    /// <summary>
    /// The selection of the built in list. Null while a tag is chosen.
    /// </summary>
    [ObservableProperty]
    public partial SidebarFilterViewModel? SelectedBuiltIn { get; set; }

    /// <summary>
    /// The selection of the tag list. Null while a built in entry is chosen.
    /// </summary>
    [ObservableProperty]
    public partial SidebarFilterViewModel? SelectedTag { get; set; }

    /// <summary>
    /// Set while one list is being cleared because the other was chosen from.
    /// </summary>
    private bool movingSelection;

    /// <summary>
    /// True while the new entry is selected, which is when marking everything seen makes sense.
    /// </summary>
    public bool IsNewSelected => SelectedFilter?.Kind == SidebarFilterKind.New;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>
    /// True while something in the environment prevents any tunnel from being started.
    /// </summary>
    /// <remarks>
    /// Shown as a banner rather than raised when a connection is attempted. The user should know
    /// before they pick a profile, and the client should not look perfectly healthy right up to the
    /// moment it cannot do the one thing it exists for.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsEnvironmentBlocked { get; set; }

    /// <summary>
    /// What is missing, one plainly worded line per failed check.
    /// </summary>
    [ObservableProperty]
    public partial string EnvironmentProblems { get; set; } = string.Empty;

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
            IReadOnlyList<TagSummary> tags = await store.GetTagsAsync(cancellationToken);
            IReadOnlyDictionary<Guid, IReadOnlyList<string>> profileTags =
                await store.GetProfileTagsAsync(cancellationToken);

            foreach (ProfileItemViewModel existing in allProfiles)
            {
                existing.PropertyChanged -= OnProfilePropertyChanged;
            }

            allProfiles.Clear();
            byId.Clear();

            foreach (Profile profile in profiles)
            {
                profileTags.TryGetValue(profile.Id, out IReadOnlyList<string>? assigned);

                ProfileItemViewModel item = new(profile, localizer, assigned)
                {
                    Status = connections.GetStatus(profile.Id),
                    IsSelecting = IsSelecting,
                };

                item.PropertyChanged += OnProfilePropertyChanged;

                allProfiles.Add(item);
                byId[profile.Id] = item;
            }

            // The credential prompt and the notifications read names from here, so it stays in step.
            nameCache.Replace(allProfiles.Select(
                profile => new KeyValuePair<Guid, string>(profile.Id, profile.Name)));

            RebuildSidebar(tags);

            if (SelectedFilter is null)
            {
                SelectedBuiltIn = Filters[0];
            }
            UpdateFilterCounts();
            ApplyFilter();

            StatusMessage = RestingStatusMessage;

            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateDetail));

            ActiveCount = connections.ActiveCount;
            RecountTicked();
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

            case HotkeyActions.ToggleQuickDisconnect:
                ScreenRequested?.Invoke(this, AppScreen.QuickDisconnect);
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

    /// <summary>
    /// Connects several profiles by identifier, one after another.
    /// </summary>
    public async Task ConnectByIdAsync(IEnumerable<Guid> profileIds)
    {
        ArgumentNullException.ThrowIfNull(profileIds);

        foreach (Guid profileId in profileIds)
        {
            await ConnectByIdAsync(profileId);
        }
    }

    /// <summary>
    /// Adds a tag to the given profiles, which is what dropping one on a tag means.
    /// </summary>
    /// <remarks>
    /// Adds rather than replaces. A profile carries as many tags as it needs, and a gesture that
    /// silently dropped the others would be a poor way to find that out. A profile that already has
    /// the tag is left alone, so dropping the same one twice is not an error.
    /// </remarks>
    public async Task AssignTagAsync(IReadOnlyList<Guid> profileIds, string tagName)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);

        int changed = 0;

        foreach (Guid profileId in profileIds)
        {
            if (!byId.TryGetValue(profileId, out ProfileItemViewModel? profile)
                || profile.Tags.Contains(tagName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            await store.SetProfileTagsAsync(profileId, [.. profile.Tags, tagName]);
            changed++;
        }

        if (changed == 0)
        {
            StatusMessage = localizer.Translate("tags.alreadyThere", tagName);
            return;
        }

        await LoadAsync();

        StatusMessage = localizer.Translate("tags.applied", tagName, changed);
    }

    /// <summary>
    /// The profiles a drag starting on one row should carry.
    /// </summary>
    /// <remarks>
    /// Dragging a row that is ticked takes everything ticked with it, which is what someone who has
    /// just ticked twenty of them expects. Dragging anything else takes only that row.
    /// </remarks>
    public IReadOnlyList<Guid> ProfilesToDrag(Guid startedOn)
    {
        if (IsSelecting
            && byId.TryGetValue(startedOn, out ProfileItemViewModel? profile)
            && profile.IsSelected)
        {
            return allProfiles.Where(item => item.IsSelected).Select(item => item.Id).ToList();
        }

        return [startedOn];
    }

    /// <summary>
    /// Stops several profiles by identifier, which is what the disconnect palette asks for.
    /// </summary>
    public async Task DisconnectByIdAsync(IEnumerable<Guid> profileIds)
    {
        ArgumentNullException.ThrowIfNull(profileIds);

        foreach (Guid profileId in profileIds)
        {
            if (byId.TryGetValue(profileId, out ProfileItemViewModel? profile))
            {
                await DisconnectAsync(profile);
            }
        }
    }

    public void SelectProfile(Guid profileId)
    {
        if (byId.TryGetValue(profileId, out ProfileItemViewModel? profile))
        {
            SelectedBuiltIn = Filters[0];
            SearchTerm = string.Empty;
            SelectedProfile = VisibleProfiles.FirstOrDefault(item => item.Id == profileId) ?? profile;
        }
    }

    partial void OnSearchTermChanged(string value) => ApplyFilter();

    partial void OnIsSelectingChanged(bool value)
    {
        IsConfirmingDelete = false;

        foreach (ProfileItemViewModel profile in allProfiles)
        {
            profile.IsSelecting = value;

            if (!value)
            {
                profile.IsSelected = false;
            }
        }

        RecountTicked();
    }

    private void RecountTicked()
    {
        TickedCount = allProfiles.Count(profile => profile.IsSelected);

        if (TickedCount == 0)
        {
            IsConfirmingDelete = false;
        }
    }

    /// <summary>
    /// The ticked profiles, or the one selected when nothing is ticked.
    /// </summary>
    private List<ProfileItemViewModel> Chosen =>
        IsSelecting && TickedCount > 0
            ? allProfiles.Where(profile => profile.IsSelected).ToList()
            : SelectedProfile is { } single ? [single] : [];

    [RelayCommand]
    private void ToggleSelecting() => IsSelecting = !IsSelecting;

    [RelayCommand]
    private void TickAllShown()
    {
        foreach (ProfileItemViewModel profile in VisibleProfiles)
        {
            profile.IsSelected = true;
        }

        RecountTicked();
    }

    [RelayCommand]
    private void TickNone()
    {
        foreach (ProfileItemViewModel profile in allProfiles)
        {
            profile.IsSelected = false;
        }

        RecountTicked();
    }

    /// <summary>
    /// Connects everything that is ticked, one after another.
    /// </summary>
    /// <remarks>
    /// In order rather than all at once. Each tunnel is a process, an adapter and a port, and
    /// twenty starting in the same instant is how a machine runs out of all three.
    /// </remarks>
    [RelayCommand]
    private async Task ConnectTickedAsync()
    {
        foreach (ProfileItemViewModel profile in Chosen.Where(profile => profile.IsIdle))
        {
            await ConnectAsync(profile);
        }
    }

    [RelayCommand]
    private async Task DisconnectTickedAsync()
    {
        foreach (ProfileItemViewModel profile in Chosen.Where(profile => !profile.IsIdle))
        {
            await DisconnectAsync(profile);
        }
    }

    [RelayCommand]
    private void AskToDeleteTicked() => IsConfirmingDelete = TickedCount > 0;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    /// <summary>
    /// Removes the ticked profiles from the store, stopping any that are running first.
    /// </summary>
    [RelayCommand]
    private async Task DeleteTickedAsync()
    {
        List<ProfileItemViewModel> chosen = Chosen;
        IsConfirmingDelete = false;

        foreach (ProfileItemViewModel profile in chosen)
        {
            if (!profile.IsIdle)
            {
                await DisconnectAsync(profile);
            }

            await store.DeleteProfileAsync(profile.Id);
        }

        await LoadAsync();

        StatusMessage = localizer.Translate("select.deleted", chosen.Count);
    }

    partial void OnSelectedFilterChanged(SidebarFilterViewModel? value) => ApplyFilter();

    partial void OnSelectedBuiltInChanged(SidebarFilterViewModel? value) => Choose(value, clearTag: true);

    partial void OnSelectedTagChanged(SidebarFilterViewModel? value) => Choose(value, clearTag: false);

    /// <summary>
    /// Takes the choice from whichever list it was made in and clears the other.
    /// </summary>
    /// <remarks>
    /// A list handed a selected item it does not contain clears its own selection and reports that,
    /// which arrives here as null. That is the other list letting go, not the user choosing nothing,
    /// so it must not become the filter.
    /// </remarks>
    private void Choose(SidebarFilterViewModel? value, bool clearTag)
    {
        if (movingSelection || value is null)
        {
            return;
        }

        movingSelection = true;

        try
        {
            if (clearTag)
            {
                SelectedTag = null;
            }
            else
            {
                SelectedBuiltIn = null;
            }
        }
        finally
        {
            movingSelection = false;
        }

        SelectedFilter = value;
    }

    private void RebuildSidebar(IReadOnlyList<TagSummary> tags)
    {
        string? selectedTag = SelectedFilter?.TagName;

        TagFilters.Clear();
        foreach (TagSummary tag in tags)
        {
            TagFilters.Add(SidebarFilterViewModel.ForTag(tag.Name));
        }

        // A rebuild replaces the instances, so a selection has to be re-established by identity.
        if (selectedTag is not null)
        {
            SidebarFilterViewModel? again =
                TagFilters.FirstOrDefault(filter => filter.TagName == selectedTag);

            if (again is not null)
            {
                SelectedTag = again;
            }
            else
            {
                SelectedBuiltIn = Filters[0];
            }
        }

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
                SidebarFilterKind.New => allProfiles.Count(profile => profile.IsNew),
                _ => 0,
            };
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
            SidebarFilterKind.New => matches
                .Where(profile => profile.IsNew)
                .OrderByDescending(profile => profile.DiscoveredAt),
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

    /// <summary>
    /// Starts one tunnel.
    /// </summary>
    /// <remarks>
    /// Two things here are about not ending the process. The environment is checked first, so a
    /// machine without the interactive service is told what is missing instead of being asked to
    /// open a pipe that is not there. And everything else is caught, because anything that escapes
    /// an asynchronous command is rethrown on the user interface thread and takes the application
    /// with it, tunnels and all. Launching talks to a service, a pipe, a socket and the file system;
    /// the exceptions those produce cannot be enumerated in advance, and none of them is worth
    /// losing every running tunnel over.
    /// </remarks>
    [RelayCommand]
    private async Task ConnectAsync(ProfileItemViewModel? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null || !profile.IsIdle)
        {
            return;
        }

        if (!await EnvironmentAllowsConnectingAsync())
        {
            StatusMessage = localizer["environment.cannotConnect"];
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
                RouteProtectionFor(profile),
                ConnectTimeout,
                settings.Current.Advanced.OpenVpnVerbosity);

            if (status.State == VpnConnectionState.Failed)
            {
                StatusMessage = localizer.Translate("status.profileFailed", profile.Name, status.Message);
                return;
            }

            profile.MarkConnected(timeProvider.GetUtcNow());
            await store.RecordConnectionAsync(profile.Id);
            StatusMessage = localizer.Translate("status.connecting", profile.Name);
        }
        catch (Exception exception)
        {
            StatusMessage = localizer.Translate("status.profileFailed", profile.Name, exception.Message);

            // Re-examined rather than assumed: the most likely reason a launch failed outright is
            // that something the environment needs went away, and the banner should say so.
            await RefreshEnvironmentAsync();
        }
    }

    /// <summary>
    /// Examines the environment and updates the banner.
    /// </summary>
    public async Task RefreshEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        await environment.RefreshAsync(cancellationToken);
        ShowEnvironment();
    }

    /// <summary>
    /// Re-checks immediately before a connection, so a service stopped since startup is caught.
    /// </summary>
    private async Task<bool> EnvironmentAllowsConnectingAsync()
    {
        await environment.EnsureExaminedAsync();
        ShowEnvironment();
        return !IsEnvironmentBlocked;
    }

    private void ShowEnvironment()
    {
        IsEnvironmentBlocked = environment.IsBlocked;
        EnvironmentProblems = string.Join(
            Environment.NewLine,
            environment.Blocking.Select(check =>
                localizer.Translate("environment." + Key(check.Id), check.Detail)));
    }

    /// <summary>
    /// The localization key suffix for one check, which is its name with a lower case first letter.
    /// </summary>
    private static string Key(EnvironmentCheckId id)
    {
        string name = id.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    [RelayCommand]
    private async Task RecheckEnvironmentAsync()
    {
        await RefreshEnvironmentAsync();

        if (!IsEnvironmentBlocked)
        {
            StatusMessage = localizer["environment.ready"];
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
    /// <summary>
    /// How long a tunnel is given to come up, or null when it may take as long as it likes.
    /// </summary>
    private TimeSpan? ConnectTimeout => settings.Current.Connections.ConnectTimeoutSeconds > 0
        ? TimeSpan.FromSeconds(settings.Current.Connections.ConnectTimeoutSeconds)
        : null;

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

    /// <summary>
    /// Stops one tunnel.
    /// </summary>
    /// <remarks>
    /// Anything that escapes a command is rethrown on the user interface thread and ends the
    /// process. Stopping a tunnel talks to a socket and to a process that may already be gone, which
    /// is the least surprising place for that to happen, and a failure to stop something is never
    /// worth losing every other tunnel over.
    /// </remarks>
    [RelayCommand]
    private async Task DisconnectAsync(ProfileItemViewModel? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            return;
        }

        try
        {
            await connections.DisconnectAsync(profile.Id);
            StatusMessage = localizer.Translate("status.disconnected", profile.Name);
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            StatusMessage = localizer.Translate("status.disconnectFailed", profile.Name, exception.Message);
        }
    }

    [RelayCommand]
    private async Task DisconnectAllAsync()
    {
        try
        {
            await connections.DisconnectAllAsync();
            StatusMessage = localizer["status.allStopped"];
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            StatusMessage = localizer.Translate("status.disconnectAllFailed", exception.Message);
        }
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
    /// Connects everything the current filter shows that is not already up.
    /// </summary>
    /// <remarks>
    /// Acts on what is on screen rather than on a saved grouping, so a tag, a search term or both
    /// together decide the set. That is the same thing the user is already looking at.
    /// </remarks>
    [RelayCommand]
    private async Task ConnectVisibleAsync()
    {
        foreach (ProfileItemViewModel profile in VisibleProfiles.Where(profile => profile.IsIdle).ToList())
        {
            await ConnectAsync(profile);
        }
    }

    /// <summary>
    /// Clears the new marks, which is how the user says they have looked at what arrived.
    /// </summary>
    [RelayCommand]
    private async Task MarkDiscoveriesSeenAsync()
    {
        int cleared = await store.ClearDiscoveriesAsync();

        if (cleared == 0)
        {
            return;
        }

        SelectedBuiltIn = Filters[0];
        await LoadAsync();

        StatusMessage = localizer.Translate("status.discoveriesCleared", cleared);
    }

    /// <summary>
    /// Forgets what is stored for a profile, so the next connection asks again.
    /// </summary>
    /// <remarks>
    /// A stored credential is used without asking, which is the point of storing it. This is the way
    /// back when the password changed on the server side and the user knows it before a refusal
    /// does: without it they would have to fail a connection first to be offered the prompt.
    /// </remarks>
    [RelayCommand]
    private async Task SignInAgainAsync(ProfileItemViewModel? profile)
    {
        profile ??= SelectedProfile;

        if (profile is null || !secrets.IsAvailable)
        {
            return;
        }

        int removed = 0;

        foreach (string reference in await secrets.ListAsync())
        {
            if (SecretReference.BelongsToProfile(reference, profile.Id))
            {
                await secrets.DeleteAsync(reference);
                removed++;
            }
        }

        StatusMessage = removed == 0
            ? localizer.Translate("status.noStoredCredentials", profile.Name)
            : localizer.Translate("status.credentialsForgotten", profile.Name);
    }

    [RelayCommand]
    private void OpenSettings() => ScreenRequested?.Invoke(this, AppScreen.Settings);

    [RelayCommand]
    private void OpenHistory() => ScreenRequested?.Invoke(this, AppScreen.History);

    [RelayCommand]
    private void OpenLog() => ScreenRequested?.Invoke(this, AppScreen.Log);

    [RelayCommand]
    private void OpenImport() => ScreenRequested?.Invoke(this, AppScreen.Import);

    [RelayCommand]
    private void OpenExport() => ScreenRequested?.Invoke(this, AppScreen.Export);

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

    private void OnProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileItemViewModel.IsSelected))
        {
            RecountTicked();
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

    /// <summary>
    /// The same palette, listing only what is running, for stopping it.
    /// </summary>
    QuickDisconnect,

    Settings,
    History,

    /// <summary>
    /// The live log, showing what the application and OpenVPN both recorded.
    /// </summary>
    Log,

    Import,
    Export,
    ProfileEditor,
}
