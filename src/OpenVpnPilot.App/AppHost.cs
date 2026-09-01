using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Localization;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;
using OpenVpnPilot.OpenVpn.Configuration;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Platform.Windows.Diagnostics;
using OpenVpnPilot.Platform.Windows.InteractiveService;
using OpenVpnPilot.Platform.Windows.Runtime;
using OpenVpnPilot.Platform.Windows.Security;
using OpenVpnPilot.Platform.Windows.Shell;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OpenVpnPilot.App;

/// <summary>
/// Composition root. Everything the application uses is registered here and nowhere else.
/// </summary>
internal static class AppHost
{
    public static IHost Build()
    {
        UserApplicationPaths paths = new();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Built before the logger, because the logger writes into it. Both are handed to the
        // container afterwards so that everything else reaches them the ordinary way.
        LogHub hub = new(paths.LogDirectory);
        LoggingLevelSwitch levelSwitch = new(LogEventLevel.Information);

        ConfigureLogging(builder, paths, hub, levelSwitch);

        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton(levelSwitch);
        builder.Services.AddSingleton<LogSettingsApplier>();
        builder.Services.AddSingleton<OpenVpnLogRelay>();

        builder.Services.AddSingleton<IApplicationPaths>(paths);
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddDbContextFactory<PilotDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath}"));

        builder.Services.AddSingleton<IProfileStore, ProfileStore>();
        builder.Services.AddSingleton<ISessionStore, SessionStore>();
        builder.Services.AddSingleton<IHotkeyStore, HotkeyStore>();
        builder.Services.AddSingleton<IProfileImportService, ProfileImportService>();
        builder.Services.AddSingleton<IProfilePackageWriter, ProfilePackageWriter>();
        builder.Services.AddSingleton<IWatchedFolderStore, WatchedFolderStore>();
        builder.Services.AddSingleton<WatchedFolderMonitor>();
        builder.Services.AddSingleton<DiagnosticsBundle>();
        builder.Services.AddSingleton<EnvironmentGate>();

        RegisterSettings(builder.Services, paths);
        RegisterLocalization(builder.Services, paths);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "This build supports Windows only. Support for other systems means adding an "
                + "implementation of the platform interfaces, not changing the rest of the application.");
        }

        RegisterPlatformServices(builder.Services, paths);

        builder.Services.AddSingleton<IOvpnFileResolver, FileSystemOvpnFileResolver>();
        builder.Services.AddSingleton<OvpnConfigInliner>();
        builder.Services.AddSingleton<IManagementChannelFactory, TcpManagementChannelFactory>();
        builder.Services.AddSingleton<IPortAllocator, LoopbackPortAllocator>();
        builder.Services.AddSingleton<ConnectionManager>();

        // The name cache sits between the view model and the credential prompt. Pointing the
        // prompt straight at the view model would close a dependency cycle through the connection
        // manager, which the container rejects at resolution time.
        builder.Services.AddSingleton<ProfileNameCache>();
        builder.Services.AddSingleton<IProfileNameLookup>(
            provider => provider.GetRequiredService<ProfileNameCache>());
        builder.Services.AddSingleton<ICredentialProvider, StoredCredentialProvider>();

        builder.Services.AddSingleton<SessionRecorder>();
        builder.Services.AddSingleton<NotificationService>();
        builder.Services.AddSingleton<ReconnectSupervisor>();
        builder.Services.AddSingleton<PingMonitor>();
        builder.Services.AddSingleton<RemoteCommandHandler>();
        builder.Services.AddSingleton<AppearanceController>();
        builder.Services.AddSingleton<HotkeyCoordinator>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<QuickSwitcherViewModel>();
        builder.Services.AddSingleton<TrayIconController>();

        // One window at a time, but a fresh view model each time it opens, so a screen that was
        // closed without saving does not reopen with the abandoned edits still in it.
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<HistoryViewModel>();
        builder.Services.AddTransient<LogViewModel>();
        builder.Services.AddTransient<ImportViewModel>();
        builder.Services.AddTransient<ExportViewModel>();

        return builder.Build();
    }

    private static void RegisterSettings(IServiceCollection services, UserApplicationPaths paths)
    {
        services.AddSingleton<JsonSettingsService>(provider => new JsonSettingsService(
            paths.SettingsPath,
            provider.GetRequiredService<ILogger<JsonSettingsService>>()));

        services.AddSingleton<ISettingsService>(
            provider => provider.GetRequiredService<JsonSettingsService>());
    }

    private static void RegisterLocalization(IServiceCollection services, UserApplicationPaths paths)
    {
        // The installed directory comes first so a file the user drops in overrides it key by key.
        services.AddSingleton<ILanguageCatalogueSource>(provider => new JsonLanguageCatalogueSource(
            [paths.InstalledLanguageDirectory, paths.UserLanguageDirectory],
            provider.GetRequiredService<ILogger<JsonLanguageCatalogueSource>>()));

        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ILocalizer>(provider => provider.GetRequiredService<LocalizationManager>());
        services.AddSingleton<LanguageCoordinator>();
        services.AddSingleton<LocalizationResourceBridge>();
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterPlatformServices(IServiceCollection services, UserApplicationPaths paths)
    {
        services.AddSingleton<InteractiveServicePipeClient>();
        services.AddSingleton<IOpenVpnLauncher, WindowsOpenVpnLauncher>();
        services.AddSingleton<IProfileMaterializer, WindowsProfileMaterializer>();
        services.AddSingleton<IOpenVpnEnvironmentProbe, WindowsOpenVpnEnvironmentProbe>();
        services.AddSingleton<ISecretStore>(_ => new DpapiSecretStore(paths.SecretsDirectory));
        services.AddSingleton<IAutoStartManager, RegistryAutoStartManager>();
        services.AddSingleton<IGlobalHotkeyService, WindowsGlobalHotkeyService>();

        // The icon and the notifications are one entry in the notification area, so they are one
        // object registered under both interfaces rather than two that would each add an icon.
        services.AddSingleton<WindowsTrayIcon>();
        services.AddSingleton<ISystemTrayIcon>(provider => provider.GetRequiredService<WindowsTrayIcon>());
        services.AddSingleton<INotificationPresenter>(
            provider => provider.GetRequiredService<WindowsTrayIcon>());
    }

    /// <summary>
    /// Sets up the log, which is one stream, one file per day and one level switch.
    /// </summary>
    /// <remarks>
    /// The level is a switch rather than a fixed minimum because the setting that chooses it is read
    /// from a file that is loaded after the container is built. Fixing it here is what made that
    /// setting decorative: it was stored, shown in the settings screen, and never applied.
    ///
    /// Entity Framework logs every command it executes at information level. In a client that reads
    /// its profile list on every change that is nine tenths of the log by volume, all of it SQL, and
    /// it buried the handful of lines that say what the application actually did. It is raised to
    /// warning, where a failing query still appears and a successful one does not.
    /// </remarks>
    private static void ConfigureLogging(
        HostApplicationBuilder builder,
        UserApplicationPaths paths,
        LogHub hub,
        LoggingLevelSwitch levelSwitch)
    {
        Serilog.Core.Logger logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .WriteTo.Sink(new LogHubSink(hub))
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: true);
    }

    /// <summary>
    /// Turns the stored level name into the switch value, defaulting rather than failing.
    /// </summary>
    public static LogEventLevel ParseLevel(string? name) => name switch
    {
        "Verbose" => LogEventLevel.Verbose,
        "Debug" => LogEventLevel.Debug,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        "Fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
