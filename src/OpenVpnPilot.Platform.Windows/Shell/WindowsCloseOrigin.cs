using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// Tells a close from the window's own controls apart from a close another program sent.
/// </summary>
/// <remarks>
/// Everything a person uses to close a window, the close button, Alt+F4, the system menu and the
/// taskbar, reaches the window as <c>WM_SYSCOMMAND</c> with <c>SC_CLOSE</c>, which the default window
/// procedure turns into <c>WM_CLOSE</c>. Task Manager's End task and <c>taskkill</c> without
/// <c>/F</c> send <c>WM_CLOSE</c> directly. Measured with <c>taskkill</c>: the close arrived, the
/// window hid itself in the notification area and the process kept running, which Task Manager then
/// reported as a program that does not respond.
/// </remarks>
public sealed class WindowsCloseOrigin : IWindowCloseOrigin
{
    private const uint SystemCommand = 0x0112;
    private const int CloseCommand = 0xF060;

    // The low four bits of the command are used by the system and are not part of it.
    private const int CommandMask = 0xFFF0;

    private bool askedFromWindow;

    public void Observe(uint message, nint wParam)
    {
        if (message == SystemCommand && ((int)wParam & CommandMask) == CloseCommand)
        {
            askedFromWindow = true;
        }
    }

    public bool TakeAskedFromWindow()
    {
        bool asked = askedFromWindow;
        askedFromWindow = false;
        return asked;
    }
}
