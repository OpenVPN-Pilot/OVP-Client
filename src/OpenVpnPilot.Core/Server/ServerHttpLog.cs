using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.Core.Server;

// Event id blocks of the server connection:
// 3600-3699 connection and HTTP, 3700-3799 sign in and tokens, 3800-3899 sync engine,
// 3900-3949 mode switch and storage, 3950-3999 outbox.

/// <summary>
/// Source generated log messages for the traffic with a server.
/// </summary>
/// <remarks>
/// Values only: method, path, status, code, request id, duration. Never a header value, a body, a
/// token, a password, a configuration or a vault entry. The path carries profile ids and realms,
/// which name things but reveal no secret.
/// </remarks>
internal static partial class ServerHttpLog
{
    [LoggerMessage(
        EventId = 3600,
        Level = LogLevel.Debug,
        Message = "{Method} {Path} answered {Status} in {DurationMs} ms, request {RequestId}.")]
    public static partial void Answered(
        ILogger logger,
        string method,
        string path,
        int status,
        long durationMs,
        string? requestId);

    [LoggerMessage(
        EventId = 3601,
        Level = LogLevel.Warning,
        Message = "{Method} {Path} was refused with {Status} {Code}, request {RequestId}.")]
    public static partial void Refused(
        ILogger logger,
        string method,
        string path,
        int status,
        string? code,
        string? requestId);

    [LoggerMessage(
        EventId = 3602,
        Level = LogLevel.Warning,
        Message = "{Method} {Path} did not reach the server ({Error}), request {RequestId}.")]
    public static partial void Unreachable(
        ILogger logger,
        string method,
        string path,
        string error,
        string? requestId);

    [LoggerMessage(
        EventId = 3603,
        Level = LogLevel.Warning,
        Message = "{Method} {Path} was refused because the server's certificate is not trusted or TLS failed ({Error}), request {RequestId}.")]
    public static partial void TlsRefused(
        ILogger logger,
        string method,
        string path,
        string error,
        string? requestId);

    [LoggerMessage(
        EventId = 3604,
        Level = LogLevel.Warning,
        Message = "{Method} {Path} answered {Status} with the wipe directive, request {RequestId}.")]
    public static partial void WipeDirective(
        ILogger logger,
        string method,
        string path,
        int status,
        string? requestId);

    [LoggerMessage(
        EventId = 3605,
        Level = LogLevel.Warning,
        Message = "{Method} {Path} got no answer within {TimeoutSeconds} s, request {RequestId}.")]
    public static partial void TimedOut(
        ILogger logger,
        string method,
        string path,
        double timeoutSeconds,
        string? requestId);

    [LoggerMessage(
        EventId = 3606,
        Level = LogLevel.Warning,
        Message = "{Method} {Path} answered {Status} with a body that is not what the contract describes, request {RequestId}.")]
    public static partial void UnreadableAnswer(
        ILogger logger,
        string method,
        string path,
        int status,
        string? requestId,
        Exception exception);

    [LoggerMessage(
        EventId = 3607,
        Level = LogLevel.Debug,
        Message = "{Method} {Path} was not sent, because the server has told this client to wipe.")]
    public static partial void SuppressedAfterWipe(ILogger logger, string method, string path);
}
