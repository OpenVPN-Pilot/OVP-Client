using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using SyncState = OpenVpnPilot.App.Services.Server.SyncState;

namespace OpenVpnPilot.Server.IntegrationTests;

/// <summary>
/// The client against a real server: signing in, uploading, synchronising, going offline and coming
/// back, two machines meeting in the vault, and an account taken away.
/// </summary>
/// <remarks>
/// One class, so the tests run one after the other: one of them stops the server. Every test makes
/// profiles with names of its own and removes them again, so the server can be used again and again.
/// </remarks>
public sealed class ServerIntegrationTests
{
    [ServerFact]
    public async Task SignIn_AdministratorAndUser_GetTheirRoles()
    {
        await using TestClient administrator = await TestClient.CreateAsync();
        await using TestClient user = await TestClient.CreateAsync();

        Assert.Equal(ServerRoles.Admin, (await administrator.SignInAsync(TestServer.Administrator, TestServer.AdministratorPassword)).Role);
        Assert.Equal(ServerRoles.User, (await user.SignInAsync(TestServer.User, TestServer.UserPassword)).Role);

        await using TestClient stranger = await TestClient.CreateAsync();
        ServerResult<CurrentUserResponse> wrong = await stranger.Connection.SignIn.SignInAsync(TestServer.User, "not the password");
        Assert.Equal(ServerErrorCodes.InvalidCredentials, wrong.Code);
    }

    [ServerFact]
    public async Task Upload_ProfilesCreatedOffline_GoUpInOneBatchUnderTheServersIds()
    {
        await using TestClient administrator = await TestClient.CreateAsync();
        await administrator.SignInAsync(TestServer.Administrator, TestServer.AdministratorPassword);

        string[] names = [TestServer.UniqueName("batch-a"), TestServer.UniqueName("batch-b"), TestServer.UniqueName("batch-c")];

        foreach (string name in names)
        {
            await administrator.CreateOfflineAsync(name);
        }

        try
        {
            SyncCycleResult result = await administrator.SyncAsync();

            Assert.True(result.Pushed >= names.Length);
            Assert.Single(administrator.Requests, request => request == "POST /api/v1/profiles/batch");

            IReadOnlyList<ProfileResponse> held = (await administrator.Api.GetProfilesAsync()).Value;

            foreach (string name in names)
            {
                Profile local = (await administrator.ProfileNamedAsync(name))!;
                Assert.Equal(ProfileSource.Server, local.Source);
                Assert.Contains(held, profile => profile.Id == local.Id && profile.Name == name);
            }
        }
        finally
        {
            await DeleteAsync(administrator, names);
        }
    }

    [ServerFact]
    public async Task FullThenDelta_ASecondClient_SeesCreatesRenamesAndDeletions()
    {
        await using TestClient administrator = await TestClient.CreateAsync();
        await using TestClient user = await TestClient.CreateAsync();
        await administrator.SignInAsync(TestServer.Administrator, TestServer.AdministratorPassword);
        await user.SignInAsync(TestServer.User, TestServer.UserPassword);

        string name = TestServer.UniqueName("feed");
        string renamed = name + "-renamed";

        try
        {
            await administrator.CreateOfflineAsync(name);
            await administrator.SyncAsync();
            Guid id = (await administrator.ProfileNamedAsync(name))!.Id;

            // The first synchronisation of a copy is a complete one.
            await user.SyncAsync();
            Assert.Contains("GET /api/v1/sync/changes?since=0", user.Requests);
            Assert.Equal(name, (await user.ProfileAsync(id))!.Name);

            long cursor = (await CursorAsync(user))!.Value;

            await administrator.Store.RenameProfileAsync(id, renamed);
            await administrator.SyncAsync();

            // Every later one asks only for what changed since.
            await user.SyncAsync();
            Assert.Contains($"GET /api/v1/sync/changes?since={cursor.ToString(CultureInfo.InvariantCulture)}", user.Requests);
            Assert.Equal(renamed, (await user.ProfileAsync(id))!.Name);

            await administrator.Store.DeleteProfileAsync(id);
            await administrator.SyncAsync();

            await user.SyncAsync();
            Assert.Null(await user.ProfileAsync(id));
        }
        finally
        {
            await DeleteAsync(administrator, [name, renamed]);
        }
    }

    [ServerFact]
    public async Task Vault_SecondMachineSharingTheSameSignIn_TakesTheOneSharedFirst()
    {
        await using TestClient administrator = await TestClient.CreateAsync();
        await using TestClient user = await TestClient.CreateAsync();
        await administrator.SignInAsync(TestServer.Administrator, TestServer.AdministratorPassword);
        await user.SignInAsync(TestServer.User, TestServer.UserPassword);

        string name = TestServer.UniqueName("vault");

        try
        {
            await administrator.CreateOfflineAsync(name);
            await administrator.SyncAsync();
            Guid id = (await administrator.ProfileNamedAsync(name))!.Id;
            await user.SyncAsync();

            // Typed on the first machine and shared from there.
            administrator.Held.Hold(id, "Auth", new StoredSecret("vpnuser", "first-shared"));
            await administrator.Outbox.RecordAsync(PendingChangeKind.VaultAdd, id, "Auth");
            await administrator.SyncAsync();
            Assert.Contains($"POST /api/v1/profiles/{id:D}/vault/Auth", administrator.Requests);

            // Typed on the second machine before it heard of the first one's.
            user.Held.Hold(id, "Auth", new StoredSecret("vpnuser", "second-typed"));
            await user.Outbox.RecordAsync(PendingChangeKind.VaultAdd, id, "Auth");
            SyncCycleResult result = await user.SyncAsync();

            Assert.Equal(0, result.Dropped);
            Assert.Contains($"GET /api/v1/profiles/{id:D}/vault", user.Requests);
            Assert.Equal("first-shared", (await user.Secrets.TryReadAsync(SecretReference.ForProfile(id, "Auth")))!.Password);
        }
        finally
        {
            await DeleteAsync(administrator, [name]);
        }
    }

    [ServerFact(TestServer.ApiContainerVariable)]
    public async Task Offline_EditsAndFavourites_ArePushedOnceTheServerIsBack()
    {
        await using TestClient administrator = await TestClient.CreateAsync();
        await using TestClient user = await TestClient.CreateAsync();
        await administrator.SignInAsync(TestServer.Administrator, TestServer.AdministratorPassword);
        await user.SignInAsync(TestServer.User, TestServer.UserPassword);

        string name = TestServer.UniqueName("offline");
        string renamed = name + "-renamed";

        try
        {
            await administrator.CreateOfflineAsync(name);
            await administrator.SyncAsync();
            Guid id = (await administrator.ProfileNamedAsync(name))!.Id;
            await user.SyncAsync();

            await TestServer.DockerAsync("stop", TestServer.ApiContainer!);

            try
            {
                await administrator.Store.RenameProfileAsync(id, renamed);
                await user.Store.SetFavouriteAsync(id, true);

                Assert.Equal(SyncState.Offline, (await administrator.Engine.SynchronizeAsync(CancellationToken.None)).State);
                Assert.Equal(SyncState.Offline, (await user.Engine.SynchronizeAsync(CancellationToken.None)).State);

                // Worked on from the copy meanwhile.
                Assert.Equal(renamed, (await administrator.ProfileAsync(id))!.Name);
                Assert.True((await user.ProfileAsync(id))!.IsFavourite);
            }
            finally
            {
                await TestServer.DockerAsync("start", TestServer.ApiContainer!);
                await WaitForServerAsync(administrator);
            }

            await administrator.SyncAsync();
            await user.SyncAsync();

            Assert.Equal(renamed, (await user.ProfileAsync(id))!.Name);
            Assert.Contains((await user.Api.GetFavouritesAsync()).Value.Items, item => item.ProfileId == id);
            Assert.Empty(await user.Outbox.GetPendingAsync());
            Assert.Empty(await administrator.Outbox.GetPendingAsync());
        }
        finally
        {
            await DeleteAsync(administrator, [name, renamed]);
        }
    }

    [ServerFact]
    public async Task Wipe_AccountDisabledByAnAdministrator_ReachesTheClientOnItsNextCall()
    {
        await using TestClient administrator = await TestClient.CreateAsync();
        await using TestClient revocable = await TestClient.CreateAsync();
        await administrator.SignInAsync(TestServer.Administrator, TestServer.AdministratorPassword);
        CurrentUserResponse carol = await revocable.SignInAsync(TestServer.Revocable, TestServer.RevocablePassword);
        await revocable.SyncAsync();

        try
        {
            Assert.True((await administrator.Api.DisableUserAsync(carol.Id)).IsSuccess);

            SyncCycleResult result = await revocable.Engine.SynchronizeAsync(CancellationToken.None);

            Assert.False(result.Completed);
            Assert.True(revocable.Connection.Wipe.IsRequested);
            Assert.Equal(401, revocable.Connection.Wipe.Directive!.Status);

            // Nothing more is sent to a server that said so.
            int sent = revocable.Requests.Count;
            await revocable.Engine.SynchronizeAsync(CancellationToken.None);
            Assert.Equal(sent, revocable.Requests.Count);
        }
        finally
        {
            Assert.True((await administrator.Api.EnableUserAsync(carol.Id)).IsSuccess);
        }
    }

    [ServerFact(TestServer.DatabaseContainerVariable)]
    public async Task CursorExpired_TheClientStartsOverWithACompleteSynchronisation()
    {
        await using TestClient administrator = await TestClient.CreateAsync();
        await administrator.SignInAsync(TestServer.Administrator, TestServer.AdministratorPassword);
        await administrator.SyncAsync();

        string name = TestServer.UniqueName("cursor");

        try
        {
            // A change, so the cursor this copy holds is not the very first.
            await administrator.CreateOfflineAsync(name);
            await administrator.SyncAsync();
            long cursor = (await CursorAsync(administrator))!.Value;

            // As if tombstones older than the cursor had been pruned by the server's maintenance.
            await PrunedThroughAsync(cursor + 1);

            try
            {
                await administrator.SyncAsync();
            }
            finally
            {
                await PrunedThroughAsync(0);
            }

            string[] pulls = [.. administrator.Requests.Where(request => request.StartsWith("GET /api/v1/sync/changes", StringComparison.Ordinal))];
            Assert.Equal($"GET /api/v1/sync/changes?since={cursor.ToString(CultureInfo.InvariantCulture)}", pulls[^2]);
            Assert.Equal("GET /api/v1/sync/changes?since=0", pulls[^1]);
            Assert.NotNull(await administrator.ProfileNamedAsync(name));
        }
        finally
        {
            await DeleteAsync(administrator, [name]);
        }
    }

    private static async Task<long?> CursorAsync(TestClient client)
    {
        await using PilotDbContext context = await client.Database.CreateDbContextAsync();
        return await context.SyncStates.Select(state => state.Cursor).SingleAsync();
    }

    private static Task PrunedThroughAsync(long value) =>
        TestServer.DockerAsync(
            "exec",
            TestServer.DatabaseContainer!,
            "psql",
            "-U",
            "ovp",
            "-d",
            "ovp",
            "-c",
            $"UPDATE sync_states SET pruned_through = {value.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Removes what a test put on the server, by name, whatever became of it.
    /// </summary>
    private static async Task DeleteAsync(TestClient administrator, IReadOnlyCollection<string> names)
    {
        ServerResult<IReadOnlyList<ProfileResponse>> held = await administrator.Api.GetProfilesAsync();

        foreach (ProfileResponse profile in (held.Value ?? []).Where(profile => names.Contains(profile.Name)))
        {
            await administrator.Api.DeleteProfileAsync(profile.Id);
        }
    }

    private static async Task WaitForServerAsync(TestClient client)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddMinutes(2);

        while ((await client.Api.GetReadinessAsync()).Outcome != ServerOutcome.Success)
        {
            if (DateTimeOffset.UtcNow > until)
            {
                Assert.Fail("The server did not come back within two minutes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}
