using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.OpenVpn.Runtime;

/// <summary>
/// Source generated log messages for <see cref="ConnectionManager"/>.
/// </summary>
internal static partial class ConnectionManagerLog
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Warning,
        Message = "Disconnecting profile {ProfileId} failed.")]
    public static partial void DisconnectFailed(ILogger logger, Guid profileId, Exception exception);
}
