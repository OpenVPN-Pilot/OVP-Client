using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Answers whether this machine can run a tunnel, and keeps the answer where the interface can say so.
/// </summary>
/// <remarks>
/// The probe existed and nothing in the window ever asked it. What the user got instead was a client
/// that started perfectly, listed every profile, and ended the process the moment they pressed
/// connect, because the interactive service was not there and the exception that says so travels
/// out of a command straight onto the user interface thread.
///
/// A missing dependency is a thing to be told about, not a thing to find out by losing the
/// application. This is therefore asked twice: once at startup, so the window can say what is wrong
/// before anything is attempted, and once immediately before each connection, because a service can
/// be stopped while the application is running and the answer from ten minutes ago would be a guess.
///
/// The result is cached and refreshed on demand rather than probed continuously. It reads the
/// registry and opens a pipe, which is cheap but not free, and nothing about it changes on its own
/// between one connection and the next.
/// </remarks>
public sealed class EnvironmentGate
{
    /// <summary>
    /// How long an answer is reused before the environment is examined again.
    /// </summary>
    /// <remarks>
    /// Connecting everything a filter shows connects the profiles one after another, and probing
    /// before each of them would put a registry read and a pipe connection between every pair.
    /// Nothing the probe looks at changes in a few seconds without somebody doing it deliberately.
    /// </remarks>
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);

    private readonly IOpenVpnEnvironmentProbe probe;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EnvironmentGate> logger;

    private DateTimeOffset examinedAt = DateTimeOffset.MinValue;

    public EnvironmentGate(
        IOpenVpnEnvironmentProbe probe,
        TimeProvider timeProvider,
        ILogger<EnvironmentGate> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.probe = probe;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// The most recent report, or null when the environment has not been examined yet.
    /// </summary>
    public OpenVpnEnvironmentReport? Report { get; private set; }

    /// <summary>
    /// The checks that block a connection, in the order they were evaluated.
    /// </summary>
    public IReadOnlyList<EnvironmentCheck> Blocking => Report is null
        ? []
        : [.. Report.Checks.Where(check => check.Status == EnvironmentCheckStatus.Failed)];

    /// <summary>
    /// True when something is known to stand in the way of connecting.
    /// </summary>
    /// <remarks>
    /// An environment that has not been examined does not block. Refusing to connect because a check
    /// has not run yet would turn a probe that failed into a client that cannot be used at all.
    /// </remarks>
    public bool IsBlocked => Report is not null && !Report.CanConnect;

    /// <summary>
    /// Examines the environment and keeps the result.
    /// </summary>
    /// <remarks>
    /// A probe that throws is reported and treated as an environment that could not be examined,
    /// which does not block. Reading the registry and opening a pipe are both things that can fail
    /// for reasons that have nothing to do with whether OpenVPN is installed.
    /// </remarks>
    public async Task<OpenVpnEnvironmentReport?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Report = await probe.ProbeAsync(cancellationToken);
            examinedAt = timeProvider.GetUtcNow();

            if (Report.CanConnect)
            {
                // Recorded either way. A log that only mentions the environment when it is broken
                // cannot be used to establish that it was fine a moment before something else went
                // wrong, which is exactly the question a support request asks.
                EnvironmentGateLog.Ready(logger);
            }
            else
            {
                EnvironmentGateLog.Blocked(
                    logger,
                    string.Join("; ", Blocking.Select(check => $"{check.Id}: {check.Detail}")));
            }

            return Report;
        }
        catch (Exception exception)
        {
            // Deliberately everything: this decides whether the user is warned, and a warning that
            // could not be produced must not be the thing that stops the application from running.
            EnvironmentGateLog.ProbeFailed(logger, exception);
            return null;
        }
    }

    /// <summary>
    /// Examines the environment unless the previous answer is still recent.
    /// </summary>
    public async Task EnsureExaminedAsync(CancellationToken cancellationToken = default)
    {
        if (Report is not null && timeProvider.GetUtcNow() - examinedAt < Freshness)
        {
            return;
        }

        await RefreshAsync(cancellationToken);
    }
}

/// <summary>
/// Source generated log messages for <see cref="EnvironmentGate"/>.
/// </summary>
internal static partial class EnvironmentGateLog
{
    [LoggerMessage(
        EventId = 3400,
        Level = LogLevel.Warning,
        Message = "Connections are blocked by the environment. {Detail}")]
    public static partial void Blocked(ILogger logger, string detail);

    [LoggerMessage(
        EventId = 3402,
        Level = LogLevel.Information,
        Message = "The OpenVPN environment is ready.")]
    public static partial void Ready(ILogger logger);

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Warning,
        Message = "The OpenVPN environment could not be examined.")]
    public static partial void ProbeFailed(ILogger logger, Exception exception);
}
