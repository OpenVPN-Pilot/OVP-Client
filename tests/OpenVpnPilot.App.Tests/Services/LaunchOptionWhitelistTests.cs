using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.Windows.InteractiveService;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Keeps the command line inside the option whitelist the interactive service applies.
/// </summary>
/// <remarks>
/// A caller the service does not authorise may only pass options from a fixed list, and may only
/// name a configuration inside the OpenVPN configuration directory. Every measurement so far ran
/// under a local administrator, who is authorised and may pass anything, so that branch is read from
/// the OpenVPN sources and has never been observed. It needs a standard user account and this test
/// is not a substitute for one.
///
/// What it does do is stop the constraint from being broken by accident. An option added for a good
/// reason on an administrator's machine would work there and fail on every machine where it matters,
/// and nothing else in the build would notice.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LaunchOptionWhitelistTests
{
    /// <summary>
    /// The options the interactive service accepts from a caller it has not authorised.
    /// </summary>
    /// <remarks>
    /// Taken from the OpenVPN interactive service sources and recorded in CLAUDE.md. Adding to this
    /// list means checking the sources again, not making room for the option that failed.
    /// </remarks>
    private static readonly HashSet<string> Whitelist = new(StringComparer.Ordinal)
    {
        "auth-retry",
        "config",
        "log",
        "log-append",
        "management",
        "management-forget-disconnect",
        "management-hold",
        "management-query-passwords",
        "management-query-proxy",
        "management-signal",
        "management-up-down",
        "mute",
        "setenv",
        "service",
        "verb",
        "pull-filter",
        "script-security",
    };

    /// <summary>
    /// The pull filters the application adds when a profile protects its routes.
    /// </summary>
    private static readonly string[] RouteProtection =
    [
        "--pull-filter ignore \"redirect-gateway\"",
        "--pull-filter ignore \"dhcp-option\"",
        "--pull-filter ignore \"block-outside-dns\"",
    ];

    [Fact]
    public void EveryOptionAConnectionUses_IsOneTheServiceWouldAccept()
    {
        string line = WindowsOpenVpnLauncher.BuildCommandLine(new OpenVpnLaunchRequest(
            @"C:\ProgramData\OpenVpnPilot\runtime\example.ovpn",
            @"C:\ProgramData\OpenVpnPilot\runtime",
            25340,
            "3E6F1A2B4C5D6E7F",
            RouteProtection,
            @"C:\ProgramData\OpenVpnPilot\runtime\example.log"));

        string[] refused = OptionsIn(line).Where(option => !Whitelist.Contains(option)).ToArray();

        Assert.True(
            refused.Length == 0,
            "The interactive service refuses these from a caller it has not authorised: "
                + string.Join(", ", refused));
    }

    [Fact]
    public void TheConnectionAlwaysSpeaksThroughTheManagementInterface()
    {
        string line = WindowsOpenVpnLauncher.BuildCommandLine(new OpenVpnLaunchRequest(
            @"C:\runtime\example.ovpn",
            @"C:\runtime",
            25340,
            "3E6F1A2B4C5D6E7F",
            []));

        // Held until released and asked for its passwords, which is what the client relies on: a
        // tunnel that comes up before the client has attached cannot be driven or authenticated.
        Assert.Contains("--management 127.0.0.1 25340 stdin", line, StringComparison.Ordinal);
        Assert.Contains("--management-hold", line, StringComparison.Ordinal);
        Assert.Contains("--management-query-passwords", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The management password is passed on standard input and must never reach the command line,
    /// where any other process could read it out of the process list.
    /// </summary>
    [Fact]
    public void TheManagementPassword_IsNotOnTheCommandLine()
    {
        const string password = "3E6F1A2B4C5D6E7F";

        string line = WindowsOpenVpnLauncher.BuildCommandLine(new OpenVpnLaunchRequest(
            @"C:\runtime\example.ovpn",
            @"C:\runtime",
            25340,
            password,
            []));

        Assert.DoesNotContain(password, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The option names in a command line, without their values.
    /// </summary>
    private static IEnumerable<string> OptionsIn(string line) =>
        Regex.Matches(line, @"(?<![\w""])--([a-z0-9-]+)", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups[1].Value);
}
