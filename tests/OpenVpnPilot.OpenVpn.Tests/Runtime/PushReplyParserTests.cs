using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.OpenVpn.Tests.Runtime;

/// <summary>
/// The push reply is the only place the pushed options appear, so reading it has to be exact.
/// </summary>
public sealed class PushReplyParserTests
{
    private const string Reply =
        "PUSH: Received control message: 'PUSH_REPLY,route 10.8.0.0 255.255.255.0,"
        + "route-gateway 10.8.0.1,dhcp-option DNS 10.8.0.1,dhcp-option DNS 10.8.0.2,"
        + "redirect-gateway def1,ifconfig 10.8.0.6 10.8.0.5'";

    [Fact]
    public void Parse_ReadsRoutesGatewayAndNameServers()
    {
        PushedOptions options = Assert.IsType<PushedOptions>(PushReplyParser.Parse(Reply));

        Assert.Equal(["10.8.0.0 255.255.255.0"], options.Routes);
        Assert.Equal(["10.8.0.1", "10.8.0.2"], options.DnsServers);
        Assert.Equal("10.8.0.1", options.Gateway);
        Assert.True(options.RedirectsDefaultRoute);
    }

    [Fact]
    public void Parse_WithoutAGateway_FallsBackToTheTunnelPeer()
    {
        PushedOptions options = Assert.IsType<PushedOptions>(PushReplyParser.Parse(
            "PUSH: Received control message: 'PUSH_REPLY,ifconfig 10.8.0.6 10.8.0.5'"));

        // The peer is the far end of the tunnel, which is what a round trip should measure.
        Assert.Equal("10.8.0.5", options.Gateway);
    }

    /// <summary>
    /// Under topology subnet the second value of ifconfig is the netmask, not the peer.
    /// </summary>
    /// <remarks>
    /// Taking it anyway produced a gateway of 255.255.255.0, which nothing answers, so the round
    /// trip silently reported that the tunnel never replies.
    /// </remarks>
    [Fact]
    public void Parse_WithANetmaskInsteadOfAPeer_NamesNoGateway()
    {
        PushedOptions options = Assert.IsType<PushedOptions>(PushReplyParser.Parse(
            "PUSH: Received control message: 'PUSH_REPLY,ifconfig 10.8.0.6 255.255.255.0'"));

        Assert.Null(options.Gateway);
    }

    [Theory]
    [InlineData("255.255.255.0", true)]
    [InlineData("255.255.0.0", true)]
    [InlineData("255.255.255.252", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("10.8.0.5", false)]
    [InlineData("255.0.255.0", false)]
    [InlineData("not an address", false)]
    public void IsNetmask_SeparatesAMaskFromAnAddress(string value, bool expected) =>
        Assert.Equal(expected, PushReplyParser.IsNetmask(value));

    [Fact]
    public void Parse_WithoutARedirect_DoesNotClaimTheServerAskedForOne()
    {
        PushedOptions options = Assert.IsType<PushedOptions>(PushReplyParser.Parse(
            "PUSH: Received control message: 'PUSH_REPLY,route 192.168.10.0 255.255.255.0'"));

        Assert.False(options.RedirectsDefaultRoute);
        Assert.Empty(options.DnsServers);
        Assert.Null(options.Gateway);
    }

    [Fact]
    public void Parse_ReadsAnIpv6Route()
    {
        PushedOptions options = Assert.IsType<PushedOptions>(PushReplyParser.Parse(
            "PUSH: Received control message: 'PUSH_REPLY,route-ipv6 2001:db8::/64'"));

        Assert.Equal(["2001:db8::/64"], options.Routes);
    }

    [Theory]
    [InlineData("Initialization Sequence Completed")]
    [InlineData("PUSH: Received control message: 'AUTH_FAILED'")]
    [InlineData("SENT CONTROL [server]: 'PUSH_REQUEST' (status=1)")]
    public void Parse_LinesThatAreNotAPushReply_ReturnNull(string line)
    {
        Assert.Null(PushReplyParser.Parse(line));
    }

    /// <summary>
    /// A pushed compression setting is the reason a tunnel can reconnect forever.
    /// </summary>
    /// <remarks>
    /// A current client with data channel offload refuses any of them and then abandons the whole
    /// reply, reporting only that it could not process the push message. Recognising it here is what
    /// lets the interface name the cause instead of repeating that.
    /// </remarks>
    [Theory]
    [InlineData("comp-lzo")]
    [InlineData("comp-lzo no")]
    [InlineData("compress lzo")]
    [InlineData("compress")]
    public void Parse_RecognisesAPushedCompressionSetting(string option)
    {
        PushedOptions options = Assert.IsType<PushedOptions>(PushReplyParser.Parse(
            $"PUSH: Received control message: 'PUSH_REPLY,route-gateway 10.8.0.1,{option},ping 10'"));

        Assert.True(options.RequestsCompression);
    }

    [Fact]
    public void Parse_WithoutCompression_DoesNotClaimAny()
    {
        PushedOptions options = Assert.IsType<PushedOptions>(PushReplyParser.Parse(Reply));

        Assert.False(options.RequestsCompression);
    }

    [Fact]
    public void IsPushReply_RecognisesTheLineWithoutParsingIt()
    {
        Assert.True(PushReplyParser.IsPushReply(Reply));
        Assert.False(PushReplyParser.IsPushReply("Initialization Sequence Completed"));
    }

    [Fact]
    public void Parse_OptionsWithoutAValue_AreIgnoredRatherThanThrowing()
    {
        PushedOptions options = Assert.IsType<PushedOptions>(PushReplyParser.Parse(
            "PUSH: Received control message: 'PUSH_REPLY,route,dhcp-option,route-gateway,ping 10'"));

        Assert.Empty(options.Routes);
        Assert.Empty(options.DnsServers);
        Assert.Null(options.Gateway);
    }
}
