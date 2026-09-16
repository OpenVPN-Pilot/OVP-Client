using System.Text.RegularExpressions;
using OpenVpnPilot.Platform.MacOS.Helper.Policy;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Tests.Helper;

/// <summary>
/// Holds the code and the build of the privileged parts to the same numbers and paths.
/// </summary>
/// <remarks>
/// Two decisions about a root process live in two places each, and neither place can tell on its own
/// that the other moved. The configuration policy was written against what one release of OpenVPN
/// does with an option, so the release the package builds has to be that one. The name server hook is
/// compiled into that build as a path, so the path the helper is installed at has to be that path.
/// These tests read the build script and compare, which is the only way a change to one of them is
/// noticed by anything other than a tunnel that quietly stops setting name servers.
/// </remarks>
public sealed class InstallationPinTests
{
    private static readonly string BuildScript = ReadBuildScript();

    [Fact]
    public void ThePolicyWasAuditedAgainstTheVersionThePackageInstalls()
    {
        Assert.Equal(HelperInstallation.OpenVpnVersion, ConfigurationPolicy.AuditedOpenVpnVersion);
    }

    [Fact]
    public void TheBuildScriptBuildsTheVersionThePolicyWasAuditedAgainst()
    {
        Assert.Equal(
            ConfigurationPolicy.AuditedOpenVpnVersion,
            Value("OPENVPN_VERSION"));
    }

    /// <summary>
    /// The path is compiled into OpenVPN, so a change here without a rebuild leaves a binary calling
    /// a hook that is no longer installed, and name servers stop being applied with no error.
    /// </summary>
    [Fact]
    public void TheBuildCompilesInTheHookPathTheHelperIsInstalledAt()
    {
        Assert.Equal(HelperInstallation.DnsHookPath, Value("SCRIPT_DIRECTORY") + "/dns-updown");
    }

    /// <summary>
    /// A space would have to survive a Makefile and a compiler command line to reach the binary.
    /// </summary>
    [Fact]
    public void TheHookPathHasNoSpaceInIt()
    {
        Assert.DoesNotContain(' ', HelperInstallation.DnsHookPath);
    }

    private static string Value(string name)
    {
        Match match = Regex.Match(
            BuildScript,
            $@"^readonly\s+{Regex.Escape(name)}='(?<value>[^']*)'",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"The build script has no {name} to compare against.");

        return match.Groups["value"].Value;
    }

    /// <summary>
    /// Found by walking up from the test assembly, because a test runs from its output directory.
    /// </summary>
    private static string ReadBuildScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "installer", "build-openvpn-macos.sh");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("installer/build-openvpn-macos.sh was not found above the test assembly.");
    }
}
