namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Keeps credentials outside the profile database, in whatever protected storage the platform offers.
/// </summary>
/// <remarks>
/// The database holds a reference only, so it can be copied, exported or backed up without carrying
/// passwords. The reference is opaque to the store: it is a key, not a path, and the implementation
/// decides how it maps onto the underlying storage.
/// </remarks>
public interface ISecretStore
{
    /// <summary>
    /// True when this machine can protect secrets at all. A store that answers false must not be
    /// offered as a "remember me" option.
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// Reads a stored secret, or null when nothing is stored under that reference.
    /// </summary>
    public Task<StoredSecret?> TryReadAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a stored secret and says why there is none: nothing is stored, or the store refused.
    /// </summary>
    /// <remarks>
    /// <see cref="TryReadAsync"/> answers null for both, which suits a sign in the person can simply
    /// type again. It does not suit a server session: one the keychain would not hand over still
    /// exists, and reading it as gone asks the person to sign in for nothing.
    /// </remarks>
    public Task<SecretRead> ReadAsync(string reference, CancellationToken cancellationToken = default);

    public Task WriteAsync(string reference, StoredSecret secret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a secret. Removing one that does not exist is not an error.
    /// </summary>
    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every reference currently held, so a settings screen can list and clear them.
    /// </summary>
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes everything this store holds.
    /// </summary>
    /// <returns>How many secrets were removed.</returns>
    public Task<int> ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A credential pair as it is held in protected storage.
/// </summary>
/// <param name="Username">Null for a realm that only wants a password, such as a private key.</param>
/// <param name="Password">The secret itself. Never written to the database or to a log.</param>
public sealed record StoredSecret(string? Username, string Password);

/// <summary>
/// How reading one secret ended.
/// </summary>
public enum SecretReadOutcome
{
    Found,

    /// <summary>
    /// Nothing is stored under the reference.
    /// </summary>
    Absent,

    /// <summary>
    /// Something may be stored, but the store did not hand it over: the person denied or dismissed
    /// the keychain's question, or the file could not be opened. Asking again later may succeed.
    /// </summary>
    Refused,
}

/// <param name="Outcome">How the read ended.</param>
/// <param name="Secret">The secret when it was found, otherwise null.</param>
public sealed record SecretRead(SecretReadOutcome Outcome, StoredSecret? Secret)
{
    public static SecretRead Absent { get; } = new(SecretReadOutcome.Absent, null);

    public static SecretRead Refused { get; } = new(SecretReadOutcome.Refused, null);

    public static SecretRead Found(StoredSecret secret) => new(SecretReadOutcome.Found, secret);
}

/// <summary>
/// Builds the references under which credentials are stored: a profile's sign ins, and the refresh
/// token of a server this machine is signed in to.
/// </summary>
/// <remarks>
/// Centralised so the credential provider, the settings screen and the delete path all agree on the
/// same key. The realm is part of a profile's reference because one profile can be asked for more
/// than one, for example a private key passphrase and a server login.
///
/// The two kinds share one store and must never be mistaken for each other. Everything that walks
/// the store looking for a profile's sign ins (an export, "sign in again", counting them) goes
/// through <see cref="TryParse"/> or <see cref="BelongsToProfile"/>, and both refuse a server
/// reference, so a refresh token can never travel in a package or be counted as a profile's sign in.
/// </remarks>
public static class SecretReference
{
    private const string ProfilePrefix = "profile/";
    private const string ServerPrefix = "server/";
    private const string RefreshSuffix = "/refresh";
    private const string EntraSuffix = "/entra";

    /// <summary>
    /// How long a server key is: lower case hex of 16 bytes.
    /// </summary>
    private const int ServerKeyLength = 32;

    public static string ForProfile(Guid profileId, string realm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        return $"{ProfilePrefix}{profileId:N}/{realm}";
    }

    /// <summary>
    /// The reference of the refresh token for one server.
    /// </summary>
    /// <param name="serverKey">The server's key, lower case hex of 16 bytes.</param>
    /// <exception cref="ArgumentException">The key is not in that form.</exception>
    public static string ForServerRefreshToken(string serverKey)
    {
        if (!IsServerKey(serverKey))
        {
            throw new ArgumentException("A server key is 32 lower case hexadecimal digits.", nameof(serverKey));
        }

        return ServerPrefix + serverKey + RefreshSuffix;
    }

    /// <summary>
    /// The reference of what a Microsoft sign in to one server left behind, from which it is renewed
    /// without the person.
    /// </summary>
    /// <param name="serverKey">The server's key, lower case hex of 16 bytes.</param>
    /// <exception cref="ArgumentException">The key is not in that form.</exception>
    public static string ForServerEntraState(string serverKey)
    {
        if (!IsServerKey(serverKey))
        {
            throw new ArgumentException("A server key is 32 lower case hexadecimal digits.", nameof(serverKey));
        }

        return ServerPrefix + serverKey + EntraSuffix;
    }

    /// <summary>
    /// Reads a server refresh token reference back into the server key it was built from.
    /// </summary>
    /// <returns>False for any other reference, including every profile reference.</returns>
    public static bool TryParseServerRefreshToken(string reference, out string serverKey)
    {
        ArgumentNullException.ThrowIfNull(reference);

        serverKey = string.Empty;

        if (!reference.StartsWith(ServerPrefix, StringComparison.Ordinal)
            || !reference.EndsWith(RefreshSuffix, StringComparison.Ordinal)
            || reference.Length != ServerPrefix.Length + ServerKeyLength + RefreshSuffix.Length)
        {
            return false;
        }

        string key = reference.Substring(ServerPrefix.Length, ServerKeyLength);

        if (!IsServerKey(key))
        {
            return false;
        }

        serverKey = key;
        return true;
    }

    /// <summary>
    /// True when the reference is a profile's sign in, of any profile and realm.
    /// </summary>
    /// <remarks>
    /// What a person means by "stored sign ins": the refresh token of a server is a credential too,
    /// but it is not one they typed for a profile, and counting it as one would be wrong.
    /// </remarks>
    public static bool IsProfileReference(string reference) => TryParse(reference, out _, out _);

    /// <summary>
    /// True when the reference belongs to the given profile, whatever the realm.
    /// </summary>
    public static bool BelongsToProfile(string reference, Guid profileId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartsWith($"{ProfilePrefix}{profileId:N}/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a reference back into the profile and realm it was built from.
    /// </summary>
    /// <remarks>
    /// Needed by anything that walks the whole store rather than looking one entry up, such as
    /// collecting the credentials that belong to a set of profiles for an export.
    /// </remarks>
    /// <returns>False for a reference this scheme did not produce.</returns>
    public static bool TryParse(string reference, out Guid profileId, out string realm)
    {
        ArgumentNullException.ThrowIfNull(reference);

        profileId = Guid.Empty;
        realm = string.Empty;

        // profile / <32 hex digits> / <realm>, and a realm may contain slashes of its own.
        string[] parts = reference.Split('/', 3);

        if (parts.Length != 3
            || !string.Equals(parts[0], "profile", StringComparison.Ordinal)
            || parts[2].Length == 0
            || !Guid.TryParseExact(parts[1], "N", out profileId))
        {
            profileId = Guid.Empty;
            return false;
        }

        realm = parts[2];
        return true;
    }

    private static bool IsServerKey(string? serverKey) =>
        serverKey is { Length: ServerKeyLength }
        && serverKey.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}
