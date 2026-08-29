using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App;

/// <summary>
/// Source generated log messages for application startup.
/// </summary>
internal static partial class AppLog
{
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Removed {Count} stale runtime configuration(s) from a previous run.")]
    public static partial void StaleRuntimeFilesRemoved(ILogger logger, int count);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Closed {Count} session(s) that a previous run left open.")]
    public static partial void AbandonedSessionsClosed(ILogger logger, int count);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Command line action '{Command}' answered: {Reply}")]
    public static partial void StartupActionRan(ILogger logger, string command, string reply);
}
