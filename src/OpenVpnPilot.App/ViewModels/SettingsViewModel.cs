using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Library;
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
    private readonly SharedLibrarySync library;
    private bool disposed;

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
        SharedLibrarySync library)
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
        ArgumentNullException.ThrowIfNull(library);

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
        this.library = library;

        library.StatusChanged += OnLibraryStatusChanged;

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
    /// How large the log files may be together, in megabytes. Zero sets no limit.
    /// </summary>
    [ObservableProperty]
    public partial int LogMaximumMegabytes { get; set; }

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

        StoredSecretCount = (await secrets.ListAsync(cancellationToken)).Count(IsSignIn);

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
        LogMaximumMegabytes = draft.Advanced.LogMaximumMegabytes;
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
        draft.Advanced.LogMaximumMegabytes = Math.Clamp(LogMaximumMegabytes, 0, 100_000);
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
        // One by one rather than clearing the store, which would take the passphrase of a shared
        // library with it: that is not a sign in, and losing it would stop this machine synchronising
        // without anything on this page having said so.
        int removed = 0;

        foreach (string reference in (await secrets.ListAsync()).Where(IsSignIn))
        {
            await secrets.DeleteAsync(reference);
            removed++;
        }

        StoredSecretCount = 0;
        StatusMessage = localizer.Translate("settings.credentialsCleared", removed);
    }

    private static bool IsSignIn(string reference) => SecretReference.TryParse(reference, out _, out _);

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

    /// <summary>
    /// The shared file, or null while the library is only on this machine.
    /// </summary>
    public string? LibraryPath => library.SharedPath;

    public bool IsLibraryShared => library.SharedPath is not null;

    public bool LibraryNeedsPassphrase => library.Status.NeedsPassphrase;

    /// <summary>
    /// When the library was last reconciled, or what stands in the way.
    /// </summary>
    public string LibraryStatusText => SharedLibraryText.Describe(library.Status, localizer);

    public string LibraryPassphraseLabel =>
        localizer[LibraryNeedsPassphrase ? "library.enterPassphrase" : "library.changePassphrase"];

    /// <summary>
    /// Shown after asking to stop sharing, so a click cannot do it by accident.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConfirmingLeave { get; set; }

    [ObservableProperty]
    public partial bool IsLibraryBusy { get; set; }

    /// <summary>
    /// Joins an existing shared file. The path comes from the view, which owns the picker.
    /// </summary>
    /// <returns>Why the passphrase was not accepted, or null when joining succeeded.</returns>
    public Task<string?> JoinLibraryAsync(string path, string passphrase) =>
        RunLibraryAsync(() => library.JoinAsync(path, passphrase));

    /// <summary>
    /// Starts sharing this machine's library as a new file.
    /// </summary>
    public Task<string?> CreateLibraryAsync(string path, string passphrase) =>
        RunLibraryAsync(() => library.CreateAsync(path, passphrase));

    /// <summary>
    /// Stores the passphrase the shared file opens with now.
    /// </summary>
    public Task<string?> ProvideLibraryPassphraseAsync(string passphrase) =>
        RunLibraryAsync(() => library.ProvidePassphraseAsync(passphrase));

    /// <summary>
    /// Encrypts the shared file with a new passphrase.
    /// </summary>
    public Task<string?> ChangeLibraryPassphraseAsync(string passphrase) =>
        RunLibraryAsync(async () =>
        {
            SharedLibraryStatus status = await library.ChangePassphraseAsync(passphrase);

            // Nothing was changed when the library could not be reconciled first, and the prompt
            // says why rather than closing as if it had worked.
            return status.Condition == SharedLibraryCondition.Synchronised
                ? status
                : throw new SharedLibraryUnavailableException(SharedLibraryText.Describe(status, localizer));
        });

    public PassphrasePromptViewModel CreateJoinPrompt(string path) => new(
        localizer,
        localizer["library.joinTitle"],
        localizer.Translate("library.joinMessage", Path.GetFileName(path)),
        isNew: false,
        passphrase => JoinLibraryAsync(path, passphrase));

    public PassphrasePromptViewModel CreateCreatePrompt(string path) => new(
        localizer,
        localizer["library.createTitle"],
        localizer.Translate("library.createMessage", Path.GetFileName(path)),
        isNew: true,
        passphrase => CreateLibraryAsync(path, passphrase));

    public PassphrasePromptViewModel CreatePassphrasePrompt() => LibraryNeedsPassphrase
        ? new(
            localizer,
            localizer["library.enterPassphrase"],
            localizer["library.enterPassphraseMessage"],
            isNew: false,
            ProvideLibraryPassphraseAsync)
        : new(
            localizer,
            localizer["library.changePassphrase"],
            localizer["library.changePassphraseMessage"],
            isNew: true,
            ChangeLibraryPassphraseAsync);

    [RelayCommand]
    private async Task SyncLibraryNowAsync()
    {
        IsLibraryBusy = true;

        try
        {
            await library.SyncNowAsync();
        }
        finally
        {
            IsLibraryBusy = false;
            RaiseLibrary();
        }
    }

    [RelayCommand]
    private void AskToLeaveLibrary() => IsConfirmingLeave = true;

    [RelayCommand]
    private void CancelLeaveLibrary() => IsConfirmingLeave = false;

    [RelayCommand]
    private async Task LeaveLibraryAsync()
    {
        IsConfirmingLeave = false;
        await library.LeaveAsync();
        StatusMessage = localizer["library.left"];
        RaiseLibrary();
    }

    /// <summary>
    /// Runs one of the actions that reach the shared file, and turns what can go wrong into a sentence.
    /// </summary>
    private async Task<string?> RunLibraryAsync(Func<Task<SharedLibraryStatus>> action)
    {
        IsLibraryBusy = true;

        try
        {
            SharedLibraryStatus status = await action();
            StatusMessage = SharedLibraryText.Describe(status, localizer);
            return null;
        }
        catch (Exception exception) when (SharedLibraryText.Refusal(exception, localizer) is not null)
        {
            // A wrong passphrase, a file that is not a library or a folder out of reach is said in the
            // prompt, which stays open for another try.
            return SharedLibraryText.Refusal(exception, localizer);
        }
        finally
        {
            IsLibraryBusy = false;
            RaiseLibrary();
        }
    }

    private void OnLibraryStatusChanged(object? sender, SharedLibraryStatus status) =>
        Dispatcher.UIThread.Post(RaiseLibrary);

    private void RaiseLibrary()
    {
        OnPropertyChanged(nameof(LibraryPath));
        OnPropertyChanged(nameof(IsLibraryShared));
        OnPropertyChanged(nameof(LibraryNeedsPassphrase));
        OnPropertyChanged(nameof(LibraryStatusText));
        OnPropertyChanged(nameof(LibraryPassphraseLabel));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        library.StatusChanged -= OnLibraryStatusChanged;
    }
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
