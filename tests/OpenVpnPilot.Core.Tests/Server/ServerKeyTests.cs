using OpenVpnPilot.Core.Server;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// One server, one key, whatever way its address was written.
/// </summary>
/// <remarks>
/// The key names the folder the server's copy lives in. Two keys for one server would be a second,
/// empty copy; a changed computation would orphan every copy that already exists. The fixed values
/// below are there for the second reason.
/// </remarks>
public sealed class ServerKeyTests
{
    [Theory]
    [InlineData("https://pilot.example.com", "https://pilot.example.com")]
    [InlineData("https://pilot.example.com/", "https://pilot.example.com")]
    [InlineData("  https://pilot.example.com/  ", "https://pilot.example.com")]
    [InlineData("HTTPS://Pilot.Example.COM", "https://pilot.example.com")]
    [InlineData("https://pilot.example.com:443", "https://pilot.example.com")]
    [InlineData("https://pilot.example.com:443/", "https://pilot.example.com")]
    [InlineData("https://pilot.example.com:8443", "https://pilot.example.com:8443")]
    [InlineData("https://203.0.113.10:19443/", "https://203.0.113.10:19443")]
    [InlineData("https://[2001:DB8::1]:8443", "https://[2001:db8::1]:8443")]
    [InlineData("https://bücher.example.com", "https://xn--bcher-kva.example.com")]
    public void TryNormalise_AcceptedSpellings_GiveTheOneNormalForm(string address, string expected)
    {
        Assert.True(ServerKey.TryNormalise(address, out string? normalised, out ServerAddressProblem problem));
        Assert.Equal(expected, normalised);
        Assert.Equal(ServerAddressProblem.None, problem);
    }

    [Theory]
    [InlineData(null, ServerAddressProblem.Empty)]
    [InlineData("", ServerAddressProblem.Empty)]
    [InlineData("   ", ServerAddressProblem.Empty)]
    [InlineData("pilot.example.com", ServerAddressProblem.NotAnAddress)]
    [InlineData("http://pilot.example.com", ServerAddressProblem.NotHttps)]
    [InlineData("ftp://pilot.example.com", ServerAddressProblem.NotHttps)]
    [InlineData("https://someone:secret@pilot.example.com", ServerAddressProblem.CarriesCredentials)]
    [InlineData("https://pilot.example.com/pilot", ServerAddressProblem.CarriesPath)]
    [InlineData("https://pilot.example.com/api/v1/", ServerAddressProblem.CarriesPath)]
    [InlineData("https://pilot.example.com/?tenant=1", ServerAddressProblem.CarriesQuery)]
    [InlineData("https://pilot.example.com/#top", ServerAddressProblem.CarriesQuery)]
    public void TryNormalise_UnusableAddresses_SayWhy(string? address, ServerAddressProblem expected)
    {
        Assert.False(ServerKey.TryNormalise(address, out string? normalised, out ServerAddressProblem problem));
        Assert.Null(normalised);
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void Compute_IsTheFirstSixteenBytesOfTheHashInLowerCaseHex()
    {
        // SHA-256 of "https://pilot.example.com", first 16 bytes.
        Assert.Equal("9ba8b7393ae311cc4dc8f89fdb377030", ServerKey.Compute("https://pilot.example.com"));
        Assert.Equal("1a2e419164b477736c000ae54f60711c", ServerKey.Compute("https://pilot.example.com:8443"));
    }

    [Fact]
    public void Compute_SpellingsOfOneServer_GiveOneKey()
    {
        string key = ServerKey.Compute("https://pilot.example.com");

        Assert.Equal(key, ServerKey.Compute("HTTPS://PILOT.EXAMPLE.COM:443/"));
        Assert.Equal(32, key.Length);
        Assert.All(key, character => Assert.True(char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character)));
    }

    [Fact]
    public void Compute_ADifferentPort_IsADifferentServer()
    {
        Assert.NotEqual(
            ServerKey.Compute("https://pilot.example.com"),
            ServerKey.Compute("https://pilot.example.com:8443"));
    }

    [Fact]
    public void Compute_AnUnusableAddress_Throws()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ServerKey.Compute("http://pilot.example.com"));

        Assert.Contains(nameof(ServerAddressProblem.NotHttps), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCompute_AnUnusableAddress_GivesNoKey()
    {
        Assert.False(ServerKey.TryCompute("https://pilot.example.com/pilot", out string? key, out ServerAddressProblem problem));
        Assert.Null(key);
        Assert.Equal(ServerAddressProblem.CarriesPath, problem);
    }
}
