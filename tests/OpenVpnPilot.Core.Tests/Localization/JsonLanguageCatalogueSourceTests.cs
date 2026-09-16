using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.Core.Tests.Localization;

public sealed class JsonLanguageCatalogueSourceTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ovp-lang-").FullName;

    [Fact]
    public void Load_NestedObjects_AreFlattenedIntoDottedKeys()
    {
        Write("en.json", """
            {
              "language": { "code": "en", "name": "English", "englishName": "English" },
              "strings": {
                "profile": { "connect": "Connect", "detail": { "server": "Server" } }
              }
            }
            """);

        LanguageCatalogue catalogue = Assert.Single(Load());

        Assert.Equal("Connect", catalogue.Strings["profile.connect"]);
        Assert.Equal("Server", catalogue.Strings["profile.detail.server"]);
    }

    [Fact]
    public void Load_FileWithoutALanguageBlock_TakesTheCodeFromTheFileName()
    {
        Write("fr.json", """{ "profile": { "connect": "Connecter" } }""");

        LanguageCatalogue catalogue = Assert.Single(Load());

        Assert.Equal("fr", catalogue.Code);
        Assert.Equal("Connecter", catalogue.Strings["profile.connect"]);
    }

    [Fact]
    public void Load_MalformedFile_IsSkippedWithoutTakingTheOthersDown()
    {
        Write("broken.json", "{ this is not json");
        Write("en.json", """{ "language": { "code": "en", "name": "English" }, "strings": { "a": "A" } }""");

        LanguageCatalogue catalogue = Assert.Single(Load());

        Assert.Equal("en", catalogue.Code);
    }

    [Fact]
    public void Load_SameLanguageInTwoDirectories_LayersTheLaterOneOnTop()
    {
        string overrides = Path.Combine(root, "user");
        Directory.CreateDirectory(overrides);

        Write("en.json", """
            {
              "language": { "code": "en", "name": "English" },
              "strings": { "a": "shipped", "b": "kept" }
            }
            """);

        File.WriteAllText(
            Path.Combine(overrides, "en.json"),
            """
            {
              "language": { "code": "en", "name": "English" },
              "strings": { "a": "replaced" }
            }
            """);

        LanguageCatalogue catalogue = Assert.Single(
            new JsonLanguageCatalogueSource([root, overrides]).Load());

        Assert.Equal("replaced", catalogue.Strings["a"]);

        // A partial override must not erase the keys it does not mention.
        Assert.Equal("kept", catalogue.Strings["b"]);
    }

    [Fact]
    public void Load_NonStringValues_AreIgnoredRatherThanCoerced()
    {
        Write("en.json", """
            {
              "language": { "code": "en", "name": "English" },
              "strings": { "text": "yes", "count": 3, "flag": true, "list": ["a"] }
            }
            """);

        LanguageCatalogue catalogue = Assert.Single(Load());

        Assert.True(catalogue.Strings.ContainsKey("text"));
        Assert.False(catalogue.Strings.ContainsKey("count"));
        Assert.False(catalogue.Strings.ContainsKey("flag"));
        Assert.False(catalogue.Strings.ContainsKey("list"));
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsNothingRatherThanThrowing()
    {
        Assert.Empty(new JsonLanguageCatalogueSource([Path.Combine(root, "absent")]).Load());
    }

    [Fact]
    public void Load_PlatformVariant_ReplacesTheNeutralKeyOnThatPlatform()
    {
        WritePlatformCatalogue();

        LanguageCatalogue catalogue = Assert.Single(
            new JsonLanguageCatalogueSource([root], platform: "macos").Load());

        Assert.Equal("Install the helper", catalogue.Strings["environment.hint"]);
        Assert.Equal("Recheck", catalogue.Strings["environment.recheck"]);
    }

    /// <summary>
    /// Only the resolved key may be visible, because the markup asks for the neutral one.
    /// </summary>
    [Fact]
    public void Load_PlatformVariant_IsNotAKeyOfItsOwn()
    {
        WritePlatformCatalogue();

        LanguageCatalogue catalogue = Assert.Single(
            new JsonLanguageCatalogueSource([root], platform: "macos").Load());

        Assert.DoesNotContain(catalogue.Strings.Keys, key => key.Contains('@', StringComparison.Ordinal));
    }

    [Fact]
    public void Load_WithoutAPlatform_KeepsTheNeutralWordingAndDropsEveryVariant()
    {
        WritePlatformCatalogue();

        LanguageCatalogue catalogue = Assert.Single(Load());

        Assert.Equal("Install the service", catalogue.Strings["environment.hint"]);
        Assert.Equal(2, catalogue.Strings.Count);
    }

    [Fact]
    public void Load_VariantForAnotherPlatform_IsIgnored()
    {
        WritePlatformCatalogue();

        LanguageCatalogue catalogue = Assert.Single(
            new JsonLanguageCatalogueSource([root], platform: "linux").Load());

        Assert.Equal("Install the service", catalogue.Strings["environment.hint"]);
    }

    [Theory]
    [InlineData("environment.hint@macos", "environment.hint", "macos")]
    [InlineData("a@b", "a", "b")]
    public void TrySplitPlatformKey_ReadsTheSuffix(string key, string neutral, string platform)
    {
        Assert.True(JsonLanguageCatalogueSource.TrySplitPlatformKey(key, out string readNeutral, out string readPlatform));
        Assert.Equal(neutral, readNeutral);
        Assert.Equal(platform, readPlatform);
    }

    [Theory]
    [InlineData("environment.hint")]
    [InlineData("group@name.key")]
    [InlineData("@macos")]
    [InlineData("key@")]
    public void TrySplitPlatformKey_LeavesAnOrdinaryKeyAlone(string key)
    {
        Assert.False(JsonLanguageCatalogueSource.TrySplitPlatformKey(key, out string neutral, out _));
        Assert.Equal(key, neutral);
    }

    private void WritePlatformCatalogue() =>
        Write("en.json", """
            {
              "language": { "code": "en", "name": "English" },
              "strings": {
                "environment": {
                  "hint": "Install the service",
                  "hint@macos": "Install the helper",
                  "recheck": "Recheck"
                }
              }
            }
            """);

    private IReadOnlyList<LanguageCatalogue> Load() => new JsonLanguageCatalogueSource([root]).Load();

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(root, name), content);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }
}
