using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Lists the profiles held in the local store.
/// </summary>
internal static class ListCommand
{
    private static readonly JsonSerializerOptions JsonOutput = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        bool asJson = args.Contains("--json", StringComparer.Ordinal);

        // Completion scripts consume this, so it stays one bare name per line with nothing else.
        bool namesOnly = args.Contains("--names", StringComparer.Ordinal);

        await using PilotDbContext context = await StoreFactory.OpenAsync();

        IQueryable<Profile> query = context.Profiles.AsNoTracking();

        if (ArgumentReader.Value(args, "--tag") is { } tag)
        {
            query = query.Where(profile => profile.Tags.Any(link =>
                link.Tag != null && EF.Functions.Like(link.Tag.Name, $"%{tag}%")));
        }

        List<ProfileSummary> profiles = await query
            .OrderBy(profile => profile.Name)
            .Select(profile => new ProfileSummary(
                profile.Name,
                profile.RemoteHost,
                profile.RemotePort,
                profile.Protocol,
                profile.RequiresCredentials,
                profile.HasUnsupportedOptions,
                profile.IsFavourite,
                profile.FavouriteSlot,
                profile.Tags.Select(link => link.Tag!.Name).ToList(),
                profile.LastConnectedAt,
                profile.ConnectCount))
            .ToListAsync();

        if (namesOnly)
        {
            foreach (ProfileSummary profile in profiles)
            {
                Console.WriteLine(profile.Name);
            }

            return 0;
        }

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(profiles, JsonOutput));
            return 0;
        }

        if (profiles.Count == 0)
        {
            Console.WriteLine("The store is empty. Use 'ovp add <path> --commit' to add profiles.");
            return 0;
        }

        Console.WriteLine($"{profiles.Count} profile(s)");
        Console.WriteLine();

        foreach (ProfileSummary profile in profiles)
        {
            string endpoint = profile.RemoteHost is null
                ? string.Empty
                : $"{profile.RemoteHost}:{profile.RemotePort}/{profile.Protocol}";

            // Markers keep the listing scannable without a second column of prose.
            string markers = string.Concat(
                SlotMarker(profile),
                profile.RequiresCredentials ? "P" : " ",
                profile.HasUnsupportedOptions ? "!" : " ");

            string lastUsed = profile.LastConnectedAt is { } when
                ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "never";

            Console.WriteLine($"  {markers} {profile.Name,-44} {endpoint,-28} {lastUsed}");

            if (profile.Tags.Count > 0)
            {
                Console.WriteLine($"      tags {string.Join(", ", profile.Tags)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  * favourite   1-9,0 favourite slot   P needs a password   ! uses script directives");
        return 0;
    }

    /// <summary>
    /// One character for the slot, so the listing stays aligned. Slot ten shows as zero, which is
    /// the key it sits under on the number row.
    /// </summary>
    private static string SlotMarker(ProfileSummary profile) => profile.FavouriteSlot switch
    {
        null => profile.IsFavourite ? "*" : " ",
        10 => "0",
        int slot => slot.ToString(CultureInfo.InvariantCulture),
    };

    private sealed record ProfileSummary(
        string Name,
        string? RemoteHost,
        int? RemotePort,
        string? Protocol,
        bool RequiresCredentials,
        bool HasUnsupportedOptions,
        bool IsFavourite,
        int? FavouriteSlot,
        IReadOnlyList<string> Tags,
        DateTimeOffset? LastConnectedAt,
        int ConnectCount);
}

/// <summary>
/// Opens the same store the application uses.
/// </summary>
internal static class StoreFactory
{
    /// <summary>
    /// The same locations the application uses, so both act on one store.
    /// </summary>
    public static IApplicationPaths Paths { get; } = new UserApplicationPaths();

    public static async Task<PilotDbContext> OpenAsync()
    {
        DbContextOptions<PilotDbContext> options = new DbContextOptionsBuilder<PilotDbContext>()
            .UseSqlite($"Data Source={Paths.DatabasePath}")
            .Options;

        PilotDbContext context = new(options);
        await context.Database.MigrateAsync();
        return context;
    }
}
