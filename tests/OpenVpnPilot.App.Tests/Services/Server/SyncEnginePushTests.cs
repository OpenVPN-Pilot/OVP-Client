using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
        Assert.Equal(ProfileSource.Manual, (await harness.ProfileAsync(local.Id))!.Source);
        Assert.Empty(await harness.Database.MarkersAsync());
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

        await harness.ChangeAsync(async context =>
            ProfileConfigurationFacts.Apply(await context.Profiles.SingleAsync(profile => profile.Id == ServerProfile), Edited));
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileUpdate, ServerProfile);

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        SentRequest put = Assert.Single(harness.Sent(HttpMethod.Put, $"/api/v1/profiles/{ServerProfile:D}"));
        Assert.Equal(Edited, put.Json.GetProperty("configuration").GetString());
        Assert.Equal(Edited, harness.Server.Configurations[ServerProfile]);
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
    public async Task SynchronizeAsync_TagRenameRefusedAsDuplicate_DropsIt()
    {
        Guid tagId = Guid.NewGuid();
        await harness.ChangeAsync(context =>
        {
            context.Tags.Add(new Tag { Id = tagId, Name = "Office" });
            return Task.CompletedTask;
        });

        await harness.Outbox.RecordAsync(PendingChangeKind.TagUpdate, tagId);
        harness.Server.On(HttpMethod.Put, $"/api/v1/tags/{tagId:D}", _ => Answers.Problem(HttpStatusCode.Conflict, ServerErrorCodes.TagDuplicate));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(1, result.Dropped);
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
