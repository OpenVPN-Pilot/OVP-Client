using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Switching between this computer's library and a server: refused while connected, written so the
/// next copy reads it, and never at the cost of the other mode's data.
/// </summary>
public sealed class StorageModeSwitcherTests : IDisposable
{
    private const int ThisProcess = 4242;

    private readonly string root = Directory.CreateTempSubdirectory("ovp-switch-").FullName;

    [Fact]
    public async Task SwitchToServer_WhileATunnelIsUp_IsRefusedAndChangesNothing()
    {
        Harness harness = await Harness.CreateAsync(root);
        harness.Tunnels.Count = 1;

        StorageSwitchResult result = await harness.Switcher.SwitchToServerAsync("https://pilot.example.com");

        Assert.Equal(StorageSwitchOutcome.TunnelsUp, result.Outcome);
        Assert.Equal(StorageMode.Local, harness.Settings.Current.Storage.Mode);
        Assert.Equal(StorageMode.Local, StorageModeReader.Read(harness.Paths.SettingsPath).Mode);
        Assert.Empty(harness.Restart.Started);
        Assert.Equal(0, harness.ShutdownRequests);
    }

    [Fact]
    public async Task SwitchToLocal_WhileATunnelIsComingUp_IsRefused()
    {
        Harness harness = await Harness.CreateAsync(root, StorageMode.Server, "https://pilot.example.com");
        harness.Tunnels.Count = 2;

        StorageSwitchResult result = await harness.Switcher.SwitchToLocalAsync();

        Assert.Equal(StorageSwitchOutcome.TunnelsUp, result.Outcome);
        Assert.Equal(StorageMode.Server, harness.Settings.Current.Storage.Mode);
    }

    [Fact]
    public async Task SwitchToServer_WritesTheNormalFormStartsTheNextCopyAndEndsThisOne()
    {
        Harness harness = await Harness.CreateAsync(root);

        StorageSwitchResult result = await harness.Switcher.SwitchToServerAsync("HTTPS://Pilot.Example.com:443/");

        Assert.Equal(StorageSwitchOutcome.Restarting, result.Outcome);

        // What the next copy will read, read the way it reads it.
        StorageSelection written = StorageModeReader.Read(harness.Paths.SettingsPath);
        Assert.Equal(StorageMode.Server, written.Mode);
        Assert.Equal("https://pilot.example.com", written.ServerUrl);

        string[] arguments = Assert.Single(harness.Restart.Started);
        Assert.Equal(["--after-restart", "4242"], arguments);
        Assert.Equal(1, harness.ShutdownRequests);
    }

    [Fact]
    public async Task SwitchToLocal_KeepsTheServerAddressForTheWayBack()
    {
        Harness harness = await Harness.CreateAsync(root, StorageMode.Server, "https://pilot.example.com");

        StorageSwitchResult result = await harness.Switcher.SwitchToLocalAsync();

        Assert.Equal(StorageSwitchOutcome.Restarting, result.Outcome);

        StorageSelection written = StorageModeReader.Read(harness.Paths.SettingsPath);
        Assert.Equal(StorageMode.Local, written.Mode);
        Assert.Equal("https://pilot.example.com", written.ServerUrl);
    }

    [Fact]
    public async Task Switching_LeavesBothDatabasesWhereTheyAre()
    {
        Harness harness = await Harness.CreateAsync(root, StorageMode.Server, "https://pilot.example.com");

        await File.WriteAllTextAsync(harness.Paths.LocalDatabasePath, "local library");
        await File.WriteAllTextAsync(harness.Storage.DatabasePath, "server copy");

        await harness.Switcher.SwitchToLocalAsync();

        Assert.Equal("local library", await File.ReadAllTextAsync(harness.Paths.LocalDatabasePath));
        Assert.Equal("server copy", await File.ReadAllTextAsync(harness.Storage.DatabasePath));
    }

    [Fact]
    public async Task SwitchToServer_AnotherServer_IsASwitch()
    {
        Harness harness = await Harness.CreateAsync(root, StorageMode.Server, "https://pilot.example.com");

        StorageSwitchResult result = await harness.Switcher.SwitchToServerAsync("https://pilot.example.com:8443");

        Assert.Equal(StorageSwitchOutcome.Restarting, result.Outcome);
        Assert.Equal("https://pilot.example.com:8443", StorageModeReader.Read(harness.Paths.SettingsPath).ServerUrl);
    }

    [Fact]
    public async Task SwitchToServer_TheOneInUse_DoesNothing()
    {
        Harness harness = await Harness.CreateAsync(root, StorageMode.Server, "https://pilot.example.com");

        StorageSwitchResult result = await harness.Switcher.SwitchToServerAsync("https://PILOT.example.com/");

        Assert.Equal(StorageSwitchOutcome.AlreadyActive, result.Outcome);
        Assert.Empty(harness.Restart.Started);
        Assert.Equal(0, harness.ShutdownRequests);
    }

    [Fact]
    public async Task SwitchToLocal_WhenLocalAlready_DoesNothing()
    {
        Harness harness = await Harness.CreateAsync(root);

        StorageSwitchResult result = await harness.Switcher.SwitchToLocalAsync();

        Assert.Equal(StorageSwitchOutcome.AlreadyActive, result.Outcome);
        Assert.Empty(harness.Restart.Started);
    }

    /// <summary>
    /// A hand edited file asked for a server whose address cannot be used, so this run is local.
    /// Switching to Local then still has the file to put right.
    /// </summary>
    [Fact]
    public async Task SwitchToLocal_RunningLocallyBecauseTheServerWasUnusable_PutsTheFileRight()
    {
        Harness harness = await Harness.CreateAsync(root, StorageMode.Server, "http://pilot.example.com");
        Assert.Equal(ServerAddressProblem.NotHttps, harness.Storage.Problem);

        StorageSwitchResult result = await harness.Switcher.SwitchToLocalAsync();

        Assert.Equal(StorageSwitchOutcome.Restarting, result.Outcome);
        Assert.Equal(StorageMode.Local, StorageModeReader.Read(harness.Paths.SettingsPath).Mode);
    }

    [Theory]
    [InlineData("http://pilot.example.com", ServerAddressProblem.NotHttps)]
    [InlineData("https://pilot.example.com/pilot", ServerAddressProblem.CarriesPath)]
    [InlineData("", ServerAddressProblem.Empty)]
    public async Task SwitchToServer_UnusableAddress_SaysWhyAndChangesNothing(
        string address,
        ServerAddressProblem expected)
    {
        Harness harness = await Harness.CreateAsync(root);

        StorageSwitchResult result = await harness.Switcher.SwitchToServerAsync(address);

        Assert.Equal(StorageSwitchOutcome.AddressUnusable, result.Outcome);
        Assert.Equal(expected, result.AddressProblem);
        Assert.Equal(StorageMode.Local, harness.Settings.Current.Storage.Mode);
        Assert.Empty(harness.Restart.Started);
    }

    [Fact]
    public async Task SwitchToServer_RestartImpossible_PutsThePreviousModeBack()
    {
        Harness harness = await Harness.CreateAsync(root);
        harness.Restart.Succeeds = false;

        StorageSwitchResult result = await harness.Switcher.SwitchToServerAsync("https://pilot.example.com");

        Assert.Equal(StorageSwitchOutcome.RestartFailed, result.Outcome);
        Assert.Equal(StorageMode.Local, harness.Settings.Current.Storage.Mode);
        Assert.Equal(StorageMode.Local, StorageModeReader.Read(harness.Paths.SettingsPath).Mode);
        Assert.Equal(0, harness.ShutdownRequests);

        // Nothing is stuck: once the restart works, the same switch goes through.
        harness.Restart.Succeeds = true;
        Assert.Equal(
            StorageSwitchOutcome.Restarting,
            (await harness.Switcher.SwitchToServerAsync("https://pilot.example.com")).Outcome);
    }

    [Fact]
    public async Task SwitchToServer_SettingsFileNotWritten_DoesNotRestartIntoTheOldMode()
    {
        // Holds the change in memory only, which is what a save that failed leaves behind.
        FakeSettingsService unsaved = new();
        Harness harness = await Harness.CreateAsync(root, settings: unsaved);

        StorageSwitchResult result = await harness.Switcher.SwitchToServerAsync("https://pilot.example.com");

        Assert.Equal(StorageSwitchOutcome.SettingsNotSaved, result.Outcome);
        Assert.Equal(StorageMode.Local, unsaved.Current.Storage.Mode);
        Assert.Empty(harness.Restart.Started);
    }

    [Fact]
    public async Task SwitchAgain_AfterTheRestartWasStarted_StartsNoSecondCopy()
    {
        Harness harness = await Harness.CreateAsync(root);

        await harness.Switcher.SwitchToServerAsync("https://pilot.example.com");
        StorageSwitchResult second = await harness.Switcher.SwitchToLocalAsync();

        Assert.Equal(StorageSwitchOutcome.InProgress, second.Outcome);
        Assert.Single(harness.Restart.Started);
        Assert.Equal(1, harness.ShutdownRequests);
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

    private sealed class Harness
    {
        private Harness(
            TemporaryPaths paths,
            ISettingsService settings,
            IActiveStorage storage)
        {
            Paths = paths;
            Settings = settings;
            Storage = storage;

            Switcher = new StorageModeSwitcher(
                settings,
                storage,
                paths,
                Tunnels,
                Restart,
                NullLogger<StorageModeSwitcher>.Instance,
                ThisProcess);

            Switcher.ShutdownRequested += (_, _) => ShutdownRequests++;
        }

        public TemporaryPaths Paths { get; }

        public ISettingsService Settings { get; }

        public IActiveStorage Storage { get; }

        public CountedTunnels Tunnels { get; } = new();

        public RecordingRestart Restart { get; } = new();

        public StorageModeSwitcher Switcher { get; }

        public int ShutdownRequests { get; private set; }

        /// <summary>
        /// A process as it would have started: settings on disk, the store resolved from them.
        /// </summary>
        public static async Task<Harness> CreateAsync(
            string root,
            StorageMode mode = StorageMode.Local,
            string? serverUrl = null,
            FakeSettingsService? settings = null)
        {
            TemporaryPaths paths = new(root);

            JsonSettingsService file = new(paths.SettingsPath);
            await file.LoadAsync();
            await file.UpdateAsync(next =>
            {
                next.Storage.Mode = mode;
                next.Storage.ServerUrl = serverUrl;
            });

            if (settings is not null)
            {
                await settings.ReplaceAsync(file.Current);
            }

            ActiveStorage storage = ActiveStorage.Resolve(paths, StorageModeReader.Read(paths.SettingsPath));
            return new Harness(paths, settings is null ? file : settings, storage);
        }
    }

    private sealed class CountedTunnels : IActiveTunnels
    {
        public int Count { get; set; }
    }

    private sealed class RecordingRestart : IApplicationRestart
    {
        public List<string[]> Started { get; } = [];

        public bool Succeeds { get; set; } = true;

        public bool TryStartSuccessor(IReadOnlyList<string> arguments)
        {
            if (!Succeeds)
            {
                return false;
            }

            Started.Add([.. arguments]);
            return true;
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
