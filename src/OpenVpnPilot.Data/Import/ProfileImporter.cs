using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.Data.Import;

/// <summary>
/// Turns configuration files into stored profiles.
/// </summary>
/// <remarks>
/// Import is deliberately a two step operation. <see cref="PrepareAsync"/> reports what would happen
/// so the user can review duplicates and problems, and <see cref="CommitAsync"/> writes the accepted
/// candidates. Nothing is imported silently.
/// </remarks>
public sealed class ProfileImporter
{
    private readonly PilotDbContext context;
    private readonly OvpnConfigInliner inliner;
    private readonly TimeProvider timeProvider;

    public ProfileImporter(PilotDbContext context, OvpnConfigInliner inliner, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inliner);

        this.context = context;
        this.inliner = inliner;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Examines files without writing anything.
    /// </summary>
    public async Task<IReadOnlyList<ImportCandidate>> PrepareAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        List<ImportCandidate> candidates = [];

        // Duplicates within the selection matter as much as duplicates against the store.
        HashSet<string> seenInThisBatch = new(StringComparer.Ordinal);

        foreach (string path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(await ExamineAsync(path, seenInThisBatch, cancellationToken));
        }

        return candidates;
    }

    /// <summary>
    /// Stores the candidates that were accepted.
    /// </summary>
    /// <returns>The profiles that were created.</returns>
    public async Task<IReadOnlyList<Profile>> CommitAsync(
        IEnumerable<ImportCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        DateTimeOffset now = timeProvider.GetUtcNow();
        List<Profile> created = [];

        foreach (ImportCandidate candidate in candidates)
        {
            if (candidate.Outcome != ImportOutcome.Importable || candidate.Configuration is null)
            {
                continue;
            }

            Profile profile = new()
            {
                Name = candidate.SuggestedName,
                Configuration = candidate.Configuration,
                ContentHash = candidate.ContentHash!,
                Source = ProfileSource.Imported,
                SourcePath = candidate.SourcePath,
                RemoteHost = candidate.RemoteHost,
                RemotePort = candidate.RemotePort,
                Protocol = candidate.Protocol,
                RequiresCredentials = candidate.RequiresCredentials,
                HasUnsupportedOptions = candidate.UnsupportedOptions.Count > 0,
                IsSelfContained = candidate.IsSelfContained,
                CreatedAt = now,
                UpdatedAt = now,
            };

            context.Profiles.Add(profile);
            created.Add(profile);
        }

        await context.SaveChangesAsync(cancellationToken);
        return created;
    }

    private async Task<ImportCandidate> ExamineAsync(
        string path,
        HashSet<string> seenInThisBatch,
        CancellationToken cancellationToken)
    {
        string name = Path.GetFileNameWithoutExtension(path);

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException exception)
        {
            return ImportCandidate.Unreadable(path, name, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ImportCandidate.Unreadable(path, name, exception.Message);
        }

        OvpnInlineResult inlined = await inliner.InlineAsync(
            raw,
            Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty,
            cancellationToken);

        OvpnConfiguration configuration = OvpnConfigParser.Parse(inlined.Content);
        string hash = ComputeHash(inlined.Content);

        OvpnRemote? remote = configuration.Remotes.Count > 0 ? configuration.Remotes[0] : null;

        List<string> unsupported = configuration.ScriptOptions
            .Select(directive => directive.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        List<string> missingFiles = inlined.Failures
            .Select(failure => failure.Reference)
            .ToList();

        ImportOutcome outcome = ImportOutcome.Importable;
        string? detail = null;

        if (!configuration.HasServerVerification)
        {
            outcome = ImportOutcome.Rejected;
            detail = "The configuration has no ca, capath, pkcs12 or peer-fingerprint directive, so "
                + "OpenVPN would refuse to start.";
        }
        else if (remote is null)
        {
            outcome = ImportOutcome.Rejected;
            detail = "The configuration names no remote server.";
        }
        else if (!seenInThisBatch.Add(hash))
        {
            outcome = ImportOutcome.DuplicateInSelection;
            detail = "An identical configuration appears earlier in this selection.";
        }
        else if (await context.Profiles.AnyAsync(p => p.ContentHash == hash, cancellationToken))
        {
            outcome = ImportOutcome.DuplicateInStore;
            detail = "An identical configuration is already stored.";
        }

        return new ImportCandidate
        {
            SourcePath = path,
            SuggestedName = name,
            Outcome = outcome,
            Detail = detail,
            Configuration = inlined.Content,
            ContentHash = hash,
            RemoteHost = remote?.Host,
            RemotePort = remote?.Port,
            Protocol = remote?.Protocol.ToString().ToLowerInvariant(),
            RequiresCredentials = configuration.RequiresUserCredentials,
            IsSelfContained = configuration.IsSelfContained,
            UnsupportedOptions = unsupported,
            MissingFiles = missingFiles,
        };
    }

    internal static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLower(CultureInfo.InvariantCulture);
}

/// <summary>
/// What importing one file would produce.
/// </summary>
public sealed record ImportCandidate
{
    public required string SourcePath { get; init; }

    public required string SuggestedName { get; init; }

    public required ImportOutcome Outcome { get; init; }

    /// <summary>
    /// Why the outcome is not importable, or null when it is.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The configuration after inlining. Null when the file could not be read.
    /// </summary>
    public string? Configuration { get; init; }

    public string? ContentHash { get; init; }

    public string? RemoteHost { get; init; }

    public int? RemotePort { get; init; }

    public string? Protocol { get; init; }

    public bool RequiresCredentials { get; init; }

    public bool IsSelfContained { get; init; }

    /// <summary>
    /// Script directives the interactive service refuses for unauthorised callers.
    /// </summary>
    public IReadOnlyList<string> UnsupportedOptions { get; init; } = [];

    /// <summary>
    /// Referenced files that could not be found, so the profile is not self contained.
    /// </summary>
    public IReadOnlyList<string> MissingFiles { get; init; } = [];

    internal static ImportCandidate Unreadable(string path, string name, string reason) =>
        new()
        {
            SourcePath = path,
            SuggestedName = name,
            Outcome = ImportOutcome.Unreadable,
            Detail = reason,
        };
}

public enum ImportOutcome
{
    /// <summary>
    /// The file can be stored as a new profile.
    /// </summary>
    Importable,

    /// <summary>
    /// Another file in the same selection has identical content.
    /// </summary>
    DuplicateInSelection,

    /// <summary>
    /// A profile with identical content is already stored.
    /// </summary>
    DuplicateInStore,

    /// <summary>
    /// The file could not be read.
    /// </summary>
    Unreadable,

    /// <summary>
    /// The configuration would not work, so importing it would only defer the failure.
    /// </summary>
    Rejected,
}
