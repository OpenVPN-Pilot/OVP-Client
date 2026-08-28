using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.OpenVpn.Tests.Configuration;

public sealed class OvpnConfigParserTests
{
    private const string SelfContainedProfile = """
        client
        nobind
        dev tun
        remote-cert-tls server

        remote vpn.example.com 1194 udp

        <key>
        -----BEGIN PRIVATE KEY-----
        MIIEvQIBADANBgkq
        -----END PRIVATE KEY-----
        </key>
        <cert>
        -----BEGIN CERTIFICATE-----
        MIIDSjCCAjKgAwIB
        -----END CERTIFICATE-----
        </cert>
        <ca>
        -----BEGIN CERTIFICATE-----
        MIIDQjCCAiqgAwIB
        -----END CERTIFICATE-----
        </ca>
        key-direction 1
        <tls-auth>
        -----BEGIN OpenVPN Static key V1-----
        6d1d2b1e6a5f4c3b
        -----END OpenVPN Static key V1-----
        </tls-auth>
        """;

    [Fact]
    public void Parse_SelfContainedProfile_ExtractsEveryInlineBlock()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse(SelfContainedProfile);

        Assert.Equal(4, configuration.InlineBlocks.Count);
        Assert.True(configuration.InlineBlocks.ContainsKey("ca"));
        Assert.True(configuration.InlineBlocks.ContainsKey("cert"));
        Assert.True(configuration.InlineBlocks.ContainsKey("key"));
        Assert.True(configuration.InlineBlocks.ContainsKey("tls-auth"));
    }

    [Fact]
    public void Parse_InlineBlock_PreservesContentExactly()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse(SelfContainedProfile);

        string ca = configuration.InlineBlocks["ca"].Content;

        Assert.Equal(
            "-----BEGIN CERTIFICATE-----\nMIIDQjCCAiqgAwIB\n-----END CERTIFICATE-----",
            ca);
    }

    [Fact]
    public void Parse_SelfContainedProfile_ReportsSelfContainedAndVerifiable()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse(SelfContainedProfile);

        Assert.True(configuration.IsSelfContained);
        Assert.True(configuration.HasServerVerification);
        Assert.Empty(configuration.ExternalFileReferences);
        Assert.Empty(configuration.ScriptOptions);
    }

    [Fact]
    public void Parse_RemoteWithInlineProtocol_IsParsed()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse(SelfContainedProfile);

        OvpnRemote remote = Assert.Single(configuration.Remotes);
        Assert.Equal("vpn.example.com", remote.Host);
        Assert.Equal(1194, remote.Port);
        Assert.Equal(OvpnProtocol.Udp, remote.Protocol);
    }

    [Fact]
    public void Parse_SeparateProtoDirective_AppliesToRemotesWithoutOne()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("""
            client
            proto tcp-client
            remote vpn.example.com 443
            remote backup.example.com 1194 udp
            """);

        Assert.Equal(2, configuration.Remotes.Count);
        Assert.Equal(OvpnProtocol.Tcp, configuration.Remotes[0].Protocol);
        Assert.Equal(443, configuration.Remotes[0].Port);
        Assert.Equal(OvpnProtocol.Udp, configuration.Remotes[1].Protocol);
    }

    [Fact]
    public void Parse_RemoteWithoutPort_FallsBackToTheDefault()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("remote vpn.example.com");

        Assert.Equal(1194, Assert.Single(configuration.Remotes).Port);
    }

    [Theory]
    [InlineData("# a hash comment")]
    [InlineData("; a semicolon comment")]
    [InlineData("")]
    [InlineData("    ")]
    public void Parse_CommentsAndBlankLines_ProduceNoDirectives(string line)
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse(line);

        Assert.Empty(configuration.Directives);
    }

    [Fact]
    public void Parse_QuotedArgument_KeepsSpacesAndDropsQuotes()
    {
        // Raw string literals do not process escape sequences, so these backslashes are literal.
        OvpnConfiguration configuration = OvpnConfigParser.Parse("""
            ca "C:\Program Files\Certificates\example ca.crt"
            """);

        OvpnDirective directive = Assert.Single(configuration.Directives);
        Assert.Equal("ca", directive.Name);
        Assert.Equal(@"C:\Program Files\Certificates\example ca.crt", Assert.Single(directive.Arguments));
    }

    [Fact]
    public void Parse_ExternalFileReferences_AreReportedAsNotSelfContained()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("""
            client
            remote vpn.example.com 1194 udp
            ca ca.crt
            cert client.crt
            key client.key
            tls-auth ta.key 1
            """);

        Assert.False(configuration.IsSelfContained);
        Assert.Equal(4, configuration.ExternalFileReferences.Count);
        Assert.Contains(configuration.ExternalFileReferences, d => d.Name == "tls-auth");
    }

    [Fact]
    public void Parse_ScriptDirectives_AreSurfaced()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("""
            client
            remote vpn.example.com 1194 udp
            script-security 2
            up update-resolv-conf
            down update-resolv-conf
            """);

        Assert.Equal(2, configuration.ScriptOptions.Count);
        Assert.Contains(configuration.ScriptOptions, d => d.Name == "up");
        Assert.Contains(configuration.ScriptOptions, d => d.Name == "down");
    }

    [Fact]
    public void Parse_PeerFingerprint_CountsAsServerVerification()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("""
            client
            remote vpn.example.com 1194 udp
            peer-fingerprint 47:03:71:16:E2:4D:2C:DD
            """);

        Assert.True(configuration.HasServerVerification);
    }

    [Fact]
    public void Parse_WithoutAnyServerVerification_IsReportedAsUnverifiable()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("""
            client
            remote vpn.example.com 1194 udp
            auth-user-pass
            """);

        Assert.False(configuration.HasServerVerification);
        Assert.True(configuration.RequiresUserCredentials);
    }

    [Fact]
    public void Parse_UnterminatedInlineBlock_KeepsWhatWasRead()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("""
            client
            <ca>
            -----BEGIN CERTIFICATE-----
            MIIDQjCCAiqgAwIB
            """);

        Assert.True(configuration.InlineBlocks.ContainsKey("ca"));
        Assert.Contains("MIIDQjCCAiqgAwIB", configuration.InlineBlocks["ca"].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DirectiveLineNumbers_ArePreservedForDiagnostics()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("""
            client

            # comment
            remote vpn.example.com 1194 udp
            """);

        OvpnDirective remote = Assert.Single(configuration.Directives, d => d.Name == "remote");
        Assert.Equal(4, remote.LineNumber);
    }

    [Fact]
    public void Parse_UnknownDirective_IsPreservedVerbatim()
    {
        OvpnConfiguration configuration = OvpnConfigParser.Parse("some-future-option value1 value2");

        OvpnDirective directive = Assert.Single(configuration.Directives);
        Assert.Equal("some-future-option", directive.Name);
        Assert.Equal(["value1", "value2"], directive.Arguments);
        Assert.Equal("some-future-option value1 value2", directive.ToString());
    }
}
