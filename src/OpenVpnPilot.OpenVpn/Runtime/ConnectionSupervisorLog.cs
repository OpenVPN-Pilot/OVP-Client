using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.OpenVpn.Runtime;

/// <summary>
/// Source generated log messages for <see cref="ConnectionSupervisor"/>.
/// </summary>
/// <remarks>
/// Only realm names are logged. Usernames and passwords never reach a log message.
/// </remarks>
internal static partial class ConnectionSupervisorLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Error, Message = "The management pump stopped unexpectedly.")]
    public static partial void PumpFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Credentials for realm {Realm} were rejected.")]
    public static partial void CredentialsRejected(ILogger logger, string realm);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "No credentials are available for realm {Realm}, abandoning the connection.")]
    public static partial void CredentialsUnavailable(ILogger logger, string realm);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Information,
        Message = "The server raised a one time code challenge for realm {Realm}.")]
    public static partial void ChallengeReceived(ILogger logger, string realm);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "OpenVPN process {ProcessId} ignored the shutdown signal and is being terminated.")]
    public static partial void ProcessDidNotExit(ILogger logger, int processId);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Information,
        Message = "The stop signal could not be delivered; the management connection was already gone.")]
    public static partial void SignalNotDelivered(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Warning,
        Message = "The stop signal was not answered in time; the process is being torn down anyway.")]
    public static partial void SignalNotAnswered(ILogger logger);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "OpenVPN process {ProcessId} could not be inspected or terminated. It may outlive this disconnect.")]
    public static partial void ProcessCheckDenied(ILogger logger, int processId, Exception exception);
}
