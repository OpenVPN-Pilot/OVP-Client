using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.Data.Tagging;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Runs the two step import for the user interface.
/// </summary>
/// <remarks>
/// The examine and commit split already exists in the importer and is reused rather than repeated,
/// so the wizard and the command line agree on what counts as a duplicate and what is rejected.
///
/// Sources are expanded first: a directory contributes every configuration under it, and an archive
/// is unpacked whole rather than entry by entry, because a configuration that refers to a certificate
/// beside it can only be resolved if that certificate is unpacked too.
/// </remarks>
public interface IProfileImportService
{
    /// <summary>
    /// Turns files, directories and archives into the configuration files they contain.
    /// </summary>
    /// <param name="includeSubfolders">
    /// Whether a chosen directory contributes what is beneath it. An archive is always unpacked
    /// whole, because its own layout is not something the user arranged.
    /// </param>
    public Task<ImportSelection> ExpandAsync(
        IEnumerable<string> paths,
        bool includeSubfolders = true,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<ImportCandidate>> PrepareAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the accepted candidates, optionally tagging them.
    /// </summary>
    /// <remarks>
    /// On a server's copy each stored profile is also an upload: it is recorded for the server in
    /// the same transaction, then a synchronisation sends it at once, in batches, and what the
    /// server made of each is in the answer. Whatever could not be sent now stays here and goes
    /// when the server can be reached; one the server refused is removed again.
    /// </remarks>
    public Task<ImportCommitResult> CommitAsync(
        IReadOnlyList<ImportCandidate> candidates,
        IReadOnlyList<string> tagNames,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What committing an import did.
/// </summary>
/// <param name="Created">Profiles stored here.</param>
/// <param name="Uploads">
/// On a server's copy, what became of each stored candidate, by its source path; empty on this
/// computer's own library.
/// </param>
public sealed record ImportCommitResult(int Created, IReadOnlyDictionary<string, ImportUploadOutcome> Uploads)
{
    public bool WentToServer => Uploads.Count > 0;
}

/// <summary>
/// What became of one imported profile on its way to the server.
/// </summary>
/// <param name="Upload">The server's answer, or null when it could not be sent yet.</param>
public sealed record ImportUploadOutcome(ProfileUploadOutcome? Upload)
{
    public bool IsWaiting => Upload is null;
}

/// <summary>
/// The configuration files an import covers, plus anything that had to be unpacked to find them.
/// </summary>
/// <param name="Files">Every configuration file the selection resolved to.</param>
/// <param name="TemporaryDirectories">
/// Directories created while unpacking archives. The caller removes them once the import is done or
/// abandoned, because an unpacked configuration carries its private key.
/// </param>
public sealed record ImportSelection(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> TemporaryDirectories) : IDisposable
{
    public void Dispose()
    {
        foreach (string directory in TemporaryDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A locked file leaves the directory behind for the next cleanup rather than
                // failing an import that has already succeeded.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning as above.
            }
        }
    }
}

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class ProfileImportService : IProfileImportService
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly OvpnConfigInliner inliner;
    private readonly TimeProvider timeProvider;
    private readonly IServerProfileMaintenance? serverProfiles;
    private readonly ISyncEngine? engine;
    private readonly IOutbox? outbox;
    private readonly ILogger<ProfileImportService> logger;

    /// <param name="engine">
    /// The synchronisation of a server's copy, which sends what is imported; null on this
    /// computer's own library, where an import stays here.
    /// </param>
    public ProfileImportService(
        IDbContextFactory<PilotDbContext> contextFactory,
        OvpnConfigInliner inliner,
        TimeProvider timeProvider,
        ISyncEngine? engine = null,
        IOutbox? outbox = null,
        IServerProfileMaintenance? serverProfiles = null,
        ILogger<ProfileImportService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(inliner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (engine is not null && (outbox is null || serverProfiles is null))
        {
            throw new ArgumentException("A server's copy needs the outbox and the profile maintenance as well.", nameof(engine));
        }

        this.contextFactory = contextFactory;
        this.inliner = inliner;
        this.timeProvider = timeProvider;
        this.engine = engine;
        this.outbox = outbox;
        this.serverProfiles = serverProfiles;
        this.logger = logger ?? NullLogger<ProfileImportService>.Instance;
    }

    public Task<ImportSelection> ExpandAsync(
        IEnumerable<string> paths,
        bool includeSubfolders = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        SearchOption depth = includeSubfolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        List<string> files = [];
        List<string> temporary = [];

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*.ovpn", depth));
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string unpacked = Unpack(path);
                temporary.Add(unpacked);
                files.AddRange(Directory.EnumerateFiles(unpacked, "*.ovpn", SearchOption.AllDirectories));
                continue;
            }

            files.Add(path);
        }

        return Task.FromResult(new ImportSelection(
            files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
            temporary));
    }

    public async Task<IReadOnlyList<ImportCandidate>> PrepareAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        ProfileImporter importer = new(context, inliner, timeProvider);

        return await importer.PrepareAsync(filePaths, cancellationToken);
    }

    public async Task<ImportCommitResult> CommitAsync(
        IReadOnlyList<ImportCandidate> candidates,
        IReadOnlyList<string> tagNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(tagNames);

        IReadOnlyList<Profile> created;

        await using (PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            // One transaction, so a profile and the note that it still has to be uploaded are
            // written together or not at all.
            await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            ProfileImporter importer = new(context, inliner, timeProvider);

            created = await importer.CommitAsync(candidates, cancellationToken);

            if (created.Count > 0 && tagNames.Count > 0)
            {
                await ApplyTagsAsync(context, created, tagNames, cancellationToken);
            }

            if (outbox is not null && engine is not null)
            {
                foreach (Profile profile in created)
                {
                    await outbox.StageAsync(context, PendingChangeKind.ProfileCreate, profile.Id, cancellationToken: cancellationToken);
                }

                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (engine is null || created.Count == 0)
        {
            return new ImportCommitResult(created.Count, new Dictionary<string, ImportUploadOutcome>());
        }

        return await UploadAsync(created, cancellationToken);
    }

    /// <summary>
    /// Sends what was just stored, and removes again what the server refused.
    /// </summary>
    private async Task<ImportCommitResult> UploadAsync(IReadOnlyList<Profile> created, CancellationToken cancellationToken)
    {
        SyncCycleResult cycle = await engine!.SynchronizeAsync(cancellationToken);

        Dictionary<string, ImportUploadOutcome> outcomes = new(StringComparer.Ordinal);
        List<Guid> refused = [];

        foreach (Profile profile in created)
        {
            ProfileUploadOutcome? upload = cycle.Uploads.GetValueOrDefault(profile.Id);
            outcomes[profile.SourcePath ?? profile.Id.ToString()] = new ImportUploadOutcome(upload);

            if (upload?.Kind == ProfileUploadKind.Rejected)
            {
                refused.Add(profile.Id);
            }
        }

        if (refused.Count > 0)
        {
            // The person sees why in the review list; a copy the server will never take would only
            // stay here as a profile nobody else has.
            await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.PendingChanges.Where(change => change.EntityId != null && refused.Contains(change.EntityId.Value)).ExecuteDeleteAsync(cancellationToken);
            await context.Profiles.Where(profile => refused.Contains(profile.Id)).ExecuteDeleteAsync(cancellationToken);
            await context.Tags.Where(tag => !tag.Profiles.Any()).ExecuteDeleteAsync(cancellationToken);
            await serverProfiles!.DeleteSecretsAsync(refused, cancellationToken);
        }

        int uploaded = outcomes.Values.Count(outcome => outcome.Upload?.Kind == ProfileUploadKind.Created);
        int duplicates = outcomes.Values.Count(outcome => outcome.Upload?.Kind == ProfileUploadKind.Duplicate);
        int waiting = outcomes.Values.Count(outcome => outcome.IsWaiting);

        ServerLibraryLog.Imported(logger, created.Count, uploaded, duplicates, refused.Count, waiting);

        return new ImportCommitResult(created.Count - refused.Count, outcomes);
    }

    private static async Task ApplyTagsAsync(
        PilotDbContext context,
        IReadOnlyList<Profile> created,
        IReadOnlyList<string> tagNames,
        CancellationToken cancellationToken)
    {
        TagCatalogue tags = await TagCatalogue.LoadAsync(context, cancellationToken);

        foreach (string name in tagNames
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Tag tag = tags.Resolve(name);

            foreach (Profile profile in created)
            {
                context.ProfileTags.Add(new ProfileTag
                {
                    ProfileId = profile.Id,
                    TagId = tag.Id,
                    Tag = tag,
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Unpacks an archive into a private temporary directory.
    /// </summary>
    /// <remarks>
    /// The framework's extraction refuses entries whose path escapes the destination, so an archive
    /// cannot write outside the directory it was given.
    /// </remarks>
    private static string Unpack(string archivePath)
    {
        string directory = Directory.CreateTempSubdirectory("ovp-import-").FullName;
        ZipFile.ExtractToDirectory(archivePath, directory);
        return directory;
    }
}
