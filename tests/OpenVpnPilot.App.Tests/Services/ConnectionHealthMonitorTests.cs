using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Testing;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// A tunnel that cannot show it is alive after a long sleep is restarted; one that can is left
/// alone; and one that shows it is dead is restarted whatever the occasion.
/// </summary>
/// <remarks>
/// Driven through a real connection manager and the management transport, because the restart is a
/// command OpenVPN has to receive, and what counts as a sign of life is the byte count it reports.
/// The windows are a fraction of a second so the suite does not wait for real ones.
/// </remarks>
public sealed class ConnectionHealthMonitorTests
{
    private static readonly Guid ProfileId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task AfterALongSleep_ATunnelWithNoSignOfLife_IsRestarted()
    {
        await using Harness harness = await Harness.ConnectedAsync();

        harness.Wake.Raise(LongSleep());

        Assert.Equal("signal SIGUSR1", await harness.Transport.ReceiveLineAsync());
        harness.Transport.SendLine("SUCCESS: signal SIGUSR1 thrown");
        harness.Transport.SendLine(">STATE:1787941400,RECONNECTING,SIGUSR1,,,,,");

        VpnConnectionStatus status = await harness.WaitForStateAsync(VpnConnectionState.Reconnecting);
        Assert.Equal(VpnStatusReasonCode.TunnelRestartedAfterSleep, status.Reason?.Code);
        Assert.True(harness.Probe.Requests > 0);
    }

    [Fact]
    public async Task AfterALongSleep_ATunnelThatReceivesSomething_IsLeftAlone()
    {
        await using Harness harness = await Harness.ConnectedAsync();

        harness.Wake.Raise(LongSleep());
        await Task.Delay(Window / 4);
        harness.Transport.SendLine(">BYTECOUNT:5000,100");

        await Task.Delay(Window * 2);

        Assert.False(harness.Transport.HasPendingClientLine);
        Assert.Equal(VpnConnectionState.Connected, harness.Manager.GetStatus(ProfileId).State);
    }

    [Fact]
    public async Task AfterALongSleep_ATunnelWhoseGatewayAnswers_IsLeftAlone()
    {
        await using Harness harness = await Harness.ConnectedAsync();

        harness.Wake.Raise(LongSleep());
        await Task.Delay(Window / 4);
        harness.Probe.Report(ProfileId, answered: true);

        await Task.Delay(Window * 2);

        Assert.False(harness.Transport.HasPendingClientLine);
    }

    /// <summary>
    /// A short pause proves nothing either way, and a tunnel that cannot show anything is not
    /// restarted on a guess.
    /// </summary>
    [Fact]
    public async Task AfterAShortPause_ATunnelThatCannotBeChecked_IsLeftAlone()
    {
        await using Harness harness = await Harness.ConnectedAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        harness.Wake.Raise(new WakeEvent(now.AddSeconds(-20), now, WakeSource.Platform));

        await Task.Delay(Window * 2);

        Assert.False(harness.Transport.HasPendingClientLine);
    }

    [Fact]
    public async Task AfterANetworkChange_ATunnelWhoseGatewayFellSilent_IsRestarted()
    {
        await using Harness harness = await Harness.ConnectedAsync();

        harness.Probe.Report(ProfileId, answered: true);
        harness.Wake.RaiseNetworkChanged();

        Assert.Equal("signal SIGUSR1", await harness.Transport.ReceiveLineAsync());
        harness.Transport.SendLine("SUCCESS: signal SIGUSR1 thrown");
    }

    [Fact]
    public async Task AfterANetworkChange_ATunnelThatCannotBeChecked_IsLeftAlone()
    {
        await using Harness harness = await Harness.ConnectedAsync();

        harness.Wake.RaiseNetworkChanged();
        await Task.Delay(Window * 2);

        Assert.False(harness.Transport.HasPendingClientLine);
    }

    /// <summary>
    /// While running, a gateway that answered and stops answering, with nothing arriving, is a
    /// tunnel that stopped carrying anything.
    /// </summary>
    [Fact]
    public async Task WhileRunning_AGatewayThatStopsAnswering_RestartsTheTunnel()
    {
        await using Harness harness = await Harness.ConnectedAsync();

        harness.Probe.Report(ProfileId, answered: true);

        for (int failure = 0; failure < ConnectionHealthMonitor.FailuresBeforeStall; failure++)
        {
            harness.Probe.Report(ProfileId, answered: false);
        }

        Assert.Equal("signal SIGUSR1", await harness.Transport.ReceiveLineAsync());
        harness.Transport.SendLine("SUCCESS: signal SIGUSR1 thrown");
        harness.Transport.SendLine(">STATE:1787941400,RECONNECTING,SIGUSR1,,,,,");

        VpnConnectionStatus status = await harness.WaitForStateAsync(VpnConnectionState.Reconnecting);
        Assert.Equal(VpnStatusReasonCode.TunnelUnresponsive, status.Reason?.Code);
    }

    /// <summary>
    /// A server that never answers round trips is not a dead tunnel.
    /// </summary>
    [Fact]
    public async Task WhileRunning_AGatewayThatNeverAnswered_IsNotTakenAsDead()
    {
        await using Harness harness = await Harness.ConnectedAsync();

        for (int failure = 0; failure < 10; failure++)
        {
            harness.Probe.Report(ProfileId, answered: false);
        }

        await Task.Delay(Window);

        Assert.False(harness.Transport.HasPendingClientLine);
    }

    [Fact]
    public async Task ATunnelRestartedAMomentAgo_IsNotRestartedAgain()
    {
        await using Harness harness = await Harness.ConnectedAsync(cooldown: TimeSpan.FromMinutes(5));

        harness.Wake.Raise(LongSleep());
        Assert.Equal("signal SIGUSR1", await harness.Transport.ReceiveLineAsync());
        harness.Transport.SendLine("SUCCESS: signal SIGUSR1 thrown");

        await Task.Delay(Window * 2);
        harness.Wake.Raise(LongSleep(DateTimeOffset.UtcNow.AddMinutes(10)));
        await Task.Delay(Window * 2);

        Assert.False(harness.Transport.HasPendingClientLine);
    }

    private static WakeEvent LongSleep(DateTimeOffset? at = null)
    {
        DateTimeOffset resumed = at ?? DateTimeOffset.UtcNow;
        return new WakeEvent(resumed.AddHours(-2), resumed, WakeSource.Platform);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(TimeSpan cooldown)
        {
            Manager = new ConnectionManager(
                new FakeLauncher(),
                new FakeChannelFactory(Transport),
                new FakeCredentialProvider(),
                new FakeMaterializer(),
                new FixedPortAllocator());

            Monitor = new ConnectionHealthMonitor(
                Manager,
                Probe,
                Wake,
                TimeProvider.System,
                NullLogger<ConnectionHealthMonitor>.Instance)
            {
                VerificationWindow = Window,
                VerificationStep = TimeSpan.FromMilliseconds(20),
                StallWindow = TimeSpan.Zero,
                SettlingTime = TimeSpan.Zero,
                RestartCooldown = cooldown,
            };

            Monitor.Attach();
        }

        public FakeManagementStream Transport { get; } = new();

        public FakeProbe Probe { get; } = new();

        public FakeWake Wake { get; } = new();

        public ConnectionManager Manager { get; }

        public ConnectionHealthMonitor Monitor { get; }

        public static async Task<Harness> ConnectedAsync(TimeSpan? cooldown = null)
        {
            Harness harness = new(cooldown ?? TimeSpan.FromMinutes(2));

            Task connect = harness.Manager.ConnectAsync(ProfileId, "client");

            harness.Transport.SendRaw("ENTER PASSWORD:");
            await harness.Transport.ReceiveLineAsync();
            harness.Transport.SendLine("SUCCESS: password is correct");

            foreach (string _ in new[] { "version 6", "state on", "bytecount 1", "log on" })
            {
                await harness.Transport.ReceiveLineAsync();
                harness.Transport.SendLine("SUCCESS: ok");
            }

            await connect;

            harness.Transport.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,10.8.0.6,203.0.113.10,1194,,");
            harness.Transport.SendLine(">BYTECOUNT:1000,1000");
            await harness.WaitForStateAsync(VpnConnectionState.Connected);
            await WaitUntilAsync(() => harness.Manager.GetStatus(ProfileId).BytesReceived == 1000);

            return harness;
        }

        public async Task<VpnConnectionStatus> WaitForStateAsync(VpnConnectionState state)
        {
            await WaitUntilAsync(() => Manager.GetStatus(ProfileId).State == state);
            return Manager.GetStatus(ProfileId);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("The expected state was not reached in time.");
        }

        public async ValueTask DisposeAsync()
        {
            await Monitor.DisposeAsync();

            // Closed first, so stopping the tunnel does not wait for an answer to its stop signal.
            Transport.CloseFromServer();
            await Manager.DisposeAsync();
        }
    }

    private sealed class FakeProbe : ITunnelProbe
    {
        private int requests;

        public event EventHandler<TunnelProbeResult>? Measured;

        public int Requests => Volatile.Read(ref requests);

        public void RequestMeasurement() => Interlocked.Increment(ref requests);

        public void Report(Guid profileId, bool answered) =>
            Measured?.Invoke(this, new TunnelProbeResult(
                profileId,
                answered ? 12 : null,
                "10.8.0.1",
                PingTargetKind.TunnelGateway));
    }

    private sealed class FakeWake : IWakeMonitor
    {
        public event EventHandler<WakeEvent>? Woke;

        public event EventHandler? NetworkChanged;

        public WakeEvent? LastWake { get; private set; }

        public void Raise(WakeEvent wake)
        {
            LastWake = wake;
            Woke?.Invoke(this, wake);
        }

        public void RaiseNetworkChanged() => NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <remarks>
    /// Reports zero, which names no process, so tearing down ends nothing on the machine running the
    /// tests.
    /// </remarks>
    private sealed class FakeLauncher : IOpenVpnLauncher
    {
        public Task<OpenVpnLaunchResult> LaunchAsync(
            OpenVpnLaunchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OpenVpnLaunchResult.Started(0));
    }

    private sealed class FakeChannelFactory : IManagementChannelFactory
    {
        private readonly Stream stream;

        public FakeChannelFactory(Stream stream) => this.stream = stream;

        public Task<Stream> ConnectAsync(int port, CancellationToken cancellationToken) =>
            Task.FromResult(stream);
    }

    private sealed class FakeCredentialProvider : ICredentialProvider
    {
        public Task<VpnCredentials?> RequestAsync(
            CredentialRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<VpnCredentials?>(null);
    }

    private sealed class FakeMaterializer : IProfileMaterializer
    {
        public Task<MaterialisedProfile> MaterialiseAsync(
            Guid profileId,
            string configuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MaterialisedProfile(
                Path.Combine(Path.GetTempPath(), $"{profileId:N}.ovpn")));

        public int RemoveStaleFiles() => 0;
    }

    private sealed class FixedPortAllocator : IPortAllocator
    {
        public int Reserve() => 25340;
    }
}
