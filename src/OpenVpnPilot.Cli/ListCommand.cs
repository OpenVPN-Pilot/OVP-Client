using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

        await using PilotDbContext context = await StoreFactory.OpenAsync();

        List<ProfileSummary> profiles = await context.Profiles
            .AsNoTracking()
            .OrderBy(profile => profile.Name)
            .Select(profile => new ProfileSummary(
                profile.Name,
                profile.RemoteHost,
                profile.RemotePort,
                profile.Protocol,
                profile.RequiresCredentials,
                profile.HasUnsupportedOptions,
                profile.IsFavourite,
                profile.LastConnectedAt,
                profile.ConnectCount))
            .ToListAsync();

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(profiles, JsonOutput));

            return 0;
        }

        if (profiles.Count == 0)
        {
            Console.WriteLine("The store is empty. Use 'ovp import <path> --commit' to add profiles.");
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
                profile.IsFavourite ? "*" : " ",
                profile.RequiresCredentials ? "P" : " ",
                profile.HasUnsupportedOptions ? "!" : " ");

            string lastUsed = profile.LastConnectedAt is { } when
                ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "never";

            Console.WriteLine($"  {markers} {profile.Name,-44} {endpoint,-28} {lastUsed}");
        }

        Console.WriteLine();
        Console.WriteLine("  * favourite    P needs a password    ! uses script directives");
        return 0;
    }

    private sealed record ProfileSummary(
        string Name,
        string? RemoteHost,
        int? RemotePort,
        string? Protocol,
        bool RequiresCredentials,
        bool HasUnsupportedOptions,
        bool IsFavourite,
        DateTimeOffset? LastConnectedAt,
        int ConnectCount);
}

/// <summary>
/// Opens the same store the application uses.
/// </summary>
internal static class StoreFactory
{
    public static async Task<PilotDbContext> OpenAsync()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenVpnPilot");

        Directory.CreateDirectory(directory);

        DbContextOptions<PilotDbContext> options = new DbContextOptionsBuilder<PilotDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "pilot.db")}")
            .Options;

        PilotDbContext context = new(options);
        await context.Database.MigrateAsync();
        return context;
    }
}
