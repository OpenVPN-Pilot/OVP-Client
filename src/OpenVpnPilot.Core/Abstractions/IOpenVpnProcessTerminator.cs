namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Makes sure an OpenVPN process that was asked to stop has stopped, and ends it when it has not.
/// </summary>
/// <remarks>
/// Stopping a tunnel is a request sent over the management interface, and a request can go
/// unanswered: with auth-retry set to interact, a process whose credentials were refused keeps
/// waiting for new ones. Something has to end it then, and who may do that depends on the platform.
/// On Windows this client may end the process the interactive service created. On macOS OpenVPN
/// runs as root, the client is not allowed to signal it, and only the helper that started it can
/// end it.
/// </remarks>
public interface IOpenVpnProcessTerminator
{
    /// <summary>
    /// Waits for the process to exit and ends it when it has not done so within the grace period.
    /// </summary>
    /// <remarks>
    /// Reports rather than raises. A tunnel that outlives a disconnect is a fault worth logging, but
    /// it must never take the application down with it.
    /// </remarks>
    public Task EnsureExitedAsync(int processId, TimeSpan grace, CancellationToken cancellationToken = default);
}
