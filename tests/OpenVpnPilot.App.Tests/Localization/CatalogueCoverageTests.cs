using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace OpenVpnPilot.App.Tests.Localization;

/// <summary>
/// Checks that every key the sources ask for is actually translated.
/// </summary>
/// <remarks>
/// A missing key is not an exception. The localizer falls back to the key itself, so the defect ships
/// as a notification reading "notify.connectingTitle" instead of a sentence, which is exactly how the
/// missing connecting notification was found. Comparing the language files against each other cannot
/// catch it, because a key absent from both is consistent.
///
/// The sources are read from the repository rather than from the build output: a key that nothing
/// translates is a defect in the source tree, and nothing in a compiled assembly reveals it.
/// </remarks>
public sealed class CatalogueCoverageTests
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Extensions that turn a matching literal into a file name rather than a key, such as the
    /// settings file, whose name collides with the settings group.
    /// </summary>
    private static readonly string[] FileExtensions =
        [".json", ".csv", ".log", ".db", ".ovpn", ".ovppkg", ".zip", ".txt"];

    [Fact]
    public void EveryKeyTheSourcesUse_IsTranslated()
    {
        HashSet<string> translated = TranslatedKeys(out IReadOnlyList<string> groups);
        List<string> problems = [];

        foreach (string path in SourceFiles())
        {
            string text = File.ReadAllText(path);
            Regex pattern = path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                ? MarkupReference(groups)
                : SourceReference(groups);

            foreach (Match match in pattern.Matches(text))
            {
                string key = match.Groups[1].Value;

                if (IsFileName(key) || translated.Contains(key))
                {
                    continue;
                }

                problems.Add($"{Path.GetFileName(path)} asks for '{key}', which no language file defines.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The repository root, recorded by the project file so the test does not have to guess it.
    /// </summary>
    private static string RepositoryRoot =>
        typeof(CatalogueCoverageTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "RepositoryRoot")
            .Value
        ?? throw new InvalidOperationException("The project file did not record the repository root.");

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// A quoted literal that names a group and a key, which is the only shape a key takes in C#.
    /// </summary>
    private static Regex SourceReference(IReadOnlyList<string> groups) => new(
        $"\"((?:{string.Join('|', groups)})\\.[A-Za-z][A-Za-z0-9]*)\"",
        RegexOptions.None,
        MatchTimeout);

    private static Regex MarkupReference(IReadOnlyList<string> groups) => new(
        $"loc:Translate\\s+((?:{string.Join('|', groups)})\\.[A-Za-z][A-Za-z0-9]*)",
        RegexOptions.None,
        MatchTimeout);

    private static bool IsFileName(string key) =>
        FileExtensions.Any(extension => key.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Flattens the English file into the dotted keys the localizer resolves.
    /// </summary>
    private static HashSet<string> TranslatedKeys(out IReadOnlyList<string> groups)
    {
        string path = Path.Combine(RepositoryRoot, "src", "OpenVpnPilot.App", "lang", "en.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement strings = document.RootElement.GetProperty("strings");

        HashSet<string> keys = new(StringComparer.Ordinal);
        List<string> topLevel = [];

        foreach (JsonProperty group in strings.EnumerateObject())
        {
            topLevel.Add(group.Name);
            Flatten(group.Name, group.Value, keys);
        }

        groups = topLevel;
        return keys;
    }

    private static void Flatten(string prefix, JsonElement node, HashSet<string> keys)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            keys.Add(prefix);
            return;
        }

        foreach (JsonProperty property in node.EnumerateObject())
        {
            Flatten($"{prefix}.{property.Name}", property.Value, keys);
        }
    }
}
