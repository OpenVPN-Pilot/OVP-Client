using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// Signs a person in to a server, in whichever way that server asks for.
/// </summary>
/// <remarks>
/// <para>
/// The form follows the server's own description of itself: a user name alone in mode <c>none</c>,
/// a user name and a password for the user file and the directory, and a button that opens the
/// Microsoft sign in in the browser for Entra ID. Whatever signs in is handed in from outside, so the
/// same form serves the first start, which signs in before the application restarts into the server,
/// and the running application, which signs in again when the session ended.
/// </para>
/// <para>
/// Every refusal the person can act on has its own sentence, with the request id beneath it for the
/// operator. A certificate that is not trusted is explained as a matter for the server's operator;
/// there is nothing here to get past it.
/// </para>
/// </remarks>
public sealed partial class SignInViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizer localizer;
    private readonly IServerSignIn signIn;
    private readonly IEntraSignIn entra;
    private readonly ServerInfoResponse info;

    // Rebuilt in the current language whenever it changes, so a message already on screen follows.
    private Func<string>? message;
    private Func<string?>? reference;
    private CancellationTokenSource? attempt;

    public SignInViewModel(
        ILocalizer localizer,
        IServerSignIn signIn,
        IEntraSignIn entra,
        ServerInfoResponse info,
        Uri serverAddress)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(entra);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(serverAddress);

        this.localizer = localizer;
        this.signIn = signIn;
        this.entra = entra;
        this.info = info;
        ServerAddress = serverAddress;

        localizer.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Raised on a successful sign in, with the person as the server described them.
    /// </summary>
    public event EventHandler<CurrentUserResponse>? SignedIn;

    public Uri ServerAddress { get; }

    public string ServerHost => ServerAddress.IsDefaultPort ? ServerAddress.Host : ServerAddress.Authority;

    public string AuthMode => info.AuthMode;

    public bool IsEntra => string.Equals(info.AuthMode, ServerAuthModes.Entra, StringComparison.Ordinal);

    /// <summary>
    /// True for a mode this version knows. A newer server could offer one it does not.
    /// </summary>
    public bool IsKnownMode => info.AuthMode is ServerAuthModes.None or ServerAuthModes.File or ServerAuthModes.Ldap
        || (IsEntra && info.Entra is not null);

    public bool AsksUsername => IsKnownMode && !IsEntra;

    public bool AsksPassword => AsksUsername && info.PasswordRequired;

    public string Heading => localizer.Translate("signIn.title", ServerHost);

    public string ModeHint => !IsKnownMode
        ? localizer["signIn.modeUnknown"]
        : info.AuthMode switch
        {
            ServerAuthModes.None => localizer["signIn.hintNone"],
            ServerAuthModes.Ldap => localizer["signIn.hintLdap"],
            ServerAuthModes.Entra => localizer["signIn.hintEntra"],
            _ => localizer["signIn.hintFile"],
        };

    /// <summary>
    /// What went wrong with the last attempt, in the current language.
    /// </summary>
    public string? Message => message?.Invoke();

    public bool HasMessage => message is not null;

    /// <summary>
    /// The request id of the last refusal, for the operator's log.
    /// </summary>
    public string? Reference => reference?.Invoke();

    public bool HasReference => Reference is not null;

    /// <summary>
    /// The person who signed in, once somebody has.
    /// </summary>
    public CurrentUserResponse? User { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// True while the browser is open for the Microsoft sign in, which the form says.
    /// </summary>
    [ObservableProperty]
    public partial bool IsWaitingForBrowser { get; set; }

    public bool IsIdle => !IsBusy;

    public void Dispose()
    {
        localizer.LanguageChanged -= OnLanguageChanged;
        attempt?.Cancel();
        attempt?.Dispose();
        attempt = null;
    }

    /// <summary>
    /// Ends an attempt that is waiting, the browser above all.
    /// </summary>
    [RelayCommand]
    private void CancelAttempt() => attempt?.Cancel();

    private bool CanSignIn() =>
        !IsBusy
        && IsKnownMode
        && (!AsksUsername || !string.IsNullOrWhiteSpace(Username))
        && (!AsksPassword || Password.Length > 0);

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        attempt?.Dispose();
        attempt = new CancellationTokenSource();
        CancellationToken cancellationToken = attempt.Token;

        IsBusy = true;
        Show(null, null);

        try
        {
            ServerResult<CurrentUserResponse>? answer = IsEntra
                ? await SignInWithMicrosoftAsync(cancellationToken)
                : await signIn.SignInAsync(Username.Trim(), AsksPassword ? Password : null, cancellationToken);

            if (answer is null)
            {
                return;
            }

            if (answer.IsSuccess)
            {
                Password = string.Empty;
                User = answer.Value;
                SignedIn?.Invoke(this, answer.Value);
                return;
            }

            if (answer.Code == ServerErrorCodes.InvalidCredentials)
            {
                Password = string.Empty;
            }

            Show(() => ServerMessages.SignInFailure(localizer, answer), () => ServerMessages.Reference(localizer, answer));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Show(() => localizer["signIn.cancelled"], null);
        }
        finally
        {
            IsWaitingForBrowser = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// The Microsoft sign in in the browser, then the server's exchange; null when there is nothing
    /// to exchange, with the reason already shown.
    /// </summary>
    private async Task<ServerResult<CurrentUserResponse>?> SignInWithMicrosoftAsync(CancellationToken cancellationToken)
    {
        IsWaitingForBrowser = true;
        EntraSignInResult microsoft = await entra.AcquireAccessTokenAsync(info.Entra!, cancellationToken);
        IsWaitingForBrowser = false;

        switch (microsoft)
        {
            case { Outcome: EntraSignInOutcome.Success, AccessToken: { } token }:
                return await signIn.SignInWithEntraAsync(token, cancellationToken);

            case { Outcome: EntraSignInOutcome.Cancelled }:
                Show(() => localizer["signIn.cancelled"], null);
                return null;

            default:
                string code = microsoft.ErrorCode ?? string.Empty;
                Show(() => localizer.Translate("signIn.entraFailed", code), null);
                return null;
        }
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
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Reference));
    });
}
