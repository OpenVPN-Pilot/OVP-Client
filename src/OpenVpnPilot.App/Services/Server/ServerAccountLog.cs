using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Source generated log messages for the session's place in the application: who signed in, what
/// the first start chose, and the Microsoft sign in.
/// </summary>
/// <remarks>
/// These continue the sign in block of the server's event ids after the ones the connection itself
/// writes, 3700 to 3711. A person is named by id, role and provider; never a token, a password or
/// what Microsoft issued.
/// </remarks>
internal static partial class ServerAccountLog
{
    [LoggerMessage(
        EventId = 3712,
        Level = LogLevel.Information,
        Message = "No session with server {ServerKey} is stored; signing in is needed before anything is synchronised.")]
    public static partial void NoStoredSession(ILogger logger, string serverKey);

    [LoggerMessage(
        EventId = 3713,
        Level = LogLevel.Information,
        Message = "User {UserId} signed in to server {ServerKey}, where this copy last knew user {PreviousUserId}. The previous user's data is discarded.")]
    public static partial void DifferentUserSignedIn(ILogger logger, string serverKey, Guid previousUserId, Guid userId);

    [LoggerMessage(
        EventId = 3714,
        Level = LogLevel.Information,
        Message = "Discarded {PendingChanges} change(s) waiting to be sent, {TemporaryProfiles} profile(s) that never reached the server, {Favourites} favourite(s) and {Hotkeys} shortcut(s) of the previous user. The next synchronisation is a full one.")]
    public static partial void PersonalDataDiscarded(ILogger logger, int pendingChanges, int favourites, int hotkeys, int temporaryProfiles);

    [LoggerMessage(
        EventId = 3715,
        Level = LogLevel.Warning,
        Message = "The user signed in to server {ServerKey} could not be remembered in the copy.")]
    public static partial void UserNotRemembered(ILogger logger, string serverKey, Exception exception);

    [LoggerMessage(
        EventId = 3716,
        Level = LogLevel.Error,
        Message = "The wipe of server {ServerKey} for request {RequestId} ended with a failure.")]
    public static partial void WipeFailed(ILogger logger, string serverKey, string? requestId, Exception exception);

    [LoggerMessage(
        EventId = 3717,
        Level = LogLevel.Information,
        Message = "First start: the profiles are to live {Choice}.")]
    public static partial void FirstRunChosen(ILogger logger, FirstRunChoice choice);

    [LoggerMessage(
        EventId = 3718,
        Level = LogLevel.Information,
        Message = "First start: signed in to {ServerAddress} as user {UserId} with role {Role}; the application restarts to synchronise.")]
    public static partial void FirstRunSignedIn(ILogger logger, string serverAddress, Guid userId, string role);

    [LoggerMessage(
        EventId = 3719,
        Level = LogLevel.Warning,
        Message = "Setting up server {ServerAddress} could not switch to it ({Outcome}).")]
    public static partial void FirstRunSwitchFailed(ILogger logger, string serverAddress, Storage.StorageSwitchOutcome outcome);

    [LoggerMessage(
        EventId = 3720,
        Level = LogLevel.Information,
        Message = "The first synchronisation with server {ServerKey} ended with {State}, {Pushed} change(s) sent.")]
    public static partial void FirstSynchronisation(ILogger logger, string serverKey, SyncState state, int pushed);

    [LoggerMessage(
        EventId = 3721,
        Level = LogLevel.Warning,
        Message = "Confirming the session with server {ServerKey} after the restart failed with {Outcome} {Code}, request {RequestId}.")]
    public static partial void SessionNotConfirmed(ILogger logger, string serverKey, ServerOutcome outcome, string? code, string? requestId);

    [LoggerMessage(
        EventId = 3722,
        Level = LogLevel.Information,
        Message = "The Microsoft sign in was cancelled.")]
    public static partial void EntraCancelled(ILogger logger);

    [LoggerMessage(
        EventId = 3723,
        Level = LogLevel.Warning,
        Message = "The Microsoft sign in failed with {ErrorCode}.")]
    public static partial void EntraFailed(ILogger logger, string errorCode, Exception exception);

    [LoggerMessage(
        EventId = 3724,
        Level = LogLevel.Warning,
        Message = "Starting the session with server {ServerKey} failed.")]
    public static partial void StartFailed(ILogger logger, string serverKey, Exception exception);

    [LoggerMessage(
        EventId = 3725,
        Level = LogLevel.Information,
        Message = "Server {ServerAddress} answered as {ServerName} {ServerVersion} with sign in mode {AuthMode}.")]
    public static partial void ServerChecked(ILogger logger, string serverAddress, string serverName, string serverVersion, string authMode);

    [LoggerMessage(
        EventId = 3726,
        Level = LogLevel.Warning,
        Message = "Server {ServerAddress} could not be checked: {Compatibility}, {Outcome} {Code}, request {RequestId}.")]
    public static partial void ServerCheckFailed(
        ILogger logger,
        string serverAddress,
        ServerCompatibility compatibility,
        ServerOutcome outcome,
        string? code,
        string? requestId);
}
