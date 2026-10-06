using System.Net;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// Asking what the server is comes first, then who signs in, and signing out discards the tokens
/// whatever the server says.
/// </summary>
public sealed class ServerSignInTests
{
    [Fact]
    public async Task CheckServerAsync_AnOpenVpnPilotServer_IsCompatible()
    {
        using TestServer server = new(_ => Answers.Json(Answers.Info(minimum: "1.9.0")));

        ServerCheckResult result = await server.Connection.SignIn.CheckServerAsync();

        Assert.Equal(ServerCompatibility.Compatible, result.Compatibility);
        Assert.Equal(ServerAuthModes.File, result.Info!.AuthMode);
    }

    [Theory]
    [InlineData("Another Service", "1", "1.0.0", ServerCompatibility.NotAPilotServer)]
    [InlineData(ServerInfoResponse.ExpectedName, "2", "1.0.0", ServerCompatibility.ApiVersionUnsupported)]
    [InlineData(ServerInfoResponse.ExpectedName, "1", "1.10.0", ServerCompatibility.ClientOutdated)]
    [InlineData(ServerInfoResponse.ExpectedName, "1", "2.0", ServerCompatibility.ClientOutdated)]
    [InlineData(ServerInfoResponse.ExpectedName, "1", "1.9", ServerCompatibility.Compatible)]
    [InlineData(ServerInfoResponse.ExpectedName, "1", "1.9.0-beta.1", ServerCompatibility.Compatible)]
    [InlineData(ServerInfoResponse.ExpectedName, "1", "not a version", ServerCompatibility.NotAPilotServer)]
    public async Task CheckServerAsync_WhatTheServerSays_DecidesTheVerdict(
        string name,
        string apiVersion,
        string minimum,
        ServerCompatibility expected)
    {
        using TestServer server = new(_ => Answers.Json(Answers.Info(name, apiVersion, minimum)), version: new Version(1, 9, 0));

        ServerCheckResult result = await server.Connection.SignIn.CheckServerAsync();

        Assert.Equal(expected, result.Compatibility);
        Assert.Equal(new Version(1, 9, 0), result.ClientVersion);
    }

    [Fact]
    public async Task CheckServerAsync_AWebSiteAtTheAddress_IsNotAPilotServer()
    {
        using TestServer server = new(_ => Answers.Raw("<html>Not found</html>", "text/html", HttpStatusCode.NotFound));

        ServerCheckResult result = await server.Connection.SignIn.CheckServerAsync();

        Assert.Equal(ServerCompatibility.NotAPilotServer, result.Compatibility);
    }

    [Fact]
    public async Task CheckServerAsync_Unreachable_LeavesTheVerdictOpenAndSaysWhy()
    {
        using TestServer server = new(_ => Answers.Fail(new HttpRequestException(HttpRequestError.ConnectionError, "refused")));

        ServerCheckResult result = await server.Connection.SignIn.CheckServerAsync();

        Assert.Equal(ServerCompatibility.Unknown, result.Compatibility);
        Assert.Equal(ServerOutcome.Offline, result.Transport.Outcome);
    }

    [Fact]
    public async Task SignInAsync_WithAPassword_StoresTheRefreshTokenAndNamesTheUser()
    {
        using TestServer server = new(_ => Answers.Tokens("access-1", "refresh-1", TestServer.Start.AddMinutes(15)));

        List<ServerSessionChangedEventArgs> changes = [];
        server.Session.Changed += (_, change) => changes.Add(change);

        ServerResult<CurrentUserResponse> result = await server.Connection.SignIn.SignInAsync("operator", "a password");

        Assert.True(result.IsSuccess);
        Assert.Equal("operator", result.Value.Username);
        Assert.True(result.Value.IsAdministrator);
        Assert.True(server.Session.IsSignedIn);
        Assert.Equal(result.Value, server.Session.User);
        Assert.Equal("refresh-1", server.Secrets.Entries[TestServer.RefreshReference].Password);
        Assert.Equal(ServerSessionChange.SignedIn, Assert.Single(changes).Change);

        SentRequest sent = Assert.Single(server.Handler.Requests);
        Assert.Equal("/api/v1/auth/login", sent.Path);
        Assert.Equal("operator", sent.Json.GetProperty("username").GetString());
        Assert.Equal("a password", sent.Json.GetProperty("password").GetString());
    }

    [Fact]
    public async Task SignInAsync_ModeNone_SendsNoPassword()
    {
        using TestServer server = new(_ => Answers.Tokens("access-1", "refresh-1", TestServer.Start.AddMinutes(15)));

        await server.Connection.SignIn.SignInAsync("operator", null);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, server.Handler.Requests[0].Json.GetProperty("password").ValueKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ServerErrorCodes.InvalidCredentials)]
    [InlineData(HttpStatusCode.Forbidden, ServerErrorCodes.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ServerErrorCodes.ProviderUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, ServerErrorCodes.ClockSkew)]
    [InlineData(HttpStatusCode.BadRequest, ServerErrorCodes.ModeMismatch)]
    public async Task SignInAsync_Refused_ReportsTheCodeAndSignsNobodyIn(HttpStatusCode status, string code)
    {
        using TestServer server = new(_ => Answers.Problem(status, code));

        ServerResult<CurrentUserResponse> result = await server.Connection.SignIn.SignInAsync("operator", "wrong");

        Assert.Equal(ServerOutcome.Problem, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Equal("Detail of " + code, result.Problem!.Detail);
        Assert.NotNull(result.RequestId);
        Assert.False(server.Session.IsSignedIn);
        Assert.Empty(server.Secrets.Entries);
    }

    [Fact]
    public async Task SignInWithEntraAsync_AnEntraToken_IsExchangedForTheServersTokens()
    {
        using TestServer server = new(_ => Answers.Tokens("access-1", "refresh-1", TestServer.Start.AddMinutes(15)));

        ServerResult<CurrentUserResponse> result = await server.Connection.SignIn.SignInWithEntraAsync("entra-token", "entra-state");

        SentRequest sent = Assert.Single(server.Handler.Requests);
        Assert.True(result.IsSuccess);
        Assert.Equal("/api/v1/auth/entra/exchange", sent.Path);
        Assert.Equal("entra-token", sent.Json.GetProperty("accessToken").GetString());
        Assert.True(server.Session.IsSignedIn);
        Assert.Equal("entra-state", server.Secrets.Entries[SecretReference.ForServerEntraState(TestServer.ServerKey)].Password);
    }

    [Fact]
    public async Task SignOutAsync_Confirmed_SendsTheRefreshTokenAndDiscardsBothTokens()
    {
        using TestServer server = new(request => request.Path == "/api/v1/auth/logout"
            ? Answers.NoContent()
            : Answers.Json(Answers.NoTags));
        await server.SignedInAsync();

        await server.Connection.SignIn.SignOutAsync();

        SentRequest sent = Assert.Single(server.Handler.Requests);
        Assert.Equal("refresh-1", sent.Json.GetProperty("refreshToken").GetString());
        Assert.False(server.Session.IsSignedIn);
        Assert.False(server.Secrets.Entries.ContainsKey(TestServer.RefreshReference));
        Assert.Equal(ServerOutcome.NotSignedIn, (await server.Api.GetTagsAsync()).Outcome);
    }

    [Fact]
    public async Task SignOutAsync_ServerUnreachable_DiscardsTheTokensAnyway()
    {
        using TestServer server = new(_ => Answers.Fail(new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        await server.SignedInAsync();

        await server.Connection.SignIn.SignOutAsync();

        Assert.False(server.Session.IsSignedIn);
        Assert.False(server.Secrets.Entries.ContainsKey(TestServer.RefreshReference));
    }

    [Fact]
    public async Task SignOutAsync_ServerFails_DiscardsTheTokensAnyway()
    {
        using TestServer server = new(_ => Answers.Problem(HttpStatusCode.InternalServerError, ServerErrorCodes.ServerError));
        await server.SignedInAsync();

        await server.Connection.SignIn.SignOutAsync();

        Assert.False(server.Session.IsSignedIn);
        Assert.Empty(server.Secrets.Entries);
    }
}
