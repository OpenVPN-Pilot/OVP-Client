using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Keeps the command from changing a server's copy behind the application's back.
/// </summary>
/// <remarks>
/// In Server mode the store is a copy the application keeps in step with the server, and it records
/// every change it makes there so the change can be sent. A change written here would be recorded
/// nowhere: it would never reach the server and would be overwritten by the next synchronisation,
/// which is worse than refusing. Reading works the same in both modes, because reading the copy is
/// what it is for, and the command does not talk to the server itself.
/// </remarks>
internal static class ServerModeGuard
{
    /// <summary>
    /// Reported when a command that writes the store was refused because the store is a server's.
    /// </summary>
    public const int RefusedInServerMode = 7;

    /// <summary>
    /// True when the command, as given, would write the store and the store is a server's copy.
    /// </summary>
    /// <param name="command">The canonical command name, after aliases are resolved.</param>
    /// <param name="arguments">What followed the command.</param>
    /// <param name="mode">The mode the store was resolved in.</param>
    public static bool Refuses(string command, IReadOnlyList<string> arguments, StorageMode mode)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return mode == StorageMode.Server && Writes(command, arguments);
    }

    /// <summary>
    /// Explains the refusal on the error stream.
    /// </summary>
    public static int Refuse(string command, IActiveStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        Console.Error.WriteLine(
            $"'ovp {command}' changes the profile store, and the profiles on this machine are the copy "
            + $"of {storage.ServerAddress}. The command line only reads that copy.");
        Console.Error.WriteLine(
            "Make the change in the application, which sends it to the server, or switch this machine "
            + "to its own profiles in the application's settings.");

        return RefusedInServerMode;
    }

    /// <summary>
    /// Whether a command writes the store. A dry run reads, so it is answered in both modes.
    /// </summary>
    private static bool Writes(string command, IReadOnlyList<string> arguments) => command switch
    {
        "favourite" or "remove" => true,
        "import" or "unpack" => arguments.Contains("--commit", StringComparer.Ordinal),
        _ => false,
    };
}
