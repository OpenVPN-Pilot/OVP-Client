using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.Data.Tests.Import;

/// <summary>
/// A profile the server stored has to be recognised by its hash, so the client's rewriting has to
/// match the server's byte for byte.
/// </summary>
/// <remarks>
/// The literal hashes were produced by the server's own <c>OvpnInspector</c> and
/// <c>TokenHashing.ContentHash</c> of version 1.0.0 for the same text. They are what proves the two
/// agree; the expected texts make a failure readable.
/// </remarks>
public sealed class ServerContentHashTests
{
    private const string Ca = "<ca>\n-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n</ca>\n";

    [Fact]
    public void Compute_ConfigurationWithoutCredentialFile_EqualsTheLocalHash()
    {
        string configuration = "client\nremote vpn.example.com 1194\n" + Ca;

        Assert.Equal(ProfileImporter.ComputeHash(configuration), ServerContentHash.Compute(configuration));
        Assert.Equal("dfcebb599a76f3ce57126ac6ef0fc0130e019e583da147bc8b7d01b9243cc1d7", ServerContentHash.Compute(configuration));
    }

    [Fact]
    public void Compute_CredentialFile_MatchesTheServersHashOfTheBareDirective()
    {
        string configuration = "client\nauth-user-pass creds.txt\n" + Ca;

        Assert.Equal("client\nauth-user-pass\n" + Ca, ServerContentHash.Normalise(configuration));
        Assert.Equal("bececec29c8c443589ec024384a7bc3953b7e81a79450c7641e74e86752b55e0", ServerContentHash.Compute(configuration));
        Assert.Equal(ServerContentHash.Compute("client\nauth-user-pass\n" + Ca), ServerContentHash.Compute(configuration));
    }

    [Fact]
    public void Normalise_QuotedFileWithIndentation_KeepsIndentationAndLineEnding()
    {
        string configuration = "client\r\n  auth-user-pass   \"my creds.txt\"  \r\nremote vpn.example.com\r\n" + Ca.Replace("\n", "\r\n");

        Assert.Equal(
            "client\r\n  auth-user-pass\r\nremote vpn.example.com\r\n" + Ca.Replace("\n", "\r\n"),
            ServerContentHash.Normalise(configuration));
        Assert.Equal("8962497c3ef1243ccb0d205f3672be1397ce6091081895a321b7c3a96718cf23", ServerContentHash.Compute(configuration));
    }

    [Fact]
    public void Normalise_LoneCarriageReturns_EndLines()
    {
        string configuration = "client\rauth-user-pass a.txt\r" + Ca;

        Assert.Equal("client\rauth-user-pass\r" + Ca, ServerContentHash.Normalise(configuration));
        Assert.Equal("b88e9fce525f0f299bc32c6a7aa406faae6fea37c5d4caba521ab53eae9b3724", ServerContentHash.Compute(configuration));
    }

    [Fact]
    public void Normalise_CommentsBlocksAndOtherDirectives_AreLeftAlone()
    {
        string configuration =
            "client\n# auth-user-pass c.txt\n;auth-user-pass d.txt\nAUTH-USER-PASS e.txt\nauth-user-pass-verify s.sh via-env\n"
            + "<connection>\nauth-user-pass inner.txt\n</connection>\n" + Ca;

        Assert.Equal(configuration, ServerContentHash.Normalise(configuration));
    }

    [Fact]
    public void Normalise_TrailingWordsAndQuotedEmptyFile_AreRewrittenAsTheServerDoes()
    {
        Assert.Equal(
            "client\nauth-user-pass\n" + Ca,
            ServerContentHash.Normalise("client\nauth-user-pass outer.txt # trailing\n" + Ca));
        Assert.Equal(
            "client\nauth-user-pass\n" + Ca,
            ServerContentHash.Normalise("client\nauth-user-pass \"\"\n" + Ca));
        Assert.Equal(
            "bececec29c8c443589ec024384a7bc3953b7e81a79450c7641e74e86752b55e0",
            ServerContentHash.Compute("client\nauth-user-pass \"\"\n" + Ca));
    }

    [Fact]
    public void Normalise_LastLineWithoutEnding_IsRewrittenToo()
    {
        string configuration = "auth-user-pass f\n" + Ca + "auth-user-pass g";

        Assert.Equal("auth-user-pass\n" + Ca + "auth-user-pass", ServerContentHash.Normalise(configuration));
        Assert.Equal("cf0877f7dc9e9e54e4920fe77de17c44497fa54da576096401e8db591cef0c3f", ServerContentHash.Compute(configuration));
    }
}
