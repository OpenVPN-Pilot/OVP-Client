using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.OpenVpn.Tests.Configuration;

/// <summary>
/// An edit changes the lines it is about and nothing else.
/// </summary>
public sealed class OvpnConfigEditorTests
{
    // Normalised, because the repository checks source files out with the line ending of the system
    // and a raw literal takes the line endings of the file it is written in.
    private static readonly string Profile = """
        # example-site
        client
        dev tun
        remote vpn.example.com 1194 udp
        remote-cert-tls server
        <ca>
        -----BEGIN CERTIFICATE-----
        MIIDQjCCAiqgAwIB
        -----END CERTIFICATE-----
        </ca>
        <cert>
        -----BEGIN CERTIFICATE-----
        MIIDSjCCAjKgAwIB
        -----END CERTIFICATE-----
        </cert>
        <key>
        -----BEGIN PRIVATE KEY-----
        MIIEvQIBADANBgkq
        -----END PRIVATE KEY-----
        </key>

        """.ReplaceLineEndings("\n");

    [Fact]
    public void SetEndpoint_ReplacesOnlyTheRemoteLine()
    {
        string edited = OvpnConfigEditor.SetEndpoint(Profile, "vpn2.example.com", 443, OvpnProtocol.Tcp);

        Assert.Equal(
            Profile.Replace("remote vpn.example.com 1194 udp", "remote vpn2.example.com 443 tcp", StringComparison.Ordinal),
            edited);
    }

    [Fact]
    public void SetEndpoint_KeepsTheLineEndingsOfTheFile()
    {
        string windows = Profile.Replace("\n", "\r\n", StringComparison.Ordinal);

        string edited = OvpnConfigEditor.SetEndpoint(windows, "198.51.100.4", 1194, OvpnProtocol.Udp);

        Assert.DoesNotContain("\r\r", edited, StringComparison.Ordinal);
        Assert.Equal(windows.Split("\r\n").Length, edited.Split("\r\n").Length);
        Assert.Contains("remote 198.51.100.4 1194 udp\r\n", edited, StringComparison.Ordinal);
    }

    /// <summary>
    /// The file keeps one place that says which protocol is used.
    /// </summary>
    [Fact]
    public void SetEndpoint_WithASharedProtoDirective_ChangesThatDirective()
    {
        const string shared = "client\nproto udp4\nremote vpn.example.com 1194\n";

        string edited = OvpnConfigEditor.SetEndpoint(shared, "vpn.example.com", 443, OvpnProtocol.Tcp);

        Assert.Equal("client\nproto tcp4\nremote vpn.example.com 443\n", edited);
    }

    [Fact]
    public void SetEndpoint_WithoutAnyProtocol_WritesItOntoTheRemoteOnlyWhenItChanges()
    {
        const string bare = "client\nremote vpn.example.com\n";

        Assert.Equal(
            "client\nremote vpn.example.com 1194\n",
            OvpnConfigEditor.SetEndpoint(bare, "vpn.example.com", 1194, OvpnProtocol.Udp));

        Assert.Equal(
            "client\nremote vpn.example.com 1194 tcp\n",
            OvpnConfigEditor.SetEndpoint(bare, "vpn.example.com", 1194, OvpnProtocol.Tcp));
    }

    [Fact]
    public void SetEndpoint_WithNoRemote_AddsOneAfterTheClientDirective()
    {
        string edited = OvpnConfigEditor.SetEndpoint("client\ndev tun\n", "vpn.example.com", 1194, OvpnProtocol.Udp);

        Assert.Equal("client\nremote vpn.example.com 1194\ndev tun\n", edited);
    }

    [Fact]
    public void SetBlock_ReplacesTheContentsBetweenTheTags()
    {
        const string key = "-----BEGIN PRIVATE KEY-----\nREPLACED\n-----END PRIVATE KEY-----";

        string edited = OvpnConfigEditor.SetBlock(Profile, "key", key);

        Assert.Equal(key, OvpnConfigEditor.ReadBlock(edited, "key"));
        Assert.Equal(OvpnConfigEditor.ReadBlock(Profile, "ca"), OvpnConfigEditor.ReadBlock(edited, "ca"));
        Assert.StartsWith("# example-site\nclient\n", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void SetBlock_WithNothing_RemovesTheBlock()
    {
        string edited = OvpnConfigEditor.SetBlock(Profile, "cert", "  ");

        Assert.Null(OvpnConfigEditor.ReadBlock(edited, "cert"));
        Assert.DoesNotContain("<cert>", edited, StringComparison.Ordinal);
        Assert.NotNull(OvpnConfigEditor.ReadBlock(edited, "key"));
    }

    /// <summary>
    /// A stored profile cannot reach a file, so the line naming one goes when the block arrives.
    /// </summary>
    [Fact]
    public void SetBlock_AddingABlock_RemovesTheFileReferenceItReplaces()
    {
        const string referencing = "client\nremote vpn.example.com 1194\ntls-crypt ta.key\n";
        const string key = "-----BEGIN OpenVPN Static key V1-----\nabc\n-----END OpenVPN Static key V1-----";

        string edited = OvpnConfigEditor.SetBlock(referencing, "tls-crypt", key);

        Assert.Equal(
            "client\nremote vpn.example.com 1194\n<tls-crypt>\n" + key + "\n</tls-crypt>\n",
            edited);
    }

    [Fact]
    public void ReadEndpoint_FallsBackToThePortDirective()
    {
        OvpnEndpoint? endpoint = OvpnConfigEditor.ReadEndpoint("client\nport 443\nproto tcp\nremote a.example.com\nremote b.example.com\n");

        Assert.Equal(new OvpnEndpoint("a.example.com", 443, OvpnProtocol.Tcp, 2), endpoint);
    }
}
