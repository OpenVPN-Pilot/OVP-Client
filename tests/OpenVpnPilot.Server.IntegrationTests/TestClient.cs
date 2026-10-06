using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.Server.IntegrationTests;

/// <summary>
/// One installation of the client, as the application composes it, against the server under test:
/// an identity, a copy in a SQLite file, a keystore and the synchronisation.
/// </summary>
/// <remarks>
/// The keystore and the settings are kept in memory; everything that talks to the server is the
/// application's own code. Two clients are two machines to the server.
/// </remarks>
internal sealed class TestClient : IAsyncDisposable
{
    private readonly string root;
    private readonly ServiceProvider services;

    private TestClient(string root, ServiceProvider services)
    {
        this.root = root;
        this.services = services;
        Database = services.GetRequiredService<IDbContextFactory<PilotDbContext>>();

        ServerHttpClientFactory clients = new(
            new AssemblyClientVersionProvider(typeof(SyncEngine).Assembly),
            new FixedInstallation(Guid.NewGuid()),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            () => new RecordingHandler(Requests) { InnerHandler = TestServer.CreateNetwork() });

        Uri address = ServerHttpClientFactory.NormaliseBaseAddress(TestServer.Address);
        Connection = new ServerConnectionFactory(clients, new AssemblyClientVersionProvider(typeof(SyncEngine).Assembly), Secrets, new NoEntraRenewal(), TimeProvider.System, NullLoggerFactory.Instance)
            .Create(address, ServerKey.Compute(address.AbsoluteUri));

        Outbox = new Outbox(Database, TimeProvider.System, NullLogger<Outbox>.Instance);
        Maintenance = new ServerProfileMaintenance(Database, Secrets, Held, Outbox, NullLogger<ServerProfileMaintenance>.Instance);
        Recorder = new ChangeRecorder(Outbox, new ServerMode());
        Settings = new RecordingSettingsService(new MemorySettings(), Recorder, new InlineThread());
        Store = new ProfileStore(Database, TimeProvider.System, Recorder, Maintenance);

        Engine = new SyncEngine(
            Connection,
            Database,
            Outbox,
            Maintenance,
            Secrets,
            Held,
            new NoTunnels(),
            new ServerNotices(),
            Settings,
            Settings,
            new LibraryChangeNotifier(),
            new QuietNetwork(),
            TimeProvider.System,
            NullLogger<SyncEngine>.Instance);
    }

    public IDbContextFactory<PilotDbContext> Database { get; }

    public MemorySecrets Secrets { get; } = new();

    public TypedCredentials Held { get; } = new(new ServerMode());

    public IServerConnection Connection { get; }

    public Outbox Outbox { get; }

    public ServerProfileMaintenance Maintenance { get; }

    public ChangeRecorder Recorder { get; }

    public RecordingSettingsService Settings { get; }

    public ProfileStore Store { get; }

    public SyncEngine Engine { get; }

    /// <summary>
    /// Every call this client made, as method, path and query.
    /// </summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public IServerApi Api => Connection.Api;

    public static async Task<TestClient> CreateAsync()
    {
        string root = Directory.CreateTempSubdirectory("ovp-it-").FullName;

        ServiceCollection collection = new();
        collection.AddDbContextFactory<PilotDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "pilot.db")}"));

        TestClient client = new(root, collection.BuildServiceProvider());

        await using PilotDbContext context = await client.Database.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        return client;
    }

    /// <summary>
    /// Signs in and fails the test when the server says no.
    /// </summary>
    public async Task<CurrentUserResponse> SignInAsync(string username, string password)
    {
        ServerResult<CurrentUserResponse> answer = await Connection.SignIn.SignInAsync(username, password);
        Assert.True(answer.IsSuccess, $"Signing in as {username}: {answer.Outcome} {answer.Code} {answer.Problem?.Detail}");
        return answer.Value;
    }

    /// <summary>
    /// Runs one cycle and fails the test when it did not complete.
    /// </summary>
    public async Task<SyncCycleResult> SyncAsync()
    {
        SyncCycleResult result = await Engine.SynchronizeAsync(CancellationToken.None);
        Assert.True(result.Completed, $"The cycle stopped: {result.State}, {Engine.Status.LastErrorCode}, request {Engine.Status.LastRequestId}.");
        return result;
    }

    /// <summary>
    /// A profile created here while the server could not be reached, waiting to be uploaded.
    /// </summary>
    public async Task<Profile> CreateOfflineAsync(string name)
    {
        await using PilotDbContext context = await Database.CreateDbContextAsync();
        string configuration = TestServer.Configuration(name);

        Profile profile = new()
        {
            Name = name,
            Configuration = configuration,
            ContentHash = OpenVpnPilot.Data.Import.ProfileImporter.ComputeHash(configuration),
        };

        context.Profiles.Add(profile);
        await Outbox.StageAsync(context, PendingChangeKind.ProfileCreate, profile.Id);
        await context.SaveChangesAsync();
        return profile;
    }

    public async Task<Profile?> ProfileAsync(Guid id)
    {
        await using PilotDbContext context = await Database.CreateDbContextAsync();
        return await context.Profiles.AsNoTracking().FirstOrDefaultAsync(profile => profile.Id == id);
    }

    public async Task<Profile?> ProfileNamedAsync(string name)
    {
        await using PilotDbContext context = await Database.CreateDbContextAsync();
        return await context.Profiles.AsNoTracking().FirstOrDefaultAsync(profile => profile.Name == name);
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.StopAsync(CancellationToken.None);
        Engine.Dispose();
        Settings.Dispose();
        Connection.Dispose();
        await services.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    private sealed class FixedInstallation(Guid id) : IInstallationIdProvider
    {
        public bool IsAvailable => true;

        public Guid InstallationId { get; } = id;
    }

    private sealed class ServerMode : IStorageModeContext
    {
        public bool IsServerMode => true;
    }

    private sealed class InlineThread : IUserInterfaceThread
    {
        public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default) => work();
    }

    private sealed class NoTunnels : IRemovedProfileTunnels
    {
        public Task<IReadOnlyList<Guid>> DisconnectAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class QuietNetwork : INetworkAvailability
    {
        public event EventHandler<bool>? AvailabilityChanged
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// Notes every call on its way out, so a test can tell which were made.
    /// </summary>
    private sealed class RecordingHandler(ConcurrentQueue<string> requests) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue($"{request.Method} {request.RequestUri?.PathAndQuery}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// A keystore in memory, which is all a test machine needs.
/// </summary>
internal sealed class MemorySecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, StoredSecret> entries = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

    public Task<StoredSecret?> TryReadAsync(string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(entries.GetValueOrDefault(reference));

    public Task<SecretRead> ReadAsync(string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(entries.TryGetValue(reference, out StoredSecret? secret) ? SecretRead.Found(secret) : SecretRead.Absent);

    public Task WriteAsync(string reference, StoredSecret secret, CancellationToken cancellationToken = default)
    {
        entries[reference] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        entries.TryRemove(reference, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. entries.Keys]);

    public Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        int count = entries.Count;
        entries.Clear();
        return Task.FromResult(count);
    }
}

/// <summary>
/// Settings in memory, in the current layout, with an identity of their own.
/// </summary>
internal sealed class MemorySettings : ISettingsService
{
    public PilotSettings Current { get; private set; } = new() { SchemaVersion = PilotSettings.CurrentSchemaVersion };

    public event EventHandler<PilotSettings>? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<PilotSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(Current);
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(PilotSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }
}
