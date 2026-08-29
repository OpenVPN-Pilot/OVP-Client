using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The history store runs against a real SQLite file, because the queries it builds have to be
/// translatable by that provider and an in memory substitute would not prove it.
/// </summary>
public sealed class SessionStoreTests : IAsyncLifetime
{
    private readonly string root = Directory.CreateTempSubdirectory("ovp-sessions-").FullName;
    private ServiceProvider services = null!;
    private IDbContextFactory<PilotDbContext> factory = null!;
    private FakeClock clock = null!;

    private Guid profileId;

    public async Task InitializeAsync()
    {
        ServiceCollection collection = new();

        collection.AddDbContextFactory<PilotDbContext>(options => options
            .UseSqlite($"Data Source={Path.Combine(root, "test.db")}")
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services = collection.BuildServiceProvider();
        factory = services.GetRequiredService<IDbContextFactory<PilotDbContext>>();

        await using PilotDbContext context = await factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        Profile profile = new()
        {
            Name = "example-site",
            Configuration = "client",
            ContentHash = new string('a', 64),
        };

        context.Profiles.Add(profile);
        await context.SaveChangesAsync();

        profileId = profile.Id;
        clock = new FakeClock(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
    }

    private SessionStore CreateStore() => new(factory, clock);

    [Fact]
    public async Task BeginAsync_ThenEndAsync_RecordsTheSessionWithItsCounters()
    {
        SessionStore store = CreateStore();

        Guid sessionId = await store.BeginAsync(profileId, "10.8.0.6", "203.0.113.10", 1194);

        clock.Advance(TimeSpan.FromMinutes(30));
        await store.EndAsync(sessionId, SessionEndReason.UserRequested, 4096, 2048, detail: null);

        SessionRecord record = Assert.Single(await store.GetHistoryAsync(new SessionQuery()));

        Assert.Equal("example-site", record.ProfileName);
        Assert.Equal(4096, record.BytesReceived);
        Assert.Equal(2048, record.BytesSent);
        Assert.Equal("203.0.113.10", record.ServerAddress);
        Assert.Equal(1194, record.ServerPort);
        Assert.Equal(SessionEndReason.UserRequested, record.EndReason);
        Assert.Equal(TimeSpan.FromMinutes(30), record.Duration);
    }

    [Fact]
    public async Task GetHistoryAsync_WithAPeriodFilter_TranslatesToTheDatabase()
    {
        SessionStore store = CreateStore();

        Guid old = await store.BeginAsync(profileId, null, null, null);
        await store.EndAsync(old, SessionEndReason.UserRequested, 1, 1, null);

        clock.Advance(TimeSpan.FromDays(40));

        Guid recent = await store.BeginAsync(profileId, null, null, null);
        await store.EndAsync(recent, SessionEndReason.UserRequested, 2, 2, null);

        // The history screen always filters by period, so this is the query that matters most.
        IReadOnlyList<SessionRecord> withinThirtyDays = await store.GetHistoryAsync(
            new SessionQuery(From: clock.GetUtcNow().AddDays(-30)));

        SessionRecord record = Assert.Single(withinThirtyDays);
        Assert.Equal(recent, record.Id);
    }

    [Fact]
    public async Task GetHistoryAsync_WithAProfileFilter_ReturnsOnlyThatProfile()
    {
        SessionStore store = CreateStore();

        Guid sessionId = await store.BeginAsync(profileId, null, null, null);
        await store.EndAsync(sessionId, SessionEndReason.UserRequested, 1, 1, null);

        Assert.Single(await store.GetHistoryAsync(new SessionQuery(ProfileId: profileId)));
        Assert.Empty(await store.GetHistoryAsync(new SessionQuery(ProfileId: Guid.NewGuid())));
    }

    [Fact]
    public async Task GetTotalsAsync_SumsDurationAndTransfer()
    {
        SessionStore store = CreateStore();

        Guid first = await store.BeginAsync(profileId, null, null, null);
        clock.Advance(TimeSpan.FromMinutes(10));
        await store.EndAsync(first, SessionEndReason.UserRequested, 100, 50, null);

        Guid second = await store.BeginAsync(profileId, null, null, null);
        clock.Advance(TimeSpan.FromMinutes(20));
        await store.EndAsync(second, SessionEndReason.ConnectionLost, 300, 150, null);

        SessionTotals totals = await store.GetTotalsAsync(new SessionQuery());

        Assert.Equal(2, totals.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), totals.Duration);
        Assert.Equal(400, totals.BytesReceived);
        Assert.Equal(200, totals.BytesSent);
    }

    [Fact]
    public async Task GetTotalsAsync_WithAPeriodFilter_TranslatesToTheDatabase()
    {
        SessionStore store = CreateStore();

        Guid sessionId = await store.BeginAsync(profileId, null, null, null);
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.EndAsync(sessionId, SessionEndReason.UserRequested, 10, 10, null);

        SessionTotals totals = await store.GetTotalsAsync(
            new SessionQuery(From: clock.GetUtcNow().AddDays(-7), To: clock.GetUtcNow()));

        Assert.Equal(1, totals.Count);
    }

    [Fact]
    public async Task CloseAbandonedAsync_EndsSessionsAPreviousRunLeftOpen()
    {
        SessionStore store = CreateStore();

        await store.BeginAsync(profileId, null, null, null);

        Assert.Equal(1, await store.CloseAbandonedAsync());

        SessionRecord record = Assert.Single(await store.GetHistoryAsync(new SessionQuery()));

        Assert.Equal(SessionEndReason.ApplicationClosed, record.EndReason);

        // The end time is unknown, so it must not claim the tunnel lasted until the restart.
        Assert.Equal(record.StartedAt, record.EndedAt);
    }

    [Fact]
    public async Task EndAsync_OnAnAlreadyClosedSession_ChangesNothing()
    {
        SessionStore store = CreateStore();

        Guid sessionId = await store.BeginAsync(profileId, null, null, null);
        await store.EndAsync(sessionId, SessionEndReason.UserRequested, 10, 10, null);
        await store.EndAsync(sessionId, SessionEndReason.Error, 999, 999, "later");

        SessionRecord record = Assert.Single(await store.GetHistoryAsync(new SessionQuery()));

        Assert.Equal(SessionEndReason.UserRequested, record.EndReason);
        Assert.Equal(10, record.BytesReceived);
    }

    [Fact]
    public async Task PruneAsync_RemovesSessionsOlderThanTheGivenMoment()
    {
        SessionStore store = CreateStore();

        Guid sessionId = await store.BeginAsync(profileId, null, null, null);
        await store.EndAsync(sessionId, SessionEndReason.UserRequested, 1, 1, null);

        clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(1, await store.PruneAsync(clock.GetUtcNow()));
        Assert.Empty(await store.GetHistoryAsync(new SessionQuery()));
    }

    [Fact]
    public async Task AddEventAsync_StoresAnEntryAgainstTheSession()
    {
        SessionStore store = CreateStore();

        Guid sessionId = await store.BeginAsync(profileId, null, null, null);
        await store.AddEventAsync(sessionId, "Warning", "ping restart");

        await using PilotDbContext context = await factory.CreateDbContextAsync();

        SessionEvent recorded = Assert.Single(await context.SessionEvents.ToListAsync());

        Assert.Equal(sessionId, recorded.SessionId);
        Assert.Equal("ping restart", recorded.Message);
    }

    public async Task DisposeAsync()
    {
        await services.DisposeAsync();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    /// <summary>
    /// A clock the test moves by hand, so durations are exact rather than approximate.
    /// </summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now;

        public FakeClock(DateTimeOffset start) => now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }
}
