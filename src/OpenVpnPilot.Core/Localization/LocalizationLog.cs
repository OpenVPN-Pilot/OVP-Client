using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.Core.Localization;

/// <summary>
/// Source generated log messages for the localization components.
/// </summary>
internal static partial class LocalizationLog
{
    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Warning,
        Message = "The language file {Path} could not be read and was skipped.")]
    public static partial void CatalogueUnreadable(ILogger logger, string path, Exception exception);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "The language file {Path} does not contain a JSON object and was skipped.")]
    public static partial void CatalogueNotAnObject(ILogger logger, string path);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Warning,
        Message = "No language catalogue was found. The application falls back to the keys themselves.")]
    public static partial void NoCatalogues(ILogger logger);

    [LoggerMessage(
        EventId = 1203,
        Level = LogLevel.Debug,
        Message = "The key {Key} is missing from the {Language} catalogue.")]
    public static partial void KeyMissing(ILogger logger, string key, string language);

    [LoggerMessage(
        EventId = 1204,
        Level = LogLevel.Warning,
        Message = "The text for {Key} in {Language} has placeholders that do not match the arguments.")]
    public static partial void FormatFailed(ILogger logger, string key, string language, Exception exception);
}
