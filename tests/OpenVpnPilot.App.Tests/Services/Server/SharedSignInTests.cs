using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// Sharing a sign in that worked, and what the synchronisation does to sign ins and tunnels.
/// </summary>
public sealed class SharedSignInTests : IAsyncLifetime
{
    private const string Configuration = "client\nremote vpn.example.com 1194\n<ca>\nA\n</ca>\n";

    private static readonly Guid ProfileA = Guid.Parse("5a000000-0000-0000-0000-00000000000a");
    private static readonly Guid ProfileB = Guid.Parse("5b000000-0000-0000-0000-00000000000b");

    private SyncHarness harness = null!;

    public async Task InitializeAsync() => harness = await SyncHarness.CreateAsync();

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task TypedAndConnected_RecordsOneVaultMarker()
    {
        await using Fixture fixture = new(harness, serverMode: true);

        fixture.Ledger.NoteTyped(Request(ProfileA), new VpnCredentials("vpnuser", "typed"), remembered: true);
        await fixture.Recorder.HandleAsync(Changed(ProfileA, VpnConnectionState.Connected), CancellationToken.None);
        await fixture.Recorder.HandleAsync(Changed(ProfileA, VpnConnectionState.Connected), CancellationToken.None);

        PendingChange marker = Assert.Single(await harness.Database.MarkersAsync());
        Assert.Equal(PendingChangeKind.VaultAdd, marker.Kind);
        Assert.Equal(ProfileA, marker.EntityId);
        Assert.Equal("Auth", marker.Realm);
    }

    [Fact]
    public async Task ReadFromTheKeystoreOrNeedingACodeOrFailing_RecordsNothing()
    {
        await using Fixture fixture = new(harness, serverMode: true);

        fixture.Ledger.NoteTyped(Request(ProfileA), new VpnCredentials("vpnuser", "typed"), remembered: true);
        fixture.Ledger.NoteStored(Request(ProfileA));
        await fixture.Recorder.HandleAsync(Changed(ProfileA, VpnConnectionState.Connected), CancellationToken.None);

        fixture.Ledger.NoteTyped(Request(ProfileB), new VpnCredentials("vpnuser", "typed"), remembered: true);
        fixture.Ledger.NoteTyped(
            Request(ProfileB) with { Challenge = new CredentialChallenge("Code", EchoResponse: true, IsDynamic: true) },
            new VpnCredentials("vpnuser", "typed", "123456"),
            remembered: false);
        await fixture.Recorder.HandleAsync(Changed(ProfileB, VpnConnectionState.Connected), CancellationToken.None);

        Guid failing = Guid.NewGuid();
        fixture.Ledger.NoteTyped(Request(failing), new VpnCredentials("vpnuser", "wrong"), remembered: false);
        await fixture.Recorder.HandleAsync(Changed(failing, VpnConnectionState.Failed), CancellationToken.None);
        await fixture.Recorder.HandleAsync(Changed(failing, VpnConnectionState.Connected), CancellationToken.None);

        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task OnTheLocalLibrary_NothingIsKeptOrRecorded()
    {
        await using Fixture fixture = new(harness, serverMode: false);

        fixture.Ledger.NoteTyped(Request(ProfileA), new VpnCredentials("vpnuser", "typed"), remembered: false);
        await fixture.Recorder.HandleAsync(Changed(ProfileA, VpnConnectionState.Connected), CancellationToken.None);

        Assert.Empty(await harness.Database.MarkersAsync());
        Assert.Null(fixture.Ledger.Peek(ProfileA, "Auth"));
    }

    [Fact]
    public async Task NotRemembered_IsHeldInMemorySentAndThenForgotten()
    {
        await AddServerProfileAsync(ProfileA);
        harness.Server.Changes = since => SyncServer.Feed(false, 1, profiles: harness.Server.Held());
        harness.Server.On(HttpMethod.Post, $"/api/v1/profiles/{ProfileA:D}/vault/Auth", _ =>
            Answers.Json(SyncServer.VaultEntry(ProfileA, "Auth", "vpnuser", "typed"), HttpStatusCode.Created));

        await using Fixture fixture = new(harness, serverMode: true, ledger: harness.Held);
        harness.Held.NoteTyped(Request(ProfileA), new VpnCredentials("vpnuser", "typed"), remembered: false);
        await fixture.Recorder.HandleAsync(Changed(ProfileA, VpnConnectionState.Connected), CancellationToken.None);

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(1, result.Pushed);
        SentRequest post = Assert.Single(harness.Sent(HttpMethod.Post, $"/api/v1/profiles/{ProfileA:D}/vault/Auth"));
        Assert.Equal("typed", post.Json.GetProperty("password").GetString());

        // Never written to the keystore the person declined, and gone from memory once sent.
        Assert.False(harness.Secrets.Entries.ContainsKey(SecretReference.ForProfile(ProfileA, "Auth")));
        Assert.Null(harness.Held.Peek(ProfileA, "Auth"));
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task CompleteAnswer_KeepsASignInWaitingToBeSharedAndRemovesTheRest()
    {
        await AddServerProfileAsync(ProfileA);
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(ProfileA, "Auth"), new StoredSecret("vpnuser", "typed-here"));
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(ProfileA, "Key"), new StoredSecret(null, "stale"));
        await harness.Outbox.RecordAsync(PendingChangeKind.VaultAdd, ProfileA, "Auth");

        harness.Server.Changes = since => SyncServer.Feed(true, 3, profiles: harness.Server.Held());

        Assert.Null(await Puller().PullAsync(new SyncCycle(), CancellationToken.None));

        Assert.Equal("typed-here", harness.Secrets.Entries[SecretReference.ForProfile(ProfileA, "Auth")].Password);
        Assert.False(harness.Secrets.Entries.ContainsKey(SecretReference.ForProfile(ProfileA, "Key")));
    }

    [Fact]
    public async Task ProfileDeletedOnTheServerWhileConnected_EndsTheTunnelFirstAndSaysSo()
    {
        await AddServerProfileAsync(ProfileA);
        harness.Tunnels.Up.Add(ProfileA);

        bool presentWhenEnded = false;
        harness.Tunnels.OnEnded = async id => presentWhenEnded = await harness.ProfileAsync(id) is not null;

        List<ServerNotice> raised = [];
        harness.Notices.Raised += (_, notice) => raised.Add(notice);

        harness.Server.Changes = since => SyncServer.Feed(false, 4, deletedProfiles: [ProfileA]);
        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal([ProfileA], harness.Tunnels.Ended);
        Assert.True(presentWhenEnded);
        Assert.Null(await harness.ProfileAsync(ProfileA));

        ServerNotice notice = Assert.Single(raised);
        Assert.Equal(ServerNoticeKind.ConnectedProfilesRemoved, notice.Kind);
        Assert.Equal(["example-site"], notice.ProfileNames);
        Assert.Same(notice, harness.Notices.Latest);
    }

    [Fact]
    public async Task ForgettingEveryCredential_ForgetsTheCursorAndWhatIsHeld()
    {
        harness.Server.Changes = since => SyncServer.Feed(since == 0, 9);
        await harness.Engine.SynchronizeAsync(CancellationToken.None);
        harness.Held.Hold(ProfileA, "Auth", new StoredSecret("vpnuser", "typed"));

        await new ServerCredentialsReset(harness.Database.Factory, harness.Held, NullLogger<ServerCredentialsReset>.Instance).ResetAsync();

        Assert.Null(await harness.QueryAsync(context => context.SyncStates.Select(state => state.Cursor).SingleAsync()));
        Assert.Null(harness.Held.Peek(ProfileA, "Auth"));

        await harness.Engine.SynchronizeAsync(CancellationToken.None);
        Assert.Equal("?since=0", harness.Sent(HttpMethod.Get, "/api/v1/sync/changes")[^1].Query);
    }

    private static CredentialRequest Request(Guid profileId) => new(profileId, "Auth", NeedsUsername: true, IsRetry: false);

    private static ConnectionStatusChanged Changed(Guid profileId, VpnConnectionState state) =>
        new(profileId, new VpnConnectionStatus { State = state });

    private ChangeFeedPuller Puller() => new(
        harness.Network.Api,
        harness.Database.Factory,
        harness.Secrets,
        harness.Maintenance,
        harness.Tunnels,
        harness.Notices,
        TimeProvider.System,
        NullLogger.Instance);

    private async Task AddServerProfileAsync(Guid id)
    {
        harness.Server.Hold(id, "example-site", Configuration);

        await harness.ChangeAsync(context =>
        {
            context.Profiles.Add(new Profile
            {
                Id = id,
                Name = "example-site",
                Configuration = Configuration,
                ContentHash = OpenVpnPilot.Data.Import.ProfileImporter.ComputeHash(Configuration),
                Source = ProfileSource.Server,
            });

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The ledger and the listener over the harness's outbox, in the mode a test names.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ConnectionManager connections = IdleConnections.Create();

        public Fixture(SyncHarness harness, bool serverMode, TypedCredentials? ledger = null)
        {
            FixedStorageMode mode = new(serverMode);
            Ledger = ledger ?? new TypedCredentials(mode);
            Recorder = new VaultShareRecorder(
                connections,
                Ledger,
                Ledger,
                new ChangeRecorder(harness.Outbox, mode),
                NullLogger<VaultShareRecorder>.Instance);
        }

        public TypedCredentials Ledger { get; }

        public VaultShareRecorder Recorder { get; }

        public async ValueTask DisposeAsync()
        {
            await Recorder.DisposeAsync();
            await connections.DisposeAsync();
        }
    }
}
