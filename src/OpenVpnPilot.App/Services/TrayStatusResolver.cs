using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Vpn;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Everything the notification area icon's state is decided from, at one moment.
/// </summary>
/// <param name="Tunnels">The state of every tunnel that has a process.</param>
/// <param name="WaitingToReconnect">Tunnels that ended and are waiting for the next attempt.</param>
/// <param name="UnseenFailures">Tunnels that ended in an error nobody has looked at yet.</param>
/// <param name="AwaitingInput">True while a credential prompt is open.</param>
/// <param name="EnvironmentBlocked">True when nothing can connect on this machine.</param>
/// <param name="Server">The server's state in Server mode, null on the local library.</param>
public sealed record TrayStatusInputs(
    IReadOnlyCollection<VpnConnectionState> Tunnels,
    int WaitingToReconnect,
    int UnseenFailures,
    bool AwaitingInput,
    bool EnvironmentBlocked,
    SyncState? Server);

/// <summary>
/// Puts every state that applies in the order the icon prefers them.
/// </summary>
/// <remarks>
/// An icon has one colour, so the order is the decision. What needs a person comes first, then what
/// went wrong, then what is under way, then what is up, and the server last, because a tunnel is what
/// somebody looks at the icon to find out about. All of them are returned rather than the first,
/// because a state the person switched off passes the decision to the next one that applies. Pure,
/// so the order can be tested on its own.
/// </remarks>
public static class TrayStatusResolver
{
    /// <summary>
    /// The states that apply, most pressing first. Never empty: whatever is not idle adds a state
    /// of its own, and with nothing going on it is the idle state alone.
    /// </summary>
    public static IReadOnlyList<TrayStatus> Resolve(TrayStatusInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        List<TrayStatus> applicable = [];

        int connected = inputs.Tunnels.Count(state => state == VpnConnectionState.Connected);
        int reconnecting = inputs.Tunnels.Count(state => state == VpnConnectionState.Reconnecting)
            + inputs.WaitingToReconnect;
        bool busy = inputs.Tunnels.Any(state => state is VpnConnectionState.Launching
            or VpnConnectionState.Authenticating
            or VpnConnectionState.Connecting
            or VpnConnectionState.Disconnecting);

        if (inputs.AwaitingInput)
        {
            applicable.Add(TrayStatus.AwaitingInput);
        }

        if (inputs.Server == SyncState.SignInRequired)
        {
            applicable.Add(TrayStatus.RemoteSignInRequired);
        }

        // A blocked environment matters only while nothing is up: whatever is connected was started
        // before it broke and still works.
        if (inputs.EnvironmentBlocked && connected == 0)
        {
            applicable.Add(TrayStatus.Unavailable);
        }

        if (inputs.UnseenFailures > 0)
        {
            applicable.Add(TrayStatus.Failed);
        }

        if (reconnecting > 0)
        {
            applicable.Add(TrayStatus.Reconnecting);
        }

        if (busy)
        {
            applicable.Add(TrayStatus.Connecting);
        }

        if (connected > 1)
        {
            applicable.Add(TrayStatus.ConnectedMultiple);
        }

        // Several tunnels up are one tunnel up as well, which is where a person who switched the
        // first off expects the decision to go.
        if (connected > 0)
        {
            applicable.Add(TrayStatus.Connected);
        }

        switch (inputs.Server)
        {
            case SyncState.Offline:
                applicable.Add(TrayStatus.RemoteOffline);
                break;

            case SyncState.Degraded
                or SyncState.ClientOutdated
                or SyncState.ClockWrong
                or SyncState.CertificateUntrusted
                or SyncState.SettingsUnreadable
                or SyncState.KeystoreRefused:
                applicable.Add(TrayStatus.RemoteProblem);
                break;
        }

        // Idle means nothing is connected, so it is not a fallback for a tunnel that is up; a person
        // who switched off every state that applies gets the platform's own drawing instead. A
        // synchronisation under way is not shown, or the icon would change every few minutes.
        if (connected == 0 && reconnecting == 0 && !busy)
        {
            applicable.Add(inputs.Server is null ? TrayStatus.IdleLocal : TrayStatus.IdleRemote);
        }

        return applicable;
    }
}

/// <summary>
/// Where the name of each state is found in the language files.
/// </summary>
public static class TrayStatusNames
{
    public static string Key(TrayStatus status) =>
        "trayStatus." + char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..];
}
