using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// The first start: asked only when there was no settings file, and a server chosen only once it
/// has been checked and has accepted the person.
/// </summary>
public sealed class FirstRunViewModelTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ovp-first-").FullName;
    private readonly RecordingSwitcher switcher = new();
    private readonly CountingFactory factory = new();

    [Fact]
    public async Task ShouldAsk_NoSettingsFile_Asks()
    {
        TemporaryPaths paths = new(root);
        JsonSettingsService settings = new(paths.SettingsPath);
        await settings.LoadAsync();

        ActiveStorage storage = ActiveStorage.Resolve(paths, StorageModeReader.Read(paths.SettingsPath));

        Assert.True(FirstRun.ShouldAsk(settings.FileExistedAtLoad, storage, headless: false));
    }

    [Fact]
    public async Task ShouldAsk_ExistingSettingsFile_DoesNotAskAndStaysLocal()
    {
        TemporaryPaths paths = new(root);
        await File.WriteAllTextAsync(paths.SettingsPath, "{ \"schemaVersion\": 1 }");

        JsonSettingsService settings = new(paths.SettingsPath);
        await settings.LoadAsync();
        ActiveStorage storage = ActiveStorage.Resolve(paths, StorageModeReader.Read(paths.SettingsPath));

        Assert.False(FirstRun.ShouldAsk(settings.FileExistedAtLoad, storage, headless: false));
        Assert.Equal(StorageMode.Local, storage.Mode);
        Assert.Equal(StorageMode.Local, settings.Current.Storage.Mode);
    }

    [Fact]
    public async Task ShouldAsk_HeadlessFirstStart_HasNobodyToAsk()
    {
        TemporaryPaths paths = new(root);
        JsonSettingsService settings = new(paths.SettingsPath);
        await settings.LoadAsync();
        ActiveStorage storage = ActiveStorage.Resolve(paths, StorageModeReader.Read(paths.SettingsPath));

        Assert.False(FirstRun.ShouldAsk(settings.FileExistedAtLoad, storage, headless: true));
    }

    [Fact]
    public async Task Continue_PlainHttp_IsRefusedWithAnExplanationAndNothingIsContacted()
    {
        FirstRunViewModel model = Model();
        model.ChooseServerCommand.Execute(null);
        model.Address = "http://pilot.example.com";

        await model.ContinueCommand.ExecuteAsync(null);

        Assert.Equal("firstRun.addressNotHttps", model.Message);
        Assert.Equal(FirstRunStep.Address, model.Step);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task Continue_NotAPilotServer_SaysSoAndStaysAtTheAddress()
    {
        factory.Answer = _ => ServerAnswers.Json(new { name = "Something else", version = "1", apiVersion = "1", minimumClientVersion = "1.0.0", authMode = "file", passwordRequired = true });
        FirstRunViewModel model = Model();
        model.ChooseServerCommand.Execute(null);
        model.Address = "pilot.example.com";

        await model.ContinueCommand.ExecuteAsync(null);

        Assert.Equal("signIn.notPilotServer", model.Message);
        Assert.Equal(FirstRunStep.Address, model.Step);
    }

    [Fact]
    public async Task Continue_ClientTooOld_AsksForAnUpdateBeforeSigningIn()
    {
        factory.Answer = _ => ServerAnswers.Json(ServerAnswers.Info(minimum: "9.0.0"));
        FirstRunViewModel model = Model();
        model.Address = "https://pilot.example.com";

        await model.ContinueCommand.ExecuteAsync(null);

        Assert.Equal("signIn.clientOutdatedVersion", model.Message);
        Assert.Null(model.SignIn);
    }

    [Fact]
    public async Task SignIn_Accepted_SwitchesToTheServerAndAsksTheNextCopyToSynchronise()
    {
        factory.Answer = request => request.RequestUri!.AbsolutePath == "/api/v1/server/info"
            ? ServerAnswers.Json(ServerAnswers.Info())
            : ServerAnswers.Tokens(ServerAnswers.Alice);

        FirstRunViewModel model = Model();
        model.Address = "https://Pilot.Example.com/";
        await model.ContinueCommand.ExecuteAsync(null);

        Assert.Equal(FirstRunStep.SignIn, model.Step);
        Assert.Equal("firstRun.serverSummary", model.ServerSummary);

        model.SignIn!.Username = "alice";
        model.SignIn.Password = "secret";
        await model.SignIn.SignInCommand.ExecuteAsync(null);

        Assert.Equal(FirstRunStep.Restarting, model.Step);
        Assert.Equal(["server https://pilot.example.com FirstSynchronisation"], switcher.Calls);

        // The session is left in the keystore for the copy that continues.
        Assert.NotNull(await factory.Secrets.TryReadAsync(SecretReference.ForServerRefreshToken(ServerKey.Compute("https://pilot.example.com"))));
    }

    [Fact]
    public async Task SignIn_SwitchFails_SignsOutAndOffersTheWayBack()
    {
        switcher.Answer = StorageSwitchOutcome.SettingsNotSaved;
        factory.Answer = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/server/info" => ServerAnswers.Json(ServerAnswers.Info()),
            "/api/v1/auth/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => ServerAnswers.Tokens(ServerAnswers.Alice),
        };

        FirstRunViewModel model = Model();
        model.Address = "https://pilot.example.com";
        await model.ContinueCommand.ExecuteAsync(null);
        model.SignIn!.Username = "alice";
        model.SignIn.Password = "secret";
        await model.SignIn.SignInCommand.ExecuteAsync(null);

        Assert.Equal(FirstRunStep.SignIn, model.Step);
        Assert.Equal("firstRun.switchFailed", model.Message);
        Assert.Empty(await factory.Secrets.ListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Choice_ThisComputerOrLater_GoesOnLocallyWithoutSwitching(bool later)
    {
        FirstRunViewModel model = Model();
        FirstRunChoice? chosen = null;
        model.Finished += (_, choice) => chosen = choice;

        (later ? model.DecideLaterCommand : model.ChooseThisComputerCommand).Execute(null);

        Assert.Equal(later ? FirstRunChoice.DecideLater : FirstRunChoice.ThisComputer, chosen);
        Assert.Empty(switcher.Calls);
    }

    [Fact]
    public async Task Switch_ServerWipesDuringTheSignIn_RemovesItsLeftoversAndSwitchesNothing()
    {
        factory.Answer = request => request.RequestUri!.AbsolutePath == "/api/v1/server/info"
            ? ServerAnswers.Json(ServerAnswers.Info())
            : ServerAnswers.Problem(HttpStatusCode.Forbidden, "auth.forbidden", wipe: true);

        RecordingLeftovers leftovers = new();
        FirstRunViewModel model = new(
            new StubLocalizer(),
            factory,
            switcher,
            new UnusedEntra(),
            NullLogger<FirstRunViewModel>.Instance,
            leftovers);

        model.BeginServerSwitch(null, activeAddress: null);
        model.Address = "https://pilot.example.com";
        await model.ContinueCommand.ExecuteAsync(null);
        model.SignIn!.Username = "alice";
        model.SignIn.Password = "secret";
        await model.SignIn.SignInCommand.ExecuteAsync(null);

        Assert.Equal(ServerKey.Compute("https://pilot.example.com"), await leftovers.Asked.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("signIn.accountRevoked", model.SignIn.Message);
        Assert.Empty(switcher.Calls);
    }

    [Fact]
    public async Task Switch_TheServerAlreadyInUse_IsNotContacted()
    {
        FirstRunViewModel model = Model();
        model.BeginServerSwitch("https://pilot.example.com", "https://pilot.example.com");

        await model.ContinueCommand.ExecuteAsync(null);

        Assert.Equal("storage.alreadyThisServer", model.Message);
        Assert.Equal(0, factory.Created);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    private FirstRunViewModel Model() => new(
        new StubLocalizer(),
        factory,
        switcher,
        new UnusedEntra(),
        NullLogger<FirstRunViewModel>.Instance);

    private sealed class RecordingLeftovers : IServerLeftovers
    {
        private readonly TaskCompletionSource<string> asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> Asked => asked.Task;

        public Task<ServerLeftoversReport?> RemoveAsync(ServerWipeDirective directive, string serverKey, CancellationToken cancellationToken = default)
        {
            asked.TrySetResult(serverKey);
            return Task.FromResult<ServerLeftoversReport?>(new ServerLeftoversReport(0, 0, false, true));
        }
    }

    private sealed class CountingFactory : IServerConnectionFactory
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public FakeSecrets Secrets { get; } = new();

        public int Created { get; private set; }

        public IServerConnection Create(Uri baseAddress, string serverKey)
        {
            Created++;
            ScriptedNetwork network = new(request => Answer(request));
            return TestConnection.CreateFactory(network, Secrets).Create(baseAddress, serverKey);
        }
    }
}
