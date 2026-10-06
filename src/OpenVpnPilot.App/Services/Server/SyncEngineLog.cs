using Microsoft.Extensions.Logging;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Source generated log messages of the synchronisation, in the block 3800 to 3899.
/// </summary>
/// <remarks>
/// Values are passed as they are: ids, codes, counts and request ids. A configuration, a vault entry
/// or a token never is, nor anything derived from one beyond its hash.
/// </remarks>
internal static partial class SyncEngineLog
{
    [LoggerMessage(
        EventId = 3800,
        Level = LogLevel.Information,
        Message = "Synchronised with {Server}: pushed {Pushed}, dropped {Dropped}; pulled {Profiles} profile(s), {Tags} tag(s), {Vault} vault entr(ies), {Deletions} deletion(s); cursor {CursorFrom} -> {CursorTo}; {DurationMs} ms.")]
    public static partial void CycleCompleted(
        ILogger logger,
        string server,
        int pushed,
        int dropped,
        int profiles,
        int tags,
        int vault,
        int deletions,
        long? cursorFrom,
        long? cursorTo,
        long durationMs);

    [LoggerMessage(
        EventId = 3801,
        Level = LogLevel.Information,
        Message = "Synchronising with {Server} stopped as {State} ({Code}, request {RequestId}) after pushing {Pushed} and dropping {Dropped}; {DurationMs} ms.")]
    public static partial void CycleStopped(
        ILogger logger,
        string server,
        SyncState state,
        string? code,
        string? requestId,
        int pushed,
        int dropped,
        long durationMs);

    [LoggerMessage(
        EventId = 3802,
        Level = LogLevel.Warning,
        Message = "{Server} cannot be reached ({State}); working from the local copy and trying again later.")]
    public static partial void WentOffline(ILogger logger, string server, SyncState state);

    [LoggerMessage(
        EventId = 3803,
        Level = LogLevel.Information,
        Message = "{Server} can be reached again.")]
    public static partial void BackOnline(ILogger logger, string server);

    [LoggerMessage(
        EventId = 3804,
        Level = LogLevel.Warning,
        Message = "The server refused the {Kind} change of {EntityId} for good with {Status} {Code} (request {RequestId}); it is not synchronised.")]
    public static partial void ChangeRefused(
        ILogger logger,
        PendingChangeKind kind,
        Guid? entityId,
        int? status,
        string? code,
        string? requestId);

    [LoggerMessage(
        EventId = 3805,
        Level = LogLevel.Warning,
        Message = "Sending the {Kind} change of {EntityId} failed with {Outcome} {Status} {Code} (request {RequestId}); it is kept for the next attempt.")]
    public static partial void ChangeDeferred(
        ILogger logger,
        PendingChangeKind kind,
        Guid? entityId,
        Core.Server.ServerOutcome outcome,
        int? status,
        string? code,
        string? requestId);

    [LoggerMessage(
        EventId = 3806,
        Level = LogLevel.Information,
        Message = "The server no longer knows cursor {Cursor} (request {RequestId}); synchronising everything again.")]
    public static partial void CursorExpired(ILogger logger, long cursor, string? requestId);

    [LoggerMessage(
        EventId = 3807,
        Level = LogLevel.Information,
        Message = "Profile {TemporaryId} was uploaded and is now {ServerId}.")]
    public static partial void ProfileUploaded(ILogger logger, Guid temporaryId, Guid serverId);

    [LoggerMessage(
        EventId = 3808,
        Level = LogLevel.Information,
        Message = "Profile {ServerId} was changed here while it was being uploaded; the change is sent again.")]
    public static partial void EditDuringUpload(ILogger logger, Guid serverId);

    [LoggerMessage(
        EventId = 3809,
        Level = LogLevel.Information,
        Message = "Profile {TemporaryId} was deleted here while it was being uploaded as {ServerId}; it is deleted on the server too.")]
    public static partial void DeletedDuringUpload(ILogger logger, Guid temporaryId, Guid serverId);

    [LoggerMessage(
        EventId = 3810,
        Level = LogLevel.Information,
        Message = "The server already holds the configuration of profile {TemporaryId} (request {RequestId}); it is matched after the next pull.")]
    public static partial void UploadDuplicate(ILogger logger, Guid temporaryId, string? requestId);

    [LoggerMessage(
        EventId = 3827,
        Level = LogLevel.Warning,
        Message = "The server refused profile {ProfileId} for good ({Refusal}); it is kept on this computer and marked as not uploaded.")]
    public static partial void UploadRefused(ILogger logger, Guid profileId, string refusal);

    [LoggerMessage(
        EventId = 3811,
        Level = LogLevel.Information,
        Message = "Profile {TemporaryId} is the server's profile {ServerId} and now carries its id.")]
    public static partial void DuplicateMatched(ILogger logger, Guid temporaryId, Guid serverId);

    [LoggerMessage(
        EventId = 3812,
        Level = LogLevel.Warning,
        Message = "The server called profile {TemporaryId} a duplicate, but no profile it holds has the same configuration; the local copy was removed.")]
    public static partial void DuplicateUnmatched(ILogger logger, Guid temporaryId);

    [LoggerMessage(
        EventId = 3813,
        Level = LogLevel.Warning,
        Message = "The server refused administrator changes (request {RequestId}); {Count} pending change(s) of that kind were dropped.")]
    public static partial void AdministratorChangesDropped(ILogger logger, string? requestId, int count);

    [LoggerMessage(
        EventId = 3814,
        Level = LogLevel.Information,
        Message = "The signed in role is now {Role}.")]
    public static partial void RoleRefreshed(ILogger logger, string role);

    [LoggerMessage(
        EventId = 3815,
        Level = LogLevel.Information,
        Message = "The shared vault already had the sign in of realm {Realm} for profile {ProfileId}; that one is stored here now.")]
    public static partial void VaultEntryAdopted(ILogger logger, Guid profileId, string realm);

    [LoggerMessage(
        EventId = 3816,
        Level = LogLevel.Warning,
        Message = "There is no stored sign in of realm {Realm} for profile {ProfileId} to share; the change was dropped.")]
    public static partial void VaultSecretMissing(ILogger logger, Guid? profileId, string? realm);

    [LoggerMessage(
        EventId = 3817,
        Level = LogLevel.Information,
        Message = "The server holds no {Kind} for this person yet; this machine's are sent.")]
    public static partial void PersonalSeeded(ILogger logger, PendingChangeKind kind);

    [LoggerMessage(
        EventId = 3818,
        Level = LogLevel.Debug,
        Message = "Applied the server's {Kind}: {Changed}.")]
    public static partial void PersonalApplied(ILogger logger, PendingChangeKind kind, bool changed);

    [LoggerMessage(
        EventId = 3819,
        Level = LogLevel.Debug,
        Message = "Fetched the configuration of profile {ProfileId} with hash {ContentHash}.")]
    public static partial void ConfigurationFetched(ILogger logger, Guid profileId, string contentHash);

    [LoggerMessage(
        EventId = 3820,
        Level = LogLevel.Error,
        Message = "A synchronisation with {Server} failed.")]
    public static partial void CycleFailed(ILogger logger, string server, Exception exception);

    [LoggerMessage(
        EventId = 3821,
        Level = LogLevel.Information,
        Message = "Synchronisation with {Server} started.")]
    public static partial void Started(ILogger logger, string server);

    [LoggerMessage(
        EventId = 3822,
        Level = LogLevel.Information,
        Message = "Synchronisation with {Server} stopped.")]
    public static partial void Stopped(ILogger logger, string server);

    [LoggerMessage(
        EventId = 3823,
        Level = LogLevel.Information,
        Message = "The server told this client to wipe; synchronisation ends.")]
    public static partial void Wiped(ILogger logger);

    [LoggerMessage(
        EventId = 3824,
        Level = LogLevel.Debug,
        Message = "Next synchronisation in {Delay}.")]
    public static partial void NextCycle(ILogger logger, TimeSpan delay);

    [LoggerMessage(
        EventId = 3825,
        Level = LogLevel.Warning,
        Message = "Reloading the screens after a change by the synchronisation ({Changes}) failed.")]
    public static partial void RefreshFailed(ILogger logger, LibraryChanges changes, Exception exception);

    [LoggerMessage(
        EventId = 3826,
        Level = LogLevel.Debug,
        Message = "Profile {ProfileId} is in the server's answer but gone from the server by now; skipped.")]
    public static partial void ProfileVanished(ILogger logger, Guid profileId);

    [LoggerMessage(
        EventId = 3828,
        Level = LogLevel.Information,
        Message = "The synchronisation interval is now {Interval}.")]
    public static partial void IntervalChanged(ILogger logger, TimeSpan interval);
}
