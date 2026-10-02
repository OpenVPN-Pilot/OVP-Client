using OpenVpnPilot.Core.Server;

namespace OpenVpnPilot.Core.Storage;

/// <summary>
/// The store this process works on, decided once before anything is composed.
/// </summary>
/// <remarks>
/// Every store, view model and command reads and writes one database, and which one is not theirs to
/// decide: in Local mode it is the library on this machine, in Server mode a copy of one server's
/// profiles that the synchronisation keeps in step. Asking this rather than building a path keeps
/// the two from ever being mixed up, and keeps the other mode's data where it is: switching opens
/// another file and deletes nothing.
///
/// It does not change while the process runs. Switching the mode is a restart.
/// </remarks>
public interface IActiveStorage
{
    /// <summary>
    /// The mode in effect for this process. Local when the settings asked for a server whose address
    /// is not usable, see <see cref="Problem"/>.
    /// </summary>
    public StorageMode Mode { get; }

    /// <summary>
    /// The database every store opens.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// The server's address in its normal form. Null in Local mode.
    /// </summary>
    public string? ServerAddress { get; }

    /// <summary>
    /// The key the server's copy is filed under. Null in Local mode.
    /// </summary>
    public string? ServerKey { get; }

    /// <summary>
    /// The folder holding everything kept for this server, <c>servers/&lt;key&gt;</c> under the data
    /// directory. Null in Local mode. Removing it removes this server's copy and nothing else.
    /// </summary>
    public string? ServerDirectory { get; }

    /// <summary>
    /// Why the settings asked for a server and this process works locally instead. None otherwise.
    /// </summary>
    public ServerAddressProblem Problem { get; }
}

/// <summary>
/// Resolves the store from what the settings file asks for.
/// </summary>
public sealed class ActiveStorage : IActiveStorage
{
    /// <summary>
    /// The name of the database file, the same in both modes, so the copy of a server is an ordinary
    /// library with the same schema.
    /// </summary>
    public const string DatabaseFileName = "pilot.db";

    private ActiveStorage(
        StorageMode mode,
        string databasePath,
        string? serverAddress,
        string? serverKey,
        string? serverDirectory,
        ServerAddressProblem problem)
    {
        Mode = mode;
        DatabasePath = databasePath;
        ServerAddress = serverAddress;
        ServerKey = serverKey;
        ServerDirectory = serverDirectory;
        Problem = problem;
    }

    public StorageMode Mode { get; }

    public string DatabasePath { get; }

    public string? ServerAddress { get; }

    public string? ServerKey { get; }

    public string? ServerDirectory { get; }

    public ServerAddressProblem Problem { get; }

    /// <summary>
    /// The store a selection means, with the folder of a server's copy created when it is missing.
    /// </summary>
    /// <remarks>
    /// A server mode whose address is not usable can only have come from a file edited by hand,
    /// because switching checks the address before storing it. Refusing to start would leave the
    /// person without the application that could put it right, so the local library is opened, and
    /// <see cref="IActiveStorage.Problem"/> says why so the reason is logged rather than guessed.
    /// </remarks>
    public static ActiveStorage Resolve(IApplicationPaths paths, StorageSelection selection)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Mode != StorageMode.Server)
        {
            return Local(paths, ServerAddressProblem.None);
        }

        if (!Server.ServerKey.TryNormalise(selection.ServerUrl, out string? address, out ServerAddressProblem problem))
        {
            return Local(paths, problem);
        }

        string key = Server.ServerKey.Compute(address);
        string directory = Path.Combine(paths.ServersDirectory, key);

        // SQLite creates the file and not the folders above it.
        Directory.CreateDirectory(directory);

        return new ActiveStorage(
            StorageMode.Server,
            Path.Combine(directory, DatabaseFileName),
            address,
            key,
            directory,
            ServerAddressProblem.None);
    }

    private static ActiveStorage Local(IApplicationPaths paths, ServerAddressProblem problem) =>
        new(StorageMode.Local, paths.LocalDatabasePath, null, null, null, problem);
}
