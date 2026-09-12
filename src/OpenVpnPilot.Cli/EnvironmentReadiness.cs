using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Cli;

/// <summary>
/// The environment report, shared by the doctor command and by anything that is about to connect.
/// </summary>
/// <remarks>
/// Both need the same answer and both should give the same wording for it. A command that tries to
/// connect on a machine without the interactive service would otherwise fail with whatever exception
/// the pipe produced, which names a pipe and nothing a user can act on.
/// </remarks>
internal static class EnvironmentReadiness
{
    /// <summary>
    /// Where the missing part is obtained. Which part that is depends on the platform.
    /// </summary>
    public static string DownloadUrl => PlatformServices.SetupUrl;

    public static Task<OpenVpnEnvironmentReport> ProbeAsync() =>
        PlatformServices.CreateEnvironmentProbe().ProbeAsync();

    /// <summary>
    /// Writes every check, in the order they were evaluated.
    /// </summary>
    public static void WriteChecks(OpenVpnEnvironmentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (EnvironmentCheck check in report.Checks)
        {
            string marker = check.Status switch
            {
                EnvironmentCheckStatus.Passed => "ok  ",
                EnvironmentCheckStatus.Warning => "warn",
                _ => "fail",
            };

            Console.WriteLine($"  [{marker}] {check.Id,-19} {check.Detail}");
        }
    }

    /// <summary>
    /// Says what has to be installed, in the same words wherever it is said.
    /// </summary>
    /// <remarks>
    /// What is missing is not the same thing on both platforms. On Windows it is OpenVPN itself,
    /// which is installed separately and has to include the interactive service. On macOS there is
    /// no such service, so the application brings its own privileged helper and its own OpenVPN in
    /// one package, and that package is what has to be installed.
    /// </remarks>
    public static void WriteGuidance()
    {
        if (OperatingSystem.IsMacOS())
        {
            Console.WriteLine($"The helper package can be installed from {DownloadUrl}");
            Console.WriteLine("It carries the OpenVPN this application runs, and installing it asks for a password.");
            return;
        }

        Console.WriteLine($"OpenVPN Community can be installed from {DownloadUrl}");
        Console.WriteLine("Make sure the OpenVPN Interactive Service component is included.");
    }

    /// <summary>
    /// Checks the environment before a connection and explains a refusal.
    /// </summary>
    /// <returns>True when connecting is possible.</returns>
    public static async Task<bool> EnsureReadyAsync()
    {
        OpenVpnEnvironmentReport report = await ProbeAsync();

        if (report.CanConnect)
        {
            return true;
        }

        Console.Error.WriteLine("This machine cannot run a tunnel yet:");
        Console.Error.WriteLine();

        foreach (EnvironmentCheck check in report.Checks
            .Where(check => check.Status == EnvironmentCheckStatus.Failed))
        {
            Console.Error.WriteLine($"  {check.Id}: {check.Detail}");
        }

        Console.Error.WriteLine();
        WriteGuidance();
        Console.Error.WriteLine("Run 'ovp doctor' for the full report.");

        return false;
    }
}
