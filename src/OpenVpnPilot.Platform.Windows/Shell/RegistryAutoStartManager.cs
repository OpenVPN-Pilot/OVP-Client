using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// Starts the application with Windows through the per user run key.
/// </summary>
/// <remarks>
/// The per user key is used rather than the machine wide one: no elevation is needed, the setting
/// belongs to the person who turned it on, and on a shared machine one account's choice does not
/// impose itself on another's.
///
/// The stored command carries a flag that starts the application hidden, because someone who wants
/// it running at logon wants the tunnels available, not a window in their way.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RegistryAutoStartManager : IAutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenVpnPilot";

    /// <summary>
    /// Command line flag the application reads to start without showing its window.
    /// </summary>
    public const string BackgroundFlag = "--background";

    private readonly ILogger<RegistryAutoStartManager> logger;

    public RegistryAutoStartManager(ILogger<RegistryAutoStartManager>? logger = null)
    {
        this.logger = logger ?? NullLogger<RegistryAutoStartManager>.Instance;
    }

    public bool IsSupported => OperatingSystem.IsWindows() && ExecutablePath is not null;

    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (UnauthorizedAccessException exception)
        {
            AutoStartLog.ReadFailed(logger, exception);
            return false;
        }
        catch (System.Security.SecurityException exception)
        {
            AutoStartLog.ReadFailed(logger, exception);
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        string? executable = ExecutablePath;

        if (executable is null)
        {
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{executable}\" {BackgroundFlag}", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            AutoStartLog.WriteFailed(logger, enabled, exception);
            return false;
        }
        catch (System.Security.SecurityException exception)
        {
            AutoStartLog.WriteFailed(logger, enabled, exception);
            return false;
        }
    }

    /// <summary>
    /// The path Windows should run. Null when it cannot be determined, in which case autostart is
    /// reported as unsupported rather than being written with a path that will not work.
    /// </summary>
    private static string? ExecutablePath
    {
        get
        {
            using Process current = Process.GetCurrentProcess();
            string? path = current.MainModule?.FileName;

            return path is { Length: > 0 } && File.Exists(path) ? path : null;
        }
    }
}

/// <summary>
/// Source generated log messages for <see cref="RegistryAutoStartManager"/>.
/// </summary>
internal static partial class AutoStartLog
{
    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Warning,
        Message = "The autostart entry could not be read.")]
    public static partial void ReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "The autostart entry could not be set to {Enabled}.")]
    public static partial void WriteFailed(ILogger logger, bool enabled, Exception exception);
}
