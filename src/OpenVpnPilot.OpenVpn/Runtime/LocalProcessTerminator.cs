using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.OpenVpn.Runtime;

/// <summary>
/// Ends a process this client is itself allowed to end.
/// </summary>
/// <remarks>
/// This is a best effort backstop, not a guarantee of its own. On Windows the process was created
/// by the interactive service, so querying or terminating it can be refused with access denied. That
/// is reported and accepted rather than propagated.
///
/// Only a positive identifier names a single process. On Unix zero addresses the caller's own
/// process group and minus one every process the caller may signal, and looking either of them up
/// succeeds, so terminating what the lookup returned would end this application or everything its
/// user is running. Measured on macOS: a launcher reporting zero took the test host, the test runner
/// and the shell that started them down in one signal.
/// </remarks>
public sealed class LocalProcessTerminator : IOpenVpnProcessTerminator
{
    private readonly ILogger logger;

    public LocalProcessTerminator(ILogger? logger = null)
    {
        this.logger = logger ?? NullLogger.Instance;
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
            using Process process = Process.GetProcessById(processId);

            try
            {
                using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(grace);
                await process.WaitForExitAsync(wait.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                // The signal was ignored, so the process is ended below.
            }

            ConnectionSupervisorLog.ProcessDidNotExit(logger, processId);
            process.Kill(entireProcessTree: false);
        }
        catch (ArgumentException)
        {
            // Already gone, which is the normal outcome.
        }
        catch (InvalidOperationException)
        {
            // It exited while being inspected.
        }
        catch (Win32Exception exception)
        {
            // The service created the process, so this client may not be allowed to query or end it.
            ConnectionSupervisorLog.ProcessCheckDenied(logger, processId, exception);
        }
    }
}
