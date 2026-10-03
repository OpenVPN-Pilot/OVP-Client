using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// What a person who is not the server's administrator is offered, and what deleting a server
/// profile takes with it.
/// </summary>
public sealed class RoleTests : IAsyncLifetime
{
    private const string Configuration = "client\nremote vpn.example.com 1194 udp\n<ca>\nA\n</ca>\n";

    private SyncHarness harness = null!;

    public async Task InitializeAsync() => harness = await SyncHarness.CreateAsync();

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Theory]
    [InlineData(ServerRoles.Admin, true)]
    [InlineData("user", false)]
    public async Task Permissions_FollowTheRoleLastKnown(string role, bool expected)
    {
        ServerAccountState account = new(harness.Database.Factory, harness.Outbox, harness.Maintenance, NullLogger<ServerAccountState>.Instance);
        using ServerLibraryPermissions permissions = new(account, harness.Engine, NullLogger<ServerLibraryPermissions>.Instance);

        Assert.False(permissions.CanChangeShared);

        await account.RememberUserAsync(new CurrentUserResponse(Guid.NewGuid(), "someone", null, role, "file"));
        await permissions.RefreshAsync();

        Assert.Equal(expected, permissions.CanChangeShared);
    }

    [Fact]
    public async Task Editor_ForSomebodyWhoIsNotTheAdministrator_SavesOnlyTheFavouriteAndCannotDelete()
    {
        FakeProfileStore store = new();
        Profile profile = store.Add("example-site");
        profile.Configuration = Configuration;

        ProfileEditorViewModel editor = new(
            store,
            new StubLocalizer(),
            new ProfileItemViewModel(profile, new StubLocalizer()),
            startsInPlainText: true,
            canChangeShared: false,
            sharedSignIns: new NoReplacement());

        await editor.LoadAsync();

        Assert.True(editor.IsReadOnly);
        Assert.False(editor.IsPlainText);
        Assert.False(editor.CanReplaceSharedSignIn);

        editor.Port = 443;
        editor.IsFavourite = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Empty(store.ConfigurationUpdates);
        Assert.True(profile.IsFavourite);

        editor.RequestDeleteCommand.Execute(null);
        Assert.False(editor.IsConfirmingDelete);
        await editor.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task DeletingOnAServersCopy_RemovesTheStoredSignInsAndRecordsTheDelete()
    {
        Profile profile = await harness.Database.AddProfileAsync("example-site", Configuration);
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(profile.Id, "Auth"), new StoredSecret("vpnuser", "shared"));

        ProfileStore store = new(
            harness.Database.Factory,
            TimeProvider.System,
            new ChangeRecorder(harness.Outbox, new FixedStorageMode(true)),
            harness.Maintenance);

        await store.DeleteProfileAsync(profile.Id);

        Assert.Empty(await store.GetProfilesAsync());
        Assert.False(harness.Secrets.Entries.ContainsKey(SecretReference.ForProfile(profile.Id, "Auth")));
        Assert.Equal(PendingChangeKind.ProfileDelete, Assert.Single(await harness.Database.MarkersAsync()).Kind);
    }

    private sealed class NoReplacement : ISharedSignInReplacement
    {
        public Task<ServerResult> ReplaceAsync(Guid profileId, string realm, string? username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nobody who is not an administrator gets this far.");
    }
}
