using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// Starts another copy of the application by running its executable again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationRestart : IApplicationRestart
{
    private readonly ILogger<WindowsApplicationRestart> logger;

    public WindowsApplicationRestart(ILogger<WindowsApplicationRestart>? logger = null)
    {
        this.logger = logger ?? NullLogger<WindowsApplicationRestart>.Instance;
    }

    public bool TryStartSuccessor(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? executable = Environment.ProcessPath;

        if (executable is not { Length: > 0 } || !File.Exists(executable))
        {
            RestartLog.ExecutableUnknown(logger);
            return false;
        }

        try
        {
            using Process? started = Process.Start(SuccessorStart.ForExecutable(executable, arguments));

            if (started is null)
            {
                RestartLog.NotStarted(logger, executable);
                return false;
            }

            return true;
        }
        catch (Win32Exception exception)
        {
            RestartLog.StartFailed(logger, executable, exception);
            return false;
        }
    }
}

/// <summary>
/// How a successor is started, apart from starting it, so it can be checked without a process.
/// </summary>
internal static class SuccessorStart
{
    /// <summary>
    /// The executable started again through the shell.
    /// </summary>
    /// <remarks>
    /// Through the shell rather than directly, so the new copy gets handles of its own instead of
    /// inheriting this process's, which is about to end. The working directory is the executable's,
    /// as it is for a start from the start menu.
    /// </remarks>
    public static ProcessStartInfo ForExecutable(string executable, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}

/// <summary>
/// Source generated log messages for <see cref="WindowsApplicationRestart"/>.
/// </summary>
internal static partial class RestartLog
{
    [LoggerMessage(
        EventId = 3940,
        Level = LogLevel.Warning,
        Message = "The application cannot restart, because the path of its executable is not known.")]
    public static partial void ExecutableUnknown(ILogger logger);

    [LoggerMessage(
        EventId = 3941,
        Level = LogLevel.Warning,
        Message = "The application could not start its successor from {Executable}.")]
    public static partial void NotStarted(ILogger logger, string executable);

    [LoggerMessage(
        EventId = 3942,
        Level = LogLevel.Warning,
        Message = "The application could not start its successor from {Executable}.")]
    public static partial void StartFailed(ILogger logger, string executable, Exception exception);
}
