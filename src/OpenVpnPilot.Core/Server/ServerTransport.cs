using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// One call to the server, described rather than built, so it can be sent again after a refresh.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">Relative to the server's address, such as <c>api/v1/tags</c>, query included.</param>
/// <param name="Body">Serialised as the contract's JSON; null for no body.</param>
/// <param name="BearerToken">The access token to send, or null for an anonymous call.</param>
/// <param name="IfMatch">The <c>If-Match</c> value, or null for none.</param>
/// <param name="Timeout">How long the whole call may take; null for the ordinary limit.</param>
internal sealed record ServerRequest(
    HttpMethod Method,
    string Path,
    object? Body = null,
    string? BearerToken = null,
    string? IfMatch = null,
    TimeSpan? Timeout = null)
{
    // The body and the token are left out, so the record can never print a secret.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Method = ").Append(Method).Append(", Path = ").Append(Path);
        return true;
    }
}

/// <summary>
/// Sends calls and turns whatever happens into a <see cref="ServerResult"/>.
/// </summary>
/// <remarks>
/// The only place that catches the transport's exceptions. An unreachable server, a name that does
/// not resolve, a reset connection and a call that ran out of time are all offline; a certificate
/// that is not trusted is its own outcome, because it is a configuration problem nobody should be
/// invited to click away. Only the caller's own cancellation passes through as an exception.
/// </remarks>
internal sealed class ServerTransport
{
    /// <summary>
    /// The limit of an ordinary call.
    /// </summary>
    public static readonly TimeSpan OrdinaryTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The limit of a batch upload, which may carry up to 64 MiB.
    /// </summary>
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient client;
    private readonly IServerWipeSignal wipe;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public ServerTransport(HttpClient client, IServerWipeSignal wipe, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(wipe);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        this.client = client;
        this.wipe = wipe;
        this.time = time;
        this.logger = logger;
    }

    public async Task<ServerResult> SendAsync(ServerRequest request, CancellationToken cancellationToken) =>
        await SendForStatusAsync(request, cancellationToken);

    /// <summary>
    /// A call whose answer has no body worth reading; the value only says it succeeded.
    /// </summary>
    public Task<ServerResult<bool>> SendForStatusAsync(ServerRequest request, CancellationToken cancellationToken) =>
        SendAsync(request, static (_, _) => Task.FromResult(true), cancellationToken);

    public Task<ServerResult<T>> SendAsync<T>(ServerRequest request, CancellationToken cancellationToken) =>
        SendAsync(request, ReadJsonAsync<T>, cancellationToken);

    private async Task<ServerResult<T>> SendAsync<T>(
        ServerRequest request,
        Func<HttpContent, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string method = request.Method.Method;
        string path = PathOf(request.Path);

        if (wipe.IsRequested)
        {
            // The contract forbids retrying once the directive has arrived, whatever is asked.
            ServerHttpLog.SuppressedAfterWipe(logger, method, path);
            return ServerResult.Failed<T>(ServerOutcome.Wiped, detail: "The server has told this client to wipe.");
        }

        TimeSpan limit = request.Timeout ?? OrdinaryTimeout;

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);

        using HttpRequestMessage message = Build(request);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token);

            int status = (int)response.StatusCode;
            string? requestId = ServerHeaderValues.Of(response, PilotHeaders.RequestId)
                ?? ServerHeaderValues.Of(message, PilotHeaders.RequestId);

            if (WipeDirectiveHandler.CarriesWipe(response))
            {
                // Already announced by the handler. The body is read only to keep its request id.
                string wipeBody = await response.Content.ReadAsStringAsync(deadline.Token);
                ServerProblem directive = ServerProblem.Read(wipeBody, status, requestId);

                return ServerResult.Failed<T>(ServerOutcome.Wiped, status, directive.RequestId ?? requestId, directive);
            }

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    T value = await read(response.Content, deadline.Token);
                    return ServerResult.Succeeded<T>(value, status, requestId);
                }
                catch (JsonException exception)
                {
                    ServerHttpLog.UnreadableAnswer(logger, method, path, status, requestId, exception);
                    return ServerResult.Failed<T>(
                        ServerOutcome.InvalidResponse,
                        status,
                        requestId,
                        detail: "The answer is not what the contract describes.");
                }
            }

            string body = await response.Content.ReadAsStringAsync(deadline.Token);
            ServerProblem problem = ServerProblem.Read(body, status, requestId);

            ServerHttpLog.Refused(logger, method, path, status, problem.Code, problem.RequestId);

            return ServerResult.Failed<T>(
                ServerOutcome.Problem,
                status,
                problem.RequestId,
                problem,
                RetryAfterOf(response));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string? requestId = ServerHeaderValues.Of(message, PilotHeaders.RequestId);
            ServerHttpLog.TimedOut(logger, method, path, limit.TotalSeconds, requestId);

            return ServerResult.Failed<T>(ServerOutcome.Offline, requestId: requestId, detail: "No answer in time.");
        }
        catch (HttpRequestException exception) when (IsTlsFailure(exception))
        {
            string? requestId = ServerHeaderValues.Of(message, PilotHeaders.RequestId);
            ServerHttpLog.TlsRefused(logger, method, path, exception.HttpRequestError.ToString(), requestId);

            return ServerResult.Failed<T>(ServerOutcome.TlsRefused, requestId: requestId, detail: exception.Message);
        }
        catch (HttpRequestException exception)
        {
            string? requestId = ServerHeaderValues.Of(message, PilotHeaders.RequestId);
            ServerHttpLog.Unreachable(logger, method, path, exception.HttpRequestError.ToString(), requestId);

            return ServerResult.Failed<T>(ServerOutcome.Offline, requestId: requestId, detail: exception.Message);
        }
        catch (IOException exception)
        {
            // A connection that broke while the answer was being read.
            string? requestId = ServerHeaderValues.Of(message, PilotHeaders.RequestId);
            ServerHttpLog.Unreachable(logger, method, path, nameof(IOException), requestId);

            return ServerResult.Failed<T>(ServerOutcome.Offline, requestId: requestId, detail: exception.Message);
        }
    }

    private static HttpRequestMessage Build(ServerRequest request)
    {
        HttpRequestMessage message = new(request.Method, new Uri(request.Path, UriKind.Relative));

        if (request.Body is not null)
        {
            ByteArrayContent content = new(JsonSerializer.SerializeToUtf8Bytes(
                request.Body,
                request.Body.GetType(),
                ServerJson.Options));

            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            message.Content = content;
        }

        if (request.BearerToken is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.BearerToken);
        }

        if (request.IfMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", request.IfMatch);
        }

        return message;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);

        return await JsonSerializer.DeserializeAsync<T>(stream, ServerJson.Options, cancellationToken)
            ?? throw new JsonException("The answer was null.");
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retry = response.Headers.RetryAfter;

        if (retry?.Delta is { } delta)
        {
            return delta;
        }

        if (retry?.Date is { } date)
        {
            TimeSpan wait = date - time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// True when the connection failed over the certificate or the TLS agreement.
    /// </summary>
    internal static bool IsTlsFailure(HttpRequestException exception) =>
        exception.HttpRequestError == HttpRequestError.SecureConnectionError
        || exception.InnerException is AuthenticationException;

    /// <summary>
    /// The path as it is logged: without the query, whose values are nobody's business in a log.
    /// </summary>
    private static string PathOf(string relative)
    {
        int query = relative.IndexOf('?', StringComparison.Ordinal);
        return "/" + (query < 0 ? relative : relative[..query]);
    }
}

/// <summary>
/// The status codes the client branches on without a code of the server's.
/// </summary>
internal static class ServerStatus
{
    public const int Unauthorized = (int)HttpStatusCode.Unauthorized;
}
