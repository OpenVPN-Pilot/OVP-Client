using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// The wipe directive carried out: everything that came from the server goes, nothing else does,
/// and the application restarts on this computer's own library without asking the server again.
/// </summary>
public sealed class ServerWipeTests : IAsyncDisposable
{
    private const string Address = "https://pilot.example.com";

    private static readonly ServerWipeDirective Directive = new(
        new Uri(Address + "/"),
        "GET",
        "/api/v1/sync/changes",
        401,
        "req-wipe",
        DateTimeOffset.UtcNow);

    private const string SharedPassword = "shared-password-value";
    private const string KeyPassphrase = "key-passphrase-value";
    private const string LocalPassword = "local-password-value";
    private const string RefreshToken = "refresh-token-value";

    private readonly string root = Directory.CreateTempSubdirectory("ovp-wipe-").FullName;
    private readonly Guid localProfile = Guid.NewGuid();
    private DatabaseAt? copy;
    private TestConnection? server;

    [Fact]
    public async Task Wipe_RemovesEverythingFromTheServerAndNothingElse()
    {
        Harness harness = await ArrangeAsync();

        ServerWipeReport? report = await harness.Wipe.WipeAsync(Directive);

        Assert.NotNull(report);

        // Tunnels and synchronisation are down.
        Assert.Equal(1, harness.Tunnels.Disconnects);
        Assert.Equal(0, harness.Tunnels.Count);
        Assert.Contains("stop", harness.Engine.Calls);

        // The copy and the server's keystore entries are gone.
        Assert.False(Directory.Exists(harness.Storage.ServerDirectory));
        IReadOnlyList<string> left = await harness.Secrets.ListAsync();
        Assert.DoesNotContain(left, reference => reference.Contains(harness.ServerProfile.ToString("N"), StringComparison.Ordinal));
        Assert.DoesNotContain(SecretReference.ForServerRefreshToken(TestConnection.ServerKey), left);
        Assert.False(server!.Connection.Session.IsSignedIn);

        // What is this computer's own stays exactly as it was.
        Assert.Equal("local library", await File.ReadAllTextAsync(harness.Paths.LocalDatabasePath));
        Assert.Contains(SecretReference.ForProfile(localProfile, "Auth"), left);

        // Back to this computer, the server forgotten, the person told, then the restart.
        StorageSelection written = StorageModeReader.Read(harness.Paths.SettingsPath);
        Assert.Equal(StorageMode.Local, written.Mode);
        Assert.Null(written.ServerUrl);
        Assert.Equal(1, harness.Notice.Shown);
        Assert.Equal(["--after-restart", "4242"], Assert.Single(harness.Restart.Started));
        Assert.Equal(1, harness.ShutdownRequests);
        Assert.Equal(StorageSwitchOutcome.Restarting, report.Switch);
        Assert.Equal(2, report.Secrets);
        Assert.True(report.RefreshTokenRemoved);
        Assert.True(report.FolderRemoved);

        // Never asked again.
        Assert.Empty(server.Network.Requests);
    }

    [Fact]
    public async Task Wipe_SecondDirective_DoesNothingMore()
    {
        Harness harness = await ArrangeAsync();

        await harness.Wipe.WipeAsync(Directive);
        ServerWipeReport? second = await harness.Wipe.WipeAsync(Directive);

        Assert.Null(second);
        Assert.Equal(1, harness.Tunnels.Disconnects);
        Assert.Equal(1, harness.Notice.Shown);
        Assert.Single(harness.Restart.Started);
    }

    [Fact]
    public async Task Wipe_TellsThePersonOnlyOnceTheModeIsWrittenLocal()
    {
        Harness harness = await ArrangeAsync();
        harness.Notice.OnShow = () => Assert.Equal(StorageMode.Local, StorageModeReader.Read(harness.Paths.SettingsPath).Mode);

        await harness.Wipe.WipeAsync(Directive);

        Assert.Equal(1, harness.Notice.Shown);
    }

    [Fact]
    public async Task Wipe_NeverWritesASecret()
    {
        RecordingLoggerFactory logs = new();
        Harness harness = await ArrangeAsync(logs);

        await harness.Wipe.WipeAsync(Directive);

        Assert.NotEmpty(logs.Lines);

        foreach (string secret in new[] { SharedPassword, KeyPassphrase, LocalPassword, RefreshToken })
        {
            Assert.DoesNotContain(logs.Lines, line => line.Contains(secret, StringComparison.Ordinal));
        }
    }

    public async ValueTask DisposeAsync()
    {
        server?.Dispose();

        if (copy is not null)
        {
            await copy.DisposeAsync();
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    private async Task<Harness> ArrangeAsync(ILoggerFactory? logs = null)
    {
        TemporaryPaths paths = new(root);

        JsonSettingsService settings = new(paths.SettingsPath);
        await settings.LoadAsync();
        await settings.UpdateAsync(next =>
        {
            next.Storage.Mode = StorageMode.Server;
            next.Storage.ServerUrl = Address;
        });

        ActiveStorage storage = ActiveStorage.Resolve(paths, StorageModeReader.Read(paths.SettingsPath));
        await File.WriteAllTextAsync(paths.LocalDatabasePath, "local library");

        copy = await DatabaseAt.CreateAsync(storage.DatabasePath);
        Guid serverProfile = Guid.NewGuid();

        await using (PilotDbContext context = await copy.Factory.CreateDbContextAsync())
        {
            context.Profiles.Add(new Profile
            {
                Id = serverProfile,
                Name = "example-site",
                Configuration = "client\nremote vpn.example.com 1194\n",
                ContentHash = new string('a', 64),
                Source = ProfileSource.Server,
            });

            await context.SaveChangesAsync();
        }

        FakeSecrets secrets = new();
        await secrets.WriteAsync(SecretReference.ForProfile(serverProfile, "Auth"), new StoredSecret("vpnuser", SharedPassword));
        await secrets.WriteAsync(SecretReference.ForProfile(serverProfile, "Private Key"), new StoredSecret(null, KeyPassphrase));
        await secrets.WriteAsync(SecretReference.ForProfile(localProfile, "Auth"), new StoredSecret("me", LocalPassword));
        await secrets.WriteAsync(SecretReference.ForServerRefreshToken(TestConnection.ServerKey), new StoredSecret(null, RefreshToken));

        server = new TestConnection(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError), secrets);
        await server.Connection.Session.RestoreAsync(ServerAnswers.Alice);

        return new Harness(paths, storage, settings, secrets, copy, server.Connection.Session, serverProfile, logs ?? NullLoggerFactory.Instance);
    }

    private sealed class Harness
    {
        public Harness(
            TemporaryPaths paths,
            ActiveStorage storage,
            ISettingsService settings,
            FakeSecrets secrets,
            DatabaseAt copy,
            IServerSession session,
            Guid serverProfile,
            ILoggerFactory logs)
        {
            Paths = paths;
            Storage = storage;
            Secrets = secrets;
            ServerProfile = serverProfile;

            StorageModeSwitcher switcher = new(
                settings,
                storage,
                paths,
                Tunnels,
                Restart,
                new Logger<StorageModeSwitcher>(logs),
                4242);

            switcher.ShutdownRequested += (_, _) => ShutdownRequests++;

            ServerProfileMaintenance maintenance = new(
                copy.Factory,
                secrets,
                new TypedCredentials(new FixedStorageMode(true)),
                new Outbox(copy.Factory, TimeProvider.System, new Logger<Outbox>(logs)),
                new Logger<ServerProfileMaintenance>(logs));

            Wipe = new ServerWipe(
                storage,
                Tunnels,
                Engine,
                session,
                copy.Factory,
                maintenance,
                secrets,
                new NoMaterializer(),
                settings,
                switcher,
                Notice,
                new Logger<ServerWipe>(logs));
        }

        public TemporaryPaths Paths { get; }

        public ActiveStorage Storage { get; }

        public FakeSecrets Secrets { get; }

        public Guid ServerProfile { get; }

        public CountedTunnels Tunnels { get; } = new() { Count = 2 };

        public FakeSyncEngine Engine { get; } = new();

        public RecordingRestart Restart { get; } = new();

        public CountingNotice Notice { get; } = new();

        public ServerWipe Wipe { get; }

        public int ShutdownRequests { get; private set; }
    }

    private sealed class CountingNotice : IAccountRevokedNotice
    {
        public int Shown { get; private set; }

        public Action? OnShow { get; set; }

        public Task ShowAsync(ServerWipeDirective directive, CancellationToken cancellationToken = default)
        {
            Shown++;
            OnShow?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class NoMaterializer : IProfileMaterializer
    {
        public Task<MaterialisedProfile> MaterialiseAsync(Guid profileId, string configuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nothing is connected in this test.");

        public int RemoveStaleFiles() => 0;
    }
}
