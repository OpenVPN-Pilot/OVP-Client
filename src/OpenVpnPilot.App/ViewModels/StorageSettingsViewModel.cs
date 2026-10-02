using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The storage page of the settings: where the profiles live, and switching that.
/// </summary>
/// <remarks>
/// <para>
/// In Server mode it shows the server, the person signed in with their role and provider, the
/// server's version, the last synchronisation and what is waiting, and offers synchronising now,
/// signing out or in, and the server's log.
/// </para>
/// <para>
/// Switching is a restart, so it is refused while a tunnel is up and confirmed before anything is
/// written. Switching to a server, another server included, opens the same address and sign in steps
/// as the first start; only a server that accepted the person is switched to. A different address is
/// a different server, with a copy of its own.
/// </para>
/// </remarks>
public sealed partial class StorageSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizer localizer;
    private readonly IActiveStorage storage;
    private readonly IActiveTunnels tunnels;
    private readonly IStorageModeSwitcher switcher;
    private readonly IUserInterfaceThread ui;
    private readonly ILogger<StorageSettingsViewModel> logger;
    private readonly IServerStatusSource? status;
    private readonly ISyncEngine? engine;
    private readonly IServerSignIn? server;

    private StorageMode? pendingSwitch;

    public StorageSettingsViewModel(
        ILocalizer localizer,
        IActiveStorage storage,
        IActiveTunnels tunnels,
        IStorageModeSwitcher switcher,
        IUserInterfaceThread ui,
        ILogger<StorageSettingsViewModel> logger,
        IServerStatusSource? status = null,
        ISyncEngine? engine = null,
        IServerSignIn? server = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(tunnels);
        ArgumentNullException.ThrowIfNull(switcher);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(logger);

        this.localizer = localizer;
        this.storage = storage;
        this.tunnels = tunnels;
        this.switcher = switcher;
        this.ui = ui;
        this.logger = logger;
        this.status = status;
        this.engine = engine;
        this.server = server;

        if (status is not null)
        {
            status.Changed += OnStatusChanged;
        }

        Refresh();
    }

    /// <summary>
    /// Raised when a screen has to be opened: the switch to a server, the sign in, the log.
    /// </summary>
    public event EventHandler<AppScreen>? ScreenRequested;

    public bool IsServerMode => storage.IsServerMode;

    public string ModeText => storage.IsServerMode ? localizer["storage.modeServer"] : localizer["storage.modeLocal"];

    public string ModeHint => storage.IsServerMode ? localizer["storage.modeServerHint"] : localizer["storage.modeLocalHint"];

    public string SwitchToServerLabel => storage.IsServerMode ? localizer["storage.switchToOtherServer"] : localizer["storage.switchToServer"];

    public string Address => storage.ServerAddress ?? string.Empty;

    [ObservableProperty]
    public partial string User { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Role { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Provider { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ServerVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastSync { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Pending { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string State { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(SignOutCommand))]
    public partial bool IsSignedIn { get; set; }

    public bool IsSignedOut => IsServerMode && !IsSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(SignOutCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// What the last action on this page came to.
    /// </summary>
    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConfirming { get; set; }

    [ObservableProperty]
    public partial string Confirmation { get; set; } = string.Empty;

    public void Dispose()
    {
        if (status is not null)
        {
            status.Changed -= OnStatusChanged;
        }
    }

    /// <summary>
    /// Reads what is shown again. Must run on the user interface thread.
    /// </summary>
    public void Refresh()
    {
        if (status is null)
        {
            return;
        }

        ServerStatusSnapshot snapshot = status.Current;
        string unknown = localizer["common.unknown"];

        IsSignedIn = snapshot.SignedIn;
        User = snapshot.User is { } user
            ? (user.DisplayName is { Length: > 0 } name ? $"{name} ({user.Username})" : user.Username)
            : localizer["storage.nobody"];
        Role = snapshot.User?.Role ?? unknown;
        Provider = snapshot.User?.Provider ?? unknown;
        ServerVersion = snapshot.Reachability.Info?.Version ?? unknown;
        LastSync = snapshot.Sync.LastPullAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? localizer["common.never"];
        Pending = snapshot.Sync.PendingChanges.ToString(CultureInfo.CurrentCulture);
        State = ServerStatusText.State(localizer, snapshot.State)
            ?? (snapshot.State == SyncState.Synchronised ? localizer["storage.stateSynchronised"] : string.Empty);
    }

    private bool CanSync() => engine is not null && IsSignedIn && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNowAsync()
    {
        IsBusy = true;
        Message = localizer["storage.syncing"];
        ServerStatusLog.SyncRequested(logger, "storage settings");

        try
        {
            SyncCycleResult result = await Task.Run(() => engine!.SynchronizeAsync(CancellationToken.None));
            Message = result.Completed
                ? localizer["storage.syncDone"]
                : localizer.Translate("storage.syncIncomplete", ServerStatusText.State(localizer, result.State) ?? result.State.ToString());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The engine reports every failure of the server as a state; this is anything else, and
            // the page says it did not work rather than staying on "synchronising".
            ServerStatusLog.SyncFailed(logger, exception);
            Message = localizer["storage.syncFailed"];
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private bool CanSignOut() => server is not null && IsSignedIn && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOutAsync()
    {
        IsBusy = true;
        ServerStatusLog.SignOutRequested(logger, Address);

        try
        {
            await Task.Run(() => server!.SignOutAsync(CancellationToken.None));
            Message = localizer["storage.signedOut"];
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void SignIn() => ScreenRequested?.Invoke(this, AppScreen.ServerSignIn);

    [RelayCommand]
    private void ShowServerLog() => ScreenRequested?.Invoke(this, AppScreen.ServerLog);

    [RelayCommand]
    private void RequestSwitchToServer() => Ask(StorageMode.Server);

    [RelayCommand]
    private void RequestSwitchToLocal() => Ask(StorageMode.Local);

    [RelayCommand]
    private void CancelSwitch()
    {
        pendingSwitch = null;
        IsConfirming = false;
    }

    /// <summary>
    /// Goes ahead with the switch that was confirmed: a server opens the address and sign in steps,
    /// this computer restarts at once.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmSwitchAsync()
    {
        StorageMode? mode = pendingSwitch;
        pendingSwitch = null;
        IsConfirming = false;

        if (mode == StorageMode.Server)
        {
            ScreenRequested?.Invoke(this, AppScreen.ServerSwitch);
            return;
        }

        if (mode != StorageMode.Local)
        {
            return;
        }

        StorageSwitchResult result = await switcher.SwitchToLocalAsync();
        ServerStatusLog.SwitchConfirmed(logger, StorageMode.Local, result.Outcome);

        Message = result.Outcome switch
        {
            StorageSwitchOutcome.Restarting => localizer["firstRun.leaving"],
            StorageSwitchOutcome.TunnelsUp => localizer["storage.disconnectFirst"],
            StorageSwitchOutcome.RestartFailed => localizer["firstRun.restartFailed"],
            StorageSwitchOutcome.AlreadyActive => localizer["storage.alreadyLocal"],
            _ => localizer["storage.switchNotSaved"],
        };
    }

    private void Ask(StorageMode mode)
    {
        int up = tunnels.Count;

        if (up > 0)
        {
            ServerStatusLog.SwitchNotOfferedTunnelsUp(logger, mode, up);
            Message = localizer["storage.disconnectFirst"];
            IsConfirming = false;
            return;
        }

        pendingSwitch = mode;
        Confirmation = mode == StorageMode.Server
            ? localizer["storage.confirmServer"]
            : localizer["storage.confirmLocal"];
        Message = string.Empty;
        IsConfirming = true;
    }

    private void OnStatusChanged(object? sender, EventArgs e) =>
        _ = ui.InvokeAsync(() =>
        {
            Refresh();
            return Task.CompletedTask;
        });
}
