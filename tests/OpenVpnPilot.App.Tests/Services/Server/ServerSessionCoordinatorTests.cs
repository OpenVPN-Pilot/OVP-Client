using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using SyncStateRow = OpenVpnPilot.Data.Entities.SyncState;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// The session's place in the application: picked up at start without the network, the
/// synchronisation running while somebody is signed in, and a different person never inheriting the
/// previous person's waiting changes or personal data.
/// </summary>
public sealed class ServerSessionCoordinatorTests : IAsyncDisposable
{
    private readonly FakeSyncEngine engine = new();
    private readonly RecordingWipe wipe = new();
    private TestDatabase? database;
    private TestConnection? server;

    [Fact]
    public async Task SignIn_DifferentUserThanLastKnown_DiscardsWaitingChangesAndPersonalDataBeforeSynchronising()
    {
        (ServerSessionCoordinator coordinator, Profile favourite) = await ArrangeAsync(lastKnown: ServerAnswers.Alice, signsIn: ServerAnswers.Bob);
        int markersWhenStarted = -1;
        engine.OnStart = async () => markersWhenStarted = (await database!.MarkersAsync()).Count;

        ServerResult<CurrentUserResponse> answer = await coordinator.SignInAsync("bob", "secret");

        Assert.True(answer.IsSuccess);
        Assert.Equal(0, markersWhenStarted);
        Assert.Empty(await database!.MarkersAsync());

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();
        Profile stored = await context.Profiles.SingleAsync(profile => profile.Id == favourite.Id);
        Assert.False(stored.IsFavourite);
        Assert.Null(stored.FavouriteSlot);
        Assert.Empty(await context.HotkeyBindings.ToListAsync());

        SyncStateRow state = await context.SyncStates.SingleAsync();
        Assert.Null(state.Cursor);
        Assert.Equal(ServerAnswers.Bob.Id, state.UserId);
        Assert.Equal(ServerAnswers.Bob.Role, state.UserRole);
        Assert.Equal(["start"], engine.Calls);
    }

    [Fact]
    public async Task SignIn_SameUserAsLastKnown_KeepsWaitingChangesAndPersonalData()
    {
        (ServerSessionCoordinator coordinator, Profile favourite) = await ArrangeAsync(lastKnown: ServerAnswers.Alice, signsIn: ServerAnswers.Alice);

        await coordinator.SignInAsync("alice", "secret");

        Assert.Single(await database!.MarkersAsync());

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();
        Profile stored = await context.Profiles.SingleAsync(profile => profile.Id == favourite.Id);
        Assert.True(stored.IsFavourite);
        Assert.Equal(3, stored.FavouriteSlot);
        Assert.Single(await context.HotkeyBindings.ToListAsync());
        Assert.Equal(42, (await context.SyncStates.SingleAsync()).Cursor);
        Assert.Equal(["start"], engine.Calls);
    }

    [Fact]
    public async Task SignIn_Refused_DoesNotStartTheSynchronisationNorDiscardAnything()
    {
        (ServerSessionCoordinator coordinator, _) = await ArrangeAsync(
            lastKnown: ServerAnswers.Alice,
            signsIn: null);

        ServerResult<CurrentUserResponse> answer = await coordinator.SignInAsync("bob", "wrong");

        Assert.Equal(ServerErrorCodes.InvalidCredentials, answer.Code);
        Assert.Single(await database!.MarkersAsync());
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task Start_WithAStoredSession_PicksItUpWithTheLastKnownUserWithoutTheNetwork()
    {
        FakeSecrets secrets = new();
        await secrets.WriteAsync(SecretReference.ForServerRefreshToken(TestConnection.ServerKey), new StoredSecret(null, "refresh-stored"));
        (ServerSessionCoordinator coordinator, _) = await ArrangeAsync(lastKnown: ServerAnswers.Bob, signsIn: null, secrets: secrets);

        bool started = await coordinator.StartAsync();

        Assert.True(started);
        Assert.True(coordinator.IsSignedIn);
        Assert.Equal(ServerAnswers.Bob, coordinator.User);
        Assert.Empty(server!.Network.Requests);
        Assert.Equal(["start"], engine.Calls);
    }

    [Fact]
    public async Task Start_WithoutAStoredSession_LeavesTheSynchronisationStopped()
    {
        (ServerSessionCoordinator coordinator, _) = await ArrangeAsync(lastKnown: ServerAnswers.Bob, signsIn: null);

        Assert.False(await coordinator.StartAsync());
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task SignOut_StopsTheSynchronisationAndKeepsTheCopyAndItsWaitingChanges()
    {
        (ServerSessionCoordinator coordinator, _) = await ArrangeAsync(lastKnown: ServerAnswers.Alice, signsIn: ServerAnswers.Alice);
        await coordinator.SignInAsync("alice", "secret");

        await coordinator.SignOutAsync();

        Assert.Equal(["start", "stop"], engine.Calls);
        Assert.False(coordinator.IsSignedIn);
        Assert.Single(await database!.MarkersAsync());
        Assert.Contains("POST /api/v1/auth/logout", server!.Network.Requests);
    }

    [Fact]
    public async Task ConfirmSession_AfterARestartSignedInBySomebodyElse_DiscardsThePreviousPersonsData()
    {
        FakeSecrets secrets = new();
        await secrets.WriteAsync(SecretReference.ForServerRefreshToken(TestConnection.ServerKey), new StoredSecret(null, "refresh-stored"));
        (ServerSessionCoordinator coordinator, _) = await ArrangeAsync(lastKnown: ServerAnswers.Alice, signsIn: ServerAnswers.Bob, secrets: secrets);

        ServerResult<CurrentUserResponse> confirmed = await coordinator.ConfirmSessionAsync();

        Assert.True(confirmed.IsSuccess);
        Assert.Equal(ServerAnswers.Bob.Id, confirmed.Value.Id);
        Assert.Empty(await database!.MarkersAsync());
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task WipeDirective_OnAnyAnswer_HandsTheDirectiveToTheWipe()
    {
        FakeSecrets secrets = new();
        await secrets.WriteAsync(SecretReference.ForServerRefreshToken(TestConnection.ServerKey), new StoredSecret(null, "refresh-stored"));
        (ServerSessionCoordinator coordinator, _) = await ArrangeAsync(
            lastKnown: ServerAnswers.Alice,
            signsIn: null,
            secrets: secrets,
            refreshWipes: true);

        ServerResult<CurrentUserResponse> confirmed = await coordinator.ConfirmSessionAsync();

        Assert.Equal(ServerOutcome.Wiped, confirmed.Outcome);
        ServerWipeDirective directive = await wipe.Asked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(401, directive.Status);
    }

    public async ValueTask DisposeAsync()
    {
        server?.Dispose();

        if (database is not null)
        {
            await database.DisposeAsync();
        }
    }

    /// <summary>
    /// A copy that last knew one person, with a change waiting, a favourite in a slot, a shortcut
    /// and a cursor, and a server that signs in the given person or refuses.
    /// </summary>
    private async Task<(ServerSessionCoordinator Coordinator, Profile Favourite)> ArrangeAsync(
        CurrentUserResponse lastKnown,
        CurrentUserResponse? signsIn,
        FakeSecrets? secrets = null,
        bool refreshWipes = false)
    {
        database = await TestDatabase.CreateAsync();
        Profile favourite = await database.AddProfileAsync("example-site");

        await using (PilotDbContext context = await database.Factory.CreateDbContextAsync())
        {
            Profile tracked = await context.Profiles.SingleAsync(profile => profile.Id == favourite.Id);
            tracked.IsFavourite = true;
            tracked.FavouriteSlot = 3;
            context.HotkeyBindings.Add(new HotkeyBinding { ActionId = "ConnectFavourite3", Gesture = "Control+Alt+3", ProfileId = favourite.Id });
            context.PendingChanges.Add(new PendingChange { Kind = PendingChangeKind.Favourites, CreatedAt = DateTimeOffset.UtcNow });
            context.SyncStates.Add(new SyncStateRow
            {
                Cursor = 42,
                UserId = lastKnown.Id,
                Username = lastKnown.Username,
                UserDisplayName = lastKnown.DisplayName,
                UserRole = lastKnown.Role,
                UserProvider = lastKnown.Provider,
            });

            await context.SaveChangesAsync();
        }

        server = new TestConnection(
            request => request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/auth/login" => signsIn is null
                    ? ServerAnswers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.InvalidCredentials)
                    : ServerAnswers.Tokens(signsIn),
                "/api/v1/auth/refresh" => refreshWipes
                    ? ServerAnswers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.AccountRevoked, wipe: true)
                    : ServerAnswers.Tokens(signsIn ?? lastKnown),
                "/api/v1/auth/me" => ServerAnswers.Json(signsIn ?? lastKnown),
                "/api/v1/auth/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            },
            secrets);

        Outbox outbox = new(database.Factory, TimeProvider.System, NullLogger<Outbox>.Instance);
        ServerAccountState account = new(database.Factory, outbox, NullLogger<ServerAccountState>.Instance);

        ServerSessionCoordinator coordinator = new(
            server.Connection,
            engine,
            account,
            wipe,
            NullLogger<ServerSessionCoordinator>.Instance);

        return (coordinator, favourite);
    }
}
