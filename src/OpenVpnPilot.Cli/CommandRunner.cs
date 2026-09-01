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
            "start" => await StartApplicationCommand.RunAsync(args[1..]),
            "stop" => await StopApplicationCommand.RunAsync(),
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
        "start" or "open" or "up" => "start",
        "stop" or "quit" or "exit" => "stop",
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
        OpenVpnEnvironmentReport report = await EnvironmentReadiness.ProbeAsync();

        Console.WriteLine("OpenVPN environment");
        Console.WriteLine();

        EnvironmentReadiness.WriteChecks(report);

        Console.WriteLine();

        if (!report.CanConnect)
        {
            Console.WriteLine("Result: connections are not possible until the failures above are resolved.");
            Console.WriteLine();
            EnvironmentReadiness.WriteGuidance();
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
        Console.WriteLine("  start [--headless]         Start the application, with or without a window.");
        Console.WriteLine("  stop                       End the application and its tunnels.");
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
        Console.WriteLine("Exit code 6 means the profile store could not be read or written. The most");
        Console.WriteLine("common reason is the application writing to it at the same moment.");
        Console.WriteLine();
        Console.WriteLine("The application owns the tunnels and the profile store, so connect, disconnect");
        Console.WriteLine("and status are handed to it. Connecting starts it first when it is not running, so");
        Console.WriteLine("this command is all anything else has to call.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine(@"  ovp add C:\profiles --commit");
        Console.WriteLine("  ovp con site-alpha");
        Console.WriteLine("  ovp con site-alpha --headless    start with no window, then connect");
        Console.WriteLine("  ovp dis --all");
        Console.WriteLine("  ovp stop");
        Console.WriteLine();
        Console.WriteLine("Completion for the current shell:");
        Console.WriteLine("  ovp completion powershell --install");
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

            case "start":
                Console.WriteLine("ovp start [--headless]");
                Console.WriteLine("Alias: open, up");
                Console.WriteLine();
                Console.WriteLine("Starts the application. Only one copy runs per user, so starting one");
                Console.WriteLine("that is already running does nothing and reports so.");
                Console.WriteLine();
                Console.WriteLine("  --headless             No window and no notification area entry. The");
                Console.WriteLine("                         tunnels are driven by this command alone.");
                Console.WriteLine();
                Console.WriteLine("Exit codes: 0 running, 4 it could not be started.");
                return 0;

            case "stop":
                Console.WriteLine("ovp stop");
                Console.WriteLine("Alias: quit, exit");
                Console.WriteLine();
                Console.WriteLine("Ends the application. Its tunnels are stopped on the way out. Reports");
                Console.WriteLine("that nothing is running rather than failing when none is.");
                return 0;

            case "list":
                Console.WriteLine("ovp list [--json] [--names] [--tag <name>]");
                Console.WriteLine("Alias: ls");
                Console.WriteLine();
                Console.WriteLine("Lists the stored profiles with their endpoint and last use.");
                Console.WriteLine();
                Console.WriteLine("  --json                 Emit machine readable output.");
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
                Console.WriteLine("ovp import <file, directory or archive> [--commit] [--tag <name>]");
                Console.WriteLine("Alias: add");
                Console.WriteLine();
                Console.WriteLine("Examines .ovpn files, pulls referenced certificates and keys inline, and");
                Console.WriteLine("reports duplicates and unsupported directives. Directories are searched");
                Console.WriteLine("recursively and ZIP archives are unpacked. Nothing is written without");
                Console.WriteLine("--commit.");
                Console.WriteLine();
                Console.WriteLine("  --commit               Store the importable profiles.");
                Console.WriteLine("  --tag <name>           Tag them. May be given more than once.");
                return 0;

            case "export":
                Console.WriteLine("ovp export <directory> [--profile <name>] [--tag <name>]");
                Console.WriteLine();
                Console.WriteLine("Writes stored profiles back out as self contained .ovpn files, one per");
                Console.WriteLine("profile. The files carry their private keys inline, so the directory is");
                Console.WriteLine("created with permissions for the current user only.");
                Console.WriteLine();
                Console.WriteLine("  --profile <name>       Export one profile matched by name.");
                Console.WriteLine("  --tag <name>           Export everything carrying that tag.");
                return 0;

            case "connect":
                Console.WriteLine("ovp connect <name> [options]");
                Console.WriteLine("ovp connect <config file> [options]");
                Console.WriteLine("Alias: con, conn");
                Console.WriteLine();
                Console.WriteLine("The request is handed to the application, which owns the tunnels and the");
                Console.WriteLine("store. When none is running it is started first, so this works whether or");
                Console.WriteLine("not anyone had it open. Naming a configuration file instead of a stored");
                Console.WriteLine("profile connects from this command alone, printing each state change until");
                Console.WriteLine("the time is up and then disconnecting, which is for trying a file that has");
                Console.WriteLine("not been imported.");
                Console.WriteLine();
                Console.WriteLine("  --profile <name>       Same as passing the name directly.");
                Console.WriteLine("  --headless             When the application has to be started, start it");
                Console.WriteLine("                         with no window and no notification area entry.");
                Console.WriteLine("  --seconds <n>          How long to stay connected. Default 30.");
                Console.WriteLine("  --protect-routes       Ignore pushed routing and DNS changes, so the");
                Console.WriteLine("                         host keeps its own default route.");
                Console.WriteLine("  --username <name>      User name for profiles that need credentials.");
                Console.WriteLine("  --password <value>     Password for profiles that need credentials.");
                Console.WriteLine("  --challenge <value>    Answer for a one time code, of either kind.");
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
                Console.WriteLine("ovp pack <file> --passphrase <value> [--profile <name>] [--tag <name>]");
                Console.WriteLine();
                Console.WriteLine("Writes profiles, their tags and the shortcut bindings into one .ovppkg");
                Console.WriteLine("file that can be moved to another machine.");
                Console.WriteLine("The session history stays behind: it belongs to the machine it happened on.");
                Console.WriteLine();
                Console.WriteLine("A package carries private keys and is always encrypted, so the passphrase is");
                Console.WriteLine("required. The mode is authenticated, so a package that was altered fails to");
                Console.WriteLine("open rather than opening with quietly different contents.");
                Console.WriteLine();
                Console.WriteLine("  --profile <name>       Only profiles matching that name.");
                Console.WriteLine("  --tag <name>           Only profiles carrying that tag.");
                Console.WriteLine("  --passphrase <value>   Required. The key that protects the package.");
                Console.WriteLine("  --with-credentials     Also carry the saved user names and passwords,");
                Console.WriteLine("                         so the recipient does not have to enter fifty of");
                Console.WriteLine("                         them. Anyone with the file and the passphrase can");
                Console.WriteLine("                         then connect as you, so send the two separately.");
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
                Console.WriteLine("Saved sign ins the package carries are written into this machine's");
                Console.WriteLine("protected storage, under the profiles they belong to.");
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
