using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// A migrated SQLite file in a temporary folder.
/// </summary>
/// <remarks>
/// A real file rather than an in memory substitute, because what is under test is that changes and
/// their markers land in one transaction, and that the queries translate for this provider.
/// </remarks>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string root;
    private readonly ServiceProvider services;

    private TestDatabase(string root, ServiceProvider services)
    {
        this.root = root;
        this.services = services;
        Factory = services.GetRequiredService<IDbContextFactory<PilotDbContext>>();
    }

    public IDbContextFactory<PilotDbContext> Factory { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        string root = Directory.CreateTempSubdirectory("ovp-outbox-").FullName;

        ServiceCollection collection = new();
        collection.AddDbContextFactory<PilotDbContext>(options => options
            .UseSqlite($"Data Source={Path.Combine(root, "pilot.db")}"));

        TestDatabase database = new(root, collection.BuildServiceProvider());

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        return database;
    }

    public async Task<Profile> AddProfileAsync(string name, string configuration = "client\nremote vpn.example.com 1194\n")
    {
        await using PilotDbContext context = await Factory.CreateDbContextAsync();

        Profile profile = new()
        {
            Name = name,
            Configuration = configuration,
            ContentHash = OpenVpnPilot.Data.Import.ProfileImporter.ComputeHash(configuration),
        };

        context.Profiles.Add(profile);
        await context.SaveChangesAsync();
        return profile;
    }

    public async Task<List<PendingChange>> MarkersAsync()
    {
        await using PilotDbContext context = await Factory.CreateDbContextAsync();
        return await context.PendingChanges.AsNoTracking().OrderBy(change => change.Id).ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
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
