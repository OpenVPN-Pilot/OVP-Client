using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Helper;

/// <summary>
/// Asks the helper to end a tunnel that did not stop when it was told to.
/// </summary>
/// <remarks>
/// OpenVPN runs as root here, and the kernel does not let this process signal it. The helper started
/// it, waits for it, and is the one party that may end it.
///
/// The helper only ends tunnels its caller's account started, and one that has already exited is an
/// answer rather than an error.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class HelperProcessTerminator : IOpenVpnProcessTerminator
{
    /// <summary>
    /// What the helper adds to the grace period: sending the signal, waiting and reaping.
    /// </summary>
    private static readonly TimeSpan HelperAllowance = TimeSpan.FromSeconds(10);

    private readonly HelperSession session;
    private readonly ILogger<HelperProcessTerminator> logger;

    public HelperProcessTerminator(HelperSession session, ILogger<HelperProcessTerminator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        this.session = session;
        this.logger = logger ?? NullLogger<HelperProcessTerminator>.Instance;
    }

    public async Task EnsureExitedAsync(
        int processId,
        TimeSpan grace,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            HelperResponse response = await session.SendAsync(
                new HelperRequest
                {
                    Type = HelperMessageType.Terminate,
                    ProcessId = processId,
                    GraceMilliseconds = (int)Math.Clamp(grace.TotalMilliseconds, 0, int.MaxValue),
                },
                cancellationToken,
                grace + HelperAllowance);

            if (response.Type != HelperMessageType.Terminated)
            {
                TerminatorLog.Refused(logger, processId, response.RefusalCode ?? "unknown", response.Message ?? string.Empty);
            }
        }
        catch (Exception exception) when (exception is HelperUnavailableException or HelperProtocolException)
        {
            // A helper that went away took its tunnels with it, so there is nothing left to end.
            TerminatorLog.HelperUnavailable(logger, processId, exception);
        }
    }
}

/// <summary>
/// Source generated log messages for <see cref="HelperProcessTerminator"/>.
/// </summary>
internal static partial class TerminatorLog
{
    [LoggerMessage(
        EventId = 5220,
        Level = LogLevel.Warning,
        Message = "The helper did not end OpenVPN process {ProcessId} ({Code}): {Reason}")]
    public static partial void Refused(ILogger logger, int processId, string code, string reason);

    [LoggerMessage(
        EventId = 5221,
        Level = LogLevel.Warning,
        Message = "The helper could not be asked to end OpenVPN process {ProcessId}.")]
    public static partial void HelperUnavailable(ILogger logger, int processId, Exception exception);
}
