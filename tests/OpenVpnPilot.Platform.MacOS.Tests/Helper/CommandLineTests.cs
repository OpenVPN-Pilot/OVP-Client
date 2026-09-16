using OpenVpnPilot.Platform.MacOS.Helper.Tunnels;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Tests.Helper;

/// <summary>
/// The command line the helper builds, and what it refuses to build one from.
/// </summary>
/// <remarks>
/// The caller never sends options, only values, so this is where a value that would change what
/// OpenVPN does has to be caught. The line itself has to carry the same options the Windows
/// launcher passes, because both platforms drive the same management protocol.
/// </remarks>
public sealed class CommandLineTests
{
    [Fact]
    public void Build_CarriesTheOptionsTheClientDrivesOpenVpnWith()
    {
        IReadOnlyList<string> arguments = CommandLine.Build("/runtime/config.ovpn", "/runtime", "/runtime/tmp", Specification());

        Assert.Equal(
            [
                "--config", "/runtime/config.ovpn",
                "--management", "127.0.0.1", "25340", "/dev/fd/3",
                "--management-query-passwords",
                "--management-hold",
                "--management-forget-disconnect",
                "--auth-retry", "interact",
                "--verb", "3",
                "--script-security", "1",
                "--cd", "/runtime",
                "--tmp-dir", "/runtime/tmp",
            ],
            arguments);
    }

    /// <summary>
    /// Script security comes after the configuration, so the configuration cannot raise it.
    /// </summary>
    [Fact]
    public void Build_SetsScriptSecurityAfterTheConfiguration()
    {
        IReadOnlyList<string> arguments = CommandLine.Build("/runtime/config.ovpn", "/runtime", "/runtime/tmp", Specification());

        Assert.True(arguments.ToList().IndexOf("--config") < arguments.ToList().IndexOf("--script-security"));
    }

    [Fact]
    public void Build_AddsEachPullFilterAsThreeArguments()
    {
        LaunchSpecification specification = Specification() with
        {
            PullFilters =
            [
                new PullFilterSpecification("ignore", "redirect-gateway"),
                new PullFilterSpecification("ignore", "dhcp-option"),
            ],
        };

        IReadOnlyList<string> arguments = CommandLine.Build("/runtime/config.ovpn", "/runtime", "/runtime/tmp", specification);

        Assert.Equal(
            ["--pull-filter", "ignore", "redirect-gateway", "--pull-filter", "ignore", "dhcp-option"],
            arguments.TakeLast(6));
    }

    [Fact]
    public void Validate_AnOrdinaryRequest_IsAccepted()
    {
        Assert.Null(CommandLine.Validate(Specification()));
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_AManagementPortOutsideTheUnprivilegedRange_IsRefused(int port)
    {
        Assert.NotNull(CommandLine.Validate(Specification() with { ManagementPort = port }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("password with spaces and more")]
    [InlineData("has\nnewline0123456789")]
    [InlineData("semi;colon0123456789")]
    public void Validate_AManagementPasswordThatIsNotOne_IsRefused(string password)
    {
        Assert.NotNull(CommandLine.Validate(Specification() with { ManagementPassword = password }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(12)]
    public void Validate_AVerbosityOutsideWhatOpenVpnKnows_IsRefused(int verbosity)
    {
        Assert.NotNull(CommandLine.Validate(Specification() with { Verbosity = verbosity }));
    }

    [Theory]
    [InlineData("delete", "redirect-gateway")]
    [InlineData("ignore", "")]
    [InlineData("ignore", "two\nlines")]
    public void Validate_APullFilterThatIsNotOne_IsRefused(string action, string text)
    {
        Assert.NotNull(CommandLine.Validate(Specification() with { PullFilters = [new PullFilterSpecification(action, text)] }));
    }

    [Fact]
    public void Validate_MorePullFiltersThanAnyClientNeeds_IsRefused()
    {
        List<PullFilterSpecification> filters =
            [.. Enumerable.Range(0, CommandLine.MaximumPullFilters + 1).Select(_ => new PullFilterSpecification("ignore", "route"))];

        Assert.NotNull(CommandLine.Validate(Specification() with { PullFilters = filters }));
    }

    /// <summary>
    /// A sender can leave out anything, and what is left out arrives as nothing rather than as the
    /// default the record declares, so the validation has to hold for a specification that is empty.
    /// </summary>
    [Fact]
    public void Validate_ASpecificationWithNothingInIt_IsRefusedRatherThanAccepted()
    {
        Assert.NotNull(CommandLine.Validate(new LaunchSpecification()));
    }

    [Fact]
    public void Validate_AConfigurationWithoutAManagementPassword_IsRefused()
    {
        Assert.NotNull(CommandLine.Validate(Specification() with { ManagementPassword = null }));
    }

    /// <summary>
    /// No filters at all is not the same as a filter that is not one.
    /// </summary>
    [Fact]
    public void Validate_NoPullFiltersAtAll_IsAccepted()
    {
        Assert.Null(CommandLine.Validate(Specification() with { PullFilters = null }));
    }

    [Fact]
    public void Build_NoPullFiltersAtAll_BuildsTheLineWithoutAny()
    {
        IReadOnlyList<string> arguments =
            CommandLine.Build("/runtime/config.ovpn", "/runtime", "/runtime/tmp", Specification() with { PullFilters = null });

        Assert.DoesNotContain("--pull-filter", arguments);
    }

    [Theory]
    [InlineData(null, "route")]
    [InlineData("ignore", null)]
    [InlineData(null, null)]
    public void Validate_APullFilterMissingItsValues_IsRefused(string? action, string? text)
    {
        LaunchSpecification specification = Specification() with
        {
            PullFilters = [new PullFilterSpecification(action!, text!)],
        };

        Assert.NotNull(CommandLine.Validate(specification));
    }

    [Fact]
    public void Validate_NeitherAConfigurationNorAnInstalledOne_IsRefused()
    {
        Assert.NotNull(CommandLine.Validate(Specification() with { Configuration = null }));
    }

    [Fact]
    public void Validate_BothAConfigurationAndAnInstalledOne_IsRefused()
    {
        Assert.NotNull(CommandLine.Validate(Specification() with { InstalledConfiguration = "site.ovpn" }));
    }

    private static LaunchSpecification Specification() => new()
    {
        Configuration = "client\n",
        ManagementPort = 25340,
        ManagementPassword = "0123456789ABCDEF",
        Verbosity = 3,
    };
}
