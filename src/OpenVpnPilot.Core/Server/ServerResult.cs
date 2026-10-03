using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// What became of a call to the server, in the few kinds a caller has to tell apart.
/// </summary>
public enum ServerOutcome
{
    /// <summary>
    /// The server answered with success.
    /// </summary>
    Success,

    /// <summary>
    /// The server refused; <see cref="ServerResult.Problem"/> says why. Branch on its code.
    /// </summary>
    Problem,

    /// <summary>
    /// The server could not be reached: no network, no name, refused, reset or timed out. Work on
    /// from the local copy and try again later.
    /// </summary>
    Offline,

    /// <summary>
    /// The connection was refused because the server's certificate is not trusted, or TLS could not
    /// be agreed. A configuration problem for the operator; never something to bypass.
    /// </summary>
    TlsRefused,

    /// <summary>
    /// The answer carried the wipe directive, or one did before. Nothing more is sent to this server
    /// and the wipe has been announced; the caller stops.
    /// </summary>
    Wiped,

    /// <summary>
    /// There is no session to make the call with, so nothing was sent. Sign in.
    /// </summary>
    NotSignedIn,

    /// <summary>
    /// The server answered with success but the body was not what the contract describes. A defect,
    /// or an address that points at something else.
    /// </summary>
    InvalidResponse,

    /// <summary>
    /// This installation has no identity to send, because its settings could not be read, so nothing
    /// was sent. Starting the application again reads them again.
    /// </summary>
    IdentityUnavailable,
}

/// <summary>
/// The result of a call that answers no value.
/// </summary>
/// <remarks>
/// Every failure of the transport ends up here rather than as an exception, so a view model never
/// sees an <see cref="HttpRequestException"/>. Cancellation by the caller is the one exception: it
/// still surfaces as <see cref="OperationCanceledException"/>, because it is not a failure.
/// </remarks>
public class ServerResult
{
    protected ServerResult(
        ServerOutcome outcome,
        int? status,
        string? requestId,
        ServerProblem? problem,
        TimeSpan? retryAfter,
        string? detail)
    {
        Outcome = outcome;
        Status = status;
        RequestId = requestId;
        Problem = problem;
        RetryAfter = retryAfter;
        Detail = detail;
    }

    public ServerOutcome Outcome { get; }

    public bool IsSuccess => Outcome == ServerOutcome.Success;

    /// <summary>
    /// The HTTP status, when an answer arrived.
    /// </summary>
    public int? Status { get; }

    /// <summary>
    /// The request id the server echoed, or the one sent when no answer came. Goes into every log
    /// line and every message about a failure.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>
    /// The refusal, for <see cref="ServerOutcome.Problem"/> and usually for <see cref="ServerOutcome.Wiped"/>.
    /// </summary>
    public ServerProblem? Problem { get; }

    /// <summary>
    /// The problem's code, or null.
    /// </summary>
    public string? Code => Problem?.Code;

    /// <summary>
    /// How long the server asked to wait, from <c>Retry-After</c>.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// A factual description of a failure that has no problem details, for the log. Never shown to
    /// a person as it is; the wording is localised where it is shown.
    /// </summary>
    public string? Detail { get; }

    public static ServerResult Succeeded(int status, string? requestId) =>
        new(ServerOutcome.Success, status, requestId, null, null, null);

    public static ServerResult Failed(
        ServerOutcome outcome,
        int? status = null,
        string? requestId = null,
        ServerProblem? problem = null,
        TimeSpan? retryAfter = null,
        string? detail = null)
    {
        if (outcome == ServerOutcome.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), "A failure cannot be a success.");
        }

        return new ServerResult(outcome, status, requestId, problem, retryAfter, detail);
    }

    public static ServerResult<T> Succeeded<T>(T value, int status, string? requestId) =>
        new(ServerOutcome.Success, value, status, requestId, null, null, null);

    public static ServerResult<T> Failed<T>(
        ServerOutcome outcome,
        int? status = null,
        string? requestId = null,
        ServerProblem? problem = null,
        TimeSpan? retryAfter = null,
        string? detail = null)
    {
        if (outcome == ServerOutcome.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), "A failure cannot be a success.");
        }

        return new ServerResult<T>(outcome, default, status, requestId, problem, retryAfter, detail);
    }

    /// <summary>
    /// The same failure as a result of another type, for a caller that passes it on.
    /// </summary>
    public ServerResult<T> AsFailure<T>()
    {
        if (IsSuccess)
        {
            throw new InvalidOperationException("Only a failure can be passed on without its value.");
        }

        return Failed<T>(Outcome, Status, RequestId, Problem, RetryAfter, Detail);
    }
}

/// <summary>
/// The result of a call that answers a value.
/// </summary>
public sealed class ServerResult<T> : ServerResult
{
    private readonly T? value;

    internal ServerResult(
        ServerOutcome outcome,
        T? value,
        int? status,
        string? requestId,
        ServerProblem? problem,
        TimeSpan? retryAfter,
        string? detail)
        : base(outcome, status, requestId, problem, retryAfter, detail)
    {
        this.value = value;
    }

    /// <summary>
    /// The answer. Only read it after checking <see cref="ServerResult.IsSuccess"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call did not succeed.</exception>
    public T Value => IsSuccess
        ? value!
        : throw new InvalidOperationException($"The call ended with {Outcome}, so it has no value.");
}
