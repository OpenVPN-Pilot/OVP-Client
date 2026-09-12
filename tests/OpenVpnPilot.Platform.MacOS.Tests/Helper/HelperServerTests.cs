using System.Net.Sockets;
using System.Runtime.Versioning;
using OpenVpnPilot.Platform.MacOS.Helper;
using OpenVpnPilot.Platform.MacOS.Helper.Native;
using OpenVpnPilot.Platform.MacOS.Helper.Security;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Tests.Helper;

/// <summary>
/// The helper answering real sessions on a real socket, with a stand in for OpenVPN.
/// </summary>
/// <remarks>
/// Everything here is the helper's own machinery: the socket, the identity of the caller, the spawn
/// with its inherited descriptors, the wait, the signals and the session that owns a tunnel. Only
/// OpenVPN itself is replaced, by a script that behaves the way the test needs, because a tunnel
/// needs root and a server and neither belongs in a unit test.
///
/// The helper runs in the test process here, as the account running the tests. The group membership
/// is a stand in, so what the test exercises is the rule and not the groups of that account.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class HelperServerTests : IAsyncLifetime, IDisposable
{
    private const string ListeningLine = "MANAGEMENT: TCP Socket listening on [AF_INET]127.0.0.1:25340";

    /// <summary>
    /// Short on purpose: a socket path on macOS may hold 104 characters, and the per user temporary
    /// directory alone is longer than that with a name after it.
    /// </summary>
    private readonly string root = Path.Combine("/tmp", "ovp-h-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationTokenSource stopping = new();

    /// <summary>
    /// Every stand in for OpenVPN a test started, so none of them outlives the test.
    /// </summary>
    private readonly List<int> launched = [];

    private Socket? listener;
    private Task? server;

    public string SocketPath => Path.Combine(root, "helper.sock");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the helper and waits for it, which xunit does before Dispose.
    /// </summary>
    /// <remarks>
    /// A session that ends takes its tunnels with it, and the helper does not wait for that before
    /// it returns, so the processes and the directories they own can still be going when the test
    /// method is over. Everything a test started is therefore waited for here, and the last word is
    /// a signal that cannot be ignored, so no stand in outlives its test and the tree can be deleted.
    /// </remarks>
    public async Task DisposeAsync()
    {
        await stopping.CancelAsync();
        listener?.Dispose();

        if (server is not null)
        {
            try
            {
                await server;
            }
            catch (OperationCanceledException)
            {
                // Expected: the test asked it to stop.
            }
        }

        foreach (int processId in launched)
        {
            for (int attempt = 0; attempt < 100 && IsRunning(processId); attempt++)
            {
                if (attempt == 20)
                {
                    _ = Libc.kill(processId, Libc.SigKill);
                }

                await Task.Delay(50, CancellationToken.None);
            }
        }

        await DeleteTreeAsync();
    }

    public void Dispose()
    {
        stopping.Dispose();
        listener?.Dispose();
    }

    /// <summary>
    /// Deletes the test's tree once the helper has finished deleting its own part of it.
    /// </summary>
    /// <remarks>
    /// A tunnel that ends takes its directory with it, and that happens on the helper's own thread
    /// after the process is gone. Deleting the tree from under it fails on a directory that is no
    /// longer there or on one that has just been emptied, which is why this waits for the runtime
    /// directory to be given up and then still tolerates losing the race.
    /// </remarks>
    private async Task DeleteTreeAsync()
    {
        string runtime = Path.Combine(root, "runtime");

        for (int attempt = 0; attempt < 100 && Directory.Exists(runtime); attempt++)
        {
            if (!Directory.EnumerateDirectories(runtime, "*", SearchOption.AllDirectories).Any())
            {
                break;
            }

            await Task.Delay(50, CancellationToken.None);
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(50, CancellationToken.None);
            }
        }

        Directory.Delete(root, recursive: true);
    }

    [MacOSFact]
    public async Task Hello_ReportsTheHelperTheOpenVpnAndWhatTheCallerMay()
    {
        Start(FakeOpenVpn("sleep"));

        await using HelperConnection connection = await ConnectAsync();

        Assert.Equal(HelperMessageType.Hello, connection.Greeting.Type);
        Assert.Equal(HelperProtocol.Version, connection.Greeting.ProtocolVersion);
        Assert.Equal("9.9.9", connection.Greeting.OpenVpnVersion);
        Assert.True(connection.Greeting.Authorised);
        Assert.Equal("a member of admin", connection.Greeting.Authorisation);
    }

    [MacOSFact]
    public async Task Hello_ForAnAccountThatIsNotAuthorised_SaysSo()
    {
        Start(FakeOpenVpn("sleep"), authorised: false);

        await using HelperConnection connection = await ConnectAsync();

        Assert.False(connection.Greeting.Authorised);
        Assert.Equal("not authorised", connection.Greeting.Authorisation);
    }

    [MacOSFact]
    public async Task Launch_StartsOpenVpnAndAnswersOnceItIsListening()
    {
        Start(FakeOpenVpn("sleep"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse response = await LaunchAsync(connection);

        Assert.Equal(HelperMessageType.Launched, response.Type);
        Assert.True(response.ProcessId > 1);
        Assert.True(IsRunning(response.ProcessId));
    }

    /// <summary>
    /// The copy OpenVPN is given is the one the policy wrote, and only root can read it.
    /// </summary>
    [MacOSFact]
    public async Task Launch_WritesTheConfigurationWhereOnlyItsOwnerCanReadIt()
    {
        Start(FakeOpenVpn("print-config"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse response = await LaunchAsync(connection, "client\n# a comment\nremote vpn.example.com 1194\n");

        Assert.Equal(HelperMessageType.Launched, response.Type);

        string configuration = Assert.Single(Directory.EnumerateFiles(root, "config.ovpn", SearchOption.AllDirectories));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(configuration));
        Assert.Equal("client\nremote \"vpn.example.com\" \"1194\"\n", await File.ReadAllTextAsync(configuration, CancellationToken.None));
    }

    /// <summary>
    /// The password reaches OpenVPN on descriptor three and nowhere else.
    /// </summary>
    [MacOSFact]
    public async Task Launch_HandsTheManagementPasswordOverOnADescriptor()
    {
        Start(FakeOpenVpn("print-secret"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse response = await LaunchAsync(connection);

        Assert.Equal(HelperMessageType.Launched, response.Type);

        Assert.Equal("0123456789ABCDEF", await ReadWhenWrittenAsync(Path.Combine(root, "secret.txt")));
    }

    [MacOSFact]
    public async Task Launch_AConfigurationThatWouldRunAProgram_IsRefusedWithTheReason()
    {
        Start(FakeOpenVpn("sleep"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse response = await LaunchAsync(connection, "client\nup /tmp/x\n");

        Assert.Equal(HelperMessageType.Refused, response.Type);
        Assert.Equal(HelperRefusal.Configuration, response.RefusalCode);
        Assert.Contains("'up'", response.Message!, StringComparison.Ordinal);
    }

    [MacOSFact]
    public async Task Launch_ByAnAccountThatIsNotAuthorised_IsRefused()
    {
        Start(FakeOpenVpn("sleep"), authorised: false);

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse response = await LaunchAsync(connection);

        Assert.Equal(HelperMessageType.Refused, response.Type);
        Assert.Equal(HelperRefusal.NotAuthorised, response.RefusalCode);
        Assert.Contains("administrator installed", response.Message!, StringComparison.Ordinal);
    }

    [MacOSFact]
    public async Task Launch_WithoutTheSpecification_SaysWhatIsMissing()
    {
        Start(FakeOpenVpn("sleep"));

        await using HelperConnection connection = await ConnectAsync();

        HelperResponse response = await connection.ExchangeAsync(
            new HelperRequest { Type = HelperMessageType.Launch },
            CancellationToken.None);

        Assert.Equal(HelperMessageType.Refused, response.Type);
        Assert.Equal(HelperRefusal.Protocol, response.RefusalCode);
        Assert.Contains("'launch' member", response.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A request with every value left out is answered, and the session survives answering it.
    /// </summary>
    /// <remarks>
    /// What a sender leaves out arrives as nothing, so a specification can be empty. Answering one
    /// used to end the session without a word, which left the caller waiting for an answer nobody
    /// was going to send. Both halves are asserted here: the refusal, and the session going on.
    /// </remarks>
    [MacOSFact]
    public async Task Launch_WithASpecificationThatIsEmpty_IsRefusedAndTheSessionGoesOn()
    {
        Start(FakeOpenVpn("sleep"));

        await using HelperConnection connection = await ConnectAsync();

        HelperResponse refusal = await connection.ExchangeAsync(
            new HelperRequest { Type = HelperMessageType.Launch, Launch = new LaunchSpecification() },
            CancellationToken.None);

        Assert.Equal(HelperMessageType.Refused, refusal.Type);
        Assert.Equal(HelperRefusal.Protocol, refusal.RefusalCode);

        HelperResponse next = await connection.ExchangeAsync(
            new HelperRequest { Type = HelperMessageType.List },
            CancellationToken.None);

        Assert.Equal(HelperMessageType.Tunnels, next.Type);
    }

    [MacOSFact]
    public async Task Launch_WhenOpenVpnEndsAtOnce_IsRefusedWithWhatItSaid()
    {
        Start(FakeOpenVpn("refuse"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse response = await LaunchAsync(connection);

        Assert.Equal(HelperMessageType.Refused, response.Type);
        Assert.Equal(HelperRefusal.LaunchFailed, response.RefusalCode);
        Assert.Contains("Options error", response.Message!, StringComparison.Ordinal);
    }

    [MacOSFact]
    public async Task List_ShowsTheCallersTunnelsAndWhichSessionOwnsThem()
    {
        Start(FakeOpenVpn("sleep"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse launched = await LaunchAsync(connection);

        HelperResponse listed = await connection.ExchangeAsync(
            new HelperRequest { Type = HelperMessageType.List },
            CancellationToken.None);

        TunnelDescription tunnel = Assert.Single(listed.Tunnels!);

        Assert.Equal(launched.ProcessId, tunnel.ProcessId);
        Assert.True(tunnel.InThisSession);

        await using HelperConnection other = await ConnectAsync();

        HelperResponse fromOther = await other.ExchangeAsync(
            new HelperRequest { Type = HelperMessageType.List },
            CancellationToken.None);

        Assert.False(Assert.Single(fromOther.Tunnels!).InThisSession);
    }

    [MacOSFact]
    public async Task Terminate_EndsTheTunnel()
    {
        Start(FakeOpenVpn("sleep"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse launched = await LaunchAsync(connection);

        HelperResponse terminated = await connection.ExchangeAsync(
            new HelperRequest { Type = HelperMessageType.Terminate, ProcessId = launched.ProcessId, GraceMilliseconds = 500 },
            CancellationToken.None);

        Assert.Equal(HelperMessageType.Terminated, terminated.Type);
        await WaitUntilGoneAsync(launched.ProcessId);
    }

    /// <summary>
    /// A process that ignores the request is ended anyway: a tunnel that cannot be stopped is worse
    /// than one that is killed.
    /// </summary>
    [MacOSFact]
    public async Task Terminate_AProcessThatIgnoresTheSignal_IsEndedAnyway()
    {
        Start(FakeOpenVpn("ignore-term"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse launched = await LaunchAsync(connection);

        HelperResponse terminated = await connection.ExchangeAsync(
            new HelperRequest { Type = HelperMessageType.Terminate, ProcessId = launched.ProcessId, GraceMilliseconds = 300 },
            CancellationToken.None);

        Assert.Equal(HelperMessageType.Terminated, terminated.Type);
        await WaitUntilGoneAsync(launched.ProcessId);
    }

    [MacOSFact]
    public async Task Terminate_ATunnelOfAnotherAccount_IsNotEnded()
    {
        Start(FakeOpenVpn("sleep"));

        await using HelperConnection connection = await ConnectAsync();
        HelperResponse launched = await LaunchAsync(connection);

        // The helper only ever ends a tunnel of the account that asked, and an identifier it does
        // not know is answered as already ended rather than acted on.
        HelperResponse terminated = await connection.ExchangeAsync(
            new HelperRequest { Type = HelperMessageType.Terminate, ProcessId = launched.ProcessId + 1, GraceMilliseconds = 100 },
            CancellationToken.None);

        Assert.Equal(HelperMessageType.Terminated, terminated.Type);
        Assert.True(IsRunning(launched.ProcessId));
    }

    /// <summary>
    /// The application that owned a tunnel is the only one that can drive it, so a session that
    /// ends takes its tunnels with it.
    /// </summary>
    [MacOSFact]
    public async Task ASessionThatCloses_EndsTheTunnelsItStarted()
    {
        Start(FakeOpenVpn("sleep"));

        HelperConnection connection = await ConnectAsync();
        HelperResponse launched = await LaunchAsync(connection);

        await connection.DisposeAsync();

        await WaitUntilGoneAsync(launched.ProcessId);
    }

    [MacOSFact]
    public async Task ARequestBeforeTheVersionsAreAgreed_IsRefused()
    {
        Start(FakeOpenVpn("sleep"));

        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), CancellationToken.None);

        await using NetworkStream stream = new(client);

        await HelperFraming.WriteRequestAsync(
            stream,
            new HelperRequest { Type = HelperMessageType.List },
            CancellationToken.None);

        HelperResponse? response = await HelperFraming.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal(HelperMessageType.Refused, response!.Type);
        Assert.Equal(HelperRefusal.Protocol, response.RefusalCode);
    }

    [MacOSFact]
    public async Task AnotherProtocolVersion_IsRefusedWithBothVersions()
    {
        Start(FakeOpenVpn("sleep"));

        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), CancellationToken.None);

        await using NetworkStream stream = new(client);

        await HelperFraming.WriteRequestAsync(
            stream,
            new HelperRequest { Type = HelperMessageType.Hello, ProtocolVersion = HelperProtocol.Version + 1 },
            CancellationToken.None);

        HelperResponse? response = await HelperFraming.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal(HelperRefusal.Version, response!.RefusalCode);
        Assert.Contains("helper package", response.Message!, StringComparison.Ordinal);
    }

    private void Start(string openVpn, bool authorised = true)
    {
        HelperOptions options = new(
            openVpn,
            Path.Combine(root, "dns-updown"),
            Path.Combine(root, "runtime"),
            Path.Combine(root, "configurations"),
            RequireRootOwnership: false);

        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(8);

        HelperServer helper = new(options, new HelperLog(Path.Combine(root, "helper.log")), new Groups(authorised));
        server = helper.RunAsync(listener, idleExit: null, stopping.Token);
    }

    private Task<HelperConnection> ConnectAsync() =>
        HelperConnection.OpenAsync("tests", SocketPath, CancellationToken.None);

    private async Task<HelperResponse> LaunchAsync(HelperConnection connection, string configuration = "client\nremote vpn.example.com 1194\n")
    {
        HelperResponse response = await connection.ExchangeAsync(
            new HelperRequest
            {
                Type = HelperMessageType.Launch,
                Launch = new LaunchSpecification
                {
                    Configuration = configuration,
                    ManagementPort = 25340,
                    ManagementPassword = "0123456789ABCDEF",
                    Verbosity = 3,
                },
            },
            CancellationToken.None);

        if (response.ProcessId > 1)
        {
            launched.Add(response.ProcessId);
        }

        return response;
    }

    /// <summary>
    /// Writes a script that behaves the way one test needs OpenVPN to behave.
    /// </summary>
    private string FakeOpenVpn(string behaviour)
    {
        string path = Path.Combine(root, "openvpn");

        string body = behaviour switch
        {
            "refuse" => "echo 'Options error: You must define CA file'\nexit 1\n",
            "print-config" => $"echo '{ListeningLine}'\ncat \"$2\" > '{root}/seen-config.txt'\nsleep 30\n",
            // Written and then renamed, so a test that finds the file finds all of it: a redirection
            // creates the file before the command it feeds has written a byte.
            "print-secret" => $"echo '{ListeningLine}'\nhead -n 1 /dev/fd/3 > '{root}/secret.part'\nmv '{root}/secret.part' '{root}/secret.txt'\nsleep 30\n",
            "ignore-term" => $"trap '' TERM\necho '{ListeningLine}'\nwhile true; do sleep 1; done\n",
            _ => $"echo '{ListeningLine}'\nsleep 30\n",
        };

        File.WriteAllText(
            path,
            "#!/bin/sh\n"
            + "if [ \"$1\" = \"--version\" ]; then echo 'OpenVPN 9.9.9 test build'; exit 0; fi\n"
            + body);

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    /// <summary>
    /// Whether a process still exists, asked with a signal of zero.
    /// </summary>
    /// <remarks>
    /// Not through the Process class: using it in this process installs the runtime's child signal
    /// handler, which reaps children the helper under test is waiting for itself.
    /// </remarks>
    private static bool IsRunning(int processId) => Libc.kill(processId, 0) == 0;

    /// <summary>
    /// Reads a file the stand in writes under another name and renames when it is complete.
    /// </summary>
    /// <remarks>
    /// Generous, because the machine running this can be busy with the rest of the suite, and a
    /// spawn that is merely slow must not read as a spawn that went wrong.
    /// </remarks>
    private static async Task<string> ReadWhenWrittenAsync(string path)
    {
        for (int attempt = 0; attempt < 300 && !File.Exists(path); attempt++)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.True(
            File.Exists(path),
            $"Nothing was written to {path}. The directory holds "
            + string.Join(", ", Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Select(Path.GetFileName))
            + ". The helper said: "
            + await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "helper.log"), CancellationToken.None));

        return (await File.ReadAllTextAsync(path, CancellationToken.None)).Trim();
    }

    private static async Task WaitUntilGoneAsync(int processId)
    {
        for (int attempt = 0; attempt < 100 && IsRunning(processId); attempt++)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.False(IsRunning(processId));
    }

    /// <summary>
    /// Group membership as the test wants it, rather than as the machine has it.
    /// </summary>
    private sealed class Groups : IGroupMembership
    {
        private readonly bool authorised;

        public Groups(bool authorised) => this.authorised = authorised;

        public bool IsMember(uint userId, uint groupId) => authorised && groupId == HelperInstallation.AdministratorsGroupId;

        public uint? ResolveGroup(string name) => null;
    }
}
