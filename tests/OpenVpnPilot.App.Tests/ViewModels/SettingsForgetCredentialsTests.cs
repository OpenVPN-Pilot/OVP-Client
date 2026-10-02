using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// "Forget all stored credentials" ends the session with the server before the refresh token goes.
/// </summary>
public sealed class SettingsForgetCredentialsTests : IAsyncDisposable
{
    private const string ServerKey = "0123456789abcdef0123456789abcdef";

    private readonly FakeSecrets secrets = new();
    private TestDatabase? database;

    [Fact]
    public async Task ForgetStoredCredentials_InServerMode_SignsOutWhileTheRefreshTokenIsStillThere()
    {
        await secrets.WriteAsync(SecretReference.ForServerRefreshToken(ServerKey), new StoredSecret(null, "refresh"));
        RecordingSignIn server = new(secrets);

        SettingsViewModel model = await ModelAsync(server);
        await model.ForgetStoredCredentialsCommand.ExecuteAsync(null);

        Assert.Equal(1, server.SignOuts);
        Assert.True(server.RefreshTokenWasThere);
        Assert.Null(await secrets.TryReadAsync(SecretReference.ForServerRefreshToken(ServerKey)));
    }

    [Fact]
    public async Task ForgetStoredCredentials_InServerMode_LeavesTheLocalLibraryAndOtherServersAlone()
    {
        database = await TestDatabase.CreateAsync();
        Guid serverProfile = (await database.AddProfileAsync("example-site")).Id;
        string serverSignIn = SecretReference.ForProfile(serverProfile, "Auth");
        string localSignIn = SecretReference.ForProfile(Guid.NewGuid(), "Auth");
        string otherServer = SecretReference.ForServerRefreshToken("fedcba9876543210fedcba9876543210");

        await secrets.WriteAsync(SecretReference.ForServerRefreshToken(ServerKey), new StoredSecret(null, "refresh"));
        await secrets.WriteAsync(serverSignIn, new StoredSecret("me", "shared"));
        await secrets.WriteAsync(localSignIn, new StoredSecret("me", "local"));
        await secrets.WriteAsync(otherServer, new StoredSecret(null, "other"));

        TypedCredentials held = new(new FixedStorageMode(true));
        held.Hold(serverProfile, "Auth", new StoredSecret("me", "typed"));

        SettingsViewModel model = await ModelAsync(
            new RecordingSignIn(secrets),
            new ServerCredentialsReset(database.Factory, secrets, held, NullLogger<ServerCredentialsReset>.Instance));
        await model.LoadAsync();

        Assert.Equal(1, model.StoredSecretCount);
        Assert.True(model.ForgetsThisServerOnly);

        await model.ForgetStoredCredentialsCommand.ExecuteAsync(null);

        Assert.Equal([localSignIn, otherServer], (await secrets.ListAsync()).Order(StringComparer.Ordinal));
        Assert.Null(held.Peek(serverProfile, "Auth"));
        Assert.Equal(0, model.StoredSecretCount);
        Assert.Equal("settings.credentialsCleared", model.StatusMessage);
    }

    [Fact]
    public async Task ForgetStoredCredentials_OnTheLocalLibrary_ClearsWithoutAServer()
    {
        await secrets.WriteAsync(SecretReference.ForProfile(Guid.NewGuid(), "Auth"), new StoredSecret("me", "mine"));

        SettingsViewModel model = await ModelAsync(server: null);
        await model.ForgetStoredCredentialsCommand.ExecuteAsync(null);

        Assert.Empty(await secrets.ListAsync());
        Assert.False(model.ForgetsThisServerOnly);
    }
    public async ValueTask DisposeAsync()
    {
        if (database is not null)
        {
            await database.DisposeAsync();
        }
    }

    private async Task<SettingsViewModel> ModelAsync(IServerSignIn? server, IServerCredentialsReset? credentialsReset = null)
    {
        database ??= await TestDatabase.CreateAsync();
        FakeSettingsService settings = new();
        HotkeyStore hotkeyStore = new(
            database.Factory,
            new ChangeRecorder(new Outbox(database.Factory, TimeProvider.System, NullLogger<Outbox>.Instance), new FixedStorageMode(false)));

        string languages = Path.Combine(AppContext.BaseDirectory, "lang");

        return new SettingsViewModel(
            settings,
            new StubLocalizer(),
            new LanguageCoordinator(new LocalizationManager(new JsonLanguageCatalogueSource([languages])), settings),
            hotkeyStore,
            new HotkeyCoordinator(new NoHotkeys(), hotkeyStore, settings),
            secrets,
            new NoAutoStart(),
            new SessionStore(database.Factory, TimeProvider.System),
            new DiagnosticsBundle(new TemporaryPaths(Path.GetTempPath()), new ReadyEnvironmentProbe(), settings, database.Factory, TimeProvider.System, ActiveStorage.Resolve(new TemporaryPaths(Path.GetTempPath()), StorageSelection.Local)),
            SilentUpdates.Coordinator(),
            dock: null,
            server: server,
            credentialsReset: credentialsReset);
    }

    private sealed class RecordingSignIn(FakeSecrets secrets) : IServerSignIn
    {
        public int SignOuts { get; private set; }

        public bool RefreshTokenWasThere { get; private set; }

        public Task<ServerCheckResult> CheckServerAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not part of forgetting.");

        public Task<ServerResult<CurrentUserResponse>> SignInAsync(string username, string? password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not part of forgetting.");

        public Task<ServerResult<CurrentUserResponse>> SignInWithEntraAsync(string entraAccessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not part of forgetting.");

        public async Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            SignOuts++;
            RefreshTokenWasThere = await secrets.TryReadAsync(SecretReference.ForServerRefreshToken(ServerKey), cancellationToken) is not null;

            // As the session does once the server has been told.
            await secrets.DeleteAsync(SecretReference.ForServerRefreshToken(ServerKey), cancellationToken);
        }
    }

    private sealed class NoHotkeys : IGlobalHotkeyService
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? Pressed
        {
            add { }
            remove { }
        }

        public HotkeyRegistration Register(string actionId, HotkeyGesture gesture) =>
            throw new NotSupportedException("No shortcuts in this test.");

        public void UnregisterAll()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoAutoStart : IAutoStartManager
    {
        public bool IsSupported => false;

        public bool IsEnabled() => false;

        public bool SetEnabled(bool enabled) => false;
    }
}
