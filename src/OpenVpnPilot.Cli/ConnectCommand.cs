using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Management;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Platform.Windows.InteractiveService;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Connects using a configuration file and reports live state until the time is up.
/// </summary>
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

        ConsoleCredentialProvider credentials = new(
            ReadValue(args, "--username"),
            ReadValue(args, "--password"));

        await using ConnectionSupervisor supervisor = new(
            new WindowsOpenVpnLauncher(new InteractiveServicePipeClient()),
            new TcpManagementChannelFactory(),
            credentials);

        supervisor.StateChanged += OnStateChanged;

        ConnectionRequest request = new(
            ProfileId: Guid.Empty,
            ConfigurationPath: configurationPath,
            WorkingDirectory: Path.GetDirectoryName(configurationPath)!,
            ManagementPort: ReservePort(),
            AdditionalOptions: protectRoutes ? RouteProtection : []);

        try
        {
            VpnConnectionStatus initial = await supervisor.ConnectAsync(request);

            if (initial.State == VpnConnectionState.Failed)
            {
                Console.Error.WriteLine($"Launch refused: {initial.Message}");
                return 2;
            }

            Console.WriteLine($"Started openvpn, process {supervisor.ProcessId}, "
                + $"management port {request.ManagementPort}.");
        }
        catch (ManagementUnavailableException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        return await ObserveAsync(supervisor, seconds);
    }

    private static async Task<int> ObserveAsync(ConnectionSupervisor supervisor, int seconds)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (supervisor.Status.State == VpnConnectionState.Failed)
            {
                break;
            }

            await Task.Delay(250);
        }

        VpnConnectionStatus status = supervisor.Status;

        Console.WriteLine();
        Console.WriteLine($"State: {status.State}   received {Format(status.BytesReceived)}"
            + $"   sent {Format(status.BytesSent)}");

        if (status.Uptime(DateTimeOffset.UtcNow) is { } uptime)
        {
            Console.WriteLine($"Uptime: {uptime:hh\\:mm\\:ss}");
        }

        if (status.State == VpnConnectionState.Failed)
        {
            Console.Error.WriteLine($"Failed: {status.Message}");
            return 3;
        }

        await supervisor.DisconnectAsync();
        Console.WriteLine("Disconnected.");

        return status.State == VpnConnectionState.Connected ? 0 : 5;
    }

    private static void OnStateChanged(object? sender, VpnConnectionStatus status)
    {
        string detail = status.State switch
        {
            VpnConnectionState.Connected =>
                $"  local {status.LocalAddress}  server {status.ServerAddress}:{status.ServerPort}",
            VpnConnectionState.Reconnecting when status.Message.Length > 0 => $"  ({status.Message})",
            _ => string.Empty,
        };

        Console.WriteLine($"  {status.State,-15}{detail}");
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

    /// <summary>
    /// Supplies the credentials given on the command line, and reports when none were supplied.
    /// </summary>
    private sealed class ConsoleCredentialProvider : ICredentialProvider
    {
        private readonly string? username;
        private readonly string? password;

        public ConsoleCredentialProvider(string? username, string? password)
        {
            this.username = username;
            this.password = password;
        }

        public Task<VpnCredentials?> RequestAsync(CredentialRequest request, CancellationToken cancellationToken)
        {
            if (password is null)
            {
                Console.Error.WriteLine(
                    $"  the server asked for '{request.Realm}' credentials, none were supplied");
                return Task.FromResult<VpnCredentials?>(null);
            }

            if (request.IsRetry)
            {
                Console.Error.WriteLine($"  credentials for '{request.Realm}' were rejected");
                return Task.FromResult<VpnCredentials?>(null);
            }

            Console.WriteLine($"  answering credential request for '{request.Realm}'");
            return Task.FromResult<VpnCredentials?>(new VpnCredentials(username, password));
        }
    }
}
