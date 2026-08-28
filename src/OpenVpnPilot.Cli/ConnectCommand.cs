using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.OpenVpn.Management;
using OpenVpnPilot.Platform.Windows.InteractiveService;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Connects using a configuration file and reports live state until the time is up.
/// </summary>
/// <remarks>
/// This exercises the launcher and the management client together against a real OpenVPN process.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ConnectCommand
{
    /// <summary>
    /// Pull filters that stop a server from taking over the host routing table and DNS. Every one of
    /// these is accepted by the interactive service option whitelist.
    /// </summary>
    private static readonly string[] RouteProtection =
    [
        "--pull-filter ignore \"redirect-gateway\"",
        "--pull-filter ignore \"dhcp-option\"",
        "--pull-filter ignore \"block-outside-dns\"",
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("A configuration file is required.");
            return 1;
        }

        string configurationPath = Path.GetFullPath(args[0]);
        if (!File.Exists(configurationPath))
        {
            Console.Error.WriteLine($"Configuration not found: {configurationPath}");
            return 1;
        }

        int seconds = ReadInt(args, "--seconds", 30);
        bool protectRoutes = args.Contains("--protect-routes", StringComparer.Ordinal);
        string? username = ReadValue(args, "--username");
        string? password = ReadValue(args, "--password");

        int managementPort = ReservePort();
        string managementPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        OpenVpnLaunchRequest request = new(
            ConfigurationPath: configurationPath,
            WorkingDirectory: Path.GetDirectoryName(configurationPath)!,
            ManagementEndpoint: managementPort,
            ManagementPassword: managementPassword,
            AdditionalOptions: protectRoutes ? RouteProtection : []);

        WindowsOpenVpnLauncher launcher = new(new InteractiveServicePipeClient());
        OpenVpnLaunchResult launch = await launcher.LaunchAsync(request);

        if (!launch.Succeeded)
        {
            Console.Error.WriteLine($"Launch refused (0x{launch.ErrorCode:X8}): {launch.Message}");
            return 2;
        }

        Console.WriteLine($"Started openvpn, process {launch.ProcessId}, management port {managementPort}.");

        using TcpClient socket = await ConnectWithRetryAsync(managementPort, TimeSpan.FromSeconds(10));
        await using ManagementClient client = new(socket.GetStream());

        await client.StartAsync(managementPassword);
        await client.OpenSessionAsync();

        return await ObserveAsync(client, username, password, seconds);
    }

    private static async Task<int> ObserveAsync(
        ManagementClient client,
        string? username,
        string? password,
        int seconds)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(seconds));
        bool connected = false;
        long bytesIn = 0;
        long bytesOut = 0;

        try
        {
            await foreach (ManagementMessage message in client.Notifications.ReadAllAsync(deadline.Token))
            {
                switch (message)
                {
                    case HoldMessage:
                        await client.ReleaseHoldAsync(deadline.Token);
                        break;

                    case StateMessage state:
                        Console.WriteLine($"  state    {state.Name}"
                            + (state.LocalAddress is null ? string.Empty : $"  local {state.LocalAddress}")
                            + (state.RemoteAddress is null ? string.Empty : $"  server {state.RemoteAddress}:{state.RemotePort}")
                            + (state.Description is null ? string.Empty : $"  ({state.Description})"));

                        connected |= state.Name == "CONNECTED";
                        break;

                    case ByteCountMessage counters:
                        bytesIn = counters.BytesIn;
                        bytesOut = counters.BytesOut;
                        break;

                    case PasswordRequestMessage credentials:
                        if (password is null)
                        {
                            Console.Error.WriteLine($"  the server asked for '{credentials.Realm}' credentials, none supplied");
                            return 3;
                        }

                        Console.WriteLine($"  answering credential request for '{credentials.Realm}'");
                        await client.SendCredentialsAsync(
                            credentials.Realm,
                            credentials.NeedsUsername ? username : null,
                            password,
                            deadline.Token);
                        break;

                    case PasswordVerificationFailedMessage failure:
                        Console.Error.WriteLine($"  credentials rejected for '{failure.Realm}': {failure.Reason}");
                        return 3;

                    case FatalMessage fatal:
                        Console.Error.WriteLine($"  fatal: {fatal.Text}");
                        return 4;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The observation window elapsed, which is the normal end of this command.
        }

        Console.WriteLine();
        Console.WriteLine($"Connected: {connected}   received {Format(bytesIn)}   sent {Format(bytesOut)}");

        await TryStopAsync(client);
        return connected ? 0 : 5;
    }

    private static async Task TryStopAsync(ManagementClient client)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await client.SignalAsync("SIGTERM", timeout.Token);
            Console.WriteLine("Disconnected.");
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The process did not acknowledge SIGTERM in time.");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Disconnected.");
        }
    }

    private static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    private static async Task<TcpClient> ConnectWithRetryAsync(int port, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            TcpClient client = new();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"The management interface on port {port} never started listening. "
                        + "OpenVPN usually exits during option parsing when this happens.");
                }

                await Task.Delay(250);
            }
        }
    }

    private static int ReservePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? ReadValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int ReadInt(string[] args, string name, int fallback) =>
        int.TryParse(ReadValue(args, name), CultureInfo.InvariantCulture, out int value) ? value : fallback;
}
