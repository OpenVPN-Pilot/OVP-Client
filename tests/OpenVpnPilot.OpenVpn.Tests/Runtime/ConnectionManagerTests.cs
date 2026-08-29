using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Testing;

namespace OpenVpnPilot.OpenVpn.Tests.Runtime;

/// <summary>
/// Covers what the manager holds on to, which decides what the rest of the application believes.
/// </summary>
/// <remarks>
/// A connection that has ended but is still held makes the profile look busy, makes the retry logic
/// decide the tunnel is already up, and makes connecting again fail as a duplicate. None of that is
/// visible from the supervisor on its own.
/// </remarks>
public sealed class ConnectionManagerTests
{
    private static readonly Guid ProfileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private const string Configuration = "client\nremote vpn.example.com 1194\n";

    [Fact]
    public async Task ConnectAsync_WhenTheLauncherRefuses_LeavesNothingActive()
    {
        Harness harness = new();
        harness.Launcher.Result = OpenVpnLaunchResult.Refused(0x80070005, "Access is denied.");

        await using ConnectionManager manager = harness.CreateManager();

        VpnConnectionStatus status = await manager.ConnectAsync(ProfileId, Configuration);

        Assert.Equal(VpnConnectionState.Failed, status.State);
        Assert.False(manager.IsActive(ProfileId));
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public async Task AConnectionThatFailsOnItsOwn_IsNoLongerActive()
    {
        Harness harness = new();
        await using ConnectionManager manager = harness.CreateManager();

        await harness.ConnectAsync(manager);
        Assert.True(manager.IsActive(ProfileId));

        harness.Transport.SendLine(">FATAL:Options error: You must define CA file");

        await harness.WaitForStateAsync(VpnConnectionState.Failed);

        Assert.False(manager.IsActive(ProfileId));
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public async Task AProcessThatExitsByItself_IsNoLongerActive()
    {
        Harness harness = new();
        await using ConnectionManager manager = harness.CreateManager();

        await harness.ConnectAsync(manager);

        harness.Transport.SendLine(">STATE:1787941299,EXITING,exit-with-notification,,,,,");

        await harness.WaitForStateAsync(VpnConnectionState.Disconnected);

        Assert.False(manager.IsActive(ProfileId));
    }

    /// <summary>
    /// Disconnecting has to leave nothing behind however badly the far end behaves.
    /// </summary>
    /// <remarks>
    /// This is the case behind a profile that stayed on "disconnecting" after stopping everything:
    /// the entry survived the failed attempt, so the profile looked busy and there was no way back.
    /// </remarks>
    [Fact]
    public async Task DisconnectAsync_WhenTheChannelIsGone_StillRemovesTheConnection()
    {
        Harness harness = new();
        await using ConnectionManager manager = harness.CreateManager();

        await harness.ConnectAsync(manager);
        harness.Transport.CloseFromServer();

        await manager.DisconnectAsync(ProfileId).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(manager.IsActive(ProfileId));
        Assert.Equal(VpnConnectionState.Disconnected, manager.GetStatus(ProfileId).State);
    }

    [Fact]
    public async Task DisconnectAllAsync_WithADeadChannel_StopsEverything()
    {
        Harness harness = new();
        await using ConnectionManager manager = harness.CreateManager();

        await harness.ConnectAsync(manager);
        harness.Transport.CloseFromServer();

        await manager.DisconnectAllAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, manager.ActiveCount);
    }

    /// <summary>
    /// A profile that failed has to be connectable again without anything being stopped first.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_AfterAFailure_IsAcceptedAgain()
    {
        Harness harness = new();
        await using ConnectionManager manager = harness.CreateManager();

        await harness.ConnectAsync(manager);
        harness.Transport.SendLine(">FATAL:Options error: You must define CA file");
        await harness.WaitForStateAsync(VpnConnectionState.Failed);

        harness.UseNewTransport();
        await harness.ConnectAsync(manager);

        Assert.True(manager.IsActive(ProfileId));
    }

    private sealed class Harness
    {
        private readonly List<VpnConnectionStatus> states = [];
        private readonly Lock gate = new();

        public FakeLauncher Launcher { get; } = new();

        public FakeCredentialProvider Credentials { get; } = new();

        public FakeManagementStream Transport { get; private set; } = new();

        public ConnectionManager CreateManager()
        {
            ConnectionManager manager = new(
                Launcher,
                new FakeChannelFactory(() => Transport),
                Credentials,
                new FakeMaterializer(),
                new FixedPortAllocator());

            manager.StateChanged += (_, change) =>
            {
                lock (gate)
                {
                    states.Add(change.Status);
                }
            };

            return manager;
        }

        /// <summary>
        /// Replaces the transport, so a second attempt is not answered by the closed first one.
        /// </summary>
        public void UseNewTransport() => Transport = new FakeManagementStream();

        /// <summary>
        /// Drives the handshake and the session opening commands the supervisor issues.
        /// </summary>
        public async Task ConnectAsync(ConnectionManager target)
        {
            Task connect = target.ConnectAsync(ProfileId, Configuration);

            Transport.SendRaw("ENTER PASSWORD:");
            await Transport.ReceiveLineAsync();
            Transport.SendLine("SUCCESS: password is correct");

            foreach (string _ in new[] { "version 6", "state on", "bytecount 1", "log on" })
            {
                await Transport.ReceiveLineAsync();
                Transport.SendLine("SUCCESS: ok");
            }

            await connect;
        }

        /// <summary>
        /// Waits until the manager has reported the given state, which is raised from the pump.
        /// </summary>
        public async Task WaitForStateAsync(VpnConnectionState state)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

            while (!cts.IsCancellationRequested)
            {
                lock (gate)
                {
                    if (states.Any(status => status.State == state))
                    {
                        return;
                    }
                }

                await Task.Delay(20, cts.Token);
            }

            Assert.Fail($"The manager never reported {state}.");
        }
    }
}
