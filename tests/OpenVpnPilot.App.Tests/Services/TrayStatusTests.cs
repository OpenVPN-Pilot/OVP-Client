using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Which state the notification area icon shows when several things are going on at once.
/// </summary>
/// <remarks>
/// The order is the decision the icon makes, so it is pinned here pair by pair rather than left to
/// whoever reorders the conditions next.
/// </remarks>
public sealed class TrayStatusTests
{
    private const VpnConnectionState Up = VpnConnectionState.Connected;

    private static TrayStatusInputs Inputs(
        VpnConnectionState[]? tunnels = null,
        int waiting = 0,
        int failures = 0,
        bool prompt = false,
        bool blocked = false,
        SyncState? server = null) =>
        new(tunnels ?? [], waiting, failures, prompt, blocked, server);

    private static TrayStatus Top(TrayStatusInputs inputs) => TrayStatusResolver.Resolve(inputs)[0];

    [Fact]
    public void NothingGoingOn_IsIdle_LocallyOrWithAServer()
    {
        Assert.Equal(TrayStatus.IdleLocal, Top(Inputs()));
        Assert.Equal(TrayStatus.IdleRemote, Top(Inputs(server: SyncState.Synchronised)));
    }

    [Theory]
    [InlineData(SyncState.Synchronising, TrayStatus.IdleRemote)]
    [InlineData(SyncState.ChangesWaiting, TrayStatus.IdleRemote)]
    [InlineData(SyncState.Idle, TrayStatus.IdleRemote)]
    [InlineData(SyncState.Offline, TrayStatus.RemoteOffline)]
    [InlineData(SyncState.SignInRequired, TrayStatus.RemoteSignInRequired)]
    [InlineData(SyncState.Degraded, TrayStatus.RemoteProblem)]
    [InlineData(SyncState.ClientOutdated, TrayStatus.RemoteProblem)]
    [InlineData(SyncState.ClockWrong, TrayStatus.RemoteProblem)]
    [InlineData(SyncState.CertificateUntrusted, TrayStatus.RemoteProblem)]
    [InlineData(SyncState.SettingsUnreadable, TrayStatus.RemoteProblem)]
    [InlineData(SyncState.KeystoreRefused, TrayStatus.RemoteProblem)]
    public void TheServer_IsShownWhenNoTunnelSaysMore(SyncState server, TrayStatus expected) =>
        Assert.Equal(expected, Top(Inputs(server: server)));

    [Fact]
    public void ATunnel_OutranksTheServer_ExceptASignInSomebodyHasToDo()
    {
        Assert.Equal(TrayStatus.Connected, Top(Inputs([Up], server: SyncState.Offline)));
        Assert.Equal(TrayStatus.RemoteSignInRequired, Top(Inputs([Up], server: SyncState.SignInRequired)));
    }

    [Fact]
    public void APrompt_OutranksEverything()
    {
        TrayStatusInputs all = Inputs([Up, VpnConnectionState.Reconnecting], waiting: 1, failures: 1, prompt: true, blocked: true, server: SyncState.SignInRequired);

        Assert.Equal(TrayStatus.AwaitingInput, Top(all));
    }

    [Theory]
    [InlineData(VpnConnectionState.Launching)]
    [InlineData(VpnConnectionState.Authenticating)]
    [InlineData(VpnConnectionState.Connecting)]
    [InlineData(VpnConnectionState.Disconnecting)]
    public void WhatIsUnderWay_IsConnecting_EvenBesideATunnelThatIsUp(VpnConnectionState state)
    {
        Assert.Equal(TrayStatus.Connecting, Top(Inputs([state])));
        Assert.Equal(TrayStatus.Connecting, Top(Inputs([Up, state])));
    }

    [Fact]
    public void OneOrMoreTunnelsUp_AreTold()
    {
        Assert.Equal(TrayStatus.Connected, Top(Inputs([Up])));
        Assert.Equal(TrayStatus.ConnectedMultiple, Top(Inputs([Up, Up])));
    }

    [Fact]
    public void ADrop_IsReconnecting_WhetherOpenVpnOrTheRetryLogicBringsItBack()
    {
        Assert.Equal(TrayStatus.Reconnecting, Top(Inputs([VpnConnectionState.Reconnecting])));
        Assert.Equal(TrayStatus.Reconnecting, Top(Inputs(waiting: 1)));
    }

    [Fact]
    public void AFailure_OutranksAReconnect_AndATunnelThatIsUp()
    {
        Assert.Equal(TrayStatus.Failed, Top(Inputs([VpnConnectionState.Reconnecting], failures: 1)));
        Assert.Equal(TrayStatus.Failed, Top(Inputs([Up], failures: 1)));
        Assert.Equal(TrayStatus.Reconnecting, Top(Inputs([Up, VpnConnectionState.Reconnecting])));
    }

    [Fact]
    public void EveryStateThatApplies_IsListedInOrder()
    {
        TrayStatusInputs inputs = Inputs([Up, Up, VpnConnectionState.Reconnecting], failures: 1, server: SyncState.Offline);

        Assert.Equal(
            [TrayStatus.Failed, TrayStatus.Reconnecting, TrayStatus.ConnectedMultiple, TrayStatus.Connected, TrayStatus.RemoteOffline],
            TrayStatusResolver.Resolve(inputs));
    }

    [Fact]
    public void Idle_IsListedOnlyWhileNothingIsConnected()
    {
        Assert.Equal([TrayStatus.RemoteOffline, TrayStatus.IdleRemote], TrayStatusResolver.Resolve(Inputs(server: SyncState.Offline)));
        Assert.DoesNotContain(TrayStatus.IdleLocal, TrayStatusResolver.Resolve(Inputs([Up])));
    }

    [Fact]
    public void AStateThatIsOff_PassesTheDecisionToTheNextOne()
    {
        TrayIconSettings preferences = new();
        IReadOnlyList<TrayStatus> applicable = [TrayStatus.Failed, TrayStatus.Reconnecting, TrayStatus.Connected];

        Assert.Equal(Parse(preferences.Failed), TrayIconController.Colour(preferences, applicable));

        preferences.Failed = TrayIconSettings.Off;
        Assert.Equal(Parse(preferences.Reconnecting), TrayIconController.Colour(preferences, applicable));

        preferences.Reconnecting = "OFF";
        Assert.Equal(Parse(preferences.Connected), TrayIconController.Colour(preferences, applicable));
    }

    [Fact]
    public void EveryStateOff_OrTheFeatureOff_OrTheOriginal_DrawsThePlatformsOwnIcon()
    {
        TrayIconSettings preferences = new() { Connected = TrayIconSettings.Off };
        IReadOnlyList<TrayStatus> connected = [TrayStatus.Connected];

        Assert.Null(TrayIconController.Colour(preferences, connected));

        preferences.Connected = null;
        Assert.Null(TrayIconController.Colour(preferences, connected));

        preferences.Connected = "#22C55E";
        preferences.ShowStatus = false;
        Assert.Null(TrayIconController.Colour(preferences, connected));
    }

    [Theory]
    [InlineData(VpnConnectionState.Connected, TrayStatus.Connected)]
    [InlineData(VpnConnectionState.Reconnecting, TrayStatus.Reconnecting)]
    [InlineData(VpnConnectionState.Launching, TrayStatus.Connecting)]
    [InlineData(VpnConnectionState.Authenticating, TrayStatus.Connecting)]
    [InlineData(VpnConnectionState.Connecting, TrayStatus.Connecting)]
    [InlineData(VpnConnectionState.Disconnecting, TrayStatus.Connecting)]
    [InlineData(VpnConnectionState.Failed, TrayStatus.Failed)]
    public void AConnection_IsDottedInTheColourOfItsState(VpnConnectionState state, TrayStatus expected)
    {
        TrayIconSettings preferences = new();

        Assert.Equal(expected, TrayIconController.StatusOf(state));
        Assert.Equal(Parse(preferences.Get(expected)), TrayIconController.BadgeOf(preferences, state));
    }

    [Fact]
    public void AConnection_HasNoDot_WhereTheIconWouldHaveNoColour()
    {
        TrayIconSettings preferences = new() { Reconnecting = TrayIconSettings.Off, Connecting = null };

        Assert.Null(TrayIconController.BadgeOf(preferences, VpnConnectionState.Reconnecting));
        Assert.Null(TrayIconController.BadgeOf(preferences, VpnConnectionState.Connecting));
        Assert.Null(TrayIconController.BadgeOf(preferences, VpnConnectionState.Disconnected));

        preferences.ShowStatus = false;
        Assert.Null(TrayIconController.BadgeOf(preferences, VpnConnectionState.Connected));
    }

    [Fact]
    public void AConnectionEntry_NamesItsProfile_AndNothingElseDoes()
    {
        Guid profile = Guid.NewGuid();

        Assert.Equal(profile, TrayIconController.ProfileOf(TrayIconController.DisconnectProfilePrefix + profile.ToString("D")));
        Assert.Null(TrayIconController.ProfileOf(TrayIconController.DisconnectAllAction));
        Assert.Null(TrayIconController.ProfileOf(TrayIconController.DisconnectProfilePrefix + "not-a-profile"));
    }

    private static TrayIconColour Parse(string? text) =>
        TrayIconColour.TryParse(text, out TrayIconColour colour) ? colour : throw new InvalidOperationException(text);

    [Fact]
    public void ABlockedEnvironment_MattersOnlyWhileNothingIsUp()
    {
        Assert.Equal(TrayStatus.Unavailable, Top(Inputs(blocked: true, failures: 1)));
        Assert.Equal(TrayStatus.Connected, Top(Inputs([Up], blocked: true)));
    }

    [Fact]
    public void EveryState_HasAName()
    {
        Assert.Equal("trayStatus.idleLocal", TrayStatusNames.Key(TrayStatus.IdleLocal));
        Assert.Equal("trayStatus.remoteSignInRequired", TrayStatusNames.Key(TrayStatus.RemoteSignInRequired));
    }

    [Fact]
    public async Task AFailedLaunch_StaysShownUntilSomebodyHasLooked()
    {
        await using ConnectionManager connections = IdleConnections.Create();
        using Monitor monitor = new(connections);

        // The launcher of the idle manager refuses, which ends the attempt in a failure.
        VpnConnectionStatus status = await connections.ConnectAsync(Guid.NewGuid(), "client");

        Assert.Equal(VpnConnectionState.Failed, status.State);
        Assert.Equal(TrayStatus.Failed, monitor.Status.Current);

        monitor.Status.AcknowledgeFailures();

        Assert.Equal(TrayStatus.IdleLocal, monitor.Status.Current);
    }

    [Fact]
    public async Task AnOpenPrompt_IsWaitingForSomebody_UntilItCloses()
    {
        await using ConnectionManager connections = IdleConnections.Create();
        using Monitor monitor = new(connections);
        int changes = 0;
        monitor.Status.Changed += (_, _) => changes++;

        using (monitor.Prompts.Open())
        {
            Assert.Equal(TrayStatus.AwaitingInput, monitor.Status.Current);
        }

        Assert.Equal(TrayStatus.IdleLocal, monitor.Status.Current);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task AnEnvironmentThatCannotConnect_IsUnavailable()
    {
        await using ConnectionManager connections = IdleConnections.Create();
        EnvironmentGate gate = new(new BlockedProbe(), TimeProvider.System, NullLogger<EnvironmentGate>.Instance);
        using Monitor monitor = new(connections, gate);

        await gate.RefreshAsync();

        Assert.Equal(TrayStatus.Unavailable, monitor.Status.Current);
    }

    private static readonly IReadOnlyList<TrayColourModeChoice> Modes =
    [
        new(TrayColourMode.Colour, "colour"),
        new(TrayColourMode.Original, "original"),
        new(TrayColourMode.Off, "off"),
    ];

    [Fact]
    public void TheSettingsRows_CoverEveryState_InThePriority()
    {
        Assert.Equal(Enum.GetValues<TrayStatus>().Length, TrayColourChoice.Order.Distinct().Count());
    }

    [Fact]
    public void ARow_WritesWhatItsChoiceMeans_AndKeepsTheColourBehindIt()
    {
        TrayColourChoice row = new(TrayStatus.Connected, "name", Modes, "#22C55E");
        Assert.Equal(TrayColourMode.Colour, row.SelectedMode?.Mode);
        Assert.Equal("#22C55E", row.Stored);

        row.SelectedMode = Modes[1];
        Assert.Null(row.Stored);
        Assert.False(row.HasColour);

        row.SelectedMode = Modes[2];
        Assert.Equal(TrayIconSettings.Off, row.Stored);

        // The colour stays behind the choice, so choosing a colour again brings it back.
        row.SelectedMode = Modes[0];
        Assert.Equal("#22C55E", row.Stored);
    }

    [Fact]
    public void ARow_ReadsOff_AndResetsToItsDefault()
    {
        TrayColourChoice idle = new(TrayStatus.IdleLocal, "name", Modes, "#000000");
        TrayColourChoice failed = new(TrayStatus.Failed, "name", Modes, "off");

        Assert.Equal(TrayColourMode.Off, failed.SelectedMode?.Mode);

        idle.Reset();
        failed.Reset();

        Assert.Null(idle.Stored);
        Assert.Equal(TrayIconSettings.Default(TrayStatus.Failed), failed.Stored);
    }

    /// <summary>
    /// The monitor with real sources around an idle connection manager.
    /// </summary>
    private sealed class Monitor : IDisposable
    {
        private readonly ReconnectSupervisor reconnects;

        public Monitor(ConnectionManager connections, EnvironmentGate? gate = null)
        {
            FakeSettingsService settings = new();
            settings.Current.Connections.AutoReconnect = false;

            reconnects = new ReconnectSupervisor(
                connections,
                new FakeProfileStore(),
                settings,
                new StubLocalizer(),
                new ProfileNameCache(),
                TimeProvider.System,
                NullLogger<ReconnectSupervisor>.Instance);

            Status = new TrayStatusMonitor(connections, reconnects, Prompts, gate ?? ReadyEnvironmentProbe.Gate());
            Status.Attach();
        }

        public CredentialPromptTracker Prompts { get; } = new();

        public TrayStatusMonitor Status { get; }

        public void Dispose()
        {
            Status.Dispose();
            reconnects.Dispose();
        }
    }

    private sealed class BlockedProbe : IOpenVpnEnvironmentProbe
    {
        public Task<OpenVpnEnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenVpnEnvironmentReport(
            [
                new EnvironmentCheck(EnvironmentCheckId.Installation, EnvironmentCheckStatus.Failed, "Test"),
            ]));
    }
}
