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
/// The bundle declares LSUIElement, so the process starts as an accessory one and the first thing
/// that puts it in the Dock is a window of its own. Starting regular and dropping out afterwards
/// leaves the Dock with an entry that outlives the process, which is in the Info.plist next to the
/// key.
///
/// What the policy is, is read rather than remembered. A remembered value and the policy the
/// application actually has drift apart as soon as anything else sets it, and the one that is
/// wrong is then the one nothing corrects.
///
/// Must be called on the main thread, which is the user interface thread on macOS.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacDockPresence : IDockPresence
{
    // NSApplicationActivationPolicyRegular and NSApplicationActivationPolicyAccessory.
    private const nint Regular = 0;
    private const nint Accessory = 1;

    public void SetListed(bool listed)
    {
        ObjectiveC.WithPool(() =>
        {
            nint application = ObjectiveC.Send(
                ObjectiveC.objc_getClass("NSApplication"),
                ObjectiveC.Selector("sharedApplication"));

            nint wanted = listed ? Regular : Accessory;

            if (ObjectiveC.Send(application, ObjectiveC.Selector("activationPolicy")) == wanted)
            {
                return 0;
            }

            ObjectiveC.Send(application, ObjectiveC.Selector("setActivationPolicy:"), wanted);

            if (listed)
            {
                ObjectiveC.Send(application, ObjectiveC.Selector("activateIgnoringOtherApps:"), 1);
            }

            return 0;
        });
    }
}
