using OpenVpnPilot.OpenVpn.Management;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Passes OpenVPN's log stream into the hub, tagged with the profile it belongs to.
/// </summary>
/// <remarks>
/// The supervisor has always read this stream, because the pushed options appear nowhere else, and
/// has always thrown it away afterwards. That is what left a failed connection with nothing to look
/// at: the state said the tunnel had gone, the client said what it made of that, and the account
/// OpenVPN itself gave was gone.
///
/// The profile name rather than its identifier. With several tunnels up the log is only readable if
/// each line says which one it is about, and a name is what the user recognises.
/// </remarks>
public sealed class OpenVpnLogRelay : IDisposable
{
    private readonly ConnectionManager connections;
    private readonly LogHub hub;
    private readonly IProfileNameLookup profileNames;
    private bool disposed;

    public OpenVpnLogRelay(ConnectionManager connections, LogHub hub, IProfileNameLookup profileNames)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(profileNames);

        this.connections = connections;
        this.hub = hub;
        this.profileNames = profileNames;
    }

    public void Attach() => connections.LogReceived += OnLogReceived;

    private void OnLogReceived(object? sender, ConnectionLogReceived received)
    {
        LogMessage message = received.Message;

        hub.Append(new LogEntry(
            message.Timestamp,
            LogSource.OpenVpn,
            Map(message.Severity),
            profileNames.GetDisplayName(received.ProfileId),
            message.Text));
    }

    /// <summary>
    /// Maps OpenVPN's severities onto the one scale both streams are filtered by.
    /// </summary>
    /// <remarks>
    /// OpenVPN sends an empty flag for ordinary progress, which is the bulk of the stream and is
    /// what a person means by "the log". It is mapped to information rather than to trace so that
    /// opening the window on the default filter shows something.
    /// </remarks>
    private static LogEntryLevel Map(LogSeverity severity) => severity switch
    {
        LogSeverity.Debug => LogEntryLevel.Debug,
        LogSeverity.Warning => LogEntryLevel.Warning,
        LogSeverity.NonFatalError => LogEntryLevel.Error,
        LogSeverity.Fatal => LogEntryLevel.Fatal,
        _ => LogEntryLevel.Information,
    };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connections.LogReceived -= OnLogReceived;
    }
}
