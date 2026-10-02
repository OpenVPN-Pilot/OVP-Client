using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// A profile created offline takes the server's id with everything that hangs on it, or not at all.
/// </summary>
public sealed class ServerProfileMaintenanceTests : IAsyncLifetime
{
    private const string Configuration = "client\nremote vpn.example.com 1194\nauth-user-pass creds.txt\n";

    private static readonly StoredSecret SignIn = new("vpnuser", "local-secret");
    private static readonly StoredSecret Passphrase = new(null, "key-passphrase");

    private TestDatabase database = null!;
    private Outbox outbox = null!;
    private readonly TypedCredentials held = new(new FixedStorageMode(true));

    private Guid temporaryId;
    private Guid otherProfileId;
    private readonly Guid serverId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        database = await TestDatabase.CreateAsync();
        outbox = new Outbox(database.Factory, TimeProvider.System, NullLogger<Outbox>.Instance);

        Profile temporary = await database.AddProfileAsync("example-site", Configuration);
        Profile other = await database.AddProfileAsync("example-site-b");
        temporaryId = temporary.Id;
        otherProfileId = other.Id;

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();

        Profile tracked = await context.Profiles.SingleAsync(profile => profile.Id == temporaryId);
        tracked.IsFavourite = true;
        tracked.FavouriteSlot = 3;
        tracked.ConnectCount = 2;
        tracked.LastConnectedAt = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

        Tag office = new() { Name = "Office" };
        context.Tags.Add(office);
        context.ProfileTags.Add(new ProfileTag { ProfileId = temporaryId, TagId = office.Id });

        context.Sessions.Add(new Session { ProfileId = temporaryId, StartedAt = DateTimeOffset.UtcNow.AddHours(-2) });
        context.Sessions.Add(new Session { ProfileId = temporaryId, StartedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        context.Sessions.Add(new Session { ProfileId = otherProfileId, StartedAt = DateTimeOffset.UtcNow });

        context.HotkeyBindings.Add(new HotkeyBinding { ActionId = "ConnectProfile", Gesture = "Control+Alt+P", ProfileId = temporaryId });

        await context.SaveChangesAsync();

        await outbox.RecordAsync(PendingChangeKind.ProfileUpdate, otherProfileId);
        await outbox.RecordAsync(PendingChangeKind.ProfileCreate, temporaryId);
        await outbox.RecordAsync(PendingChangeKind.VaultAdd, temporaryId, "Auth");
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task RekeyAsync_ProfileCreatedOffline_MovesEverythingToTheServerId()
    {
        FakeSecrets secrets = await SeedAsync(new FakeSecrets());

        ProfileRekeyOutcome outcome = await CreateMaintenance(secrets).RekeyAsync(temporaryId, serverId);

        Assert.Equal(ProfileRekeyOutcome.Moved, outcome);

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();

        Assert.Null(await context.Profiles.FindAsync(temporaryId));

        Profile moved = await context.Profiles.SingleAsync(profile => profile.Id == serverId);
        Assert.Equal("example-site", moved.Name);
        Assert.Equal(Configuration, moved.Configuration);
        Assert.Equal(ProfileImporter.ComputeHash(Configuration), moved.ContentHash);
        Assert.Equal(ProfileSource.Server, moved.Source);
        Assert.True(moved.IsFavourite);
        Assert.Equal(3, moved.FavouriteSlot);
        Assert.Equal(2, moved.ConnectCount);
        Assert.NotNull(moved.LastConnectedAt);

        Assert.Equal(2, await context.Sessions.CountAsync(session => session.ProfileId == serverId));
        Assert.Equal(1, await context.Sessions.CountAsync(session => session.ProfileId == otherProfileId));
        Assert.Equal("Office", (await context.ProfileTags.Include(link => link.Tag).SingleAsync(link => link.ProfileId == serverId)).Tag!.Name);
        Assert.Equal(serverId, (await context.HotkeyBindings.SingleAsync()).ProfileId);

        Assert.Equal(
            [
                (PendingChangeKind.ProfileUpdate, (Guid?)otherProfileId, (string?)null),
                (PendingChangeKind.VaultAdd, serverId, "Auth"),
                (PendingChangeKind.Favourites, null, null),
                (PendingChangeKind.Hotkeys, null, null),
            ],
            (await database.MarkersAsync()).Select(change => (change.Kind, change.EntityId, change.Realm)));

        Assert.Equal(SignIn, await secrets.TryReadAsync(SecretReference.ForProfile(serverId, "Auth")));
        Assert.Equal(Passphrase, await secrets.TryReadAsync(SecretReference.ForProfile(serverId, "Private Key")));
        Assert.Null(await secrets.TryReadAsync(SecretReference.ForProfile(temporaryId, "Auth")));
        Assert.Null(await secrets.TryReadAsync(SecretReference.ForProfile(temporaryId, "Private Key")));
        Assert.NotNull(await secrets.TryReadAsync(SecretReference.ForProfile(otherProfileId, "Auth")));
    }

    [Fact]
    public async Task RekeyAsync_KeystoreFailsHalfway_LeavesEverythingAsBefore()
    {
        FailingSecrets secrets = new(failOnWrite: 2);
        await secrets.SeedAsync(SecretReference.ForProfile(temporaryId, "Auth"), SignIn);
        await secrets.SeedAsync(SecretReference.ForProfile(temporaryId, "Private Key"), Passphrase);

        List<PendingChange> markersBefore = await database.MarkersAsync();

        await Assert.ThrowsAsync<IOException>(() => CreateMaintenance(secrets).RekeyAsync(temporaryId, serverId));

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();

        Assert.Null(await context.Profiles.FindAsync(serverId));

        Profile kept = await context.Profiles.SingleAsync(profile => profile.Id == temporaryId);
        Assert.Equal(3, kept.FavouriteSlot);
        Assert.NotEqual(ProfileSource.Server, kept.Source);
        Assert.Equal(2, await context.Sessions.CountAsync(session => session.ProfileId == temporaryId));
        Assert.Equal(1, await context.ProfileTags.CountAsync(link => link.ProfileId == temporaryId));
        Assert.Equal(temporaryId, (await context.HotkeyBindings.SingleAsync()).ProfileId);

        Assert.Equal(
            markersBefore.Select(change => (change.Id, change.Kind, change.EntityId, change.Realm)),
            (await database.MarkersAsync()).Select(change => (change.Id, change.Kind, change.EntityId, change.Realm)));

        Assert.Equal(
            [SecretReference.ForProfile(temporaryId, "Auth"), SecretReference.ForProfile(temporaryId, "Private Key")],
            (await secrets.ListAsync()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RekeyAsync_SignInHeldInMemory_MovesToTheServerIdOnlyWhenTheRekeyHappens()
    {
        StoredSecret typed = new("vpnuser", "typed-not-remembered");
        held.Hold(temporaryId, "Auth", typed);
        FailingSecrets failing = new(failOnWrite: 1);
        await failing.SeedAsync(SecretReference.ForProfile(temporaryId, "Private Key"), Passphrase);

        await Assert.ThrowsAsync<IOException>(() => CreateMaintenance(failing).RekeyAsync(temporaryId, serverId));

        Assert.Equal(typed, held.Peek(temporaryId, "Auth"));
        Assert.Null(held.Peek(serverId, "Auth"));

        await CreateMaintenance(new FakeSecrets()).RekeyAsync(temporaryId, serverId);

        Assert.Null(held.Peek(temporaryId, "Auth"));
        Assert.Equal(typed, held.Peek(serverId, "Auth"));
    }

    [Fact]
    public async Task RekeyAsync_AfterAFailure_SucceedsWhenTriedAgain()
    {
        FakeSecrets secrets = await SeedAsync(new FakeSecrets());
        FailingSecrets failing = new(failOnWrite: 1);
        await failing.SeedAsync(SecretReference.ForProfile(temporaryId, "Auth"), SignIn);

        await Assert.ThrowsAsync<IOException>(() => CreateMaintenance(failing).RekeyAsync(temporaryId, serverId));

        Assert.Equal(ProfileRekeyOutcome.Moved, await CreateMaintenance(secrets).RekeyAsync(temporaryId, serverId));
    }

    [Fact]
    public async Task RekeyAsync_ServerCopyAlreadyHeld_FoldsTheTemporaryProfileIntoIt()
    {
        string stored = ServerContentHash.Normalise(Configuration);
        Guid tagId;

        await using (PilotDbContext setup = await database.Factory.CreateDbContextAsync())
        {
            Profile server = new()
            {
                Id = serverId,
                Name = "example-site (server)",
                Configuration = stored,
                ContentHash = ProfileImporter.ComputeHash(stored),
                Source = ProfileSource.Server,
                ConnectCount = 1,
            };

            Tag europe = new() { Name = "Europe" };
            tagId = europe.Id;
            setup.Profiles.Add(server);
            setup.Tags.Add(europe);
            setup.ProfileTags.Add(new ProfileTag { ProfileId = serverId, TagId = europe.Id });
            await setup.SaveChangesAsync();
        }

        FakeSecrets secrets = await SeedAsync(new FakeSecrets());
        StoredSecret shared = new("vpnuser", "shared-secret");
        await secrets.WriteAsync(SecretReference.ForProfile(serverId, "Auth"), shared);

        ServerProfileMaintenance maintenance = CreateMaintenance(secrets);

        Assert.Equal(serverId, await maintenance.FindServerDuplicateAsync(temporaryId));
        Assert.Equal(ProfileRekeyOutcome.Merged, await maintenance.RekeyAsync(temporaryId, serverId));

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();

        Profile merged = await context.Profiles.SingleAsync(profile => profile.Id == serverId);
        Assert.Equal("example-site (server)", merged.Name);
        Assert.Equal(stored, merged.Configuration);
        Assert.True(merged.IsFavourite);
        Assert.Equal(3, merged.FavouriteSlot);
        Assert.Equal(3, merged.ConnectCount);
        Assert.Null(await context.Profiles.FindAsync(temporaryId));

        Assert.Equal(2, await context.Sessions.CountAsync(session => session.ProfileId == serverId));
        Assert.Equal([tagId], await context.ProfileTags.Where(link => link.ProfileId == serverId).Select(link => link.TagId).ToListAsync());

        Assert.Equal(shared, await secrets.TryReadAsync(SecretReference.ForProfile(serverId, "Auth")));
        Assert.Equal(Passphrase, await secrets.TryReadAsync(SecretReference.ForProfile(serverId, "Private Key")));
        Assert.DoesNotContain(await secrets.ListAsync(), reference => SecretReference.BelongsToProfile(reference, temporaryId));
    }

    [Fact]
    public async Task RekeyAsync_NoProfileUnderTheTemporaryId_ReturnsNotFound()
    {
        ServerProfileMaintenance maintenance = CreateMaintenance(new FakeSecrets());

        Assert.Equal(ProfileRekeyOutcome.NotFound, await maintenance.RekeyAsync(Guid.NewGuid(), serverId));
    }

    [Fact]
    public async Task FindServerDuplicateAsync_OnlyALocalProfileMatches_ReturnsNull()
    {
        await database.AddProfileAsync("example-site-copy", ServerContentHash.Normalise(Configuration));

        Assert.Null(await CreateMaintenance(new FakeSecrets()).FindServerDuplicateAsync(temporaryId));
    }

    [Fact]
    public async Task DeleteSecretsAsync_RemovesEveryRealmOfTheGivenProfilesOnly()
    {
        FakeSecrets secrets = await SeedAsync(new FakeSecrets());
        await secrets.WriteAsync("server/0123456789abcdef0123456789abcdef/refresh", new StoredSecret(null, "token"));

        int removed = await CreateMaintenance(secrets).DeleteSecretsAsync([temporaryId]);

        Assert.Equal(2, removed);
        Assert.Equal(
            [SecretReference.ForProfile(otherProfileId, "Auth"), "server/0123456789abcdef0123456789abcdef/refresh"],
            (await secrets.ListAsync()).Order(StringComparer.Ordinal));
    }

    private async Task<FakeSecrets> SeedAsync(FakeSecrets secrets)
    {
        await secrets.WriteAsync(SecretReference.ForProfile(temporaryId, "Auth"), SignIn);
        await secrets.WriteAsync(SecretReference.ForProfile(temporaryId, "Private Key"), Passphrase);
        await secrets.WriteAsync(SecretReference.ForProfile(otherProfileId, "Auth"), new StoredSecret("other", "other-secret"));
        return secrets;
    }

    private ServerProfileMaintenance CreateMaintenance(ISecretStore secrets) => new(
        database.Factory,
        secrets,
        held,
        outbox,
        NullLogger<ServerProfileMaintenance>.Instance);
}
