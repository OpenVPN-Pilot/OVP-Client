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
            WriteUsage();
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This build supports Windows only.");
            return 1;
        }

        return args[0] switch
        {
            "doctor" => await RunDoctorAsync(),
            "connect" => await ConnectCommand.RunAsync(args[1..]),
            "import" => await ImportCommand.RunAsync(args[1..]),
            "--help" or "-h" or "help" => WriteUsage(),
            _ => Unknown(args[0]),
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
            return 2;
        }

        Console.WriteLine(report.HasWarnings
            ? "Result: connections are possible, with the limitations noted above."
            : "Result: ready.");

        return 0;
    }

    private static int WriteUsage()
    {
        Console.WriteLine("Usage: ovp <command>");
        Console.WriteLine();
        Console.WriteLine("  doctor                     Check whether OpenVPN is installed and usable.");
        Console.WriteLine("  connect <config> [options] Connect using a configuration file.");
        Console.WriteLine("  import <path> [--commit]   Examine .ovpn files and optionally store them.");
        Console.WriteLine();
        Console.WriteLine("Connect options:");
        Console.WriteLine("  --seconds <n>              How long to stay connected. Default 30.");
        Console.WriteLine("  --protect-routes           Ignore pushed routing and DNS changes.");
        Console.WriteLine("  --username <name>          Username for profiles that require credentials.");
        Console.WriteLine("  --password <value>         Password for profiles that require credentials.");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'ovp help' for usage.");
        return 1;
    }
}
