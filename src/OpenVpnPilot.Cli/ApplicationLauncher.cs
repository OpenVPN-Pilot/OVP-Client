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
    /// <summary>
    /// What the application is called where it is installed, newest name first.
    /// </summary>
    /// <remarks>
    /// On macOS it is a bundle, which is a directory, and it is started through the bundle rather
    /// than by the executable inside it: that is what registers the application with the window
    /// server, gives it its name and icon, and lets the Dock and the Finder see it as one thing.
    ///
    /// Two names there, because the bundle used to carry the compact one. The Finder labels an
    /// application with its file name and with nothing else, so a bundle that is to read as
    /// "OpenVPN Pilot" has to be called that; an installation made before that is still found.
    /// </remarks>
    private static IReadOnlyList<string> InstalledNames => OperatingSystem.IsMacOS()
        ? ["OpenVPN Pilot.app", "OpenVpnPilot.app"]
        : ["OpenVpnPilot.exe"];

    /// <summary>
    /// The executable itself, which is what a development tree has and what sits inside a bundle.
    /// </summary>
    private static string ExecutableName => OperatingSystem.IsWindows() ? "OpenVpnPilot.exe" : "OpenVpnPilot";

    /// <summary>
    /// How long the application is given to claim the instance and start listening.
    /// </summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The application, or null when it cannot be found.
    /// </summary>
    /// <remarks>
    /// Beside this command first, which is where the installer puts both. The installation directory
    /// is checked as well, so a command installed as a .NET tool still finds an installed
    /// application, and finally the path, which is what a development tree relies on.
    /// </remarks>
    public static ApplicationTarget? Locate()
    {
        foreach (ApplicationTarget candidate in Candidates())
        {
            if (candidate.IsBundle ? Directory.Exists(candidate.Path) : File.Exists(candidate.Path))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<ApplicationTarget> Candidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Inside the bundle this command was installed into, when it was: ovp sits beside the
            // application's own executable, and the bundle is two directories above them.
            string beside = Path.Combine(AppContext.BaseDirectory, ExecutableName);

            if (AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).EndsWith("Contents/MacOS", StringComparison.Ordinal))
            {
                yield return new ApplicationTarget(
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")),
                    IsBundle: true);
            }

            foreach (string name in InstalledNames)
            {
                yield return new ApplicationTarget($"/Applications/{name}", IsBundle: true);
            }

            foreach (string name in InstalledNames)
            {
                yield return new ApplicationTarget(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", name),
                    IsBundle: true);
            }

            // A development tree, where the executable is built on its own without a bundle.
            yield return new ApplicationTarget(beside, IsBundle: false);
        }
        else
        {
            yield return new ApplicationTarget(Path.Combine(AppContext.BaseDirectory, ExecutableName), IsBundle: false);

            foreach (Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
            })
            {
                string root = Environment.GetFolderPath(folder);

                if (root.Length > 0)
                {
                    yield return new ApplicationTarget(Path.Combine(root, "OpenVpnPilot", ExecutableName), IsBundle: false);
                }
            }
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return new ApplicationTarget(Path.Combine(directory, ExecutableName), IsBundle: false);
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

        ApplicationTarget? target = Locate();

        if (target is null)
        {
            Console.Error.WriteLine(
                $"{InstalledNames[0]} could not be found beside this command, where applications are "
                + "installed, or on PATH.");
            Console.Error.WriteLine("Install OpenVpnPilot, or run the command from the directory it lives in.");
            return false;
        }

        ProcessStartInfo start = Describe(target, headless);

        using Process? process = Process.Start(start);

        if (process is null)
        {
            Console.Error.WriteLine($"{target.Path} could not be started.");
            return false;
        }

        Console.WriteLine(headless
            ? $"Started {Path.GetFileName(target.Path)} with no window."
            : $"Started {Path.GetFileName(target.Path)}.");

        // A bundle is opened by another program, which has already ended. Its exit says nothing
        // about the application, so there is nothing to watch and the wait only listens.
        return await WaitUntilListeningAsync(target.IsBundle ? null : process, cancellationToken);
    }

    /// <summary>
    /// How the application is started on this platform.
    /// </summary>
    private static ProcessStartInfo Describe(ApplicationTarget target, bool headless)
    {
        if (target.IsBundle)
        {
            // open registers the bundle with the window server and hands it the arguments after
            // --args. Opened in the background when no window is wanted, so the terminal keeps focus.
            ProcessStartInfo bundle = new("/usr/bin/open");

            if (headless)
            {
                bundle.ArgumentList.Add("-g");
            }

            bundle.ArgumentList.Add("-a");
            bundle.ArgumentList.Add(target.Path);

            if (headless)
            {
                bundle.ArgumentList.Add("--args");
                bundle.ArgumentList.Add("--headless");
            }

            return bundle;
        }

        // Started through the shell, so the application gets its own handles rather than inheriting
        // this command's. Inheriting them keeps the terminal's output pipe open for as long as the
        // application lives, and whatever called ovp waits for a command that has already finished.
        ProcessStartInfo executable = new(target.Path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(target.Path) ?? AppContext.BaseDirectory,
        };

        if (headless)
        {
            executable.ArgumentList.Add("--headless");
        }

        return executable;
    }

    /// <summary>
    /// Waits for the application to claim the instance, which is what makes it answer commands.
    /// </summary>
    /// <remarks>
    /// The process existing is not enough: the store has to be migrated and the profile list read
    /// before a connection can be asked for by name. Sending a command any earlier would be answered
    /// with a profile that is not there yet.
    /// </remarks>
    private static async Task<bool> WaitUntilListeningAsync(Process? process, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process is { HasExited: true })
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

/// <summary>
/// Where the application was found, and whether that is a bundle or an executable.
/// </summary>
internal sealed record ApplicationTarget(string Path, bool IsBundle);
