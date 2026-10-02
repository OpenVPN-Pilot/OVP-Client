using System.Reflection;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// The version this client reports to a server and compares with what the server requires.
/// </summary>
public interface IClientVersionProvider
{
    /// <summary>
    /// The version to three parts, such as 1.9.0.
    /// </summary>
    public Version Version { get; }
}

/// <summary>
/// The installation's own identifier, generated once and kept for good.
/// </summary>
/// <remarks>
/// Every token the server issues is bound to it, so a token copied to another machine is worthless.
/// The value must therefore never change for an installation, and never be per user or per server.
/// </remarks>
public interface IInstallationIdProvider
{
    /// <summary>
    /// False while there is no identity to send, which is asked before <see cref="InstallationId"/>.
    /// </summary>
    /// <remarks>
    /// Inventing one would introduce this installation to the server as a stranger, so a call that
    /// needs it is not made at all and answers <see cref="ServerOutcome.IdentityUnavailable"/>.
    /// </remarks>
    public bool IsAvailable { get; }

    /// <exception cref="InvalidOperationException">There is none, see <see cref="IsAvailable"/>.</exception>
    public Guid InstallationId { get; }
}

/// <summary>
/// Raised by <see cref="PilotHeadersHandler"/> for a call that needs the installation identity while
/// there is none, and turned into <see cref="ServerOutcome.IdentityUnavailable"/> by the transport.
/// </summary>
internal sealed class InstallationIdentityUnavailableException()
    : InvalidOperationException("There is no installation identity, so no call that needs one is made.");

/// <summary>
/// Reads the version from an assembly, to three parts.
/// </summary>
/// <remarks>
/// Three parts for the same reason the update check uses three: an assembly version always carries a
/// revision and a release never does, and the server compares the numbers of a version such as
/// 1.9.0. The application passes its own assembly; every assembly of this repository carries the
/// same version, so which one is passed does not change the answer.
/// </remarks>
public sealed class AssemblyClientVersionProvider : IClientVersionProvider
{
    public AssemblyClientVersionProvider(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        Version version = assembly.GetName().Version ?? new Version(0, 0, 0);
        Version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    public Version Version { get; }
}

/// <summary>
/// Names the platform the way the contract's <c>X-Pilot-Platform</c> header does.
/// </summary>
public static class ClientPlatform
{
    public const string Windows = "windows";
    public const string MacOS = "macos";
    public const string Linux = "linux";

    /// <summary>
    /// The platform this process runs on.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">A platform the contract has no name for.</exception>
    public static string Current =>
        OperatingSystem.IsWindows() ? Windows
        : OperatingSystem.IsMacOS() ? MacOS
        : OperatingSystem.IsLinux() ? Linux
        : throw new PlatformNotSupportedException("The server knows only windows, macos and linux.");
}
