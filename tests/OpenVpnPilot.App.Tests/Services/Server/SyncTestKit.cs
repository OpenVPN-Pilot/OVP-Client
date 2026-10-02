using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// A clock whose time and timers only move when the test moves them.
/// </summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);

        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    /// <summary>
    /// How far away the nearest timer that will fire is, or null when none is armed.
    /// </summary>
    public TimeSpan? NextDue
    {
        get
        {
            lock (gate)
            {
                DateTimeOffset? due = timers.Where(timer => timer.Due is not null).Min(timer => timer.Due);
                return due - now;
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;

        lock (gate)
        {
            target = now + by;
        }

        while (true)
        {
            ManualTimer? next;

            lock (gate)
            {
                next = timers
                    .Where(timer => timer.Due is { } due && due <= target)
                    .OrderBy(timer => timer.Due)
                    .FirstOrDefault();

                if (next is null)
                {
                    now = target;
                    return;
                }

                now = next.Due!.Value;
                next.Due = next.Period is { } period && period > TimeSpan.Zero ? now + period : null;
            }

            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate)
        {
            timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeNetwork : INetworkAvailability
{
    public event EventHandler<bool>? AvailabilityChanged;

    public void Raise(bool available) => AvailabilityChanged?.Invoke(this, available);
}

/// <summary>
/// Tunnels that are up for the profiles a test names, and a record of which were ended and when.
/// </summary>
internal sealed class FakeRemovedTunnels : IRemovedProfileTunnels
{
    public HashSet<Guid> Up { get; } = [];

    public List<Guid> Ended { get; } = [];

    /// <summary>
    /// Run as each tunnel ends, so a test can look at the database at that moment.
    /// </summary>
    public Func<Guid, Task>? OnEnded { get; set; }

    public async Task<IReadOnlyList<Guid>> DisconnectAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken)
    {
        List<Guid> ended = [];

        foreach (Guid id in profileIds.Where(Up.Contains))
        {
            if (OnEnded is { } check)
            {
                await check(id);
            }

            Up.Remove(id);
            ended.Add(id);
        }

        Ended.AddRange(ended);
        return ended;
    }
}

internal sealed class InlineUserInterfaceThread : IUserInterfaceThread
{
    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default) => work();
}

internal sealed class RecordingNotifier : ILibraryChangeNotifier
{
    private readonly List<LibraryChanges> notified = [];

    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public IReadOnlyList<LibraryChanges> Notified
    {
        get
        {
            lock (notified)
            {
                return [.. notified];
            }
        }
    }

    public void Notify(LibraryChanges changes)
    {
        if (changes == LibraryChanges.None)
        {
            return;
        }

        lock (notified)
        {
            notified.Add(changes);
        }

        Changed?.Invoke(this, new LibraryChangedEventArgs(changes));
    }
}

/// <summary>
/// The server's side of the synchronisation, scripted: what it holds and how it answers.
/// </summary>
/// <remarks>
/// Every endpoint the engine uses answers the way the contract describes unless a test puts its own
/// answer in front with <see cref="On"/>.
/// </remarks>
internal sealed class SyncServer
{
    private readonly List<(HttpMethod Method, string Path, Func<SentRequest, Task<HttpResponseMessage>> Answer)> overrides = [];

    public SyncServer()
    {
        Changes = since => Feed(since == 0, 1, profiles: Held());
    }

    public static readonly DateTimeOffset Moment = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    public bool Offline { get; set; }

    public Func<long, SyncChangesResponse> Changes { get; set; }

    public Dictionary<Guid, string> Names { get; } = [];

    /// <summary>
    /// Every profile the server holds, as its feed and its list describe them.
    /// </summary>
    public List<ProfileResponse> Held() => [.. Configurations.Select(pair => Profile(pair.Key, Names.GetValueOrDefault(pair.Key, "held"), pair.Value))];

    public Dictionary<Guid, string> Configurations { get; } = [];

    public FavouritesDocument Favourites { get; set; } = new([]);

    public HotkeysDocument Hotkeys { get; set; } = new([]);

    public SettingsResponse Settings { get; set; } = StoredSettings(new PilotSettings());

    public void On(HttpMethod method, string path, Func<SentRequest, HttpResponseMessage> answer) =>
        overrides.Insert(0, (method, path, request => Task.FromResult(answer(request))));

    public void On(HttpMethod method, string path, Func<SentRequest, Task<HttpResponseMessage>> answer) =>
        overrides.Insert(0, (method, path, answer));

    public async Task<HttpResponseMessage> AnswerAsync(SentRequest request)
    {
        if (Offline)
        {
            throw new HttpRequestException("No route to the server.");
        }

        foreach ((HttpMethod method, string path, Func<SentRequest, Task<HttpResponseMessage>> answer) in overrides)
        {
            if (request.Method == method && request.Path == path)
            {
                return await answer(request);
            }
        }

        return DefaultAnswer(request);
    }

    public static SyncChangesResponse Feed(
        bool full,
        long cursor,
        IReadOnlyList<ProfileResponse>? profiles = null,
        IReadOnlyList<TagResponse>? tags = null,
        IReadOnlyList<VaultEntryResponse>? vault = null,
        IReadOnlyList<Guid>? deletedProfiles = null,
        IReadOnlyList<Guid>? deletedTags = null,
        IReadOnlyList<VaultKeyResponse>? deletedVault = null) =>
        new(cursor, full, profiles ?? [], tags ?? [], vault ?? [], deletedProfiles ?? [], deletedTags ?? [], deletedVault ?? []);

    public static ProfileResponse Profile(Guid id, string name, string configuration, params string[] tags) =>
        new(
            id,
            name,
            "vpn.example.com",
            1194,
            "udp",
            RequiresCredentials: false,
            HasUnsupportedOptions: false,
            ProtectRoutes: null,
            Notes: null,
            Colour: null,
            Tags: tags,
            ContentHash: ProfileImporter.ComputeHash(configuration),
            ChangeSeq: 1,
            ETag: "\"1\"",
            CreatedAt: Moment,
            CreatedBy: "admin",
            UpdatedAt: Moment,
            UpdatedBy: "admin");

    public static VaultEntryResponse VaultEntry(Guid profileId, string realm, string? username, string password) =>
        new(profileId, realm, username, password, 1, Moment, "admin", Moment, "admin");

    public static SettingsResponse StoredSettings(PilotSettings settings) =>
        new(PilotSettings.CurrentSchemaVersion, PilotSettingsTransfer.Export(settings), "\"7\"", Moment);

    public static SettingsResponse NothingStored { get; } =
        new(0, JsonDocument.Parse("{}").RootElement.Clone(), null, null);

    /// <summary>
    /// Adds a profile to what the server holds, with its configuration.
    /// </summary>
    public ProfileResponse Hold(Guid id, string name, string configuration, params string[] tags)
    {
        Configurations[id] = configuration;
        Names[id] = name;
        return Profile(id, name, configuration, tags);
    }

    public HttpResponseMessage DefaultAnswer(SentRequest request)
    {
        string path = request.Path;

        if (request.Method == HttpMethod.Get && path == "/api/v1/sync/changes")
        {
            long since = long.Parse(request.Query.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
            return Answers.Json(Changes(since));
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/configuration", StringComparison.Ordinal))
        {
            Guid id = Guid.Parse(path.Split('/')[4]);

            return Configurations.TryGetValue(id, out string? text)
                ? Answers.Json(new ProfileConfigurationResponse(id, ProfileImporter.ComputeHash(text), text))
                : Answers.Problem(HttpStatusCode.NotFound, ServerErrorCodes.ProfileNotFound);
        }

        if (path == "/api/v1/me/favourites")
        {
            return Answers.Json(request.Method == HttpMethod.Put ? request.Json.Deserialize<FavouritesDocument>(ServerJson.Options)! : Favourites);
        }

        if (path == "/api/v1/me/hotkeys")
        {
            return Answers.Json(request.Method == HttpMethod.Put ? request.Json.Deserialize<HotkeysDocument>(ServerJson.Options)! : Hotkeys);
        }

        if (path == "/api/v1/me/settings")
        {
            return Answers.Json(request.Method == HttpMethod.Put
                ? new SettingsResponse(PilotSettings.CurrentSchemaVersion, request.Json.GetProperty("document").Clone(), "\"8\"", Moment)
                : Settings);
        }

        if (request.Method == HttpMethod.Post && path == "/api/v1/profiles")
        {
            ProfileCreateRequest created = request.Json.Deserialize<ProfileCreateRequest>(ServerJson.Options)!;
            Guid id = Guid.NewGuid();
            string stored = ServerContentHash.Normalise(created.Configuration);
            Configurations[id] = stored;
            Names[id] = created.Name;
            return Answers.Json(Profile(id, created.Name, stored, [.. created.Tags ?? []]), HttpStatusCode.Created);
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/api/v1/profiles/", StringComparison.Ordinal)
            && path.Split('/') is [_, _, _, _, string owner, "vault", string realm])
        {
            Guid id = Guid.Parse(owner);
            VaultEntryRequest entry = request.Json.Deserialize<VaultEntryRequest>(ServerJson.Options)!;

            return Configurations.ContainsKey(id)
                ? Answers.Json(VaultEntry(id, realm, entry.Username, entry.Password), HttpStatusCode.Created)
                : Answers.Problem(HttpStatusCode.NotFound, ServerErrorCodes.ProfileNotFound);
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/profiles/", StringComparison.Ordinal))
        {
            Guid id = Guid.Parse(path.Split('/')[4]);

            return Configurations.TryGetValue(id, out string? text)
                ? Answers.Json(Profile(id, Names.GetValueOrDefault(id, "held"), text))
                : Answers.Problem(HttpStatusCode.NotFound, ServerErrorCodes.ProfileNotFound);
        }

        if (request.Method == HttpMethod.Put && path.StartsWith("/api/v1/profiles/", StringComparison.Ordinal))
        {
            Guid id = Guid.Parse(path.Split('/')[4]);
            ProfileUpdateRequest update = request.Json.Deserialize<ProfileUpdateRequest>(ServerJson.Options)!;
            Names[id] = update.Name;

            if (update.Configuration is { } configuration)
            {
                Configurations[id] = ServerContentHash.Normalise(configuration);
            }

            return Answers.Json(Profile(id, update.Name, Configurations.GetValueOrDefault(id, string.Empty), [.. update.Tags ?? []]));
        }

        if (request.Method == HttpMethod.Get && path == "/api/v1/profiles")
        {
            return Answers.Json(Held());
        }

        if (request.Method == HttpMethod.Delete)
        {
            return Answers.NoContent();
        }

        if (path == "/health/ready")
        {
            return Answers.Raw("Healthy", "text/plain");
        }

        return Answers.Problem(HttpStatusCode.NotFound, ServerErrorCodes.NotFound);
    }
}

/// <summary>
/// An engine over a migrated database and a scripted server, signed in.
/// </summary>
internal sealed class SyncHarness : IAsyncDisposable
{
    private SyncHarness(TestDatabase database, SyncServer server, TestServer network, TimeProvider time, ILoggerFactory logs)
    {
        Database = database;
        Server = server;
        Network = network;
        Outbox = new Outbox(database.Factory, TimeProvider.System, new Logger<Outbox>(logs));
        Maintenance = new ServerProfileMaintenance(database.Factory, network.Secrets, Held, Outbox, new Logger<ServerProfileMaintenance>(logs));
        Settings = new RecordingSettingsService(
            SettingsBackend,
            new ChangeRecorder(Outbox, new FixedStorageMode(true)),
            new InlineUserInterfaceThread());

        Engine = new SyncEngine(
            network.Connection,
            database.Factory,
            Outbox,
            Maintenance,
            network.Secrets,
            Held,
            Tunnels,
            Notices,
            Settings,
            Notifier,
            NetworkAvailability,
            time,
            new Logger<SyncEngine>(logs));
    }

    public TestDatabase Database { get; }

    public SyncServer Server { get; }

    public TestServer Network { get; }

    public FakeSecretStore Secrets => Network.Secrets;

    public Outbox Outbox { get; }

    public ServerProfileMaintenance Maintenance { get; }

    public FakeSettingsService SettingsBackend { get; } = new();

    public RecordingSettingsService Settings { get; }

    public RecordingNotifier Notifier { get; } = new();

    public TypedCredentials Held { get; } = new(new FixedStorageMode(true));

    public FakeRemovedTunnels Tunnels { get; } = new();

    public ServerNotices Notices { get; } = new();

    public FakeNetwork NetworkAvailability { get; } = new();

    public SyncEngine Engine { get; }

    public IReadOnlyList<SentRequest> Requests => Network.Handler.Requests;

    /// <param name="logs">Where the engine, the outbox and the maintenance write; nowhere by default.</param>
    public static async Task<SyncHarness> CreateAsync(TimeProvider? time = null, bool signedIn = true, ILoggerFactory? logs = null)
    {
        TestDatabase database = await TestDatabase.CreateAsync();
        SyncServer server = new();
        TestServer network = new(server.AnswerAsync);

        if (signedIn)
        {
            await network.SignedInAsync();
        }

        SyncHarness harness = new(database, server, network, time ?? TimeProvider.System, logs ?? NullLoggerFactory.Instance);

        // As the settings service holds them once loaded: in the current layout.
        await harness.SettingsBackend.ReplaceAsync(new PilotSettings { SchemaVersion = PilotSettings.CurrentSchemaVersion });
        return harness;
    }

    public int Count(HttpMethod method, string path) =>
        Requests.Count(request => request.Method == method && request.Path == path);

    public IReadOnlyList<SentRequest> Sent(HttpMethod method, string path) =>
        [.. Requests.Where(request => request.Method == method && request.Path == path)];

    public async Task<Profile?> ProfileAsync(Guid id)
    {
        await using PilotDbContext context = await Database.Factory.CreateDbContextAsync();

        return await context.Profiles
            .AsNoTracking()
            .Include(profile => profile.Tags)
            .ThenInclude(link => link.Tag)
            .FirstOrDefaultAsync(profile => profile.Id == id);
    }

    public async Task<T> QueryAsync<T>(Func<PilotDbContext, Task<T>> query)
    {
        await using PilotDbContext context = await Database.Factory.CreateDbContextAsync();
        return await query(context);
    }

    public async Task ChangeAsync(Func<PilotDbContext, Task> change)
    {
        await using PilotDbContext context = await Database.Factory.CreateDbContextAsync();
        await change(context);
        await context.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.StopAsync(CancellationToken.None);
        Engine.Dispose();
        Settings.Dispose();
        Network.Dispose();
        await Database.DisposeAsync();
    }

    /// <summary>
    /// Waits, in real time and bounded, for something the engine does on its own thread.
    /// </summary>
    public static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        Stopwatch watch = Stopwatch.StartNew();

        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("Timed out waiting for " + what + ".");
            }

            await Task.Delay(10);
        }
    }
}
