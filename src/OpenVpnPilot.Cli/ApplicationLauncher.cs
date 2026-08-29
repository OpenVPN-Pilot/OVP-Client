using System.Diagnostics;
using OpenVpnPilot.Core.Ipc;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Finds the application and starts it, so that `ovp` is the only thing anyone has to call.
/// </summary>
/// <remarks>
/// The tunnels belong to the application: it owns the store, the window and the connections, and two
/// processes driving the same set would each believe they were the only one. That makes `ovp` a front
/// door rather than a second client, and a front door has to be able to open the house.
///
/// Whether a window appears is the caller's choice. Something bringing a tunnel up before a remote
/// session wants nothing on screen; a person typing `ovp con` usually does.
/// </remarks>
internal static class ApplicationLauncher
{
    private const string ExecutableName = "OpenVpnPilot.exe";

    /// <summary>
    /// How long the application is given to claim the instance and start listening.
    /// </summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The application executable, or null when it cannot be found.
    /// </summary>
    /// <remarks>
    /// Beside this command first, which is where the installer puts both. The installation directory
    /// is checked as well, so a command installed as a .NET tool still finds an installed
    /// application, and finally the path, which is what a development tree relies on.
    /// </remarks>
    public static string? Locate()
    {
        foreach (string candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, ExecutableName);

        foreach (Environment.SpecialFolder folder in new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        })
        {
            string root = Environment.GetFolderPath(folder);

            if (root.Length > 0)
            {
                yield return Path.Combine(root, "OpenVpnPilot", ExecutableName);
            }
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory, ExecutableName);
        }
    }

    /// <summary>
    /// Starts the application and waits until it is ready to answer commands.
    /// </summary>
    /// <param name="headless">Start with no window and no notification area entry.</param>
    /// <returns>True when it is running and listening.</returns>
    public static async Task<bool> StartAsync(bool headless, CancellationToken cancellationToken = default)
    {
        if (PilotCommandClient.IsApplicationRunning())
        {
            return true;
        }

        string? executable = Locate();

        if (executable is null)
        {
            Console.Error.WriteLine(
                $"{ExecutableName} could not be found beside this command, in Program Files or on PATH.");
            Console.Error.WriteLine("Install OpenVpnPilot, or run the command from the directory it lives in.");
            return false;
        }

        // Started through the shell, so the application gets its own handles rather than inheriting
        // this command's. Inheriting them keeps the terminal's output pipe open for as long as the
        // application lives, and whatever called ovp waits for a command that has already finished.
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };

        if (headless)
        {
            start.ArgumentList.Add("--headless");
        }

        using Process? process = Process.Start(start);

        if (process is null)
        {
            Console.Error.WriteLine($"{executable} could not be started.");
            return false;
        }

        Console.WriteLine(headless
            ? $"Started {Path.GetFileName(executable)} with no window."
            : $"Started {Path.GetFileName(executable)}.");

        return await WaitUntilListeningAsync(process, cancellationToken);
    }

    /// <summary>
    /// Waits for the application to claim the instance, which is what makes it answer commands.
    /// </summary>
    /// <remarks>
    /// The process existing is not enough: the store has to be migrated and the profile list read
    /// before a connection can be asked for by name. Sending a command any earlier would be answered
    /// with a profile that is not there yet.
    /// </remarks>
    private static async Task<bool> WaitUntilListeningAsync(Process process, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                Console.Error.WriteLine(
                    $"The application exited during startup with code {process.ExitCode}. "
                    + "Look in the logs directory under the application data for the reason.");
                return false;
            }

            string? answer = await PilotCommandClient.SendAsync(
                PilotCommands.Ping,
                cancellationToken: cancellationToken);

            // Listening is not the same as ready: a copy that has claimed the instance still has to
            // read the profile list before a command naming one means anything.
            if (string.Equals(answer, PilotCommands.Ready, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Console.Error.WriteLine("The application did not start answering in time.");
        return false;
    }
}
