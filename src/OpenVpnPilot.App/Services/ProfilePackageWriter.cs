using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Packaging;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Writes and reads portable packages for the user interface.
/// </summary>
/// <remarks>
/// Exists so the export and import screens never touch a database context directly, in the same way
/// the profile store keeps Entity Framework out of the view models.
///
/// Credentials are the reason this sits in the application layer rather than in the data layer. They
/// live in the operating system keystore, not in the database, so collecting them for an export and
/// putting them back after an import is the only part of a package that needs a platform facility.
/// </remarks>
public interface IProfilePackageWriter
{
    /// <summary>
    /// Writes the given profiles to a package.
    /// </summary>
    /// <param name="passphrase">
    /// Required. A package is one file carrying private keys, so it is encrypted or it is not
    /// written.
    /// </param>
    /// <param name="includeCredentials">
    /// Carries the stored user names and passwords for those profiles as well, so a set can be
    /// handed over ready to connect. Only ever with a passphrase, which is why it is not offered
    /// separately from one.
    /// </param>
    public Task<PackageWriteResult> WriteAsync(
        string path,
        IReadOnlyCollection<Guid> profileIds,
        string passphrase,
        bool includeCredentials = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a package and writes it into the store, restoring any credentials it carries.
    /// </summary>
    public Task<PackageImportResult> ApplyAsync(
        string path,
        string? passphrase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many stored credentials each profile has, so a screen can say whether offering to carry
    /// them would mean anything.
    /// </summary>
    /// <remarks>
    /// Read once for the whole store rather than per selection: the answer changes only when the
    /// user signs in somewhere, and the export screen recounts on every tick of a checkbox.
    /// </remarks>
    public Task<IReadOnlyDictionary<Guid, int>> CountCredentialsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a package ended up containing.
/// </summary>
public sealed record PackageWriteResult(int Profiles, int Credentials);

/// <summary>
/// What reading a package changed.
/// </summary>
/// <param name="Added">Profiles created.</param>
/// <param name="Skipped">Profiles the store already held, recognised by their contents.</param>
/// <param name="Hotkeys">Shortcut bindings created.</param>
/// <param name="Credentials">Credentials written into the keystore.</param>
public sealed record PackageImportResult(int Added, int Skipped, int Hotkeys, int Credentials);

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class ProfilePackageWriter : IProfilePackageWriter
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly ISecretStore secrets;
    private readonly TimeProvider timeProvider;

    public ProfilePackageWriter(
        IDbContextFactory<PilotDbContext> contextFactory,
        ISecretStore secrets,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.contextFactory = contextFactory;
        this.secrets = secrets;
        this.timeProvider = timeProvider;
    }

    public async Task<PackageWriteResult> WriteAsync(
        string path,
        IReadOnlyCollection<Guid> profileIds,
        string passphrase,
        bool includeCredentials = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profileIds);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        IReadOnlyList<PackagedCredential> credentials = includeCredentials
            ? await ReadCredentialsAsync(profileIds, cancellationToken)
            : [];

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        ProfilePackageContent content = await new ProfilePackageService(context, timeProvider)
            .CreateAsync(profileIds, credentials, cancellationToken);

        await ProfilePackageFile.WriteAsync(path, content, passphrase, cancellationToken);

        return new PackageWriteResult(content.Profiles.Count, credentials.Count);
    }

    public async Task<PackageImportResult> ApplyAsync(
        string path,
        string? passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ProfilePackageContent content = await ProfilePackageFile.ReadAsync(path, passphrase, cancellationToken);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        PackageApplyResult result = await new ProfilePackageService(context, timeProvider)
            .ApplyAsync(content, cancellationToken);

        int restored = await RestoreCredentialsAsync(result.Credentials, cancellationToken);

        return new PackageImportResult(result.Added, result.Skipped, result.Hotkeys, restored);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        Dictionary<Guid, int> counts = [];

        if (!secrets.IsAvailable)
        {
            return counts;
        }

        foreach (string reference in await secrets.ListAsync(cancellationToken))
        {
            if (SecretReference.TryParse(reference, out Guid profileId, out _))
            {
                counts[profileId] = counts.GetValueOrDefault(profileId) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Collects the stored credentials belonging to the profiles being written.
    /// </summary>
    /// <remarks>
    /// A stored secret is protected for one user on one machine, so it cannot simply be copied. It
    /// is read back out here and re-protected by the package's own passphrase, which is the only
    /// form the recipient can open.
    /// </remarks>
    private async Task<IReadOnlyList<PackagedCredential>> ReadCredentialsAsync(
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken cancellationToken)
    {
        if (!secrets.IsAvailable)
        {
            return [];
        }

        HashSet<Guid> wanted = [.. profileIds];
        List<PackagedCredential> credentials = [];

        foreach (string reference in await secrets.ListAsync(cancellationToken))
        {
            if (!SecretReference.TryParse(reference, out Guid profileId, out string realm)
                || !wanted.Contains(profileId))
            {
                continue;
            }

            StoredSecret? secret = await secrets.TryReadAsync(reference, cancellationToken);

            if (secret is not null)
            {
                credentials.Add(new PackagedCredential(profileId, realm, secret.Username, secret.Password));
            }
        }

        return credentials;
    }

    /// <summary>
    /// Puts the credentials a package carried into protected storage.
    /// </summary>
    /// <remarks>
    /// Nothing is written when the machine cannot protect a secret at all, because writing it
    /// anywhere else would be a leak the user never agreed to.
    /// </remarks>
    private async Task<int> RestoreCredentialsAsync(
        IReadOnlyList<PackagedCredential> credentials,
        CancellationToken cancellationToken)
    {
        if (!secrets.IsAvailable || credentials.Count == 0)
        {
            return 0;
        }

        foreach (PackagedCredential credential in credentials)
        {
            await secrets.WriteAsync(
                SecretReference.ForProfile(credential.ProfileId, credential.Realm),
                new StoredSecret(credential.Username, credential.Password),
                cancellationToken);
        }

        return credentials.Count;
    }
}
