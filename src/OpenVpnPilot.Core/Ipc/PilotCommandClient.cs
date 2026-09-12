using System.IO.Pipes;
using System.Text;

namespace OpenVpnPilot.Core.Ipc;

/// <summary>
/// Sends a command to the running application and returns its reply.
/// </summary>
/// <remarks>
/// The companion command and the application share one profile store and one set of tunnels. When
/// the application is running it owns both, so a request typed in a terminal is handed to it rather
/// than acted on separately. Two processes driving the same tunnels would each believe they were the
/// only one, and a disconnect from one would be invisible to the other.
///
/// The channel is a named pipe scoped to the current user, so sessions on a shared machine stay
/// independent. A caller that gets null back knows nothing is listening and may act on its own.
/// </remarks>
public static class PilotCommandClient
{
    /// <summary>
    /// How long to wait for the running application to accept the connection.
    /// </summary>
    private const int ConnectTimeoutMilliseconds = 2000;

    public static string PipeNameFor(string? identity = null) =>
        $"OpenVpnPilot.Activate.{identity ?? Environment.UserName}";

    /// <summary>
    /// True when a copy of the application holds the single instance claim for this user.
    /// </summary>
    public static bool IsApplicationRunning(string? identity = null)
    {
        using Mutex probe = ApplicationInstance.CreateClaim(initiallyOwned: false, identity, out bool created);

        return !created;
    }

    /// <summary>
    /// Sends one command and waits for the reply.
    /// </summary>
    /// <returns>The reply, or null when no application is listening.</returns>
    public static async Task<string?> SendAsync(
        string command,
        string? identity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        try
        {
            await using NamedPipeClientStream pipe = new(
                ".",
                PipeNameFor(identity),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken);

            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command), cancellationToken);
            await pipe.FlushAsync(cancellationToken);

            byte[] buffer = new byte[16384];
            int read = await pipe.ReadAsync(buffer, cancellationToken);

            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The commands the application answers over its channel.
/// </summary>
/// <remarks>
/// Deliberately plain text. The channel carries a handful of verbs between two copies of the same
/// application, and a text protocol can be read in a log and typed by hand while diagnosing it.
/// </remarks>
public static class PilotCommands
{
    /// <summary>
    /// Brings the window forward. Sent by a second copy that is about to exit.
    /// </summary>
    public const string Activate = "activate";

    /// <summary>
    /// Followed by a profile name or part of one.
    /// </summary>
    public const string Connect = "connect ";

    /// <summary>
    /// Followed by a profile name, or by the all marker.
    /// </summary>
    public const string Disconnect = "disconnect ";

    /// <summary>
    /// Argument to <see cref="Disconnect"/> that stops every tunnel.
    /// </summary>
    public const string AllMarker = "--all";

    /// <summary>
    /// Followed by the path of a configuration, an archive or a package to import.
    /// </summary>
    /// <remarks>
    /// One path per command rather than a list. A path may contain anything a file name may contain,
    /// including whatever separator a list would need, and a command that cannot be parsed back
    /// unambiguously is a command that will eventually import the wrong file.
    /// </remarks>
    public const string Import = "import ";

    /// <summary>
    /// Reports what is connected, one line per tunnel.
    /// </summary>
    public const string Status = "status";

    /// <summary>
    /// Asks whether the application is ready to act on a command.
    /// </summary>
    /// <remarks>
    /// A process that has claimed the instance is not yet one that can connect a profile by name:
    /// the store has to be migrated and the profile list read first. Anything that starts the
    /// application and then tells it what to do waits for <see cref="Ready"/> rather than for the
    /// process to exist.
    /// </remarks>
    public const string Ping = "ping";

    /// <summary>
    /// The answer to <see cref="Ping"/> once the profile list has been read.
    /// </summary>
    public const string Ready = "ready";

    /// <summary>
    /// Ends the running copy, stopping its tunnels on the way out.
    /// </summary>
    /// <remarks>
    /// A copy started with no window has no menu to quit from, so ending it has to be something
    /// another process can ask for.
    /// </remarks>
    public const string Quit = "quit";

    /// <summary>
    /// The answer to <see cref="Status"/> when no tunnel is up.
    /// </summary>
    /// <remarks>
    /// Named here because both sides depend on the exact words: the application writes it and the
    /// companion command reads it to decide an exit code. Everything that crosses this channel is
    /// English whatever language the window is in, for the same reason. This used to be two string
    /// literals in two projects, which is a thing that stays in agreement right up until it does not.
    /// </remarks>
    public const string NothingConnected = "Nothing is connected.";

    /// <summary>
    /// How the answer to <see cref="Connect"/> or <see cref="Disconnect"/> begins when the name
    /// matched no profile.
    /// </summary>
    public const string NoSuchProfile = "No stored profile matches";
}
