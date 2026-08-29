using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Testing;

namespace OpenVpnPilot.OpenVpn.Tests.Runtime;

public sealed class ConnectionSupervisorTests
{
    private static readonly Guid ProfileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task ConnectAsync_WhenTheLauncherRefuses_ReportsFailedWithTheReason()
    {
        Harness harness = new();
        harness.Launcher.Result = OpenVpnLaunchResult.Refused(0x80070005, "Access is denied.");

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        VpnConnectionStatus status = await supervisor.ConnectAsync(Request());

        Assert.Equal(VpnConnectionState.Failed, status.State);
        Assert.Equal("Access is denied.", status.Message);
    }

    [Fact]
    public async Task ConnectAsync_PassesTheGeneratedManagementPasswordToTheProcess()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();

        await harness.ConnectAsync(supervisor);

        Assert.NotNull(harness.Launcher.LastRequest);
        Assert.Equal(32, harness.Launcher.LastRequest!.ManagementPassword.Length);
        Assert.Equal(harness.Launcher.LastRequest.ManagementPassword, harness.AuthenticatedWith);
    }

    [Fact]
    public async Task Connected_StateCarriesAddressesAndPort()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,10.8.0.6,203.0.113.10,1194,,");

        VpnConnectionStatus status = await harness.WaitForAsync(
            supervisor,
            s => s.State == VpnConnectionState.Connected);

        Assert.Equal("10.8.0.6", status.LocalAddress);
        Assert.Equal("203.0.113.10", status.ServerAddress);
        Assert.Equal(1194, status.ServerPort);
        Assert.NotNull(status.ConnectedSince);
    }

    [Fact]
    public async Task ByteCount_UpdatesTheTelemetryCounters()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">BYTECOUNT:4096,2048");

        VpnConnectionStatus status = await harness.WaitForAsync(supervisor, s => s.BytesReceived == 4096);
        Assert.Equal(2048, status.BytesSent);
    }

    [Fact]
    public async Task Hold_IsReleasedOnlyAfterOpenVpnReportsBeingHeld()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">HOLD:Waiting for hold release:0");

        Assert.Equal("hold release", await harness.Transport.ReceiveLineAsync());
    }

    [Fact]
    public async Task CredentialRequest_IsAnsweredFromTheProvider()
    {
        Harness harness = new();
        harness.Credentials.Response = new VpnCredentials("operator", "secret");

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password");

        Assert.Equal("username \"Auth\" \"operator\"", await harness.Transport.ReceiveLineAsync());
        harness.Transport.SendLine("SUCCESS: ok");
        Assert.Equal("password \"Auth\" \"secret\"", await harness.Transport.ReceiveLineAsync());

        CredentialRequest request = Assert.Single(harness.Credentials.Requests);
        Assert.Equal(ProfileId, request.ProfileId);
        Assert.Equal("Auth", request.Realm);
        Assert.True(request.NeedsUsername);
        Assert.False(request.IsRetry);
    }

    [Fact]
    public async Task CredentialRequest_AfterARejection_IsMarkedAsARetry()
    {
        Harness harness = new();
        harness.Credentials.Response = new VpnCredentials("operator", "secret");

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">PASSWORD:Verification Failed: 'Auth' ['wrong']");
        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password");

        await harness.Transport.ReceiveLineAsync();

        CredentialRequest request = Assert.Single(harness.Credentials.Requests);
        Assert.True(request.IsRetry);
    }

    [Fact]
    public async Task CredentialRequest_WhenTheProviderDeclines_FailsAndStopsTheProcess()
    {
        Harness harness = new();
        harness.Credentials.Response = null;

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password");

        VpnConnectionStatus status = await harness.WaitForAsync(
            supervisor,
            s => s.State == VpnConnectionState.Failed);

        Assert.Contains("Auth", status.Message, StringComparison.Ordinal);
        Assert.Equal("signal SIGTERM", await harness.Transport.ReceiveLineAsync());
    }

    [Fact]
    public async Task PrivateKeyRequest_AsksForAPasswordWithoutAUsername()
    {
        Harness harness = new();
        harness.Credentials.Response = new VpnCredentials(Username: null, "key-passphrase");

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">PASSWORD:Need 'Private Key' password");

        Assert.Equal("password \"Private Key\" \"key-passphrase\"", await harness.Transport.ReceiveLineAsync());
        Assert.False(Assert.Single(harness.Credentials.Requests).NeedsUsername);
    }

    [Fact]
    public async Task Reconnecting_ExposesTheRestartReasonAndClearsUptime()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,10.8.0.6,203.0.113.10,1194,,");
        await harness.WaitForAsync(supervisor, s => s.State == VpnConnectionState.Connected);

        harness.Transport.SendLine(">STATE:1787941400,RECONNECTING,ping-restart,,,,,");

        VpnConnectionStatus status = await harness.WaitForAsync(
            supervisor,
            s => s.State == VpnConnectionState.Reconnecting);

        Assert.Equal("ping-restart", status.Message);
        Assert.Null(status.ConnectedSince);
    }

    [Fact]
    public async Task FatalMessage_MovesTheConnectionToFailed()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">FATAL:Options error: You must define CA file");

        VpnConnectionStatus status = await harness.WaitForAsync(
            supervisor,
            s => s.State == VpnConnectionState.Failed);

        Assert.Contains("CA file", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedCredentials_ProduceAnActionableFailureMessage()
    {
        Harness harness = new();
        harness.Credentials.Responses.Enqueue(new VpnCredentials("operator", "wrong"));
        harness.Credentials.Responses.Enqueue(null);

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password");
        await harness.Transport.ReceiveLineAsync();
        harness.Transport.SendLine("SUCCESS: ok");
        await harness.Transport.ReceiveLineAsync();
        harness.Transport.SendLine("SUCCESS: ok");

        harness.Transport.SendLine(">PASSWORD:Verification Failed: 'Auth'");
        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password");

        VpnConnectionStatus status = await harness.WaitForAsync(
            supervisor,
            s => s.State == VpnConnectionState.Failed);

        Assert.Contains("rejected by the server", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FatalMessage_DoesNotOverwriteAnAlreadyDiagnosedFailure()
    {
        Harness harness = new();
        harness.Credentials.Responses.Enqueue(null);

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password");

        VpnConnectionStatus diagnosed = await harness.WaitForAsync(
            supervisor,
            s => s.State == VpnConnectionState.Failed);

        // OpenVPN reports the shutdown that follows as a fatal error of its own.
        harness.Transport.SendLine(">FATAL:could not read Auth username/password from management interface");
        await Task.Delay(150);

        Assert.Equal(diagnosed.Message, supervisor.Status.Message);
        Assert.DoesNotContain("management interface", supervisor.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_WhileAlreadyConnecting_IsRejected()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.ConnectAsync(Request()));
    }

    [Fact]
    public async Task DisconnectAsync_SignalsTheProcessAndReturnsToDisconnected()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        Task disconnect = supervisor.DisconnectAsync();

        Assert.Equal("signal SIGTERM", await harness.Transport.ReceiveLineAsync());
        harness.Transport.SendLine("SUCCESS: signal SIGTERM thrown");

        await disconnect;
        Assert.Equal(VpnConnectionState.Disconnected, supervisor.Status.State);
    }

    [Fact]
    public async Task DisconnectAsync_WithNothingRunning_DoesNothing()
    {
        Harness harness = new();
        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();

        await supervisor.DisconnectAsync();

        Assert.Equal(VpnConnectionState.Disconnected, supervisor.Status.State);
    }

    [Fact]
    public async Task StaticChallenge_IsPassedToTheProviderAndEncodedIntoThePassword()
    {
        Harness harness = new();
        harness.Credentials.Response = new VpnCredentials("operator", "secret", "123456");

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password SC:1,Enter your token code");

        Assert.Equal("username \"Auth\" \"operator\"", await harness.Transport.ReceiveLineAsync());
        harness.Transport.SendLine("SUCCESS: ok");

        Assert.Equal(
            "password \"Auth\" \"SCRV1:c2VjcmV0:MTIzNDU2\"",
            await harness.Transport.ReceiveLineAsync());

        CredentialChallenge challenge = Assert.Single(harness.Credentials.Requests).Challenge!;
        Assert.Equal("Enter your token code", challenge.Text);
        Assert.True(challenge.EchoResponse);
        Assert.False(challenge.IsDynamic);
    }

    [Fact]
    public async Task StaticChallenge_WithoutAResponse_SendsThePasswordUnchanged()
    {
        Harness harness = new();
        harness.Credentials.Response = new VpnCredentials("operator", "secret");

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password SC:1,Enter your token code");

        await harness.Transport.ReceiveLineAsync();
        harness.Transport.SendLine("SUCCESS: ok");

        Assert.Equal("password \"Auth\" \"secret\"", await harness.Transport.ReceiveLineAsync());
    }

    [Fact]
    public async Task DynamicChallenge_IsAnsweredOnTheFollowingAttemptWithTheServerState()
    {
        Harness harness = new();
        harness.Credentials.Response = new VpnCredentials("operator", "secret", "987654");

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(
            ">PASSWORD:Verification Failed: 'Auth' ['CRV1:R,E:state-7:b3BlcmF0b3I=:Enter your token code']");
        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password");

        Assert.Equal("username \"Auth\" \"operator\"", await harness.Transport.ReceiveLineAsync());
        harness.Transport.SendLine("SUCCESS: ok");

        Assert.Equal(
            "password \"Auth\" \"CRV1::state-7::987654\"",
            await harness.Transport.ReceiveLineAsync());

        CredentialRequest request = Assert.Single(harness.Credentials.Requests);
        Assert.NotNull(request.Challenge);
        Assert.True(request.Challenge.IsDynamic);
        Assert.Equal("Enter your token code", request.Challenge.Text);

        // A challenge is a request for a code, not a wrong password, so it must not warn about one.
        Assert.False(request.IsRetry);
    }

    [Fact]
    public async Task DynamicChallenge_UsesTheUsernameTheServerEchoedWhenTheProviderSuppliesNone()
    {
        Harness harness = new();
        harness.Credentials.Response = new VpnCredentials(Username: null, "unused", "987654");

        await using ConnectionSupervisor supervisor = harness.CreateSupervisor();
        await harness.ConnectAsync(supervisor);

        harness.Transport.SendLine(
            ">PASSWORD:Verification Failed: 'Auth' ['CRV1:R,E:state-7:b3BlcmF0b3I=:Enter your token code']");
        harness.Transport.SendLine(">PASSWORD:Need 'Auth' username/password");

        Assert.Equal("username \"Auth\" \"operator\"", await harness.Transport.ReceiveLineAsync());
    }

    private static ConnectionRequest Request() =>
        new(ProfileId, @"C:\profiles\example.ovpn", @"C:\profiles", 25340, []);

    private sealed class Harness
    {
        public FakeLauncher Launcher { get; } = new();

        public FakeCredentialProvider Credentials { get; } = new();

        public FakeManagementStream Transport { get; } = new();

        public string? AuthenticatedWith { get; private set; }

        public ConnectionSupervisor CreateSupervisor() =>
            new(Launcher, new FakeChannelFactory(Transport), Credentials);

        /// <summary>
        /// Drives the password handshake and the session opening commands the supervisor issues.
        /// </summary>
        public async Task ConnectAsync(ConnectionSupervisor supervisor)
        {
            Task connect = supervisor.ConnectAsync(Request());

            Transport.SendRaw("ENTER PASSWORD:");
            AuthenticatedWith = await Transport.ReceiveLineAsync();
            Transport.SendLine("SUCCESS: password is correct");

            foreach (string _ in new[] { "version 6", "state on", "bytecount 1", "log on" })
            {
                await Transport.ReceiveLineAsync();
                Transport.SendLine("SUCCESS: ok");
            }

            await connect;
        }

        public async Task<VpnConnectionStatus> WaitForAsync(
            ConnectionSupervisor supervisor,
            Func<VpnConnectionStatus, bool> predicate)
        {
            TaskCompletionSource<VpnConnectionStatus> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnChanged(object? sender, VpnConnectionStatus status)
            {
                if (predicate(status))
                {
                    completion.TrySetResult(status);
                }
            }

            supervisor.StatusChanged += OnChanged;
            try
            {
                if (predicate(supervisor.Status))
                {
                    return supervisor.Status;
                }

                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                supervisor.StatusChanged -= OnChanged;
            }
        }
    }

    private sealed class FakeLauncher : IOpenVpnLauncher
    {
        public OpenVpnLaunchResult Result { get; set; } = OpenVpnLaunchResult.Started(4242);

        public OpenVpnLaunchRequest? LastRequest { get; private set; }

        public Task<OpenVpnLaunchResult> LaunchAsync(
            OpenVpnLaunchRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
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
        /// <summary>
        /// Answer used when <see cref="Responses"/> is empty.
        /// </summary>
        public VpnCredentials? Response { get; set; }

        /// <summary>
        /// Answers for consecutive requests, so a rejected first attempt can be modelled.
        /// </summary>
        public Queue<VpnCredentials?> Responses { get; } = new();

        public List<CredentialRequest> Requests { get; } = [];

        public Task<VpnCredentials?> RequestAsync(CredentialRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : Response);
        }
    }
}
