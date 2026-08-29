using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Ipc;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The command line is the interface other software drives the application through.
/// </summary>
/// <remarks>
/// A session manager that brings a tunnel up before opening a remote desktop calls the same
/// executable a person does, so what it accepts and what it refuses is part of the contract rather
/// than a convenience.
/// </remarks>
public sealed class StartupOptionsTests
{
    [Fact]
    public void NoArguments_MeansAnOrdinaryStart()
    {
        StartupOptions options = StartupOptions.Parse([]);

        Assert.Null(options.Error);
        Assert.False(options.Headless);
        Assert.False(options.StartsHidden);
        Assert.False(options.HasActions);
        Assert.Empty(options.ToCommands());
    }

    [Fact]
    public void Headless_StartsHiddenAndCarriesTheProfilesToConnect()
    {
        StartupOptions options = StartupOptions.Parse(
            ["--headless", "--connect", "site-alpha", "--connect", "site-beta"]);

        Assert.Null(options.Error);
        Assert.True(options.Headless);
        Assert.True(options.StartsHidden);
        Assert.Equal(["site-alpha", "site-beta"], options.Connect);
        Assert.True(options.HasActions);
    }

    [Fact]
    public void Background_HidesTheWindowButIsNotHeadless()
    {
        StartupOptions options = StartupOptions.Parse(["--background"]);

        Assert.False(options.Headless);
        Assert.True(options.StartsHidden);
    }

    /// <summary>
    /// Switching from one tunnel to another has to stop the old one first.
    /// </summary>
    [Fact]
    public void DisconnectingComesBeforeConnecting()
    {
        StartupOptions options = StartupOptions.Parse(
            ["--connect", "site-beta", "--disconnect-all"]);

        Assert.Equal(
            [
                PilotCommands.Disconnect + PilotCommands.AllMarker,
                PilotCommands.Connect + "site-beta",
            ],
            options.ToCommands());
    }

    [Fact]
    public void Quit_IsTheLastThingSent()
    {
        StartupOptions options = StartupOptions.Parse(["--disconnect-all", "--quit"]);

        Assert.Equal(PilotCommands.Quit, options.ToCommands().Last());
    }

    [Fact]
    public void AProfileNameWithSpaces_SurvivesAsOneArgument()
    {
        StartupOptions options = StartupOptions.Parse(["--connect", "Site Alpha (production)"]);

        Assert.Equal(
            PilotCommands.Connect + "Site Alpha (production)",
            Assert.Single(options.ToCommands()));
    }

    [Theory]
    [InlineData("--connect")]
    [InlineData("--disconnect")]
    public void AnOptionMissingItsValue_IsReported(string option)
    {
        StartupOptions options = StartupOptions.Parse([option]);

        Assert.NotNull(options.Error);
        Assert.Contains(option, options.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value swallowed from the next option would connect a profile nobody named.
    /// </summary>
    [Fact]
    public void AnOptionFollowedByAnotherOption_IsNotGivenItAsAValue()
    {
        StartupOptions options = StartupOptions.Parse(["--connect", "--headless"]);

        Assert.NotNull(options.Error);
    }

    [Fact]
    public void AnUnknownOption_IsRefusedRatherThanIgnored()
    {
        StartupOptions options = StartupOptions.Parse(["--tunnel-everything"]);

        Assert.NotNull(options.Error);
        Assert.Contains("--tunnel-everything", options.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_IsRecognisedInEveryFormWindowsUsersExpect()
    {
        foreach (string form in new[] { "--help", "-h", "-?", "/?" })
        {
            Assert.True(StartupOptions.Parse([form]).ShowHelp, form);
        }
    }
}
