using System.Text;
using OpenVpnPilot.OpenVpn.Management;

namespace OpenVpnPilot.OpenVpn.Tests.Management;

/// <summary>
/// The one time code protocol, both the form presented up front and the one raised after a refusal.
/// </summary>
public sealed class ChallengeTests
{
    [Fact]
    public void Parse_StaticChallengeWithEcho_ExposesPromptAndEchoFlag()
    {
        PasswordRequestMessage message = Assert.IsType<PasswordRequestMessage>(
            ManagementMessageParser.Parse(
                ">PASSWORD:Need 'Auth' username/password SC:1,Enter the code from your token"));

        Assert.Equal("Auth", message.Realm);
        Assert.True(message.NeedsUsername);
        Assert.NotNull(message.Challenge);
        Assert.Equal("Enter the code from your token", message.Challenge.Text);
        Assert.True(message.Challenge.Echo);
    }

    [Fact]
    public void Parse_StaticChallengeWithoutEcho_TreatsTheResponseAsSecret()
    {
        PasswordRequestMessage message = Assert.IsType<PasswordRequestMessage>(
            ManagementMessageParser.Parse(">PASSWORD:Need 'Auth' username/password SC:0,Passphrase"));

        Assert.NotNull(message.Challenge);
        Assert.False(message.Challenge.Echo);
    }

    [Fact]
    public void Parse_StaticChallengePromptContainingCommas_KeepsTheWholePrompt()
    {
        PasswordRequestMessage message = Assert.IsType<PasswordRequestMessage>(
            ManagementMessageParser.Parse(
                ">PASSWORD:Need 'Auth' username/password SC:1,Enter code, then press return"));

        Assert.NotNull(message.Challenge);
        Assert.Equal("Enter code, then press return", message.Challenge.Text);
    }

    [Fact]
    public void Parse_OrdinaryPasswordRequest_HasNoChallenge()
    {
        PasswordRequestMessage message = Assert.IsType<PasswordRequestMessage>(
            ManagementMessageParser.Parse(">PASSWORD:Need 'Auth' username/password"));

        Assert.Null(message.Challenge);
    }

    [Fact]
    public void Parse_DynamicChallenge_ExposesStateUsernameAndPrompt()
    {
        string username = Convert.ToBase64String(Encoding.UTF8.GetBytes("operator"));
        string line = $">PASSWORD:Verification Failed: 'Auth' ['CRV1:R,E:Sm9obg==:{username}:Enter your token code']";

        PasswordVerificationFailedMessage message =
            Assert.IsType<PasswordVerificationFailedMessage>(ManagementMessageParser.Parse(line));

        Assert.Equal("Auth", message.Realm);
        Assert.NotNull(message.Challenge);
        Assert.Equal("Sm9obg==", message.Challenge.StateId);
        Assert.Equal("operator", message.Challenge.Username);
        Assert.Equal("Enter your token code", message.Challenge.Text);
        Assert.True(message.Challenge.Echo);
        Assert.True(message.Challenge.ResponseRequired);
    }

    [Fact]
    public void Parse_DynamicChallengePromptContainingColons_KeepsTheWholePrompt()
    {
        string line = ">PASSWORD:Verification Failed: 'Auth' ['CRV1:R:state1::Approve then reply: yes']";

        PasswordVerificationFailedMessage message =
            Assert.IsType<PasswordVerificationFailedMessage>(ManagementMessageParser.Parse(line));

        Assert.NotNull(message.Challenge);
        Assert.Equal("Approve then reply: yes", message.Challenge.Text);
        Assert.Null(message.Challenge.Username);
        Assert.False(message.Challenge.Echo);
    }

    [Fact]
    public void Parse_PlainVerificationFailure_IsNotMistakenForAChallenge()
    {
        PasswordVerificationFailedMessage message = Assert.IsType<PasswordVerificationFailedMessage>(
            ManagementMessageParser.Parse(">PASSWORD:Verification Failed: 'Auth' ['AUTH_FAILED']"));

        Assert.Null(message.Challenge);
        Assert.Equal("AUTH_FAILED", message.Reason);
    }

    [Fact]
    public void ForStaticChallenge_EncodesBothPartsAsBase64()
    {
        Assert.Equal(
            "SCRV1:cGFzc3dvcmQ=:MTIzNDU2",
            ChallengeEncoding.ForStaticChallenge("password", "123456"));
    }

    [Fact]
    public void ForDynamicChallenge_KeepsTheStateAndLeavesTheOtherFieldsEmpty()
    {
        Assert.Equal(
            "CRV1::Sm9obg==::123456",
            ChallengeEncoding.ForDynamicChallenge("Sm9obg==", "123456"));
    }
}
