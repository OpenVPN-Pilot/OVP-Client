using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.Windows.InteractiveService;
using OpenVpnPilot.Platform.Windows.Security;

namespace OpenVpnPilot.Platform.Windows.Environment;

/// <summary>
/// Determines whether OpenVPN Community is installed and usable by the current user.
/// </summary>
/// <remarks>
/// The checks are deliberately fine grained. A user who has OpenVPN Connect installed reasonably
/// believes OpenVPN is present, so that case is detected and reported on its own rather than as a
/// missing installation.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsOpenVpnEnvironmentProbe : IOpenVpnEnvironmentProbe
{
    private const string RegistryPath = @"SOFTWARE\OpenVPN";
    private const string InteractiveServiceName = "OpenVPNServiceInteractive";
    private const string DefaultAdminGroup = "OpenVPN Administrators";

    /// <summary>
    /// The oldest release whose management interface and service protocol match what this client expects.
    /// </summary>
    private static readonly Version MinimumVersion = new(2, 6);

    /// <summary>
    /// Well known SID of the local Administrators group. Resolving by name fails on localized
    /// installations, where the group is called something else entirely.
    /// </summary>
    private static readonly SecurityIdentifier LocalAdministrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);

    private readonly InteractiveServicePipeClient pipeClient;

    public WindowsOpenVpnEnvironmentProbe(InteractiveServicePipeClient pipeClient)
    {
        ArgumentNullException.ThrowIfNull(pipeClient);
        this.pipeClient = pipeClient;
    }

    public Task<OpenVpnEnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        List<EnvironmentCheck> checks = [];

        OpenVpnInstallation? installation = ReadInstallation();

        if (installation is null)
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.Installation,
                EnvironmentCheckStatus.Failed,
                $@"HKLM\{RegistryPath} does not describe an OpenVPN Community installation."));

            AddConflictingProductCheck(checks);
            return Task.FromResult(new OpenVpnEnvironmentReport(checks));
        }

        checks.Add(new EnvironmentCheck(
            EnvironmentCheckId.Installation,
            EnvironmentCheckStatus.Passed,
            $"Configuration directory {installation.ConfigDirectory}"));

        AddExecutableChecks(checks, installation);
        AddServiceChecks(checks);
        AddAuthorisationCheck(checks, installation);

        return Task.FromResult(new OpenVpnEnvironmentReport(checks));
    }

    private static OpenVpnInstallation? ReadInstallation()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath);

        // OpenVPN Connect also writes this key, but only the Community installer sets exe_path.
        if (key?.GetValue("exe_path") is not string executablePath || executablePath.Length == 0)
        {
            return null;
        }

        return new OpenVpnInstallation(
            executablePath,
            key.GetValue("config_dir") as string ?? string.Empty,
            key.GetValue("ovpn_admin_group") as string ?? DefaultAdminGroup);
    }

    private static void AddExecutableChecks(List<EnvironmentCheck> checks, OpenVpnInstallation installation)
    {
        if (!File.Exists(installation.ExecutablePath))
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.Executable,
                EnvironmentCheckStatus.Failed,
                $"The registry points at {installation.ExecutablePath}, which does not exist."));
            return;
        }

        checks.Add(new EnvironmentCheck(
            EnvironmentCheckId.Executable,
            EnvironmentCheckStatus.Passed,
            installation.ExecutablePath));

        Version? version = ReadVersion(installation.ExecutablePath);

        if (version is null)
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.Version,
                EnvironmentCheckStatus.Warning,
                "The OpenVPN version could not be determined."));
            return;
        }

        checks.Add(version >= MinimumVersion
            ? new EnvironmentCheck(EnvironmentCheckId.Version, EnvironmentCheckStatus.Passed, version.ToString())
            : new EnvironmentCheck(
                EnvironmentCheckId.Version,
                EnvironmentCheckStatus.Failed,
                $"OpenVPN {version} is older than the required {MinimumVersion}."));
    }

    private static Version? ReadVersion(string executablePath)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
            return info.FileMajorPart == 0 && info.FileMinorPart == 0
                ? null
                : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private void AddServiceChecks(List<EnvironmentCheck> checks)
    {
        ServiceControllerStatus? status = ReadServiceStatus();

        if (status is null)
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.InteractiveService,
                EnvironmentCheckStatus.Failed,
                $"The {InteractiveServiceName} service is not installed."));
        }
        else if (status != ServiceControllerStatus.Running)
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.InteractiveService,
                EnvironmentCheckStatus.Failed,
                $"The {InteractiveServiceName} service is {status}."));
        }
        else
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.InteractiveService,
                EnvironmentCheckStatus.Passed,
                "Running"));
        }

        checks.Add(pipeClient.IsAvailable()
            ? new EnvironmentCheck(EnvironmentCheckId.ServicePipe, EnvironmentCheckStatus.Passed, "Reachable")
            : new EnvironmentCheck(
                EnvironmentCheckId.ServicePipe,
                EnvironmentCheckStatus.Failed,
                "The interactive service control pipe could not be opened."));
    }

    private static ServiceControllerStatus? ReadServiceStatus()
    {
        try
        {
            using ServiceController controller = new(InteractiveServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Being unauthorised is not fatal: configurations inside the OpenVPN configuration directory
    // still work. It only blocks running profiles from the application's own store.
    private static void AddAuthorisationCheck(List<EnvironmentCheck> checks, OpenVpnInstallation installation)
    {
        // Membership is evaluated against the unfiltered token, because that is what the service uses.
        if (WindowsAuthorisation.IsEffectivelyInGroup(LocalAdministrators))
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.Authorisation,
                EnvironmentCheckStatus.Passed,
                "Authorised through the local Administrators group."));
            return;
        }

        SecurityIdentifier? adminGroup = ResolveGroup(installation.AdminGroup);

        if (adminGroup is null)
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.Authorisation,
                EnvironmentCheckStatus.Warning,
                $"The group '{installation.AdminGroup}' does not exist, so profiles must live under "
                + installation.ConfigDirectory));
            return;
        }

        checks.Add(WindowsAuthorisation.IsEffectivelyInGroup(adminGroup)
            ? new EnvironmentCheck(
                EnvironmentCheckId.Authorisation,
                EnvironmentCheckStatus.Passed,
                $"Authorised through the group '{installation.AdminGroup}'.")
            : new EnvironmentCheck(
                EnvironmentCheckId.Authorisation,
                EnvironmentCheckStatus.Warning,
                $"Not a member of '{installation.AdminGroup}', so profiles must live under "
                + installation.ConfigDirectory));
    }

    private static SecurityIdentifier? ResolveGroup(string groupName)
    {
        try
        {
            NTAccount account = new(System.Environment.MachineName, groupName);
            return (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    // OpenVPN Connect is a different product built on a different core. It cannot be driven through
    // the management interface, and users understandably assume it counts as "OpenVPN installed".
    private static void AddConflictingProductCheck(List<EnvironmentCheck> checks)
    {
        bool connectPresent = ReadServiceStatusFor("agent_ovpnconnect") is not null
            || ReadServiceStatusFor("ovpnhelper_service") is not null;

        if (connectPresent)
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.ConflictingProduct,
                EnvironmentCheckStatus.Warning,
                "OpenVPN Connect is installed. It is a separate product and cannot be used as the backend."));
        }
    }

    private static ServiceControllerStatus? ReadServiceStatusFor(string serviceName)
    {
        try
        {
            using ServiceController controller = new(serviceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record OpenVpnInstallation(string ExecutablePath, string ConfigDirectory, string AdminGroup);
}
