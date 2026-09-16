namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Tells a close somebody asked for from the main window apart from one another program asked for.
/// </summary>
/// <remarks>
/// Closing the window can keep the application running in the notification area. That is what a
/// person closing the window means, and not what Task Manager, <c>taskkill</c> or an installer mean
/// when they ask a window to close: they wait for the process to end, and a window that hides instead
/// is reported as not responding. Both arrive as the same close, so the platform has to say which one
/// it was. A platform where only a person can close the window does not register this at all.
/// </remarks>
public interface IWindowCloseOrigin
{
    /// <summary>
    /// Sees a message delivered to the main window, before the window handles it.
    /// </summary>
    public void Observe(uint message, nint wParam);

    /// <summary>
    /// True when the close under way was asked for from the window itself: its close button, its
    /// system menu, its keyboard shortcut or its taskbar entry. Answers once per close.
    /// </summary>
    public bool TakeAskedFromWindow();
}
