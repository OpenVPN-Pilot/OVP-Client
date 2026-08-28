using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Imports configuration files from a directory into the local store.
/// </summary>
/// <remarks>
/// The default is a dry run. Nothing is written until the caller passes the commit flag, which
/// mirrors how the import wizard in the application behaves.
/// </remarks>
internal static class ImportCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("A directory or file is required.");
            return 1;
        }

        string source = Path.GetFullPath(args[0]);
        bool commit = args.Contains("--commit", StringComparer.Ordinal);

        string[] files = Directory.Exists(source)
            ? Directory.GetFiles(source, "*.ovpn", SearchOption.AllDirectories).Order().ToArray()
            : [source];

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"No .ovpn files found under {source}");
            return 1;
        }

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
        Console.WriteLine();
        Console.WriteLine($"Stored {created.Count} profile(s). The store now holds "
            + $"{await context.Profiles.CountAsync()}.");

        return 0;
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
