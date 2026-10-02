using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// The sign in form: the fields each mode asks for, and a sentence of its own for every refusal a
/// person can act on.
/// </summary>
public sealed class SignInViewModelTests : IDisposable
{
    private TestConnection? server;

    [Theory]
    [InlineData(ServerErrorCodes.InvalidCredentials, HttpStatusCode.Unauthorized, "signIn.invalidCredentials")]
    [InlineData(ServerErrorCodes.Forbidden, HttpStatusCode.Forbidden, "signIn.forbidden")]
    [InlineData(ServerErrorCodes.ProviderUnavailable, HttpStatusCode.ServiceUnavailable, "signIn.providerUnavailable")]
    [InlineData(ServerErrorCodes.TooManyRequests, (HttpStatusCode)429, "signIn.tooMany")]
    [InlineData(ServerErrorCodes.ClockSkew, HttpStatusCode.BadRequest, "signIn.clockSkew")]
    [InlineData(ServerErrorCodes.IdentityConflict, HttpStatusCode.Conflict, "signIn.identityConflict")]
    [InlineData(ServerErrorCodes.ClientOutdated, (HttpStatusCode)426, "signIn.clientOutdated")]
    [InlineData("server.error", HttpStatusCode.InternalServerError, "signIn.failedCode")]
    public async Task SignIn_Refused_SaysWhyInItsOwnWordsWithTheRequestId(string code, HttpStatusCode status, string expected)
    {
        SignInViewModel form = Form(_ =>
        {
            HttpResponseMessage response = ServerAnswers.Problem(status, code);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        form.Username = "alice";
        form.Password = "secret";
        await form.SignInCommand.ExecuteAsync(null);

        Assert.Equal(expected, form.Message);
        Assert.Equal("signIn.reference", form.Reference);
        Assert.Null(form.User);
    }

    [Fact]
    public async Task SignIn_CertificateNotTrusted_IsAConfigurationProblemWithNothingToBypass()
    {
        SignInViewModel form = Form(_ => throw new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid.")));

        form.Username = "alice";
        form.Password = "secret";
        await form.SignInCommand.ExecuteAsync(null);

        Assert.Equal("signIn.certificateUntrusted", form.Message);
        Assert.False(form.HasReference);
    }

    [Fact]
    public async Task SignIn_Accepted_ReportsThePersonAndForgetsThePassword()
    {
        SignInViewModel form = Form(_ => ServerAnswers.Tokens(ServerAnswers.Alice));
        CurrentUserResponse? signedIn = null;
        form.SignedIn += (_, user) => signedIn = user;

        form.Username = "alice";
        form.Password = "secret";
        await form.SignInCommand.ExecuteAsync(null);

        Assert.Equal(ServerAnswers.Alice, signedIn);
        Assert.Empty(form.Password);
        Assert.False(form.HasMessage);
    }

    [Fact]
    public void ModeNone_AsksForTheUserNameOnly()
    {
        SignInViewModel form = Form(_ => ServerAnswers.Tokens(ServerAnswers.Alice), ServerAnswers.Info(ServerAuthModes.None));

        Assert.True(form.AsksUsername);
        Assert.False(form.AsksPassword);

        form.Username = "alice";
        Assert.True(form.SignInCommand.CanExecute(null));
    }

    [Fact]
    public void ModeFile_NeedsAPasswordBeforeItCanSignIn()
    {
        SignInViewModel form = Form(_ => ServerAnswers.Tokens(ServerAnswers.Alice));
        form.Username = "alice";

        Assert.True(form.AsksPassword);
        Assert.False(form.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task ModeEntra_SignsInWithMicrosoftThenExchangesTheToken()
    {
        UnusedEntra entra = new() { Result = new EntraSignInResult(EntraSignInOutcome.Success, "entra-token") };
        ServerInfoResponse info = new(
            ServerInfoResponse.ExpectedName,
            "1.0.0",
            "1",
            "1.0.0",
            ServerAuthModes.Entra,
            false,
            new EntraInfoResponse("tenant", "client", "api://client/access_as_user", "https://login.microsoftonline.com/tenant/v2.0"));

        SignInViewModel form = Form(_ => ServerAnswers.Tokens(ServerAnswers.Alice), info, entra);

        Assert.False(form.AsksUsername);
        await form.SignInCommand.ExecuteAsync(null);

        Assert.Equal(1, entra.Calls);
        Assert.Contains("POST /api/v1/auth/entra/exchange", server!.Network.Requests);
        Assert.Equal(ServerAnswers.Alice, form.User);
    }

    [Fact]
    public async Task ModeEntra_Cancelled_SendsNothingToTheServer()
    {
        UnusedEntra entra = new();
        ServerInfoResponse info = new(
            ServerInfoResponse.ExpectedName,
            "1.0.0",
            "1",
            "1.0.0",
            ServerAuthModes.Entra,
            false,
            new EntraInfoResponse("tenant", "client", "scope", "https://login.microsoftonline.com/tenant/v2.0"));

        SignInViewModel form = Form(_ => ServerAnswers.Tokens(ServerAnswers.Alice), info, entra);
        await form.SignInCommand.ExecuteAsync(null);

        Assert.Equal("signIn.cancelled", form.Message);
        Assert.Empty(server!.Network.Requests);
    }

    public void Dispose() => server?.Dispose();

    private SignInViewModel Form(
        Func<HttpRequestMessage, HttpResponseMessage> answer,
        ServerInfoResponse? info = null,
        IEntraSignIn? entra = null)
    {
        server = new TestConnection(answer);

        return new SignInViewModel(
            new StubLocalizer(),
            server.Connection.SignIn,
            entra ?? new UnusedEntra(),
            info ?? ServerAnswers.Info(),
            server.Connection.BaseAddress);
    }
}
