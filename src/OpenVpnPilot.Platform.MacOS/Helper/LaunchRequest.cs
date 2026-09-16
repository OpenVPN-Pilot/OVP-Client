using System.Text;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Helper;

/// <summary>
/// Turns what the application asks for into what the helper is told.
/// </summary>
/// <remarks>
/// Kept apart from the launcher that sends it, because deciding which option may be passed on and
/// which configuration is one an administrator installed is a decision worth testing on its own, on
/// any system.
/// </remarks>
internal static class LaunchRequest
{
    /// <summary>
    /// The file name within the configurations directory, or null for a file anywhere else.
    /// </summary>
    internal static string? InstalledName(string fullPath)
    {
        string directory = HelperInstallation.ConfigurationsDirectory + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(directory, StringComparison.Ordinal))
        {
            return null;
        }

        string name = fullPath[directory.Length..];

        return name.Length > 0 && !name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? name
            : null;
    }

    /// <summary>
    /// Reads the extra options, which may only be pull filters.
    /// </summary>
    internal static bool TryReadPullFilters(
        IReadOnlyList<string> options,
        out List<PullFilterSpecification> filters,
        out string? problem)
    {
        filters = [];
        problem = null;

        foreach (string option in options)
        {
            if (string.IsNullOrWhiteSpace(option))
            {
                continue;
            }

            List<string>? tokens = Tokenise(option);

            if (tokens is not ["--pull-filter", string action, string text]
                || action is not ("accept" or "ignore" or "reject")
                || text.Length == 0
                || text.Any(char.IsControl))
            {
                problem = $"The option '{option}' cannot be passed to OpenVPN on macOS. Only pull filters can.";
                return false;
            }

            filters.Add(new PullFilterSpecification(action, text));
        }

        return true;
    }

    /// <summary>
    /// Splits an option the way the Windows launcher writes one: words, and double quoted text with
    /// backslash escapes.
    /// </summary>
    /// <returns>The words, or null for text with an unterminated quote.</returns>
    private static List<string>? Tokenise(string option)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        bool quoted = false;
        bool inToken = false;

        for (int index = 0; index < option.Length; index++)
        {
            char character = option[index];

            if (quoted)
            {
                if (character == '\\' && index + 1 < option.Length)
                {
                    current.Append(option[++index]);
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            inToken = true;

            if (character == '"')
            {
                quoted = true;
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted)
        {
            return null;
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
