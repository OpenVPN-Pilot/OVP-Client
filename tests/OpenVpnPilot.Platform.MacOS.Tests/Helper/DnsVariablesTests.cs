using OpenVpnPilot.Platform.MacOS.Helper.Dns;

namespace OpenVpnPilot.Platform.MacOS.Tests.Helper;

/// <summary>
/// What the helper lets through to OpenVPN's own name server script.
/// </summary>
/// <remarks>
/// Everything here arrives from a server over the tunnel and is applied by a script that runs as
/// root, so a value that is not what it claims to be must not reach it, and a variable that decides
/// what a program does must not reach it at all.
/// </remarks>
public sealed class DnsVariablesTests
{
    [Fact]
    public void TryCollect_TheVariablesTheScriptReads_ArePassedOn()
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["script_type"] = "dns-up",
            ["dev"] = "utun7",
            ["dns_search_domain_1"] = "lab.example",
            ["dns_server_1_address_1"] = "10.9.0.53",
            ["dns_server_1_address_2"] = "fd00::53",
            ["dns_server_1_port_1"] = "53",
            ["dns_server_1_resolve_domain_1"] = "lab.example",
            ["dns_server_1_exclude_domain_1"] = "public.example",
            ["dns_server_1_dnssec"] = "optional",
            ["dns_server_1_transport"] = "DoH",
            ["dns_server_1_sni"] = "dns.lab.example",
        };

        Assert.True(DnsVariables.TryCollect(environment, out Dictionary<string, string> variables, out string? problem));
        Assert.Null(problem);
        Assert.Equal(environment.Count, variables.Count);
        Assert.Equal("10.9.0.53", variables["dns_server_1_address_1"]);
    }

    /// <summary>
    /// PATH and BASH_ENV decide what the script runs; neither is a DNS variable, and neither is
    /// carried over.
    /// </summary>
    [Fact]
    public void TryCollect_EverythingElseInTheEnvironment_IsLeftBehind()
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["script_type"] = "dns-down",
            ["dev"] = "utun7",
            ["PATH"] = "/tmp/first",
            ["BASH_ENV"] = "/tmp/x.sh",
            ["DYLD_INSERT_LIBRARIES"] = "/tmp/x.dylib",
            ["foreign_option_1"] = "dhcp-option DNS 10.9.0.53",
            ["config"] = "/tmp/other.ovpn",
        };

        Assert.True(DnsVariables.TryCollect(environment, out Dictionary<string, string> variables, out _));
        Assert.Equal(["dev", "script_type"], variables.Keys.Order());
    }

    [Theory]
    [InlineData("dns_server_1_address_1", "not-an-address")]
    [InlineData("dns_server_1_address_1", "10.9.0.53 10.9.0.54")]
    [InlineData("dns_server_1_port_1", "0")]
    [InlineData("dns_server_1_port_1", "99999")]
    [InlineData("dns_server_1_port_1", "53abc")]
    [InlineData("dns_search_domain_1", "lab.example\nremove State:/Network/Global/DNS")]
    [InlineData("dns_search_domain_1", "lab example")]
    [InlineData("dns_search_domain_1", "$(id)")]
    [InlineData("dns_server_1_dnssec", "maybe")]
    [InlineData("dns_server_1_transport", "DoQ")]
    [InlineData("dns_server_1_sni", "dns lab")]
    [InlineData("dns_unknown_setting", "x")]
    public void TryCollect_AValueThatIsNotWhatItClaims_RefusesTheWholeRun(string name, string value)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["script_type"] = "dns-up",
            ["dev"] = "utun7",
            [name] = value,
        };

        Assert.False(DnsVariables.TryCollect(environment, out _, out string? problem));
        Assert.Contains(name, problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("up")]
    [InlineData("")]
    [InlineData("route-up")]
    public void TryCollect_AnotherScriptType_IsRefused(string scriptType)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["script_type"] = scriptType,
            ["dev"] = "utun7",
        };

        Assert.False(DnsVariables.TryCollect(environment, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("en0")]
    [InlineData("../../etc")]
    [InlineData("utun7; rm -rf /")]
    public void TryCollect_ADeviceThatIsNotATunnel_IsRefused(string device)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["script_type"] = "dns-up",
            ["dev"] = device,
        };

        Assert.False(DnsVariables.TryCollect(environment, out _, out _));
    }

    /// <summary>
    /// The variables file is only used when OpenVPN has dropped its privileges, which the helper
    /// never lets it do. Set by anything else, it would name a file the script reads as root.
    /// </summary>
    [Fact]
    public void TryCollect_AVariablesFile_IsRefused()
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["script_type"] = "dns-up",
            ["dev"] = "utun7",
            ["dns_vars_file"] = "/tmp/x.sh",
        };

        Assert.False(DnsVariables.TryCollect(environment, out _, out string? problem));
        Assert.Contains("dns_vars_file", problem!, StringComparison.Ordinal);
    }
}
