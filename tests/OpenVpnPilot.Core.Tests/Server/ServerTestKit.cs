using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// One place where things happened, in order, across the keystore and the network, so a test can
/// say which came first.
/// </summary>
internal sealed class Journal
{
    private readonly ConcurrentQueue<string> entries = new();

    public void Add(string entry) => entries.Enqueue(entry);

    public IReadOnlyList<string> Entries => [.. entries];

    public int IndexOf(string entry) => Entries.ToList().IndexOf(entry);
}

/// <summary>
/// A request as the network saw it, copied before the pipeline disposes it.
/// </summary>
internal sealed record SentRequest(
    HttpMethod Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    string? Bearer,
    string? Body,
    bool? ExpectContinue)
{
    /// <summary>
    /// The token the pipeline passed down, which a real network observes and a script may too.
    /// </summary>
    public CancellationToken Cancellation { get; init; }

    public string? Header(string name) => Headers.GetValueOrDefault(name);

    public JsonElement Json => JsonDocument.Parse(Body ?? "null").RootElement;
}

/// <summary>
/// Answers with whatever the test scripts and records every request, in place of the network.
/// </summary>
internal sealed class StubHandler(Func<SentRequest, Task<HttpResponseMessage>> answer, Journal? journal = null)
    : HttpMessageHandler
{
    private readonly ConcurrentQueue<SentRequest> requests = new();

    public StubHandler(Func<SentRequest, HttpResponseMessage> answer, Journal? journal = null)
        : this(request => Task.FromResult(answer(request)), journal)
    {
    }

    public IReadOnlyList<SentRequest> Requests => [.. requests];

    public int Count(string path) => Requests.Count(request => request.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        string? bearer = request.Headers.Authorization?.Parameter;

        SentRequest sent = new(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.RequestUri.Query,
            headers,
            bearer,
            body,
            request.Headers.ExpectContinue)
        {
            Cancellation = cancellationToken,
        };

        requests.Enqueue(sent);
        journal?.Add($"send {sent.Path} {bearer}");

        HttpResponseMessage response = await answer(sent);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>
/// Builds answers in the server's shape.
/// </summary>
internal static class Answers
{
    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK, bool wipe = false) =>
        Raw(JsonSerializer.Serialize(body, ServerJson.Options), "application/json", status, wipe);

    public static HttpResponseMessage Raw(
        string body,
        string mediaType,
        HttpStatusCode status = HttpStatusCode.OK,
        bool wipe = false)
    {
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };

        response.Headers.TryAddWithoutValidation(PilotHeaders.RequestId, "server-" + Guid.NewGuid().ToString("N"));

        if (wipe)
        {
            response.Headers.TryAddWithoutValidation(PilotHeaders.Directive, PilotHeaders.WipeDirective);
        }

        return response;
    }

    public static HttpResponseMessage NoContent(bool wipe = false)
    {
        HttpResponseMessage response = new(HttpStatusCode.NoContent);

        if (wipe)
        {
            response.Headers.TryAddWithoutValidation(PilotHeaders.Directive, PilotHeaders.WipeDirective);
        }

        return response;
    }

    public static HttpResponseMessage Problem(HttpStatusCode status, string code, bool wipe = false)
    {
        string requestId = "req-" + Guid.NewGuid().ToString("N");

        string body = JsonSerializer.Serialize(new
        {
            type = "urn:openvpnpilot:error:" + code,
            title = code,
            status = (int)status,
            detail = "Detail of " + code,
            instance = "/api/v1/somewhere",
            code,
            requestId,
        });

        HttpResponseMessage response = Raw(body, ServerProblem.MediaType, status, wipe);
        response.Headers.Remove(PilotHeaders.RequestId);
        response.Headers.TryAddWithoutValidation(PilotHeaders.RequestId, requestId);
        return response;
    }

    public static HttpResponseMessage Tokens(
        string access,
        string refresh,
        DateTimeOffset expiresAt,
        string role = ServerRoles.Admin) =>
        Json(new TokenResponse(access, expiresAt, refresh, expiresAt.AddDays(30), User(role)));

    public static CurrentUserResponse User(string role = ServerRoles.Admin) =>
        new(Guid.Parse("01a0b3d3-6064-75e9-a940-c28df3732a05"), "operator", "Example Operator", role, ServerAuthModes.File);

    public static ServerInfoResponse Info(
        string name = ServerInfoResponse.ExpectedName,
        string apiVersion = "1",
        string minimum = "1.0.0") =>
        new(name, "1.0.0", apiVersion, minimum, ServerAuthModes.File, true, null);

    public static IReadOnlyList<TagResponse> NoTags { get; } = [];

    /// <summary>
    /// What the network does instead of answering.
    /// </summary>
    public static HttpResponseMessage Fail(Exception exception) => throw exception;
}

/// <summary>
/// A keystore that protects nothing and writes into the journal, which is why it is only a test.
/// </summary>
internal sealed class FakeSecretStore(Journal? journal = null) : ISecretStore
{
    private readonly ConcurrentDictionary<string, StoredSecret> entries = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public IReadOnlyDictionary<string, StoredSecret> Entries => entries;

    public Task<StoredSecret?> TryReadAsync(string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(entries.GetValueOrDefault(reference));

    public Task WriteAsync(string reference, StoredSecret secret, CancellationToken cancellationToken = default)
    {
        entries[reference] = secret;
        journal?.Add($"store {reference} {secret.Password}");
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        entries.TryRemove(reference, out _);
        journal?.Add($"delete {reference}");
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
/// A clock that only moves when the test moves it.
/// </summary>
internal sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FixedVersion(Version version) : IClientVersionProvider
{
    public Version Version { get; } = version;
}

internal sealed class FixedInstallation(Guid id) : IInstallationIdProvider
{
    public Guid InstallationId { get; } = id;
}

/// <summary>
/// Keeps every log line, so a test can show that no secret was written.
/// </summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<string> lines = new();

    public IReadOnlyList<string> Lines => [.. lines];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(lines);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue($"{eventId.Id} {logLevel} {formatter(state, exception)} {exception}");
        }
    }
}

/// <summary>
/// A server connection over a scripted network.
/// </summary>
internal sealed class TestServer : IDisposable
{
    public const string ServerKey = "0123456789abcdef0123456789abcdef";

    public static readonly Uri Address = new("https://pilot.example.com/");

    public static readonly Guid InstallationId = Guid.Parse("6f1c1a52-1d2e-4a3b-9c4d-5e6f7a8b9c0d");

    public static readonly DateTimeOffset Start = new(2026, 9, 18, 9, 23, 9, TimeSpan.Zero);

    public TestServer(Func<SentRequest, Task<HttpResponseMessage>> answer, Uri? address = null, Version? version = null)
    {
        Handler = new StubHandler(answer, Journal);
        Secrets = new FakeSecretStore(Journal);

        ServerHttpClientFactory clients = new(
            new FixedVersion(version ?? new Version(1, 9, 0)),
            new FixedInstallation(InstallationId),
            Time,
            Logs,
            () => Handler,
            ClientPlatform.Windows);

        ServerConnectionFactory connections = new(
            clients,
            new FixedVersion(version ?? new Version(1, 9, 0)),
            Secrets,
            Time,
            Logs);

        Connection = connections.Create(address ?? Address, ServerKey);
    }

    public TestServer(Func<SentRequest, HttpResponseMessage> answer, Uri? address = null, Version? version = null)
        : this(request => Task.FromResult(answer(request)), address, version)
    {
    }

    public Journal Journal { get; } = new();

    public StubHandler Handler { get; }

    public FakeSecretStore Secrets { get; }

    public FakeTime Time { get; } = new(Start);

    public RecordingLoggerFactory Logs { get; } = new();

    public IServerConnection Connection { get; }

    public IServerApi Api => Connection.Api;

    public IServerSession Session => Connection.Session;

    public static string RefreshReference => SecretReference.ForServerRefreshToken(ServerKey);

    /// <summary>
    /// Signs in as the server would let anyone, without going through the network.
    /// </summary>
    public Task SignedInAsync(string access = "access-1", string refresh = "refresh-1", TimeSpan? validFor = null) =>
        Session.EstablishAsync(new TokenResponse(
            access,
            Time.Now + (validFor ?? TimeSpan.FromMinutes(15)),
            refresh,
            Time.Now.AddDays(30),
            Answers.User()));

    public void Dispose() => Connection.Dispose();

    public static string Timestamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
