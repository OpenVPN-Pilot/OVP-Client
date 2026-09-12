using System.Text;

namespace OpenVpnPilot.Platform.MacOS.Helper.Policy;

/// <summary>
/// Splits one configuration line into words exactly the way OpenVPN 2.7 does.
/// </summary>
/// <remarks>
/// A port of parse_line in options_parse.c, character for character, because the helper decides
/// what may run as root from what this returns and OpenVPN acts on what its own parser returns. Any
/// difference between the two is a directive the helper did not see. Where OpenVPN would refuse a
/// line, this refuses it as well rather than guessing what OpenVPN would have made of it.
///
/// OpenVPN works on bytes and only ever tests them against ASCII, so characters outside ASCII are
/// ordinary word characters here too. A character is a space when C's isspace says so in the C
/// locale: space, tab, line feed, vertical tab, form feed and carriage return.
/// </remarks>
internal static class OpenVpnLineParser
{
    /// <summary>
    /// OPTION_PARM_SIZE: a word of this many bytes or more is refused by OpenVPN.
    /// </summary>
    public const int MaximumWordBytes = 256;

    /// <summary>
    /// MAX_PARMS: OpenVPN reads this many words from a line and silently drops the rest.
    /// </summary>
    public const int MaximumWords = 16;

    /// <summary>
    /// Parses one line, including the line feed that ended it when there was one.
    /// </summary>
    /// <returns>The words, possibly none for a blank line or a comment.</returns>
    /// <exception cref="ConfigurationRefusedException">OpenVPN would not accept the line.</exception>
    public static IReadOnlyList<string> Parse(string line, int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(line);

        const int initial = 0;
        const int quoted = 1;
        const int unquoted = 2;
        const int done = 3;
        const int singleQuoted = 4;

        List<string> words = [];
        StringBuilder word = new();
        int state = initial;
        bool backslash = false;

        // The C loop runs over the terminating NUL as well, which is what ends a final word.
        for (int index = 0; index <= line.Length; index++)
        {
            char input = index < line.Length ? line[index] : '\0';
            char output = '\0';

            if (!backslash && input == '\\' && state != singleQuoted)
            {
                backslash = true;
            }
            else
            {
                if (state == initial)
                {
                    if (!IsSpace(input))
                    {
                        // A comment starts at a word boundary, and a backslash does not protect it.
                        if (input is ';' or '#')
                        {
                            break;
                        }

                        if (!backslash && input == '"')
                        {
                            state = quoted;
                        }
                        else if (!backslash && input == '\'')
                        {
                            state = singleQuoted;
                        }
                        else
                        {
                            output = input;
                            state = unquoted;
                        }
                    }
                }
                else if (state == unquoted)
                {
                    if (!backslash && IsSpace(input))
                    {
                        state = done;
                    }
                    else
                    {
                        output = input;
                    }
                }
                else if (state == quoted)
                {
                    if (!backslash && input == '"')
                    {
                        state = done;
                    }
                    else
                    {
                        output = input;
                    }
                }
                else if (state == singleQuoted)
                {
                    if (input == '\'')
                    {
                        state = done;
                    }
                    else
                    {
                        output = input;
                    }
                }

                if (state == done)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    state = initial;
                }

                if (backslash && output != '\0' && !(output is '\\' or '"' || IsSpace(output)))
                {
                    throw new ConfigurationRefusedException(lineNumber, "uses a backslash OpenVPN does not accept");
                }

                backslash = false;
            }

            if (output != '\0')
            {
                word.Append(output);

                if (Encoding.UTF8.GetByteCount(word.ToString()) >= MaximumWordBytes)
                {
                    throw new ConfigurationRefusedException(lineNumber, "has a word longer than OpenVPN accepts");
                }
            }

            if (words.Count >= MaximumWords)
            {
                // OpenVPN stops reading here and drops whatever follows without a word. Anything that
                // does follow would be read differently by the two parsers, so it is refused.
                if (!RestIsEmpty(line, index + 1))
                {
                    throw new ConfigurationRefusedException(lineNumber, $"has more than {MaximumWords} words");
                }

                return words;
            }
        }

        return state switch
        {
            quoted => throw new ConfigurationRefusedException(lineNumber, "has no closing quotation mark"),
            singleQuoted => throw new ConfigurationRefusedException(lineNumber, "has no closing single quotation mark"),
            initial => words,
            _ => throw new ConfigurationRefusedException(lineNumber, "ends in the middle of a word"),
        };
    }

    /// <summary>
    /// C's isspace in the C locale, plus the NUL that ends a C string.
    /// </summary>
    public static bool IsSpace(char character) =>
        character is '\0' or ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary>
    /// True when nothing but spaces or a comment follows a position.
    /// </summary>
    private static bool RestIsEmpty(string line, int start)
    {
        for (int index = start; index < line.Length; index++)
        {
            if (line[index] is ';' or '#')
            {
                return true;
            }

            if (!IsSpace(line[index]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A configuration the helper will not start, with the line and the reason.
/// </summary>
internal sealed class ConfigurationRefusedException : Exception
{
    public ConfigurationRefusedException()
    {
    }

    public ConfigurationRefusedException(string message)
        : base(message)
    {
    }

    public ConfigurationRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ConfigurationRefusedException(int lineNumber, string reason)
        : base($"Line {lineNumber} {reason}.")
    {
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }
}
