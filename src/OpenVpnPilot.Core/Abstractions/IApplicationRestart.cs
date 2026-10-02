namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Starts another copy of this application, the first half of a restart.
/// </summary>
/// <remarks>
/// Switching between the local library and a server changes the database every service was composed
/// against, and the only clean way to recompose is a new process. The new copy is started while this
/// one still runs and still holds the single instance claim, so it is told to wait for this process
/// to end before it claims the instance itself; ending this process is the caller's second half.
///
/// Platform dependent, because how an application is started properly differs: on Windows the
/// executable is started directly, on macOS an application bundle has to be opened through Launch
/// Services to be registered with the window server as itself.
/// </remarks>
public interface IApplicationRestart
{
    /// <summary>
    /// Starts another copy of this application with the given options.
    /// </summary>
    /// <param name="arguments">The options the new copy is started with, one per entry.</param>
    /// <returns>True when the new copy was started; false when it could not be, which is logged.</returns>
    public bool TryStartSuccessor(IReadOnlyList<string> arguments);
}
