using OpenVpnPilot.Core.Ipc;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// What the process was asked to do on the command line.
/// </summary>
/// <remarks>
/// The application is started by people, by the autostart entry and by other software: a session
/// manager that brings a tunnel up before opening a remote desktop is the case this exists for. All
/// three want different things from the same executable, so what to show and what to connect are
/// decided here rather than being spread through startup.
///
/// The actions are the same verbs the companion command sends, which is deliberate. Only one copy
/// runs per user, so a second launch cannot drive its own tunnels: it hands the actions to the copy
/// that already runs and exits. Whether the application was running makes no difference to the
/// caller, which is what a launcher needs.
/// </remarks>
public sealed record StartupOptions
{
    /// <summary>
    /// No window and no notification area icon. The tunnels are driven by whatever launched it.
    /// </summary>
    public bool Headless { get; init; }

    /// <summary>
    /// A window that starts hidden, with the notification area icon still there to get it back.
    /// </summary>
    public bool Background { get; init; }

    /// <summary>
    /// Profiles to connect once the store has been read.
    /// </summary>
    public IReadOnlyList<string> Connect { get; init; } = [];

    /// <summary>
    /// Profiles to disconnect.
    /// </summary>
    public IReadOnlyList<string> Disconnect { get; init; } = [];

    public bool DisconnectAll { get; init; }

    /// <summary>
    /// Ends the running copy. Only meaningful when one is already running.
    /// </summary>
    public bool Quit { get; init; }

    /// <summary>
    /// Configurations, archives or packages named on the command line, to be imported.
    /// </summary>
    /// <remarks>
    /// This is what a double click on a configuration comes through as. The shell passes the file
    /// as a bare argument, so an argument that is not an option is read as one: rejecting it, which
    /// is what used to happen, means the application opens and immediately exits reporting an
    /// unknown option that the user never typed.
    /// </remarks>
    public IReadOnlyList<string> FilesToImport { get; init; } = [];

    /// <summary>
    /// Set when the arguments could not be understood, which is reported rather than ignored.
    /// </summary>
    public string? Error { get; init; }

    public bool ShowHelp { get; init; }

    /// <summary>
    /// True when the process was asked to do something rather than only to appear.
    /// </summary>
    public bool HasActions =>
        Connect.Count > 0 || Disconnect.Count > 0 || DisconnectAll || Quit || FilesToImport.Count > 0;

    /// <summary>
    /// True when starting with no window of any kind, which is what a launcher wants.
    /// </summary>
    public bool StartsHidden => Headless || Background;

    public static StartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool headless = false;
        bool background = false;
        bool disconnectAll = false;
        bool quit = false;
        bool help = false;
        List<string> connect = [];
        List<string> disconnect = [];
        List<string> files = [];

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            switch (argument)
            {
                case "--headless":
                    headless = true;
                    break;

                case "--background" or "--minimised" or "--minimized":
                    background = true;
                    break;

                case "--connect":
                    if (!TryTakeValue(args, ref index, out string? profile))
                    {
                        return new StartupOptions { Error = "--connect needs a profile name." };
                    }

                    connect.Add(profile);
                    break;

                case "--disconnect":
                    if (!TryTakeValue(args, ref index, out string? stopping))
                    {
                        return new StartupOptions { Error = "--disconnect needs a profile name." };
                    }

                    disconnect.Add(stopping);
                    break;

                case "--disconnect-all":
                    disconnectAll = true;
                    break;

                case "--quit":
                    quit = true;
                    break;

                case "--help" or "-h" or "-?" or "/?":
                    help = true;
                    break;

                default:
                    // Anything that is not an option is a file to import. An empty argument is not
                    // a path, and neither is something that begins with a dash: that is a mistyped
                    // option, and reading it as a file name would hide the mistake.
                    if (argument.Length == 0 || argument.StartsWith('-'))
                    {
                        return new StartupOptions { Error = $"Unknown option '{argument}'." };
                    }

                    files.Add(argument);
                    break;
            }
        }

        return new StartupOptions
        {
            Headless = headless,
            Background = background,
            Connect = connect,
            Disconnect = disconnect,
            DisconnectAll = disconnectAll,
            Quit = quit,
            ShowHelp = help,
            FilesToImport = files,
        };
    }

    /// <summary>
    /// The commands these options mean, in the order they have to be sent.
    /// </summary>
    /// <remarks>
    /// Disconnecting comes first so that "--disconnect-all --connect x" reads as switching to x
    /// rather than as a race between the two.
    /// </remarks>
    public IEnumerable<string> ToCommands()
    {
        if (DisconnectAll)
        {
            yield return PilotCommands.Disconnect + PilotCommands.AllMarker;
        }

        foreach (string profile in Disconnect)
        {
            yield return PilotCommands.Disconnect + profile;
        }

        foreach (string profile in Connect)
        {
            yield return PilotCommands.Connect + profile;
        }

        foreach (string path in FilesToImport)
        {
            yield return PilotCommands.Import + path;
        }

        if (Quit)
        {
            yield return PilotCommands.Quit;
        }
    }

    public static string Usage =>
        """
        OpenVPN Pilot - a desktop client for OpenVPN

        Usage:
          OpenVpnPilot.exe [options] [file...]

        A file named without an option is imported, which is what a double click on a .ovpn, a ZIP
        archive or a .ovppkg package arrives as.

        Options:
          --headless             Start with no window and no notification area icon. The tunnels
                                 are driven by whatever launched it, or by the ovp command.
          --background           Start with the window hidden, reachable from the notification area.
          --connect <profile>    Connect a profile. May be given more than once.
          --disconnect <profile> Disconnect a profile. May be given more than once.
          --disconnect-all       Disconnect everything.
          --quit                 End the copy that is running.
          -h, --help             Show this text.

        Only one copy runs per user. A second launch hands its options to the copy that already
        runs and exits, so a launcher can use the same command line either way.

        Exit codes: 0 done, 1 the options were not understood, 4 nothing was listening.
        """;

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }
}
