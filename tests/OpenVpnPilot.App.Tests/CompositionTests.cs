using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.Tests;

/// <summary>
/// Builds the application's own composition root in both modes and checks that it is complete.
/// </summary>
/// <remarks>
/// The host validates its container only in the development environment, so a registration that
/// cannot be satisfied is otherwise found by the first copy that starts in the mode needing it.
/// Server mode composes the synchronisation, the session coordinator and the wipe on top of the
/// local graph, and nothing but a start against a server ever resolved them together.
/// </remarks>
public sealed class CompositionTests : IDisposable
{
    private static readonly ServiceProviderOptions Strict = new() { ValidateOnBuild = true, ValidateScopes = true };

    private readonly string root = Path.Combine(Path.GetTempPath(), "ovp-composition-" + Guid.NewGuid().ToString("N"));

    public CompositionTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Build_LocalMode_ValidatesEveryRegistration()
    {
        using IHost host = Build(StorageSelection.Local);

        Assert.False(host.Services.GetRequiredService<IActiveStorage>().IsServerMode);
        Assert.Null(host.Services.GetService<ISyncEngine>());
        Assert.Null(host.Services.GetService<IServerSessionCoordinator>());
    }

    [Fact]
    public void Build_ServerMode_ValidatesEveryRegistration()
    {
        using IHost host = Build(ServerSelection);

        Assert.True(host.Services.GetRequiredService<IActiveStorage>().IsServerMode);
    }

    [Fact]
    public void Resolve_ServerMode_ComposesTheSynchronisationAndTheSession()
    {
        using IHost host = Build(ServerSelection);
        IServiceProvider services = host.Services;

        // Registered through factories, which validating the container does not look inside.
        Assert.NotNull(services.GetRequiredService<IServerConnection>());
        Assert.NotNull(services.GetRequiredService<IServerSession>());
        Assert.NotNull(services.GetRequiredService<IServerApi>());
        Assert.NotNull(services.GetRequiredService<ISyncEngine>());
        Assert.NotNull(services.GetRequiredService<IServerWipe>());
        Assert.NotNull(services.GetRequiredService<IAccountRevokedNotice>());

        ServerSessionCoordinator coordinator = services.GetRequiredService<ServerSessionCoordinator>();
        Assert.Same(coordinator, services.GetRequiredService<IServerSessionCoordinator>());
        Assert.Same(coordinator, services.GetRequiredService<IServerSignIn>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_EitherMode_CreatesEveryRegisteredService(bool serverMode)
    {
        HostApplicationBuilder builder = Compose(serverMode ? ServerSelection : StorageSelection.Local);
        List<ServiceDescriptor> registrations = [.. builder.Services];
        using IHost host = builder.Build();

        List<string> failures = [];

        // A scoped registration is the database context, which is only ever created through its
        // factory; resolving it from the root is refused by design and says nothing here.
        foreach (Type type in registrations
            .Where(descriptor => descriptor.Lifetime != ServiceLifetime.Scoped)
            .Where(descriptor => !descriptor.ServiceType.ContainsGenericParameters)
            .Where(descriptor => !IsNativeOnTheMac(descriptor))
            .Select(descriptor => descriptor.ServiceType)
            .Distinct())
        {
            try
            {
                _ = host.Services.GetRequiredService(type);
            }
            catch (InvalidOperationException exception)
            {
                failures.Add($"{type.FullName}: {exception.Message}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Compose_ReadsNoFilesAndWatchesNothing()
    {
        HostApplicationBuilder builder = Compose(StorageSelection.Local);

        // A file source reloading on change is a recursive watcher on the content root, which for
        // an application started from the Finder is the whole disk.
        Assert.DoesNotContain(builder.Configuration.Sources, source => source is FileConfigurationSource);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath));
    }

    [Theory]
    [InlineData(false, typeof(FirstRunViewModel))]
    [InlineData(true, typeof(FirstRunViewModel))]
    [InlineData(true, typeof(FirstSyncViewModel))]
    public void CreateInstance_SetupViewModels_FindEveryDependency(bool serverMode, Type viewModel)
    {
        using IHost host = Build(serverMode ? ServerSelection : StorageSelection.Local);
        IServiceProviderIsService known = host.Services.GetRequiredService<IServiceProviderIsService>();

        // The startup creates these from the container rather than resolving them, so nothing
        // registers them and validation never sees what they ask for.
        ConstructorInfo constructor = Assert.Single(viewModel.GetConstructors());
        List<string> missing = [.. constructor.GetParameters()
            .Where(parameter => !parameter.HasDefaultValue && !known.IsService(parameter.ParameterType))
            .Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)];

        Assert.Empty(missing);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A log or database handle the host released late; the folder is in the temporary
            // directory and is removed with it.
        }
    }

    private static StorageSelection ServerSelection { get; } = new(StorageMode.Server, "https://pilot.example.com");

    /// <summary>
    /// Whether the registration is one of the macOS implementations, when the test runs on a Mac.
    /// </summary>
    /// <remarks>
    /// Those reach the status bar, the keychain and the application object as they are created,
    /// which belongs to the main thread of a running application and not to a test worker. Their
    /// dependencies are still checked by the validation the container runs when it is built; only
    /// creating them is left out. The Windows implementations create nothing native until they are
    /// attached, so they are created here like everything else.
    /// </remarks>
    private static bool IsNativeOnTheMac(ServiceDescriptor descriptor)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        Type type = descriptor.ImplementationType ?? descriptor.ServiceType;
        string space = type.Namespace ?? string.Empty;

        return space.StartsWith("OpenVpnPilot.Platform.", StringComparison.Ordinal)
            || (descriptor.ImplementationFactory is not null
                && descriptor.ServiceType.Namespace == "OpenVpnPilot.Core.Abstractions");
    }

    private IHost Build(StorageSelection selection) => Compose(selection).Build();

    private HostApplicationBuilder Compose(StorageSelection selection)
    {
        TemporaryPaths paths = new(root);
        Directory.CreateDirectory(paths.LogDirectory);

        ActiveStorage storage = ActiveStorage.Resolve(paths, selection);
        return AppHost.Compose(paths, storage, headless: true, Strict);
    }
}
