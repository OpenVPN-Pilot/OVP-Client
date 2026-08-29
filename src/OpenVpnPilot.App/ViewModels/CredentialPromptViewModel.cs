using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// Asks the user for the credentials a server requested.
/// </summary>
/// <remarks>
/// The dialog adapts to what the server actually wants. A private key asks for a passphrase only, a
/// server login asks for both fields, and a one time code adds a third. A dynamic challenge does not
/// ask for the password again, because the protocol answers it with the challenge state rather than
/// with the original credentials.
/// </remarks>
public sealed partial class CredentialPromptViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILocalizer localizer;
    private readonly string profileName;

    public CredentialPromptViewModel(
        ILocalizer localizer,
        string profileName,
        CredentialRequest request,
        StoredSecret? stored,
        bool canRemember,
        bool rememberByDefault)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(request);

        this.localizer = localizer;
        this.profileName = profileName;

        Realm = request.Realm;
        NeedsUsername = request.NeedsUsername;
        IsRetry = request.IsRetry;
        Challenge = request.Challenge;
        CanRemember = canRemember;
        Remember = canRemember && rememberByDefault;

        Username = stored?.Username ?? string.Empty;
        Password = stored?.Password ?? string.Empty;
    }

    public string Realm { get; }

    public bool NeedsUsername { get; }

    /// <summary>
    /// True when a previous attempt was refused, so the prompt can say so.
    /// </summary>
    public bool IsRetry { get; }

    public CredentialChallenge? Challenge { get; }

    /// <summary>
    /// False when no protected storage is available, in which case remembering is not offered.
    /// </summary>
    public bool CanRemember { get; }

    public bool HasChallenge => Challenge is not null;

    public string ChallengeText => Challenge?.Text ?? string.Empty;

    /// <summary>
    /// A code the server marked as not secret is shown while it is typed, which is what someone
    /// copying digits off a token display expects.
    /// </summary>
    public char? ChallengeMask => Challenge is { EchoResponse: true } ? null : '•';

    /// <summary>
    /// A dynamic challenge is answered with the server's state rather than the password, so asking
    /// for the password again would be misleading.
    /// </summary>
    public bool NeedsPassword => Challenge is not { IsDynamic: true };

    public string Title => NeedsUsername
        ? localizer.Translate("credentials.signInTitle", profileName)
        : localizer.Translate("credentials.unlockTitle", Realm, profileName);

    [ObservableProperty]
    public partial string Username { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; }

    [ObservableProperty]
    public partial string ChallengeResponse { get; set; } = string.Empty;

    /// <summary>
    /// Whether the credentials should be kept for the next connection.
    /// </summary>
    [ObservableProperty]
    public partial bool Remember { get; set; }

    /// <summary>
    /// Completes with true when the user confirmed and false when they cancelled.
    /// </summary>
    public Task<bool> Result => completion.Task;

    [RelayCommand]
    private void Confirm() => completion.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => completion.TrySetResult(false);

    /// <summary>
    /// Called when the window closes without either button being used.
    /// </summary>
    public void Abandon() => completion.TrySetResult(false);

    /// <summary>
    /// True when the user supplied enough to attempt a connection.
    /// </summary>
    public bool HasUsableAnswer =>
        (!NeedsPassword || Password.Length > 0)
        && (Challenge is not { } challenge || !challenge.IsDynamic || ChallengeResponse.Length > 0);

    public VpnCredentials ToCredentials() => new(
        NeedsUsername ? Username : null,
        Password,
        HasChallenge ? ChallengeResponse : null);
}
