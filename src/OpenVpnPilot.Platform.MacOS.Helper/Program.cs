using System.Globalization;
using System.Net.Sockets;
using OpenVpnPilot.Platform.MacOS.Helper.Dns;
using OpenVpnPilot.Platform.MacOS.Helper.Native;
using OpenVpnPilot.Platform.MacOS.Helper.Security;
using OpenVpnPilot.Platform.MacOS.Helper;
using OpenVpnPilot.Platform.MacOS.Protocol;

// The helper is started in one of three ways, and which one is decided before anything else:
//
//   by OpenVPN, as the command that applies name servers, with no arguments and script_type set;
//   by launchd, as the root daemon, with no arguments, taking the socket launchd holds for it;
//   by a developer or a test, with --socket and paths of its own, as whatever account runs it.
//
// The installed job definition passes no arguments, and only root can change it, so the paths an
// installed helper uses are the fixed ones.

const string LogPath = "/Library/Logs/OpenVpnPilot/helper.log";

if (!OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("The helper runs on macOS only.");
    return 1;
}

if (DnsHook.IsInvokedAsHook(args))
{
    return DnsHook.Run(new HelperLog(LogPath), HelperInstallation.DnsScriptPath, HelperOptions.Installed.DnsStateDirectory);
}

// Nothing the helper creates is meant for another account: every directory and file is private.
_ = Libc.umask(Convert.ToUInt32("077", 8));

try
{
    if (args.Length == 0)
    {
        return await RunInstalledAsync();
    }

    return await RunForDevelopmentAsync(args);
}
catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine($"The helper could not run: {exception.Message}");
    return 1;
}

static async Task<int> RunInstalledAsync()
{
    HelperLog log = new(LogPath);

    if (Libc.geteuid() != 0)
    {
        Console.Error.WriteLine("The helper is started by launchd as root. Run it with --socket for development.");
        return 1;
    }

    using Socket listener = TakeLaunchdSocket();

    // Idle for a minute with nothing to do, it ends; launchd starts it again on the next connection.
    await new HelperServer(HelperOptions.Installed, log, new DirectoryGroupMembership())
        .RunAsync(listener, TimeSpan.FromMinutes(1), CancellationToken.None);

    return 0;
}

static async Task<int> RunForDevelopmentAsync(string[] arguments)
{
    Dictionary<string, string> values = [];

    for (int index = 0; index < arguments.Length; index++)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
        {
            Console.Error.WriteLine($"Unexpected argument '{arguments[index]}'.");
            return 2;
        }

        values[arguments[index][2..]] = arguments[++index];
    }

    if (!values.TryGetValue("socket", out string? socketPath))
    {
        Console.Error.WriteLine("--socket <path> is required outside launchd.");
        return 2;
    }

    string runtime = values.GetValueOrDefault("runtime") ?? Path.Combine(Path.GetTempPath(), "openvpnpilot-helper-runtime");

    HelperOptions options = new(
        values.GetValueOrDefault("openvpn") ?? HelperInstallation.OpenVpnPath,
        values.GetValueOrDefault("dns-script") ?? HelperInstallation.DnsScriptPath,
        runtime,
        values.GetValueOrDefault("configurations") ?? HelperInstallation.ConfigurationsDirectory,
        RequireRootOwnership: Libc.geteuid() == 0);

    HelperLog log = new(values.GetValueOrDefault("log") is { } path && path != "-" ? path : null);

    File.Delete(socketPath);

    using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    listener.Bind(new UnixDomainSocketEndPoint(socketPath));
    listener.Listen(16);

    TimeSpan? idle = values.TryGetValue("idle-exit", out string? seconds)
        ? TimeSpan.FromSeconds(int.Parse(seconds, CultureInfo.InvariantCulture))
        : null;

    using CancellationTokenSource stop = new();
    Console.CancelKeyPress += (_, cancel) =>
    {
        cancel.Cancel = true;
        stop.Cancel();
    };

    await new HelperServer(options, log, new DirectoryGroupMembership()).RunAsync(listener, idle, stop.Token);
    return 0;
}

static unsafe Socket TakeLaunchdSocket()
{
    int* descriptors = null;
    nuint count = 0;

    int result = Libc.launch_activate_socket("Listener", &descriptors, &count);

    if (result != 0 || count == 0)
    {
        throw new InvalidOperationException($"launchd handed over no socket (error {result}).");
    }

    try
    {
        return new Socket(new SafeSocketHandle(descriptors[0], ownsHandle: true));
    }
    finally
    {
        Libc.free(descriptors);
    }
}
