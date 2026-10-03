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

    [LoggerMessage(
        EventId = 1304,
        Level = LogLevel.Information,
        Message = "The settings file was brought up to layout {Version}.")]
    public static partial void Migrated(ILogger logger, int version);

    [LoggerMessage(
        EventId = 1305,
        Level = LogLevel.Information,
        Message = "This installation was given its identity.")]
    public static partial void InstallationIdCreated(ILogger logger);

    [LoggerMessage(
        EventId = 1306,
        Level = LogLevel.Debug,
        Message = "The settings file {Path} could not be opened on attempt {Attempt}. Trying again.")]
    public static partial void ReadRetrying(ILogger logger, string path, int attempt, Exception exception);

    [LoggerMessage(
        EventId = 1307,
        Level = LogLevel.Warning,
        Message = "The settings file {Path} could not be opened. The defaults are used for this run and the file is not written until the next start.")]
    public static partial void FileInaccessible(ILogger logger, string path, Exception exception);

    [LoggerMessage(
        EventId = 1308,
        Level = LogLevel.Warning,
        Message = "Settings were not written to {Path}, because it could not be read at start. The change applies to this run only.")]
    public static partial void SaveSkipped(ILogger logger, string path);
}
