using OpenVpnPilot.Platform.MacOS.Helper.Policy;

namespace OpenVpnPilot.Platform.MacOS.Tests.Policy;

/// <summary>
/// The guard on what a configuration may make OpenVPN do as root.
/// </summary>
/// <remarks>
/// The counterpart of the Windows launch option whitelist test, with more to guard: on macOS
/// OpenVPN runs as root, so the configuration itself is part of what reaches root, not only the
/// command line. Every refused directive here is one that would run a program, load code, write a
/// file or read a path as root.
/// </remarks>
public sealed class ConfigurationPolicyTests
{
    private const string Certificate = "-----BEGIN CERTIFICATE-----\nMIIBszCCAVmgAwIBAgIUQ0FEQUJDREVGR0hJSktMTU5PUFFSUzAKBggqhkjOPQQD\n-----END CERTIFICATE-----\n";

    private static readonly string Client =
        "client\n"
        + "dev tun\n"
        + "proto udp\n"
        + "remote vpn.example.com 1194\n"
        + "resolv-retry infinite\n"
        + "nobind\n"
        + "remote-cert-tls server\n"
        + "verb 3\n"
        + "<ca>\n" + Certificate + "</ca>\n"
        + "<cert>\n" + Certificate + "</cert>\n"
        + "<key>\n-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg\n-----END PRIVATE KEY-----\n</key>\n";

    [Fact]
    public void Check_AnOrdinaryClientConfiguration_IsAccepted()
    {
        string copy = ConfigurationPolicy.Check(Client);

        Assert.Contains("remote \"vpn.example.com\" \"1194\"\n", copy, StringComparison.Ordinal);
        Assert.Contains("<ca>\n-----BEGIN CERTIFICATE-----\n", copy, StringComparison.Ordinal);
        Assert.EndsWith("</key>\n", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_WindowsLineEndings_AreAccepted()
    {
        string copy = ConfigurationPolicy.Check(Client.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.DoesNotContain('\r', copy);
        Assert.Contains("remote \"vpn.example.com\" \"1194\"\n", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_TheCopy_HasNoCommentsLeft()
    {
        string copy = ConfigurationPolicy.Check("# note\nclient ; inline note\n\n  # indented note\n");

        Assert.Equal("client\n", copy);
    }

    /// <summary>
    /// What OpenVPN reads from the copy is what the helper read from the original.
    /// </summary>
    [Theory]
    [InlineData("verify-x509-name \"CN=a \\\"b\\\" c\" name\n")]
    [InlineData("x 'single \\ quoted' a\\ b\n")]
    [InlineData("static-challenge \"Enter your code\" 1\n")]
    [InlineData("setenv UV_NAME 'two words'\n")]
    public void Check_TheCopy_ReadsBackAsTheSameWords(string line)
    {
        string copy = ConfigurationPolicy.Check(line);

        Assert.Equal(OpenVpnLineParser.Parse(line, 1), OpenVpnLineParser.Parse(copy, 1));
    }

    [Fact]
    public void Check_TheCopy_IsAlreadyInItsOwnForm()
    {
        string copy = ConfigurationPolicy.Check(Client);

        Assert.Equal(copy, ConfigurationPolicy.Check(copy));
    }

    [Theory]
    [InlineData("up /tmp/x")]
    [InlineData("down /tmp/x")]
    [InlineData("route-up /tmp/x")]
    [InlineData("route-pre-down /tmp/x")]
    [InlineData("ipchange /tmp/x")]
    [InlineData("tls-verify /tmp/x")]
    [InlineData("tls-crypt-v2-verify /tmp/x")]
    [InlineData("auth-user-pass-verify /tmp/x via-env")]
    [InlineData("client-connect /tmp/x")]
    [InlineData("client-disconnect /tmp/x")]
    [InlineData("learn-address /tmp/x")]
    [InlineData("dns-updown /tmp/x")]
    [InlineData("iproute /tmp/x")]
    [InlineData("plugin /tmp/x.so")]
    [InlineData("engine dynamic")]
    [InlineData("providers legacy default")]
    [InlineData("pkcs11-providers /tmp/x.dylib")]
    [InlineData("setcon unconfined")]
    [InlineData("script-security 2")]
    [InlineData("log /etc/sudoers.d/x")]
    [InlineData("log-append /etc/sudoers.d/x")]
    [InlineData("writepid /etc/x")]
    [InlineData("status /etc/x 1")]
    [InlineData("replay-persist /etc/x")]
    [InlineData("ifconfig-pool-persist /etc/x")]
    [InlineData("tls-export-cert /etc")]
    [InlineData("genkey secret /etc/x")]
    [InlineData("mktun")]
    [InlineData("rmtun")]
    [InlineData("config /var/root/x")]
    [InlineData("cd /var/root")]
    [InlineData("chroot /var/root")]
    [InlineData("capath /var/root")]
    [InlineData("dev-node /dev/disk0")]
    [InlineData("tmp-dir /tmp")]
    [InlineData("daemon")]
    [InlineData("syslog")]
    [InlineData("user nobody")]
    [InlineData("group nobody")]
    [InlineData("management 0.0.0.0 1234")]
    [InlineData("management-client-user root")]
    [InlineData("management-external-key")]
    [InlineData("mode server")]
    [InlineData("server 10.8.0.0 255.255.255.0")]
    [InlineData("tls-server")]
    [InlineData("port-share 127.0.0.1 22")]
    [InlineData("client-config-dir /tmp")]
    [InlineData("auth-gen-token-secret /tmp/x")]
    public void Check_ADirectiveThatActsAsRoot_IsRefused(string directive)
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check("client\n" + directive + "\n"));
    }

    [Theory]
    [InlineData("--up /tmp/x")]
    [InlineData("\"up\" /tmp/x")]
    [InlineData("'up' /tmp/x")]
    [InlineData("UP /tmp/x")]
    [InlineData("setenv opt up /tmp/x")]
    [InlineData("setenv opt --up /tmp/x")]
    [InlineData("setenv opt setenv opt up /tmp/x")]
    public void Check_ARefusedDirectiveInDisguise_IsRefused(string directive)
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check(directive + "\n"));
    }

    /// <summary>
    /// A variable reaches the programs OpenVPN runs as root, and OpenVPN's own DNS script calls tools
    /// without a path and reads BASH_ENV.
    /// </summary>
    [Theory]
    [InlineData("setenv PATH /tmp")]
    [InlineData("setenv BASH_ENV /tmp/x")]
    [InlineData("setenv dns_vars_file /tmp/x")]
    [InlineData("setenv DYLD_INSERT_LIBRARIES /tmp/x.dylib")]
    [InlineData("setenv")]
    public void Check_AnEnvironmentVariableForAProgram_IsRefused(string directive)
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check(directive + "\n"));
    }

    [Theory]
    [InlineData("setenv UV_DEVICE laptop")]
    [InlineData("setenv FORWARD_COMPATIBLE 1")]
    [InlineData("setenv opt block-outside-dns")]
    [InlineData("setenv-safe NOTE value")]
    public void Check_AVariableThatReachesNoProgram_IsAccepted(string directive)
    {
        ConfigurationPolicy.Check(directive + "\n");
    }

    [Theory]
    [InlineData("ca /etc/master.passwd")]
    [InlineData("cert /var/root/x")]
    [InlineData("key /var/root/x")]
    [InlineData("pkcs12 /var/root/x")]
    [InlineData("tls-auth /var/root/x 1")]
    [InlineData("tls-crypt /var/root/x")]
    [InlineData("tls-crypt-v2 /var/root/x")]
    [InlineData("secret /var/root/x")]
    [InlineData("crl-verify /var/root/x")]
    [InlineData("extra-certs /var/root/x")]
    [InlineData("http-proxy-user-pass /var/root/x")]
    [InlineData("auth-user-pass /var/root/x")]
    [InlineData("askpass /var/root/x")]
    [InlineData("http-proxy proxy.example.com 8080 /var/root/x")]
    [InlineData("socks-proxy proxy.example.com 1080 /var/root/x")]
    public void Check_AFileNamedByPath_IsRefused(string directive)
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check(directive + "\n"));
    }

    [Theory]
    [InlineData("auth-user-pass")]
    [InlineData("askpass")]
    [InlineData("http-proxy proxy.example.com 8080")]
    [InlineData("http-proxy proxy.example.com 8080 auto")]
    [InlineData("socks-proxy proxy.example.com 1080")]
    public void Check_TheSameDirectivesWithoutAFile_AreAccepted(string directive)
    {
        ConfigurationPolicy.Check(directive + "\n");
    }

    [Theory]
    [InlineData("<auth-user-pass>\nuser\npassword\n</auth-user-pass>\n")]
    [InlineData("<tls-crypt>\n-----BEGIN OpenVPN Static key V1-----\n0123\n-----END OpenVPN Static key V1-----\n</tls-crypt>\n")]
    [InlineData("<peer-fingerprint>\nAA:BB\n</peer-fingerprint>\n")]
    public void Check_AnInlineBlockOpenVpnAccepts_IsAccepted(string block)
    {
        ConfigurationPolicy.Check(block);
    }

    [Theory]
    [InlineData("<setenv>\nPATH\n</setenv>\n")]
    [InlineData("<up>\n/tmp/x\n</up>\n")]
    [InlineData("<config>\n/var/root/x\n</config>\n")]
    [InlineData("<log>\n/etc/x\n</log>\n")]
    public void Check_AnyOtherInlineBlock_IsRefused(string block)
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check(block));
    }

    /// <summary>
    /// The one way a directive can hide inside an inline block: OpenVPN reads a block line into 256
    /// bytes and continues a longer one on its next read, so the end tag in the tail of a long line
    /// closes the block for OpenVPN while a line based reader is still inside it.
    /// </summary>
    [Fact]
    public void Check_ALineOpenVpnWouldSplit_IsRefused()
    {
        string smuggled =
            "<ca>\n"
            + new string('A', 255) + "</ca>\n"
            + "up /tmp/x\n"
            + "<ca>\n"
            + "</ca>\n";

        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check(smuggled));
    }

    [Fact]
    public void Check_ALongComment_IsRefusedToo()
    {
        Assert.Throws<ConfigurationRefusedException>(
            () => ConfigurationPolicy.Check("# " + new string('x', 300) + " up /tmp/x\n"));
    }

    /// <summary>
    /// The longest line OpenVPN still reads whole is accepted, and one byte more is not.
    /// </summary>
    [Fact]
    public void Check_TheLongestLineOpenVpnReadsWhole_IsAccepted()
    {
        string longest = new('x', ConfigurationPolicy.MaximumLineBytes - 2);

        ConfigurationPolicy.Check("# " + longest + "\n");

        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check("# " + longest + "x\n"));
    }

    [Fact]
    public void Check_ANulCharacter_IsRefused()
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check("remote vpn.example.com\0\nup /tmp/x\n"));
    }

    [Fact]
    public void Check_AnInlineBlockThatNeverCloses_IsRefused()
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check("<ca>\n" + Certificate));
    }

    /// <summary>
    /// OpenVPN ends a block at a line that begins with the end tag, whatever follows it on that line.
    /// </summary>
    [Fact]
    public void Check_AnEndTagWithTextAfterIt_EndsTheBlockAsItDoesForOpenVpn()
    {
        Assert.Throws<ConfigurationRefusedException>(
            () => ConfigurationPolicy.Check("<ca>\n" + Certificate + "  </ca> trailing\nup /tmp/x\n"));
    }

    [Fact]
    public void Check_ADirectiveInsideAConnectionBlock_IsJudgedToo()
    {
        Assert.Throws<ConfigurationRefusedException>(
            () => ConfigurationPolicy.Check("<connection>\nremote vpn.example.com 1194\nup /tmp/x\n</connection>\n"));
    }

    [Fact]
    public void Check_AConnectionBlock_IsWrittenOutDirectiveByDirective()
    {
        string copy = ConfigurationPolicy.Check("<connection>\n# primary\nremote vpn.example.com 1194 udp\n</connection>\n");

        Assert.Equal("<connection>\nremote \"vpn.example.com\" \"1194\" \"udp\"\n</connection>\n", copy);
    }

    [Fact]
    public void Check_AConnectionBlockInsideAnother_IsRefused()
    {
        Assert.Throws<ConfigurationRefusedException>(
            () => ConfigurationPolicy.Check("<connection>\n<connection>\nremote a\n</connection>\n</connection>\n"));
    }

    [Theory]
    [InlineData("remo#te host")]
    [InlineData("<ca$> x")]
    [InlineData("remoteé host")]
    public void Check_SomethingThatIsNotADirectiveName_IsRefused(string line)
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check(line + "\n"));
    }

    [Fact]
    public void Check_AnArgumentWithAControlCharacter_IsRefused()
    {
        Assert.Throws<ConfigurationRefusedException>(() => ConfigurationPolicy.Check("remote \"vpnexample\"\n"));
    }

    [Fact]
    public void Check_ARefusal_NamesTheLineAndTheDirective()
    {
        ConfigurationRefusedException refusal = Assert.Throws<ConfigurationRefusedException>(
            () => ConfigurationPolicy.Check("client\nremote vpn.example.com\nup /tmp/x\n"));

        Assert.Equal(3, refusal.LineNumber);
        Assert.Contains("'up'", refusal.Message, StringComparison.Ordinal);
    }
}
