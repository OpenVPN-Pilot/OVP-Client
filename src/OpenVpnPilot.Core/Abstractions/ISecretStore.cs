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
/// Builds the reference under which a profile's credentials are stored.
/// </summary>
/// <remarks>
/// Centralised so the credential provider, the settings screen and the delete path all agree on the
/// same key. The realm is part of the reference because one profile can be asked for more than one,
/// for example a private key passphrase and a server login.
/// </remarks>
public static class SecretReference
{
    /// <summary>
    /// Where the passphrase of the shared library is kept.
    /// </summary>
    /// <remarks>
    /// Beside the sign ins rather than in the settings or the database, because it opens a file that
    /// holds every sign in of the team. It is not a profile's, so nothing that walks the profiles'
    /// secrets takes it for one.
    /// </remarks>
    public const string LibraryPassphrase = "library/passphrase";

    public static string ForProfile(Guid profileId, string realm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        return $"profile/{profileId:N}/{realm}";
    }

    /// <summary>
    /// True when the reference belongs to the given profile, whatever the realm.
    /// </summary>
    public static bool BelongsToProfile(string reference, Guid profileId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartsWith($"profile/{profileId:N}/", StringComparison.Ordinal);
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
}
