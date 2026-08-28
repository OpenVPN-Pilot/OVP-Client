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
public sealed record CredentialRequest(Guid ProfileId, string Realm, bool NeedsUsername, bool IsRetry);

/// <summary>
/// Credentials for one realm.
/// </summary>
/// <remarks>
/// Held in memory only for as long as it takes to hand them to OpenVPN over the management
/// interface. They are never written to a configuration file.
/// </remarks>
public sealed record VpnCredentials(string? Username, string Password);
