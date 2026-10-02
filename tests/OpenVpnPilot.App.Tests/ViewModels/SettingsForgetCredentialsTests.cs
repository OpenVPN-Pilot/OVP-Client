using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

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
        await secrets.WriteAsync(SecretReference.ForProfile(Guid.NewGuid(), "Auth"), new StoredSecret("me", "mine"));
        RecordingSignIn server = new(secrets);

        SettingsViewModel model = await ModelAsync(server);
        await model.ForgetStoredCredentialsCommand.ExecuteAsync(null);

        Assert.Equal(1, server.SignOuts);
        Assert.True(server.RefreshTokenWasThere);
        Assert.Empty(await secrets.ListAsync());
    }

    [Fact]
    public async Task ForgetStoredCredentials_OnTheLocalLibrary_ClearsWithoutAServer()
    {
        await secrets.WriteAsync(SecretReference.ForProfile(Guid.NewGuid(), "Auth"), new StoredSecret("me", "mine"));

        SettingsViewModel model = await ModelAsync(server: null);
        await model.ForgetStoredCredentialsCommand.ExecuteAsync(null);

        Assert.Empty(await secrets.ListAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (database is not null)
        {
            await database.DisposeAsync();
        }
    }

    private async Task<SettingsViewModel> ModelAsync(IServerSignIn? server)
    {
        database = await TestDatabase.CreateAsync();
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
            new DiagnosticsBundle(new TemporaryPaths(Path.GetTempPath()), new ReadyEnvironmentProbe(), settings, database.Factory, TimeProvider.System),
            SilentUpdates.Coordinator(),
            dock: null,
            server: server);
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
