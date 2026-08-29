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

    private static IReadOnlyList<LanguageCatalogue> Load() =>
        new JsonLanguageCatalogueSource([LanguageDirectory]).Load();

    [Fact]
    public void Catalogues_AreDiscoveredBesideTheApplication()
    {
        IReadOnlyList<LanguageCatalogue> catalogues = Load();

        Assert.Contains(catalogues, catalogue => catalogue.Code == "en");
        Assert.Contains(catalogues, catalogue => catalogue.Code == "de");
    }

    [Fact]
    public void EveryLanguage_CoversTheSameKeysAsEnglish()
    {
        IReadOnlyList<LanguageCatalogue> catalogues = Load();
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

    [Fact]
    public void NoTranslation_IsEmpty()
    {
        foreach (LanguageCatalogue catalogue in Load())
        {
            foreach ((string key, string value) in catalogue.Strings)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(value),
                    $"{catalogue.Code} has no text for {key}.");
            }
        }
    }

    [Fact]
    public void EveryLanguage_UsesTheSamePlaceholdersAsEnglish()
    {
        IReadOnlyList<LanguageCatalogue> catalogues = Load();
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

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
}
