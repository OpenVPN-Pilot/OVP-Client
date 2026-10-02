using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The steps of finishing a server's setup after the restart into it.
/// </summary>
public enum FirstSyncStep
{
    /// <summary>Confirming the sign in, then fetching the profiles.</summary>
    Working,

    /// <summary>Signing in, because the stored session was not accepted or the person went back.</summary>
    SignIn,

    /// <summary>The synchronisation did not complete; try again, go back or use this computer.</summary>
    Failed,

    /// <summary>Leaving for this computer; the application restarts.</summary>
    Leaving,
}

/// <summary>
/// Finishes setting up a server in the copy that was restarted into it: confirms the sign in the
/// previous copy made, then runs the first full synchronisation with its progress on screen.
/// </summary>
/// <remarks>
/// The main window appears once the profiles are here, so the first thing the person sees of the
/// server is its list rather than an empty one filling up. A failure offers going back to the sign
/// in and using this computer instead, which is a switch back to the local library.
/// </remarks>
public sealed partial class FirstSyncViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizer localizer;
    private readonly IServerSessionCoordinator coordinator;
    private readonly IServerSignIn signIn;
    private readonly ISyncEngine engine;
    private readonly IEntraSignIn entra;
    private readonly IStorageModeSwitcher switcher;
    private readonly Uri serverAddress;
    private readonly string serverKey;
    private readonly ILogger<FirstSyncViewModel> logger;
    private readonly CancellationTokenSource lifetime = new();

    private Func<string>? status;
    private Func<string>? message;
    private Func<string?>? reference;
    private bool finished;
    private bool disposed;

    public FirstSyncViewModel(
        ILocalizer localizer,
        IServerSessionCoordinator coordinator,
        IServerSignIn signIn,
        ISyncEngine engine,
        IEntraSignIn entra,
        IStorageModeSwitcher switcher,
        IServerConnection connection,
        ILogger<FirstSyncViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(entra);
        ArgumentNullException.ThrowIfNull(switcher);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        this.localizer = localizer;
        this.coordinator = coordinator;
        this.signIn = signIn;
        this.engine = engine;
        this.entra = entra;
        this.switcher = switcher;
        this.logger = logger;
        serverAddress = connection.BaseAddress;
        serverKey = connection.ServerKey;

        localizer.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Raised once, when the main window may appear.
    /// </summary>
    public event EventHandler? Finished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(IsSignIn), nameof(IsFailed), nameof(IsLeaving))]
    public partial FirstSyncStep Step { get; set; }

    public bool IsWorking => Step == FirstSyncStep.Working;

    public bool IsSignIn => Step == FirstSyncStep.SignIn;

    public bool IsFailed => Step == FirstSyncStep.Failed;

    public bool IsLeaving => Step == FirstSyncStep.Leaving;

    public string Heading => localizer.Translate(
        "firstRun.syncTitle",
        serverAddress.IsDefaultPort ? serverAddress.Host : serverAddress.Authority);

    /// <summary>
    /// What is happening right now.
    /// </summary>
    public string? Status => status?.Invoke();

    public string? Message => message?.Invoke();

    public bool HasMessage => message is not null;

    public string? Reference => reference?.Invoke();

    public bool HasReference => Reference is not null;

    [ObservableProperty]
    public partial SignInViewModel? SignIn { get; set; }

    /// <summary>
    /// Confirms the session and synchronises. Called once the window is up.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await ConfirmAndSynchronizeAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The window was closed meanwhile; the application has gone on without waiting.
        }
    }

    private async Task ConfirmAndSynchronizeAsync()
    {
        Step = FirstSyncStep.Working;
        Show(null, null);
        SetStatus(() => localizer["firstRun.syncConfirming"]);

        ServerResult<CurrentUserResponse> confirmed = await coordinator.ConfirmSessionAsync(lifetime.Token);

        if (!confirmed.IsSuccess)
        {
            ServerAccountLog.SessionNotConfirmed(logger, serverKey, confirmed.Outcome, confirmed.Code, confirmed.RequestId);

            if (NeedsSignIn(confirmed))
            {
                await ShowSignInAsync();
                return;
            }

            Fail(() => ServerMessages.SignInFailure(localizer, confirmed), () => ServerMessages.Reference(localizer, confirmed));
            return;
        }

        await SynchronizeAsync();
    }

    /// <summary>
    /// The person closed the window: the application goes on to its main window as on any start.
    /// </summary>
    /// <returns>False when leaving for this computer, which ends the application instead.</returns>
    public bool Abandon()
    {
        if (Step == FirstSyncStep.Leaving)
        {
            return false;
        }

        if (!disposed)
        {
            lifetime.Cancel();
        }

        Finish();
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        localizer.LanguageChanged -= OnLanguageChanged;
        lifetime.Cancel();
        lifetime.Dispose();
        DropSignIn();
    }

    [RelayCommand]
    private Task RetryAsync() => StartAsync();

    /// <summary>
    /// Back to the sign in, signed out first so the person can sign in as somebody else.
    /// </summary>
    [RelayCommand]
    private async Task BackAsync()
    {
        Step = FirstSyncStep.Working;
        Show(null, null);
        SetStatus(() => localizer["firstRun.checking"]);

        try
        {
            await signIn.SignOutAsync(lifetime.Token);
            await ShowSignInAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The window was closed meanwhile; the application has gone on without waiting.
        }
    }

    [RelayCommand]
    private async Task UseThisComputerAsync()
    {
        Step = FirstSyncStep.Leaving;
        StorageSwitchResult result = await switcher.SwitchToLocalAsync(CancellationToken.None);

        if (result.Outcome is StorageSwitchOutcome.Restarting)
        {
            return;
        }

        ServerAccountLog.FirstRunSwitchFailed(logger, serverAddress.AbsoluteUri, result.Outcome);
        Fail(
            () => result.Outcome == StorageSwitchOutcome.RestartFailed
                ? localizer["firstRun.restartFailed"]
                : localizer["firstRun.switchFailed"],
            null);
    }

    private async Task SynchronizeAsync()
    {
        Step = FirstSyncStep.Working;
        SetStatus(() => localizer["firstRun.syncRunning"]);

        SyncCycleResult result = await engine.SynchronizeAsync(lifetime.Token);
        ServerAccountLog.FirstSynchronisation(logger, serverKey, result.State, result.Pushed);

        if (!result.Completed)
        {
            SyncStatus snapshot = engine.Status;
            Fail(() => SyncFailure(result.State, snapshot), () => snapshot.LastRequestId is { Length: > 0 } id
                ? localizer.Translate("signIn.reference", id)
                : null);
            return;
        }

        await coordinator.StartAsync(lifetime.Token);
        Finish();
    }

    private async Task ShowSignInAsync()
    {
        SetStatus(() => localizer["firstRun.checking"]);

        ServerCheckResult check = await signIn.CheckServerAsync(lifetime.Token);

        if (!check.IsCompatible || check.Info is null)
        {
            Fail(
                () => ServerMessages.CheckFailure(localizer, check) ?? string.Empty,
                () => ServerMessages.Reference(localizer, check.Transport));
            return;
        }

        DropSignIn();

        SignInViewModel form = new(localizer, signIn, entra, check.Info, serverAddress);
        form.SignedIn += OnSignedIn;
        SignIn = form;
        Step = FirstSyncStep.SignIn;
    }

    private async void OnSignedIn(object? sender, CurrentUserResponse user)
    {
        try
        {
            DropSignIn();
            await SynchronizeAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The window was closed meanwhile; the application has gone on without waiting.
        }
    }

    private string SyncFailure(SyncState state, SyncStatus snapshot) => state switch
    {
        SyncState.Offline => localizer["firstRun.syncOffline"],
        SyncState.Degraded => localizer["firstRun.syncDegraded"],
        SyncState.SignInRequired => localizer["firstRun.syncSignIn"],
        SyncState.ClientOutdated => localizer["signIn.clientOutdated"],
        SyncState.ClockWrong => localizer.Translate("signIn.clockSkew", snapshot.Detail ?? string.Empty),
        SyncState.CertificateUntrusted => localizer["signIn.certificateUntrusted"],
        SyncState.SettingsUnreadable => localizer["signIn.identityUnavailable"],
        _ => localizer["firstRun.syncFailed"],
    };

    private static bool NeedsSignIn(ServerResult result) =>
        result.Outcome == ServerOutcome.NotSignedIn
        || (result.Code is { } code && (ServerErrorCodes.SignInRequired.Contains(code)
            || code is ServerErrorCodes.ClientMismatch or ServerErrorCodes.TokenInvalid or ServerErrorCodes.TokenMissing));

    private void Fail(Func<string> text, Func<string?>? requestReference)
    {
        Step = FirstSyncStep.Failed;
        SetStatus(null);
        Show(text, requestReference);
    }

    private void Finish()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void DropSignIn()
    {
        if (SignIn is { } form)
        {
            form.SignedIn -= OnSignedIn;
            form.Dispose();
            SignIn = null;
        }
    }

    private void SetStatus(Func<string>? text)
    {
        status = text;
        OnPropertyChanged(nameof(Status));
    }

    private void Show(Func<string>? text, Func<string?>? requestReference)
    {
        message = text;
        reference = requestReference;
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(Reference));
        OnPropertyChanged(nameof(HasReference));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Reference));
    });
}
