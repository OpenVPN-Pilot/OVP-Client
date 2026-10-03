using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Source generated log messages for what the application shows about the server and for the
/// storage settings: how the server answers, the leftovers of a server that withdrew the account,
/// and what a person asked for in the storage settings.
/// </summary>
/// <remarks>
/// These take 3913 to 3939 of the block of the store and the mode switch, after the wipe. Nothing
/// secret is written: a server is named by its host or key, a person by id and role.
/// </remarks>
internal static partial class ServerStatusLog
{
    [LoggerMessage(
        EventId = 3913,
        Level = LogLevel.Debug,
        Message = "Server {Server} answered in {Milliseconds} ms; ready {Ready}.")]
    public static partial void Probed(ILogger logger, string server, long milliseconds, bool ready);

    [LoggerMessage(
        EventId = 3914,
        Level = LogLevel.Information,
        Message = "Server {Server} stopped answering ({Outcome}). Asking again less often until it does.")]
    public static partial void Unreachable(ILogger logger, string server, ServerOutcome outcome);

    [LoggerMessage(
        EventId = 3915,
        Level = LogLevel.Information,
        Message = "Server {Server} answers again.")]
    public static partial void Reachable(ILogger logger, string server);

    [LoggerMessage(
        EventId = 3916,
        Level = LogLevel.Warning,
        Message = "Server {Server} answers, but its readiness check says it cannot serve (status {Status}, request {RequestId}).")]
    public static partial void Degraded(ILogger logger, string server, int? status, string? requestId);

    [LoggerMessage(
        EventId = 3917,
        Level = LogLevel.Warning,
        Message = "Server {ServerKey} told this client to wipe in answer to {Method} {Path} ({Status}), request {RequestId}, during a sign in made from another store. What this computer kept of that server is removed; the store in use stays.")]
    public static partial void LeftoversWipeStarted(ILogger logger, string serverKey, string method, string path, int status, string? requestId);

    [LoggerMessage(
        EventId = 3918,
        Level = LogLevel.Warning,
        Message = "Removed what this computer kept of server {ServerKey} for request {RequestId}: {Profiles} profile(s) in its copy, {Secrets} keystore entr(y/ies), refresh token removed {RefreshTokenRemoved}, folder removed {FolderRemoved}.")]
    public static partial void LeftoversRemoved(
        ILogger logger,
        string serverKey,
        string? requestId,
        int profiles,
        int secrets,
        bool refreshTokenRemoved,
        bool folderRemoved);

    [LoggerMessage(
        EventId = 3919,
        Level = LogLevel.Warning,
        Message = "A wipe directive for server {ServerKey} (request {RequestId}) named the copy this process works on; it is carried out by the wipe of the running copy instead.")]
    public static partial void LeftoversAreActive(ILogger logger, string serverKey, string? requestId);

    [LoggerMessage(
        EventId = 3920,
        Level = LogLevel.Error,
        Message = "A step of removing what this computer kept of a server failed: {Step}. The remaining steps still run.")]
    public static partial void LeftoversStepFailed(ILogger logger, string step, Exception exception);

    [LoggerMessage(
        EventId = 3921,
        Level = LogLevel.Information,
        Message = "User {UserId} ({Role}) signed in to {ServerAddress} from the storage settings; switching to it.")]
    public static partial void SwitchSignedIn(ILogger logger, string serverAddress, Guid userId, string role);

    [LoggerMessage(
        EventId = 3922,
        Level = LogLevel.Warning,
        Message = "Switching to {ServerAddress} after signing in did not happen: {Outcome}. The sign in was ended again.")]
    public static partial void SwitchAfterSignInFailed(ILogger logger, string serverAddress, StorageSwitchOutcome outcome);

    [LoggerMessage(
        EventId = 3923,
        Level = LogLevel.Information,
        Message = "A synchronisation was asked for from {Origin}.")]
    public static partial void SyncRequested(ILogger logger, string origin);

    [LoggerMessage(
        EventId = 3924,
        Level = LogLevel.Information,
        Message = "Signing out of {ServerAddress} from the storage settings.")]
    public static partial void SignOutRequested(ILogger logger, string serverAddress);

    [LoggerMessage(
        EventId = 3925,
        Level = LogLevel.Information,
        Message = "Switching to {Mode} was confirmed in the storage settings; the outcome is {Outcome}.")]
    public static partial void SwitchConfirmed(ILogger logger, StorageMode mode, StorageSwitchOutcome outcome);

    [LoggerMessage(
        EventId = 3926,
        Level = LogLevel.Information,
        Message = "Switching to {Mode} was not offered, because {Count} tunnel(s) are up.")]
    public static partial void SwitchNotOfferedTunnelsUp(ILogger logger, StorageMode mode, int count);

    [LoggerMessage(
        EventId = 3927,
        Level = LogLevel.Warning,
        Message = "The synchronisation asked for from the storage settings failed.")]
    public static partial void SyncFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3928,
        Level = LogLevel.Error,
        Message = "Asking the server how it answers failed unexpectedly. It is asked again as usual.")]
    public static partial void ProbeFailed(ILogger logger, Exception exception);
}
