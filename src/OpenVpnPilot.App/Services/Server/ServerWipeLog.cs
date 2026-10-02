using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Source generated log messages for carrying out the wipe directive.
/// </summary>
/// <remarks>
/// The wipe takes 3907 to 3939 of the block of the store and the mode switch. What was removed is
/// counted, never named: no profile, no realm, nothing stored.
/// </remarks>
internal static partial class ServerWipeLog
{
    [LoggerMessage(
        EventId = 3908,
        Level = LogLevel.Warning,
        Message = "Server {ServerKey} told this client to wipe in answer to {Method} {Path} ({Status}), request {RequestId}. Everything received from it is removed.")]
    public static partial void Started(ILogger logger, string serverKey, string method, string path, int status, string? requestId);

    [LoggerMessage(
        EventId = 3909,
        Level = LogLevel.Warning,
        Message = "Wiped the copy of server {ServerKey} for request {RequestId}: {Tunnels} tunnel(s) ended, {Profiles} profile(s), {Secrets} keystore entr(y/ies) removed, refresh token removed {RefreshTokenRemoved}, folder removed {FolderRemoved}.")]
    public static partial void Completed(
        ILogger logger,
        string serverKey,
        string? requestId,
        int tunnels,
        int profiles,
        int secrets,
        bool refreshTokenRemoved,
        bool folderRemoved);

    [LoggerMessage(
        EventId = 3910,
        Level = LogLevel.Error,
        Message = "A step of the wipe failed: {Step}. The remaining steps still run.")]
    public static partial void StepFailed(ILogger logger, string step, Exception exception);

    [LoggerMessage(
        EventId = 3911,
        Level = LogLevel.Information,
        Message = "The folder of the server's copy was still in use on attempt {Attempt} ({Reason}); trying again.")]
    public static partial void FolderBusy(ILogger logger, int attempt, string reason);

    [LoggerMessage(
        EventId = 3912,
        Level = LogLevel.Warning,
        Message = "A wipe directive (request {RequestId}) reached a process that does not work on a server's copy, so there was nothing to wipe.")]
    public static partial void NotInServerMode(ILogger logger, string? requestId);
}
