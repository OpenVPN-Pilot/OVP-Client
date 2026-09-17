using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// Makes this application the front one on macOS.
/// </summary>
/// <remarks>
/// Ignoring the other applications is what an accessory application needs here. The polite form
/// activates only when the system already considers this application a candidate for the front,
/// which one without a Dock icon is not, and the window then stays behind what is in front.
///
/// Must be called on the main thread, which is the user interface thread on macOS.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacApplicationActivation : IApplicationActivation
{
    public void BringToFront() =>
        ObjectiveC.WithPool(() =>
        {
            nint application = ObjectiveC.Send(
                ObjectiveC.objc_getClass("NSApplication"),
                ObjectiveC.Selector("sharedApplication"));

            ObjectiveC.Send(application, ObjectiveC.Selector("activateIgnoringOtherApps:"), 1);
            return 0;
        });
}
