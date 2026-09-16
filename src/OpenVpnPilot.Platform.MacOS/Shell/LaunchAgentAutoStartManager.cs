using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// Opens the application at login through a launch agent in the user's own library.
/// </summary>
/// <remarks>
/// The counterpart of the per user run key on Windows: no administrator rights, the setting belongs
/// to the account that turned it on, and another account on the same Mac is not affected.
///
/// A login item registered through the service management interface would be the other way, and it
/// was measured: without a Developer ID the registration succeeds but stays waiting for approval in
/// System Settings, so turning the setting on would look like it worked and do nothing. A launch
/// agent takes effect at the next login. macOS lists it under Login Items, Allow in the Background,
/// attributed to this application, where it can also be switched off.
///
/// The agent asks Launch Services to open the application rather than starting the executable
/// itself, so the application is launched the way a double click launches it and is not a process
/// that launchd supervises and might end.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class LaunchAgentAutoStartManager : IAutoStartManager
{
    /// <summary>
    /// The agent's label, which is also its file name. Fixed, because macOS records approvals and
    /// switches against it.
    /// </summary>
    public const string Label = "org.openvpnpilot.app.login";

    /// <summary>
    /// Command line flag the application reads to start without showing its window.
    /// </summary>
    public const string BackgroundFlag = "--background";

    /// <summary>
    /// The bundle identifier the agent is attributed to in System Settings.
    /// </summary>
    private const string BundleIdentifier = "org.openvpnpilot.app";

    private readonly string agentPath;
    private readonly ILogger<LaunchAgentAutoStartManager> logger;

    public LaunchAgentAutoStartManager(
        ILogger<LaunchAgentAutoStartManager>? logger = null,
        string? agentDirectory = null)
    {
        this.logger = logger ?? NullLogger<LaunchAgentAutoStartManager>.Instance;

        agentPath = Path.Combine(
            agentDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "LaunchAgents"),
            Label + ".plist");
    }

    public bool IsSupported => OperatingSystem.IsMacOS() && ProgramArguments() is not null;

    public bool IsEnabled() => File.Exists(agentPath);

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                // The agent is not unloaded: it has done its work at login, and unloading a job whose
                // process might be this one is not a risk worth taking to save a file on the next boot.
                File.Delete(agentPath);
                return true;
            }

            if (ProgramArguments() is not { } arguments)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
            File.WriteAllText(agentPath, LoginAgent.BuildDefinition(Label, BundleIdentifier, arguments));
            return true;
        }
        catch (IOException exception)
        {
            AutoStartLog.WriteFailed(logger, enabled, exception);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            AutoStartLog.WriteFailed(logger, enabled, exception);
            return false;
        }
    }

    /// <summary>
    /// What launchd should run at login, or null when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// Inside an application bundle that is open with the bundle and the flag, which is how Finder
    /// would open it. A build run from the source tree has no bundle, so the executable is named
    /// directly, which is what the Windows implementation does in every case.
    /// </remarks>
    private static string[]? ProgramArguments()
    {
        string? executable = Environment.ProcessPath;

        if (executable is not { Length: > 0 } || !File.Exists(executable))
        {
            return null;
        }

        string? bundle = LoginAgent.BundleOf(executable);

        return bundle is null
            ? [executable, BackgroundFlag]
            : ["/usr/bin/open", "-g", "-a", bundle, "--args", BackgroundFlag];
    }

}

/// <summary>
/// Source generated log messages for <see cref="LaunchAgentAutoStartManager"/>.
/// </summary>
internal static partial class AutoStartLog
{
    [LoggerMessage(
        EventId = 5500,
        Level = LogLevel.Warning,
        Message = "The login agent could not be set to {Enabled}.")]
    public static partial void WriteFailed(ILogger logger, bool enabled, Exception exception);
}
