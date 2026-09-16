using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// Takes the application out of the Dock while its window is closed, and puts it back when the
/// window is shown.
/// </summary>
/// <remarks>
/// The activation policy is what decides it. A regular application has a Dock icon and a menu bar;
/// an accessory one has neither and can still show windows and a status item, which is what an
/// application kept in the menu bar is. Switching back to regular activates the application as
/// well, because the menu bar of an application that became regular while inactive is not the one
/// shown until it is activated.
///
/// Must be called on the main thread, which is the user interface thread on macOS.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacDockPresence : IDockPresence
{
    // NSApplicationActivationPolicyRegular and NSApplicationActivationPolicyAccessory.
    private const nint Regular = 0;
    private const nint Accessory = 1;

    private bool? listed;

    public void SetListed(bool listed)
    {
        if (this.listed == listed)
        {
            return;
        }

        this.listed = listed;

        ObjectiveC.WithPool(() =>
        {
            nint application = ObjectiveC.Send(
                ObjectiveC.objc_getClass("NSApplication"),
                ObjectiveC.Selector("sharedApplication"));

            ObjectiveC.Send(application, ObjectiveC.Selector("setActivationPolicy:"), listed ? Regular : Accessory);

            if (listed)
            {
                ObjectiveC.Send(application, ObjectiveC.Selector("activateIgnoringOtherApps:"), 1);
            }

            return 0;
        });
    }
}
