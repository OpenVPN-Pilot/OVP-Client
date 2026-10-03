using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using SyncState = OpenVpnPilot.App.Services.Server.SyncState;
using SyncStateRow = OpenVpnPilot.Data.Entities.SyncState;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// The pull: what the server's change feed does to the copy, and what it leaves alone.
/// </summary>
public sealed class SyncEnginePullTests : IAsyncLifetime
{
    private const string ConfigurationA = "client\nremote vpn.example.com 1194\n<ca>\nA\n</ca>\n";
    private const string ConfigurationB = "client\nremote vpn.example.com 1195\n<ca>\nB\n</ca>\n";

    private static readonly Guid ProfileA = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    private static readonly Guid ProfileB = Guid.Parse("0b000000-0000-0000-0000-00000000000b");
    private static readonly Guid OfficeTag = Guid.Parse("0c000000-0000-0000-0000-00000000000c");

    private SyncHarness harness = null!;

    public async Task InitializeAsync() => harness = await SyncHarness.CreateAsync();

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task SynchronizeAsync_FullSyncIntoEmptyCache_StoresProfilesTagsVaultAndCursor()
    {
        ProfileResponse a = harness.Server.Hold(ProfileA, "example-site-a", ConfigurationA, "Office");
        ProfileResponse b = harness.Server.Hold(ProfileB, "example-site-b", ConfigurationB);

        harness.Server.Changes = since => SyncServer.Feed(
            full: true,
            cursor: 42,
            profiles: [a, b],
            tags: [new TagResponse(OfficeTag, "Office", "#ffaa00", 1)],
            vault: [SyncServer.VaultEntry(ProfileA, "Auth", "vpnuser", "shared-secret")]);

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(SyncState.Synchronised, result.State);
        Assert.True(result.Changed);

        Profile stored = (await harness.ProfileAsync(ProfileA))!;
        Assert.Equal("example-site-a", stored.Name);
        Assert.Equal(ConfigurationA, stored.Configuration);
        Assert.Equal(a.ContentHash, stored.ContentHash);
        Assert.Equal(ProfileSource.Server, stored.Source);
        Assert.Equal(OfficeTag, Assert.Single(stored.Tags).TagId);

        Assert.NotNull(await harness.ProfileAsync(ProfileB));
        Assert.Equal(new StoredSecret("vpnuser", "shared-secret"), harness.Secrets.Entries[SecretReference.ForProfile(ProfileA, "Auth")]);

        SyncStateRow row = await harness.QueryAsync(context => context.SyncStates.SingleAsync());
        Assert.Equal(42, row.Cursor);
        Assert.NotNull(row.LastSuccessfulPullAt);
        Assert.Equal(42, harness.Engine.Status.Cursor);
        Assert.Contains(LibraryChanges.Profiles, harness.Notifier.Notified);
    }

    [Fact]
    public async Task SynchronizeAsync_SecondPull_AsksFromTheStoredCursor()
    {
        harness.Server.Changes = since => SyncServer.Feed(since == 0, since == 0 ? 10 : 11);

        await harness.Engine.SynchronizeAsync(CancellationToken.None);
        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        IReadOnlyList<SentRequest> pulls = harness.Sent(HttpMethod.Get, "/api/v1/sync/changes");
        Assert.Equal(["?since=0", "?since=10"], pulls.Select(request => request.Query));
        Assert.Equal(11, harness.Engine.Status.Cursor);
    }

    [Fact]
    public async Task SynchronizeAsync_DeltaWithChangesAndDeletions_AppliesBoth()
    {
        await FullSyncOfBothAsync();

        ProfileResponse renamed = SyncServer.Profile(ProfileA, "example-site-renamed", ConfigurationA);
        harness.Server.Changes = since => SyncServer.Feed(
            full: false,
            cursor: 2,
            profiles: [renamed],
            deletedProfiles: [ProfileB],
            deletedTags: [OfficeTag],
            deletedVault: [new VaultKeyResponse(ProfileA, "Auth")]);

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal("example-site-renamed", (await harness.ProfileAsync(ProfileA))!.Name);
        Assert.Null(await harness.ProfileAsync(ProfileB));
        Assert.False(await harness.QueryAsync(context => context.Tags.AnyAsync(tag => tag.Id == OfficeTag)));

        // A deleted profile takes its stored sign ins along; a deleted entry goes on its own.
        Assert.DoesNotContain(harness.Secrets.Entries.Keys, key => key.Contains(ProfileB.ToString("N"), StringComparison.Ordinal));
        Assert.False(harness.Secrets.Entries.ContainsKey(SecretReference.ForProfile(ProfileA, "Auth")));
    }

    [Fact]
    public async Task SynchronizeAsync_EntriesAndDeletionsInOneAnswer_WritesEntriesBeforeDeleting()
    {
        await FullSyncOfBothAsync();

        harness.Server.Changes = since => SyncServer.Feed(
            full: false,
            cursor: 2,
            vault: [SyncServer.VaultEntry(ProfileA, "Key", null, "new-passphrase")],
            deletedProfiles: [ProfileB]);

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        IReadOnlyList<string> journal = harness.Network.Journal.Entries;
        int written = journal.ToList().FindIndex(entry => entry.StartsWith("store " + SecretReference.ForProfile(ProfileA, "Key"), StringComparison.Ordinal));
        int deleted = journal.ToList().FindIndex(entry => entry.StartsWith("delete " + SecretReference.ForProfile(ProfileB, "Auth"), StringComparison.Ordinal));

        Assert.True(written >= 0 && deleted >= 0, string.Join(Environment.NewLine, journal));
        Assert.True(written < deleted);
    }

    [Fact]
    public async Task SynchronizeAsync_HashUnchanged_DoesNotFetchTheConfigurationAgain()
    {
        await FullSyncOfBothAsync();
        int fetched = harness.Count(HttpMethod.Get, $"/api/v1/profiles/{ProfileA:D}/configuration");

        ProfileResponse renamed = SyncServer.Profile(ProfileA, "example-site-renamed", ConfigurationA);
        harness.Server.Changes = since => SyncServer.Feed(false, 2, profiles: [renamed]);
        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(fetched, harness.Count(HttpMethod.Get, $"/api/v1/profiles/{ProfileA:D}/configuration"));

        const string Changed = ConfigurationA + "verb 4\n";
        ProfileResponse rewritten = harness.Server.Hold(ProfileA, "example-site-renamed", Changed);
        harness.Server.Changes = since => SyncServer.Feed(false, 3, profiles: [rewritten]);
        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(fetched + 1, harness.Count(HttpMethod.Get, $"/api/v1/profiles/{ProfileA:D}/configuration"));
        Assert.Equal(Changed, (await harness.ProfileAsync(ProfileA))!.Configuration);
    }

    [Fact]
    public async Task SynchronizeAsync_FullSync_KeepsWhatBelongsToThisMachine()
    {
        await FullSyncOfBothAsync();

        DateTimeOffset connected = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

        await harness.ChangeAsync(async context =>
        {
            Profile profile = await context.Profiles.SingleAsync(candidate => candidate.Id == ProfileA);
            profile.LastConnectedAt = connected;
            profile.ConnectCount = 7;
            context.Sessions.Add(new Session { ProfileId = ProfileA, StartedAt = connected });
        });

        harness.Server.Changes = since => SyncServer.Feed(true, 5, profiles: [
            SyncServer.Profile(ProfileA, "example-site-a", ConfigurationA),
            SyncServer.Profile(ProfileB, "example-site-b", ConfigurationB)]);

        await harness.QueryAsync(context => context.SyncStates.ExecuteUpdateAsync(setters => setters.SetProperty(state => state.Cursor, (long?)null)));
        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Profile kept = (await harness.ProfileAsync(ProfileA))!;
        Assert.Equal(connected, kept.LastConnectedAt);
        Assert.Equal(7, kept.ConnectCount);
        Assert.Equal(1, await harness.QueryAsync(context => context.Sessions.CountAsync(session => session.ProfileId == ProfileA)));
    }

    [Fact]
    public async Task SynchronizeAsync_FullSync_RemovesWhatTheServerNoLongerHas()
    {
        await FullSyncOfBothAsync();
        Profile local = await harness.Database.AddProfileAsync("example-local", "client\nremote vpn.example.com 443\n");
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(local.Id, "Auth"), new StoredSecret("me", "mine"));

        harness.Server.Changes = since => SyncServer.Feed(true, 9, profiles: [SyncServer.Profile(ProfileA, "example-site-a", ConfigurationA)]);
        await harness.QueryAsync(context => context.SyncStates.ExecuteUpdateAsync(setters => setters.SetProperty(state => state.Cursor, (long?)null)));

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.NotNull(await harness.ProfileAsync(ProfileA));
        Assert.Null(await harness.ProfileAsync(ProfileB));
        Assert.False(await harness.QueryAsync(context => context.Tags.AnyAsync(tag => tag.Id == OfficeTag)));

        // The vault entry the answer no longer carries is gone; a profile of this machine is untouched.
        Assert.False(harness.Secrets.Entries.ContainsKey(SecretReference.ForProfile(ProfileA, "Auth")));
        Assert.NotNull(await harness.ProfileAsync(local.Id));
        Assert.True(harness.Secrets.Entries.ContainsKey(SecretReference.ForProfile(local.Id, "Auth")));
    }

    [Fact]
    public async Task SynchronizeAsync_CursorExpired_StartsAgainFromZeroInTheSameCycle()
    {
        harness.Server.Changes = since => SyncServer.Feed(since == 0, 30);
        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        harness.Server.On(HttpMethod.Get, "/api/v1/sync/changes", request => request.Query == "?since=30"
            ? Answers.Problem(HttpStatusCode.Gone, ServerErrorCodes.SyncCursorExpired)
            : Answers.Json(SyncServer.Feed(true, 31, profiles: [harness.Server.Hold(ProfileA, "example-site-a", ConfigurationA)])));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(["?since=0", "?since=30", "?since=0"], harness.Sent(HttpMethod.Get, "/api/v1/sync/changes").Select(request => request.Query));
        Assert.Equal(31, harness.Engine.Status.Cursor);
        Assert.NotNull(await harness.ProfileAsync(ProfileA));
    }

    [Fact]
    public async Task SynchronizeAsync_ConfigurationCannotBeFetched_AppliesNothingAndKeepsTheCursor()
    {
        harness.Server.Changes = since => SyncServer.Feed(since == 0, 3);
        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        harness.Server.Changes = since => SyncServer.Feed(false, 4, profiles: [harness.Server.Hold(ProfileA, "example-site-a", ConfigurationA)]);
        harness.Server.On(HttpMethod.Get, $"/api/v1/profiles/{ProfileA:D}/configuration", _ => Answers.Problem(HttpStatusCode.InternalServerError, ServerErrorCodes.ServerError));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(SyncState.Degraded, result.State);
        Assert.Null(await harness.ProfileAsync(ProfileA));
        Assert.Equal(3, await harness.QueryAsync(context => context.SyncStates.Select(state => state.Cursor).SingleAsync()));
    }

    [Fact]
    public async Task PullAsync_PendingMarkers_LeaveTheLocalStateAlone()
    {
        await FullSyncOfBothAsync();

        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileUpdate, ProfileA);
        await harness.Outbox.RecordAsync(PendingChangeKind.VaultAdd, ProfileB, "Key");
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(ProfileB, "Key"), new StoredSecret(null, "typed-here"));

        harness.Server.Changes = since => SyncServer.Feed(false, 2,
            profiles: [SyncServer.Profile(ProfileA, "renamed-elsewhere", ConfigurationA)],
            vault: [SyncServer.VaultEntry(ProfileB, "Key", null, "from-the-server")]);

        ChangeFeedPuller puller = new(
            harness.Network.Api,
            harness.Database.Factory,
            harness.Secrets,
            harness.Maintenance,
            harness.Tunnels,
            harness.Notices,
            TimeProvider.System,
            NullLogger.Instance);

        Assert.Null(await puller.PullAsync(new SyncCycle(), CancellationToken.None));

        Assert.Equal("example-site-a", (await harness.ProfileAsync(ProfileA))!.Name);
        Assert.Equal("typed-here", harness.Secrets.Entries[SecretReference.ForProfile(ProfileB, "Key")].Password);
        Assert.Equal(2, await harness.QueryAsync(context => context.SyncStates.Select(state => state.Cursor).SingleAsync()));
    }

    [Fact]
    public async Task SynchronizeAsync_LocalTagWithTheServersName_IsReplacedByTheServersTag()
    {
        Profile local = await harness.Database.AddProfileAsync("example-local", "client\nremote vpn.example.com 443\n");
        Guid localTag = Guid.NewGuid();

        await harness.ChangeAsync(context =>
        {
            context.Tags.Add(new Tag { Id = localTag, Name = "office" });
            context.ProfileTags.Add(new ProfileTag { ProfileId = local.Id, TagId = localTag });
            return Task.CompletedTask;
        });

        harness.Server.Changes = since => SyncServer.Feed(true, 1, tags: [new TagResponse(OfficeTag, "Office", null, 1)]);

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Profile moved = (await harness.ProfileAsync(local.Id))!;
        Assert.Equal(OfficeTag, Assert.Single(moved.Tags).TagId);
        Assert.Equal("Office", moved.Tags[0].Tag!.Name);
    }

    /// <summary>
    /// A full synchronisation of two profiles, a tag, and sign ins for both.
    /// </summary>
    private async Task FullSyncOfBothAsync()
    {
        ProfileResponse a = harness.Server.Hold(ProfileA, "example-site-a", ConfigurationA, "Office");
        ProfileResponse b = harness.Server.Hold(ProfileB, "example-site-b", ConfigurationB);

        harness.Server.Changes = since => SyncServer.Feed(
            full: true,
            cursor: 1,
            profiles: [a, b],
            tags: [new TagResponse(OfficeTag, "Office", null, 1)],
            vault: [
                SyncServer.VaultEntry(ProfileA, "Auth", "vpnuser", "shared-a"),
                SyncServer.VaultEntry(ProfileB, "Auth", "vpnuser", "shared-b")]);

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);
        Assert.True(result.Completed);
    }
}
