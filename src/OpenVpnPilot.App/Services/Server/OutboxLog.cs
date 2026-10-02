using Microsoft.Extensions.Logging;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Source generated log messages for the outbox and the work on server profiles in the database.
/// </summary>
/// <remarks>
/// Server mode takes the free blocks of the application's event ids: 3600 the connection and HTTP,
/// 3700 sign in and tokens, 3800 the synchronisation, 3900 the outbox and the mode switch. Within the
/// last, 3950 to 3999 belong to the outbox, the re-key and the removal of a server profile's
/// keystore entries.
///
/// A realm is logged because it names a kind of sign in, such as Auth, and is no secret. What is
/// stored under it never is.
/// </remarks>
internal static partial class OutboxLog
{
    [LoggerMessage(
        EventId = 3950,
        Level = LogLevel.Debug,
        Message = "Recorded a {Kind} change for {EntityId} {Realm} to send to the server.")]
    public static partial void Recorded(ILogger logger, PendingChangeKind kind, Guid? entityId, string? realm);

    [LoggerMessage(
        EventId = 3951,
        Level = LogLevel.Debug,
        Message = "A {Kind} change for {EntityId} replaced {Count} pending change(s).")]
    public static partial void Superseded(ILogger logger, PendingChangeKind kind, Guid? entityId, int count);

    [LoggerMessage(
        EventId = 3952,
        Level = LogLevel.Debug,
        Message = "Pending change {MarkerId} is no longer pending.")]
    public static partial void Dropped(ILogger logger, long markerId);

    [LoggerMessage(
        EventId = 3953,
        Level = LogLevel.Debug,
        Message = "Sending pending change {MarkerId} failed with {Code} and will be tried again.")]
    public static partial void AttemptFailed(ILogger logger, long markerId, string? code);

    [LoggerMessage(
        EventId = 3954,
        Level = LogLevel.Information,
        Message = "Discarded {Count} pending change(s).")]
    public static partial void Cleared(ILogger logger, int count);

    [LoggerMessage(
        EventId = 3960,
        Level = LogLevel.Information,
        Message = "Profile {TemporaryId} now has the server's id {ServerId} ({Outcome}); moved {Sessions} session(s), {Bindings} shortcut(s) and {Secrets} stored sign in(s).")]
    public static partial void Rekeyed(
        ILogger logger,
        Guid temporaryId,
        Guid serverId,
        ProfileRekeyOutcome outcome,
        int sessions,
        int bindings,
        int secrets);

    [LoggerMessage(
        EventId = 3961,
        Level = LogLevel.Warning,
        Message = "Giving profile {TemporaryId} the server's id {ServerId} failed; it stays as it was.")]
    public static partial void RekeyFailed(ILogger logger, Guid temporaryId, Guid serverId, Exception exception);

    [LoggerMessage(
        EventId = 3962,
        Level = LogLevel.Warning,
        Message = "A stored sign in of realm {Realm} copied to profile {ServerId} by a failed re-key could not be removed again.")]
    public static partial void CopiedSecretNotRemoved(ILogger logger, Guid serverId, string realm, Exception exception);

    [LoggerMessage(
        EventId = 3963,
        Level = LogLevel.Warning,
        Message = "The stored sign in of realm {Realm} under the former id {TemporaryId} could not be removed after the re-key.")]
    public static partial void FormerSecretNotRemoved(ILogger logger, Guid temporaryId, string realm, Exception exception);

    [LoggerMessage(
        EventId = 3964,
        Level = LogLevel.Information,
        Message = "Removed {Count} stored sign in(s) of {Profiles} server profile(s).")]
    public static partial void SecretsRemoved(ILogger logger, int count, int profiles);
}
