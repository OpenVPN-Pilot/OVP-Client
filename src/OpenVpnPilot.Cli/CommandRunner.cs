using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.Windows.Diagnostics;
using OpenVpnPilot.Platform.Windows.InteractiveService;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Entry point for the headless companion command.
/// </summary>
internal static class CommandRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return WriteUsage();
        }

        string command = args[0];

        if (command is "--help" or "-h" or "help")
        {
            return args.Length > 1 ? WriteCommandHelp(args[1]) : WriteUsage();
        }

        if (command is "--version" or "-v" or "version")
        {
            Console.WriteLine($"ovp {VersionInfo.Current}");
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "This build supports Windows only. Support for another system means adding an "
                + "implementation of the platform interfaces, not changing the rest of the application.");
            return 1;
        }

        return command switch
        {
            "doctor" => await RunDoctorAsync(),
            "connect" => await ConnectCommand.RunAsync(args[1..]),
            "import" => await ImportCommand.RunAsync(args[1..]),
            "list" => await ListCommand.RunAsync(args[1..]),
            _ => Unknown(command),
        };
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunDoctorAsync()
    {
        WindowsOpenVpnEnvironmentProbe probe = new(new InteractiveServicePipeClient());
        OpenVpnEnvironmentReport report = await probe.ProbeAsync();

        Console.WriteLine("OpenVPN environment");
        Console.WriteLine();

        foreach (EnvironmentCheck check in report.Checks)
        {
            string marker = check.Status switch
            {
                EnvironmentCheckStatus.Passed => "ok  ",
                EnvironmentCheckStatus.Warning => "warn",
                _ => "fail",
            };

            Console.WriteLine($"  [{marker}] {check.Id,-19} {check.Detail}");
        }

        Console.WriteLine();

        if (!report.CanConnect)
        {
            Console.WriteLine("Result: connections are not possible until the failures above are resolved.");
            Console.WriteLine();
            Console.WriteLine("OpenVPN Community can be installed from https://openvpn.net/community-downloads/");
            Console.WriteLine("Make sure the OpenVPN Interactive Service component is included.");
            return 2;
        }

        Console.WriteLine(report.HasWarnings
            ? "Result: connections are possible, with the limitations noted above."
            : "Result: ready.");

        return 0;
    }

    private static int WriteUsage()
    {
        Console.WriteLine("ovp - command line companion for OpenVpnPilot");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ovp <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  doctor                     Check whether OpenVPN is installed and usable.");
        Console.WriteLine("  list                       List the profiles in the local store.");
        Console.WriteLine("  import <path> [--commit]   Examine .ovpn files and optionally store them.");
        Console.WriteLine("  connect <config|--profile> Connect and report live status.");
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine("  -h, --help [command]       Show this text, or the help for one command.");
        Console.WriteLine("  -v, --version              Print the version.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine(@"  ovp import C:\profiles --commit");
        Console.WriteLine("  ovp connect --profile site-alpha --protect-routes");
        return 0;
    }

    private static int WriteCommandHelp(string command)
    {
        switch (command)
        {
            case "doctor":
                Console.WriteLine("ovp doctor");
                Console.WriteLine();
                Console.WriteLine("Reports each requirement separately: the OpenVPN installation, the");
                Console.WriteLine("executable and its version, the interactive service, its control pipe,");
                Console.WriteLine("and whether this account may launch configurations from outside the");
                Console.WriteLine("OpenVPN configuration directory.");
                Console.WriteLine();
                Console.WriteLine("Exit codes: 0 ready, 2 something blocks connecting.");
                return 0;

            case "list":
                Console.WriteLine("ovp list [--json]");
                Console.WriteLine();
                Console.WriteLine("Lists the stored profiles with their endpoint and last use.");
                Console.WriteLine();
                Console.WriteLine("  --json                 Emit machine readable output.");
                return 0;

            case "import":
                Console.WriteLine("ovp import <file or directory> [--commit]");
                Console.WriteLine();
                Console.WriteLine("Examines .ovpn files, pulls referenced certificates and keys inline, and");
                Console.WriteLine("reports duplicates and unsupported directives. Directories are searched");
                Console.WriteLine("recursively. Nothing is written without --commit.");
                Console.WriteLine();
                Console.WriteLine("  --commit               Store the importable profiles.");
                return 0;

            case "connect":
                Console.WriteLine("ovp connect <config file> [options]");
                Console.WriteLine("ovp connect --profile <name> [options]");
                Console.WriteLine();
                Console.WriteLine("Connects and prints each state change until the time is up, then");
                Console.WriteLine("disconnects. A stored profile is written to a private runtime file for");
                Console.WriteLine("the duration of the connection and removed afterwards.");
                Console.WriteLine();
                Console.WriteLine("  --profile <name>       Connect a stored profile matched by name.");
                Console.WriteLine("  --seconds <n>          How long to stay connected. Default 30.");
                Console.WriteLine("  --protect-routes       Ignore pushed routing and DNS changes, so the");
                Console.WriteLine("                         host keeps its own default route.");
                Console.WriteLine("  --username <name>      User name for profiles that need credentials.");
                Console.WriteLine("  --password <value>     Password for profiles that need credentials.");
                Console.WriteLine();
                Console.WriteLine("Exit codes: 0 connected, 2 launch refused, 3 failed, 5 never connected.");
                return 0;

            default:
                Console.Error.WriteLine($"No help for '{command}'. Run 'ovp help' for the command list.");
                return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'ovp help' for usage.");
        return 1;
    }
}

/// <summary>
/// Version information, kept in one place so the help text and any banner agree.
/// </summary>
internal static class VersionInfo
{
    public static string Current =>
        typeof(VersionInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
