using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
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
    /// <returns>How many profiles were created.</returns>
    public Task<int> CommitAsync(
        IReadOnlyList<ImportCandidate> candidates,
        IReadOnlyList<string> tagNames,
        CancellationToken cancellationToken = default);
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

    public ProfileImportService(
        IDbContextFactory<PilotDbContext> contextFactory,
        OvpnConfigInliner inliner,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(inliner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.contextFactory = contextFactory;
        this.inliner = inliner;
        this.timeProvider = timeProvider;
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

    public async Task<int> CommitAsync(
        IReadOnlyList<ImportCandidate> candidates,
        IReadOnlyList<string> tagNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(tagNames);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        ProfileImporter importer = new(context, inliner, timeProvider);

        IReadOnlyList<Profile> created = await importer.CommitAsync(candidates, cancellationToken);

        if (created.Count > 0 && tagNames.Count > 0)
        {
            await ApplyTagsAsync(context, created, tagNames, cancellationToken);
        }

        return created.Count;
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
