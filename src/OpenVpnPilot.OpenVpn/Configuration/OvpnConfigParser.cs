using System.Text;

namespace OpenVpnPilot.OpenVpn.Configuration;

/// <summary>
/// Reads OpenVPN configuration text into a structured form.
/// </summary>
/// <remarks>
/// The parser is deliberately permissive. Configuration files come from many sources and an
/// unrecognised directive is preserved verbatim rather than rejected, so that round tripping a
/// profile never loses information.
/// </remarks>
public static class OvpnConfigParser
{
    public static OvpnConfiguration Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<OvpnDirective> directives = [];
        Dictionary<string, OvpnInlineBlock> inlineBlocks = new(StringComparer.Ordinal);

        string? openBlockName = null;
        StringBuilder openBlockContent = new();
        int lineNumber = 0;

        foreach (string rawLine in SplitLines(content))
        {
            lineNumber++;
            string line = rawLine.Trim();

            if (openBlockName is not null)
            {
                if (IsClosingTag(line, openBlockName))
                {
                    inlineBlocks[openBlockName] = new OvpnInlineBlock(
                        openBlockName,
                        openBlockContent.ToString().TrimEnd('\r', '\n'));

                    openBlockName = null;
                    openBlockContent.Clear();
                    continue;
                }

                // Block content is kept byte for byte; certificates must not be reformatted.
                openBlockContent.Append(rawLine.TrimEnd('\r')).Append('\n');
                continue;
            }

            if (line.Length == 0 || IsComment(line))
            {
                continue;
            }

            if (TryReadOpeningTag(line, out string? blockName))
            {
                openBlockName = blockName;
                continue;
            }

            directives.Add(ParseDirective(line, lineNumber));
        }

        // An unterminated block still carries usable content, so it is kept rather than discarded.
        if (openBlockName is not null)
        {
            inlineBlocks[openBlockName] = new OvpnInlineBlock(
                openBlockName,
                openBlockContent.ToString().TrimEnd('\r', '\n'));
        }

        return new OvpnConfiguration(directives, inlineBlocks);
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        using StringReader reader = new(content);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static bool IsComment(string line) => line[0] is '#' or ';';

    private static bool TryReadOpeningTag(string line, out string? name)
    {
        name = null;

        if (line.Length < 3 || line[0] != '<' || line[1] == '/' || line[^1] != '>')
        {
            return false;
        }

        string candidate = line[1..^1];
        if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace))
        {
            return false;
        }

        name = candidate;
        return true;
    }

    private static bool IsClosingTag(string line, string blockName) =>
        line.Length == blockName.Length + 3
        && line.StartsWith("</", StringComparison.Ordinal)
        && line[^1] == '>'
        && string.CompareOrdinal(line, 2, blockName, 0, blockName.Length) == 0;

    private static OvpnDirective ParseDirective(string line, int lineNumber)
    {
        List<string> tokens = Tokenize(line);
        string name = tokens.Count > 0 ? tokens[0] : string.Empty;
        List<string> arguments = tokens.Count > 1 ? tokens[1..] : [];

        return new OvpnDirective(name, arguments, lineNumber);
    }

    // OpenVPN allows single and double quoted arguments so that paths may contain spaces.
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

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(character);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
