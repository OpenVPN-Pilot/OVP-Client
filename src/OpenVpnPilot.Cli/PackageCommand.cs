using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Packaging;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Writes a portable package of the profile set.
/// </summary>
/// <remarks>
/// The file carries every private key in the selection, so a passphrase is required rather than
/// offered. A package is made to be moved, which means it will sit somewhere unattended.
/// </remarks>
internal static class PackCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? target = ArgumentReader.FirstPositional(args);

        if (target is null)
        {
            Console.Error.WriteLine("A destination file is required.");
            return 1;
        }

        string path = Path.GetFullPath(target);

        if (!path.EndsWith(".ovppkg", StringComparison.OrdinalIgnoreCase))
        {
            path += ".ovppkg";
        }

        await using PilotDbContext context = await StoreFactory.OpenAsync();

        IReadOnlyCollection<Guid>? selected = await ResolveSelectionAsync(context, args);

        if (selected is { Count: 0 })
        {
            Console.Error.WriteLine("Nothing matched, so nothing was written.");
            return 1;
        }

        string? passphrase = ArgumentReader.Value(args, "--passphrase");

        if (string.IsNullOrEmpty(passphrase))
        {
            Console.Error.WriteLine("A package carries private keys and is always encrypted. "
                + "Pass --passphrase <value>.");
            return 1;
        }

        ProfilePackageService service = new(context);
        ProfilePackageContent content = await service.CreateAsync(selected);

        await ProfilePackageFile.WriteAsync(path, content, passphrase);

        Console.WriteLine($"Wrote {content.Profiles.Count} profile(s) and "
            + $"{content.Hotkeys.Count} shortcut(s) to {path}.");

        return 0;
    }

    /// <summary>
    /// The profiles a filter selects, or null when everything is included.
    /// </summary>
    private static async Task<IReadOnlyCollection<Guid>?> ResolveSelectionAsync(
        PilotDbContext context,
        string[] args)
    {
        string? name = ArgumentReader.Value(args, "--profile");
        string? tag = ArgumentReader.Value(args, "--tag");

        if (name is null && tag is null)
        {
            return null;
        }

        IQueryable<Profile> query = context.Profiles.AsNoTracking();

        if (name is not null)
        {
            query = query.Where(profile => EF.Functions.Like(profile.Name, $"%{name}%"));
        }

        if (tag is not null)
        {
            query = query.Where(profile => profile.Tags.Any(link =>
                link.Tag != null && EF.Functions.Like(link.Tag.Name, $"%{tag}%")));
        }

        return await query.Select(profile => profile.Id).ToListAsync();
    }
}

/// <summary>
/// Reads a portable package back into the store.
/// </summary>
internal static class UnpackCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? source = ArgumentReader.FirstPositional(args);

        if (source is null)
        {
            Console.Error.WriteLine("A package file is required.");
            return 1;
        }

        string path = Path.GetFullPath(source);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Package not found: {path}");
            return 1;
        }

        string? passphrase = ArgumentReader.Value(args, "--passphrase");

        if (await ProfilePackageFile.IsEncryptedAsync(path) && passphrase is null)
        {
            Console.Error.WriteLine("This package is protected. Pass --passphrase to open it.");
            return 1;
        }

        ProfilePackageContent content;

        try
        {
            content = await ProfilePackageFile.ReadAsync(path, passphrase);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The mode is authenticated, so this is either the wrong passphrase or a changed file.
            Console.Error.WriteLine("The package could not be opened. The passphrase is wrong, "
                + "or the file was altered after it was written.");
            return 3;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }

        Console.WriteLine($"Package written {content.CreatedAt:yyyy-MM-dd HH:mm} by version {content.WrittenBy}.");
        Console.WriteLine($"It holds {content.Profiles.Count} profile(s).");

        if (!args.Contains("--commit", StringComparer.Ordinal))
        {
            Console.WriteLine();
            Console.WriteLine("This was a dry run. Pass --commit to write it into the store.");
            return 0;
        }

        await using PilotDbContext context = await StoreFactory.OpenAsync();
        ProfilePackageService service = new(context);

        PackageApplyResult result = await service.ApplyAsync(content);

        Console.WriteLine();
        Console.WriteLine($"Added {result.Added} profile(s), skipped {result.Skipped} already stored, "
            + $"added {result.Hotkeys} shortcut(s).");

        return 0;
    }
}
