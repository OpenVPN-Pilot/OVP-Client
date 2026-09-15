using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App.Services.Library;

/// <summary>
/// Source generated log messages for the shared library.
/// </summary>
/// <remarks>
/// Paths and counts only. The passphrase and anything read from the file never reach a log.
/// </remarks>
internal static partial class SharedLibraryLog
{
    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Information,
        Message = "Shared library {Path} reconciled: {Added} added, {Updated} changed, {Removed} removed here, {Conflicts} conflict(s), written back: {Written}.")]
    public static partial void Reconciled(ILogger logger, string path, int added, int updated, int removed, int conflicts, bool written);

    [LoggerMessage(
        EventId = 3210,
        Level = LogLevel.Warning,
        Message = "Shared library {Path} holds a version that does not descend from this machine's last write; merging against the version that write started from.")]
    public static partial void WriteNotKept(ILogger logger, string path);

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Warning,
        Message = "Shared library {Path} could not be reached.")]
    public static partial void Unreachable(ILogger logger, string path, Exception? exception);

    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Warning,
        Message = "Shared library {Path} is locked by {Holder}.")]
    public static partial void Locked(ILogger logger, string path, string holder);

    [LoggerMessage(
        EventId = 3203,
        Level = LogLevel.Warning,
        Message = "Shared library {Path} did not open with the stored passphrase.")]
    public static partial void PassphraseRejected(ILogger logger, string path);

    [LoggerMessage(
        EventId = 3204,
        Level = LogLevel.Warning,
        Message = "Shared library {Path} was written by a newer version and is left untouched.")]
    public static partial void TooNew(ILogger logger, string path);

    [LoggerMessage(
        EventId = 3205,
        Level = LogLevel.Error,
        Message = "Shared library {Path} could not be reconciled.")]
    public static partial void Failed(ILogger logger, string path, Exception exception);

    [LoggerMessage(
        EventId = 3206,
        Level = LogLevel.Warning,
        Message = "Shared library {Path} is missing from its folder.")]
    public static partial void FileMissing(ILogger logger, string path);

    [LoggerMessage(
        EventId = 3207,
        Level = LogLevel.Information,
        Message = "Shared library {Path} changed while it was being reconciled, trying again.")]
    public static partial void ChangedMeanwhile(ILogger logger, string path);

    [LoggerMessage(
        EventId = 3208,
        Level = LogLevel.Warning,
        Message = "The record of the last synchronisation could not be read or written, so the next one starts without it.")]
    public static partial void RecordUnusable(ILogger logger, Exception exception);
}
