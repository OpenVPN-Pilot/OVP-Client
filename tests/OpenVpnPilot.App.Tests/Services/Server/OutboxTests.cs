using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// Markers collapse, so what is pushed is the least that brings the server up to date.
/// </summary>
public sealed class OutboxTests : IAsyncLifetime
{
    private TestDatabase database = null!;
    private Outbox outbox = null!;

    public async Task InitializeAsync()
    {
        database = await TestDatabase.CreateAsync();
        outbox = new Outbox(database.Factory, TimeProvider.System, NullLogger<Outbox>.Instance);
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task RecordAsync_UpdateTwice_KeepsOneUpdate()
    {
        Guid profileId = Guid.NewGuid();

        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, profileId);
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, profileId);

        PendingChange marker = Assert.Single(await database.MarkersAsync());
        Assert.Equal(PendingChangeKind.ProfileUpdate, marker.Kind);
        Assert.Equal(profileId, marker.EntityId);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task StageAsync_ConfigurationEditedBeforeOrAfterARename_StaysOneUpdateThatSendsIt(bool first, bool second)
    {
        Guid profileId = Guid.NewGuid();

        foreach (bool configurationChanged in new[] { first, second })
        {
            await using PilotDbContext context = await database.Factory.CreateDbContextAsync();
            await outbox.StageAsync(context, PendingChangeKind.ProfileUpdate, profileId, configurationChanged: configurationChanged);
            await context.SaveChangesAsync();
        }

        PendingChange marker = Assert.Single(await database.MarkersAsync());
        Assert.True(marker.ConfigurationChanged);
    }

    [Fact]
    public async Task StageAsync_ConfigurationFlagOnAnotherKind_IsRefused()
    {
        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            outbox.StageAsync(context, PendingChangeKind.ProfileCreate, Guid.NewGuid(), configurationChanged: true));
    }

    [Fact]
    public async Task RecordAsync_UpdatesOfTwoProfiles_KeepsBoth()
    {
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, Guid.NewGuid());
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, Guid.NewGuid());

        Assert.Equal(2, await outbox.CountAsync());
    }

    [Fact]
    public async Task RecordAsync_DeleteAfterUpdate_ReplacesTheUpdate()
    {
        Guid profileId = Guid.NewGuid();

        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, profileId);
        await outbox.RecordAsync(PendingChangeKind.VaultAdd, profileId, "Auth");
        await outbox.RecordAsync(PendingChangeKind.ProfileDelete, profileId);

        PendingChange marker = Assert.Single(await database.MarkersAsync());
        Assert.Equal(PendingChangeKind.ProfileDelete, marker.Kind);
        Assert.Equal(profileId, marker.EntityId);
    }

    [Fact]
    public async Task RecordAsync_EditsAfterCreate_StayOneCreate()
    {
        Guid profileId = Guid.NewGuid();

        await outbox.RecordAsync(PendingChangeKind.ProfileCreate, profileId);
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, profileId);
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, profileId);

        PendingChange marker = Assert.Single(await database.MarkersAsync());
        Assert.Equal(PendingChangeKind.ProfileCreate, marker.Kind);
    }

    [Fact]
    public async Task RecordAsync_DeleteAfterCreate_LeavesNothingToSend()
    {
        Guid profileId = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, other);
        await outbox.RecordAsync(PendingChangeKind.ProfileCreate, profileId);
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, profileId);
        await outbox.RecordAsync(PendingChangeKind.VaultAdd, profileId, "Auth");
        await outbox.RecordAsync(PendingChangeKind.ProfileDelete, profileId);

        PendingChange marker = Assert.Single(await database.MarkersAsync());
        Assert.Equal(other, marker.EntityId);
    }

    [Fact]
    public async Task RecordAsync_UpdateAfterDelete_IsIgnored()
    {
        Guid profileId = Guid.NewGuid();

        await outbox.RecordAsync(PendingChangeKind.ProfileDelete, profileId);
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, profileId);

        Assert.Equal(PendingChangeKind.ProfileDelete, Assert.Single(await database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task RecordAsync_ListsAndSettings_AreEachRecordedOnce()
    {
        foreach (PendingChangeKind kind in new[] { PendingChangeKind.Favourites, PendingChangeKind.Hotkeys, PendingChangeKind.Settings })
        {
            await outbox.RecordAsync(kind);
            await outbox.RecordAsync(kind);
        }

        Assert.Equal(
            [PendingChangeKind.Favourites, PendingChangeKind.Hotkeys, PendingChangeKind.Settings],
            (await database.MarkersAsync()).Select(change => change.Kind));
    }

    [Fact]
    public async Task RecordAsync_VaultAdds_AreKeyedByProfileAndRealm()
    {
        Guid profileId = Guid.NewGuid();

        await outbox.RecordAsync(PendingChangeKind.VaultAdd, profileId, "Auth");
        await outbox.RecordAsync(PendingChangeKind.VaultAdd, profileId, "Auth");
        await outbox.RecordAsync(PendingChangeKind.VaultAdd, profileId, "Private Key");
        await outbox.RecordAsync(PendingChangeKind.VaultAdd, Guid.NewGuid(), "Auth");

        List<PendingChange> markers = await database.MarkersAsync();
        Assert.Equal(3, markers.Count);
        Assert.Equal(["Auth", "Private Key", "Auth"], markers.Select(change => change.Realm));
    }

    [Fact]
    public async Task RecordAsync_TagDeleteTwice_KeepsOne()
    {
        Guid tagId = Guid.NewGuid();

        await outbox.RecordAsync(PendingChangeKind.TagDelete, tagId);
        await outbox.RecordAsync(PendingChangeKind.TagDelete, tagId);
        await outbox.RecordAsync(PendingChangeKind.TagDelete, Guid.NewGuid());

        Assert.Equal(2, await outbox.CountAsync());
    }

    [Fact]
    public async Task RecordAsync_MarkerWithoutWhatItNames_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => outbox.RecordAsync(PendingChangeKind.ProfileUpdate));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => outbox.RecordAsync(PendingChangeKind.VaultAdd, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => outbox.RecordAsync(PendingChangeKind.Favourites, Guid.NewGuid()));
    }

    [Fact]
    public async Task GetPendingAsync_AfterCollapsing_ReturnsMarkersInTheOrderTheyWereRecorded()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, first);
        await outbox.RecordAsync(PendingChangeKind.Favourites);
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, second);
        await outbox.RecordAsync(PendingChangeKind.ProfileDelete, first);
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, second);

        IReadOnlyList<PendingChange> pending = await outbox.GetPendingAsync();

        Assert.Equal(
            [(PendingChangeKind.Favourites, (Guid?)null), (PendingChangeKind.ProfileUpdate, second), (PendingChangeKind.ProfileDelete, first)],
            pending.Select(change => (change.Kind, change.EntityId)));
        Assert.True(pending.Zip(pending.Skip(1)).All(pair => pair.First.Id < pair.Second.Id));
    }

    [Fact]
    public async Task GetPendingAsync_AfterTheNewestWasDropped_NeverReusesItsNumber()
    {
        await outbox.RecordAsync(PendingChangeKind.Favourites);
        await outbox.RecordAsync(PendingChangeKind.Hotkeys);

        long newest = (await outbox.GetPendingAsync())[^1].Id;
        await outbox.DropAsync(newest);
        await outbox.RecordAsync(PendingChangeKind.Settings);

        Assert.True((await outbox.GetPendingAsync())[^1].Id > newest);
    }

    [Fact]
    public async Task RecordFailedAttemptAsync_CountsAttemptsAndKeepsTheLastCode()
    {
        await outbox.RecordAsync(PendingChangeKind.Settings);
        long id = (await outbox.GetPendingAsync())[0].Id;

        await outbox.RecordFailedAttemptAsync(id, "request.too_many");
        await outbox.RecordFailedAttemptAsync(id, null);

        PendingChange marker = Assert.Single(await database.MarkersAsync());
        Assert.Equal(2, marker.Attempts);
        Assert.Null(marker.LastErrorCode);
    }

    [Fact]
    public async Task DropAsync_RemovesOnlyThatMarker()
    {
        await outbox.RecordAsync(PendingChangeKind.Favourites);
        await outbox.RecordAsync(PendingChangeKind.Hotkeys);

        await outbox.DropAsync((await outbox.GetPendingAsync())[0].Id);

        Assert.Equal(PendingChangeKind.Hotkeys, Assert.Single(await database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task ClearAsync_RemovesEveryMarker()
    {
        await outbox.RecordAsync(PendingChangeKind.Favourites);
        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, Guid.NewGuid());

        Assert.Equal(2, await outbox.ClearAsync());
        Assert.Equal(0, await outbox.CountAsync());
    }

    [Fact]
    public async Task StageAsync_UnitOfWorkNotSaved_WritesNothingAndRequestsNoPush()
    {
        int requests = 0;
        outbox.PushRequested += (_, _) => requests++;

        await using (PilotDbContext context = await database.Factory.CreateDbContextAsync())
        {
            Assert.True(await outbox.StageAsync(context, PendingChangeKind.Favourites));
        }

        Assert.Empty(await database.MarkersAsync());
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task StageAsync_TwiceInOneUnitOfWork_CollapsesBeforeTheSave()
    {
        Guid profileId = Guid.NewGuid();
        int requests = 0;
        outbox.PushRequested += (_, _) => requests++;

        await using (PilotDbContext context = await database.Factory.CreateDbContextAsync())
        {
            Assert.True(await outbox.StageAsync(context, PendingChangeKind.ProfileUpdate, profileId));
            Assert.False(await outbox.StageAsync(context, PendingChangeKind.ProfileUpdate, profileId));
            await context.SaveChangesAsync();
        }

        Assert.Single(await database.MarkersAsync());
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task RecordAsync_MarkerThatCollapses_RequestsNoPush()
    {
        int requests = 0;
        outbox.PushRequested += (_, _) => requests++;

        await outbox.RecordAsync(PendingChangeKind.Settings);
        await outbox.RecordAsync(PendingChangeKind.Settings);

        Assert.Equal(1, requests);
    }
}
