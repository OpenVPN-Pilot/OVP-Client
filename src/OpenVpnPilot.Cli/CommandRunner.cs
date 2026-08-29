using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.Windows.Diagnostics;
using OpenVpnPilot.Platform.Windows.InteractiveService;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Entry point for the headless companion command.
/// </summary>
/// <remarks>
/// Short aliases exist only where they read well for something typed all day. A name that has to be
/// looked up is worse than a longer one that can be guessed.
/// </remarks>
internal static class CommandRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return WriteUsage();
        }

        string command = Canonical(args[0]);

        if (command is "help")
        {
            return args.Length > 1 ? WriteCommandHelp(Canonical(args[1])) : WriteUsage();
        }

        if (command is "version")
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
            "disconnect" => await DisconnectCommand.RunAsync(args[1..]),
            "status" => await StatusCommand.RunAsync(),
            "import" => await ImportCommand.RunAsync(args[1..]),
            "list" => await ListCommand.RunAsync(args[1..]),
            "export" => await ExportCommand.RunAsync(args[1..]),
            "pack" => await PackCommand.RunAsync(args[1..]),
            "unpack" => await UnpackCommand.RunAsync(args[1..]),
            "favourite" => await FavouriteCommand.RunAsync(args[1..]),
            "remove" => await RemoveCommand.RunAsync(args[1..]),
            "completion" => CompletionCommand.Run(args[1..]),
            _ => Unknown(args[0]),
        };
    }

    /// <summary>
    /// Resolves an alias to the command it stands for.
    /// </summary>
    private static string Canonical(string command) => command switch
    {
        "--help" or "-h" or "help" or "-?" => "help",
        "--version" or "-v" or "version" => "version",
        "dr" or "doctor" => "doctor",
        "ls" or "list" => "list",
        "con" or "conn" or "connect" => "connect",
        "dis" or "disconnect" => "disconnect",
        "st" or "status" => "status",
        "add" or "import" => "import",
        "fav" or "favourite" or "favorite" => "favourite",
        "rm" or "remove" or "delete" => "remove",
        "export" => "export",
        "pack" => "pack",
        "unpack" => "unpack",
        "completion" => "completion",
        _ => command,
    };

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
        Console.WriteLine("  doctor, dr                 Check whether OpenVPN is installed and usable.");
        Console.WriteLine("  list, ls                   List the profiles in the local store.");
        Console.WriteLine("  status, st                 Show what is currently connected.");
        Console.WriteLine("  connect, con <name>        Connect a profile.");
        Console.WriteLine("  disconnect, dis <name>     Disconnect a profile, or --all.");
        Console.WriteLine("  import, add <path>         Examine .ovpn files and optionally store them.");
        Console.WriteLine("  export <path>              Write profiles back out as .ovpn files.");
        Console.WriteLine("  pack <file>                Write a portable .ovppkg package.");
        Console.WriteLine("  unpack <file>              Read a package back into the store.");
        Console.WriteLine("  favourite, fav <name>      Set or clear a favourite and its slot.");
        Console.WriteLine("  remove, rm <name>          Delete a profile from the store.");
        Console.WriteLine("  completion <shell>         Print a shell completion script.");
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine("  -h, --help [command]       Show this text, or the help for one command.");
        Console.WriteLine("  -v, --version              Print the version.");
        Console.WriteLine();
        Console.WriteLine("When the application is running, connect, disconnect and status are handed to");
        Console.WriteLine("it so they act on the tunnels its window shows.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine(@"  ovp add C:\profiles --commit");
        Console.WriteLine("  ovp con site-alpha");
        Console.WriteLine("  ovp dis --all");
        return 0;
    }

    private static int WriteCommandHelp(string command)
    {
        switch (command)
        {
            case "doctor":
                Console.WriteLine("ovp doctor");
                Console.WriteLine("Alias: dr");
                Console.WriteLine();
                Console.WriteLine("Reports each requirement separately: the OpenVPN installation, the");
                Console.WriteLine("executable and its version, the interactive service, its control pipe,");
                Console.WriteLine("and whether this account may launch configurations from outside the");
                Console.WriteLine("OpenVPN configuration directory.");
                Console.WriteLine();
                Console.WriteLine("Exit codes: 0 ready, 2 something blocks connecting.");
                return 0;

            case "list":
                Console.WriteLine("ovp list [--json] [--folder <name>] [--tag <name>]");
                Console.WriteLine("Alias: ls");
                Console.WriteLine();
                Console.WriteLine("Lists the stored profiles with their endpoint and last use.");
                Console.WriteLine();
                Console.WriteLine("  --json                 Emit machine readable output.");
                Console.WriteLine("  --folder <name>        Only profiles filed under that folder.");
                Console.WriteLine("  --tag <name>           Only profiles carrying that tag.");
                Console.WriteLine("  --names                Print names only, one per line.");
                return 0;

            case "status":
                Console.WriteLine("ovp status");
                Console.WriteLine("Alias: st");
                Console.WriteLine();
                Console.WriteLine("Shows what the running application currently has connected. Reports that");
                Console.WriteLine("nothing is running when the application is not started, because a tunnel");
                Console.WriteLine("belongs to the process that created it.");
                Console.WriteLine();
                Console.WriteLine("Exit codes: 0 something is connected, 1 nothing is, 4 no application.");
                return 0;

            case "import":
                Console.WriteLine("ovp import <file, directory or archive> [--commit] [--folder <name>] [--tag <name>]");
                Console.WriteLine("Alias: add");
                Console.WriteLine();
                Console.WriteLine("Examines .ovpn files, pulls referenced certificates and keys inline, and");
                Console.WriteLine("reports duplicates and unsupported directives. Directories are searched");
                Console.WriteLine("recursively and ZIP archives are unpacked. Nothing is written without");
                Console.WriteLine("--commit.");
                Console.WriteLine();
                Console.WriteLine("  --commit               Store the importable profiles.");
                Console.WriteLine("  --folder <name>        File them under that folder, creating it if needed.");
                Console.WriteLine("  --tag <name>           Tag them. May be given more than once.");
                return 0;

            case "export":
                Console.WriteLine("ovp export <directory> [--profile <name>] [--folder <name>]");
                Console.WriteLine();
                Console.WriteLine("Writes stored profiles back out as self contained .ovpn files, one per");
                Console.WriteLine("profile. The files carry their private keys inline, so the directory is");
                Console.WriteLine("created with permissions for the current user only.");
                Console.WriteLine();
                Console.WriteLine("  --profile <name>       Export one profile matched by name.");
                Console.WriteLine("  --folder <name>        Export everything filed under that folder.");
                return 0;

            case "connect":
                Console.WriteLine("ovp connect <name> [options]");
                Console.WriteLine("ovp connect <config file> [options]");
                Console.WriteLine("Alias: con, conn");
                Console.WriteLine();
                Console.WriteLine("With the application running, the request is handed to it and the tunnel");
                Console.WriteLine("appears in its window. Otherwise the connection is made by this command,");
                Console.WriteLine("which prints each state change until the time is up and then disconnects.");
                Console.WriteLine();
                Console.WriteLine("  --profile <name>       Same as passing the name directly.");
                Console.WriteLine("  --seconds <n>          How long to stay connected. Default 30.");
                Console.WriteLine("  --protect-routes       Ignore pushed routing and DNS changes, so the");
                Console.WriteLine("                         host keeps its own default route.");
                Console.WriteLine("  --username <name>      User name for profiles that need credentials.");
                Console.WriteLine("  --password <value>     Password for profiles that need credentials.");
                Console.WriteLine("  --detached             Do not hand the request to the running application.");
                Console.WriteLine();
                Console.WriteLine("Exit codes: 0 connected, 2 launch refused, 3 failed, 5 never connected.");
                return 0;

            case "disconnect":
                Console.WriteLine("ovp disconnect <name>");
                Console.WriteLine("ovp disconnect --all");
                Console.WriteLine("Alias: dis");
                Console.WriteLine();
                Console.WriteLine("Stops a tunnel the running application owns. A tunnel started by this");
                Console.WriteLine("command in the same terminal ends when that command does, so there is");
                Console.WriteLine("nothing here to stop.");
                Console.WriteLine();
                Console.WriteLine("Exit codes: 0 stopped, 1 no match, 4 no application is running.");
                return 0;

            case "pack":
                Console.WriteLine("ovp pack <file> [--profile <name>] [--folder <name>] [--passphrase <value>]");
                Console.WriteLine();
                Console.WriteLine("Writes profiles, the folders they are filed under and the shortcut");
                Console.WriteLine("bindings into one .ovppkg file that can be moved to another machine.");
                Console.WriteLine("The session history stays behind: it belongs to the machine it happened on.");
                Console.WriteLine();
                Console.WriteLine("A package carries private keys. Without a passphrase it is written in the");
                Console.WriteLine("clear and the command says so. With one it is encrypted and authenticated,");
                Console.WriteLine("so a package that was altered fails to open rather than opening changed.");
                Console.WriteLine();
                Console.WriteLine("  --profile <name>       Only profiles matching that name.");
                Console.WriteLine("  --folder <name>        Only profiles filed under that folder.");
                Console.WriteLine("  --passphrase <value>   Encrypt the package.");
                return 0;

            case "unpack":
                Console.WriteLine("ovp unpack <file> [--commit] [--passphrase <value>]");
                Console.WriteLine();
                Console.WriteLine("Reads a package. Nothing is written without --commit.");
                Console.WriteLine();
                Console.WriteLine("A configuration the store already holds is recognised by its contents and");
                Console.WriteLine("skipped, and a name that is taken gets a suffix, so importing the same");
                Console.WriteLine("package twice changes nothing the second time and never replaces anything.");
                Console.WriteLine();
                Console.WriteLine("  --commit               Write the package into the store.");
                Console.WriteLine("  --passphrase <value>   Open a protected package.");
                Console.WriteLine();
                Console.WriteLine("Exit codes: 0 read, 1 no such package, 3 it could not be opened.");
                return 0;

            case "favourite":
                Console.WriteLine("ovp favourite <name> [--slot <1-9>] [--clear]");
                Console.WriteLine("Alias: fav");
                Console.WriteLine();
                Console.WriteLine("Marks a profile as a favourite, optionally in a numbered slot bound to the");
                Console.WriteLine("matching connect shortcut. A slot is unique, so assigning one that is taken");
                Console.WriteLine("moves it.");
                Console.WriteLine();
                Console.WriteLine("  --slot <1-9>           Put the profile in that slot.");
                Console.WriteLine("  --clear                Remove the favourite mark and any slot.");
                return 0;

            case "remove":
                Console.WriteLine("ovp remove <name> [--yes]");
                Console.WriteLine("Alias: rm");
                Console.WriteLine();
                Console.WriteLine("Deletes a profile and its history from the store. Asks for confirmation");
                Console.WriteLine("unless --yes is given.");
                return 0;

            case "completion":
                Console.WriteLine("ovp completion <powershell|bash>");
                Console.WriteLine();
                Console.WriteLine("Prints a completion script that offers the commands and the stored profile");
                Console.WriteLine("names. Add it to the shell profile to make it permanent.");
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
