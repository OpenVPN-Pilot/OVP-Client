using System.Text.RegularExpressions;
using System.Text;

namespace OpenVpnPilot.Platform.MacOS.Helper.Policy;

/// <summary>
/// Decides whether a configuration may be run as root, and writes the copy that is.
/// </summary>
/// <remarks>
/// On Windows OpenVPN runs as the user who asked for it and the interactive service does the
/// privileged parts. On macOS OpenVPN itself runs as root, so everything a configuration makes it do,
/// it does as root: run a program, load a library, write or read a file. For a process running under
/// an administrator's account that would be root without the password macOS otherwise asks for, so
/// these directives are refused for every caller except root, not only for callers who are not
/// authorised.
///
/// The configuration is not passed on as it arrived. It is read the way OpenVPN reads it, every
/// directive is checked, and a copy is written back out in one plain form: no comments, every
/// argument quoted, inline blocks exactly as read. OpenVPN then parses the copy, so what it acts on
/// is what was checked even where a line could be read two ways. Two such ways are known and are
/// refused outright: a line longer than OpenVPN reads in one piece, which it continues on the next
/// read and so starts a directive in the middle of what looked like a comment or a certificate, and
/// a NUL, after which OpenVPN stops reading a line that this parser would still see.
///
/// The list below was audited against the OpenVPN 2.7.7 manual and options_parse.c. A directive
/// not on it passes through and is judged by OpenVPN itself, which rejects anything it does not
/// know. The OpenVPN version the package builds is pinned for exactly this reason, and a test fails
/// when the pin moves without the audit moving with it.
/// </remarks>
internal static partial class ConfigurationPolicy
{
    /// <summary>
    /// The OpenVPN release the list of directives was checked against.
    /// </summary>
    public const string AuditedOpenVpnVersion = "2.7.7";

    /// <summary>
    /// The longest line, in bytes without its line feed, that OpenVPN reads in one piece everywhere:
    /// an inline block line is read into 256 bytes including the line feed and the terminator.
    /// </summary>
    public const int MaximumLineBytes = 254;

    /// <summary>
    /// More directives than any real configuration carries. A limit, so a request cannot make the
    /// helper build an arbitrarily large copy.
    /// </summary>
    public const int MaximumDirectives = 4096;

    private const string UnicodeByteOrderMark = "\uFEFF";

    private static readonly Dictionary<string, string> Refused = BuildRefusals();

    /// <summary>
    /// Directives whose argument is a file OpenVPN would open as root. They are accepted inline only,
    /// which is how an imported profile carries them anyway.
    /// </summary>
    private static readonly HashSet<string> InlineOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "ca", "cert", "key", "extra-certs", "dh", "pkcs12", "crl-verify",
        "tls-auth", "tls-crypt", "tls-crypt-v2", "secret", "http-proxy-user-pass",
    };

    /// <summary>
    /// The directives OpenVPN 2.7.7 accepts as an inline block, those verified with OPT_P_INLINE.
    /// </summary>
    /// <remarks>
    /// OpenVPN refuses any other block itself. The helper refuses it first, so a block it has no
    /// reason to expect never reaches the stage where OpenVPN decides what to make of it.
    /// </remarks>
    private static readonly HashSet<string> InlineAccepted = new(InlineOnly, StringComparer.OrdinalIgnoreCase)
    {
        "auth-user-pass", "peer-fingerprint", "connection",
    };

    /// <summary>
    /// Checks a configuration and produces the copy OpenVPN is given.
    /// </summary>
    /// <exception cref="ConfigurationRefusedException">Something in it may not be run as root.</exception>
    public static string Check(string configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Contains('\0', StringComparison.Ordinal))
        {
            throw new ConfigurationRefusedException(
                "The configuration contains a NUL character, after which OpenVPN would stop reading a line.");
        }

        List<Line> lines = SplitLines(configuration);
        List<Item> items = Read(lines, nested: false);

        StringBuilder copy = new();

        foreach (Item item in items)
        {
            Write(item, copy);
        }

        return copy.ToString();
    }

    /// <summary>
    /// Splits into lines the way fgets returns them, each with the line feed that ended it.
    /// </summary>
    private static List<Line> SplitLines(string configuration)
    {
        // OpenVPN skips a byte order mark at the very start of the file and nowhere else.
        if (configuration.StartsWith(UnicodeByteOrderMark, StringComparison.Ordinal))
        {
            configuration = configuration[UnicodeByteOrderMark.Length..];
        }

        List<Line> lines = [];
        int start = 0;
        int number = 0;

        while (start < configuration.Length)
        {
            int end = configuration.IndexOf('\n', start);
            number++;

            string text = end < 0 ? configuration[start..] : configuration[start..end];

            if (Encoding.UTF8.GetByteCount(text) > MaximumLineBytes)
            {
                throw new ConfigurationRefusedException(
                    number,
                    $"is longer than the {MaximumLineBytes} bytes OpenVPN reads in one piece");
            }

            lines.Add(new Line(number, text, HasLineFeed: end >= 0));
            start = end < 0 ? configuration.Length : end + 1;
        }

        return lines;
    }

    /// <summary>
    /// Reads directives and inline blocks in order, the way read_config_file does.
    /// </summary>
    private static List<Item> Read(List<Line> lines, bool nested)
    {
        List<Item> items = [];

        for (int index = 0; index < lines.Count; index++)
        {
            Line line = lines[index];
            List<string> words = [.. OpenVpnLineParser.Parse(line.Text + (line.HasLineFeed ? "\n" : string.Empty), line.Number)];

            if (words.Count == 0)
            {
                continue;
            }

            // bypass_doubledash: a leading -- is dropped from the directive name, as on the command line.
            if (words[0].Length >= 3 && words[0].StartsWith("--", StringComparison.Ordinal))
            {
                words[0] = words[0][2..];
            }

            if (words.Count == 1 && words[0].Length >= 2 && words[0][0] == '<' && words[0][^1] == '>')
            {
                string tag = words[0][1..^1];
                RequireName(tag, line.Number);

                List<Line> content = ReadBlock(lines, ref index, tag, line.Number);
                Item block = new(line.Number, tag, [], content, []);

                if (string.Equals(tag, "connection", StringComparison.OrdinalIgnoreCase))
                {
                    if (nested)
                    {
                        throw new ConfigurationRefusedException(line.Number, "opens a connection block inside another one");
                    }

                    block = block with { Nested = Read(content, nested: true) };
                }

                Admit(block);
                items.Add(block);
            }
            else
            {
                RequireName(words[0], line.Number);

                Item directive = new(line.Number, words[0], words[1..], null, []);
                Admit(directive);
                items.Add(directive);
            }

            if (items.Count > MaximumDirectives)
            {
                throw new ConfigurationRefusedException($"The configuration has more than {MaximumDirectives} directives.");
            }
        }

        return items;
    }

    /// <summary>
    /// Reads the lines of an inline block up to its end tag, as read_inline_file does.
    /// </summary>
    /// <remarks>
    /// OpenVPN ends the block at the first line that, after leading spaces, begins with the end tag,
    /// whatever follows the tag on that line. This does the same, so both agree where it ends.
    /// </remarks>
    private static List<Line> ReadBlock(List<Line> lines, ref int index, string tag, int startLine)
    {
        string end = $"</{tag}>";
        List<Line> content = [];

        while (++index < lines.Count)
        {
            string text = lines[index].Text;
            int first = 0;

            while (first < text.Length && OpenVpnLineParser.IsSpace(text[first]))
            {
                first++;
            }

            if (text.AsSpan(first).StartsWith(end, StringComparison.Ordinal))
            {
                return content;
            }

            content.Add(lines[index]);
        }

        throw new ConfigurationRefusedException(startLine, $"opens <{tag}> and never closes it");
    }

    /// <summary>
    /// Refuses a directive that may not run as root.
    /// </summary>
    private static void Admit(Item item)
    {
        string name = item.Name;
        IReadOnlyList<string> arguments = item.Arguments;

        // "setenv opt" marks the directive after it as optional: OpenVPN applies that directive, so
        // it is the one that is judged.
        if (string.Equals(name, "setenv", StringComparison.OrdinalIgnoreCase)
            && item.Content is null
            && arguments.Count >= 2
            && string.Equals(arguments[0], "opt", StringComparison.Ordinal))
        {
            RequireName(arguments[1], item.Line);

            if (string.Equals(arguments[1], "setenv", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationRefusedException(item.Line, "nests setenv opt inside setenv opt");
            }

            Admit(new Item(item.Line, arguments[1], arguments.Skip(2).ToList(), null, []));
            return;
        }

        if (Refused.TryGetValue(name, out string? reason)
            || name.StartsWith("management", StringComparison.OrdinalIgnoreCase)
                && Refused.TryGetValue("management", out reason))
        {
            throw new ConfigurationRefusedException(item.Line, $"uses '{name}', which {reason}");
        }

        if (item.Content is not null)
        {
            if (!InlineAccepted.Contains(name))
            {
                throw new ConfigurationRefusedException(item.Line, $"opens <{name}>, which is not a directive OpenVPN takes as an inline block");
            }

            // An inline block carries its data in the configuration, so nothing is opened by path.
            return;
        }

        if (InlineOnly.Contains(name))
        {
            throw new ConfigurationRefusedException(
                item.Line,
                $"names a file for '{name}', which OpenVPN would open as root. It is accepted as an inline block only");
        }

        switch (name.ToLowerInvariant())
        {
            case "auth-user-pass" when arguments.Count > 0:
            case "askpass" when arguments.Count > 0:
                throw new ConfigurationRefusedException(
                    item.Line,
                    $"names a file for '{name}', which OpenVPN would read as root and could send to the server");

            case "http-proxy" when arguments.Count > 2 && arguments[2] is not ("auto" or "auto-nct"):
            case "socks-proxy" when arguments.Count > 2:
                throw new ConfigurationRefusedException(
                    item.Line,
                    $"names a credentials file for '{name}', which OpenVPN would read as root");

            case "setenv":
                if (arguments.Count == 0 || !AllowedVariable().IsMatch(arguments[0]))
                {
                    throw new ConfigurationRefusedException(
                        item.Line,
                        "sets an environment variable, which reaches the programs OpenVPN runs as root. Only UV_ variables for the server are accepted");
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Writes one item in the plain form OpenVPN is given.
    /// </summary>
    private static void Write(Item item, StringBuilder copy)
    {
        if (item.Content is null)
        {
            StringBuilder line = new(item.Name);

            foreach (string argument in item.Arguments)
            {
                if (argument.Any(char.IsControl))
                {
                    throw new ConfigurationRefusedException(item.Line, "has an argument with a control character");
                }

                line.Append(' ').Append('"')
                    .Append(argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal))
                    .Append('"');
            }

            AppendLine(copy, line.ToString(), item.Line);
            return;
        }

        AppendLine(copy, $"<{item.Name}>", item.Line);

        if (item.Nested.Count > 0 || string.Equals(item.Name, "connection", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Item nested in item.Nested)
            {
                Write(nested, copy);
            }
        }
        else
        {
            foreach (Line line in item.Content)
            {
                AppendLine(copy, line.Text.TrimEnd('\r'), line.Number);
            }
        }

        AppendLine(copy, $"</{item.Name}>", item.Line);
    }

    private static void AppendLine(StringBuilder copy, string line, int number)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaximumLineBytes)
        {
            throw new ConfigurationRefusedException(number, $"would be longer than the {MaximumLineBytes} bytes OpenVPN reads in one piece");
        }

        copy.Append(line).Append('\n');
    }

    /// <summary>
    /// A directive name is letters, digits and dashes, which is every name OpenVPN has.
    /// </summary>
    private static void RequireName(string name, int line)
    {
        if (!DirectiveName().IsMatch(name))
        {
            throw new ConfigurationRefusedException(line, $"has '{name}' where a directive name belongs");
        }
    }

    private static Dictionary<string, string> BuildRefusals()
    {
        const string runsProgram = "runs a program, and OpenVPN runs as root";
        const string loadsCode = "loads code into OpenVPN, which runs as root";
        const string writesFile = "writes a file, and OpenVPN would write it as root";
        const string readsPath = "opens a path, and OpenVPN would open it as root";
        const string helperOwns = "the helper sets itself";
        const string dropsPrivileges = "changes the account OpenVPN runs as, after which it can no longer undo its routes and name servers";
        const string server = "runs OpenVPN as a server, which the helper does not do";

        Dictionary<string, string> refused = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in new[]
        {
            "up", "down", "route-up", "route-pre-down", "ipchange", "tls-verify", "tls-crypt-v2-verify",
            "auth-user-pass-verify", "client-connect", "client-disconnect", "learn-address",
            "dns-updown", "iproute",
        })
        {
            refused[name] = runsProgram;
        }

        foreach (string name in new[] { "plugin", "engine", "providers", "pkcs11-providers", "setcon" })
        {
            refused[name] = loadsCode;
        }

        foreach (string name in new[]
        {
            "log", "log-append", "writepid", "status", "replay-persist", "ifconfig-pool-persist",
            "tls-export-cert", "genkey", "mktun", "rmtun",
        })
        {
            refused[name] = writesFile;
        }

        foreach (string name in new[] { "config", "cd", "chroot", "capath", "dev-node", "tmp-dir", "win-sys", "service" })
        {
            refused[name] = readsPath;
        }

        foreach (string name in new[] { "script-security", "management", "daemon", "syslog" })
        {
            refused[name] = helperOwns;
        }

        refused["user"] = dropsPrivileges;
        refused["group"] = dropsPrivileges;

        foreach (string name in new[]
        {
            "mode", "server", "server-bridge", "server-ipv6", "tls-server", "port-share",
            "client-config-dir", "auth-gen-token-secret",
        })
        {
            refused[name] = server;
        }

        return refused;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]*$")]
    private static partial Regex DirectiveName();

    /// <summary>
    /// UV_ variables are sent to the server as peer information. FORWARD_COMPATIBLE is the old way
    /// of telling OpenVPN to skip directives it does not know. Neither reaches a program.
    /// </summary>
    [GeneratedRegex("^(UV_[A-Za-z0-9_]+|FORWARD_COMPATIBLE)$")]
    private static partial Regex AllowedVariable();

    private sealed record Line(int Number, string Text, bool HasLineFeed);

    /// <summary>
    /// One directive, or one inline block when <see cref="Content"/> is set.
    /// </summary>
    private sealed record Item(
        int Line,
        string Name,
        IReadOnlyList<string> Arguments,
        List<Line>? Content,
        List<Item> Nested);
}
