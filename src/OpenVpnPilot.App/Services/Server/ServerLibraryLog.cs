using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Source generated log messages for what the screens do to a server's copy: removing, importing
/// and the sign ins that are reset.
/// </summary>
/// <remarks>
/// Event ids 3970 to 3989, inside the outbox's block (see <see cref="OutboxLog"/>).
/// </remarks>
internal static partial class ServerLibraryLog
{
    [LoggerMessage(
        EventId = 3970,
        Level = LogLevel.Information,
        Message = "The server deleted profile {ProfileId} while its tunnel was up; the tunnel was ended before the profile was removed.")]
    public static partial void TunnelEndedForRemoval(ILogger logger, Guid profileId);

    [LoggerMessage(
        EventId = 3971,
        Level = LogLevel.Information,
        Message = "Forgot the synchronisation cursor, so the next synchronisation fetches everything again.")]
    public static partial void CursorReset(ILogger logger);

    [LoggerMessage(
        EventId = 3972,
        Level = LogLevel.Information,
        Message = "Deleted server profile {ProfileId} here and removed {Secrets} stored sign in(s) of it.")]
    public static partial void ProfileDeleted(ILogger logger, Guid profileId, int secrets);

    [LoggerMessage(
        EventId = 3973,
        Level = LogLevel.Information,
        Message = "Imported {Count} profile(s) into the server's copy: {Created} created on the server, {Duplicates} duplicate(s), {Rejected} refused, {Waiting} waiting to be sent.")]
    public static partial void Imported(ILogger logger, int count, int created, int duplicates, int rejected, int waiting);

    [LoggerMessage(
        EventId = 3974,
        Level = LogLevel.Information,
        Message = "Applied a package to the server's copy: {Profiles} profile(s) and {Credentials} sign in(s) to send to the server.")]
    public static partial void PackageApplied(ILogger logger, int profiles, int credentials);
}
