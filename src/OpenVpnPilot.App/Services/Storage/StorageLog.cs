using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server;

namespace OpenVpnPilot.App.Services.Storage;

/// <summary>
/// Source generated log messages for the store this process works on and for switching it.
/// </summary>
/// <remarks>
/// Server mode takes the event ids from 3600 to 3999, in blocks: 3600 to 3799 the connection to the
/// server and signing in, 3800 to 3899 synchronisation, 3900 to 3949 the store and switching it, and
/// 3950 to 3999 the record of changes waiting to be sent.
/// </remarks>
internal static partial class StorageLog
{
    [LoggerMessage(
        EventId = 3900,
        Level = LogLevel.Information,
        Message = "Working on the local library {DatabasePath}.")]
    public static partial void LocalStoreOpened(ILogger logger, string databasePath);

    [LoggerMessage(
        EventId = 3901,
        Level = LogLevel.Information,
        Message = "Working on the copy of {ServerAddress}, kept under {ServerKey}.")]
    public static partial void ServerStoreOpened(ILogger logger, string serverAddress, string serverKey);

    [LoggerMessage(
        EventId = 3902,
        Level = LogLevel.Warning,
        Message = "The settings ask for a server whose address is not usable ({Problem}), so this run works on the local library.")]
    public static partial void ServerAddressUnusable(ILogger logger, ServerAddressProblem problem);
}
