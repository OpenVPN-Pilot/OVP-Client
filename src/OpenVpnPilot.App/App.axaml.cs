using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Data;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App;

public partial class App : Application
{
    /// <summary>
    /// Set before the framework starts, so the running copy can be brought forward when a second
    /// one is launched.
    /// </summary>
    internal static SingleInstanceGuard? InstanceGuard { get; set; }

    private IHost? host;

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

        MainWindowViewModel viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
        MainWindow window = new() { DataContext = viewModel };

        window.Opened += async (_, _) => await viewModel.LoadAsync();

        // Closing the window keeps the tunnels running; the tray icon is the way back in.
        window.Closing += (_, args) =>
        {
            if (desktop.ShutdownMode == ShutdownMode.OnExplicitShutdown)
            {
                args.Cancel = true;
                window.Hide();
            }
        };

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.MainWindow = window;

        host.Services.GetRequiredService<TrayIconController>().Attach(this, desktop);

        if (InstanceGuard is not null)
        {
            InstanceGuard.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            });
        }

        // Closing the application must not leave tunnels running unattended.
        desktop.ShutdownRequested += OnShutdownRequested;

        base.OnFrameworkInitializationCompleted();
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

        ConnectionManager connections = host.Services.GetRequiredService<ConnectionManager>();

        // Shutdown cannot await, so the tunnels are stopped before the process exits.
        connections.DisconnectAllAsync().GetAwaiter().GetResult();

        host.Dispose();
        host = null;
    }
}
