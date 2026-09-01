using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// Gives the process the identity Windows uses to attribute its notifications.
/// </summary>
/// <remarks>
/// A notification area balloon is not shown as a balloon on Windows 10 and later. The shell turns it
/// into a toast, files it in the notification centre and labels it with the calling process's
/// application user model identity. A process that has never declared one is given a generated
/// identity instead, which is what put <c>Microsoft.Explorer.Notification{...}</c> above every
/// message this application sent.
///
/// Two things are needed and both are cheap. The identity has to be set on the process before
/// anything shows a notification, and it has to be registered so the shell can look up a name and an
/// icon for it. The registration lives under the current user, because the application may well be
/// installed without administrative rights and because the identity belongs to the user's session.
///
/// The installer additionally stamps the same identity onto the start menu shortcut. That is what
/// lets Windows associate the running process with an installed application rather than with a bare
/// identifier, and it is why the identifier must never change once released.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsAppIdentity
{
    /// <summary>
    /// The identity, in the form Windows expects. Fixed for the life of the application: changing it
    /// orphans every notification setting the user made against the old one.
    /// </summary>
    public const string ApplicationUserModelId = "OpenVpnPilot";

    private const string RegistryPath = @"Software\Classes\AppUserModelId\" + ApplicationUserModelId;

    /// <summary>
    /// Declares the identity and makes sure the shell can resolve it to a name and an icon.
    /// </summary>
    /// <param name="displayName">The name the notification centre shows above a message.</param>
    /// <returns>True when the identity was set on the process.</returns>
    /// <remarks>
    /// Reports rather than raises. Running without a registered identity means notifications are
    /// labelled badly, which is not a reason to refuse to start.
    /// </remarks>
    public static bool Apply(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        Register(displayName);

        return NativeMethods.SetCurrentProcessExplicitAppUserModelID(ApplicationUserModelId) == 0;
    }

    /// <summary>
    /// Writes the name and icon the shell shows for this identity.
    /// </summary>
    private static void Register(string displayName)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);

            key.SetValue("DisplayName", displayName, RegistryValueKind.String);

            if (IconPath() is { } icon)
            {
                key.SetValue("IconUri", icon, RegistryValueKind.String);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // The identity is still set on the process, so the notifications are still grouped
            // correctly. Only the name and icon fall back to what the shell can work out itself.
        }
    }

    /// <summary>
    /// The icon file beside the executable, or the executable itself.
    /// </summary>
    /// <remarks>
    /// The shell reads an image file here. A published build ships the icon next to the binary, and
    /// the executable is named only as a fallback for a build that does not.
    /// </remarks>
    private static string? IconPath()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "openvpnpilot.ico");

        if (File.Exists(beside))
        {
            return beside;
        }

        using Process current = Process.GetCurrentProcess();
        return current.MainModule?.FileName;
    }
}
