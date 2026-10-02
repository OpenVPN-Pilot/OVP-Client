using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;

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

    [Fact]
    public async Task FileExistedAtLoad_NoFile_IsFalseAlthoughTheDefaultsWereWritten()
    {
        using JsonSettingsService service = new(Path);

        Assert.Null(service.FileExistedAtLoad);

        await service.LoadAsync();

        // The defaults now exist on disk, which is exactly what must not be read as an earlier start.
        Assert.True(File.Exists(Path));
        Assert.False(service.FileExistedAtLoad);
    }

    [Fact]
    public async Task FileExistedAtLoad_ExistingFile_IsTrueAndTheModeStaysLocal()
    {
        await File.WriteAllTextAsync(Path, """{ "general": { "language": "de" } }""");

        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        Assert.True(service.FileExistedAtLoad);
        Assert.Equal(StorageMode.Local, service.Current.Storage.Mode);
    }

    [Fact]
    public async Task FileExistedAtLoad_UnreadableFile_StillCountsAsAnEarlierStart()
    {
        await File.WriteAllTextAsync(Path, "{ not json at all");

        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        Assert.True(service.FileExistedAtLoad);
    }

    [Fact]
    public async Task LoadAsync_NoFile_GivesTheInstallationAnIdentityAndWritesIt()
    {
        using (JsonSettingsService first = new(Path))
        {
            await first.LoadAsync();
            Assert.NotNull(first.Current.Installation.Id);
            Assert.NotEqual(Guid.Empty, first.Current.Installation.Id);
        }

        string content = await File.ReadAllTextAsync(Path);
        Assert.Contains("\"installation\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_InstallationIdentity_IsNeverRegenerated()
    {
        Guid? created;

        using (JsonSettingsService first = new(Path))
        {
            await first.LoadAsync();
            created = first.Current.Installation.Id;
        }

        using JsonSettingsService second = new(Path);
        await second.LoadAsync();

        Assert.Equal(created, second.Current.Installation.Id);
    }

    [Fact]
    public async Task LoadAsync_FileFromAnOlderBuild_IsGivenAnIdentityOnce()
    {
        await File.WriteAllTextAsync(Path, """{ "schemaVersion": 2, "general": { "language": "de" } }""");

        Guid? assigned;

        using (JsonSettingsService first = new(Path))
        {
            await first.LoadAsync();
            assigned = first.Current.Installation.Id;
        }

        using JsonSettingsService second = new(Path);
        await second.LoadAsync();

        Assert.NotNull(assigned);
        Assert.Equal(assigned, second.Current.Installation.Id);
        Assert.Equal("de", second.Current.General.Language);
    }

    [Fact]
    public async Task ReplaceAsync_SettingsWithoutAnIdentity_KeepTheOneThisInstallationHas()
    {
        using JsonSettingsService service = new(Path);
        await service.LoadAsync();
        Guid? id = service.Current.Installation.Id;

        await service.ReplaceAsync(new PilotSettings());

        Assert.Equal(id, service.Current.Installation.Id);
    }

    [Fact]
    public async Task UpdateAsync_ModeAndServer_SurviveAReload()
    {
        using (JsonSettingsService writer = new(Path))
        {
            await writer.LoadAsync();
            await writer.UpdateAsync(settings =>
            {
                settings.Storage.Mode = StorageMode.Server;
                settings.Storage.ServerUrl = "https://pilot.example.com";
            });
        }

        string content = await File.ReadAllTextAsync(Path);

        using JsonSettingsService reader = new(Path);
        await reader.LoadAsync();

        Assert.Contains("\"storage\"", content, StringComparison.Ordinal);
        Assert.Contains("\"Server\"", content, StringComparison.Ordinal);
        Assert.Equal(StorageMode.Server, reader.Current.Storage.Mode);
        Assert.Equal("https://pilot.example.com", reader.Current.Storage.ServerUrl);
    }

    /// <summary>
    /// A file from 1.6.0 may still carry the shared folder section, which has nothing to do with this.
    /// </summary>
    [Fact]
    public async Task LoadAsync_FormerLibrarySection_IsNotReadAsTheStorageMode()
    {
        await File.WriteAllTextAsync(
            Path,
            """{ "library": { "sharedPath": "/Volumes/shared/profiles.ovppkg" } }""");

        using JsonSettingsService service = new(Path);
        await service.LoadAsync();

        Assert.Equal(StorageMode.Local, service.Current.Storage.Mode);
        Assert.Null(service.Current.Storage.ServerUrl);
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
