using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.Core.Tests.Storage;

/// <summary>
/// Which database a process opens, decided from the settings file before anything is composed.
/// </summary>
public sealed class ActiveStorageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ovp-storage-").FullName;

    private string SettingsPath => Path.Combine(root, "settings.json");

    private TemporaryPaths Paths => new(root);

    [Fact]
    public void Read_NoSettingsFile_IsLocal()
    {
        Assert.Equal(StorageSelection.Local, StorageModeReader.Read(SettingsPath));
    }

    [Fact]
    public void Read_NoSettingsFile_CreatesNothing()
    {
        StorageModeReader.Read(SettingsPath);

        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Read_ServerMode_GivesTheModeAndTheAddressAsWritten()
    {
        await File.WriteAllTextAsync(
            SettingsPath,
            """{ "storage": { "mode": "Server", "serverUrl": "https://Pilot.Example.com/" } }""");

        StorageSelection selection = StorageModeReader.Read(SettingsPath);

        Assert.Equal(StorageMode.Server, selection.Mode);
        Assert.Equal("https://Pilot.Example.com/", selection.ServerUrl);
    }

    [Fact]
    public async Task Read_ModeInAnyCase_IsUnderstoodLikeTheSettingsServiceUnderstandsIt()
    {
        await File.WriteAllTextAsync(
            SettingsPath,
            """{ "Storage": { "Mode": "server", "ServerUrl": "https://pilot.example.com" } }""");

        Assert.Equal(StorageMode.Server, StorageModeReader.Read(SettingsPath).Mode);
    }

    [Fact]
    public async Task Read_FileWithoutTheSection_IsLocal()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "general": { "language": "de" } }""");

        Assert.Equal(StorageSelection.Local, StorageModeReader.Read(SettingsPath));
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("""{ "storage": { "mode": "Somewhere" } }""")]
    [InlineData("""{ "storage": { "mode": 7, "serverUrl": "https://pilot.example.com" } }""")]
    [InlineData("""{ "storage": { "mode": "Server", "serverUrl": "https://pilot.example.com" }, "general": 5 }""")]
    [InlineData("null")]
    public async Task Read_AnythingTheSettingsServiceWouldNotRead_IsLocal(string content)
    {
        await File.WriteAllTextAsync(SettingsPath, content);

        Assert.Equal(StorageSelection.Local, StorageModeReader.Read(SettingsPath));
    }

    [Fact]
    public async Task Read_UnreadableFile_IsLeftWhereItIs()
    {
        await File.WriteAllTextAsync(SettingsPath, "{ not json at all");

        StorageModeReader.Read(SettingsPath);

        // Setting the file aside is the settings service's job, and only once it is loaded.
        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".invalid"));
    }

    [Fact]
    public void Resolve_Local_IsTheLibraryItAlwaysWas()
    {
        ActiveStorage storage = ActiveStorage.Resolve(Paths, StorageSelection.Local);

        Assert.Equal(StorageMode.Local, storage.Mode);
        Assert.Equal(Path.Combine(root, "pilot.db"), storage.DatabasePath);
        Assert.Null(storage.ServerAddress);
        Assert.Null(storage.ServerKey);
        Assert.Null(storage.ServerDirectory);
        Assert.Equal(ServerAddressProblem.None, storage.Problem);
        Assert.False(Directory.Exists(Path.Combine(root, "servers")));
    }

    [Fact]
    public void Resolve_LocalWithAServerRemembered_IsStillTheLibrary()
    {
        ActiveStorage storage = ActiveStorage.Resolve(
            Paths,
            new StorageSelection(StorageMode.Local, "https://pilot.example.com"));

        Assert.Equal(Path.Combine(root, "pilot.db"), storage.DatabasePath);
        Assert.Null(storage.ServerKey);
    }

    [Fact]
    public void Resolve_Server_IsTheCopyFiledUnderTheKey()
    {
        ActiveStorage storage = ActiveStorage.Resolve(
            Paths,
            new StorageSelection(StorageMode.Server, "HTTPS://pilot.example.com/"));

        string directory = Path.Combine(root, "servers", "9ba8b7393ae311cc4dc8f89fdb377030");

        Assert.Equal(StorageMode.Server, storage.Mode);
        Assert.Equal("https://pilot.example.com", storage.ServerAddress);
        Assert.Equal("9ba8b7393ae311cc4dc8f89fdb377030", storage.ServerKey);
        Assert.Equal(directory, storage.ServerDirectory);
        Assert.Equal(Path.Combine(directory, "pilot.db"), storage.DatabasePath);

        // SQLite creates the file but not the folder it goes in.
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void Resolve_TwoServers_NeverShareADatabase()
    {
        ActiveStorage first = ActiveStorage.Resolve(
            Paths,
            new StorageSelection(StorageMode.Server, "https://pilot.example.com"));
        ActiveStorage second = ActiveStorage.Resolve(
            Paths,
            new StorageSelection(StorageMode.Server, "https://pilot.example.com:8443"));

        Assert.NotEqual(first.DatabasePath, second.DatabasePath);
        Assert.NotEqual(Paths.LocalDatabasePath, first.DatabasePath);
    }

    [Theory]
    [InlineData(null, ServerAddressProblem.Empty)]
    [InlineData("http://pilot.example.com", ServerAddressProblem.NotHttps)]
    [InlineData("https://pilot.example.com/pilot", ServerAddressProblem.CarriesPath)]
    public void Resolve_ServerWithAnUnusableAddress_WorksLocallyAndSaysWhy(
        string? address,
        ServerAddressProblem expected)
    {
        ActiveStorage storage = ActiveStorage.Resolve(Paths, new StorageSelection(StorageMode.Server, address));

        Assert.Equal(StorageMode.Local, storage.Mode);
        Assert.Equal(Path.Combine(root, "pilot.db"), storage.DatabasePath);
        Assert.Equal(expected, storage.Problem);
        Assert.False(Directory.Exists(Path.Combine(root, "servers")));
    }

    [Fact]
    public async Task ReadAndResolve_ServerSettings_GiveTheServerCopy()
    {
        await File.WriteAllTextAsync(
            SettingsPath,
            """{ "storage": { "mode": "Server", "serverUrl": "https://pilot.example.com" } }""");

        ActiveStorage storage = ActiveStorage.Resolve(Paths, StorageModeReader.Read(SettingsPath));

        Assert.Equal(
            Path.Combine(root, "servers", "9ba8b7393ae311cc4dc8f89fdb377030", "pilot.db"),
            storage.DatabasePath);
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

    private sealed class TemporaryPaths : IApplicationPaths
    {
        public TemporaryPaths(string root) => DataDirectory = root;

        public string DataDirectory { get; }

        public string LocalDatabasePath => Path.Combine(DataDirectory, "pilot.db");

        public string ServersDirectory => Path.Combine(DataDirectory, "servers");

        public string LogDirectory => Path.Combine(DataDirectory, "logs");

        public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

        public string SecretsDirectory => Path.Combine(DataDirectory, "secrets");

        public string InstalledLanguageDirectory => Path.Combine(DataDirectory, "installed");

        public string UserLanguageDirectory => Path.Combine(DataDirectory, "lang");
    }
}
