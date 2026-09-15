using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
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
public sealed partial class SettingsViewModel : ViewModelBase
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
        UpdateCoordinator updates)
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

    public bool AutoStartAvailable => autoStart.IsSupported;

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

        StoredSecretCount = (await secrets.ListAsync(cancellationToken)).Count;

        // The registry is the truth for autostart, not the settings file, because the entry can be
        // removed from outside the application.
        StartWithSystem = autoStart.IsSupported && autoStart.IsEnabled();
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

        SelectedEditorView = EditorViews.FirstOrDefault(view => view.View == draft.General.ProfileEditor)
            ?? EditorViews[0];

        ProtectRoutes = draft.Connections.ProtectRoutes;
        ConnectTimeoutSeconds = draft.Connections.ConnectTimeoutSeconds;
        AutoReconnect = draft.Connections.AutoReconnect;
        MaxReconnectAttempts = draft.Connections.MaxReconnectAttempts;
        ReconnectDelaySeconds = draft.Connections.ReconnectDelaySeconds;
        RestoreOnStart = draft.Connections.RestoreOnStart;

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
        CheckForUpdates = draft.Advanced.CheckForUpdates;
        UpdateRepository = draft.Advanced.UpdateRepository ?? string.Empty;
    }

    private void WriteToDraft()
    {
        draft.General.Language = SelectedLanguage?.Code;
        draft.General.StartMinimised = StartMinimised;
        draft.General.StartWithSystem = StartWithSystem;
        draft.General.CloseToTray = CloseToTray;
        draft.General.ProfileEditor = SelectedEditorView?.View ?? ProfileEditorView.Form;

        draft.Appearance.Theme = SelectedTheme?.Preference ?? ThemePreference.System;

        draft.Connections.ProtectRoutes = ProtectRoutes;
        draft.Connections.ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 5, 600);
        draft.Connections.AutoReconnect = AutoReconnect;
        draft.Connections.MaxReconnectAttempts = Math.Clamp(MaxReconnectAttempts, 0, 100);
        draft.Connections.ReconnectDelaySeconds = Math.Clamp(ReconnectDelaySeconds, 1, 600);
        draft.Connections.RestoreOnStart = RestoreOnStart;

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
        int removed = await secrets.ClearAsync();
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
/// One entry in the picker for what the profile editor opens with.
/// </summary>
public sealed record EditorViewChoice(ProfileEditorView View, string Name);
