using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Platform.MacOS.Diagnostics;
using OpenVpnPilot.Platform.MacOS.Helper;
using OpenVpnPilot.Platform.MacOS.Runtime;
using OpenVpnPilot.Platform.MacOS.Security;
using OpenVpnPilot.Platform.Windows.Diagnostics;
using OpenVpnPilot.Platform.Windows.InteractiveService;
using OpenVpnPilot.Platform.Windows.Runtime;
using OpenVpnPilot.Platform.Windows.Security;

namespace OpenVpnPilot.Cli;

/// <summary>
/// The one place this command decides which platform it is running on.
/// </summary>
/// <remarks>
/// The counterpart of what <c>AppHost</c> does for the application, and deliberately the same shape:
/// every capability that depends on the operating system is asked for through its interface, and the
/// choice is made once. A command that reached for a platform type directly would have to be
/// rewritten for the next platform, which is the thing the interfaces exist to prevent.
/// </remarks>
internal static class PlatformServices
{
    /// <summary>
    /// Whether this build can do anything on the system it was started on.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Said when it cannot, in the same words the application uses.
    /// </summary>
    public const string UnsupportedMessage =
        "This build supports Windows and macOS. Support for another system means adding an "
        + "implementation of the platform interfaces, not changing the rest of the application.";

    /// <summary>
    /// Where the part that is missing comes from, for a report that says what to do next.
    /// </summary>
    /// <remarks>
    /// On macOS that is the helper package, which is a release of this project, and the repository it
    /// comes from is the one the update check reads. On Windows OpenVPN itself is what can be
    /// missing, and that comes from the OpenVPN project.
    /// </remarks>
    public static string SetupUrl => OperatingSystem.IsMacOS()
        ? $"https://github.com/{new AdvancedSettings().UpdateRepository}/releases/latest"
        : "https://openvpn.net/community-downloads/";

    public static IOpenVpnEnvironmentProbe CreateEnvironmentProbe()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsProbe();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOpenVpnEnvironmentProbe(SetupUrl);
        }

        throw new PlatformNotSupportedException(UnsupportedMessage);
    }

    public static IProfileMaterializer CreateMaterializer(IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (OperatingSystem.IsWindows())
        {
            return WindowsMaterializer();
        }

        if (OperatingSystem.IsMacOS())
        {
            // The same directory the application materialises into, so both write in one place.
            return new MacProfileMaterializer(Path.Combine(paths.DataDirectory, "runtime"));
        }

        throw new PlatformNotSupportedException(UnsupportedMessage);
    }

    public static ISecretStore CreateSecretStore(IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (OperatingSystem.IsWindows())
        {
            return WindowsSecrets(paths.SecretsDirectory);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new KeychainSecretStore();
        }

        throw new PlatformNotSupportedException(UnsupportedMessage);
    }

    /// <summary>
    /// A launcher, together with what has to be disposed when the connection is over.
    /// </summary>
    /// <remarks>
    /// On macOS the launcher talks to the privileged helper over a session, and that session owns the
    /// tunnels it started: closing it ends them. This command therefore has to keep it open for as
    /// long as it holds the tunnel and close it afterwards, which is why the lifetime is handed back
    /// rather than left to a finaliser.
    /// </remarks>
    public static PlatformLauncher CreateLauncher()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PlatformLauncher(WindowsLauncher(), null, null);
        }

        if (OperatingSystem.IsMacOS())
        {
            // Names itself after this assembly, so the helper's log says which side asked.
            HelperSession session = new();

            return new PlatformLauncher(
                new MacOpenVpnLauncher(session),
                new HelperProcessTerminator(session),
                session);
        }

        throw new PlatformNotSupportedException(UnsupportedMessage);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsOpenVpnEnvironmentProbe WindowsProbe() =>
        new(new InteractiveServicePipeClient());

    [SupportedOSPlatform("windows")]
    private static WindowsProfileMaterializer WindowsMaterializer() => new();

    [SupportedOSPlatform("windows")]
    private static DpapiSecretStore WindowsSecrets(string directory) => new(directory);

    [SupportedOSPlatform("windows")]
    private static WindowsOpenVpnLauncher WindowsLauncher() => new(new InteractiveServicePipeClient());
}

/// <summary>
/// What it takes to start OpenVPN on this platform, and what has to be let go of afterwards.
/// </summary>
/// <param name="Launcher">Starts the process.</param>
/// <param name="Terminator">Ends it, when the platform ends it other than by process identifier.</param>
/// <param name="Lifetime">Held open for as long as the tunnel is wanted, then disposed.</param>
internal sealed record PlatformLauncher(
    IOpenVpnLauncher Launcher,
    IOpenVpnProcessTerminator? Terminator,
    IAsyncDisposable? Lifetime) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        if (Lifetime is not null)
        {
            await Lifetime.DisposeAsync();
        }
    }
}
