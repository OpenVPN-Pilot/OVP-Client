using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Updates;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The settings screen, editing a copy so nothing takes effect until it is saved.
/// </summary>
/// <remarks>
/// Everything the application used to decide in code lives here: the language, the theme, whether
/// pushed routes are ignored, what is announced, whether credentials are remembered, and where
/// OpenVPN is. The form edits a clone, so closing without saving leaves the running application
/// exactly as it was.
/// </remarks>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsService settings;
    private readonly ILocalizer localizer;
    private readonly LanguageCoordinator languages;
    private readonly IHotkeyStore hotkeyStore;
    private readonly HotkeyCoordinator hotkeys;
    private readonly ISecretStore secrets;
    private readonly IAutoStartManager autoStart;
    private readonly ISessionStore sessions;
    private readonly DiagnosticsBundle diagnostics;
    private readonly UpdateCoordinator updates;

    /// <summary>
    /// The platform's list of running applications, on a platform that keeps one apart from windows.
    /// Null where there is nothing to decide.
    /// </summary>
    private readonly IDockPresence? dock;

    /// <summary>
    /// The server this copy works with. Null on the local library, where there is none.
    /// </summary>
    private readonly IServerSignOut? server;

    /// <summary>
    /// What else forgetting every sign in means for a server's copy. Null on the local library.
    /// </summary>
    private readonly IServerCredentialsReset? credentialsReset;

    /// <summary>
    /// Whether importing is offered. Null means the local library, where it always is.
    /// </summary>
    private readonly ILibraryPermissions? permissions;

    private readonly IUserInterfaceThread? ui;

    private PilotSettings draft;

    public SettingsViewModel(
        ISettingsService settings,
        ILocalizer localizer,
        LanguageCoordinator languages,
        IHotkeyStore hotkeyStore,
        HotkeyCoordinator hotkeys,
        ISecretStore secrets,
        IAutoStartManager autoStart,
        ISessionStore sessions,
        DiagnosticsBundle diagnostics,
        UpdateCoordinator updates,
        IDockPresence? dock = null,
        IServerSignOut? server = null,
        StorageSettingsViewModel? storage = null,
        IServerCredentialsReset? credentialsReset = null,
        ILibraryPermissions? permissions = null,
        IUserInterfaceThread? ui = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(hotkeyStore);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(autoStart);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(updates);

        this.settings = settings;
        this.localizer = localizer;
        this.languages = languages;
        this.hotkeyStore = hotkeyStore;
        this.hotkeys = hotkeys;
        this.secrets = secrets;
        this.autoStart = autoStart;
        this.sessions = sessions;
        this.diagnostics = diagnostics;
        this.updates = updates;
        this.dock = dock;
        this.server = server;
        this.credentialsReset = credentialsReset;
        this.permissions = permissions;
        this.ui = ui;
        Storage = storage;

        if (permissions is not null)
        {
            permissions.Changed += OnPermissionsChanged;
        }

        if (storage is not null)
        {
            storage.ScreenRequested += (_, screen) => ScreenRequested?.Invoke(this, screen);
        }

        draft = settings.Current.Clone();

        Languages = [new LanguageChoice(null, localizer["settings.followSystem"])];

        foreach (LanguageDescriptor language in localizer.AvailableLanguages)
        {
            Languages.Add(new LanguageChoice(language.Code, language.NativeName));
        }

        Themes =
        [
            new ThemeChoice(ThemePreference.System, localizer["settings.themeSystem"]),
            new ThemeChoice(ThemePreference.Light, localizer["settings.themeLight"]),
            new ThemeChoice(ThemePreference.Dark, localizer["settings.themeDark"]),
        ];

        EditorViews =
        [
            new EditorViewChoice(ProfileEditorView.Form, localizer["settings.profileEditorForm"]),
            new EditorViewChoice(ProfileEditorView.PlainText, localizer["settings.profileEditorPlain"]),
        ];

        ReadFromDraft();
    }

    /// <summary>
    /// Raised when the screen is finished with, with true when the settings were saved.
    /// </summary>
    public event EventHandler<bool>? Closed;

    /// <summary>
    /// Raised when a screen reached from here has to be opened, such as the import wizard.
    /// </summary>
    /// <remarks>
    /// The settings screen does not own the other windows any more than the main window does, so it
    /// asks for one in the same way and whoever coordinates the windows decides what that means.
    /// </remarks>
    public event EventHandler<AppScreen>? ScreenRequested;

    /// <summary>
    /// Raised when the profile list should be read from the store again.
    /// </summary>
    public event EventHandler? ProfileReloadRequested;

    /// <summary>
    /// Raised when the settings are to show one page, wherever it stands among the tabs.
    /// </summary>
    public event EventHandler<SettingsPage>? PageRequested;

    /// <summary>
    /// The storage page: where the profiles live, and switching that. Null where nothing composed it.
    /// </summary>
    public StorageSettingsViewModel? Storage { get; }

    public bool HasStorage => Storage is not null;

    /// <summary>
    /// Shows one page, for a screen elsewhere that opens the settings at it.
    /// </summary>
    public void ShowPage(SettingsPage page) => PageRequested?.Invoke(this, page);

    public ObservableCollection<LanguageChoice> Languages { get; }

    public ObservableCollection<ThemeChoice> Themes { get; }

    public ObservableCollection<EditorViewChoice> EditorViews { get; }

    /// <summary>
    /// What the profile editor shows when it opens.
    /// </summary>
    [ObservableProperty]
    public partial EditorViewChoice? SelectedEditorView { get; set; }

    public ObservableCollection<HotkeyEditorViewModel> Hotkeys { get; } = [];

    [ObservableProperty]
    public partial LanguageChoice? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial ThemeChoice? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial bool StartMinimised { get; set; }

    [ObservableProperty]
    public partial bool StartWithSystem { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool ShowInDock { get; set; }

    /// <summary>
    /// Whether the notification area icon is coloured by the application's state.
    /// </summary>
    [ObservableProperty]
    public partial bool TrayShowStatus { get; set; }

    /// <summary>
    /// One row per state, in the order the icon gives them priority.
    /// </summary>
    public ObservableCollection<TrayColourChoice> TrayColours { get; } = [];

    [ObservableProperty]
    public partial bool ProtectRoutes { get; set; }

    [ObservableProperty]
    public partial int ConnectTimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial bool AutoReconnect { get; set; }

    [ObservableProperty]
    public partial int MaxReconnectAttempts { get; set; }

    [ObservableProperty]
    public partial int ReconnectDelaySeconds { get; set; }

    [ObservableProperty]
    public partial bool RestoreOnStart { get; set; }

    /// <summary>
    /// How often, in minutes, the server is asked for changes. Offered only with a server.
    /// </summary>
    [ObservableProperty]
    public partial int SyncIntervalMinutes { get; set; }

    [ObservableProperty]
    public partial bool NotificationsEnabled { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnConnecting { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnConnected { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnDisconnected { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnConnectionLost { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnReconnecting { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnFailed { get; set; }

    [ObservableProperty]
    public partial bool UseStoredSecrets { get; set; }

    [ObservableProperty]
    public partial bool RememberByDefault { get; set; }

    [ObservableProperty]
    public partial string OpenVpnPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int OpenVpnVerbosity { get; set; }

    [ObservableProperty]
    public partial string LogLevel { get; set; } = "Information";

    /// <summary>
    /// How many days of log files are kept. Zero keeps them until somebody removes them.
    /// </summary>
    [ObservableProperty]
    public partial int LogRetentionDays { get; set; }

    /// <summary>
    /// How large the log files may be together, in megabytes. Zero sets no limit.
    /// </summary>
    [ObservableProperty]
    public partial int LogMaximumMegabytes { get; set; }

    /// <summary>
    /// How large one day's log file may grow before only warnings and errors are written to it.
    /// </summary>
    [ObservableProperty]
    public partial int LogDailyMegabytes { get; set; }

    /// <summary>
    /// Ask GitHub for a newer release when the application starts.
    /// </summary>
    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    /// <summary>
    /// The repository the check asks about, in the form owner/name.
    /// </summary>
    [ObservableProperty]
    public partial string UpdateRepository { get; set; } = string.Empty;

    /// <summary>
    /// What the last check found. Every outcome is reported here, including being up to date,
    /// because somebody pressed a button and is waiting for an answer.
    /// </summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int StoredSecretCount { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// False when the platform offers no protected storage, in which case remembering is not offered.
    /// </summary>
    public bool SecretsAvailable => secrets.IsAvailable;

    /// <summary>
    /// True on a server's copy, where forgetting leaves this computer's library and other servers alone.
    /// </summary>
    public bool ForgetsThisServerOnly => credentialsReset is not null;

    public bool AutoStartAvailable => autoStart.IsSupported;

    /// <summary>
    /// False where the list of running applications follows the windows, which leaves nothing to
    /// choose.
    /// </summary>
    public bool DockAvailable => dock is not null;

    public bool HotkeysAvailable => hotkeys.IsAvailable;

    public IReadOnlyList<string> LogLevels { get; } =
        ["Verbose", "Debug", "Information", "Warning", "Error"];

    /// <summary>
    /// Reads the stored shortcuts and the current registration problems.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HotkeyBindingRecord> bindings = await hotkeyStore.GetBindingsAsync(cancellationToken);
        Dictionary<string, string> byAction = bindings.ToDictionary(
            binding => binding.ActionId,
            binding => binding.Gesture,
            StringComparer.Ordinal);

        IReadOnlyList<HotkeyRegistration> registrations = hotkeys.Registrations;

        Hotkeys.Clear();
        foreach (string actionId in HotkeyActions.All)
        {
            byAction.TryGetValue(actionId, out string? gesture);

            HotkeyEditorViewModel editor = new(actionId, gesture, localizer);

            HotkeyRegistration? failure = registrations
                .FirstOrDefault(registration =>
                    registration.ActionId == actionId && !registration.Succeeded);

            if (failure is not null)
            {
                editor.Problem = failure.Detail;
            }

            Hotkeys.Add(editor);
        }

        // Profile sign ins only: a server's refresh token shares the store but is not one of them. On
        // a server's copy, only its own profiles' are counted, because only those would be forgotten.
        StoredSecretCount = credentialsReset is not null
            ? await credentialsReset.CountAsync(cancellationToken)
            : (await secrets.ListAsync(cancellationToken)).Count(SecretReference.IsProfileReference);

        // The registry is the truth for autostart, not the settings file, because the entry can be
        // removed from outside the application.
        StartWithSystem = autoStart.IsSupported && autoStart.IsEnabled();

        Storage?.Refresh();
    }

    private void ReadFromDraft()
    {
        SelectedLanguage = Languages.FirstOrDefault(language => language.Code == draft.General.Language)
            ?? Languages[0];

        SelectedTheme = Themes.FirstOrDefault(theme => theme.Preference == draft.Appearance.Theme)
            ?? Themes[0];

        StartMinimised = draft.General.StartMinimised;
        StartWithSystem = draft.General.StartWithSystem;
        CloseToTray = draft.General.CloseToTray;
        ShowInDock = draft.General.ShowInDock;

        TrayShowStatus = draft.Appearance.TrayIcon.ShowStatus;
        TrayColours.Clear();

        IReadOnlyList<TrayColourModeChoice> modes =
        [
            new TrayColourModeChoice(TrayColourMode.Colour, localizer["settings.trayIconColour"]),
            new TrayColourModeChoice(TrayColourMode.Original, localizer["settings.trayIconOriginal"]),
            new TrayColourModeChoice(TrayColourMode.Off, localizer["settings.trayIconOff"]),
        ];

        foreach (TrayStatus status in TrayColourChoice.Order)
        {
            TrayColours.Add(new TrayColourChoice(
                status,
                localizer[TrayStatusNames.Key(status)],
                modes,
                draft.Appearance.TrayIcon.Get(status)));
        }

        SelectedEditorView = EditorViews.FirstOrDefault(view => view.View == draft.General.ProfileEditor)
            ?? EditorViews[0];

        ProtectRoutes = draft.Connections.ProtectRoutes;
        ConnectTimeoutSeconds = draft.Connections.ConnectTimeoutSeconds;
        AutoReconnect = draft.Connections.AutoReconnect;
        MaxReconnectAttempts = draft.Connections.MaxReconnectAttempts;
        ReconnectDelaySeconds = draft.Connections.ReconnectDelaySeconds;
        RestoreOnStart = draft.Connections.RestoreOnStart;

        SyncIntervalMinutes = draft.Storage.SyncIntervalMinutes;

        NotificationsEnabled = draft.Notifications.Enabled;
        NotifyOnConnecting = draft.Notifications.OnConnecting;
        NotifyOnConnected = draft.Notifications.OnConnected;
        NotifyOnDisconnected = draft.Notifications.OnDisconnected;
        NotifyOnConnectionLost = draft.Notifications.OnConnectionLost;
        NotifyOnReconnecting = draft.Notifications.OnReconnecting;
        NotifyOnFailed = draft.Notifications.OnFailed;

        UseStoredSecrets = draft.Credentials.UseStoredSecrets;
        RememberByDefault = draft.Credentials.RememberByDefault;

        OpenVpnPath = draft.Advanced.OpenVpnPath ?? string.Empty;
        OpenVpnVerbosity = draft.Advanced.OpenVpnVerbosity;
        LogLevel = draft.Advanced.LogLevel;
        LogRetentionDays = draft.Advanced.LogRetentionDays;
        LogMaximumMegabytes = draft.Advanced.LogMaximumMegabytes;
        LogDailyMegabytes = draft.Advanced.LogDailyMegabytes;
        CheckForUpdates = draft.Advanced.CheckForUpdates;
        UpdateRepository = draft.Advanced.UpdateRepository ?? string.Empty;
    }

    private void WriteToDraft()
    {
        draft.General.Language = SelectedLanguage?.Code;
        draft.General.StartMinimised = StartMinimised;
        draft.General.StartWithSystem = StartWithSystem;
        draft.General.CloseToTray = CloseToTray;
        draft.General.ShowInDock = ShowInDock;
        draft.General.ProfileEditor = SelectedEditorView?.View ?? ProfileEditorView.Form;

        draft.Appearance.Theme = SelectedTheme?.Preference ?? ThemePreference.System;
        draft.Appearance.TrayIcon.ShowStatus = TrayShowStatus;

        foreach (TrayColourChoice choice in TrayColours)
        {
            draft.Appearance.TrayIcon.Set(choice.Status, choice.Stored);
        }

        draft.Connections.ProtectRoutes = ProtectRoutes;
        draft.Connections.ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 5, 600);
        draft.Connections.AutoReconnect = AutoReconnect;
        draft.Connections.MaxReconnectAttempts = Math.Clamp(MaxReconnectAttempts, 0, 100);
        draft.Connections.ReconnectDelaySeconds = Math.Clamp(ReconnectDelaySeconds, 1, 600);
        draft.Connections.RestoreOnStart = RestoreOnStart;

        draft.Storage.SyncIntervalMinutes = Math.Clamp(
            SyncIntervalMinutes,
            StorageSettings.MinimumSyncIntervalMinutes,
            StorageSettings.MaximumSyncIntervalMinutes);

        draft.Notifications.Enabled = NotificationsEnabled;
        draft.Notifications.OnConnecting = NotifyOnConnecting;
        draft.Notifications.OnConnected = NotifyOnConnected;
        draft.Notifications.OnDisconnected = NotifyOnDisconnected;
        draft.Notifications.OnConnectionLost = NotifyOnConnectionLost;
        draft.Notifications.OnReconnecting = NotifyOnReconnecting;
        draft.Notifications.OnFailed = NotifyOnFailed;

        draft.Credentials.UseStoredSecrets = UseStoredSecrets;
        draft.Credentials.RememberByDefault = RememberByDefault;

        draft.Advanced.OpenVpnPath = string.IsNullOrWhiteSpace(OpenVpnPath) ? null : OpenVpnPath.Trim();
        draft.Advanced.OpenVpnVerbosity = Math.Clamp(OpenVpnVerbosity, 0, 11);
        draft.Advanced.LogLevel = LogLevel;
        draft.Advanced.LogRetentionDays = Math.Clamp(LogRetentionDays, 0, 365);
        draft.Advanced.LogMaximumMegabytes = Math.Clamp(LogMaximumMegabytes, 0, 100_000);
        draft.Advanced.LogDailyMegabytes = Math.Clamp(LogDailyMegabytes, 0, 10_000);
        draft.Advanced.CheckForUpdates = CheckForUpdates;
        draft.Advanced.UpdateRepository =
            string.IsNullOrWhiteSpace(UpdateRepository) ? null : UpdateRepository.Trim();
    }

    /// <summary>
    /// True when two shortcuts would claim the same combination.
    /// </summary>
    private HotkeyEditorViewModel? FindConflict()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (HotkeyEditorViewModel editor in Hotkeys)
        {
            if (!editor.HasGesture)
            {
                continue;
            }

            if (!seen.Add(editor.Gesture))
            {
                return editor;
            }
        }

        return null;
    }

    /// <summary>
    /// Puts every state back to the colour it starts with. Takes effect when the settings are saved,
    /// like everything else on this screen.
    /// </summary>
    [RelayCommand]
    private void ResetTrayColours()
    {
        foreach (TrayColourChoice choice in TrayColours)
        {
            choice.Reset();
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (FindConflict() is { } conflict)
        {
            conflict.Problem = localizer["hotkey.conflict"];
            StatusMessage = localizer["settings.conflictNotSaved"];
            return;
        }

        WriteToDraft();

        if (autoStart.IsSupported && autoStart.IsEnabled() != StartWithSystem
            && !autoStart.SetEnabled(StartWithSystem))
        {
            // The rest of the settings still save. Silently showing the switch as on would be worse
            // than saving what worked and saying what did not.
            StatusMessage = localizer["settings.autoStartFailed"];
            draft.General.StartWithSystem = autoStart.IsEnabled();
        }

        await settings.ReplaceAsync(draft);

        foreach (HotkeyEditorViewModel editor in Hotkeys)
        {
            await hotkeyStore.SetBindingAsync(
                editor.ActionId,
                editor.HasGesture ? editor.Gesture : null);
        }

        await hotkeys.ReloadAsync();
        await LoadAsync();

        // The draft has been applied, so further edits start from the values now in force.
        draft = settings.Current.Clone();

        Closed?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    /// <summary>
    /// Whether the import is offered: only to whoever may change what everybody shares.
    /// </summary>
    public bool CanChangeShared => permissions?.CanChangeShared != false;

    public void Dispose()
    {
        if (permissions is not null)
        {
            permissions.Changed -= OnPermissionsChanged;
        }

        Storage?.Dispose();
    }

    // The role can change on the synchronisation thread while the screen is open.
    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        if (ui is null)
        {
            OnPropertyChanged(nameof(CanChangeShared));
            return;
        }

        _ = ui.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(CanChangeShared));
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private void OpenImport() => ScreenRequested?.Invoke(this, AppScreen.Import);

    [RelayCommand]
    private void OpenExport() => ScreenRequested?.Invoke(this, AppScreen.Export);

    /// <summary>
    /// Reads the profile list from the store again.
    /// </summary>
    /// <remarks>
    /// The list keeps itself current for everything the window does: an import and an edit both
    /// reload it. What it cannot see is the store being changed from outside, by the companion
    /// command in a terminal. This is the way back from that, and it is here rather than in the
    /// header because it is needed rarely.
    /// </remarks>
    [RelayCommand]
    private void ReloadProfiles()
    {
        ProfileReloadRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = localizer["settings.profilesReloaded"];
    }

    [RelayCommand]
    private async Task ForgetStoredCredentialsAsync()
    {
        // Signed out first, while the refresh token is still there to end the session with: removed
        // from the keystore alone, the session would stay open on the server until it expired.
        if (server is not null)
        {
            await server.SignOutAndEraseAsync();
        }

        int removed;

        if (credentialsReset is not null)
        {
            // Only this server's: the library on this computer and other servers' copies keep theirs.
            removed = await credentialsReset.ForgetAsync();
        }
        else
        {
            // Everything goes, but what is reported is the number of profile sign ins, the same
            // number the screen showed before the button was pressed.
            removed = (await secrets.ListAsync()).Count(SecretReference.IsProfileReference);
            await secrets.ClearAsync();
        }

        StoredSecretCount = 0;
        StatusMessage = localizer.Translate("settings.credentialsCleared", removed);
    }

    /// <summary>
    /// Checks now, whatever the switch says, and reports whatever came back.
    /// </summary>
    /// <remarks>
    /// The repository from the form rather than from the saved settings, so the field can be tried
    /// before it is saved. Saving first would make a typo something you discover after committing
    /// to it.
    /// </remarks>
    [RelayCommand]
    private async Task CheckForUpdatesNowAsync(CancellationToken cancellationToken = default)
    {
        UpdateStatus = localizer["update.checking"];

        WriteToDraft();
        await settings.ReplaceAsync(draft, cancellationToken);

        UpdateCheckResult result = await updates.CheckAsync(cancellationToken);

        UpdateStatus = result.Outcome switch
        {
            UpdateOutcome.UpdateAvailable => localizer.Translate(
                "update.available",
                result.LatestVersion?.ToString() ?? string.Empty,
                UpdateCoordinator.CurrentVersion.ToString()),
            UpdateOutcome.UpToDate => localizer.Translate(
                "update.upToDate",
                UpdateCoordinator.CurrentVersion.ToString()),
            UpdateOutcome.NotConfigured => localizer["update.notConfigured"],
            _ => localizer.Translate("update.failed", result.Detail),
        };
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        int removed = await sessions.PruneAsync(DateTimeOffset.UtcNow);
        StatusMessage = localizer.Translate("settings.historyCleared", removed);
    }

    /// <summary>
    /// The name a diagnostics bundle should be offered under.
    /// </summary>
    public string DiagnosticsFileName => diagnostics.SuggestedFileName;

    /// <summary>
    /// Writes a diagnostics bundle. The destination comes from the view, which owns the picker.
    /// </summary>
    public async Task WriteDiagnosticsAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        int entries = await diagnostics.WriteAsync(path, cancellationToken);
        StatusMessage = localizer.Translate("settings.diagnosticsWritten", entries, path);
    }

    [RelayCommand]
    private void ReloadLanguages()
    {
        // Picks up a language file dropped into the user directory while the application is running.
        if (localizer is LocalizationManager manager)
        {
            manager.Reload();
        }

        Languages.Clear();
        Languages.Add(new LanguageChoice(null, localizer["settings.followSystem"]));

        foreach (LanguageDescriptor language in localizer.AvailableLanguages)
        {
            Languages.Add(new LanguageChoice(language.Code, language.NativeName));
        }

        SelectedLanguage = Languages.FirstOrDefault(language => language.Code == draft.General.Language)
            ?? Languages[0];

        StatusMessage = localizer.Translate("settings.languagesReloaded", Languages.Count - 1);
    }

    /// <summary>
    /// The language the system asks for, shown next to the follow system entry.
    /// </summary>
    public string SystemLanguageDisplay => languages.SystemLanguage;
}

/// <summary>
/// One entry in the language picker. A null code means follow the operating system.
/// </summary>
public sealed record LanguageChoice(string? Code, string Name);

/// <summary>
/// One entry in the theme picker.
/// </summary>
public sealed record ThemeChoice(ThemePreference Preference, string Name);

/// <summary>
/// What one state of the notification area icon does.
/// </summary>
public enum TrayColourMode
{
    /// <summary>The state draws the icon in a colour of its own.</summary>
    Colour,

    /// <summary>The state draws the icon as the platform does.</summary>
    Original,

    /// <summary>The state is passed over and the next one that applies decides.</summary>
    Off,
}

/// <summary>
/// One entry in the picker for what a state of the icon does.
/// </summary>
public sealed record TrayColourModeChoice(TrayColourMode Mode, string Name);

/// <summary>
/// The colour chosen for one state of the notification area icon.
/// </summary>
/// <remarks>
/// The original colour is a choice of its own rather than a colour, because on macOS it is the
/// template the menu bar tints and no fixed colour is. The picker keeps the last colour while another
/// choice is made, so switching back and forth does not lose it.
/// </remarks>
public sealed partial class TrayColourChoice : ObservableObject
{
    /// <summary>
    /// The states in the order the icon gives them priority, so the list reads like the decision.
    /// </summary>
    public static IReadOnlyList<TrayStatus> Order { get; } =
    [
        TrayStatus.AwaitingInput,
        TrayStatus.RemoteSignInRequired,
        TrayStatus.Unavailable,
        TrayStatus.Failed,
        TrayStatus.Reconnecting,
        TrayStatus.Connecting,
        TrayStatus.ConnectedMultiple,
        TrayStatus.Connected,
        TrayStatus.RemoteOffline,
        TrayStatus.RemoteProblem,
        TrayStatus.IdleRemote,
        TrayStatus.IdleLocal,
    ];

    /// <summary>
    /// What the picker shows while no colour was ever chosen: the blue of the mark, which is what the
    /// original is on Windows.
    /// </summary>
    private static readonly Color Mark = Color.FromRgb(59, 130, 246);

    public TrayColourChoice(TrayStatus status, string name, IReadOnlyList<TrayColourModeChoice> modes, string? stored)
    {
        ArgumentNullException.ThrowIfNull(modes);

        Status = status;
        Name = name;
        Modes = modes;
        Load(stored);
    }

    public TrayStatus Status { get; }

    public string Name { get; }

    public IReadOnlyList<TrayColourModeChoice> Modes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColour))]
    public partial TrayColourModeChoice? SelectedMode { get; set; }

    [ObservableProperty]
    public partial Color Colour { get; set; }

    /// <summary>
    /// True when the colour picker applies.
    /// </summary>
    public bool HasColour => SelectedMode?.Mode == TrayColourMode.Colour;

    /// <summary>
    /// The value the settings file keeps: <c>#RRGGBB</c>, null for the original, or
    /// <see cref="TrayIconSettings.Off"/>.
    /// </summary>
    public string? Stored => SelectedMode?.Mode switch
    {
        TrayColourMode.Colour => new TrayIconColour(Colour.R, Colour.G, Colour.B).ToString(),
        TrayColourMode.Off => TrayIconSettings.Off,
        _ => null,
    };

    public void Reset() => Load(TrayIconSettings.Default(Status));

    private void Load(string? stored)
    {
        TrayColourMode mode;

        if (string.Equals(stored?.Trim(), TrayIconSettings.Off, StringComparison.OrdinalIgnoreCase))
        {
            mode = TrayColourMode.Off;
        }
        else if (TrayIconColour.TryParse(stored, out TrayIconColour colour))
        {
            mode = TrayColourMode.Colour;
            Colour = Color.FromRgb(colour.Red, colour.Green, colour.Blue);
        }
        else
        {
            mode = TrayColourMode.Original;
        }

        if (mode != TrayColourMode.Colour)
        {
            Colour = TrayIconColour.TryParse(TrayIconSettings.Default(Status), out TrayIconColour fallback)
                ? Color.FromRgb(fallback.Red, fallback.Green, fallback.Blue)
                : Mark;
        }

        SelectedMode = Modes.FirstOrDefault(choice => choice.Mode == mode) ?? Modes[0];
    }
}

/// <summary>
/// One entry in the picker for what the profile editor opens with.
/// </summary>
public sealed record EditorViewChoice(ProfileEditorView View, string Name);

/// <summary>
/// A page of the settings that another screen can open them at.
/// </summary>
public enum SettingsPage
{
    Storage,
}
