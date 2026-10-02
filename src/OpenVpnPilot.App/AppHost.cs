using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Localization;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;
using OpenVpnPilot.OpenVpn.Configuration;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Platform.MacOS.Diagnostics;
using OpenVpnPilot.Platform.MacOS.Helper;
using OpenVpnPilot.Platform.MacOS.Runtime;
using OpenVpnPilot.Platform.MacOS.Security;
using OpenVpnPilot.Platform.MacOS.Shell;
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
    /// <summary>
    /// Where a person is sent to get the macOS helper package.
    /// </summary>
    /// <remarks>
    /// The macOS page rather than the releases, because there is nothing to download there. A macOS
    /// build cannot be published without an Apple Developer ID to sign it with and a Mac to make it
    /// on, and this project has neither, so both halves are built from the source. That page is
    /// where the one command to do it is written down; the releases would be a page with nothing on
    /// it for the reader.
    /// </remarks>
    private static readonly string HelperSetupUrl =
        $"https://github.com/{new AdvancedSettings().UpdateRepository}/blob/master/docs/macos.md";

    public static IHost Build()
    {
        UserApplicationPaths paths = new();

        // Before anything is composed, because every store is composed against the one file this
        // decides. Switching the mode restarts the process, so it is decided exactly once.
        ActiveStorage storage = ActiveStorage.Resolve(paths, StorageModeReader.Read(paths.SettingsPath));

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
        builder.Services.AddSingleton<IActiveStorage>(storage);
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddDbContextFactory<PilotDbContext>(options =>
            options.UseSqlite($"Data Source={storage.DatabasePath}"));

        builder.Services.AddSingleton<IStorageModeContext>(storage);
        builder.Services.AddSingleton<IOutbox, Outbox>();
        builder.Services.AddSingleton<IChangeRecorder, ChangeRecorder>();
        builder.Services.AddSingleton<IServerProfileMaintenance, ServerProfileMaintenance>();
        builder.Services.AddSingleton<IUserInterfaceThread, AvaloniaUserInterfaceThread>();
        RegisterSynchronisation(builder.Services, storage);

        builder.Services.AddSingleton<IProfileStore, ProfileStore>();
        builder.Services.AddSingleton<ISessionStore, SessionStore>();
        builder.Services.AddSingleton<IHotkeyStore, HotkeyStore>();
        builder.Services.AddSingleton<IProfileImportService, ProfileImportService>();
        builder.Services.AddSingleton<IProfilePackageWriter, ProfilePackageWriter>();
        builder.Services.AddSingleton<DiagnosticsBundle>();
        builder.Services.AddSingleton<EnvironmentGate>();
        builder.Services.AddSingleton<UpdateCoordinator>();

        RegisterSettings(builder.Services, paths);
        RegisterLocalization(builder.Services, paths);
        RegisterServer(builder.Services, storage);

        if (OperatingSystem.IsWindows())
        {
            RegisterWindowsServices(builder.Services, paths);
        }
        else if (OperatingSystem.IsMacOS())
        {
            RegisterMacServices(builder.Services, paths);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "This build supports Windows and macOS. Support for another system means adding an "
                + "implementation of the platform interfaces, not changing the rest of the application.");
        }

        builder.Services.AddSingleton<IOvpnFileResolver, FileSystemOvpnFileResolver>();
        builder.Services.AddSingleton<OvpnConfigInliner>();
        builder.Services.AddSingleton<IManagementChannelFactory, TcpManagementChannelFactory>();
        builder.Services.AddSingleton<IPortAllocator, LoopbackPortAllocator>();
        builder.Services.AddSingleton<ConnectionManager>();
        builder.Services.AddSingleton<IActiveTunnels, ConnectionManagerTunnels>();
        builder.Services.AddSingleton<IStorageModeSwitcher, StorageModeSwitcher>();

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

        // Everything saves through the recording service, so a change to what follows the person
        // to a server is noted for it. On the local library the recorder notes nothing.
        services.AddSingleton(provider => new RecordingSettingsService(
            provider.GetRequiredService<JsonSettingsService>(),
            provider.GetRequiredService<IChangeRecorder>(),
            provider.GetRequiredService<IUserInterfaceThread>()));

        services.AddSingleton<ISettingsService>(provider => provider.GetRequiredService<RecordingSettingsService>());
        services.AddSingleton<IPortableSettings>(provider => provider.GetRequiredService<RecordingSettingsService>());
    }

    /// <summary>
    /// The synchronisation, which exists only while the application runs against a server.
    /// </summary>
    /// <remarks>
    /// It is registered here and never resolved on the local library, so the local mode does not
    /// need a server connection to start. The connection itself is registered by the sign in side,
    /// and whoever signs in starts the engine; nothing starts it from here.
    /// </remarks>
    private static void RegisterSynchronisation(IServiceCollection services, ActiveStorage storage)
    {
        services.AddSingleton<ILibraryChangeNotifier, LibraryChangeNotifier>();
        services.AddSingleton<LibraryRefresh>();

        if (storage.IsServerMode)
        {
            services.AddSingleton<INetworkAvailability, SystemNetworkAvailability>();
            services.AddSingleton<ISyncEngine, SyncEngine>();
        }
    }

    /// <summary>
    /// What talks to a server, and in Server mode the connection to the one this copy works with.
    /// </summary>
    /// <remarks>
    /// The factories exist in both modes, because a server is checked and signed in to from the local
    /// library before switching to it: the first start does, and so does choosing a server later.
    /// They contact nothing until they are used. The connection itself, the session, the
    /// coordinator and the wipe exist only in Server mode, where there is exactly one server for the
    /// life of the process; the local library composes nothing that could reach a server by itself.
    /// </remarks>
    private static void RegisterServer(IServiceCollection services, ActiveStorage storage)
    {
        services.AddSingleton<IClientVersionProvider>(_ => new AssemblyClientVersionProvider(typeof(AppHost).Assembly));
        services.AddSingleton<IInstallationIdProvider, SettingsInstallationId>();
        services.AddSingleton<IServerHttpClientFactory>(provider => new ServerHttpClientFactory(
            provider.GetRequiredService<IClientVersionProvider>(),
            provider.GetRequiredService<IInstallationIdProvider>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IServerConnectionFactory, ServerConnectionFactory>();
        services.AddSingleton<IEntraSignIn, MsalEntraSignIn>();

        if (storage is not { IsServerMode: true, ServerAddress: { } address, ServerKey: { } key })
        {
            return;
        }

        services.AddSingleton(provider =>
            provider.GetRequiredService<IServerConnectionFactory>().Create(new Uri(address), key));
        services.AddSingleton(provider => provider.GetRequiredService<IServerConnection>().Session);
        services.AddSingleton(provider => provider.GetRequiredService<IServerConnection>().Api);
        services.AddSingleton(provider => provider.GetRequiredService<IServerConnection>().Wipe);

        services.AddSingleton<IServerAccountState, ServerAccountState>();
        services.AddSingleton<IServerWipe, ServerWipe>();
        services.AddSingleton<IAccountRevokedNotice>(provider => new WindowAccountRevokedNotice(
            provider.GetRequiredService<ILocalizer>(),
            showsWindows: !App.Startup.Headless,
            provider.GetService<IApplicationActivation>()));

        // One coordinator, which is also the sign in everything in this mode uses, so that every
        // sign in passes its rule for a different person.
        services.AddSingleton<ServerSessionCoordinator>();
        services.AddSingleton<IServerSessionCoordinator>(provider => provider.GetRequiredService<ServerSessionCoordinator>());
        services.AddSingleton<IServerSignIn>(provider => provider.GetRequiredService<ServerSessionCoordinator>());
    }

    private static void RegisterLocalization(IServiceCollection services, UserApplicationPaths paths)
    {
        // Wording that names a part of one system, a key or a component, is chosen by platform.
        string? platform = OperatingSystem.IsMacOS() ? "macos" : null;

        // The installed directory comes first so a file the user drops in overrides it key by key.
        services.AddSingleton<ILanguageCatalogueSource>(provider => new JsonLanguageCatalogueSource(
            [paths.InstalledLanguageDirectory, paths.UserLanguageDirectory],
            provider.GetRequiredService<ILogger<JsonLanguageCatalogueSource>>(),
            platform));

        services.AddSingleton<LocalizationManager>();
        services.AddSingleton<ILocalizer>(provider => provider.GetRequiredService<LocalizationManager>());
        services.AddSingleton<LanguageCoordinator>();
        services.AddSingleton<LocalizationResourceBridge>();
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindowsServices(IServiceCollection services, UserApplicationPaths paths)
    {
        services.AddSingleton<InteractiveServicePipeClient>();
        services.AddSingleton<IOpenVpnLauncher, WindowsOpenVpnLauncher>();
        services.AddSingleton<IProfileMaterializer, WindowsProfileMaterializer>();
        services.AddSingleton<IOpenVpnEnvironmentProbe, WindowsOpenVpnEnvironmentProbe>();
        services.AddSingleton<ISecretStore>(_ => new DpapiSecretStore(paths.SecretsDirectory));
        services.AddSingleton<IAutoStartManager, RegistryAutoStartManager>();
        services.AddSingleton<IGlobalHotkeyService, WindowsGlobalHotkeyService>();
        services.AddSingleton<IWindowCloseOrigin, WindowsCloseOrigin>();
        services.AddSingleton<IApplicationRestart, WindowsApplicationRestart>();

        // The icon and the notifications are one entry in the notification area, so they are one
        // object registered under both interfaces rather than two that would each add an icon.
        services.AddSingleton<WindowsTrayIcon>();
        services.AddSingleton<ISystemTrayIcon>(provider => provider.GetRequiredService<WindowsTrayIcon>());
        services.AddSingleton<INotificationPresenter>(
            provider => provider.GetRequiredService<WindowsTrayIcon>());
    }

    /// <summary>
    /// The macOS implementations of the platform interfaces.
    /// </summary>
    /// <remarks>
    /// One helper session serves the launcher and the terminator, because the helper ties every
    /// tunnel to the session that started it and ends them together when it closes. The menu bar
    /// entry and the notifications are separate objects here: a notification is not attached to the
    /// status item the way a balloon is attached to a notification area icon.
    /// </remarks>
    [SupportedOSPlatform("macos")]
    private static void RegisterMacServices(IServiceCollection services, UserApplicationPaths paths)
    {
        services.AddSingleton<HelperSession>();
        services.AddSingleton<IOpenVpnLauncher, MacOpenVpnLauncher>();
        services.AddSingleton<IOpenVpnProcessTerminator, HelperProcessTerminator>();
        services.AddSingleton<IProfileMaterializer>(
            _ => new MacProfileMaterializer(Path.Combine(paths.DataDirectory, "runtime")));
        services.AddSingleton<IOpenVpnEnvironmentProbe>(_ => new MacOpenVpnEnvironmentProbe(HelperSetupUrl));
        services.AddSingleton<ISecretStore, KeychainSecretStore>();
        services.AddSingleton<IAutoStartManager, LaunchAgentAutoStartManager>();
        services.AddSingleton<IGlobalHotkeyService, MacGlobalHotkeyService>();
        services.AddSingleton<ISystemTrayIcon, MacStatusItem>();
        services.AddSingleton<IDockPresence, MacDockPresence>();
        services.AddSingleton<IApplicationActivation, MacApplicationActivation>();
        services.AddSingleton<IApplicationRestart, MacApplicationRestart>();

        // Without this the application menu was never filled and kept the framework's entry about
        // itself, although everything that fills it existed.
        services.AddSingleton<IApplicationMenu, MacApplicationMenu>();
        services.AddSingleton<INotificationPresenter, MacNotificationPresenter>();
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
