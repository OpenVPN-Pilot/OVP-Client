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
    private bool attached;

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
        if (attached)
        {
            attached = false;
            notifier.Changed -= OnChanged;
        }
    }

    private async void OnChanged(object? sender, LibraryChangedEventArgs arguments)
    {
        try
        {
            await userInterface.InvokeAsync(() => ReloadAsync(arguments.Changes));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An event handler has nobody to hand a failure to. The list stays as it was until the
            // next change or the next start, which is worth a line in the log and not a crash.
            SyncEngineLog.RefreshFailed(logger, arguments.Changes, exception);
        }
    }

    private async Task ReloadAsync(LibraryChanges changes)
    {
        if (changes.HasFlag(LibraryChanges.Profiles))
        {
            await viewModel.LoadAsync();
        }

        if (changes.HasFlag(LibraryChanges.Hotkeys) && hotkeys.IsAttached)
        {
            await hotkeys.ReloadAsync();
        }
    }
}
