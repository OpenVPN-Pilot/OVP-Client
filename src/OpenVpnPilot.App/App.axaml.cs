using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Localization;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Library;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App;

public partial class App : Application
{
    /// <summary>
    /// Set before the framework starts, so the running copy can be brought forward when a second
    /// one is launched.
    /// </summary>
    internal static SingleInstanceGuard? InstanceGuard { get; set; }

    /// <summary>
    /// What the command line asked for. Set before the framework starts.
    /// </summary>
    internal static StartupOptions Startup { get; set; } = new();

    /// <summary>
    /// How long the whole teardown may take.
    /// </summary>
    /// <remarks>
    /// Windows gives an application a few seconds to end its session and kills whatever is still
    /// running, so the steps share one budget instead of each holding its own: four steps waiting
    /// two seconds apiece would take longer than Windows waits, and the tunnels the last of them
    /// stops would be the ones left running.
    /// </remarks>
    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The most one asynchronous step may take, so one that hangs still leaves time for the rest.
    /// </summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(2);

    private IHost? host;
    private WindowCoordinator? windows;
    private ApplicationMenuController? applicationMenu;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The platform reads the application's menu once, right after this and before anything else
        // of the application runs, and a menu set any later is never shown. It is set here empty and
        // filled once the services that word it exist. Where no platform shows such a menu this is
        // simply never read.
        NativeMenu.SetMenu(this, []);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        host = AppHost.Build();
        ApplyMigrations();
        RemoveStaleRuntimeFiles();

        // Settings and the language have to be in place before anything reads a label.
        ISettingsService settings = host.Services.GetRequiredService<ISettingsService>();
        RunOffUiThread(() => settings.LoadAsync());

        host.Services.GetRequiredService<LanguageCoordinator>().Attach();
        host.Services.GetRequiredService<LocalizationResourceBridge>().Attach(this);
        host.Services.GetRequiredService<AppearanceController>().Attach(this);

        MainWindowViewModel viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
        MainWindow window = new() { DataContext = viewModel };

        windows = new WindowCoordinator(host.Services, window, viewModel, desktop);
        windows.Attach();

        // Before anything that takes time: a file opened from the Finder that started this process
        // arrives as an activation right after launch, and one raised before a handler exists is lost.
        AttachActivation(window);
        AttachApplicationMenu(desktop);

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // The main window is handed to the lifetime only when it is meant to appear. The lifetime
        // shows whatever it is given once startup returns, so assigning it here and deciding later
        // not to show it is not a choice that gets respected: a copy asked to start with no window
        // still got one.

        StartServices(desktop, window, viewModel, settings);

        if (InstanceGuard is not null)
        {
            InstanceGuard.ActivationRequested += (_, _) =>
                Dispatcher.UIThread.Post(() => WindowCoordinator.Reveal(window));

            // The companion command drives the tunnels this copy owns rather than starting its own.
            RemoteCommandHandler remote = host.Services.GetRequiredService<RemoteCommandHandler>();
            remote.ShutdownRequested += (_, _) => desktop.Shutdown();

            // A configuration opened from the shell while a copy is already running arrives here,
            // because the second process hands its arguments over and exits.
            remote.ImportRequested += (_, path) =>
                Dispatcher.UIThread.Post(() => windows!.QueueImport([path]));

            InstanceGuard.CommandHandler = remote.HandleAsync;
        }

        // Closing the application must not leave tunnels running unattended, and Exit is the only
        // event that reports every way out. Measured against Avalonia 12.1: ending the Windows
        // session raises ShutdownRequested and then closes the windows, while the application
        // shutting itself down, which is what the tray and the companion command do, raises no
        // such request at all. Both raise Exit, once, after the last window has gone, which is also
        // the first moment at which nothing is left that could ask for a service this disposes.
        desktop.Exit += (_, _) => Teardown();

        base.OnFrameworkInitializationCompleted();
    }

    private void StartServices(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel viewModel,
        ISettingsService settings)
    {
        IServiceProvider services = host!.Services;

        // The log first, so what the other services do on their way up is recorded rather than
        // being the part of the startup nothing kept.
        services.GetRequiredService<LogSettingsApplier>().Attach();
        services.GetRequiredService<OpenVpnLogRelay>().Attach();

        ILogger<App> logger = services.GetRequiredService<ILogger<App>>();
        Version? version = typeof(App).Assembly.GetName().Version;

        AppLog.Started(logger, version, Environment.OSVersion.VersionString);

        services.GetRequiredService<SessionRecorder>().Attach();
        services.GetRequiredService<PingMonitor>().Start();

        // A headless copy has no notification area entry, and a balloon has nothing to hang off.
        if (!Startup.Headless)
        {
            services.GetRequiredService<NotificationService>().Attach();

            services.GetRequiredService<NotificationService>().ProfileActivated += (_, profileId) =>
                Dispatcher.UIThread.Post(() =>
                {
                    WindowCoordinator.Reveal(window);
                    viewModel.SelectProfile(profileId);
                });
        }

        ReconnectSupervisor reconnects = services.GetRequiredService<ReconnectSupervisor>();
        reconnects.Attach();
        reconnects.Reported += (_, message) =>
            Dispatcher.UIThread.Post(() => viewModel.StatusMessage = message);

        // A headless copy is driven by whatever launched it, so it offers nothing to click.
        if (!Startup.Headless)
        {
            TrayIconController tray = services.GetRequiredService<TrayIconController>();
            tray.Attach();
            tray.ShowWindowRequested += (_, _) => WindowCoordinator.Reveal(window);
            tray.MenuActionRequested += (_, action) => windows!.HandleTrayAction(action, desktop);
        }

        // A window that starts hidden still has to load, because the tray menu, the shortcuts and
        // the commands another process sends all act on the same view model.
        window.Opened += async (_, _) => await LoadAndAnnounceAsync(viewModel);

        bool startHidden = Startup.StartsHidden || settings.Current.General.StartMinimised;

        if (startHidden)
        {
            // Loading is normally triggered by the window opening, which never happens here.
            Dispatcher.UIThread.Post(async () => await LoadAndAnnounceAsync(viewModel));
        }
        else
        {
            desktop.MainWindow = window;
            window.Show();
        }

        Dispatcher.UIThread.Post(async () => await StartBackgroundWorkAsync());

        if (Startup.HasActions)
        {
            Dispatcher.UIThread.Post(async () => await RunStartupActionsAsync(viewModel));
        }
    }

    /// <summary>
    /// Handles what the platform asks of a running application other than through its windows.
    /// </summary>
    /// <remarks>
    /// On macOS a file opened from the Finder never reaches the command line: the system starts the
    /// application if it has to and then hands the file over as an event, to the copy that already
    /// runs as well. The same goes for a click on the Dock icon while every window is hidden, which
    /// is how a person asks for the window back. Windows passes files as arguments and raises
    /// neither, so there this changes nothing.
    /// </remarks>
    private void AttachActivation(MainWindow window)
    {
        if (TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime activatable)
        {
            return;
        }

        activatable.Activated += (_, activation) =>
        {
            switch (activation)
            {
                case FileActivatedEventArgs opened:
                    List<string> paths = [.. opened.Files
                        .Select(file => file.TryGetLocalPath())
                        .OfType<string>()];

                    if (paths.Count > 0)
                    {
                        Dispatcher.UIThread.Post(() => windows!.QueueImport(paths));
                    }

                    break;

                case { Kind: ActivationKind.Reopen }:
                    Dispatcher.UIThread.Post(() => WindowCoordinator.Reveal(window));
                    break;

                default:
                    break;
            }
        };
    }

    /// <summary>
    /// Fills the application menu, on a platform whose menu bar shows one.
    /// </summary>
    private void AttachApplicationMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (host!.Services.GetService<IApplicationMenu>() is null)
        {
            return;
        }

        applicationMenu = ActivatorUtilities.CreateInstance<ApplicationMenuController>(host.Services);
        applicationMenu.ActionRequested += (_, action) => windows!.HandleTrayAction(action, desktop);
        applicationMenu.Attach(this);
    }

    /// <summary>
    /// Reads the profile list, then reports that commands naming a profile can be answered.
    /// </summary>
    /// <remarks>
    /// Whatever started this process may be waiting to tell it what to connect, and a name cannot
    /// be matched against a list that has not been read yet.
    /// </remarks>
    private async Task LoadAndAnnounceAsync(MainWindowViewModel viewModel)
    {
        await viewModel.LoadAsync();

        RemoteCommandHandler handler = host!.Services.GetRequiredService<RemoteCommandHandler>();
        handler.HasWindows = !Startup.Headless;
        handler.IsReady = true;

        // After the list, not before it. The window should appear at once; whether OpenVPN is
        // installed is worth knowing a moment later and is not worth waiting for a pipe to answer.
        await viewModel.RefreshEnvironmentAsync();

        // Last, because it leaves the machine. It reports only a release that is actually newer, so
        // the ordinary outcome of this line is that nothing at all happens.
        await viewModel.CheckForUpdatesAsync();
    }

    /// <summary>
    /// Carries out what the command line asked for, once the profile list has been read.
    /// </summary>
    /// <remarks>
    /// Loading is posted to the same queue just above, so this arrives after it. The actions go
    /// through the same handler the companion command uses, so a profile named on the command line
    /// is matched exactly the way one named in a terminal is.
    /// </remarks>
    private async Task RunStartupActionsAsync(MainWindowViewModel viewModel)
    {
        RemoteCommandHandler handler = host!.Services.GetRequiredService<RemoteCommandHandler>();
        ILogger<App> logger = host.Services.GetRequiredService<ILogger<App>>();

        foreach (string command in Startup.ToCommands())
        {
            string reply = await handler.HandleAsync(command);
            viewModel.StatusMessage = reply;
            AppLog.StartupActionRan(logger, command, reply);
        }
    }

    /// <summary>
    /// Work that needs the interface to exist but must not delay it appearing.
    /// </summary>
    private async Task StartBackgroundWorkAsync()
    {
        IServiceProvider services = host!.Services;

        // A session left open by a forced exit would otherwise be shown as still running.
        int abandoned = await services.GetRequiredService<ISessionStore>().CloseAbandonedAsync();

        if (abandoned > 0)
        {
            ILogger<App> logger = services.GetRequiredService<ILogger<App>>();
            AppLog.AbandonedSessionsClosed(logger, abandoned);
        }

        StartSharedLibrary(services);

        // A shortcut that opens a window is not something a headless copy should own, and the
        // copy that a person is using may be the one that wants them.
        if (Startup.Headless)
        {
            return;
        }

        HotkeyCoordinator hotkeys = services.GetRequiredService<HotkeyCoordinator>();
        hotkeys.ActionRequested += (_, action) => Dispatcher.UIThread.Post(async () =>
            await services.GetRequiredService<MainWindowViewModel>().ExecuteHotkeyActionAsync(action));

        await hotkeys.AttachAsync();
    }

    /// <summary>
    /// Keeps the library in step with its shared file, when it has one, and tells the window what that did.
    /// </summary>
    /// <remarks>
    /// A headless copy reconciles as well. It is the one a script drives, and a profile a colleague
    /// changed is one it should connect with the change.
    /// </remarks>
    private static void StartSharedLibrary(IServiceProvider services)
    {
        SharedLibrarySync library = services.GetRequiredService<SharedLibrarySync>();
        MainWindowViewModel model = services.GetRequiredService<MainWindowViewModel>();

        foreach (SharedLibraryActivity entry in library.RecentActivity)
        {
            model.AddLibraryActivity(entry);
        }

        library.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => model.ShowLibraryStatus(status));
        library.ActivityRecorded += (_, entry) => Dispatcher.UIThread.Post(() => model.AddLibraryActivity(entry));

        library.Reconciled += (_, report) => Dispatcher.UIThread.Post(async () =>
        {
            if (report.ChangedHere)
            {
                await model.LoadAsync();
            }

            model.ShowLibraryReport(report);
        });

        model.LibraryRetryRequested += async (_, _) => await library.SyncNowAsync();
        model.LibraryDeletionConfirmed += async (_, _) => await library.ConfirmDeletionsAsync();
        model.LibraryRestoreRequested += async (_, _) =>
        {
            try
            {
                await library.RestoreHeldDeletionsAsync();
                await model.LoadAsync();
            }
            catch (Exception exception) when (SharedLibraryText.Refusal(exception, services.GetRequiredService<Core.Localization.ILocalizer>()) is { } refusal)
            {
                // The file could not be read again to restore from. Said where the question was asked.
                model.StatusMessage = refusal;
            }
        };

        library.Start();
    }

    /// <summary>
    /// Clears configurations a previous run could not clean up, for example after a forced exit.
    /// </summary>
    private void RemoveStaleRuntimeFiles()
    {
        int removed = host!.Services.GetRequiredService<IProfileMaterializer>().RemoveStaleFiles();

        if (removed > 0)
        {
            ILogger<App> logger = host.Services.GetRequiredService<ILogger<App>>();
            AppLog.StaleRuntimeFilesRemoved(logger, removed);
        }
    }

    private void ApplyMigrations()
    {
        IDbContextFactory<PilotDbContext> factory =
            host!.Services.GetRequiredService<IDbContextFactory<PilotDbContext>>();

        using PilotDbContext context = factory.CreateDbContext();
        context.Database.Migrate();
    }

    /// <summary>
    /// Stops everything the application started, once, on the way out.
    /// </summary>
    /// <remarks>
    /// Windows ends a session by giving each application a few seconds and killing whatever is left,
    /// so no step may wait without a deadline. A step that fails must not cost the ones after it
    /// either: an exception escaping here would leave the tunnels up and end the process with a
    /// fault dialog on the shutdown screen rather than a shutdown.
    /// </remarks>
    private void Teardown()
    {
        if (host is null)
        {
            return;
        }

        IServiceProvider services = host.Services;
        ILogger<App> logger = services.GetRequiredService<ILogger<App>>();
        long started = Stopwatch.GetTimestamp();

        RunStep(logger, started, "sessions", async () =>
        {
            SessionRecorder recorder = services.GetRequiredService<SessionRecorder>();
            await recorder.CloseOpenSessionsAsync(SessionEndReason.ApplicationClosed);
            await recorder.DisposeAsync();
        });

        RunStep(logger, started, "connections", () => services.GetRequiredService<ConnectionManager>().DisconnectAllAsync());

        // After the tunnels, which matter more on the way out. Whatever does not make it into the
        // shared file in time is recorded as waiting and reconciled first on the next start.
        RunStep(logger, started, "shared library", async () =>
        {
            SharedLibrarySync library = services.GetRequiredService<SharedLibrarySync>();
            await library.FlushAsync();
            await library.DisposeAsync();
        });
        RunStep(logger, "hotkeys", () => services.GetRequiredService<HotkeyCoordinator>().Dispose());
        RunStep(logger, "reconnects", () => services.GetRequiredService<ReconnectSupervisor>().Dispose());
        RunStep(logger, started, "ping", async () => await services.GetRequiredService<PingMonitor>().DisposeAsync());
        RunStep(logger, "tray icon", () => services.GetRequiredService<TrayIconController>().Dispose());
        RunStep(logger, "application menu", () => applicationMenu?.Dispose());
        RunStep(logger, "log relay", () => services.GetRequiredService<OpenVpnLogRelay>().Dispose());
        RunStep(logger, "log settings", () => services.GetRequiredService<LogSettingsApplier>().Dispose());

        long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        AppLog.ShutdownCompleted(logger, elapsed);

        // After the last line worth writing, and before the container that owns the file handle goes.
        RunStep(logger, started, "log", async () => await services.GetRequiredService<LogHub>().DisposeAsync());

        IHost stopping = host;
        host = null;

        RunStep(logger, "host", stopping.Dispose);
    }

    /// <summary>
    /// Runs one step of the teardown on this thread, reporting rather than raising a failure.
    /// </summary>
    /// <remarks>
    /// A synchronous step is not handed to the thread pool: the message window that carries the
    /// notification area icon and the global shortcuts can only be destroyed from the thread that
    /// created it, and destroying it from another one fails rather than waits. It is therefore run
    /// here, without a deadline, which none of these steps needs.
    /// </remarks>
    private static void RunStep(ILogger logger, string step, Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            AppLog.ShutdownStepFailed(logger, step, exception);
        }
    }

    /// <summary>
    /// Runs one asynchronous step of the teardown, within what is left of the budget, reporting
    /// rather than raising a failure.
    /// </summary>
    /// <remarks>
    /// A step whose turn comes with nothing left is still started and simply not waited for. It is
    /// about to be killed with the process either way, and a tunnel that stops on the way out is
    /// worth more than one that was never asked to.
    /// </remarks>
    private static void RunStep(ILogger logger, long started, string step, Func<Task> work)
    {
        TimeSpan left = TeardownBudget - Stopwatch.GetElapsedTime(started);
        TimeSpan wait = left < StepTimeout ? left : StepTimeout;

        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        try
        {
            if (!Task.Run(work).Wait(wait))
            {
                AppLog.ShutdownStepTimedOut(logger, step, wait.TotalSeconds);
            }
        }
        catch (Exception exception)
        {
            // Every step is someone else's resource being released, so the failures are theirs and
            // cannot be enumerated here. Reporting one and carrying on is what keeps the remaining
            // tunnels from being left running by the first thing that goes wrong.
            AppLog.ShutdownStepFailed(logger, step, exception);
        }
    }

    /// <summary>
    /// Runs asynchronous work to completion from a place that cannot await.
    /// </summary>
    /// <remarks>
    /// Startup and shutdown are synchronous callbacks on the user interface thread, and that thread
    /// carries a synchronisation context. Awaiting inside the work would post its continuation back
    /// to a thread that is blocked waiting for that same work, which deadlocks. Running it on the
    /// thread pool gives the continuations somewhere to go.
    /// </remarks>
    private static void RunOffUiThread(Func<Task> work) =>
        Task.Run(work).GetAwaiter().GetResult();
}
