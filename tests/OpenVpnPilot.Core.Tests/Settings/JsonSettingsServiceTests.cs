using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.Core.Tests.Settings;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ovp-settings-").FullName;

    private string Path => System.IO.Path.Combine(root, "settings.json");

    [Fact]
    public async Task LoadAsync_NoFile_WritesTheDefaultsSoTheFileIsDiscoverable()
    {
        using JsonSettingsService service = new(Path);

        await service.LoadAsync();

        Assert.True(File.Exists(Path));
        Assert.True(service.Current.Connections.ProtectRoutes);
        Assert.Equal(ThemePreference.System, service.Current.Appearance.Theme);
    }

    [Fact]
    public async Task UpdateAsync_ChangeSurvivesAReload()
    {
        using (JsonSettingsService writer = new(Path))
        {
            await writer.LoadAsync();
            await writer.UpdateAsync(settings => settings.General.Language = "de");
        }

        using JsonSettingsService reader = new(Path);
        await reader.LoadAsync();

        Assert.Equal("de", reader.Current.General.Language);
    }

    [Fact]
    public async Task UpdateAsync_RaisesChangedWithTheNewValues()
    {
        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        PilotSettings? observed = null;
        service.Changed += (_, settings) => observed = settings;

        await service.UpdateAsync(settings => settings.Appearance.Theme = ThemePreference.Dark);

        Assert.NotNull(observed);
        Assert.Equal(ThemePreference.Dark, observed.Appearance.Theme);
    }

    [Fact]
    public async Task LoadAsync_PartialFile_FillsTheMissingSectionsWithDefaults()
    {
        await File.WriteAllTextAsync(Path, """{ "general": { "language": "de" } }""");

        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        Assert.Equal("de", service.Current.General.Language);
        Assert.Equal(60, service.Current.Connections.ConnectTimeoutSeconds);
    }

    [Fact]
    public async Task LoadAsync_EnumWrittenByName_IsUnderstood()
    {
        await File.WriteAllTextAsync(Path, """{ "appearance": { "theme": "Dark" } }""");

        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        Assert.Equal(ThemePreference.Dark, service.Current.Appearance.Theme);
    }

    [Fact]
    public async Task LoadAsync_UnreadableFile_IsKeptAsideAndTheDefaultsTakeOver()
    {
        await File.WriteAllTextAsync(Path, "{ not json at all");

        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        Assert.True(File.Exists(Path + ".invalid"));
        Assert.True(service.Current.Connections.ProtectRoutes);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotMutateTheInstanceCallersAlreadyHold()
    {
        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        PilotSettings before = service.Current;
        await service.UpdateAsync(settings => settings.General.StartMinimised = true);

        Assert.False(before.General.StartMinimised);
        Assert.True(service.Current.General.StartMinimised);
    }

    [Fact]
    public async Task SavedFile_IsIndentedSoItCanBeEditedByHand()
    {
        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        string content = await File.ReadAllTextAsync(Path);

        Assert.Contains(Environment.NewLine, content, StringComparison.Ordinal);
        Assert.Contains("\"general\"", content, StringComparison.Ordinal);
    }

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
