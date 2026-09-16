using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// The parts of the macOS application menu only AppKit can provide.
/// </summary>
/// <remarks>
/// The about panel is AppKit's own. It reads the name, the version, the copyright and the icon from
/// the bundle's Info.plist, so it says exactly what the Finder says about the application, and it
/// looks like the panel of every other application on the Mac.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacApplicationMenu : IApplicationMenu
{
    public void ShowAbout() =>
        ObjectiveC.WithPool(() =>
        {
            nint application = ObjectiveC.Send(
                ObjectiveC.objc_getClass("NSApplication"),
                ObjectiveC.Selector("sharedApplication"));

            // Brought to the front first: the panel belongs to whichever application is active, and
            // an application whose window is hidden is not.
            ObjectiveC.Send(application, ObjectiveC.Selector("activateIgnoringOtherApps:"), 1);
            ObjectiveC.Send(application, ObjectiveC.Selector("orderFrontStandardAboutPanel:"), 0);
            return 0;
        });
}
