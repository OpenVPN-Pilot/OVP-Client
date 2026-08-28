using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Data;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App;

public partial class App : Application
{
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

        // Closing the application must not leave tunnels running unattended.
        desktop.ShutdownRequested += OnShutdownRequested;

        base.OnFrameworkInitializationCompleted();
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
