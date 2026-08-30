using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App;

/// <summary>
/// Source generated log messages for application startup and shutdown.
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

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Shutdown step '{Step}' did not finish within {Seconds} second(s) and was left running.")]
    public static partial void ShutdownStepTimedOut(ILogger logger, string step, double seconds);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Error,
        Message = "Shutdown step '{Step}' failed. The remaining steps were carried out.")]
    public static partial void ShutdownStepFailed(ILogger logger, string step, Exception exception);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Information,
        Message = "Shutdown complete after {Milliseconds} ms.")]
    public static partial void ShutdownCompleted(ILogger logger, long milliseconds);
}
