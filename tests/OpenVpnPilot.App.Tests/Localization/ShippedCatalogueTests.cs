using System.Text.Json;
using System.Text.RegularExpressions;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.Tests.Localization;

/// <summary>
/// Guards the language files that ship with the application.
/// </summary>
/// <remarks>
/// A key present in one language and missing from another falls back to English at runtime, which is
/// usable but invisible: nobody notices a screen that quietly reverts. Comparing the files here turns
/// that into a failing build instead.
///
/// The files are copied next to the test assembly by the application project, so this reads exactly
/// what would be shipped.
/// </remarks>
public sealed class ShippedCatalogueTests
{
    private static readonly string LanguageDirectory =
        Path.Combine(AppContext.BaseDirectory, "lang");

    /// <summary>
    /// Every platform whose wording the files carry, with null standing for the neutral wording.
    /// </summary>
    public static TheoryData<string?> Platforms { get; } = new() { null, "macos" };

    private static IReadOnlyList<LanguageCatalogue> Load(string? platform = null) =>
        new JsonLanguageCatalogueSource([LanguageDirectory], platform: platform).Load();

    /// <summary>
    /// The editor names a finding by its code, which a search of the sources cannot follow.
    /// </summary>
    [Fact]
    public void EveryConfigurationFinding_HasASentence()
    {
        LanguageCatalogue english = Load().Single(catalogue => catalogue.Code == "en");

        foreach (OpenVpnPilot.OpenVpn.Configuration.OvpnConfigIssueCode code in
            Enum.GetValues<OpenVpnPilot.OpenVpn.Configuration.OvpnConfigIssueCode>())
        {
            Assert.True(
                english.Strings.ContainsKey("editor.issue." + code),
                $"No sentence describes the configuration finding {code}.");
        }
    }

    /// <summary>
    /// The shared library words its condition by the name of the value, which a search of the
    /// sources cannot follow either.
    /// </summary>
    [Fact]
    public void EverySharedLibraryCondition_HasASentence()
    {
        LanguageCatalogue english = Load().Single(catalogue => catalogue.Code == "en");

        foreach (OpenVpnPilot.App.Services.Library.SharedLibraryCondition condition in
            Enum.GetValues<OpenVpnPilot.App.Services.Library.SharedLibraryCondition>())
        {
            Assert.True(
                english.Strings.ContainsKey("library.condition." + condition),
                $"No sentence describes the shared library condition {condition}.");
        }
    }

    [Fact]
    public void EverySharedLibraryState_AndStep_HasWords()
    {
        LanguageCatalogue english = Load().Single(catalogue => catalogue.Code == "en");

        foreach (OpenVpnPilot.App.Services.Library.SharedLibraryCondition condition in
            Enum.GetValues<OpenVpnPilot.App.Services.Library.SharedLibraryCondition>())
        {
            Assert.True(
                english.Strings.ContainsKey("library.state." + condition),
                $"No words for the shared library state {condition} in the status bar.");
        }

        foreach (OpenVpnPilot.App.Services.Library.SharedLibraryActivityKind kind in
            Enum.GetValues<OpenVpnPilot.App.Services.Library.SharedLibraryActivityKind>())
        {
            Assert.True(
                english.Strings.ContainsKey("library.activity." + kind),
                $"No line for the shared library step {kind}.");
        }
    }

    /// <summary>
    /// The nested keys the shared library uses, which the search of the sources does not reach.
    /// </summary>
    [Theory]
    [InlineData("library.conflict.bothKeptHere")]
    [InlineData("library.conflict.bothKeptThere")]
    [InlineData("library.conflict.changedHereDeletedThere")]
    [InlineData("library.conflict.deletedHereChangedThere")]
    [InlineData("library.conflict.more")]
    [InlineData("library.state.retry")]
    [InlineData("library.state.waiting")]
    public void EverySharedLibraryConflict_HasASentence(string key)
    {
        LanguageCatalogue english = Load().Single(catalogue => catalogue.Code == "en");

        Assert.True(english.Strings.ContainsKey(key), $"No sentence for {key}.");
    }

    [Fact]
    public void Catalogues_AreDiscoveredBesideTheApplication()
    {
        IReadOnlyList<LanguageCatalogue> catalogues = Load();

        Assert.Contains(catalogues, catalogue => catalogue.Code == "en");
        Assert.Contains(catalogues, catalogue => catalogue.Code == "de");
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void EveryLanguage_CoversTheSameKeysAsEnglish(string? platform)
    {
        IReadOnlyList<LanguageCatalogue> catalogues = Load(platform);
        LanguageCatalogue english = catalogues.Single(catalogue => catalogue.Code == "en");

        foreach (LanguageCatalogue catalogue in catalogues.Where(item => item.Code != "en"))
        {
            IEnumerable<string> missing = english.Strings.Keys.Except(catalogue.Strings.Keys);
            IEnumerable<string> extra = catalogue.Strings.Keys.Except(english.Strings.Keys);

            Assert.True(
                !missing.Any(),
                $"{catalogue.Code} is missing: {string.Join(", ", missing)}");

            Assert.True(
                !extra.Any(),
                $"{catalogue.Code} has keys English does not: {string.Join(", ", extra)}");
        }
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void NoTranslation_IsEmpty(string? platform)
    {
        foreach (LanguageCatalogue catalogue in Load(platform))
        {
            foreach ((string key, string value) in catalogue.Strings)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(value),
                    $"{catalogue.Code} has no text for {key}.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void EveryLanguage_UsesTheSamePlaceholdersAsEnglish(string? platform)
    {
        IReadOnlyList<LanguageCatalogue> catalogues = Load(platform);
        LanguageCatalogue english = catalogues.Single(catalogue => catalogue.Code == "en");

        foreach (LanguageCatalogue catalogue in catalogues.Where(item => item.Code != "en"))
        {
            foreach ((string key, string translated) in catalogue.Strings)
            {
                if (!english.Strings.TryGetValue(key, out string? original))
                {
                    continue;
                }

                // A translation that drops a placeholder loses information, and one that invents an
                // extra placeholder throws at format time. Both are caught by comparing the sets.
                Assert.Equal(Placeholders(original), Placeholders(translated));
            }
        }
    }

    /// <summary>
    /// A platform's wording replaces a key the other platforms also show, with the same values.
    /// </summary>
    /// <remarks>
    /// The code asks for the neutral key and passes the same arguments everywhere. A variant with no
    /// neutral key would leave every other platform showing the key itself, and a variant with other
    /// placeholders would lose a value or throw when it is formatted.
    /// </remarks>
    [Fact]
    public void EveryPlatformVariant_ReplacesAKeyWithTheSamePlaceholders()
    {
        foreach (string path in Directory.EnumerateFiles(LanguageDirectory, "*.json"))
        {
            Dictionary<string, string> strings = new(StringComparer.Ordinal);

            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Flatten(string.Empty, document.RootElement.GetProperty("strings"), strings);
            }

            foreach ((string key, string text) in strings)
            {
                if (!JsonLanguageCatalogueSource.TrySplitPlatformKey(key, out string neutral, out _))
                {
                    continue;
                }

                Assert.True(
                    strings.TryGetValue(neutral, out string? original),
                    $"{Path.GetFileName(path)} words {key} for one platform, but {neutral} does not exist.");

                Assert.Equal(Placeholders(original!), Placeholders(text));
            }
        }
    }

    private static void Flatten(string prefix, JsonElement node, Dictionary<string, string> target)
    {
        foreach (JsonProperty property in node.EnumerateObject())
        {
            string key = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Flatten(key, property.Value, target);
            }
            else if (property.Value.ValueKind == JsonValueKind.String)
            {
                target[key] = property.Value.GetString() ?? string.Empty;
            }
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
}
