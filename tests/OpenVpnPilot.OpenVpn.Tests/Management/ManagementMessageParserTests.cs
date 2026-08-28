using OpenVpnPilot.OpenVpn.Management;

namespace OpenVpnPilot.OpenVpn.Tests.Management;

/// <summary>
/// Every input in this file was captured from a real OpenVPN 2.7.6 management session.
/// </summary>
public sealed class ManagementMessageParserTests
{
    [Fact]
    public void Parse_ConnectedState_ExtractsAddressesAndPort()
    {
        StateMessage message = Assert.IsType<StateMessage>(
            ManagementMessageParser.Parse(">STATE:1787941299,CONNECTED,SUCCESS,192.168.255.6,127.0.0.1,1194,,"));

        Assert.Equal("CONNECTED", message.Name);
        Assert.Equal("SUCCESS", message.Description);
        Assert.Equal("192.168.255.6", message.LocalAddress);
        Assert.Equal("127.0.0.1", message.RemoteAddress);
        Assert.Equal(1194, message.RemotePort);
        Assert.Null(message.LocalPort);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787941299), message.Timestamp);
    }

    [Theory]
    [InlineData(">STATE:1787941298,WAIT,,,,,,", "WAIT")]
    [InlineData(">STATE:1787941298,AUTH,,,,,,", "AUTH")]
    [InlineData(">STATE:1787941299,GET_CONFIG,,,,,,", "GET_CONFIG")]
    [InlineData(">STATE:1787941299,ADD_ROUTES,,,,,,", "ADD_ROUTES")]
    public void Parse_StateWithoutAddresses_LeavesOptionalFieldsNull(string line, string expectedName)
    {
        StateMessage message = Assert.IsType<StateMessage>(ManagementMessageParser.Parse(line));

        Assert.Equal(expectedName, message.Name);
        Assert.Null(message.Description);
        Assert.Null(message.LocalAddress);
        Assert.Null(message.RemoteAddress);
        Assert.Null(message.RemotePort);
    }

    [Fact]
    public void Parse_AssignIpState_CarriesOnlyTheTunnelAddress()
    {
        StateMessage message = Assert.IsType<StateMessage>(
            ManagementMessageParser.Parse(">STATE:1787941299,ASSIGN_IP,,192.168.255.6,,,,"));

        Assert.Equal("ASSIGN_IP", message.Name);
        Assert.Equal("192.168.255.6", message.LocalAddress);
        Assert.Null(message.RemoteAddress);
    }

    [Fact]
    public void Parse_ReconnectingState_ExposesRestartReasonAsDescription()
    {
        StateMessage message = Assert.IsType<StateMessage>(
            ManagementMessageParser.Parse(">STATE:1787941214,RECONNECTING,process-push-msg-failed,,,,,"));

        Assert.Equal("RECONNECTING", message.Name);
        Assert.Equal("process-push-msg-failed", message.Description);
    }

    [Theory]
    [InlineData(">BYTECOUNT:3196,4624", 3196L, 4624L)]
    [InlineData(">BYTECOUNT:4653,37212", 4653L, 37212L)]
    [InlineData(">BYTECOUNT:0,0", 0L, 0L)]
    public void Parse_ByteCount_ExtractsBothCounters(string line, long expectedIn, long expectedOut)
    {
        ByteCountMessage message = Assert.IsType<ByteCountMessage>(ManagementMessageParser.Parse(line));

        Assert.Equal(expectedIn, message.BytesIn);
        Assert.Equal(expectedOut, message.BytesOut);
    }

    [Fact]
    public void Parse_ByteCount_SurvivesCounterLargerThanInt32()
    {
        ByteCountMessage message = Assert.IsType<ByteCountMessage>(
            ManagementMessageParser.Parse(">BYTECOUNT:5368709120,9663676416"));

        Assert.Equal(5368709120L, message.BytesIn);
        Assert.Equal(9663676416L, message.BytesOut);
    }

    [Fact]
    public void Parse_ByteCount_WithMalformedPayload_DoesNotThrow()
    {
        UnknownNotificationMessage message = Assert.IsType<UnknownNotificationMessage>(
            ManagementMessageParser.Parse(">BYTECOUNT:not-a-number"));

        Assert.Equal("BYTECOUNT", message.Kind);
    }

    [Fact]
    public void Parse_Hold_ExtractsTimeoutAfterTheFinalColon()
    {
        HoldMessage message = Assert.IsType<HoldMessage>(
            ManagementMessageParser.Parse(">HOLD:Waiting for hold release:0"));

        Assert.Equal("Waiting for hold release", message.Text);
        Assert.Equal(0, message.TimeoutSeconds);
    }

    [Fact]
    public void Parse_Info_KeepsFullBanner()
    {
        InfoMessage message = Assert.IsType<InfoMessage>(
            ManagementMessageParser.Parse(">INFO:OpenVPN Management Interface Version 6 -- type 'help' for more info"));

        Assert.StartsWith("OpenVPN Management Interface Version 6", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Log_SeparatesSeverityFromText()
    {
        LogMessage message = Assert.IsType<LogMessage>(ManagementMessageParser.Parse(
            ">LOG:1787941213,I,TCP/UDP: Preserving recently used remote address: [AF_INET]127.0.0.1:1194"));

        Assert.Equal(LogSeverity.Informational, message.Severity);
        Assert.Equal("TCP/UDP: Preserving recently used remote address: [AF_INET]127.0.0.1:1194", message.Text);
    }

    [Fact]
    public void Parse_Log_WithEmptyFlags_IsVerbose()
    {
        LogMessage message = Assert.IsType<LogMessage>(
            ManagementMessageParser.Parse(">LOG:1787941213,,Need hold release from management interface, waiting..."));

        Assert.Equal(LogSeverity.Verbose, message.Severity);
        Assert.Equal("Need hold release from management interface, waiting...", message.Text);
    }

    [Fact]
    public void Parse_Log_KeepsCommasAndNestedNotificationsInText()
    {
        LogMessage message = Assert.IsType<LogMessage>(
            ManagementMessageParser.Parse(">LOG:1787941213,,MANAGEMENT: >STATE:1787941213,WAIT,,,,,,"));

        Assert.Equal("MANAGEMENT: >STATE:1787941213,WAIT,,,,,,", message.Text);
    }

    [Fact]
    public void Parse_Log_NonFatalErrorSeverity()
    {
        LogMessage message = Assert.IsType<LogMessage>(ManagementMessageParser.Parse(
            ">LOG:1787941214,N,OPTIONS ERROR: server pushed compression settings that are not allowed"));

        Assert.Equal(LogSeverity.NonFatalError, message.Severity);
    }

    [Fact]
    public void Parse_PasswordRequest_WithUsername()
    {
        PasswordRequestMessage message = Assert.IsType<PasswordRequestMessage>(
            ManagementMessageParser.Parse(">PASSWORD:Need 'Auth' username/password"));

        Assert.Equal("Auth", message.Realm);
        Assert.True(message.NeedsUsername);
    }

    [Fact]
    public void Parse_PasswordRequest_PrivateKeyNeedsPasswordOnly()
    {
        PasswordRequestMessage message = Assert.IsType<PasswordRequestMessage>(
            ManagementMessageParser.Parse(">PASSWORD:Need 'Private Key' password"));

        Assert.Equal("Private Key", message.Realm);
        Assert.False(message.NeedsUsername);
    }

    [Fact]
    public void Parse_PasswordVerificationFailed_ExtractsRealmAndReason()
    {
        PasswordVerificationFailedMessage message = Assert.IsType<PasswordVerificationFailedMessage>(
            ManagementMessageParser.Parse(">PASSWORD:Verification Failed: 'Auth' ['invalid credentials']"));

        Assert.Equal("Auth", message.Realm);
        Assert.Equal("invalid credentials", message.Reason);
    }

    [Fact]
    public void Parse_Fatal_KeepsText()
    {
        FatalMessage message = Assert.IsType<FatalMessage>(
            ManagementMessageParser.Parse(">FATAL:Options error: You must define CA file"));

        Assert.Equal("Options error: You must define CA file", message.Text);
    }

    [Theory]
    [InlineData("SUCCESS: hold release succeeded", "hold release succeeded")]
    [InlineData("SUCCESS: password is correct", "password is correct")]
    [InlineData("SUCCESS: real-time state notification set to ON", "real-time state notification set to ON")]
    [InlineData("SUCCESS: bytecount interval changed", "bytecount interval changed")]
    public void Parse_SuccessResponse_IsTerminal(string line, string expectedText)
    {
        CommandResponseMessage message = Assert.IsType<CommandResponseMessage>(ManagementMessageParser.Parse(line));

        Assert.Equal(CommandResponseKind.Success, message.Kind);
        Assert.Equal(expectedText, message.Text);
        Assert.True(message.IsTerminal);
    }

    [Fact]
    public void Parse_ErrorResponse_IsTerminal()
    {
        CommandResponseMessage message = Assert.IsType<CommandResponseMessage>(
            ManagementMessageParser.Parse("ERROR: unknown command [foo], enter 'help' for more options"));

        Assert.Equal(CommandResponseKind.Error, message.Kind);
        Assert.True(message.IsTerminal);
    }

    [Fact]
    public void Parse_End_IsTerminal()
    {
        CommandResponseMessage message = Assert.IsType<CommandResponseMessage>(ManagementMessageParser.Parse("END"));

        Assert.Equal(CommandResponseKind.End, message.Kind);
        Assert.True(message.IsTerminal);
    }

    [Fact]
    public void Parse_DumpLine_IsContinuationAndNotTerminal()
    {
        CommandResponseMessage message = Assert.IsType<CommandResponseMessage>(
            ManagementMessageParser.Parse("1787941144,I,OpenVPN 2.7.6 Windows"));

        Assert.Equal(CommandResponseKind.Continuation, message.Kind);
        Assert.False(message.IsTerminal);
    }

    [Fact]
    public void Parse_UnmodelledNotification_IsPreservedRatherThanDropped()
    {
        UnknownNotificationMessage message = Assert.IsType<UnknownNotificationMessage>(
            ManagementMessageParser.Parse(">NEED-OK:token-insertion-request:Need token insertion"));

        Assert.Equal("NEED-OK", message.Kind);
        Assert.Equal("token-insertion-request:Need token insertion", message.Payload);
        Assert.Equal(">NEED-OK:token-insertion-request:Need token insertion", message.RawLine);
    }
}
