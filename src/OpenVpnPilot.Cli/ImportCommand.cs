using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Imports configuration files from a file, a directory or an archive into the local store.
/// </summary>
/// <remarks>
/// The default is a dry run. Nothing is written until the caller passes the commit flag, which
/// mirrors how the import wizard in the application behaves.
/// </remarks>
internal static class ImportCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? source = ArgumentReader.FirstPositional(args);

        if (source is null)
        {
            Console.Error.WriteLine("A file, directory or archive is required.");
            return 1;
        }

        source = Path.GetFullPath(source);
        bool commit = args.Contains("--commit", StringComparer.Ordinal);

        string? unpacked = null;

        try
        {
            string[] files = Expand(source, ref unpacked);

            if (files.Length == 0)
            {
                Console.Error.WriteLine($"No .ovpn files found under {source}");
                return 1;
            }

            return await ExamineAsync(args, source, files, commit);
        }
        finally
        {
            // An unpacked archive contains private keys, so it does not outlive the command.
            if (unpacked is not null && Directory.Exists(unpacked))
            {
                try
                {
                    Directory.Delete(unpacked, recursive: true);
                }
                catch (IOException)
                {
                    Console.Error.WriteLine($"The temporary directory {unpacked} could not be removed.");
                }
            }
        }
    }

    private static string[] Expand(string source, ref string? unpacked)
    {
        if (Directory.Exists(source))
        {
            return Directory.GetFiles(source, "*.ovpn", SearchOption.AllDirectories).Order().ToArray();
        }

        if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // The whole archive is unpacked, not only the configurations, because a configuration
            // that refers to a certificate beside it can only be resolved if that file is there too.
            unpacked = Directory.CreateTempSubdirectory("ovp-import-").FullName;
            ZipFile.ExtractToDirectory(source, unpacked);

            return Directory.GetFiles(unpacked, "*.ovpn", SearchOption.AllDirectories).Order().ToArray();
        }

        return File.Exists(source) ? [source] : [];
    }

    private static async Task<int> ExamineAsync(string[] args, string source, string[] files, bool commit)
    {
        await using PilotDbContext context = await StoreFactory.OpenAsync();
        ProfileImporter importer = new(context, new OvpnConfigInliner(new FileSystemOvpnFileResolver()));

        IReadOnlyList<ImportCandidate> candidates = await importer.PrepareAsync(files);

        Console.WriteLine($"Examined {candidates.Count} file(s) from {source}");
        Console.WriteLine();

        foreach (ImportCandidate candidate in candidates)
        {
            Report(candidate);
        }

        int importable = candidates.Count(candidate => candidate.Outcome == ImportOutcome.Importable);

        Console.WriteLine();
        Console.WriteLine($"Importable: {importable}   "
            + $"Duplicates: {candidates.Count(c => c.Outcome is ImportOutcome.DuplicateInSelection or ImportOutcome.DuplicateInStore)}   "
            + $"Rejected: {candidates.Count(c => c.Outcome is ImportOutcome.Rejected or ImportOutcome.Unreadable)}");

        if (!commit)
        {
            Console.WriteLine();
            Console.WriteLine("This was a dry run. Pass --commit to store the importable profiles.");
            return 0;
        }

        IReadOnlyList<Profile> created = await importer.CommitAsync(candidates);

        IReadOnlyList<string> tags = ArgumentReader.Values(args, "--tag");

        if (created.Count > 0 && tags.Count > 0)
        {
            await ApplyTagsAsync(context, created, tags);
        }

        Console.WriteLine();
        Console.WriteLine($"Stored {created.Count} profile(s). The store now holds "
            + $"{await context.Profiles.CountAsync()}.");

        return 0;
    }

    private static async Task ApplyTagsAsync(
        PilotDbContext context,
        IReadOnlyList<Profile> created,
        IReadOnlyList<string> tagNames)
    {
        foreach (string name in tagNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Tag? tag = await context.Tags.FirstOrDefaultAsync(candidate => candidate.Name == name);

            if (tag is null)
            {
                tag = new Tag { Name = name };
                context.Tags.Add(tag);
            }

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

        await context.SaveChangesAsync();
    }

    private static void Report(ImportCandidate candidate)
    {
        string marker = candidate.Outcome switch
        {
            ImportOutcome.Importable => "ok  ",
            ImportOutcome.DuplicateInSelection or ImportOutcome.DuplicateInStore => "dup ",
            _ => "skip",
        };

        string endpoint = candidate.RemoteHost is null
            ? string.Empty
            : $"{candidate.RemoteHost}:{candidate.RemotePort}/{candidate.Protocol}";

        Console.WriteLine($"  [{marker}] {candidate.SuggestedName,-42} {endpoint}");

        if (candidate.Detail is { Length: > 0 })
        {
            Console.WriteLine($"         {candidate.Detail}");
        }

        if (candidate.UnsupportedOptions.Count > 0)
        {
            Console.WriteLine($"         uses script directives the interactive service refuses: "
                + string.Join(", ", candidate.UnsupportedOptions));
        }

        if (candidate.MissingFiles.Count > 0)
        {
            Console.WriteLine($"         referenced files not found: {string.Join(", ", candidate.MissingFiles)}");
        }
    }
}
