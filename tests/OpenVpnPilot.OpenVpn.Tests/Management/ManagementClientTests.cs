using OpenVpnPilot.OpenVpn.Management;

namespace OpenVpnPilot.OpenVpn.Tests.Management;

public sealed class ManagementClientTests
{
    private const string Password = "3E6F1A2B4C5D6E7F";

    [Fact]
    public async Task StartAsync_AnswersThePromptAndCompletesOnAcceptance()
    {
        FakeManagementStream transport = new();
        await using ManagementClient client = new(transport);

        Task start = client.StartAsync(Password);

        transport.SendRaw("ENTER PASSWORD:");
        Assert.Equal(Password, await transport.ReceiveLineAsync());

        transport.SendLine("SUCCESS: password is correct");
        await start;
    }

    [Fact]
    public async Task StartAsync_WithoutPassword_DoesNotWaitForAPrompt()
    {
        FakeManagementStream transport = new();
        await using ManagementClient client = new(transport);

        await client.StartAsync(password: null).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(transport.HasPendingClientLine);
    }

    [Fact]
    public async Task StartAsync_WhenTheServerRejectsThePassword_Fails()
    {
        FakeManagementStream transport = new();
        await using ManagementClient client = new(transport);

        Task start = client.StartAsync(Password);

        transport.SendRaw("ENTER PASSWORD:");
        await transport.ReceiveLineAsync();
        transport.SendLine("ERROR: bad password");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        Assert.Contains("bad password", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_RejectsAPasswordLongerThanOpenVpnAccepts()
    {
        FakeManagementStream transport = new();
        await using ManagementClient client = new(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => client.StartAsync(new string('a', 257)));
    }

    [Fact]
    public async Task SendAsync_ReturnsTheSuccessText()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task<CommandResult> pending = client.SendAsync("state on");

        Assert.Equal("state on", await transport.ReceiveLineAsync());
        transport.SendLine("SUCCESS: real-time state notification set to ON");

        CommandResult result = await pending;
        Assert.True(result.Succeeded);
        Assert.Equal("real-time state notification set to ON", result.Text);
    }

    [Fact]
    public async Task SendAsync_ReportsAnErrorResponseWithoutThrowing()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task<CommandResult> pending = client.SendAsync("nonsense");

        await transport.ReceiveLineAsync();
        transport.SendLine("ERROR: unknown command [nonsense], enter 'help' for more options");

        CommandResult result = await pending;
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SendAsync_DoesNotSendTheNextCommandUntilTheCurrentOneCompletes()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task<CommandResult> first = client.SendAsync("version 6");
        Assert.Equal("version 6", await transport.ReceiveLineAsync());

        Task<CommandResult> second = client.SendAsync("state on");

        // OpenVPN discards pipelined commands, so the second must still be queued locally.
        await Task.Delay(100);
        Assert.False(transport.HasPendingClientLine);
        Assert.False(second.IsCompleted);

        transport.SendLine("SUCCESS: Management client version set to 6");
        await first;

        Assert.Equal("state on", await transport.ReceiveLineAsync());
        transport.SendLine("SUCCESS: real-time state notification set to ON");
        await second;
    }

    [Fact]
    public async Task SendAsync_WithEndMarkerCompletion_CollectsEveryDumpLine()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task<CommandResult> pending = client.SendAsync("log on all", CommandCompletion.EndMarker);
        await transport.ReceiveLineAsync();

        // The dump is preceded by its own SUCCESS line, which must not end the command.
        transport.SendLine("SUCCESS: real-time log notification set to ON");
        transport.SendLine("1787941144,I,OpenVPN 2.7.6");
        transport.SendLine("1787941144,I,Windows version: 10.0.22631");
        transport.SendLine("END");

        CommandResult result = await pending;
        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Lines.Count);
    }

    [Fact]
    public async Task Notifications_AreRoutedToTheChannelAndNotTreatedAsResponses()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task<CommandResult> pending = client.SendAsync("bytecount 1");
        await transport.ReceiveLineAsync();

        // A notification arriving mid command must not complete that command.
        transport.SendLine(">HOLD:Waiting for hold release:0");
        transport.SendLine("SUCCESS: bytecount interval changed");

        await pending;

        ManagementMessage message = await ReadNotificationAsync(client);
        HoldMessage hold = Assert.IsType<HoldMessage>(message);
        Assert.Equal(0, hold.TimeoutSeconds);
    }

    [Fact]
    public async Task Notifications_DeliverStateAndByteCountInOrder()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        transport.SendLine(">STATE:1787941299,CONNECTED,SUCCESS,192.168.255.6,127.0.0.1,1194,,");
        transport.SendLine(">BYTECOUNT:3196,4624");

        StateMessage state = Assert.IsType<StateMessage>(await ReadNotificationAsync(client));
        Assert.Equal("CONNECTED", state.Name);

        ByteCountMessage bytes = Assert.IsType<ByteCountMessage>(await ReadNotificationAsync(client));
        Assert.Equal(3196, bytes.BytesIn);
    }

    [Fact]
    public async Task OpenSessionAsync_IssuesTheExpectedCommandsInOrder()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task session = client.OpenSessionAsync();

        foreach (string expected in new[] { "version 6", "state on", "bytecount 1", "log on" })
        {
            Assert.Equal(expected, await transport.ReceiveLineAsync());
            transport.SendLine("SUCCESS: ok");
        }

        await session;
    }

    [Fact]
    public async Task SendCredentialsAsync_SendsUsernameThenPasswordForTheRealm()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task credentials = client.SendCredentialsAsync("Auth", "operator", "secret");

        Assert.Equal("username \"Auth\" \"operator\"", await transport.ReceiveLineAsync());
        transport.SendLine("SUCCESS: 'username' command succeeded");

        Assert.Equal("password \"Auth\" \"secret\"", await transport.ReceiveLineAsync());
        transport.SendLine("SUCCESS: 'password' command succeeded");

        await credentials;
    }

    [Fact]
    public async Task SendCredentialsAsync_WithoutUsername_SendsOnlyThePassword()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task credentials = client.SendCredentialsAsync("Private Key", username: null, "secret");

        Assert.Equal("password \"Private Key\" \"secret\"", await transport.ReceiveLineAsync());
        transport.SendLine("SUCCESS: ok");

        await credentials;
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("quote\"inside", "\"quote\\\"inside\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    public void Quote_EscapesQuotesAndBackslashes(string value, string expected)
    {
        Assert.Equal(expected, ManagementClient.Quote(value));
    }

    [Fact]
    public async Task SendAsync_WhenTheConnectionDropsMidCommand_FailsRatherThanHanging()
    {
        (FakeManagementStream transport, ManagementClient client) = await ConnectAsync();
        await using ManagementClient _ = client;

        Task<CommandResult> pending = client.SendAsync("state on");
        await transport.ReceiveLineAsync();

        transport.CloseFromServer();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
    }

    private static async Task<(FakeManagementStream Transport, ManagementClient Client)> ConnectAsync()
    {
        FakeManagementStream transport = new();
        ManagementClient client = new(transport);

        Task start = client.StartAsync(Password);
        transport.SendRaw("ENTER PASSWORD:");
        await transport.ReceiveLineAsync();
        transport.SendLine("SUCCESS: password is correct");
        await start;

        return (transport, client);
    }

    private static async Task<ManagementMessage> ReadNotificationAsync(ManagementClient client)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        return await client.Notifications.ReadAsync(cts.Token);
    }
}
