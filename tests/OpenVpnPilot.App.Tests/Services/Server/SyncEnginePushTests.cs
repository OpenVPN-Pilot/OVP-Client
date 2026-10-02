using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using SyncState = OpenVpnPilot.App.Services.Server.SyncState;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// The push: every kind of change, every answer the brief names, and what an answer that means
/// "not now" does to the rest of the cycle.
/// </summary>
public sealed class SyncEnginePushTests : IAsyncLifetime
{
    private const string Configuration = "client\nremote vpn.example.com 1194\n<ca>\nA\n</ca>\n";
    private const string WithCredentialFile = "client\nremote vpn.example.com 1194\nauth-user-pass creds.txt\n<ca>\nA\n</ca>\n";

    private static readonly Guid ServerProfile = Guid.Parse("5e000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherServerProfile = Guid.Parse("5e000000-0000-0000-0000-000000000002");

    private SyncHarness harness = null!;

    public async Task InitializeAsync() => harness = await SyncHarness.CreateAsync();

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task SynchronizeAsync_ProfileCreatedOffline_UploadsAndTakesTheServersIdAndConfiguration()
    {
        UseDeltas();
        Profile local = await harness.Database.AddProfileAsync("example-site", WithCredentialFile);
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(local.Id, "Auth"), new StoredSecret("vpnuser", "typed"));
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, local.Id);

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.Pushed);
        Assert.Null(await harness.ProfileAsync(local.Id));

        Guid serverId = Assert.Single(harness.Server.Configurations.Keys);
        Profile uploaded = (await harness.ProfileAsync(serverId))!;
        Assert.Equal(ProfileSource.Server, uploaded.Source);
        Assert.Equal(ServerContentHash.Normalise(WithCredentialFile), uploaded.Configuration);
        Assert.Equal(ServerContentHash.Compute(WithCredentialFile), uploaded.ContentHash);
        Assert.Equal("typed", harness.Secrets.Entries[SecretReference.ForProfile(serverId, "Auth")].Password);
        Assert.DoesNotContain(await harness.Database.MarkersAsync(), marker => marker.Kind == PendingChangeKind.ProfileCreate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SynchronizeAsync_SignInOfAProfileCreatedOffline_IsSharedUnderTheServersIdInTheSameCycle(bool remembered)
    {
        UseDeltas();
        Profile local = await harness.Database.AddProfileAsync("example-site", WithCredentialFile);
        StoredSecret typed = new("vpnuser", "typed");

        if (remembered)
        {
            await harness.Secrets.WriteAsync(SecretReference.ForProfile(local.Id, "Auth"), typed);
        }
        else
        {
            harness.Held.Hold(local.Id, "Auth", typed);
        }

        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, local.Id);
        await harness.Outbox.RecordAsync(PendingChangeKind.VaultAdd, local.Id, "Auth");

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Guid serverId = Assert.Single(harness.Server.Configurations.Keys);

        List<string> writes = [.. harness.Requests
            .Where(request => request.Method == HttpMethod.Post)
            .Select(request => request.Path)];
        Assert.Equal(["/api/v1/profiles", $"/api/v1/profiles/{serverId:D}/vault/Auth"], writes);

        SentRequest shared = Assert.Single(harness.Sent(HttpMethod.Post, $"/api/v1/profiles/{serverId:D}/vault/Auth"));
        Assert.Equal("typed", shared.Json.GetProperty("password").GetString());
        Assert.DoesNotContain(await harness.Database.MarkersAsync(), marker => marker.Kind == PendingChangeKind.VaultAdd);
        Assert.Null(harness.Held.Peek(local.Id, "Auth"));
        Assert.Null(harness.Held.Peek(serverId, "Auth"));
    }

    [Fact]
    public async Task SynchronizeAsync_ProfileEditedWhileItsUploadIsUnderWay_SendsTheEditAgainUnderTheNewId()
    {
        Profile local = await harness.Database.AddProfileAsync("example-site", Configuration);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, local.Id);

        harness.Server.On(HttpMethod.Post, "/api/v1/profiles", async request =>
        {
            // The person renames it while the server is answering.
            await harness.ChangeAsync(async context =>
                (await context.Profiles.SingleAsync(profile => profile.Id == local.Id)).Name = "example-site-renamed");

            return harness.Server.DefaultAnswer(request);
        });

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Guid serverId = Assert.Single(harness.Server.Configurations.Keys);

        Assert.Equal("example-site", harness.Server.Names[serverId]);
        Assert.Equal("example-site-renamed", (await harness.ProfileAsync(serverId))!.Name);
        PendingChange update = Assert.Single(await harness.Database.MarkersAsync(), marker => marker.Kind == PendingChangeKind.ProfileUpdate);
        Assert.Equal(serverId, update.EntityId);
    }

    [Fact]
    public async Task SynchronizeAsync_SeveralProfilesCreatedOffline_UploadsThemInOneBatch()
    {
        Profile first = await harness.Database.AddProfileAsync("example-site-a", Configuration);
        Profile second = await harness.Database.AddProfileAsync("example-site-b", Configuration + "verb 4\n");
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, first.Id);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, second.Id);

        harness.Server.On(HttpMethod.Post, "/api/v1/profiles/batch", request =>
        {
            ProfileBatchRequest batch = request.Json.Deserialize<ProfileBatchRequest>(ServerJson.Options)!;
            List<ProfileBatchItemResponse> items = [];

            for (int index = 0; index < batch.Items.Count; index++)
            {
                Guid id = Guid.NewGuid();
                ProfileResponse created = harness.Server.Hold(id, batch.Items[index].Name, batch.Items[index].Configuration);
                items.Add(new ProfileBatchItemResponse(index, ProfileBatchOutcomes.Created, created, null, null));
            }

            return Answers.Json(new ProfileBatchResponse(items.Count, 0, 0, items));
        });

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(2, result.Pushed);
        Assert.Equal(1, harness.Count(HttpMethod.Post, "/api/v1/profiles/batch"));
        Assert.Equal(0, harness.Count(HttpMethod.Post, "/api/v1/profiles"));
        Assert.Null(await harness.ProfileAsync(first.Id));
        Assert.Equal(2, await harness.QueryAsync(context => context.Profiles.CountAsync(profile => profile.Source == ProfileSource.Server)));
    }

    [Fact]
    public async Task SynchronizeAsync_MoreThanABatchCreatedOffline_UploadsThemInBatchesOfFiveHundred()
    {
        const int Count = ProfileBatchRequest.MaximumItems + 1;
        await AddOfflineProfilesAsync(Count);

        List<int> sizes = [];
        harness.Server.On(HttpMethod.Post, "/api/v1/profiles/batch", request =>
        {
            ProfileBatchRequest batch = request.Json.Deserialize<ProfileBatchRequest>(ServerJson.Options)!;
            sizes.Add(batch.Items.Count);

            List<ProfileBatchItemResponse> items = [.. batch.Items.Select((item, index) => new ProfileBatchItemResponse(
                index,
                ProfileBatchOutcomes.Created,
                harness.Server.Hold(Guid.NewGuid(), item.Name, item.Configuration),
                null,
                null))];

            return Answers.Json(new ProfileBatchResponse(items.Count, 0, 0, items));
        });

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal([ProfileBatchRequest.MaximumItems, 1], sizes);
        Assert.Equal(Count, result.Pushed);
        Assert.Equal(0, harness.Count(HttpMethod.Post, "/api/v1/profiles"));
        Assert.DoesNotContain(await harness.Database.MarkersAsync(), marker => marker.Kind == PendingChangeKind.ProfileCreate);
    }

    [Fact]
    public async Task SynchronizeAsync_BatchTooLarge_SendsItsProfilesOneByOne()
    {
        await AddOfflineProfilesAsync(3);
        harness.Server.On(HttpMethod.Post, "/api/v1/profiles/batch", _ => Answers.Problem(HttpStatusCode.RequestEntityTooLarge, ServerErrorCodes.ValidationFailed));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, harness.Count(HttpMethod.Post, "/api/v1/profiles/batch"));
        Assert.Equal(3, harness.Count(HttpMethod.Post, "/api/v1/profiles"));
        Assert.Equal(3, harness.Server.Configurations.Count);
        Assert.Equal(0, result.Dropped);
    }

    [Fact]
    public async Task SynchronizeAsync_UploadIsADuplicate_MatchesTheServersProfileAfterThePull()
    {
        UseDeltas();
        harness.Server.Hold(ServerProfile, "example-site-server", ServerContentHash.Normalise(WithCredentialFile));
        Profile local = await harness.Database.AddProfileAsync("example-site", WithCredentialFile);
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(local.Id, "Auth"), new StoredSecret("vpnuser", "typed"));
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, local.Id);

        harness.Server.On(HttpMethod.Post, "/api/v1/profiles", _ => Answers.Problem(HttpStatusCode.Conflict, ServerErrorCodes.ProfileDuplicate));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Null(await harness.ProfileAsync(local.Id));
        Assert.Equal("example-site-server", (await harness.ProfileAsync(ServerProfile))!.Name);
        Assert.Equal("typed", harness.Secrets.Entries[SecretReference.ForProfile(ServerProfile, "Auth")].Password);

        // Only the favourites are left to send, because the server only now knows what they name.
        Assert.Equal(PendingChangeKind.Favourites, Assert.Single(await harness.Database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task SynchronizeAsync_DuplicateWithoutAMatch_RemovesTheTemporaryProfileAndItsSignIns()
    {
        Profile local = await harness.Database.AddProfileAsync("example-site", Configuration);
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(local.Id, "Auth"), new StoredSecret("vpnuser", "typed"));
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, local.Id);

        harness.Server.On(HttpMethod.Post, "/api/v1/profiles", _ => Answers.Problem(HttpStatusCode.Conflict, ServerErrorCodes.ProfileDuplicate));

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Null(await harness.ProfileAsync(local.Id));
        Assert.False(harness.Secrets.Entries.ContainsKey(SecretReference.ForProfile(local.Id, "Auth")));
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task SynchronizeAsync_UploadRefused_DropsTheChangeKeepsTheProfileAndCountsIt()
    {
        Profile local = await harness.Database.AddProfileAsync("example-site", Configuration);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, local.Id);

        harness.Server.On(HttpMethod.Post, "/api/v1/profiles", _ => Answers.Problem(HttpStatusCode.BadRequest, ServerErrorCodes.ProfileNotSelfContained));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.Dropped);
        Assert.Equal(1, harness.Engine.Status.DroppedChanges);
        Assert.Equal(ServerErrorCodes.ProfileNotSelfContained, harness.Engine.Status.LastErrorCode);

        Profile kept = (await harness.ProfileAsync(local.Id))!;
        Assert.Equal(ProfileSource.Manual, kept.Source);
        Assert.Equal(ServerErrorCodes.ProfileNotSelfContained, kept.UploadRefusedCode);
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task SynchronizeAsync_OneItemOfABatchRefused_MarksThatProfileOnly()
    {
        Profile accepted = await harness.Database.AddProfileAsync("example-site-a", Configuration);
        Profile refused = await harness.Database.AddProfileAsync("example-site-b", WithCredentialFile);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, accepted.Id);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, refused.Id);

        harness.Server.On(HttpMethod.Post, "/api/v1/profiles/batch", request =>
        {
            ProfileBatchRequest batch = request.Json.Deserialize<ProfileBatchRequest>(ServerJson.Options)!;
            ProfileResponse created = harness.Server.Hold(Guid.NewGuid(), batch.Items[0].Name, batch.Items[0].Configuration);

            return Answers.Json(new ProfileBatchResponse(1, 0, 1,
            [
                new ProfileBatchItemResponse(0, ProfileBatchOutcomes.Created, created, null, null),
                new ProfileBatchItemResponse(1, ProfileBatchOutcomes.Rejected, null, ServerErrorCodes.ProfileNotSelfContained, "Detail"),
            ]));
        });

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(1, result.Dropped);
        Assert.Null(await harness.ProfileAsync(accepted.Id));
        Assert.Equal(ServerErrorCodes.ProfileNotSelfContained, (await harness.ProfileAsync(refused.Id))!.UploadRefusedCode);
    }

    [Fact]
    public async Task SynchronizeAsync_CreateForbidden_MarksTheProfileAndDropsAdministratorChanges()
    {
        Profile local = await harness.Database.AddProfileAsync("example-site", Configuration);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, local.Id);
        await harness.Outbox.RecordAsync(PendingChangeKind.Favourites);

        harness.Server.On(HttpMethod.Post, "/api/v1/profiles", _ => Answers.Problem(HttpStatusCode.Forbidden, ServerErrorCodes.Forbidden));
        harness.Server.On(HttpMethod.Get, "/api/v1/auth/me", _ => Answers.Json(Answers.User(ServerRoles.User)));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(ProfileUploadKind.Rejected, result.Uploads[local.Id].Kind);
        Assert.Equal(ServerErrorCodes.Forbidden, (await harness.ProfileAsync(local.Id))!.UploadRefusedCode);
        Assert.Equal(1, harness.Count(HttpMethod.Put, "/api/v1/me/favourites"));
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task EditingARefusedProfile_ClearsTheMarkAndOffersItToTheServerAgain()
    {
        Profile local = await harness.Database.AddProfileAsync("example-site", Configuration);
        await harness.ChangeAsync(async context =>
            (await context.Profiles.SingleAsync(profile => profile.Id == local.Id)).UploadRefusedCode = ServerErrorCodes.ProfileNotSelfContained);

        await Store().RenameProfileAsync(local.Id, "example-site-fixed");

        Assert.Null((await harness.ProfileAsync(local.Id))!.UploadRefusedCode);
        PendingChange marker = Assert.Single(await harness.Database.MarkersAsync());
        Assert.Equal((PendingChangeKind.ProfileCreate, (Guid?)local.Id), (marker.Kind, marker.EntityId));

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal("example-site-fixed", Assert.Single(harness.Server.Names.Values));
    }

    [Fact]
    public async Task SynchronizeAsync_ProfileUpdate_OverwritesWithIfMatchStarAndLeavesAnUnchangedConfigurationOut()
    {
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        await harness.ChangeAsync(async context =>
            (await context.Profiles.SingleAsync(profile => profile.Id == ServerProfile)).Notes = "edited offline");
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileUpdate, ServerProfile);

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        SentRequest put = Assert.Single(harness.Sent(HttpMethod.Put, $"/api/v1/profiles/{ServerProfile:D}"));
        Assert.Equal("*", put.Header("If-Match"));
        Assert.Equal(JsonValueKind.Null, put.Json.GetProperty("configuration").ValueKind);
        Assert.Equal("edited offline", put.Json.GetProperty("notes").GetString());
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task SynchronizeAsync_ConfigurationChangedLocally_SendsTheConfiguration()
    {
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        const string Edited = Configuration + "verb 4\n";

        await Store().UpdateConfigurationAsync(ServerProfile, Edited);
        await Store().RenameProfileAsync(ServerProfile, "example-site-renamed");

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        SentRequest put = Assert.Single(harness.Sent(HttpMethod.Put, $"/api/v1/profiles/{ServerProfile:D}"));
        Assert.Equal(Edited, put.Json.GetProperty("configuration").GetString());
        Assert.Equal(Edited, harness.Server.Configurations[ServerProfile]);
        Assert.Equal(0, harness.Count(HttpMethod.Get, $"/api/v1/profiles/{ServerProfile:D}"));
    }

    [Fact]
    public async Task SynchronizeAsync_OnlyTheNameChangedHereAndTheServerChangedTheConfiguration_KeepsTheServers()
    {
        UseDeltas();
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        const string AdministratorsEdit = Configuration + "verb 5\n";
        harness.Server.Configurations[ServerProfile] = AdministratorsEdit;

        await Store().RenameProfileAsync(ServerProfile, "example-site-renamed");

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        SentRequest put = Assert.Single(harness.Sent(HttpMethod.Put, $"/api/v1/profiles/{ServerProfile:D}"));
        Assert.Equal("*", put.Header("If-Match"));
        Assert.Equal(JsonValueKind.Null, put.Json.GetProperty("configuration").ValueKind);
        Assert.Equal("example-site-renamed", put.Json.GetProperty("name").GetString());
        Assert.Equal(AdministratorsEdit, harness.Server.Configurations[ServerProfile]);

        // The server's answer carries its hash, and the copy takes the configuration it names.
        Assert.Equal(AdministratorsEdit, (await harness.ProfileAsync(ServerProfile))!.Configuration);
    }

    /// <summary>
    /// Two saves that each staged an update before either was written leave two markers. There is
    /// deliberately no unique index to prevent it: the second save would then fail, and it carries
    /// a person's edit. Both markers send the current state, so the duplicate costs one call.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_TwoWritersRecordedTheSameUpdate_SendsTheLatestStateAndLeavesNothing()
    {
        UseDeltas();
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);

        await using (PilotDbContext first = await harness.Database.Factory.CreateDbContextAsync())
        await using (PilotDbContext second = await harness.Database.Factory.CreateDbContextAsync())
        {
            (await first.Profiles.SingleAsync(profile => profile.Id == ServerProfile)).Name = "example-site-a";
            (await second.Profiles.SingleAsync(profile => profile.Id == ServerProfile)).Notes = "edited";
            await harness.Outbox.StageAsync(first, PendingChangeKind.ProfileUpdate, ServerProfile);
            await harness.Outbox.StageAsync(second, PendingChangeKind.ProfileUpdate, ServerProfile);
            await first.SaveChangesAsync();
            await second.SaveChangesAsync();
        }

        Assert.Equal(2, (await harness.Database.MarkersAsync()).Count(marker => marker.Kind == PendingChangeKind.ProfileUpdate));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.All(
            harness.Sent(HttpMethod.Put, $"/api/v1/profiles/{ServerProfile:D}"),
            put => Assert.Equal("edited", put.Json.GetProperty("notes").GetString()));
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ServerErrorCodes.ProfileNotFound)]
    [InlineData(HttpStatusCode.Conflict, ServerErrorCodes.ProfileDuplicate)]
    [InlineData(HttpStatusCode.BadRequest, ServerErrorCodes.ProfileInvalidConfiguration)]
    public async Task SynchronizeAsync_UpdateRefusedForGood_DropsAndCountsIt(HttpStatusCode status, string code)
    {
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileUpdate, ServerProfile);
        harness.Server.On(HttpMethod.Put, $"/api/v1/profiles/{ServerProfile:D}", _ => Answers.Problem(status, code));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(1, result.Dropped);
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronizeAsync_ProfileDeleteAnsweredWithNoContentOrNotFound_IsDone(bool alreadyGone)
    {
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileDelete, ServerProfile);

        if (alreadyGone)
        {
            harness.Server.On(HttpMethod.Delete, $"/api/v1/profiles/{ServerProfile:D}", _ => Answers.Problem(HttpStatusCode.NotFound, ServerErrorCodes.ProfileNotFound));
        }

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Dropped);
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task SynchronizeAsync_TagLeftByItsLastProfile_IsDeletedOnTheServerToo()
    {
        UseDeltas();
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        Guid tagId = Guid.NewGuid();
        await harness.ChangeAsync(context =>
        {
            context.Tags.Add(new Tag { Id = tagId, Name = "Office" });
            context.ProfileTags.Add(new ProfileTag { ProfileId = ServerProfile, TagId = tagId });
            return Task.CompletedTask;
        });

        await Store().SetProfileTagsAsync(ServerProfile, []);

        Assert.Equal(
            [(PendingChangeKind.ProfileUpdate, (Guid?)ServerProfile), (PendingChangeKind.TagDelete, tagId)],
            (await harness.Database.MarkersAsync()).Select(marker => (marker.Kind, marker.EntityId)));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(2, result.Pushed);
        Assert.Equal(0, Assert.Single(harness.Sent(HttpMethod.Put, $"/api/v1/profiles/{ServerProfile:D}")).Json.GetProperty("tags").GetArrayLength());
        Assert.Equal(1, harness.Count(HttpMethod.Delete, $"/api/v1/tags/{tagId:D}"));
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task TagLeftByItsLastProfile_OnTheLocalLibrary_RecordsNothing()
    {
        Profile local = await harness.Database.AddProfileAsync("example-site", Configuration);
        ProfileStore store = new(harness.Database.Factory, TimeProvider.System, new ChangeRecorder(harness.Outbox, new FixedStorageMode(false)));

        await store.SetProfileTagsAsync(local.Id, ["Office"]);
        await store.SetProfileTagsAsync(local.Id, []);

        Assert.Empty(await harness.QueryAsync(context => context.Tags.ToListAsync()));
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task SynchronizeAsync_SeveralKinds_PushesThemInTheOrderTheyWereRecorded()
    {
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        await harness.Outbox.RecordAsync(PendingChangeKind.Settings);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileDelete, OtherServerProfile);
        await harness.Outbox.RecordAsync(PendingChangeKind.Favourites);

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        List<string> writes = [.. harness.Requests
            .Where(request => request.Method != HttpMethod.Get)
            .Select(request => request.Method + " " + request.Path)];

        Assert.Equal(
            ["PUT /api/v1/me/settings", $"DELETE /api/v1/profiles/{OtherServerProfile:D}", "PUT /api/v1/me/favourites"],
            writes);
    }

    [Fact]
    public async Task SynchronizeAsync_ServerUnreachable_KeepsTheChangesCountsTheAttemptAndDoesNotPull()
    {
        await harness.Outbox.RecordAsync(PendingChangeKind.Settings);
        await harness.Outbox.RecordAsync(PendingChangeKind.Favourites);
        harness.Server.Offline = true;

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(SyncState.Offline, result.State);
        Assert.Equal(SyncState.Offline, harness.Engine.Status.State);
        Assert.Equal(2, harness.Engine.Status.PendingChanges);

        List<PendingChange> markers = await harness.Database.MarkersAsync();
        Assert.Equal([1, 0], markers.Select(marker => marker.Attempts));
        Assert.Equal(0, harness.Count(HttpMethod.Get, "/api/v1/sync/changes"));
    }

    [Fact]
    public async Task SynchronizeAsync_ServerError_KeepsTheChangeAndReportsTheServerDegraded()
    {
        await harness.Outbox.RecordAsync(PendingChangeKind.Settings);
        harness.Server.On(HttpMethod.Put, "/api/v1/me/settings", _ => Answers.Problem(HttpStatusCode.InternalServerError, ServerErrorCodes.ServerError));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(SyncState.Degraded, result.State);
        Assert.Equal(1, Assert.Single(await harness.Database.MarkersAsync()).Attempts);
        Assert.Equal(0, harness.Count(HttpMethod.Get, "/api/v1/sync/changes"));
    }

    [Fact]
    public async Task SynchronizeAsync_NotSignedIn_StopsAndKeepsEverything()
    {
        await using SyncHarness signedOut = await SyncHarness.CreateAsync(signedIn: false);
        await signedOut.Outbox.RecordAsync(PendingChangeKind.Settings);

        SyncCycleResult result = await signedOut.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(SyncState.SignInRequired, result.State);
        Assert.Equal(0, Assert.Single(await signedOut.Database.MarkersAsync()).Attempts);
        Assert.Empty(signedOut.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ServerErrorCodes.ClockSkew, SyncState.ClockWrong)]
    [InlineData((HttpStatusCode)426, ServerErrorCodes.ClientOutdated, SyncState.ClientOutdated)]
    public async Task SynchronizeAsync_RefusalAboutThisMachine_StopsWithItsStateAndKeepsTheChange(
        HttpStatusCode status,
        string code,
        SyncState expected)
    {
        await harness.Outbox.RecordAsync(PendingChangeKind.Settings);
        harness.Server.On(HttpMethod.Put, "/api/v1/me/settings", _ => Answers.Problem(status, code));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(expected, result.State);
        Assert.Single(await harness.Database.MarkersAsync());

        if (expected == SyncState.ClockWrong)
        {
            Assert.Equal("Detail of " + code, harness.Engine.Status.Detail);
        }
    }

    [Fact]
    public async Task SynchronizeAsync_RoleNoLongerAdministrator_DropsAdministratorChangesAndSendsTheRest()
    {
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileUpdate, ServerProfile);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileDelete, OtherServerProfile);
        await harness.Outbox.RecordAsync(PendingChangeKind.Favourites);

        harness.Server.On(HttpMethod.Put, $"/api/v1/profiles/{ServerProfile:D}", _ => Answers.Problem(HttpStatusCode.Forbidden, ServerErrorCodes.Forbidden));
        harness.Server.On(HttpMethod.Get, "/api/v1/auth/me", _ => Answers.Json(Answers.User(ServerRoles.User)));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, result.Dropped);
        Assert.Equal(0, harness.Count(HttpMethod.Delete, $"/api/v1/profiles/{OtherServerProfile:D}"));
        Assert.Equal(1, harness.Count(HttpMethod.Put, "/api/v1/me/favourites"));
        Assert.Empty(await harness.Database.MarkersAsync());
        Assert.Equal(ServerRoles.User, await harness.QueryAsync(context => context.SyncStates.Select(state => state.UserRole).SingleAsync()));
    }

    [Fact]
    public async Task SynchronizeAsync_FavouriteOfAProfileTheServerDeleted_SendsTheListAgainWithoutIt()
    {
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        await AddServerProfileAsync(OtherServerProfile, "example-site-b", Configuration + "verb 4\n");
        harness.Server.Configurations.Remove(OtherServerProfile);

        await harness.ChangeAsync(async context =>
        {
            foreach (Profile profile in await context.Profiles.ToListAsync())
            {
                profile.IsFavourite = true;
            }
        });

        await harness.Outbox.RecordAsync(PendingChangeKind.Favourites);

        harness.Server.On(HttpMethod.Put, "/api/v1/me/favourites", request =>
            request.Body!.Contains(OtherServerProfile.ToString("D"), StringComparison.Ordinal)
                ? Answers.Problem(HttpStatusCode.NotFound, ServerErrorCodes.ProfileNotFound)
                : harness.Server.DefaultAnswer(request));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        IReadOnlyList<SentRequest> puts = harness.Sent(HttpMethod.Put, "/api/v1/me/favourites");
        Assert.Equal(2, puts.Count);
        Assert.Equal(ServerProfile, puts[1].Json.GetProperty("items")[0].GetProperty("profileId").GetGuid());
        Assert.Equal(1, puts[1].Json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task SynchronizeAsync_ShortcutOfAProfileTheServerDeleted_SendsTheShortcutsAgainWithoutIt()
    {
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        await AddServerProfileAsync(OtherServerProfile, "example-site-b", Configuration + "verb 4\n");
        harness.Server.Configurations.Remove(OtherServerProfile);

        await harness.ChangeAsync(context =>
        {
            context.HotkeyBindings.Add(new HotkeyBinding { ActionId = "ConnectProfile", Gesture = "Control+Alt+P", ProfileId = OtherServerProfile });
            return Task.CompletedTask;
        });

        await harness.Outbox.RecordAsync(PendingChangeKind.Hotkeys);

        harness.Server.On(HttpMethod.Put, "/api/v1/me/hotkeys", request =>
            request.Body!.Contains(OtherServerProfile.ToString("D"), StringComparison.Ordinal)
                ? Answers.Problem(HttpStatusCode.NotFound, ServerErrorCodes.ProfileNotFound)
                : harness.Server.DefaultAnswer(request));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        IReadOnlyList<SentRequest> puts = harness.Sent(HttpMethod.Put, "/api/v1/me/hotkeys");
        Assert.Equal(2, puts.Count);

        JsonElement kept = Assert.Single(puts[1].Json.GetProperty("items").EnumerateArray());
        Assert.Equal("Control+Alt+P", kept.GetProperty("gesture").GetString());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("profileId").ValueKind);
    }

    [Fact]
    public async Task SynchronizeAsync_Settings_AreSentWithoutIfMatchAndARefusalIsDropped()
    {
        await harness.SettingsBackend.UpdateAsync(settings => settings.Appearance.Theme = OpenVpnPilot.Core.Settings.ThemePreference.Dark);
        await harness.Outbox.RecordAsync(PendingChangeKind.Settings);
        harness.Server.On(HttpMethod.Put, "/api/v1/me/settings", _ => Answers.Problem(HttpStatusCode.BadRequest, ServerErrorCodes.ValidationFailed));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        SentRequest put = Assert.Single(harness.Sent(HttpMethod.Put, "/api/v1/me/settings"));
        Assert.Null(put.Header("If-Match"));
        Assert.Equal("Dark", put.Json.GetProperty("document").GetProperty("appearance").GetProperty("theme").GetString());
        Assert.Equal(1, result.Dropped);
    }

    [Fact]
    public async Task SynchronizeAsync_SharedSignIn_IsPostedOnce()
    {
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(ServerProfile, "Auth"), new StoredSecret("vpnuser", "typed"));
        await harness.Outbox.RecordAsync(PendingChangeKind.VaultAdd, ServerProfile, "Auth");

        harness.Server.On(HttpMethod.Post, $"/api/v1/profiles/{ServerProfile:D}/vault/Auth", _ =>
            Answers.Json(SyncServer.VaultEntry(ServerProfile, "Auth", "vpnuser", "typed"), HttpStatusCode.Created));
        harness.Server.Changes = since => SyncServer.Feed(since == 0, 1, profiles: harness.Server.Held(),
            vault: [SyncServer.VaultEntry(ServerProfile, "Auth", "vpnuser", "typed")]);

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        SentRequest post = Assert.Single(harness.Sent(HttpMethod.Post, $"/api/v1/profiles/{ServerProfile:D}/vault/Auth"));
        Assert.Equal("typed", post.Json.GetProperty("password").GetString());
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task SynchronizeAsync_SharedSignInAlreadyThere_StoresTheServersOne()
    {
        UseDeltas();
        await AddServerProfileAsync(ServerProfile, "example-site", Configuration);
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(ServerProfile, "Auth"), new StoredSecret("vpnuser", "typed"));
        await harness.Outbox.RecordAsync(PendingChangeKind.VaultAdd, ServerProfile, "Auth");

        harness.Server.On(HttpMethod.Post, $"/api/v1/profiles/{ServerProfile:D}/vault/Auth", _ =>
            Answers.Problem(HttpStatusCode.Conflict, ServerErrorCodes.VaultEntryExists));
        harness.Server.On(HttpMethod.Get, $"/api/v1/profiles/{ServerProfile:D}/vault", _ =>
            Answers.Json(new[] { SyncServer.VaultEntry(ServerProfile, "Auth", "shared", "from-the-vault") }));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(0, result.Dropped);
        Assert.Equal(new StoredSecret("shared", "from-the-vault"), harness.Secrets.Entries[SecretReference.ForProfile(ServerProfile, "Auth")]);
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    /// <summary>
    /// Answers with deltas. A complete answer removes every stored sign in of a server profile that
    /// the vault has no entry for, which is the point of a complete answer but not of these tests.
    /// </summary>
    private void UseDeltas() =>
        harness.Server.Changes = since => SyncServer.Feed(false, 1, profiles: harness.Server.Held());

    /// <summary>
    /// Profiles created while the server could not be reached, each with a configuration of its own.
    /// </summary>
    private async Task AddOfflineProfilesAsync(int count)
    {
        await using PilotDbContext context = await harness.Database.Factory.CreateDbContextAsync();

        for (int index = 0; index < count; index++)
        {
            string configuration = Configuration + "# " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
            Profile profile = new()
            {
                Name = "example-site-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Configuration = configuration,
                ContentHash = ProfileImporter.ComputeHash(configuration),
            };

            context.Profiles.Add(profile);
            await harness.Outbox.StageAsync(context, PendingChangeKind.ProfileCreate, profile.Id);
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The store the editor writes through, recording into the harness's outbox.
    /// </summary>
    private ProfileStore Store() =>
        new(harness.Database.Factory, TimeProvider.System, new ChangeRecorder(harness.Outbox, new FixedStorageMode(true)));

    private async Task AddServerProfileAsync(Guid id, string name, string configuration)
    {
        harness.Server.Hold(id, name, configuration);

        await harness.ChangeAsync(context =>
        {
            context.Profiles.Add(new Profile
            {
                Id = id,
                Name = name,
                Configuration = configuration,
                ContentHash = ProfileImporter.ComputeHash(configuration),
                Source = ProfileSource.Server,
            });

            return Task.CompletedTask;
        });
    }
}
