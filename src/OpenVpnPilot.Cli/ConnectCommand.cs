using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Ipc;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Management;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Platform.Windows.InteractiveService;
using OpenVpnPilot.Platform.Windows.Runtime;

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
        // A bare name is the common case, so it does not have to be introduced by a flag.
        string? profileName = ReadValue(args, "--profile") ?? BareName(args);

        if (args.Length == 0 || (profileName is null && args[0].StartsWith("--", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("A profile name, a configuration file or --profile <name> is required.");
            return 1;
        }

        // The application owns the tunnels, the store and the window that shows them, so a request
        // is handed to it rather than driving a second, invisible set beside it. When none is
        // running it is started first: a command that only works when a window happens to be open
        // is no use to anything calling it.
        if (profileName is not null && !args.Contains("--detached", StringComparer.Ordinal))
        {
            string? reply = await PilotCommandClient.SendAsync(PilotCommands.Connect + profileName);

            if (reply is null && !IsConfigurationFile(profileName))
            {
                bool headless = args.Contains("--headless", StringComparer.Ordinal);

                if (!await ApplicationLauncher.StartAsync(headless))
                {
                    return 4;
                }

                reply = await PilotCommandClient.SendAsync(PilotCommands.Connect + profileName);
            }

            if (reply is not null)
            {
                Console.WriteLine(reply);
                return reply.StartsWith("No stored profile", StringComparison.Ordinal) ? 1 : 0;
            }
        }

        Guid profileId = Guid.Empty;
        string configurationPath;
        MaterialisedProfile? materialised = null;

        if (profileName is not null && !File.Exists(Path.GetFullPath(profileName)))
        {
            StoredProfile? stored = await LoadProfileAsync(profileName);
            if (stored is null)
            {
                Console.Error.WriteLine($"No stored profile matches '{profileName}'.");
                return 1;
            }

            profileId = stored.Id;
            materialised = await new WindowsProfileMaterializer()
                .MaterialiseAsync(stored.Id, stored.Configuration);

            configurationPath = materialised.Path;
            Console.WriteLine($"Using stored profile '{stored.Name}'.");
        }
        else
        {
            configurationPath = Path.GetFullPath(profileName ?? args[0]);
            if (!File.Exists(configurationPath))
            {
                Console.Error.WriteLine($"Configuration not found: {configurationPath}");
                return 1;
            }
        }

        try
        {
            return await RunConnectedAsync(args, profileId, configurationPath);
        }
        finally
        {
            if (materialised is not null)
            {
                await materialised.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// True when the argument names a file on disk rather than a stored profile.
    /// </summary>
    /// <remarks>
    /// Connecting straight from a file is the one case that does not go through the application: it
    /// is for trying a configuration that has not been imported, and importing it silently to do so
    /// would be a surprise.
    /// </remarks>
    private static bool IsConfigurationFile(string value)
    {
        try
        {
            return File.Exists(Path.GetFullPath(value));
        }
        catch (ArgumentException)
        {
            // Not a path at all, which means it is a name.
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<int> RunConnectedAsync(string[] args, Guid profileId, string configurationPath)
    {
        // Asked before the pipe is opened. Without it a machine that has no interactive service
        // fails inside the launch with an exception naming a named pipe, which is true and useless.
        if (!await EnvironmentReadiness.EnsureReadyAsync())
        {
            return 2;
        }

        int seconds = ReadInt(args, "--seconds", 30);
        bool protectRoutes = args.Contains("--protect-routes", StringComparer.Ordinal);

        ConsoleCredentialProvider credentials = new(
            ReadValue(args, "--username"),
            ReadValue(args, "--password"),
            ReadValue(args, "--challenge"));

        await using ConnectionSupervisor supervisor = new(
            new WindowsOpenVpnLauncher(new InteractiveServicePipeClient()),
            new TcpManagementChannelFactory(),
            credentials);

        supervisor.StateChanged += OnStateChanged;

        ConnectionRequest request = new(
            ProfileId: profileId,
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
        catch (Exception exception)
            when (exception is ManagementUnavailableException or IOException or TimeoutException)
        {
            // The environment was checked a moment ago, so anything here is the service or the
            // socket failing during the attempt rather than a missing installation. It is still a
            // sentence to print rather than a stack trace to read.
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

        // Tearing down matters most when the attempt failed, because a refused connection can
        // leave the OpenVPN process waiting instead of exiting.
        await supervisor.DisconnectAsync();

        if (status.State == VpnConnectionState.Failed)
        {
            Console.Error.WriteLine($"Failed: {status.Message}");
            return 3;
        }

        Console.WriteLine("Disconnected.");
        return status.State == VpnConnectionState.Connected ? 0 : 5;
    }

    private static async Task<StoredProfile?> LoadProfileAsync(string name)
    {
        string databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenVpnPilot",
            "pilot.db");

        if (!File.Exists(databasePath))
        {
            return null;
        }

        DbContextOptions<PilotDbContext> options = new DbContextOptionsBuilder<PilotDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        await using PilotDbContext context = new(options);

        Profile? match = await context.Profiles
            .Where(profile => EF.Functions.Like(profile.Name, $"%{name}%"))
            .OrderBy(profile => profile.Name)
            .FirstOrDefaultAsync();

        return match is null ? null : new StoredProfile(match.Id, match.Name, match.Configuration);
    }

    private sealed record StoredProfile(Guid Id, string Name, string Configuration);

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

    /// <summary>
    /// The first argument that is neither a flag nor a flag's value, and is not an existing file.
    /// </summary>
    private static string? BareName(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[index] is "--profile" or "--seconds" or "--username" or "--password"
                    or "--challenge")
                {
                    index++;
                }

                continue;
            }

            return args[index];
        }

        return null;
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
    /// <summary>
    /// Answers credential requests from what was given on the command line.
    /// </summary>
    /// <remarks>
    /// A one time code cannot be typed by a command that is not being watched, so it is supplied up
    /// front. That is not how a person would use a code, and it is exactly how a test does: without
    /// it neither kind of challenge can be exercised without a window.
    /// </remarks>
    private sealed class ConsoleCredentialProvider : ICredentialProvider
    {
        private readonly string? username;
        private readonly string? password;
        private readonly string? challengeResponse;

        public ConsoleCredentialProvider(string? username, string? password, string? challengeResponse)
        {
            this.username = username;
            this.password = password;
            this.challengeResponse = challengeResponse;
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

            if (request.Challenge is { } challenge)
            {
                if (challengeResponse is null)
                {
                    Console.Error.WriteLine(
                        $"  the server asked for a code: {challenge.Text}. Pass --challenge <value>.");
                    return Task.FromResult<VpnCredentials?>(null);
                }

                Console.WriteLine($"  answering the code challenge for '{request.Realm}'");
            }
            else
            {
                Console.WriteLine($"  answering credential request for '{request.Realm}'");
            }

            return Task.FromResult<VpnCredentials?>(
                new VpnCredentials(username, password, challengeResponse));
        }
    }
}
