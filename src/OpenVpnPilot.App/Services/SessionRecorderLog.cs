using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Source generated log messages for <see cref="SessionRecorder"/>.
/// </summary>
internal static partial class SessionRecorderLog
{
    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Warning,
        Message = "The session history for profile {ProfileId} could not be written.")]
    public static partial void RecordFailed(ILogger logger, Guid profileId, Exception exception);
}
