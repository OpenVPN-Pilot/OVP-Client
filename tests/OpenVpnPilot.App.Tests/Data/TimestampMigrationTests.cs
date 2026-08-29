using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Data;

/// <summary>
/// The move from text timestamps to ticks has to carry the values that were already stored.
/// </summary>
/// <remarks>
/// A migration that changes a column type is the one kind that can destroy data silently, so it is
/// exercised against a database built by the previous migration rather than only against a fresh one.
/// </remarks>
public sealed class TimestampMigrationTests : IDisposable
{
    /// <summary>
    /// The last migration that still stored timestamps as text.
    /// </summary>
    private const string PreviousMigration = "20260829082429_AddProfileRouteProtection";

    private readonly string root = Directory.CreateTempSubdirectory("ovp-migration-").FullName;

    private string DatabasePath => Path.Combine(root, "pilot.db");

    private string ConnectionString => $"Data Source={DatabasePath}";

    [Fact]
    public async Task Migration_ConvertsTimestampsThatWereAlreadyStoredAsText()
    {
        await MigrateToAsync(PreviousMigration);

        // Written exactly as the previous version of the application wrote it.
        const string stored = "2026-08-28 19:19:50.0908795+00:00";
        Guid profileId = Guid.NewGuid();

        await ExecuteAsync($"""
            INSERT INTO "Profiles"
                ("Id", "Name", "Configuration", "ContentHash", "Source", "RequiresCredentials",
                 "IsFavourite", "HasUnsupportedOptions", "IsSelfContained", "ConnectCount",
                 "CreatedAt", "UpdatedAt", "LastConnectedAt")
            VALUES
                ('{profileId}', 'example-site', 'client', '{new string('a', 64)}', 1, 0,
                 0, 0, 1, 0,
                 '{stored}', '{stored}', '{stored}');
            """);

        await MigrateToAsync(target: null);

        await using PilotDbContext context = CreateContext();
        Profile profile = await context.Profiles.SingleAsync();

        DateTimeOffset expected = DateTimeOffset.Parse(stored, System.Globalization.CultureInfo.InvariantCulture);

        Assert.NotNull(profile.LastConnectedAt);

        // The conversion goes through the SQLite date functions, which resolve to milliseconds.
        Assert.True(
            (profile.LastConnectedAt.Value - expected).Duration() < TimeSpan.FromMilliseconds(1),
            $"Expected roughly {expected:O} but read {profile.LastConnectedAt:O}.");

        Assert.Equal(TimeSpan.Zero, profile.CreatedAt.Offset);
    }

    [Fact]
    public async Task Migration_LeavesNullTimestampsAlone()
    {
        await MigrateToAsync(PreviousMigration);

        Guid profileId = Guid.NewGuid();

        await ExecuteAsync($"""
            INSERT INTO "Profiles"
                ("Id", "Name", "Configuration", "ContentHash", "Source", "RequiresCredentials",
                 "IsFavourite", "HasUnsupportedOptions", "IsSelfContained", "ConnectCount",
                 "CreatedAt", "UpdatedAt", "LastConnectedAt")
            VALUES
                ('{profileId}', 'never-used', 'client', '{new string('b', 64)}', 1, 0,
                 0, 0, 1, 0,
                 '2026-01-01 00:00:00.0000000+00:00', '2026-01-01 00:00:00.0000000+00:00', NULL);
            """);

        await MigrateToAsync(target: null);

        await using PilotDbContext context = CreateContext();
        Profile profile = await context.Profiles.SingleAsync();

        Assert.Null(profile.LastConnectedAt);
    }

    [Fact]
    public async Task Migration_ProducesColumnsTheDatabaseCanOrderBy()
    {
        await MigrateToAsync(target: null);

        await using PilotDbContext context = CreateContext();

        Profile profile = new()
        {
            Name = "example-site",
            Configuration = "client",
            ContentHash = new string('c', 64),
        };

        context.Profiles.Add(profile);
        context.Sessions.Add(new Session
        {
            ProfileId = profile.Id,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        // This is the shape that could not be translated before the change. The bound is computed
        // first so it becomes a parameter, which is what the store itself does.
        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-1);

        List<Session> ordered = await context.Sessions
            .Where(session => session.StartedAt >= since)
            .OrderByDescending(session => session.StartedAt)
            .ToListAsync();

        Assert.Single(ordered);
    }

    private PilotDbContext CreateContext()
    {
        DbContextOptions<PilotDbContext> options = new DbContextOptionsBuilder<PilotDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        return new PilotDbContext(options);
    }

    private async Task MigrateToAsync(string? target)
    {
        await using PilotDbContext context = CreateContext();

        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(target);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using SqliteConnection connection = new(ConnectionString);
        await connection.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }
}
