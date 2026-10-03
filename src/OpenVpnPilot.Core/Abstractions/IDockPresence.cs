namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Whether the application is listed among the running applications while it has no window open.
/// </summary>
/// <remarks>
/// Closing the window keeps the tunnels running and leaves the entry in the notification area or the
/// menu bar as the way back in. On Windows that needs nothing: the taskbar lists windows, so a hidden
/// window takes its button with it. macOS lists applications in the Dock whether they have a window or
/// not, so an application that only lives in the menu bar still sits in the Dock unless it says
/// otherwise. A platform whose list follows the windows does not register this at all.
///
/// Whether it is listed at all is the user's, under the general settings and in the menu bar entry.
/// Being listed costs an entry in the Dock's list of recent applications, which outlives the window
/// and the process and which the application cannot take back, so somebody who wants nothing of the
/// Dock has to be able to say so.
/// </remarks>
public interface IDockPresence
{
    /// <summary>
    /// Lists the application while it has a window to show, and removes it while it has none.
    /// </summary>
    public void SetListed(bool listed);
}
