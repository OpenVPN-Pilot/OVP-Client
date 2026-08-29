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
}
