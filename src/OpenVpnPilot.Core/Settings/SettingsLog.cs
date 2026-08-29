using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.Core.Settings;

/// <summary>
/// Source generated log messages for <see cref="JsonSettingsService"/>.
/// </summary>
internal static partial class SettingsLog
{
    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "The settings file {Path} could not be read. The defaults are used for this run.")]
    public static partial void FileUnreadable(ILogger logger, string path, Exception exception);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "The unreadable settings file was kept as {Path}.")]
    public static partial void FileQuarantined(ILogger logger, string path);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Warning,
        Message = "The unreadable settings file {Path} could not be moved aside.")]
    public static partial void QuarantineFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(
        EventId = 1303,
        Level = LogLevel.Warning,
        Message = "Settings could not be written to {Path}. The change applies to this run only.")]
    public static partial void SaveFailed(ILogger logger, string path, Exception exception);
}
