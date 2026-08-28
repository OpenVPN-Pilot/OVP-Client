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
}
