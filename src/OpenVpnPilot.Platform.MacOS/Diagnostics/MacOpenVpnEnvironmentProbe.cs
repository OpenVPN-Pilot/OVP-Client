using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Helper;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Diagnostics;

/// <summary>
/// Determines whether the helper package is installed and usable by the current account.
/// </summary>
/// <remarks>
/// The checks follow the same order and the same identifiers as on Windows, so the window and the
/// doctor command read them the same way; what each one looks at is the macOS counterpart. The
/// installation is the helper and its launchd job, the service is launchd having created the
/// socket, the pipe is a session that agrees on the protocol, and the executable and version are the
/// OpenVPN build the helper runs, as the helper reports it.
///
/// The probe opens a session of its own and closes it again. It never starts anything, so it can run
/// at any time without touching a tunnel.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacOpenVpnEnvironmentProbe : IOpenVpnEnvironmentProbe
{
    /// <summary>
    /// The oldest OpenVPN whose management interface matches what this client expects.
    /// </summary>
    private static readonly Version MinimumVersion = new(2, 6);

    private readonly string? setupUrl;
    private readonly string socketPath;

    /// <param name="setupUrl">Where the helper package is obtained, for the banner's button.</param>
    public MacOpenVpnEnvironmentProbe(string? setupUrl = null, string socketPath = HelperInstallation.SocketPath)
    {
        this.setupUrl = setupUrl;
        this.socketPath = socketPath;
    }

    public async Task<OpenVpnEnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        List<EnvironmentCheck> checks = [];

        if (!AddInstallationCheck(checks))
        {
            AddConflictingProductCheck(checks);
            return Report(checks);
        }

        if (!File.Exists(socketPath))
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.InteractiveService,
                EnvironmentCheckStatus.Failed,
                $"launchd has not created {socketPath}, so the job {HelperInstallation.Label} is not loaded."));

            return Report(checks);
        }

        checks.Add(new EnvironmentCheck(
            EnvironmentCheckId.InteractiveService,
            EnvironmentCheckStatus.Passed,
            $"{HelperInstallation.Label} is loaded"));

        HelperResponse greeting;

        try
        {
            await using HelperConnection connection = await HelperConnection.OpenAsync(
                HelperSession.ClientName,
                socketPath,
                cancellationToken);

            greeting = connection.Greeting;
        }
        catch (Exception exception) when (exception is HelperUnavailableException or HelperProtocolException)
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.ServicePipe,
                EnvironmentCheckStatus.Failed,
                exception.Message));

            return Report(checks);
        }

        checks.Add(new EnvironmentCheck(
            EnvironmentCheckId.ServicePipe,
            EnvironmentCheckStatus.Passed,
            $"Helper {greeting.HelperVersion ?? "unknown"}, protocol {greeting.ProtocolVersion}"));

        AddOpenVpnChecks(checks, greeting);
        AddAuthorisationCheck(checks, greeting);

        return Report(checks);
    }

    private OpenVpnEnvironmentReport Report(List<EnvironmentCheck> checks) => new(checks, setupUrl);

    private static bool AddInstallationCheck(List<EnvironmentCheck> checks)
    {
        bool executable = File.Exists(HelperInstallation.ExecutablePath);
        bool job = File.Exists(HelperInstallation.JobDefinitionPath);

        if (executable && job)
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.Installation,
                EnvironmentCheckStatus.Passed,
                HelperInstallation.ExecutablePath));

            return true;
        }

        string missing = (executable, job) switch
        {
            (false, false) => $"Neither {HelperInstallation.ExecutablePath} nor {HelperInstallation.JobDefinitionPath} exists.",
            (false, true) => $"{HelperInstallation.ExecutablePath} does not exist.",
            _ => $"{HelperInstallation.JobDefinitionPath} does not exist.",
        };

        checks.Add(new EnvironmentCheck(EnvironmentCheckId.Installation, EnvironmentCheckStatus.Failed, missing));
        return false;
    }

    private static void AddOpenVpnChecks(List<EnvironmentCheck> checks, HelperResponse greeting)
    {
        if (string.IsNullOrEmpty(greeting.OpenVpnVersion))
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.Executable,
                EnvironmentCheckStatus.Failed,
                $"The helper finds no OpenVPN at {greeting.OpenVpnPath ?? HelperInstallation.OpenVpnPath}."));

            return;
        }

        checks.Add(new EnvironmentCheck(
            EnvironmentCheckId.Executable,
            EnvironmentCheckStatus.Passed,
            greeting.OpenVpnPath ?? HelperInstallation.OpenVpnPath));

        if (!Version.TryParse(greeting.OpenVpnVersion, out Version? version))
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.Version,
                EnvironmentCheckStatus.Warning,
                $"The OpenVPN version '{greeting.OpenVpnVersion}' could not be read."));

            return;
        }

        checks.Add(version >= MinimumVersion
            ? new EnvironmentCheck(EnvironmentCheckId.Version, EnvironmentCheckStatus.Passed, version.ToString())
            : new EnvironmentCheck(
                EnvironmentCheckId.Version,
                EnvironmentCheckStatus.Failed,
                $"OpenVPN {version} is older than the required {MinimumVersion}."));
    }

    // Being unauthorised is not fatal: configurations an administrator installed still work. It only
    // blocks running profiles from the application's own store, as on Windows.
    private static void AddAuthorisationCheck(List<EnvironmentCheck> checks, HelperResponse greeting)
    {
        checks.Add(greeting.Authorised
            ? new EnvironmentCheck(
                EnvironmentCheckId.Authorisation,
                EnvironmentCheckStatus.Passed,
                $"Authorised as {greeting.Authorisation ?? "a member of an authorised group"}.")
            : new EnvironmentCheck(
                EnvironmentCheckId.Authorisation,
                EnvironmentCheckStatus.Warning,
                $"Not an administrator and not in the group '{HelperInstallation.AuthorisedGroup}', so "
                + $"profiles must be installed under {HelperInstallation.ConfigurationsDirectory}."));
    }

    // OpenVPN Connect is a different product built on a different core. It cannot be driven through
    // the management interface, and people understandably assume it counts as OpenVPN installed.
    private static void AddConflictingProductCheck(List<EnvironmentCheck> checks)
    {
        if (Directory.Exists("/Applications/OpenVPN Connect.app")
            || Directory.Exists("/Applications/OpenVPN Connect/OpenVPN Connect.app"))
        {
            checks.Add(new EnvironmentCheck(
                EnvironmentCheckId.ConflictingProduct,
                EnvironmentCheckStatus.Warning,
                "OpenVPN Connect is installed. It is a separate product and cannot be used as the backend."));
        }
    }
}
