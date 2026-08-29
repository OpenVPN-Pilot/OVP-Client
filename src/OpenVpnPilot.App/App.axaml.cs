using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Localization;
using OpenVpnPilot.App.Services;
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
    /// True when the process was started by the autostart entry, which asks for no window.
    /// </summary>
    internal static bool StartInBackground { get; set; }

    private IHost? host;
    private WindowCoordinator? windows;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.MainWindow = window;

        StartServices(desktop, window, viewModel, settings);

        if (InstanceGuard is not null)
        {
            InstanceGuard.ActivationRequested += (_, _) =>
                Dispatcher.UIThread.Post(() => WindowCoordinator.Reveal(window));

            // The companion command drives the tunnels this copy owns rather than starting its own.
            RemoteCommandHandler remote = host.Services.GetRequiredService<RemoteCommandHandler>();
            InstanceGuard.CommandHandler = remote.HandleAsync;
        }

        // Closing the application must not leave tunnels running unattended.
        desktop.ShutdownRequested += OnShutdownRequested;

        base.OnFrameworkInitializationCompleted();
    }

    private void StartServices(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel viewModel,
        ISettingsService settings)
    {
        IServiceProvider services = host!.Services;

        services.GetRequiredService<SessionRecorder>().Attach();
        services.GetRequiredService<NotificationService>().Attach();

        services.GetRequiredService<PingMonitor>().Start();

        ReconnectSupervisor reconnects = services.GetRequiredService<ReconnectSupervisor>();
        reconnects.Attach();
        reconnects.Reported += (_, message) =>
            Dispatcher.UIThread.Post(() => viewModel.StatusMessage = message);

        TrayIconController tray = services.GetRequiredService<TrayIconController>();
        tray.Attach();
        tray.ShowWindowRequested += (_, _) => WindowCoordinator.Reveal(window);
        tray.MenuActionRequested += (_, action) => windows!.HandleTrayAction(action, desktop);

        services.GetRequiredService<NotificationService>().ProfileActivated += (_, profileId) =>
            Dispatcher.UIThread.Post(() =>
            {
                WindowCoordinator.Reveal(window);
                viewModel.SelectProfile(profileId);
            });

        // A window that starts hidden still has to load, because the tray menu and the shortcuts act
        // on the same view model.
        window.Opened += async (_, _) => await viewModel.LoadAsync();

        bool startHidden = StartInBackground || settings.Current.General.StartMinimised;

        if (startHidden)
        {
            // Loading is normally triggered by the window opening, which never happens here.
            Dispatcher.UIThread.Post(async () => await viewModel.LoadAsync());
        }
        else
        {
            window.Show();
        }

        Dispatcher.UIThread.Post(async () => await StartBackgroundWorkAsync());
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
            AppLog.AbandonedSessionsClosed(services.GetRequiredService<ILogger<App>>(), abandoned);
        }

        WatchedFolderMonitor watched = services.GetRequiredService<WatchedFolderMonitor>();

        watched.Imported += (_, imported) => Dispatcher.UIThread.Post(async () =>
        {
            MainWindowViewModel model = services.GetRequiredService<MainWindowViewModel>();
            await model.LoadAsync();
            model.StatusMessage = services.GetRequiredService<Core.Localization.ILocalizer>()
                .Translate("watch.imported", imported.Count, imported.Path);
        });

        await watched.StartAsync();

        HotkeyCoordinator hotkeys = services.GetRequiredService<HotkeyCoordinator>();
        hotkeys.ActionRequested += (_, action) => Dispatcher.UIThread.Post(async () =>
            await services.GetRequiredService<MainWindowViewModel>().ExecuteHotkeyActionAsync(action));

        await hotkeys.AttachAsync();
    }

    /// <summary>
    /// Clears configurations a previous run could not clean up, for example after a forced exit.
    /// </summary>
    private void RemoveStaleRuntimeFiles()
    {
        int removed = host!.Services.GetRequiredService<IProfileMaterializer>().RemoveStaleFiles();

        if (removed > 0)
        {
            AppLog.StaleRuntimeFilesRemoved(host.Services.GetRequiredService<ILogger<App>>(), removed);
        }
    }

    private void ApplyMigrations()
    {
        IDbContextFactory<PilotDbContext> factory =
            host!.Services.GetRequiredService<IDbContextFactory<PilotDbContext>>();

        using PilotDbContext context = factory.CreateDbContext();
        context.Database.Migrate();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (host is null)
        {
            return;
        }

        IServiceProvider services = host.Services;

        // Shutdown cannot await, so the tunnels are stopped before the process exits.
        RunOffUiThread(async () =>
        {
            SessionRecorder recorder = services.GetRequiredService<SessionRecorder>();
            await recorder.CloseOpenSessionsAsync(SessionEndReason.ApplicationClosed);
            await recorder.DisposeAsync();
        });

        RunOffUiThread(() => services.GetRequiredService<ConnectionManager>().DisconnectAllAsync());

        services.GetRequiredService<HotkeyCoordinator>().Dispose();
        services.GetRequiredService<ReconnectSupervisor>().Dispose();
        RunOffUiThread(async () => await services.GetRequiredService<PingMonitor>().DisposeAsync());
        RunOffUiThread(async () => await services.GetRequiredService<WatchedFolderMonitor>().DisposeAsync());
        services.GetRequiredService<TrayIconController>().Dispose();

        host.Dispose();
        host = null;
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
