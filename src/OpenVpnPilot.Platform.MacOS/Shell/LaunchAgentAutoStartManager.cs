using System.Runtime.Versioning;
using System.Xml.Linq;
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
            File.WriteAllText(agentPath, BuildDefinition(arguments));
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
    /// The launchd job definition, written as a property list.
    /// </summary>
    internal static string BuildDefinition(IReadOnlyList<string> arguments)
    {
        XElement Key(string name) => new("key", name);
        XElement Text(string value) => new("string", value);

        XDocument document = new(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement(
                "plist",
                new XAttribute("version", "1.0"),
                new XElement(
                    "dict",
                    Key("Label"),
                    Text(Label),
                    Key("ProgramArguments"),
                    new XElement("array", arguments.Select(Text)),
                    Key("RunAtLoad"),
                    new XElement("true"),
                    Key("LimitLoadToSessionType"),
                    Text("Aqua"),
                    Key("ProcessType"),
                    Text("Interactive"),
                    Key("AssociatedBundleIdentifiers"),
                    new XElement("array", Text(BundleIdentifier)))));

        return document.Declaration + Environment.NewLine + document.ToString() + Environment.NewLine;
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

        string? bundle = BundleOf(executable);

        return bundle is null
            ? [executable, BackgroundFlag]
            : ["/usr/bin/open", "-g", "-a", bundle, "--args", BackgroundFlag];
    }

    /// <summary>
    /// The application bundle an executable sits in, when it sits in one.
    /// </summary>
    internal static string? BundleOf(string executable)
    {
        DirectoryInfo? macOs = new FileInfo(executable).Directory;
        DirectoryInfo? contents = macOs?.Parent;
        DirectoryInfo? bundle = contents?.Parent;

        return macOs?.Name == "MacOS"
            && contents?.Name == "Contents"
            && bundle is not null
            && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? bundle.FullName
                : null;
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
