using System.Globalization;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// What "follow the system" resolves to.
/// </summary>
/// <remarks>
/// This is the test that would have caught the language setting doing nothing. The repository built
/// with InvariantGlobalization, which leaves CultureInfo.CurrentUICulture as the invariant culture
/// whose name is the empty string: the system language was therefore never anything but English, on
/// Windows as well as macOS, however the machine was set up. Under that setting these tests cannot
/// even be written, because constructing a named culture throws.
/// </remarks>
public sealed class LanguageCoordinatorTests
{
    private static readonly string LanguageDirectory =
        Path.Combine(AppContext.BaseDirectory, "lang");

    private static LanguageCoordinator Coordinator() =>
        new(new LocalizationManager(new JsonLanguageCatalogueSource([LanguageDirectory])),
            new FakeSettingsService());

    /// <summary>
    /// Runs the body with the process reporting the given user interface culture, and puts the old
    /// one back whatever happens.
    /// </summary>
    private static void WithUiCulture(string name, Action body)
    {
        CultureInfo before = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(name);
            body();
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("de-AT")]
    [InlineData("de")]
    public void ReducesARegionalGermanToTheGermanCatalogue(string culture)
    {
        WithUiCulture(culture, () => Assert.Equal("de", Coordinator().SystemLanguage));
    }

    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    public void ReducesARegionalEnglishToTheEnglishCatalogue(string culture)
    {
        WithUiCulture(culture, () => Assert.Equal("en", Coordinator().SystemLanguage));
    }

    /// <summary>
    /// A language nothing has been translated into falls back rather than leaving the interface with
    /// keys in it.
    /// </summary>
    [Fact]
    public void FallsBackForALanguageNobodyHasTranslated()
    {
        WithUiCulture("ja-JP", () => Assert.Equal("en", Coordinator().SystemLanguage));
    }

    /// <summary>
    /// The invariant culture is what a process reports when globalization is switched off, and it
    /// names no language at all.
    /// </summary>
    [Fact]
    public void FallsBackWhenTheSystemNamesNoLanguage()
    {
        WithUiCulture(string.Empty, () => Assert.Equal("en", Coordinator().SystemLanguage));
    }
}
