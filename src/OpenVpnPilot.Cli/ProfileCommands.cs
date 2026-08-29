using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Sets or clears a favourite and its numbered slot.
/// </summary>
internal static class FavouriteCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? name = ArgumentReader.FirstPositional(args);

        if (name is null)
        {
            Console.Error.WriteLine("A profile name is required.");
            return 1;
        }

        await using PilotDbContext context = await StoreFactory.OpenAsync();

        Profile? profile = await ProfileLookup.FindAsync(context, name);

        if (profile is null)
        {
            Console.Error.WriteLine($"No stored profile matches '{name}'.");
            return 1;
        }

        if (args.Contains("--clear", StringComparer.Ordinal))
        {
            profile.IsFavourite = false;
            profile.FavouriteSlot = null;
            await context.SaveChangesAsync();

            Console.WriteLine($"{profile.Name} is no longer a favourite.");
            return 0;
        }

        profile.IsFavourite = true;

        string? slotText = ArgumentReader.Value(args, "--slot");

        if (slotText is not null)
        {
            if (!int.TryParse(slotText, CultureInfo.InvariantCulture, out int slot) || slot is < 1 or > 9)
            {
                Console.Error.WriteLine("A slot is a number from one to nine.");
                return 1;
            }

            // The slot is unique, so whoever held it gives it up in the same transaction.
            Profile? previous = await context.Profiles
                .FirstOrDefaultAsync(other => other.FavouriteSlot == slot && other.Id != profile.Id);

            if (previous is not null)
            {
                previous.FavouriteSlot = null;
                Console.WriteLine($"Slot {slot} was held by {previous.Name} and has been moved.");
            }

            profile.FavouriteSlot = slot;
        }

        await context.SaveChangesAsync();

        Console.WriteLine(profile.FavouriteSlot is { } assigned
            ? $"{profile.Name} is a favourite in slot {assigned}."
            : $"{profile.Name} is a favourite.");

        return 0;
    }
}

/// <summary>
/// Deletes a profile from the store.
/// </summary>
internal static class RemoveCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? name = ArgumentReader.FirstPositional(args);

        if (name is null)
        {
            Console.Error.WriteLine("A profile name is required.");
            return 1;
        }

        await using PilotDbContext context = await StoreFactory.OpenAsync();

        Profile? profile = await ProfileLookup.FindAsync(context, name);

        if (profile is null)
        {
            Console.Error.WriteLine($"No stored profile matches '{name}'.");
            return 1;
        }

        if (!args.Contains("--yes", StringComparer.Ordinal))
        {
            Console.Write($"Delete '{profile.Name}' and its history? [y/N] ");
            string? answer = Console.ReadLine();

            if (answer is not ("y" or "Y" or "yes" or "Yes"))
            {
                Console.WriteLine("Nothing was deleted.");
                return 0;
            }
        }

        context.Profiles.Remove(profile);
        await context.SaveChangesAsync();

        Console.WriteLine($"Deleted {profile.Name}.");
        return 0;
    }
}

/// <summary>
/// Writes stored profiles back out as self contained configuration files.
/// </summary>
/// <remarks>
/// Each file carries its certificates and its private key inline, which is what makes it usable on
/// another machine. That also makes the output as sensitive as the key it contains, so the
/// destination is reported plainly and the files are written with no wider permissions than the
/// directory already has.
/// </remarks>
internal static class ExportCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? directory = ArgumentReader.FirstPositional(args);

        if (directory is null)
        {
            Console.Error.WriteLine("A destination directory is required.");
            return 1;
        }

        string target = Path.GetFullPath(directory);
        Directory.CreateDirectory(target);

        await using PilotDbContext context = await StoreFactory.OpenAsync();

        IQueryable<Profile> query = context.Profiles.AsNoTracking();

        if (ArgumentReader.Value(args, "--profile") is { } name)
        {
            query = query.Where(profile => EF.Functions.Like(profile.Name, $"%{name}%"));
        }

        if (ArgumentReader.Value(args, "--tag") is { } tag)
        {
            query = query.Where(profile => profile.Tags.Any(link =>
                link.Tag != null && EF.Functions.Like(link.Tag.Name, $"%{tag}%")));
        }

        List<Profile> profiles = await query.OrderBy(profile => profile.Name).ToListAsync();

        if (profiles.Count == 0)
        {
            Console.Error.WriteLine("Nothing matched, so nothing was written.");
            return 1;
        }

        foreach (Profile profile in profiles)
        {
            string path = Path.Combine(target, SafeFileName(profile.Name) + ".ovpn");
            await File.WriteAllTextAsync(path, profile.Configuration, new UTF8Encoding(false));
            Console.WriteLine($"  {profile.Name} -> {path}");
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {profiles.Count} profile(s) to {target}.");
        Console.WriteLine("These files contain private keys. Treat them as credentials.");
        return 0;
    }

    /// <summary>
    /// Turns a profile name into a file name, because a name is free text and a path is not.
    /// </summary>
    private static string SafeFileName(string name)
    {
        StringBuilder builder = new(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (char character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Shared argument parsing for the commands.
/// </summary>
internal static class ArgumentReader
{
    /// <summary>
    /// The first argument that is not a flag and not a flag's value.
    /// </summary>
    public static string? FirstPositional(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                // Flags that take a value consume the next argument.
                if (TakesValue(args[index]))
                {
                    index++;
                }

                continue;
            }

            return args[index];
        }

        return null;
    }

    public static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Every occurrence of a repeatable flag, such as a tag given more than once.
    /// </summary>
    public static IReadOnlyList<string> Values(string[] args, string name)
    {
        List<string> values = [];

        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                values.Add(args[index + 1]);
            }
        }

        return values;
    }

    private static bool TakesValue(string flag) => flag
        is "--profile" or "--tag" or "--slot" or "--seconds"
        or "--username" or "--password" or "--passphrase" or "--challenge";
}

/// <summary>
/// Finds a profile by name, preferring an exact match over a partial one.
/// </summary>
internal static class ProfileLookup
{
    public static async Task<Profile?> FindAsync(PilotDbContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await context.Profiles.FirstOrDefaultAsync(profile => profile.Name == name)
            ?? await context.Profiles
                .Where(profile => EF.Functions.Like(profile.Name, $"%{name}%"))
                .OrderBy(profile => profile.Name)
                .FirstOrDefaultAsync();
    }
}
