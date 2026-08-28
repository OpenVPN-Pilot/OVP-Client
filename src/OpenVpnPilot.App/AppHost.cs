using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.OpenVpn.Configuration;
using OpenVpnPilot.OpenVpn.Runtime;
using OpenVpnPilot.Platform.Windows.Diagnostics;
using OpenVpnPilot.Platform.Windows.InteractiveService;
using OpenVpnPilot.Platform.Windows.Runtime;
using Serilog;

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

        ConfigureLogging(builder, paths);

        builder.Services.AddSingleton<IApplicationPaths>(paths);
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddDbContextFactory<PilotDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath}"));

        builder.Services.AddSingleton<IProfileStore, ProfileStore>();

        RegisterPlatformServices(builder.Services);

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
        builder.Services.AddSingleton<ICredentialProvider, InteractiveCredentialProvider>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<TrayIconController>();

        return builder.Build();
    }

    private static void RegisterPlatformServices(IServiceCollection services)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "This build supports Windows only. Support for other systems means adding an "
                + "implementation of the platform interfaces, not changing the rest of the application.");
        }

        services.AddSingleton<InteractiveServicePipeClient>();
        services.AddSingleton<IOpenVpnLauncher, WindowsOpenVpnLauncher>();
        services.AddSingleton<IProfileMaterializer, WindowsProfileMaterializer>();
        services.AddSingleton<IOpenVpnEnvironmentProbe, WindowsOpenVpnEnvironmentProbe>();
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, UserApplicationPaths paths)
    {
        Serilog.Core.Logger logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "pilot-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: true);
    }
}
