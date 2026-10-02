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
/// The steps of the first start.
/// </summary>
public enum FirstRunStep
{
    /// <summary>This computer, a server, or later.</summary>
    Choice,

    /// <summary>The server's address, and what it said about itself.</summary>
    Address,

    /// <summary>Signing in the way the server asks.</summary>
    SignIn,

    /// <summary>Signed in; the application restarts into the server.</summary>
    Restarting,
}

/// <summary>
/// Asks, on the very first start, where the profiles should live, and sets up a server when that is
/// the answer.
/// </summary>
/// <remarks>
/// <para>
/// This runs in a copy composed for the local library, because that is what a first start is. The
/// server is reached through a connection made for the purpose, which checks the address and signs
/// in. The sign in stores the server's refresh token in the keystore under the server's key, and only
/// then is the mode written and the application restarted into the server, told to continue with
/// the first synchronisation. The new copy finds the session where any later start would and goes on
/// from there without asking again.
/// </para>
/// <para>
/// Signing in before the restart keeps a person who cannot sign in, or changes their mind, on this
/// computer without a restart at all: nothing is written until the server has accepted them.
/// </para>
/// </remarks>
public sealed partial class FirstRunViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizer localizer;
    private readonly IServerConnectionFactory connections;
    private readonly IStorageModeSwitcher switcher;
    private readonly IEntraSignIn entra;
    private readonly ILogger<FirstRunViewModel> logger;

    /// <summary>
    /// Removes what this computer kept of a server that withdraws the account during the sign in.
    /// </summary>
    private readonly IServerLeftovers? leftovers;

    private IServerConnection? connection;
    private string? currentAddress;
    private string? normalisedAddress;
    private Func<string>? message;
    private Func<string?>? reference;
    private Func<string>? summary;
    private CancellationTokenSource? checking;
    private bool finished;

    public FirstRunViewModel(
        ILocalizer localizer,
        IServerConnectionFactory connections,
        IStorageModeSwitcher switcher,
        IEntraSignIn entra,
        ILogger<FirstRunViewModel> logger,
        IServerLeftovers? leftovers = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(switcher);
        ArgumentNullException.ThrowIfNull(entra);
        ArgumentNullException.ThrowIfNull(logger);

        this.localizer = localizer;
        this.connections = connections;
        this.switcher = switcher;
        this.entra = entra;
        this.logger = logger;
        this.leftovers = leftovers;

        localizer.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Raised once, when the application goes on with the local library. A switch to the server ends
    /// this copy instead, through the switcher.
    /// </summary>
    public event EventHandler<FirstRunChoice>? Finished;

    /// <summary>
    /// Raised once when a switch begun from the settings is abandoned; the application stays as it is.
    /// </summary>
    public event EventHandler? Cancelled;

    /// <summary>
    /// True for choosing a server from the settings rather than on the first start.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstRun), nameof(WindowTitle))]
    public partial bool IsSwitch { get; set; }

    public bool IsFirstRun => !IsSwitch;

    public string WindowTitle => IsSwitch ? localizer["storage.switchTitle"] : localizer["firstRun.windowTitle"];

    /// <summary>
    /// Starts at the address, for switching to a server from the settings.
    /// </summary>
    /// <param name="suggestedAddress">The address to offer, such as the server used last.</param>
    /// <param name="activeAddress">The server the application works with now, if any, which is not a switch.</param>
    public void BeginServerSwitch(string? suggestedAddress, string? activeAddress)
    {
        IsSwitch = true;
        currentAddress = activeAddress;
        Address = suggestedAddress ?? string.Empty;
        Show(null, null);
        Step = FirstRunStep.Address;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoice), nameof(IsAddress), nameof(IsSignIn), nameof(IsRestarting), nameof(CanGoBack))]
    public partial FirstRunStep Step { get; set; }

    public bool IsChoice => Step == FirstRunStep.Choice;

    public bool IsAddress => Step == FirstRunStep.Address;

    public bool IsSignIn => Step == FirstRunStep.SignIn;

    public bool IsRestarting => Step == FirstRunStep.Restarting;

    public bool CanGoBack => Step is FirstRunStep.Address or FirstRunStep.SignIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    public partial string Address { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsChecking { get; set; }

    public bool IsIdle => !IsChecking;

    /// <summary>
    /// The form that signs in, once the server has said how.
    /// </summary>
    [ObservableProperty]
    public partial SignInViewModel? SignIn { get; set; }

    /// <summary>
    /// The server's name, version and way of signing in, once it has answered.
    /// </summary>
    public string? ServerSummary => summary?.Invoke();

    public bool HasServerSummary => summary is not null;

    /// <summary>
    /// The precise problem with the address or the server, or with leaving for it.
    /// </summary>
    public string? Message => message?.Invoke();

    public bool HasMessage => message is not null;

    public string? Reference => reference?.Invoke();

    public bool HasReference => Reference is not null;

    /// <summary>
    /// Ends the first start as if "decide later" had been chosen, for a window closed midway.
    /// </summary>
    /// <returns>False when the application is restarting into the server and there is nothing to end.</returns>
    public bool Abandon()
    {
        if (Step == FirstRunStep.Restarting)
        {
            return false;
        }

        Finish(FirstRunChoice.DecideLater);
        return true;
    }

    public void Dispose()
    {
        localizer.LanguageChanged -= OnLanguageChanged;
        checking?.Cancel();
        checking?.Dispose();
        checking = null;
        DropConnection();
    }

    [RelayCommand]
    private void ChooseThisComputer() => Finish(FirstRunChoice.ThisComputer);

    [RelayCommand]
    private void DecideLater() => Finish(FirstRunChoice.DecideLater);

    [RelayCommand]
    private void ChooseServer()
    {
        Show(null, null);
        Step = FirstRunStep.Address;
    }

    /// <summary>
    /// Offered when signing in or switching did not work out.
    /// </summary>
    [RelayCommand]
    private void UseThisComputer() => Finish(FirstRunChoice.ThisComputer);

    [RelayCommand]
    private void Back()
    {
        checking?.Cancel();
        Show(null, null);

        switch (Step)
        {
            case FirstRunStep.SignIn:
                DropConnection();
                Step = FirstRunStep.Address;
                break;

            case FirstRunStep.Address when IsSwitch:
                Finish(FirstRunChoice.ThisComputer);
                break;

            case FirstRunStep.Address:
                Step = FirstRunStep.Choice;
                break;

            default:
                break;
        }
    }

    private bool CanContinue() => !IsChecking && !string.IsNullOrWhiteSpace(Address);

    /// <summary>
    /// Checks the address, then asks the server about itself.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        Show(null, null);
        DropConnection();

        string typed = Address.Trim();

        // A bare host name is what people type; the scheme is the one thing a server can be.
        if (!typed.Contains("://", StringComparison.Ordinal))
        {
            typed = "https://" + typed;
        }

        if (!ServerKey.TryNormalise(typed, out string? normalised, out ServerAddressProblem problem))
        {
            Show(() => AddressProblem(problem), null);
            return;
        }

        if (IsSwitch && string.Equals(normalised, currentAddress, StringComparison.Ordinal))
        {
            Show(() => localizer["storage.alreadyThisServer"], null);
            return;
        }

        checking?.Dispose();
        checking = new CancellationTokenSource();
        CancellationToken cancellationToken = checking.Token;

        string candidateKey = ServerKey.Compute(normalised);
        IServerConnection candidate = connections.Create(new Uri(normalised), candidateKey);
        candidate.Wipe.WipeRequested += (_, wiped) => RemoveLeftovers(wiped.Directive, candidateKey);
        IsChecking = true;

        try
        {
            ServerCheckResult check = await candidate.SignIn.CheckServerAsync(cancellationToken);

            if (!check.IsCompatible || check.Info is null)
            {
                ServerAccountLog.ServerCheckFailed(
                    logger,
                    normalised,
                    check.Compatibility,
                    check.Transport.Outcome,
                    check.Transport.Code,
                    check.Transport.RequestId);

                candidate.Dispose();
                Show(
                    () => ServerMessages.CheckFailure(localizer, check) ?? string.Empty,
                    () => ServerMessages.Reference(localizer, check.Transport));
                return;
            }

            ServerInfoResponse info = check.Info;
            ServerAccountLog.ServerChecked(logger, normalised, info.Name, info.Version, info.AuthMode);

            connection = candidate;
            normalisedAddress = normalised;
            summary = () => localizer.Translate("firstRun.serverSummary", info.Name, info.Version, ServerMessages.AuthMode(localizer, info.AuthMode));
            OnPropertyChanged(nameof(ServerSummary));
            OnPropertyChanged(nameof(HasServerSummary));

            SignInViewModel form = new(localizer, candidate.SignIn, entra, info, candidate.BaseAddress);
            form.SignedIn += OnSignedIn;
            SignIn = form;
            Step = FirstRunStep.SignIn;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            candidate.Dispose();
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async void OnSignedIn(object? sender, CurrentUserResponse user)
    {
        if (connection is null || normalisedAddress is null)
        {
            return;
        }

        try
        {
            await LeaveForServerAsync(connection, normalisedAddress, user);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An event handler is the end of the line for an exception, so it is reported here and
            // the person is offered the way back rather than left on a screen that waits for nothing.
            ServerAccountLog.StartFailed(logger, connection.ServerKey, exception);
            Step = FirstRunStep.SignIn;
            Show(() => localizer["firstRun.switchFailed"], null);
        }
    }

    /// <summary>
    /// Writes the server as the mode and restarts into it, which continues with the first
    /// synchronisation.
    /// </summary>
    private async Task LeaveForServerAsync(IServerConnection signedIn, string address, CurrentUserResponse user)
    {
        Step = FirstRunStep.Restarting;

        if (IsSwitch)
        {
            ServerStatusLog.SwitchSignedIn(logger, address, user.Id, user.Role);
        }
        else
        {
            ServerAccountLog.FirstRunChosen(logger, FirstRunChoice.Server);
            ServerAccountLog.FirstRunSignedIn(logger, address, user.Id, user.Role);
        }

        StorageSwitchResult result = await switcher.SwitchToServerAsync(address, StorageSwitchFollowUp.FirstSynchronisation);

        if (result.Outcome == StorageSwitchOutcome.Restarting)
        {
            // The session stays in the keystore for the next copy, which picks it up from there.
            return;
        }

        if (IsSwitch)
        {
            ServerStatusLog.SwitchAfterSignInFailed(logger, address, result.Outcome);
        }
        else
        {
            ServerAccountLog.FirstRunSwitchFailed(logger, address, result.Outcome);
        }

        // Nothing changed, so the sign in that was made for the server is not left behind either.
        await signedIn.SignIn.SignOutAsync(CancellationToken.None);

        Step = FirstRunStep.SignIn;
        Show(
            () => result.Outcome switch
            {
                StorageSwitchOutcome.RestartFailed => localizer["firstRun.restartFailed"],
                StorageSwitchOutcome.TunnelsUp => localizer["storage.disconnectFirst"],
                _ => localizer["firstRun.switchFailed"],
            },
            null);
    }

    /// <summary>
    /// The server withdrew the account while it was being signed in to: what this computer kept of
    /// it goes, and the application stays where it is.
    /// </summary>
    /// <remarks>
    /// Raised on the thread that received the answer, which must not wait for folders being removed.
    /// The sign in form says that the account has no access; the removal tells the person the rest.
    /// </remarks>
    private void RemoveLeftovers(ServerWipeDirective directive, string key)
    {
        if (leftovers is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await leftovers.RemoveAsync(directive, key, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Every step reports its own failures; this is what none of them caught.
                ServerStatusLog.LeftoversStepFailed(logger, "leftovers", exception);
            }
        });
    }

    private void Finish(FirstRunChoice choice)
    {
        if (finished)
        {
            return;
        }

        finished = true;
        checking?.Cancel();
        DropConnection();

        if (IsSwitch)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
            return;
        }

        ServerAccountLog.FirstRunChosen(logger, choice);
        Finished?.Invoke(this, choice);
    }

    private string AddressProblem(ServerAddressProblem problem) => problem switch
    {
        ServerAddressProblem.Empty => localizer["firstRun.addressEmpty"],
        ServerAddressProblem.NotHttps => localizer["firstRun.addressNotHttps"],
        ServerAddressProblem.CarriesCredentials => localizer["firstRun.addressCredentials"],
        ServerAddressProblem.CarriesPath or ServerAddressProblem.CarriesQuery => localizer["firstRun.addressPath"],
        _ => localizer["firstRun.addressInvalid"],
    };

    private void DropConnection()
    {
        if (SignIn is { } form)
        {
            form.SignedIn -= OnSignedIn;
            form.Dispose();
            SignIn = null;
        }

        summary = null;
        OnPropertyChanged(nameof(ServerSummary));
        OnPropertyChanged(nameof(HasServerSummary));

        connection?.Dispose();
        connection = null;
        normalisedAddress = null;
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
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Reference));
        OnPropertyChanged(nameof(ServerSummary));
    });
}
