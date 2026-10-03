using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Data;

/// <summary>
/// The tables synchronisation needs are added to every database, the local library included.
/// </summary>
/// <remarks>
/// An existing library is the case that matters: it is migrated on the first start of the new
/// version and must come out of it with every profile it had.
/// </remarks>
public sealed class ServerSyncMigrationTests : IDisposable
{
    /// <summary>
    /// The last migration before synchronisation existed.
    /// </summary>
    private const string PreviousMigration = "20260915173628_RemoveWatchedFolders";

    private readonly string root = Directory.CreateTempSubdirectory("ovp-migration-").FullName;

    private string ConnectionString => $"Data Source={Path.Combine(root, "pilot.db")}";

    [Fact]
    public async Task Migrate_ExistingLocalLibrary_KeepsProfilesAndAddsEmptySyncTables()
    {
        await MigrateToAsync(PreviousMigration);

        Guid profileId = Guid.NewGuid();

        await using (PilotDbContext before = CreateContext())
        {
            // Written through raw SQL, because the current model knows tables the old schema lacks.
            await before.Database.ExecuteSqlAsync($"""
                INSERT INTO "Profiles"
                    ("Id", "Name", "Configuration", "ContentHash", "Source", "RequiresCredentials",
                     "IsFavourite", "HasUnsupportedOptions", "IsSelfContained", "ConnectCount",
                     "CreatedAt", "UpdatedAt")
                VALUES
                    ({profileId}, 'example-site', 'client', {new string('a', 64)}, 1, 0, 0, 0, 1, 3, 0, 0);
                """);
        }

        await MigrateToAsync(target: null);

        await using PilotDbContext context = CreateContext();

        Profile profile = await context.Profiles.SingleAsync();
        Assert.Equal(profileId, profile.Id);
        Assert.Equal(ProfileSource.Imported, profile.Source);
        Assert.Equal(3, profile.ConnectCount);
        Assert.Empty(await context.PendingChanges.ToListAsync());
        Assert.Empty(await context.SyncStates.ToListAsync());
    }

    [Fact]
    public async Task Migrate_EmptyDatabase_StoresMarkersInOrderAndTheSyncState()
    {
        await MigrateToAsync(target: null);

        DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        await using (PilotDbContext write = CreateContext())
        {
            write.PendingChanges.Add(new PendingChange { Kind = PendingChangeKind.Favourites, CreatedAt = now });
            write.PendingChanges.Add(new PendingChange
            {
                Kind = PendingChangeKind.VaultAdd,
                EntityId = Guid.NewGuid(),
                Realm = "Auth",
                CreatedAt = now.AddSeconds(1),
            });
            write.SyncStates.Add(new SyncState { Cursor = 42, LastSuccessfulPullAt = now, UserRole = "admin" });
            await write.SaveChangesAsync();
        }

        await using PilotDbContext context = CreateContext();

        List<PendingChange> markers = await context.PendingChanges.OrderBy(change => change.Id).ToListAsync();
        Assert.Equal([PendingChangeKind.Favourites, PendingChangeKind.VaultAdd], markers.Select(change => change.Kind));
        Assert.True(markers[0].Id < markers[1].Id);

        // Sorting by an instant is what the ticks storage exists for.
        DateTimeOffset since = now.AddMilliseconds(500);
        Assert.Single(await context.PendingChanges.Where(change => change.CreatedAt > since).ToListAsync());

        SyncState state = await context.SyncStates.SingleAsync();
        Assert.Equal(SyncState.SingletonId, state.Id);
        Assert.Equal(42, state.Cursor);
        Assert.Equal(now, state.LastSuccessfulPullAt);
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
