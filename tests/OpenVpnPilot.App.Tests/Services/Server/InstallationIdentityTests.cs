using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// Settings that could not be read carry no installation identity, and a call that needs one says so
/// instead of throwing.
/// </summary>
public sealed class InstallationIdentityTests : IDisposable
{
    private const string Address = "https://pilot.example.com";

    private readonly FakeSettingsService settings = new();
    private readonly FakeSecrets secrets = new();
    private readonly ScriptedNetwork network = new(_ => ServerAnswers.Json(ServerAnswers.Info()));
    private readonly IServerConnection connection;

    public InstallationIdentityTests()
    {
        // As the settings service leaves them when the file exists but could not be read.
        settings.Current.Installation.Id = null;

        ServerHttpClientFactory clients = new(
            new AssemblyClientVersionProvider(typeof(InstallationIdentityTests).Assembly),
            new SettingsInstallationId(settings),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            () => network,
            ClientPlatform.Windows);

        connection = new ServerConnectionFactory(
                clients,
                new AssemblyClientVersionProvider(typeof(InstallationIdentityTests).Assembly),
                secrets,
                TimeProvider.System,
                NullLoggerFactory.Instance)
            .Create(new Uri(Address), ServerKey.Compute(Address));
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task SignInAsync_NoInstallationIdentity_AnswersWithoutSendingAnything()
    {
        ServerResult<CurrentUserResponse> result = await connection.SignIn.SignInAsync("operator", "secret");

        Assert.Equal(ServerOutcome.IdentityUnavailable, result.Outcome);
        Assert.Empty(network.Requests);
        Assert.Equal(SyncState.SettingsUnreadable, SyncFailures.StateOf(result));
        Assert.Equal("signIn.identityUnavailable", ServerMessages.SignInFailure(new StubLocalizer(), result));
    }

    [Fact]
    public async Task GetTagsAsync_SessionRestoredWithoutInstallationIdentity_AnswersWithoutSendingAnything()
    {
        await secrets.WriteAsync(SecretReference.ForServerRefreshToken(ServerKey.Compute(Address)), new StoredSecret(null, "refresh"));
        await connection.Session.RestoreAsync(ServerAnswers.Alice);

        ServerResult result = await connection.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.IdentityUnavailable, result.Outcome);
        Assert.Empty(network.Requests);

        // Nothing about the session was decided by a call that never left this machine.
        Assert.True(connection.Session.IsSignedIn);
    }

    [Fact]
    public async Task CheckServerAsync_NoInstallationIdentity_StillAsksTheServerAboutItself()
    {
        ServerCheckResult check = await connection.SignIn.CheckServerAsync();

        Assert.Equal(ServerInfoResponse.ExpectedName, check.Info?.Name);
        Assert.Equal(["GET /api/v1/server/info"], network.Requests);
    }
}
