using OpenVpnPilot.Platform.MacOS.Helper.Policy;

namespace OpenVpnPilot.Platform.MacOS.Tests.Policy;

/// <summary>
/// The word splitting the helper shares with OpenVPN's parse_line.
/// </summary>
/// <remarks>
/// Every case here is one where a parser that is merely plausible would read a line differently
/// from OpenVPN, and a directive the helper did not see would run as root.
/// </remarks>
public sealed class OpenVpnLineParserTests
{
    [Theory]
    [InlineData("remote vpn.example.com 1194 udp\n", new[] { "remote", "vpn.example.com", "1194", "udp" })]
    [InlineData("  remote\tvpn.example.com   1194\r\n", new[] { "remote", "vpn.example.com", "1194" })]
    [InlineData("verify-x509-name \"CN=a \\\"b\\\" c\" name\n", new[] { "verify-x509-name", "CN=a \"b\" c", "name" })]
    [InlineData("x 'a\\b c'\n", new[] { "x", "a\\b c" })]
    [InlineData("x a\\ b\n", new[] { "x", "a b" })]
    [InlineData("x a#b a;b\n", new[] { "x", "a#b", "a;b" })]
    [InlineData("remote host # up /tmp/x\n", new[] { "remote", "host" })]
    [InlineData("remote host ; up /tmp/x\n", new[] { "remote", "host" })]
    [InlineData("x \"\"\n", new[] { "x", "" })]
    [InlineData("\"up\" /tmp/x\n", new[] { "up", "/tmp/x" })]
    public void Parse_SplitsWordsTheWayOpenVpnDoes(string line, string[] expected)
    {
        Assert.Equal(expected, OpenVpnLineParser.Parse(line, 1));
    }

    [Theory]
    [InlineData("# up /tmp/x\n")]
    [InlineData("; up /tmp/x\n")]
    [InlineData("   \t\r\n")]
    [InlineData("")]
    public void Parse_CommentOrBlank_HasNoWords(string line)
    {
        Assert.Empty(OpenVpnLineParser.Parse(line, 1));
    }

    /// <summary>
    /// A backslash does not protect a comment character at the start of a word: OpenVPN tests for the
    /// comment before it looks at the backslash, so the line is a comment and nothing on it runs.
    /// </summary>
    [Fact]
    public void Parse_EscapedCommentCharacterAtAWordStart_IsStillAComment()
    {
        Assert.Empty(OpenVpnLineParser.Parse("\\# up /tmp/x\n", 1));
    }

    [Theory]
    [InlineData("x a\\qb\n")]
    [InlineData("x \"unterminated\n")]
    [InlineData("x 'unterminated\n")]
    public void Parse_WhatOpenVpnRefuses_IsRefused(string line)
    {
        Assert.Throws<ConfigurationRefusedException>(() => OpenVpnLineParser.Parse(line, 7));
    }

    /// <summary>
    /// OpenVPN keeps sixteen words and drops the rest without a word, so the two parsers would
    /// disagree about anything after them.
    /// </summary>
    [Fact]
    public void Parse_MoreThanSixteenWords_IsRefused()
    {
        string sixteen = string.Join(' ', Enumerable.Range(1, 16).Select(number => $"w{number}"));

        Assert.Equal(16, OpenVpnLineParser.Parse(sixteen + "\n", 1).Count);
        Assert.Throws<ConfigurationRefusedException>(() => OpenVpnLineParser.Parse(sixteen + " up\n", 1));
    }

    [Fact]
    public void Parse_SixteenWordsFollowedByAComment_IsAccepted()
    {
        string sixteen = string.Join(' ', Enumerable.Range(1, 16).Select(number => $"w{number}"));

        Assert.Equal(16, OpenVpnLineParser.Parse(sixteen + " # note\n", 1).Count);
    }

    [Fact]
    public void Parse_AWordOpenVpnWouldCutShort_IsRefused()
    {
        Assert.Throws<ConfigurationRefusedException>(
            () => OpenVpnLineParser.Parse("x " + new string('a', 256) + "\n", 1));
    }
}
