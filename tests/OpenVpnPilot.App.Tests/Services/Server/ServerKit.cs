using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// A network that answers what the test scripts and remembers what was asked.
/// </summary>
internal sealed class ScriptedNetwork(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> requests = new();

    public IReadOnlyList<string> Requests => [.. requests];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        requests.Enqueue($"{request.Method} {request.RequestUri!.AbsolutePath}");
        HttpResponseMessage response = answer(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>
/// Answers in the server's shape.
/// </summary>
internal static class ServerAnswers
{
    public static readonly CurrentUserResponse Alice =
        new(Guid.Parse("01a0b3d3-6064-75e9-a940-c28df3732a05"), "alice", "Alice Example", ServerRoles.Admin, ServerAuthModes.File);

    public static readonly CurrentUserResponse Bob =
        new(Guid.Parse("7c1e0d55-2a7b-4f7e-9a51-0e3c2b1d4f60"), "bob", "Bob Example", ServerRoles.User, ServerAuthModes.File);

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK, bool wipe = false)
    {
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, ServerJson.Options), Encoding.UTF8, "application/json"),
        };

        response.Headers.TryAddWithoutValidation(PilotHeaders.RequestId, "req-ok");

        if (wipe)
        {
            response.Headers.TryAddWithoutValidation(PilotHeaders.Directive, PilotHeaders.WipeDirective);
        }

        return response;
    }

    public static HttpResponseMessage Tokens(CurrentUserResponse user) =>
        Json(new TokenResponse("access", DateTimeOffset.UtcNow.AddMinutes(15), "refresh-" + user.Username, DateTimeOffset.UtcNow.AddDays(30), user));

    public static HttpResponseMessage Problem(HttpStatusCode status, string code, bool wipe = false)
    {
        string body = JsonSerializer.Serialize(new { status = (int)status, code, detail = "Detail of " + code, requestId = "req-1" });
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, ServerProblem.MediaType),
        };

        response.Headers.TryAddWithoutValidation(PilotHeaders.RequestId, "req-1");

        if (wipe)
        {
            response.Headers.TryAddWithoutValidation(PilotHeaders.Directive, PilotHeaders.WipeDirective);
        }

        return response;
    }

    public static ServerInfoResponse Info(string authMode = ServerAuthModes.File, string minimum = "1.0.0") =>
        new(ServerInfoResponse.ExpectedName, "1.0.0", "1", minimum, authMode, authMode is ServerAuthModes.File or ServerAuthModes.Ldap, null);
}

/// <summary>
/// The real connection to a server, over a scripted network and a keystore in memory.
/// </summary>
internal sealed class TestConnection : IDisposable
{
    public const string Address = "https://pilot.example.com";

    public TestConnection(Func<HttpRequestMessage, HttpResponseMessage> answer, FakeSecrets? secrets = null)
    {
        Network = new ScriptedNetwork(answer);
        Secrets = secrets ?? new FakeSecrets();
        Factory = CreateFactory(Network, Secrets);
        Connection = Factory.Create(new Uri(Address), ServerKey);
    }

    public static string ServerKey => OpenVpnPilot.Core.Server.ServerKey.Compute(Address);

    public ScriptedNetwork Network { get; }

    public FakeSecrets Secrets { get; }

    public IServerConnectionFactory Factory { get; }

    public IServerConnection Connection { get; }

    public static IServerConnectionFactory CreateFactory(HttpMessageHandler network, ISecretStore secrets)
    {
        FixedVersion version = new(new Version(1, 9, 0));

        ServerHttpClientFactory clients = new(
            version,
            new FixedInstallation(Guid.Parse("6f1c1a52-1d2e-4a3b-9c4d-5e6f7a8b9c0d")),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            () => network,
            ClientPlatform.Windows);

        return new ServerConnectionFactory(clients, version, secrets, TimeProvider.System, NullLoggerFactory.Instance);
    }

    public void Dispose() => Connection.Dispose();

    private sealed class FixedVersion(Version version) : IClientVersionProvider
    {
        public Version Version { get; } = version;
    }

    private sealed class FixedInstallation(Guid id) : IInstallationIdProvider
    {
        public Guid InstallationId { get; } = id;
    }
}

/// <summary>
/// A synchronisation that records what it was asked to do.
/// </summary>
internal sealed class FakeSyncEngine : ISyncEngine
{
    public List<string> Calls { get; } = [];

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Runs when the engine is started, so a test can see what the database held at that moment.
    /// </summary>
    public Func<Task>? OnStart { get; set; }

    public SyncCycleResult NextCycle { get; set; } = new(true, SyncState.Synchronised, 0, 0, true);

    public SyncStatus Status { get; set; } = SyncStatus.Initial;

    public event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Calls.Add("start");
        IsRunning = true;

        if (OnStart is not null)
        {
            await OnStart();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Calls.Add("stop");
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Task<SyncCycleResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        Calls.Add("sync");
        return Task.FromResult(NextCycle);
    }

    public void RequestSync() => Calls.Add("request");
}

/// <summary>
/// Tunnels counted rather than brought up.
/// </summary>
internal sealed class CountedTunnels : IActiveTunnels
{
    public int Count { get; set; }

    public int Disconnects { get; private set; }

    public Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        Disconnects++;
        Count = 0;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRestart : IApplicationRestart
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

internal sealed class TemporaryPaths : IApplicationPaths
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

/// <summary>
/// A migrated database at a path the test chooses, such as inside a server's folder.
/// </summary>
internal sealed class DatabaseAt : IAsyncDisposable
{
    private readonly ServiceProvider services;

    private DatabaseAt(ServiceProvider services)
    {
        this.services = services;
        Factory = services.GetRequiredService<IDbContextFactory<PilotDbContext>>();
    }

    public IDbContextFactory<PilotDbContext> Factory { get; }

    public static async Task<DatabaseAt> CreateAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        ServiceCollection collection = new();
        collection.AddDbContextFactory<PilotDbContext>(options => options.UseSqlite($"Data Source={path}"));

        DatabaseAt database = new(collection.BuildServiceProvider());

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        return database;
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>
/// A wipe that only records that it was asked for.
/// </summary>
internal sealed class RecordingWipe : IServerWipe
{
    private readonly TaskCompletionSource<ServerWipeDirective> asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ServerWipeDirective> Asked => asked.Task;

    public Task<ServerWipeReport?> WipeAsync(ServerWipeDirective directive, CancellationToken cancellationToken = default)
    {
        asked.TrySetResult(directive);
        return Task.FromResult<ServerWipeReport?>(null);
    }
}

/// <summary>
/// A switcher that records what it was asked for and answers as told.
/// </summary>
internal sealed class RecordingSwitcher : IStorageModeSwitcher
{
    public List<string> Calls { get; } = [];

    public StorageSwitchOutcome Answer { get; set; } = StorageSwitchOutcome.Restarting;

    public event EventHandler? ShutdownRequested
    {
        add { }
        remove { }
    }

    public Task<StorageSwitchResult> SwitchToLocalAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("local");
        return Task.FromResult(new StorageSwitchResult(Answer));
    }

    public Task<StorageSwitchResult> SwitchToServerAsync(
        string serverAddress,
        StorageSwitchFollowUp followUp = StorageSwitchFollowUp.None,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"server {serverAddress} {followUp}");
        return Task.FromResult(new StorageSwitchResult(Answer));
    }

    public async Task<StorageSwitchResult> LeaveRevokedServerAsync(
        Func<CancellationToken, Task> announce,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("leave");
        await announce(cancellationToken);
        return new StorageSwitchResult(Answer);
    }
}

internal sealed class UnusedEntra : IEntraSignIn
{
    public EntraSignInResult Result { get; set; } = new(EntraSignInOutcome.Cancelled);

    public int Calls { get; private set; }

    public Task<EntraSignInResult> AcquireAccessTokenAsync(EntraInfoResponse entra, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
