using System.Diagnostics;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Waits, for a bounded time, for the copy of the application this one replaces to end.
/// </summary>
/// <remarks>
/// A restart starts the new copy before the old one has let go of the single instance claim, the
/// database and the tunnels it is tearing down. The old copy's teardown is bounded at a few seconds,
/// so the wait here is bounded too, generously: a copy that is still there afterwards is treated as
/// a running copy, and the new one hands over to it rather than waiting for ever.
///
/// Runs before anything else exists, so it has nothing to log to; the outcome decides what happens
/// next and that is visible enough.
/// </remarks>
internal static class PredecessorExit
{
    /// <summary>
    /// How long the copy being replaced is given to end.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits for a process to end, if it is a copy of this application.
    /// </summary>
    /// <param name="processId">The process being replaced.</param>
    /// <param name="expectedName">The process name a copy of this application has.</param>
    /// <param name="timeout">How long to wait at most.</param>
    /// <returns>True when that process is gone; false when it still runs after the time is up.</returns>
    /// <remarks>
    /// A process identifier is reused once its process has ended, and by the time this runs it may
    /// name something else entirely. Only a process with this application's name is waited for;
    /// anything else means the predecessor is already gone.
    /// </remarks>
    public static bool WaitFor(int processId, string expectedName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedName);

        Process process;

        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // No process has that identifier any more, which is the outcome being waited for.
            return true;
        }

        using (process)
        {
            try
            {
                if (!string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return process.WaitForExit(timeout);
            }
            catch (InvalidOperationException)
            {
                // It ended between being found and being asked its name.
                return true;
            }
        }
    }
}
