using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// Adds the headers the contract makes mandatory to every call that needs them.
/// </summary>
/// <remarks>
/// <c>GET /api/v1/server/info</c> and <c>/health*</c> are asked before a client knows what the
/// server expects and carry none of them, not even a request id. Everything else carries the
/// version, the API version, the installation id, the platform, the moment it is sent and a fresh
/// request id, so a repeated call is a new request in the server's log as well as in this one.
/// </remarks>
public sealed class PilotHeadersHandler : DelegatingHandler
{
    private readonly Uri baseAddress;
    private readonly IClientVersionProvider version;
    private readonly IInstallationIdProvider installation;
    private readonly TimeProvider time;
    private readonly string platform;

    public PilotHeadersHandler(
        Uri baseAddress,
        IClientVersionProvider version,
        IInstallationIdProvider installation,
        TimeProvider time,
        string platform)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        this.baseAddress = baseAddress;
        this.version = version;
        this.installation = installation;
        this.time = time;
        this.platform = platform;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is { } uri && !IsAnonymous(baseAddress, uri))
        {
            if (!installation.IsAvailable)
            {
                throw new InstallationIdentityUnavailableException();
            }

            Set(request, PilotHeaders.ClientVersion, version.Version.ToString(3));
            Set(request, PilotHeaders.ApiVersion, PilotHeaders.CurrentApiVersion);
            Set(request, PilotHeaders.ClientId, installation.InstallationId.ToString("D"));
            Set(request, PilotHeaders.Platform, platform);
            Set(
                request,
                PilotHeaders.Timestamp,
                time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            Set(request, PilotHeaders.RequestId, Guid.NewGuid().ToString("D"));
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// True for the calls the contract lets through without any <c>X-Pilot-*</c> header.
    /// </summary>
    /// <remarks>
    /// Compared below the base address, so a server published under a path prefix is recognised
    /// as well as one at the root.
    /// </remarks>
    internal static bool IsAnonymous(Uri baseAddress, Uri requestUri)
    {
        string basePath = baseAddress.AbsolutePath.TrimEnd('/');
        string path = requestUri.AbsolutePath;

        if (basePath.Length > 0 && path.StartsWith(basePath, StringComparison.Ordinal))
        {
            path = path[basePath.Length..];
        }

        return string.Equals(path, "/api/v1/server/info", StringComparison.Ordinal)
            || string.Equals(path, "/health", StringComparison.Ordinal)
            || path.StartsWith("/health/", StringComparison.Ordinal);
    }

    private static void Set(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}

/// <summary>
/// Looks at every answer for the wipe directive before anything else reads it.
/// </summary>
/// <remarks>
/// It sits next to the network so that no other part of the pipeline, and no caller, can look at an
/// answer and act on it first. The header decides, whatever the status.
/// </remarks>
public sealed class WipeDirectiveHandler : DelegatingHandler
{
    private readonly Uri baseAddress;
    private readonly ServerWipeSignal signal;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public WipeDirectiveHandler(Uri baseAddress, ServerWipeSignal signal, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        this.baseAddress = baseAddress;
        this.signal = signal;
        this.time = time;
        this.logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        if (CarriesWipe(response))
        {
            string method = request.Method.Method;
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string? requestId = ServerHeaderValues.Of(response, PilotHeaders.RequestId)
                ?? ServerHeaderValues.Of(request, PilotHeaders.RequestId);

            ServerHttpLog.WipeDirective(logger, method, path, (int)response.StatusCode, requestId);

            signal.Report(new ServerWipeDirective(
                baseAddress,
                method,
                path,
                (int)response.StatusCode,
                requestId,
                time.GetUtcNow()));
        }

        return response;
    }

    internal static bool CarriesWipe(HttpResponseMessage response) =>
        response.Headers.TryGetValues(PilotHeaders.Directive, out IEnumerable<string>? values)
        && values.Any(value => value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(PilotHeaders.WipeDirective, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Writes one debug line per exchange with the server: method, path, status, duration, request id.
/// </summary>
public sealed class ServerTrafficLogHandler : DelegatingHandler
{
    private readonly ILogger logger;

    public ServerTrafficLogHandler(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            long duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            string? requestId = ServerHeaderValues.Of(response, PilotHeaders.RequestId)
                ?? ServerHeaderValues.Of(request, PilotHeaders.RequestId);

            ServerHttpLog.Answered(
                logger,
                request.Method.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                (int)response.StatusCode,
                duration,
                requestId);
        }

        return response;
    }
}

/// <summary>
/// Reads single header values without caring whether a header is absent.
/// </summary>
internal static class ServerHeaderValues
{
    public static string? Of(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

    public static string? Of(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
}
