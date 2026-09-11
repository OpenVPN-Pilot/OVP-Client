using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Testing;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The recorder is driven through a real connection manager, because the bug worth guarding against
/// lives in the order the statuses arrive rather than in any single one of them.
/// </summary>
public sealed class SessionRecorderTests
{
    private static readonly Guid ProfileId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task ATunnelThatCarriedTraffic_RecordsWhatItCarried()
    {
        Harness harness = new();

        await harness.ConnectAsync();
        harness.Transport.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,10.8.0.6,203.0.113.10,1194,,");
        await harness.WaitForSessionsAsync(1);

        harness.Transport.SendLine(">BYTECOUNT:33280,23654");
        await harness.WaitForCountersAsync(33280);

        await harness.DisconnectAsync();
        await Harness.WaitUntilAsync(() => harness.Sessions.Single().EndReason != SessionEndReason.Running);

        RecordedSession session = harness.Sessions.Single();

        // The final status carries no counters, so a naive copy would record a session of nothing.
        Assert.Equal(33280, session.BytesReceived);
        Assert.Equal(23654, session.BytesSent);
        Assert.Equal(SessionEndReason.UserRequested, session.EndReason);
    }

    [Fact]
    public async Task AConnectedTunnel_OpensExactlyOneSession()
    {
        Harness harness = new();

        await harness.ConnectAsync();
        harness.Transport.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,10.8.0.6,203.0.113.10,1194,,");
        await harness.WaitForSessionsAsync(1);

        // Telemetry keeps arriving while connected and must not open a second row.
        harness.Transport.SendLine(">BYTECOUNT:100,100");
        harness.Transport.SendLine(">BYTECOUNT:200,200");
        await harness.WaitForCountersAsync(200);

        Assert.Single(harness.Sessions);

        RecordedSession session = harness.Sessions.Single();
        Assert.Equal("203.0.113.10", session.ServerAddress);
        Assert.Equal(1194, session.ServerPort);
        Assert.Equal("10.8.0.6", session.LocalAddress);
    }

    [Fact]
    public async Task AConnectionThatNeverCameUp_RecordsNothing()
    {
        Harness harness = new();

        await harness.ConnectAsync();
        harness.Transport.SendLine(">FATAL:Options error: You must define CA file");

        await Task.Delay(200);

        // A history of attempts that established nothing would bury the sessions that matter.
        Assert.Empty(harness.Sessions);
    }

    [Fact]
    public async Task ATunnelThatDropped_IsRecordedAsLostRatherThanAsADisconnect()
    {
        Harness harness = new();

        await harness.ConnectAsync();
        harness.Transport.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,10.8.0.6,203.0.113.10,1194,,");
        await harness.WaitForSessionsAsync(1);

        harness.Transport.SendLine(">BYTECOUNT:512,256");
        await harness.WaitForCountersAsync(512);

        // The process exited on its own, which is not something the user asked for.
        harness.Transport.SendLine(">STATE:1787941400,EXITING,,,,,,");

        await Harness.WaitUntilAsync(() => harness.Sessions.Single().EndReason != SessionEndReason.Running);

        RecordedSession session = harness.Sessions.Single();

        Assert.Equal(SessionEndReason.ConnectionLost, session.EndReason);
        Assert.Equal(512, session.BytesReceived);
    }

    [Fact]
    public async Task CloseOpenSessionsAsync_EndsWhatIsStillRunning()
    {
        Harness harness = new();

        await harness.ConnectAsync();
        harness.Transport.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,10.8.0.6,203.0.113.10,1194,,");
        await harness.WaitForSessionsAsync(1);

        harness.Transport.SendLine(">BYTECOUNT:64,32");
        await harness.WaitForCountersAsync(64);

        await harness.Recorder.CloseOpenSessionsAsync(SessionEndReason.ApplicationClosed);

        RecordedSession session = harness.Sessions.Single();

        Assert.Equal(SessionEndReason.ApplicationClosed, session.EndReason);
        Assert.Equal(64, session.BytesReceived);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Manager = new ConnectionManager(
                Launcher,
                new FakeChannelFactory(Transport),
                new FakeCredentialProvider(),
                new FakeMaterializer(),
                new FixedPortAllocator());

            Recorder = new SessionRecorder(Manager, Store, NullLogger<SessionRecorder>.Instance);
            Recorder.Attach();
        }

        public FakeLauncher Launcher { get; } = new();

        public FakeManagementStream Transport { get; } = new();

        public RecordingSessionStore Store { get; } = new();

        public ConnectionManager Manager { get; }

        public SessionRecorder Recorder { get; }

        public IReadOnlyList<RecordedSession> Sessions => Store.Sessions;

        /// <summary>
        /// Drives the password handshake and the session opening commands the supervisor issues.
        /// </summary>
        public async Task ConnectAsync()
        {
            Task connect = Manager.ConnectAsync(ProfileId, "client");

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

        public async Task DisconnectAsync()
        {
            Task disconnect = Manager.DisconnectAsync(ProfileId);

            await Transport.ReceiveLineAsync();
            Transport.SendLine("SUCCESS: signal SIGTERM thrown");

            await disconnect;
        }

        public Task WaitForSessionsAsync(int count) => WaitUntilAsync(() => Sessions.Count == count);

        /// <summary>
        /// Waits until the connection reports the given counter, which is what the recorder sees.
        /// </summary>
        /// <remarks>
        /// The counters are written to the store only when a session closes, so while a tunnel is up
        /// the live status is the only place they can be observed.
        /// </remarks>
        public Task WaitForCountersAsync(long received) =>
            WaitUntilAsync(() => Manager.GetStatus(ProfileId).BytesReceived == received);

        /// <summary>
        /// The recorder writes from a background task, so the assertions wait for it to catch up.
        /// </summary>
        public static async Task WaitUntilAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (condition())
                    {
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The collection is still empty, which is one of the states being waited on.
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("The recorder did not reach the expected state in time.");
        }
    }

    private sealed class RecordingSessionStore : ISessionStore
    {
        private readonly List<RecordedSession> sessions = [];

        public IReadOnlyList<RecordedSession> Sessions
        {
            get
            {
                lock (sessions)
                {
                    return [.. sessions];
                }
            }
        }

        public Task<Guid> BeginAsync(
            Guid profileId,
            string? localAddress,
            string? serverAddress,
            int? serverPort,
            CancellationToken cancellationToken = default)
        {
            RecordedSession session = new(Guid.NewGuid(), profileId, localAddress, serverAddress, serverPort);

            lock (sessions)
            {
                sessions.Add(session);
            }

            return Task.FromResult(session.Id);
        }

        public Task EndAsync(
            Guid sessionId,
            SessionEndReason reason,
            long bytesReceived,
            long bytesSent,
            string? detail,
            CancellationToken cancellationToken = default)
        {
            lock (sessions)
            {
                RecordedSession? session = sessions.Find(item => item.Id == sessionId);

                if (session is not null)
                {
                    session.EndReason = reason;
                    session.BytesReceived = bytesReceived;
                    session.BytesSent = bytesSent;
                    session.EndDetail = detail;
                }
            }

            return Task.CompletedTask;
        }

        public Task AddEventAsync(
            Guid sessionId,
            string level,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> CloseAbandonedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(
            SessionQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([]);

        public Task<SessionTotals> GetTotalsAsync(
            SessionQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionTotals(0, TimeSpan.Zero, 0, 0));

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> DeleteAsync(SessionQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class RecordedSession
    {
        public RecordedSession(
            Guid id,
            Guid profileId,
            string? localAddress,
            string? serverAddress,
            int? serverPort)
        {
            Id = id;
            ProfileId = profileId;
            LocalAddress = localAddress;
            ServerAddress = serverAddress;
            ServerPort = serverPort;
        }

        public Guid Id { get; }

        public Guid ProfileId { get; }

        public string? LocalAddress { get; }

        public string? ServerAddress { get; }

        public int? ServerPort { get; }

        public long BytesReceived { get; set; }

        public long BytesSent { get; set; }

        public SessionEndReason EndReason { get; set; } = SessionEndReason.Running;

        public string? EndDetail { get; set; }
    }

    /// <remarks>
    /// Reports zero, which names no process. Tearing a connection down ends the process it was
    /// given, and on a system that hands out identifiers in sequence any plausible number may well
    /// belong to something the person running the tests has open.
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
