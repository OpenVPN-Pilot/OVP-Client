using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// Source generated log messages for signing in and keeping the session alive.
/// </summary>
/// <remarks>
/// Never a token, a password or an Entra token. Who signed in is named by id, role and provider.
/// </remarks>
internal static partial class ServerSessionLog
{
    [LoggerMessage(
        EventId = 3700,
        Level = LogLevel.Information,
        Message = "Signed in to server {ServerKey} as user {UserId} with role {Role} through {Provider}.")]
    public static partial void SignedIn(ILogger logger, string serverKey, Guid userId, string role, string provider);

    [LoggerMessage(
        EventId = 3701,
        Level = LogLevel.Information,
        Message = "Signed out of server {ServerKey}.")]
    public static partial void SignedOut(ILogger logger, string serverKey);

    [LoggerMessage(
        EventId = 3702,
        Level = LogLevel.Debug,
        Message = "The session with server {ServerKey} was refreshed; the access token is valid until {ExpiresAt}.")]
    public static partial void Refreshed(ILogger logger, string serverKey, DateTimeOffset expiresAt);

    [LoggerMessage(
        EventId = 3703,
        Level = LogLevel.Warning,
        Message = "Refreshing the session with server {ServerKey} failed with {Outcome} {Code}, request {RequestId}.")]
    public static partial void RefreshFailed(
        ILogger logger,
        string serverKey,
        ServerOutcome outcome,
        string? code,
        string? requestId);

    [LoggerMessage(
        EventId = 3704,
        Level = LogLevel.Information,
        Message = "The session with server {ServerKey} ended with {Code}, request {RequestId}; signing in again is needed.")]
    public static partial void SessionEnded(ILogger logger, string serverKey, string? code, string? requestId);

    [LoggerMessage(
        EventId = 3705,
        Level = LogLevel.Information,
        Message = "The role of user {UserId} on server {ServerKey} changed from {PreviousRole} to {Role}.")]
    public static partial void RoleChanged(ILogger logger, string serverKey, Guid userId, string previousRole, string role);

    [LoggerMessage(
        EventId = 3706,
        Level = LogLevel.Warning,
        Message = "The refresh token of server {ServerKey} could not be stored in the keystore. The session lasts until the application ends.")]
    public static partial void RefreshTokenNotStored(ILogger logger, string serverKey, Exception exception);

    [LoggerMessage(
        EventId = 3707,
        Level = LogLevel.Warning,
        Message = "The refresh token of server {ServerKey} could not be removed from the keystore.")]
    public static partial void RefreshTokenNotRemoved(ILogger logger, string serverKey, Exception exception);

    [LoggerMessage(
        EventId = 3708,
        Level = LogLevel.Information,
        Message = "A stored session with server {ServerKey} was found.")]
    public static partial void Restored(ILogger logger, string serverKey);

    [LoggerMessage(
        EventId = 3709,
        Level = LogLevel.Warning,
        Message = "The server at {BaseAddress} is not usable: {Compatibility} (server {ServerName} {ServerVersion}, API {ApiVersion}, minimum client {MinimumClientVersion}, this client {ClientVersion}).")]
    public static partial void ServerNotUsable(
        ILogger logger,
        Uri baseAddress,
        ServerCompatibility compatibility,
        string? serverName,
        string? serverVersion,
        string? apiVersion,
        string? minimumClientVersion,
        Version clientVersion);

    [LoggerMessage(
        EventId = 3710,
        Level = LogLevel.Warning,
        Message = "Signing in to server {ServerKey} failed with {Outcome} {Code}, request {RequestId}.")]
    public static partial void SignInFailed(
        ILogger logger,
        string serverKey,
        ServerOutcome outcome,
        string? code,
        string? requestId);

    [LoggerMessage(
        EventId = 3711,
        Level = LogLevel.Warning,
        Message = "Signing out of server {ServerKey} was not confirmed ({Outcome} {Code}, request {RequestId}); the tokens are discarded anyway.")]
    public static partial void SignOutNotConfirmed(
        ILogger logger,
        string serverKey,
        ServerOutcome outcome,
        string? code,
        string? requestId);

    [LoggerMessage(
        EventId = 3729,
        Level = LogLevel.Warning,
        Message = "The keystore did not hand over the session with server {ServerKey}. It stays stored, and the next start asks for it again.")]
    public static partial void RestoreRefused(ILogger logger, string serverKey);

    [LoggerMessage(
        EventId = 3730,
        Level = LogLevel.Information,
        Message = "The session with server {ServerKey} was renewed through Microsoft without asking user {UserId}.")]
    public static partial void EntraRenewed(ILogger logger, string serverKey, Guid userId);

    [LoggerMessage(
        EventId = 3731,
        Level = LogLevel.Information,
        Message = "Renewing the session with server {ServerKey} through Microsoft was refused ({Code}); signing in again is needed.")]
    public static partial void EntraRenewalRefused(ILogger logger, string serverKey, string? code);

    [LoggerMessage(
        EventId = 3732,
        Level = LogLevel.Warning,
        Message = "Renewing the session with server {ServerKey} through Microsoft could not be done now ({Outcome} {Code}); it is tried again with the next refresh.")]
    public static partial void EntraRenewalPostponed(ILogger logger, string serverKey, string outcome, string? code);

    [LoggerMessage(
        EventId = 3733,
        Level = LogLevel.Warning,
        Message = "Renewing the session with server {ServerKey} through Microsoft answered for user {UserId} instead of {PreviousUserId}; the new session was ended.")]
    public static partial void EntraRenewalOtherUser(ILogger logger, string serverKey, Guid previousUserId, Guid userId);

    [LoggerMessage(
        EventId = 3734,
        Level = LogLevel.Warning,
        Message = "What the Microsoft sign in to server {ServerKey} left behind could not be stored. The session is not renewed without the person.")]
    public static partial void EntraStateNotStored(ILogger logger, string serverKey, Exception exception);

    [LoggerMessage(
        EventId = 3735,
        Level = LogLevel.Warning,
        Message = "What the Microsoft sign in to server {ServerKey} left behind could not be removed from the keystore.")]
    public static partial void EntraStateNotRemoved(ILogger logger, string serverKey, Exception exception);
}
