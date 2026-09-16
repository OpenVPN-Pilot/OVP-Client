using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.OpenVpn.Tests.Configuration;

/// <summary>
/// What OpenVPN would refuse is found before a profile is saved with it.
/// </summary>
public sealed class OvpnConfigValidatorTests
{
    // Normalised, because the repository checks source files out with the line ending of the system
    // and a raw literal takes the line endings of the file it is written in.
    private static readonly string Valid = """
        client
        remote vpn.example.com 1194 udp
        <ca>
        -----BEGIN CERTIFICATE-----
        MIIDQjCCAiqgAwIB
        -----END CERTIFICATE-----
        </ca>
        <key>
        -----BEGIN EC PRIVATE KEY-----
        MHcCAQEEIB
        -----END EC PRIVATE KEY-----
        </key>
        <tls-crypt>
        -----BEGIN OpenVPN Static key V1-----
        6d1d2b1e6a5f4c3b
        -----END OpenVPN Static key V1-----
        </tls-crypt>
        """.ReplaceLineEndings("\n");

    [Fact]
    public void AWorkingConfiguration_HasNothingToReport()
    {
        Assert.Empty(OvpnConfigValidator.Validate(Valid));
    }

    [Fact]
    public void NoRemote_IsAnError()
    {
        string content = Valid.Replace("remote vpn.example.com 1194 udp\n", string.Empty, StringComparison.Ordinal);

        OvpnConfigIssue issue = Assert.Single(OvpnConfigValidator.Validate(content));

        Assert.Equal(OvpnConfigIssueCode.NoRemote, issue.Code);
        Assert.True(issue.IsError);
    }

    [Theory]
    [InlineData("remote vpn.example.com 70000 udp", OvpnConfigIssueCode.InvalidPort, "70000")]
    [InlineData("remote vpn.example.com 1194 sctp", OvpnConfigIssueCode.InvalidProtocol, "sctp")]
    [InlineData("remote vpn.example.com https udp", OvpnConfigIssueCode.InvalidPort, "https")]
    public void AnUnreadableRemote_NamesTheValueAndTheLine(string remote, OvpnConfigIssueCode code, string value)
    {
        string content = Valid.Replace("remote vpn.example.com 1194 udp", remote, StringComparison.Ordinal);

        OvpnConfigIssue issue = Assert.Single(OvpnConfigValidator.Validate(content));

        Assert.Equal(code, issue.Code);
        Assert.Equal(2, issue.LineNumber);
        Assert.Equal([value], issue.Arguments);
    }

    [Fact]
    public void ABlockThatIsNeverClosed_IsReportedWhereItOpens()
    {
        string content = Valid.Replace("</key>\n", string.Empty, StringComparison.Ordinal);

        Assert.Contains(
            OvpnConfigValidator.Validate(content),
            issue => issue.Code == OvpnConfigIssueCode.UnclosedBlock && issue.LineNumber == 8 && issue.Arguments[0] == "key");
    }

    /// <summary>
    /// The most likely slip when a key is replaced by hand is pasting it into the wrong block.
    /// </summary>
    [Fact]
    public void AKeyInTheCertificateBlock_IsNamed()
    {
        string content = Valid.Replace(
            "-----BEGIN CERTIFICATE-----\nMIIDQjCCAiqgAwIB\n-----END CERTIFICATE-----",
            "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkq\n-----END PRIVATE KEY-----",
            StringComparison.Ordinal);

        OvpnConfigIssue issue = Assert.Single(OvpnConfigValidator.Validate(content));

        Assert.Equal(OvpnConfigIssueCode.NotACertificate, issue.Code);
        Assert.Equal(["ca"], issue.Arguments);
    }

    [Fact]
    public void NothingToVerifyTheServerWith_IsAnError()
    {
        string content = "client\nremote vpn.example.com 1194\n";

        Assert.Contains(
            OvpnConfigValidator.Validate(content),
            issue => issue.Code == OvpnConfigIssueCode.NoServerVerification && issue.IsError);
    }

    [Fact]
    public void AFileReferenceAndAScript_AreWarningsRatherThanErrors()
    {
        string content = Valid + "\ntls-auth ta.key 1\nup /etc/openvpn/up.sh\n";

        IReadOnlyList<OvpnConfigIssue> issues = OvpnConfigValidator.Validate(content);

        Assert.All(issues, issue => Assert.False(issue.IsError));
        Assert.Contains(issues, issue => issue.Code == OvpnConfigIssueCode.ExternalFile && issue.Arguments[0] == "tls-auth");
        Assert.Contains(issues, issue => issue.Code == OvpnConfigIssueCode.ScriptDirective && issue.Arguments[0] == "up");
    }
}
