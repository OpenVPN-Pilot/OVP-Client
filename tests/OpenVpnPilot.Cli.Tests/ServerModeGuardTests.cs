using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.Cli.Tests;

/// <summary>
/// What the command may do to a server's copy: read it, and nothing else.
/// </summary>
public sealed class ServerModeGuardTests
{
    [Theory]
    [InlineData("import", new[] { "C:/profiles", "--commit" })]
    [InlineData("import", new[] { "--commit", "C:/profiles", "--tag", "production" })]
    [InlineData("unpack", new[] { "set.ovppkg", "--commit", "--passphrase", "example" })]
    [InlineData("favourite", new[] { "site-alpha", "--slot", "3" })]
    [InlineData("favourite", new[] { "site-alpha", "--clear" })]
    [InlineData("remove", new[] { "site-alpha", "--yes" })]
    [InlineData("remove", new[] { "site-alpha" })]
    public void Refuses_CommandsThatWrite_InServerMode(string command, string[] arguments)
    {
        Assert.True(ServerModeGuard.Refuses(command, arguments, StorageMode.Server));
    }

    [Theory]
    [InlineData("import", new[] { "C:/profiles", "--commit" })]
    [InlineData("unpack", new[] { "set.ovppkg", "--commit" })]
    [InlineData("favourite", new[] { "site-alpha" })]
    [InlineData("remove", new[] { "site-alpha", "--yes" })]
    public void Refuses_Nothing_InLocalMode(string command, string[] arguments)
    {
        Assert.False(ServerModeGuard.Refuses(command, arguments, StorageMode.Local));
    }

    [Theory]
    [InlineData("list", new[] { "--json" })]
    [InlineData("status", new string[0])]
    [InlineData("connect", new[] { "site-alpha" })]
    [InlineData("connect", new[] { "site-alpha", "--detached", "--seconds", "10" })]
    [InlineData("disconnect", new[] { "--all" })]
    [InlineData("export", new[] { "C:/out" })]
    [InlineData("pack", new[] { "set.ovppkg", "--passphrase", "example" })]
    [InlineData("completion", new[] { "zsh", "--install" })]
    [InlineData("doctor", new string[0])]
    [InlineData("start", new[] { "--headless" })]
    [InlineData("stop", new string[0])]
    public void Refuses_Nothing_ThatOnlyReads_InServerMode(string command, string[] arguments)
    {
        Assert.False(ServerModeGuard.Refuses(command, arguments, StorageMode.Server));
    }

    /// <summary>
    /// A dry run reports what an import would do, which is reading, and is useful in either mode.
    /// </summary>
    [Theory]
    [InlineData("import", new[] { "C:/profiles" })]
    [InlineData("unpack", new[] { "set.ovppkg", "--passphrase", "example" })]
    public void Refuses_Nothing_ForADryRun_InServerMode(string command, string[] arguments)
    {
        Assert.False(ServerModeGuard.Refuses(command, arguments, StorageMode.Server));
    }

    [Fact]
    public void RefusedInServerMode_IsItsOwnExitCode()
    {
        // 0 to 6 already mean something documented; a refusal must not look like any of them.
        Assert.Equal(7, ServerModeGuard.RefusedInServerMode);
    }
}
