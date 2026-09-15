using System.Globalization;
using System.Text;

namespace OpenVpnPilot.OpenVpn.Configuration;

/// <summary>
/// Changes the few things people change in a configuration by hand, and leaves every other line as
/// it was.
/// </summary>
/// <remarks>
/// A configuration is not regenerated from what was parsed. The parser is permissive and a round
/// trip through it would lose comments, ordering and whatever it does not model, and a profile that
/// comes back from an edit different in places nobody touched is a profile nobody can trust to be
/// the one they had. So each change finds the lines it concerns and replaces those, and the file
/// keeps its line endings.
/// </remarks>
public static class OvpnConfigEditor
{
    /// <summary>
    /// The inline blocks the form offers, in the order it shows them.
    /// </summary>
    public static readonly IReadOnlyList<string> KeyBlocks = ["ca", "cert", "key"];

    /// <summary>
    /// The blocks that carry the additional TLS key. A configuration has at most one of them.
    /// </summary>
    public static readonly IReadOnlyList<string> TlsKeyBlocks = ["tls-crypt-v2", "tls-crypt", "tls-auth"];

    /// <summary>
    /// Reads the server, port and protocol the first remote resolves to.
    /// </summary>
    public static OvpnEndpoint? ReadEndpoint(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        OvpnConfiguration configuration = OvpnConfigParser.Parse(content);

        return configuration.Remotes.Count == 0
            ? null
            : new OvpnEndpoint(
                configuration.Remotes[0].Host,
                configuration.Remotes[0].Port,
                configuration.Remotes[0].Protocol,
                configuration.Remotes.Count);
    }

    /// <summary>
    /// Points the first remote at a different server, port or protocol.
    /// </summary>
    /// <remarks>
    /// The protocol goes where the configuration already keeps it. A remote line that names one gets
    /// the new one; otherwise a proto directive that sets it for every remote is changed, so the file
    /// keeps a single place saying which protocol is used; and only when neither exists is it written
    /// onto the remote line. A token such as udp4 or tcp6-client keeps its address family.
    /// </remarks>
    public static string SetEndpoint(string content, string host, int port, OvpnProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        Lines lines = Lines.Split(content);
        OvpnConfiguration configuration = OvpnConfigParser.Parse(content);

        OvpnDirective? remote = configuration.Directives.FirstOrDefault(directive => directive.Name == "remote");
        OvpnDirective? proto = configuration.Directives.FirstOrDefault(directive => directive.Name == "proto");
        OvpnProtocol current = configuration.Remotes.Count > 0 ? configuration.Remotes[0].Protocol : OvpnProtocol.Udp;

        string? remoteProtocol = remote?.ArgumentAt(2);

        if (remoteProtocol is not null)
        {
            remoteProtocol = Retarget(remoteProtocol, protocol);
        }
        else if (protocol != current)
        {
            if (proto?.FirstArgument is { } shared)
            {
                lines.Replace(proto.LineNumber, $"proto {Retarget(shared, protocol)}");
            }
            else
            {
                remoteProtocol = Name(protocol);
            }
        }

        string line = string.Create(CultureInfo.InvariantCulture, $"remote {Quote(host.Trim())} {port}");

        if (remoteProtocol is not null)
        {
            line += " " + remoteProtocol;
        }

        if (remote is not null)
        {
            lines.Replace(remote.LineNumber, line);
        }
        else
        {
            lines.Insert(InsertionPointForRemote(configuration), line);
        }

        return lines.Join();
    }

    /// <summary>
    /// Replaces the contents of an inline block, adds the block, or removes it when the contents are
    /// empty.
    /// </summary>
    /// <remarks>
    /// Adding a block also removes a directive that points the same option at a file. A stored
    /// profile cannot reach a file beside the one it was imported from, and leaving both would keep
    /// a line that reads as if it mattered.
    /// </remarks>
    public static string SetBlock(string content, string name, string? blockContent)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Lines lines = Lines.Split(content);
        string[] replacement = string.IsNullOrWhiteSpace(blockContent)
            ? []
            : blockContent.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n').Split('\n');

        (int open, int close)? block = FindBlock(lines, name);

        if (block is { } found)
        {
            if (replacement.Length == 0)
            {
                lines.RemoveRange(found.open, found.close - found.open + 1);
            }
            else
            {
                lines.RemoveRange(found.open + 1, found.close - found.open - 1);
                lines.InsertRange(found.open + 1, replacement);
            }

            return lines.Join();
        }

        if (replacement.Length == 0)
        {
            return content;
        }

        OvpnConfiguration configuration = OvpnConfigParser.Parse(content);

        foreach (OvpnDirective reference in configuration.Directives
            .Where(directive => directive.Name == name && directive.Arguments.Count > 0)
            .OrderByDescending(directive => directive.LineNumber))
        {
            lines.RemoveRange(reference.LineNumber - 1, 1);
        }

        lines.TrimTrailingBlank();
        lines.Add($"<{name}>");
        lines.AddRange(replacement);
        lines.Add($"</{name}>");

        return lines.Join();
    }

    /// <summary>
    /// The contents of an inline block, or null when the configuration has none by that name.
    /// </summary>
    public static string? ReadBlock(string content, string name)
    {
        ArgumentNullException.ThrowIfNull(content);

        return OvpnConfigParser.Parse(content).InlineBlocks.TryGetValue(name, out OvpnInlineBlock? block)
            ? block.Content
            : null;
    }

    /// <summary>
    /// The protocol token with its family changed and its address family kept.
    /// </summary>
    private static string Retarget(string token, OvpnProtocol protocol)
    {
        string lower = token.ToLowerInvariant();
        OvpnProtocol family = lower.StartsWith("tcp", StringComparison.Ordinal) ? OvpnProtocol.Tcp : OvpnProtocol.Udp;

        if (family == protocol)
        {
            return token;
        }

        string version = lower.Length > 3 && lower[3] is '4' or '6' ? lower[3].ToString() : string.Empty;
        return Name(protocol) + version;
    }

    private static string Name(OvpnProtocol protocol) => protocol == OvpnProtocol.Tcp ? "tcp" : "udp";

    /// <summary>
    /// Quotes a host that the tokenizer would otherwise split, which nothing valid needs but a
    /// pasted value with a stray space would.
    /// </summary>
    private static string Quote(string host) =>
        host.Any(char.IsWhiteSpace) ? "\"" + host + "\"" : host;

    /// <summary>
    /// Where a remote goes when the configuration has none: after the client directive when there is
    /// one, otherwise at the top.
    /// </summary>
    private static int InsertionPointForRemote(OvpnConfiguration configuration) =>
        configuration.Directives.FirstOrDefault(directive => directive.Name is "client" or "tls-client")?.LineNumber ?? 0;

    private static (int Open, int Close)? FindBlock(Lines lines, string name)
    {
        string opening = $"<{name}>";
        string closing = $"</{name}>";

        for (int index = 0; index < lines.Count; index++)
        {
            if (!string.Equals(lines[index].Trim(), opening, StringComparison.Ordinal))
            {
                continue;
            }

            for (int end = index + 1; end < lines.Count; end++)
            {
                if (string.Equals(lines[end].Trim(), closing, StringComparison.Ordinal))
                {
                    return (index, end);
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// The lines of a file, numbered the way the parser numbers them, with the file's own line ending.
    /// </summary>
    private sealed class Lines : List<string>
    {
        private string newline = "\n";
        private bool endsWithNewline;

        public static Lines Split(string content)
        {
            Lines lines = new()
            {
                newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
                endsWithNewline = content.EndsWith('\n'),
            };

            using StringReader reader = new(content);

            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }

        /// <summary>
        /// Replaces a line given the one based number the parser reports.
        /// </summary>
        public void Replace(int lineNumber, string text) => this[lineNumber - 1] = text;

        public void TrimTrailingBlank()
        {
            while (Count > 0 && this[^1].Trim().Length == 0)
            {
                RemoveAt(Count - 1);
            }
        }

        public string Join()
        {
            StringBuilder builder = new();

            for (int index = 0; index < Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(newline);
                }

                builder.Append(this[index]);
            }

            if (endsWithNewline && Count > 0)
            {
                builder.Append(newline);
            }

            return builder.ToString();
        }
    }
}

/// <summary>
/// The server a configuration connects to first.
/// </summary>
/// <param name="RemoteCount">How many remotes the configuration names in all.</param>
public sealed record OvpnEndpoint(string Host, int Port, OvpnProtocol Protocol, int RemoteCount);
