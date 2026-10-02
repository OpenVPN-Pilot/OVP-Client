using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// Starts another copy of the application, through Launch Services when it runs from a bundle.
/// </summary>
/// <remarks>
/// A bundle is opened rather than its executable started, for the reason the command line opens it
/// that way: that is what registers the application with the window server, gives it its name and
/// icon, and keeps the Dock and the Finder seeing it as one thing.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacApplicationRestart : IApplicationRestart
{
    private readonly ILogger<MacApplicationRestart> logger;

    public MacApplicationRestart(ILogger<MacApplicationRestart>? logger = null)
    {
        this.logger = logger ?? NullLogger<MacApplicationRestart>.Instance;
    }

    public bool TryStartSuccessor(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? executable = Environment.ProcessPath;

        if (executable is not { Length: > 0 } || !File.Exists(executable))
        {
            MacRestartLog.ExecutableUnknown(logger);
            return false;
        }

        ProcessStartInfo start = SuccessorStart.For(executable, LoginAgent.BundleOf(executable), arguments);

        try
        {
            using Process? started = Process.Start(start);

            if (started is null)
            {
                MacRestartLog.NotStarted(logger, start.FileName);
                return false;
            }

            return true;
        }
        catch (Win32Exception exception)
        {
            MacRestartLog.StartFailed(logger, start.FileName, exception);
            return false;
        }
    }
}

/// <summary>
/// How a successor is started, apart from starting it, so it can be checked without a process.
/// </summary>
internal static class SuccessorStart
{
    private const string Open = "/usr/bin/open";

    /// <param name="executable">The running executable.</param>
    /// <param name="bundle">The bundle it sits in, or null for an executable built on its own.</param>
    /// <param name="arguments">The options for the new copy.</param>
    public static ProcessStartInfo For(string executable, string? bundle, IReadOnlyList<string> arguments)
    {
        if (bundle is null)
        {
            // A development tree has no bundle, so the executable is all there is to start.
            ProcessStartInfo direct = new(executable) { UseShellExecute = false };

            foreach (string argument in arguments)
            {
                direct.ArgumentList.Add(argument);
            }

            return direct;
        }

        // -n because this copy still runs: without it Launch Services brings the running copy
        // forward instead of starting another, and the restart becomes a quit.
        ProcessStartInfo opened = new(Open);
        opened.ArgumentList.Add("-n");
        opened.ArgumentList.Add("-a");
        opened.ArgumentList.Add(bundle);

        if (arguments.Count > 0)
        {
            opened.ArgumentList.Add("--args");

            foreach (string argument in arguments)
            {
                opened.ArgumentList.Add(argument);
            }
        }

        return opened;
    }
}

/// <summary>
/// Source generated log messages for <see cref="MacApplicationRestart"/>.
/// </summary>
internal static partial class MacRestartLog
{
    [LoggerMessage(
        EventId = 3943,
        Level = LogLevel.Warning,
        Message = "The application cannot restart, because the path of its executable is not known.")]
    public static partial void ExecutableUnknown(ILogger logger);

    [LoggerMessage(
        EventId = 3944,
        Level = LogLevel.Warning,
        Message = "The application could not start its successor through {Program}.")]
    public static partial void NotStarted(ILogger logger, string program);

    [LoggerMessage(
        EventId = 3945,
        Level = LogLevel.Warning,
        Message = "The application could not start its successor through {Program}.")]
    public static partial void StartFailed(ILogger logger, string program, Exception exception);
}
