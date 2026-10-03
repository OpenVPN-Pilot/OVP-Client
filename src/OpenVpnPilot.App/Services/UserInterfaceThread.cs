using Avalonia.Threading;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Runs work on the thread that owns the windows.
/// </summary>
/// <remarks>
/// Services that change what the windows show from a background thread, such as the synchronisation
/// applying the server's settings or reloading the list, hand that work over through this rather than
/// reaching for the framework's dispatcher, so they stay testable without a running interface.
/// </remarks>
public interface IUserInterfaceThread
{
    /// <summary>
    /// Runs the work on the interface thread and completes when it has.
    /// </summary>
    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default);
}

/// <summary>
/// The interface thread of the running application.
/// </summary>
internal sealed class AvaloniaUserInterfaceThread : IUserInterfaceThread
{
    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (Dispatcher.UIThread.CheckAccess())
        {
            return work();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.UIThread.InvokeAsync(work, DispatcherPriority.Normal);
    }
}
