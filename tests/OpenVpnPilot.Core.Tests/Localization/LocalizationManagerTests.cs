using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.Core.Tests.Localization;

public sealed class LocalizationManagerTests
{
    [Fact]
    public void Indexer_KnownKey_ReturnsTextOfTheActiveLanguage()
    {
        LocalizationManager manager = Build();
        Assert.True(manager.TrySetLanguage("de"));

        Assert.Equal("Verbinden", manager["profile.connect"]);
    }

    [Fact]
    public void Indexer_KeyMissingFromActiveLanguage_FallsBackToEnglish()
    {
        LocalizationManager manager = Build();
        manager.TrySetLanguage("de");

        // Present in English only, which is what a half finished translation looks like.
        Assert.Equal("Refresh", manager["header.refresh"]);
    }

    [Fact]
    public void Indexer_KeyMissingEverywhere_ReturnsTheKeyItself()
    {
        LocalizationManager manager = Build();

        Assert.Equal("nothing.here", manager["nothing.here"]);
    }

    [Fact]
    public void Translate_WithArguments_FillsPlaceholders()
    {
        LocalizationManager manager = Build();

        Assert.Equal("3 profile(s)", manager.Translate("status.profileCount", 3));
    }

    [Fact]
    public void Translate_TemplateWithBrokenPlaceholder_ReturnsTheTemplateUnchanged()
    {
        LocalizationManager manager = Build();

        Assert.Equal("broken {oops}", manager.Translate("status.broken", 1));
    }

    [Fact]
    public void TrySetLanguage_UnknownCode_LeavesTheActiveLanguageAlone()
    {
        LocalizationManager manager = Build();

        Assert.False(manager.TrySetLanguage("fr"));
        Assert.Equal("en", manager.CurrentLanguage);
    }

    [Fact]
    public void TrySetLanguage_KnownCode_RaisesLanguageChangedOnce()
    {
        LocalizationManager manager = Build();
        int raised = 0;
        manager.LanguageChanged += (_, _) => raised++;

        manager.TrySetLanguage("de");
        manager.TrySetLanguage("de");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Keys_CoversTheActiveLanguageAndTheFallback()
    {
        LocalizationManager manager = Build();
        manager.TrySetLanguage("de");

        Assert.Contains("profile.connect", manager.Keys);
        Assert.Contains("header.refresh", manager.Keys);
    }

    [Fact]
    public void AvailableLanguages_ListsEveryCatalogue()
    {
        LocalizationManager manager = Build();

        Assert.Equal(["Deutsch", "English"], manager.AvailableLanguages.Select(language => language.NativeName));
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("de", "de")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "en")]
    public void ResolveBestMatch_ReducesARegionalCodeToAnAvailableLanguage(string requested, string expected)
    {
        Assert.Equal(expected, Build().ResolveBestMatch(requested));
    }

    [Fact]
    public void Reload_PicksUpACatalogueAddedAfterConstruction()
    {
        StubSource source = new();
        source.Add(new LanguageCatalogue(
            new LanguageDescriptor("en", "English", "English"),
            new Dictionary<string, string> { ["profile.connect"] = "Connect" }));

        LocalizationManager manager = new(source);

        Assert.Single(manager.AvailableLanguages);

        source.Add(new LanguageCatalogue(
            new LanguageDescriptor("de", "Deutsch", "German"),
            new Dictionary<string, string> { ["profile.connect"] = "Verbinden" }));

        manager.Reload();

        Assert.Equal(2, manager.AvailableLanguages.Count);
    }

    private static LocalizationManager Build()
    {
        StubSource source = new();

        source.Add(new LanguageCatalogue(
            new LanguageDescriptor("en", "English", "English"),
            new Dictionary<string, string>
            {
                ["profile.connect"] = "Connect",
                ["header.refresh"] = "Refresh",
                ["status.profileCount"] = "{0} profile(s)",
                ["status.broken"] = "broken {oops}",
            }));

        source.Add(new LanguageCatalogue(
            new LanguageDescriptor("de", "Deutsch", "German"),
            new Dictionary<string, string>
            {
                ["profile.connect"] = "Verbinden",
            }));

        return new LocalizationManager(source);
    }

    private sealed class StubSource : ILanguageCatalogueSource
    {
        private readonly List<LanguageCatalogue> catalogues = [];

        public void Add(LanguageCatalogue catalogue) => catalogues.Add(catalogue);

        public IReadOnlyList<LanguageCatalogue> Load() => catalogues.ToList();
    }
}
