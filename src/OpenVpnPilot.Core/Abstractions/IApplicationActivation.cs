namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Brings the application in front of whatever else is there, on a platform where a window of its
/// own does not do that.
/// </summary>
/// <remarks>
/// On Windows a window that is shown and activated takes the foreground with it. macOS separates the
/// two: showing a window orders it in front of this application's other windows, and which
/// application is in front is a decision of its own. An application that is not in the Dock is never
/// made the front one by the system, so a window opened from the menu bar appeared behind whatever
/// the person was working in and did not take the keyboard.
///
/// A platform whose windows carry the foreground with them does not register this at all.
/// </remarks>
public interface IApplicationActivation
{
    /// <summary>
    /// Makes this application the one in front. Must be called from the user interface thread.
    /// </summary>
    public void BringToFront();
}
