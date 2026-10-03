using System.Text;

namespace OpenVpnPilot.Data.Import;

/// <summary>
/// The content hash a configuration gets once the server has stored it.
/// </summary>
/// <remarks>
/// The server keeps every byte of an uploaded configuration but one: a line <c>auth-user-pass
/// &lt;file&gt;</c> becomes a bare <c>auth-user-pass</c>, because the file holds a user name and
/// password that belong in the vault and exist on no other machine. A profile uploaded from here can
/// therefore come back with a different hash than the local copy has, and recognising it as the same
/// profile means rewriting the line the way the server does before hashing.
///
/// This follows the server's <c>OvpnInspector</c> and <c>OvpnLine</c> exactly: lines end at
/// <c>\r\n</c>, <c>\n</c> or a lone <c>\r</c>; inline blocks and comments are skipped; quotes group
/// words; the line keeps its indentation and its ending, and everything after the indentation is
/// replaced. Any difference, however small, makes a duplicate unrecognisable.
/// </remarks>
public static class ServerContentHash
{
    private const string Directive = "auth-user-pass";

    /// <summary>
    /// The hash of the configuration as the server would store it.
    /// </summary>
    public static string Compute(string configuration) => ProfileImporter.ComputeHash(Normalise(configuration));

    /// <summary>
    /// The configuration as the server would store it.
    /// </summary>
    public static string Normalise(string configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        List<Line> rewritten = [];
        string? openBlock = null;

        foreach (Line raw in Split(configuration))
        {
            string line = raw.Text.Trim();

            if (openBlock is not null)
            {
                if (line == $"</{openBlock}>")
                {
                    openBlock = null;
                }

                continue;
            }

            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (IsOpeningTag(line))
            {
                openBlock = line[1..^1];
                continue;
            }

            if (Tokenize(line) is [Directive, _, ..])
            {
                rewritten.Add(raw);
            }
        }

        return Replace(configuration, rewritten);
    }

    private static bool IsOpeningTag(string line) =>
        line.Length >= 3 && line[0] == '<' && line[1] != '/' && line[^1] == '>' && !line[1..^1].Any(char.IsWhiteSpace);

    private static List<Line> Split(string text)
    {
        List<Line> lines = [];
        int start = 0;

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            lines.Add(new Line(start, text[start..index]));

            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        if (start < text.Length)
        {
            lines.Add(new Line(start, text[start..]));
        }

        return lines;
    }

    private static List<string> Tokenize(string line)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        char quote = '\0';
        bool hasToken = false;

        foreach (char character in line)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(character);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string Replace(string text, List<Line> lines)
    {
        if (lines.Count == 0)
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        int copied = 0;

        foreach (Line line in lines)
        {
            int indent = line.Text.Length - line.Text.TrimStart().Length;
            result.Append(text, copied, line.Start + indent - copied).Append(Directive);
            copied = line.Start + line.Text.Length;
        }

        return result.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// One line and where it starts, so it can be rewritten without touching the bytes around it.
    /// </summary>
    private readonly record struct Line(int Start, string Text);
}
