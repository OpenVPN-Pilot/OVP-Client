namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Supplies credentials when a server asks for them mid connection.
/// </summary>
/// <remarks>
/// The implementation decides where credentials come from: the operating system keystore for a saved
/// profile, or a prompt shown to the user. Returning null means the connection should be abandoned,
/// which is what happens when a prompt is cancelled.
/// </remarks>
public interface ICredentialProvider
{
    /// <summary>
    /// Answers a credential request for the given realm.
    /// </summary>
    /// <param name="request">What the server asked for.</param>
    /// <returns>The credentials, or null to abandon the connection.</returns>
    public Task<VpnCredentials?> RequestAsync(CredentialRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A credential request raised by OpenVPN.
/// </summary>
/// <param name="ProfileId">Which profile is connecting.</param>
/// <param name="Realm">The realm named by OpenVPN, such as Auth or a private key name.</param>
/// <param name="NeedsUsername">False when only a password is wanted, as for a private key.</param>
/// <param name="IsRetry">True when a previous attempt for this realm was rejected.</param>
/// <param name="Challenge">
/// Set when the server additionally wants a one time code. The prompt must show the server's own
/// wording, because only the server knows whether it is asking for a token, an app code or a reply
/// to a question.
/// </param>
public sealed record CredentialRequest(
    Guid ProfileId,
    string Realm,
    bool NeedsUsername,
    bool IsRetry,
    CredentialChallenge? Challenge = null);

/// <summary>
/// A one time code the server asks for alongside the password.
/// </summary>
/// <param name="Text">The server's own prompt text, shown to the user unchanged.</param>
/// <param name="EchoResponse">
/// True when the response is not secret and may be shown while it is typed, which is what the
/// server signals for a code read off a token display.
/// </param>
/// <param name="IsDynamic">
/// True for a challenge raised after a first attempt was refused, false for one presented up front.
/// A dynamic challenge already knows the user name, so the prompt only asks for the code.
/// </param>
public sealed record CredentialChallenge(string Text, bool EchoResponse, bool IsDynamic);

/// <summary>
/// Credentials for one realm.
/// </summary>
/// <remarks>
/// Held in memory only for as long as it takes to hand them to OpenVPN over the management
/// interface. They are never written to a configuration file.
/// </remarks>
/// <param name="ChallengeResponse">
/// The one time code, when the request carried a challenge. Encoded into the wire format by the
/// management client, because the encoding differs between a static and a dynamic challenge.
/// </param>
public sealed record VpnCredentials(string? Username, string Password, string? ChallengeResponse = null);
