using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Reloads what the windows show when the database changed without a screen changing it.
/// </summary>
/// <remarks>
/// The list is what the quick switcher fills itself from when it opens, and the favourite shortcuts
/// look their slot up when pressed, so reloading the list covers both. The bindings themselves have
/// to be claimed again, but only where they were claimed at all: a headless copy never claims them.
/// </remarks>
public sealed class LibraryRefresh : IDisposable
{
    private readonly ILibraryChangeNotifier notifier;
    private readonly MainWindowViewModel viewModel;
    private readonly HotkeyCoordinator hotkeys;
    private readonly IUserInterfaceThread userInterface;
    private readonly ILogger<LibraryRefresh> logger;

    // Cancelled when the windows are torn down, so a reload under way does not outlive them.
    private readonly CancellationTokenSource lifetime = new();

    // Taken once, because a change can still be announced after the source was disposed; it is
    // cancelled by then, which is all a reload needs to know.
    private readonly CancellationToken stopping;
    private bool attached;
    private bool disposed;

    public LibraryRefresh(
        ILibraryChangeNotifier notifier,
        MainWindowViewModel viewModel,
        HotkeyCoordinator hotkeys,
        IUserInterfaceThread userInterface,
        ILogger<LibraryRefresh> logger)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(userInterface);
        ArgumentNullException.ThrowIfNull(logger);

        this.notifier = notifier;
        this.viewModel = viewModel;
        this.hotkeys = hotkeys;
        this.userInterface = userInterface;
        this.logger = logger;
        stopping = lifetime.Token;
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        attached = true;
        notifier.Changed += OnChanged;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (attached)
        {
            attached = false;
            notifier.Changed -= OnChanged;
        }

        lifetime.Cancel();
        lifetime.Dispose();
    }

    private async void OnChanged(object? sender, LibraryChangedEventArgs arguments)
    {
        CancellationToken cancellationToken = stopping;

        try
        {
            await userInterface.InvokeAsync(() => ReloadAsync(arguments.Changes, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The windows are being torn down, so there is nothing left to show the change in.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An event handler has nobody to hand a failure to. The list stays as it was until the
            // next change or the next start, which is worth a line in the log and not a crash.
            SyncEngineLog.RefreshFailed(logger, arguments.Changes, exception);
        }
    }

    private async Task ReloadAsync(LibraryChanges changes, CancellationToken cancellationToken)
    {
        if (changes.HasFlag(LibraryChanges.Profiles))
        {
            await viewModel.LoadAsync(cancellationToken);
        }

        if (changes.HasFlag(LibraryChanges.Hotkeys) && hotkeys.IsAttached)
        {
            await hotkeys.ReloadAsync(cancellationToken);
        }
    }
}
