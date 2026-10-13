using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Testing;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The supervisor is driven through a real connection manager, because a retry publishes launching,
/// connecting and the failure in that order, and the count has to survive the states in between.
/// </summary>
public sealed class ReconnectSupervisorTests
{
    private static readonly Guid ProfileId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task RetriesThatKeepFailing_BackOffAndGiveUpAtTheLimit()
    {
        await using Harness harness = new(maxAttempts: 3, delaySeconds: 5);

        await harness.ConnectAndComeUpAsync();
        harness.Drop();

        List<TimeSpan> delays = [];

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            delays.Add(await harness.AdvanceToNextRetryAsync());
            await harness.HandshakeAsync();
            harness.Drop();
        }

        await Harness.WaitUntilAsync(() => harness.Reports.Contains("status.reconnectGaveUp"));

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], delays);
        Assert.Null(harness.Time.NextDue);
        Assert.Equal(4, harness.Launches);
    }

    [Fact]
    public async Task ARetryThatComesUp_StartsTheNextDropFromTheFirstAttempt()
    {
        await using Harness harness = new(maxAttempts: 3, delaySeconds: 5);

        await harness.ConnectAndComeUpAsync();
        harness.Drop();

        Assert.Equal(TimeSpan.FromSeconds(5), await harness.AdvanceToNextRetryAsync());
        await harness.HandshakeAsync();
        harness.Drop();

        Assert.Equal(TimeSpan.FromSeconds(10), await harness.AdvanceToNextRetryAsync());
        await harness.HandshakeAsync();
        harness.ComeUp();
        harness.Drop();

        // The tunnel that came up has nothing left to recover from, so its next drop is a first.
        Assert.Equal(TimeSpan.FromSeconds(5), await harness.AdvanceToNextRetryAsync());
    }

    /// <summary>
    /// The sequence a server that is down produces: every retry launches, connects and then fails
    /// because it did not come up in time.
    /// </summary>
    [Fact]
    public async Task RetriesThatTimeOut_BackOffAndGiveUpAtTheLimit()
    {
        await using Harness harness = new(maxAttempts: 2, delaySeconds: 5, connectTimeoutSeconds: 1);

        await harness.ConnectAndComeUpAsync();
        harness.Drop();

        Assert.Equal(TimeSpan.FromSeconds(5), await harness.AdvanceToNextRetryAsync());
        await harness.HandshakeAsync();

        Assert.Equal(TimeSpan.FromSeconds(10), await harness.AdvanceToNextRetryAsync());
        await harness.HandshakeAsync();

        await Harness.WaitUntilAsync(() => harness.Reports.Contains("status.reconnectGaveUp"));

        Assert.Equal(
            [
                VpnConnectionState.Launching,
                VpnConnectionState.Connecting,
                VpnConnectionState.Failed,
            ],
            harness.States.SkipWhile(state => state != VpnConnectionState.Disconnected).Skip(1).Take(3));
        Assert.Equal(2, harness.Failures.Count(failure => failure == VpnFailureKind.ConnectionLost));
        Assert.Null(harness.Time.NextDue);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Channel<FakeManagementStream> transports = Channel.CreateUnbounded<FakeManagementStream>();
        private readonly ConcurrentQueue<string> reports = new();
        private readonly ConcurrentQueue<VpnConnectionStatus> statuses = new();
        private readonly CountingLauncher launcher = new();
        private FakeManagementStream? transport;

        public Harness(int maxAttempts, int delaySeconds, int connectTimeoutSeconds = 0)
        {
            Manager = new ConnectionManager(
                launcher,
                new FreshChannelFactory(transports.Writer),
                new SilentCredentialProvider(),
                new TemporaryMaterializer(),
                new FixedPortAllocator());

            Manager.StateChanged += (_, change) => statuses.Enqueue(change.Status);

            FakeSettingsService settings = new();
            settings.Current.Connections.AutoReconnect = true;
            settings.Current.Connections.MaxReconnectAttempts = maxAttempts;
            settings.Current.Connections.ReconnectDelaySeconds = delaySeconds;
            settings.Current.Connections.ConnectTimeoutSeconds = connectTimeoutSeconds;

            FakeProfileStore store = new();
            store.Profiles.Add(new Profile { Id = ProfileId, Name = "example-site", Configuration = "client", ContentHash = "test" });

            Supervisor = new ReconnectSupervisor(
                Manager,
                store,
                settings,
                new StubLocalizer(),
                new FixedNames(),
                Time,
                NullLogger<ReconnectSupervisor>.Instance);

            Supervisor.Reported += (_, message) => reports.Enqueue(message);
            Supervisor.Attach();
        }

        public ConnectionManager Manager { get; }

        public ReconnectSupervisor Supervisor { get; }

        public ManualTime Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        public IReadOnlyCollection<string> Reports => reports;

        public int Launches => launcher.Count;

        public IReadOnlyList<VpnConnectionState> States => [.. statuses.Select(status => status.State)];

        /// <summary>
        /// The failure kind of every status that reported the attempt as failed.
        /// </summary>
        public IReadOnlyList<VpnFailureKind> Failures =>
            [.. statuses.Where(status => status.State == VpnConnectionState.Failed).Select(status => status.Failure)];

        public async Task ConnectAndComeUpAsync()
        {
            Task connect = Manager.ConnectAsync(ProfileId, "client");
            await HandshakeAsync();
            await connect;
            ComeUp();
        }

        /// <summary>
        /// Answers the password prompt and the session opening commands of the next process.
        /// </summary>
        public async Task HandshakeAsync()
        {
            transport = await transports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            transport.SendRaw("ENTER PASSWORD:");
            await transport.ReceiveLineAsync();
            transport.SendLine("SUCCESS: password is correct");

            foreach (string _ in new[] { "version 6", "state on", "bytecount 1", "log on" })
            {
                await transport.ReceiveLineAsync();
                transport.SendLine("SUCCESS: ok");
            }

            await WaitUntilAsync(() => Manager.GetStatus(ProfileId).State == VpnConnectionState.Connecting);
        }

        public void ComeUp() =>
            transport!.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,10.8.0.6,203.0.113.10,1194,,");

        /// <summary>
        /// Ends the process the way a tunnel that went away by itself does.
        /// </summary>
        public void Drop() => transport!.SendLine(">STATE:1787941400,EXITING,,,,,,");

        /// <summary>
        /// Waits for the supervisor to arm its back-off, lets it expire, and says how long it was.
        /// </summary>
        public async Task<TimeSpan> AdvanceToNextRetryAsync()
        {
            await WaitUntilAsync(() => Time.NextDue is not null);

            TimeSpan delay = Time.NextDue!.Value;
            Time.Advance(delay);
            return delay;
        }

        public static async Task WaitUntilAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("The supervisor did not reach the expected state in time.");
        }

        public async ValueTask DisposeAsync()
        {
            Supervisor.Dispose();
            await Manager.DisposeAsync();
        }
    }

    /// <remarks>
    /// Reports zero, which names no process, so tearing a connection down cannot end anything the
    /// person running the tests has open.
    /// </remarks>
    private sealed class CountingLauncher : IOpenVpnLauncher
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public Task<OpenVpnLaunchResult> LaunchAsync(
            OpenVpnLaunchRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(OpenVpnLaunchResult.Started(0));
        }
    }

    /// <summary>
    /// Hands every process a channel of its own, as a real one would, and lets the test answer it.
    /// </summary>
    private sealed class FreshChannelFactory(ChannelWriter<FakeManagementStream> created) : IManagementChannelFactory
    {
        public Task<Stream> ConnectAsync(int port, CancellationToken cancellationToken)
        {
            FakeManagementStream stream = new();
            created.TryWrite(stream);
            return Task.FromResult<Stream>(stream);
        }
    }

    private sealed class SilentCredentialProvider : ICredentialProvider
    {
        public Task<VpnCredentials?> RequestAsync(
            CredentialRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<VpnCredentials?>(null);
    }

    private sealed class TemporaryMaterializer : IProfileMaterializer
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
        public int Reserve() => 25341;
    }

    private sealed class FixedNames : IProfileNameLookup
    {
        public string GetDisplayName(Guid profileId) => "example-site";
    }
}
