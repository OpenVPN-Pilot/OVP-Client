namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Says when the computer is about to sleep and when it has woken again.
/// </summary>
/// <remarks>
/// A tunnel does not survive a sleep of any length in a state anybody can rely on: the server stops
/// hearing from it, a NAT forgets it, and an OpenVPN that was never told to give up on a silent far
/// end goes on reporting the tunnel as connected for as long as the process lives. Waking is
/// therefore the moment every tunnel has to be checked, and only the platform knows when it is.
///
/// Events are raised on whatever thread the platform delivers them on, and handlers must return
/// quickly and never throw. A platform whose notification cannot be registered says so in its log
/// and raises nothing; the application also notices a sleep from the clock jumping, so the absence
/// of these events costs promptness rather than correctness.
/// </remarks>
public interface ISystemPowerEvents : IDisposable
{
    /// <summary>
    /// Raised as the computer is about to sleep.
    /// </summary>
    public event EventHandler? Suspending;

    /// <summary>
    /// Raised once the computer has woken.
    /// </summary>
    public event EventHandler? Resumed;

    /// <summary>
    /// Starts listening. Called once, after the handlers are attached.
    /// </summary>
    public void Start();
}
