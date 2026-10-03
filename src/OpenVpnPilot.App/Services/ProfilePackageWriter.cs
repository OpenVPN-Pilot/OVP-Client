using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Packaging;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Writes and reads portable packages for the user interface.
/// </summary>
/// <remarks>
/// Exists so the export and import screens never touch a database context directly, in the same way
/// the profile store keeps Entity Framework out of the view models.
///
/// Credentials and settings are the reason this sits in the application layer rather than in the
/// data layer. Credentials live in the operating system keystore, not in the database, and settings
/// live in their own file, so collecting either for an export and putting it back after an import
/// needs something only the application has.
///
/// Importing is two steps. Opening decrypts the package and says what it holds; applying takes what
/// the person chose from that. The opened contents stay in memory between the two and nowhere else.
/// </remarks>
public interface IProfilePackageWriter
{
    /// <summary>
    /// Writes a package of what the request names.
    /// </summary>
    /// <param name="passphrase">
    /// Required. A package is one file carrying private keys, so it is encrypted or it is not
    /// written.
    /// </param>
    public Task<PackageWriteResult> WriteAsync(
        string path,
        PackageExportRequest request,
        string passphrase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a package and describes what it holds, measured against this store.
    /// </summary>
    public Task<OpenedPackage> OpenAsync(
        string path,
        string? passphrase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes what was chosen from an opened package into the store, the keystore and the settings.
    /// </summary>
    public Task<PackageImportResult> ApplyAsync(
        OpenedPackage package,
        PackageImportChoice choice,
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
/// What an export should carry.
/// </summary>
/// <param name="ProfileIds">The profiles to write.</param>
public sealed record PackageExportRequest(IReadOnlyCollection<Guid> ProfileIds)
{
    /// <summary>
    /// Carries the stored user names and passwords for those profiles as well, so a set can be
    /// handed over ready to connect.
    /// </summary>
    public bool IncludeCredentials { get; init; }

    public bool IncludeHotkeys { get; init; } = true;

    /// <summary>
    /// Carries the settings, without the ones that describe this machine.
    /// </summary>
    public bool IncludeSettings { get; init; }
}

/// <summary>
/// What to take from an opened package.
/// </summary>
/// <param name="ProfileIds">The packaged profiles to take, by their identifier in the package.</param>
public sealed record PackageImportChoice(IReadOnlyCollection<Guid> ProfileIds)
{
    public bool IncludeCredentials { get; init; } = true;

    public bool IncludeHotkeys { get; init; } = true;

    public bool IncludeSettings { get; init; }
}

/// <summary>
/// A package that has been decrypted, and what it holds.
/// </summary>
public sealed class OpenedPackage
{
    internal OpenedPackage(string path, ProfilePackageContent content, PackagePreview preview)
    {
        Path = path;
        Content = content;
        Preview = preview;
    }

    public string Path { get; }

    public DateTimeOffset CreatedAt => Content.CreatedAt;

    public string WrittenBy => Content.WrittenBy;

    public PackagePreview Preview { get; }

    internal ProfilePackageContent Content { get; }
}

/// <summary>
/// What a package ended up containing.
/// </summary>
public sealed record PackageWriteResult(int Profiles, int Credentials, int Hotkeys, bool Settings);

/// <summary>
/// What reading a package changed.
/// </summary>
/// <param name="Added">Profiles created.</param>
/// <param name="Skipped">Profiles the store already held, recognised by their contents.</param>
/// <param name="Hotkeys">Shortcut bindings created.</param>
/// <param name="Credentials">Credentials written into the keystore.</param>
/// <param name="Settings">True when the settings were replaced by the ones the package carried.</param>
public sealed record PackageImportResult(int Added, int Skipped, int Hotkeys, int Credentials, bool Settings = false);

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class ProfilePackageWriter : IProfilePackageWriter
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly ISecretStore secrets;
    private readonly ISettingsService settings;
    private readonly TimeProvider timeProvider;
    private readonly ISyncEngine? engine;
    private readonly IOutbox? outbox;
    private readonly ILogger<ProfilePackageWriter> logger;

    /// <param name="engine">
    /// The synchronisation of a server's copy. With it, applying a package is an import for the
    /// server: every profile it adds is recorded as a creation, every sign in it carries as one to
    /// share, and the synchronisation is asked to send them. Null on this computer's own library.
    /// </param>
    public ProfilePackageWriter(
        IDbContextFactory<PilotDbContext> contextFactory,
        ISecretStore secrets,
        ISettingsService settings,
        TimeProvider timeProvider,
        ISyncEngine? engine = null,
        IOutbox? outbox = null,
        ILogger<ProfilePackageWriter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (engine is not null && outbox is null)
        {
            throw new ArgumentException("A server's copy needs the outbox as well.", nameof(outbox));
        }

        this.contextFactory = contextFactory;
        this.secrets = secrets;
        this.settings = settings;
        this.timeProvider = timeProvider;
        this.engine = engine;
        this.outbox = outbox;
        this.logger = logger ?? NullLogger<ProfilePackageWriter>.Instance;
    }

    public async Task<PackageWriteResult> WriteAsync(
        string path,
        PackageExportRequest request,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        IReadOnlyList<PackagedCredential> credentials = request.IncludeCredentials
            ? await ReadCredentialsAsync(request.ProfileIds, cancellationToken)
            : [];

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        ProfilePackageContent content = await new ProfilePackageService(context, timeProvider).CreateAsync(
            request.ProfileIds,
            credentials,
            request.IncludeHotkeys,
            request.IncludeSettings ? PilotSettingsTransfer.Export(settings.Current) : null,
            cancellationToken);

        await ProfilePackageFile.WriteAsync(path, content, passphrase, cancellationToken);

        return new PackageWriteResult(
            content.Profiles.Count,
            credentials.Count,
            content.Hotkeys.Count,
            content.Settings is not null);
    }

    public async Task<OpenedPackage> OpenAsync(
        string path,
        string? passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ProfilePackageContent content = await ProfilePackageFile.ReadAsync(path, passphrase, cancellationToken);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        PackagePreview preview = await new ProfilePackageService(context, timeProvider)
            .PreviewAsync(content, cancellationToken);

        return new OpenedPackage(path, content, preview);
    }

    public async Task<PackageImportResult> ApplyAsync(
        OpenedPackage package,
        PackageImportChoice choice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(choice);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // On a server's copy the profiles and the notes that they still have to be uploaded are
        // written together or not at all.
        HashSet<Guid> before = engine is null
            ? []
            : await context.Profiles.Select(profile => profile.Id).ToHashSetAsync(cancellationToken);

        await using IDbContextTransaction? transaction = engine is null
            ? null
            : await context.Database.BeginTransactionAsync(cancellationToken);

        PackageApplyResult result = await new ProfilePackageService(context, timeProvider).ApplyAsync(
            package.Content,
            new PackageApplyOptions
            {
                ProfileIds = choice.ProfileIds.ToHashSet(),
                IncludeCredentials = choice.IncludeCredentials,
                IncludeHotkeys = choice.IncludeHotkeys,
            },
            cancellationToken);

        if (transaction is not null)
        {
            await RecordForServerAsync(context, before, result, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        int restored = await RestoreCredentialsAsync(result.Credentials, cancellationToken);

        if (engine is not null)
        {
            // What the package carried is offered to the vault like a sign in typed here; the
            // server keeps what it already has.
            if (restored > 0)
            {
                foreach (PackagedCredential credential in result.Credentials)
                {
                    await outbox!.RecordAsync(PendingChangeKind.VaultAdd, credential.ProfileId, credential.Realm, cancellationToken);
                }
            }

            ServerLibraryLog.PackageApplied(logger, result.Added, restored);
            engine.RequestSync();
        }

        bool settingsApplied = false;

        if (choice.IncludeSettings
            && package.Content.Settings is { } carried
            && PilotSettingsTransfer.Import(carried, settings.Current) is { } incoming)
        {
            await settings.ReplaceAsync(incoming, cancellationToken);
            settingsApplied = true;
        }

        return new PackageImportResult(result.Added, result.Skipped, result.Hotkeys, restored, settingsApplied);
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
    /// Records what applying the package changed that the server has to hear about.
    /// </summary>
    private async Task RecordForServerAsync(
        PilotDbContext context,
        HashSet<Guid> before,
        PackageApplyResult result,
        CancellationToken cancellationToken)
    {
        List<Profile> added = await context.Profiles
            .AsNoTracking()
            .Where(profile => !before.Contains(profile.Id))
            .ToListAsync(cancellationToken);

        foreach (Profile profile in added)
        {
            await outbox!.StageAsync(context, PendingChangeKind.ProfileCreate, profile.Id, cancellationToken: cancellationToken);
        }

        if (added.Any(profile => profile.IsFavourite || profile.FavouriteSlot is not null))
        {
            await outbox!.StageAsync(context, PendingChangeKind.Favourites, cancellationToken: cancellationToken);
        }

        if (result.Hotkeys > 0)
        {
            await outbox!.StageAsync(context, PendingChangeKind.Hotkeys, cancellationToken: cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
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
