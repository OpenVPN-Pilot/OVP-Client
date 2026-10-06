using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The server's segment of the status bar and the banners for the states that block.
/// </summary>
/// <remarks>
/// <para>
/// In Local mode the segment is a grey "Local" label that points at the storage settings. In Server
/// mode it is a dot coloured by the state and one compact line, with the details in the tooltip and
/// the actions in a flyout. "synced ... ago" is relative, so the line is rewritten every 30 seconds
/// even when nothing else changed.
/// </para>
/// <para>
/// Only what blocks gets a banner: a sign in that is needed, a client too old for the server, a clock
/// the server refuses and a certificate this computer does not trust. Being offline is not one,
/// because the application keeps working from the copy; it is the amber dot.
/// </para>
/// <para>
/// Something the server did on its own, such as deleting a profile whose tunnel was up, is a notice:
/// a line beside the state for <see cref="NoticeDuration"/>, long enough to be read and gone before
/// it turns into clutter.
/// </para>
/// </remarks>
public sealed partial class ServerStatusViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(30);

    private readonly ILocalizer localizer;
    private readonly ISettingsService settings;
    private readonly IUserInterfaceThread ui;
    private readonly TimeProvider time;
    private readonly IServerStatusSource? source;
    private readonly ITimer? ticker;
    private readonly IServerNotices? notices;

    // Touched only on the user interface thread.
    private ServerNotice? shownNotice;
    private ITimer? noticeTimer;

    public ServerStatusViewModel(
        ILocalizer localizer,
        ISettingsService settings,
        IUserInterfaceThread ui,
        TimeProvider time,
        IServerStatusSource? source = null,
        IServerNotices? notices = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(time);

        this.localizer = localizer;
        this.settings = settings;
        this.ui = ui;
        this.time = time;
        this.source = source;
        this.notices = notices;

        localizer.LanguageChanged += OnChanged;

        if (source is not null)
        {
            source.Changed += OnChanged;
            ticker = time.CreateTimer(_ => OnChanged(this, EventArgs.Empty), null, RefreshInterval, RefreshInterval);
        }

        if (notices is not null)
        {
            notices.Raised += OnNoticeRaised;
        }

        Refresh();
    }

    /// <summary>
    /// Raised when a screen has to be opened, such as the log or the storage settings.
    /// </summary>
    public event EventHandler<AppScreen>? ScreenRequested;

    /// <summary>
    /// Raised with the address of a web page to open.
    /// </summary>
    public event EventHandler<string>? PageRequested;

    public bool IsServerMode => source is not null;

    public bool IsLocal => source is null;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Details { get; set; } = string.Empty;

    /// <summary>
    /// The latest notice in words while it is shown, otherwise empty.
    /// </summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGood), nameof(IsBusy), nameof(IsWarning), nameof(IsProblem))]
    public partial ServerStatusTone Tone { get; set; }

    public bool IsGood => Tone == ServerStatusTone.Good;

    public bool IsBusy => Tone == ServerStatusTone.Busy;

    public bool IsWarning => Tone == ServerStatusTone.Warning;

    public bool IsProblem => Tone == ServerStatusTone.Problem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    public partial bool CanSyncNow { get; set; }

    [ObservableProperty]
    public partial bool NeedsSignIn { get; set; }

    [ObservableProperty]
    public partial bool HasBanner { get; set; }

    [ObservableProperty]
    public partial string BannerTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BannerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool BannerOffersUpdate { get; set; }

    /// <summary>
    /// The page a client that is too old gets its update from; null when no repository is named.
    /// </summary>
    public string? UpdatePageUrl =>
        settings.Current.Advanced.UpdateRepository is { Length: > 0 } repository
            ? $"https://github.com/{repository}/releases/latest"
            : null;

    public void Dispose()
    {
        localizer.LanguageChanged -= OnChanged;

        if (source is not null)
        {
            source.Changed -= OnChanged;
        }

        if (notices is not null)
        {
            notices.Raised -= OnNoticeRaised;
        }

        ticker?.Dispose();
        noticeTimer?.Dispose();
    }

    /// <summary>
    /// Reads the state again. Must run on the user interface thread.
    /// </summary>
    public void Refresh()
    {
        Notice = shownNotice is null ? string.Empty : ServerNoticeText.Describe(localizer, shownNotice);
        HasNotice = Notice.Length > 0;

        if (source is null)
        {
            Text = localizer["statusBar.local"];
            Details = localizer["statusBar.localHint"];
            Tone = ServerStatusTone.Neutral;
            return;
        }

        ServerStatusSnapshot snapshot = source.Current;
        SyncState state = snapshot.State;

        Text = ServerStatusText.Compact(localizer, snapshot, time.GetUtcNow());
        Details = ServerStatusText.Details(localizer, snapshot);
        Tone = ServerStatusText.Tone(state);
        CanSyncNow = snapshot.SignedIn && state != SyncState.Synchronising;
        NeedsSignIn = state is SyncState.SignInRequired or SyncState.KeystoreRefused;

        (string Title, string Text)? banner = state switch
        {
            SyncState.SignInRequired => (localizer["banner.signInTitle"], localizer.Translate("banner.signInText", snapshot.Host)),
            SyncState.ClientOutdated => (localizer["banner.clientOutdatedTitle"], localizer.Translate("banner.clientOutdatedText", snapshot.Host)),
            SyncState.ClockWrong => (localizer["banner.clockTitle"], localizer.Translate("banner.clockText", snapshot.Sync.Detail ?? string.Empty)),
            SyncState.CertificateUntrusted => (localizer["banner.certificateTitle"], localizer.Translate("banner.certificateText", snapshot.Host)),
            SyncState.SettingsUnreadable => (localizer["banner.settingsTitle"], localizer["banner.settingsText"]),
            SyncState.KeystoreRefused => (localizer["banner.keystoreTitle"], localizer["banner.keystoreText"]),
            _ => null,
        };

        HasBanner = banner is not null;
        BannerTitle = banner?.Title ?? string.Empty;
        BannerText = banner?.Text ?? string.Empty;
        BannerOffersUpdate = state == SyncState.ClientOutdated && UpdatePageUrl is not null;
    }

    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private void SyncNow() => source?.RequestSync();

    [RelayCommand]
    private void ShowServerLog() => ScreenRequested?.Invoke(this, AppScreen.ServerLog);

    [RelayCommand]
    private void SignIn() => ScreenRequested?.Invoke(this, AppScreen.ServerSignIn);

    [RelayCommand]
    private void OpenStorageSettings() => ScreenRequested?.Invoke(this, AppScreen.StorageSettings);

    /// <summary>
    /// Asks the window to open the page the update comes from; opening a page is the window's business.
    /// </summary>
    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (UpdatePageUrl is { } url)
        {
            PageRequested?.Invoke(this, url);
        }
    }

    private void OnNoticeRaised(object? sender, ServerNotice notice) =>
        _ = ui.InvokeAsync(() =>
        {
            ShowNotice(notice);
            return Task.CompletedTask;
        });

    private void ShowNotice(ServerNotice notice)
    {
        shownNotice = notice;
        noticeTimer?.Dispose();
        noticeTimer = time.CreateTimer(
            _ => _ = ui.InvokeAsync(() =>
            {
                ClearNotice(notice);
                return Task.CompletedTask;
            }),
            null,
            NoticeDuration,
            Timeout.InfiniteTimeSpan);
        Refresh();
    }

    // Only the notice this timer was armed for, so a newer one keeps its full time.
    private void ClearNotice(ServerNotice notice)
    {
        if (!ReferenceEquals(shownNotice, notice))
        {
            return;
        }

        shownNotice = null;
        noticeTimer?.Dispose();
        noticeTimer = null;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) =>
        _ = ui.InvokeAsync(() =>
        {
            Refresh();
            return Task.CompletedTask;
        });
}
