using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.OpenVpn.Management;

/// <summary>
/// Source generated log messages for <see cref="ManagementClient"/>.
/// </summary>
/// <remarks>
/// Credential values are never passed to any of these messages. Command text is logged only at
/// trace level, and the credential commands carry their values in the same string, so callers must
/// not route credential commands through <see cref="CommandSent"/>.
/// </remarks>
internal static partial class ManagementClientLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Trace, Message = "Management command sent: {Command}")]
    public static partial void CommandSent(ILogger logger, string command);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Management connection closed.")]
    public static partial void ConnectionClosed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Debug,
        Message = "Unsolicited management response ignored: {Line}")]
    public static partial void UnsolicitedResponse(ILogger logger, string line);
}
