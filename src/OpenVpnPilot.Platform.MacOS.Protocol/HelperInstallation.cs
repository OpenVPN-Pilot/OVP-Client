namespace OpenVpnPilot.Platform.MacOS.Protocol;

/// <summary>
/// Where the privileged helper and everything it runs are installed.
/// </summary>
/// <remarks>
/// Every location here is owned by root and not writable by anyone else, and that is the point of
/// having them fixed. The helper runs OpenVPN as root, so any file it executes or reads on a
/// caller's behalf has to sit where no ordinary account can replace it. OpenVPN from Homebrew lives
/// under a directory owned by the user who installed Homebrew, together with the libraries it loads
/// and the script it runs to set name servers; running that as root would hand root to anything
/// running as that user. The helper package therefore brings its own copy.
///
/// The identifiers are fixed for good. Background item approvals, the launchd job and the package
/// receipt are all recorded against them, and changing one orphans whatever was recorded.
/// </remarks>
public static class HelperInstallation
{
    /// <summary>
    /// The launchd label of the helper, and the identifier of its package.
    /// </summary>
    public const string Label = "org.openvpnpilot.helper";

    /// <summary>
    /// The socket launchd listens on for the helper. Under a directory only root can write to, so no
    /// other process can put its own socket in its place.
    /// </summary>
    public const string SocketPath = "/var/run/org.openvpnpilot.helper.sock";

    /// <summary>
    /// The helper executable.
    /// </summary>
    public const string ExecutablePath = "/Library/PrivilegedHelperTools/org.openvpnpilot.helper";

    /// <summary>
    /// The launchd job definition.
    /// </summary>
    public const string JobDefinitionPath = "/Library/LaunchDaemons/org.openvpnpilot.helper.plist";

    /// <summary>
    /// The directory the helper package installs into, beside the helper executable.
    /// </summary>
    public const string SupportDirectory = "/Library/Application Support/OpenVpnPilot";

    /// <summary>
    /// The OpenVPN build the package installs. Nothing else is ever run as root.
    /// </summary>
    public const string OpenVpnPath = SupportDirectory + "/openvpn/sbin/openvpn";

    /// <summary>
    /// The script OpenVPN ships to apply pushed name servers, installed with the helper.
    /// </summary>
    public const string DnsScriptPath = SupportDirectory + "/openvpn/libexec/dns-updown";

    /// <summary>
    /// Configurations an administrator installed. The only ones an account that is not authorised
    /// may start, which is the same rule the Windows interactive service applies to its
    /// configuration directory.
    /// </summary>
    public const string ConfigurationsDirectory = SupportDirectory + "/Configurations";

    /// <summary>
    /// Where the helper keeps its private copies of running configurations and the state it needs to
    /// undo what a tunnel changed. Root only.
    /// </summary>
    public const string RuntimeDirectory = SupportDirectory + "/runtime";

    /// <summary>
    /// The script that removes the helper again.
    /// </summary>
    public const string UninstallScriptPath = SupportDirectory + "/uninstall.sh";

    /// <summary>
    /// The group whose members may start their own configurations without being administrators.
    /// </summary>
    /// <remarks>
    /// The counterpart of the group named by the interactive service's ovpn_admin_group setting. The
    /// helper package creates it and adds the account that installed it when that account is not an
    /// administrator, so a standard account can use the application like any other program.
    /// </remarks>
    public const string AuthorisedGroup = "openvpnpilot";

    /// <summary>
    /// The group macOS gives its administrators. Addressed by number, which unlike a name cannot be
    /// shadowed by a directory service entry of the same name.
    /// </summary>
    public const uint AdministratorsGroupId = 80;
}
