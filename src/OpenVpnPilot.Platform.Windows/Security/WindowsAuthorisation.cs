using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenVpnPilot.Platform.Windows.Security;

/// <summary>
/// Answers whether the current user belongs to a group, as the interactive service sees it.
/// </summary>
/// <remarks>
/// With User Account Control enabled, the process token of an administrator does not contain the
/// Administrators SID at all, so <see cref="WindowsPrincipal.IsInRole(SecurityIdentifier)"/> reports
/// false for a user the service will happily authorise. Windows keeps the unfiltered token as the
/// linked token, and that is what has to be inspected to match the service's decision.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class WindowsAuthorisation
{
    private const int TokenLinkedToken = 19;

    /// <summary>
    /// True when the user is a member of the group, whether or not the current token is elevated.
    /// </summary>
    public static bool IsEffectivelyInGroup(SecurityIdentifier group)
    {
        ArgumentNullException.ThrowIfNull(group);

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        if (new WindowsPrincipal(identity).IsInRole(group))
        {
            return true;
        }

        // The process token may be a filtered one. The linked token carries the full membership.
        using WindowsIdentity? linked = TryOpenLinkedIdentity(identity);
        return linked is not null && new WindowsPrincipal(linked).IsInRole(group);
    }

    /// <summary>
    /// True when the process token itself carries the group, meaning no elevation is involved.
    /// </summary>
    public static bool IsInGroupWithoutElevation(SecurityIdentifier group)
    {
        ArgumentNullException.ThrowIfNull(group);

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(group);
    }

    private static WindowsIdentity? TryOpenLinkedIdentity(WindowsIdentity identity)
    {
        nint buffer = Marshal.AllocHGlobal(nint.Size);

        try
        {
            if (!GetTokenInformation(identity.AccessToken, TokenLinkedToken, buffer, nint.Size, out int _))
            {
                // A standard user has no linked token, which is a legitimate answer rather than a fault.
                return null;
            }

            nint linkedToken = Marshal.ReadIntPtr(buffer);
            if (linkedToken == nint.Zero)
            {
                return null;
            }

            try
            {
                return new WindowsIdentity(linkedToken);
            }
            finally
            {
                CloseHandle(linkedToken);
            }
        }
        catch (Win32Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
