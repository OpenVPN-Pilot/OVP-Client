using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Storage;

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

    [LoggerMessage(
        EventId = 3903,
        Level = LogLevel.Information,
        Message = "Switching to {Mode} was refused, because {Count} tunnel(s) are up.")]
    public static partial void SwitchRefusedTunnelsUp(ILogger logger, StorageMode mode, int count);

    [LoggerMessage(
        EventId = 3904,
        Level = LogLevel.Warning,
        Message = "Switching to {Mode} was abandoned, because the settings file did not take the change.")]
    public static partial void SwitchNotSaved(ILogger logger, StorageMode mode);

    [LoggerMessage(
        EventId = 3905,
        Level = LogLevel.Warning,
        Message = "Switching to {Mode} was abandoned, because the application could not start again. The previous mode was put back.")]
    public static partial void SwitchRestartFailed(ILogger logger, StorageMode mode);

    [LoggerMessage(
        EventId = 3906,
        Level = LogLevel.Information,
        Message = "Switching from {From} to {To} {ServerAddress}. The application restarts.")]
    public static partial void Switching(ILogger logger, StorageMode from, StorageMode to, string? serverAddress);

    [LoggerMessage(
        EventId = 3907,
        Level = LogLevel.Warning,
        Message = "Leaving the server after the wipe, the application could not start again. It ends now and starts locally next time.")]
    public static partial void LeaveRestartFailed(ILogger logger);
}
