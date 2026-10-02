using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Source generated log messages for sharing sign ins with the server's vault.
/// </summary>
/// <remarks>
/// Event ids 3965 to 3969, inside the outbox's block (see <see cref="OutboxLog"/>). A realm names a
/// kind of sign in and is no secret; what is stored under it is never logged.
/// </remarks>
internal static partial class VaultShareLog
{
    [LoggerMessage(
        EventId = 3965,
        Level = LogLevel.Information,
        Message = "The sign in typed for {ProfileId} {Realm} worked and will be shared with the server (kept in the keystore: {Remembered}).")]
    public static partial void Recorded(ILogger logger, Guid profileId, string realm, bool remembered);

    [LoggerMessage(
        EventId = 3966,
        Level = LogLevel.Warning,
        Message = "Could not note the sign in of {ProfileId} for sharing.")]
    public static partial void RecordFailed(ILogger logger, Guid profileId, Exception exception);

    [LoggerMessage(
        EventId = 3967,
        Level = LogLevel.Information,
        Message = "Replaced the shared sign in of {ProfileId} {Realm} on the server.")]
    public static partial void Replaced(ILogger logger, Guid profileId, string realm);

    [LoggerMessage(
        EventId = 3968,
        Level = LogLevel.Warning,
        Message = "Replacing the shared sign in of {ProfileId} {Realm} ended with {Outcome} {Status} {Code}, request {RequestId}.")]
    public static partial void ReplaceFailed(
        ILogger logger,
        Guid profileId,
        string realm,
        Core.Server.ServerOutcome outcome,
        int? status,
        string? code,
        string? requestId);
}
