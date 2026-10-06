using System.Net;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// What keeps a session alive past the server's demand for a new Microsoft sign in, past a stop in
/// the middle of a refresh, and past a keychain that would not answer at the start.
/// </summary>
public sealed class ServerSessionRenewalTests
{
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string InfoPath = "/api/v1/server/info";
    private const string ExchangePath = "/api/v1/auth/entra/exchange";
    private const string LogoutPath = "/api/v1/auth/logout";
    private const string TagsPath = "/api/v1/tags";

    private static readonly ServerInfoResponse EntraInfo = new(
        ServerInfoResponse.ExpectedName,
        "1.0.0",
        "1",
        "1.0.0",
        ServerAuthModes.Entra,
        false,
        new EntraInfoResponse(
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002",
            "api://00000000-0000-0000-0000-000000000002/access_as_user",
            "https://login.example.com/00000000-0000-0000-0000-000000000001/v2.0"));

    [Fact]
    public async Task Refresh_ReauthenticationRequired_RenewsThroughMicrosoftWithoutEndingTheSession()
    {
        using TestServer server = new(request => request.Path switch
        {
            RefreshPath => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.ReauthenticationRequired),
            InfoPath => Answers.Json(EntraInfo),
            ExchangePath => Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(30)),
            _ when request.Bearer == "access-1" => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
            _ => Answers.Json(Answers.NoTags),
        });
        await server.SignedInAsync(entraState: "entra-state-1");

        List<ServerSessionChangedEventArgs> changes = [];
        server.Session.Changed += (_, change) => changes.Add(change);

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.True(result.IsSuccess);
        Assert.True(server.Session.IsSignedIn);
        Assert.Empty(changes);
        Assert.Equal(["entra-state-1"], server.Entra.States);
        Assert.Equal("entra-renewed", server.Handler.Requests.Single(r => r.Path == ExchangePath).Json.GetProperty("accessToken").GetString());
        Assert.Equal("refresh-2", server.Secrets.Entries[TestServer.RefreshReference].Password);
        Assert.Equal("entra-state-2", server.Secrets.Entries[TestServer.EntraReference].Password);
        Assert.Equal("access-2", server.Handler.Requests.Last(r => r.Path == TagsPath).Bearer);
    }

    [Fact]
    public async Task Refresh_MicrosoftWantsThePerson_EndsTheSessionAndForgetsWhatItKept()
    {
        using TestServer server = new(request => request.Path switch
        {
            RefreshPath => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.ReauthenticationRequired),
            InfoPath => Answers.Json(EntraInfo),
            _ => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
        });
        server.Entra.Answer = new EntraRenewalResult(EntraRenewalOutcome.InteractionRequired, ErrorCode: "invalid_grant");
        await server.SignedInAsync(entraState: "entra-state-1");

        List<ServerSessionChangedEventArgs> changes = [];
        server.Session.Changed += (_, change) => changes.Add(change);

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerErrorCodes.ReauthenticationRequired, result.Code);
        Assert.False(server.Session.IsSignedIn);
        Assert.Equal(ServerSessionChange.SessionEnded, Assert.Single(changes).Change);
        Assert.False(server.Secrets.Entries.ContainsKey(TestServer.RefreshReference));
        Assert.False(server.Secrets.Entries.ContainsKey(TestServer.EntraReference));
        Assert.Equal(0, server.Handler.Count(ExchangePath));
    }

    [Fact]
    public async Task Refresh_MicrosoftUnreachable_KeepsEverythingAndTriesAgainLater()
    {
        using TestServer server = new(request => request.Path switch
        {
            RefreshPath => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.ReauthenticationRequired),
            InfoPath => Answers.Json(EntraInfo),
            _ => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
        });
        server.Entra.Answer = new EntraRenewalResult(EntraRenewalOutcome.Unavailable, ErrorCode: "request_timeout");
        await server.SignedInAsync(entraState: "entra-state-1");

        ServerResult first = await server.Api.GetTagsAsync();
        ServerResult second = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.Offline, first.Outcome);
        Assert.Equal(ServerOutcome.Offline, second.Outcome);
        Assert.True(server.Session.IsSignedIn);
        Assert.Equal("refresh-1", server.Secrets.Entries[TestServer.RefreshReference].Password);
        Assert.Equal("entra-state-1", server.Secrets.Entries[TestServer.EntraReference].Password);
        Assert.Equal(2, server.Entra.States.Count);
    }

    [Fact]
    public async Task Refresh_ReauthenticationRequiredWithoutAMicrosoftSignIn_EndsTheSession()
    {
        using TestServer server = new(request => request.Path == RefreshPath
            ? Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.ReauthenticationRequired)
            : Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired));
        await server.SignedInAsync();

        await server.Api.GetTagsAsync();

        Assert.False(server.Session.IsSignedIn);
        Assert.Empty(server.Entra.States);
        Assert.Equal(0, server.Handler.Count(InfoPath));
    }

    [Fact]
    public async Task Refresh_RenewalAnswersForSomebodyElse_EndsBothSessions()
    {
        CurrentUserResponse other = Answers.User() with { Id = Guid.Parse("01a0b3d3-6064-75e9-a940-c28df3732a06") };

        using TestServer server = new(request => request.Path switch
        {
            RefreshPath => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.ReauthenticationRequired),
            InfoPath => Answers.Json(EntraInfo),
            ExchangePath => Answers.Json(new TokenResponse("access-x", TestServer.Start.AddMinutes(30), "refresh-x", TestServer.Start.AddDays(30), other)),
            LogoutPath => Answers.NoContent(),
            _ => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
        });
        await server.SignedInAsync(entraState: "entra-state-1");

        await server.Api.GetTagsAsync();

        Assert.False(server.Session.IsSignedIn);
        Assert.Equal("refresh-x", server.Handler.Requests.Single(r => r.Path == LogoutPath).Json.GetProperty("refreshToken").GetString());
        Assert.False(server.Secrets.Entries.ContainsKey(TestServer.RefreshReference));
    }

    [Fact]
    public async Task Refresh_CallerStopsWhileTheServerAnswers_StillKeepsTheNewToken()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using TestServer server = new(async request =>
        {
            entered.TrySetResult();
            await release.Task;
            return Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(30));
        });
        await server.SignedInAsync();

        using CancellationTokenSource stop = new();
        Task<ServerResult<string>> refresh = server.Session.RefreshAsync("access-1", stop.Token);

        await entered.Task;
        await stop.CancelAsync();
        release.SetResult();

        ServerResult<string> result = await refresh;

        Assert.True(result.IsSuccess);
        Assert.False(server.Handler.Requests.Single().Cancellation.IsCancellationRequested);
        Assert.Equal("refresh-2", server.Secrets.Entries[TestServer.RefreshReference].Password);
    }

    [Fact]
    public async Task RestoreAsync_KeystoreRefuses_KeepsTheSessionAndSendsNothing()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags));
        await server.Secrets.WriteAsync(TestServer.RefreshReference, new StoredSecret(null, "refresh-stored"));
        server.Secrets.RefusesReads = true;

        bool restored = await server.Session.RestoreAsync(Answers.User(ServerRoles.User));
        ServerResult result = await server.Api.GetTagsAsync();

        Assert.True(restored);
        Assert.True(server.Session.IsSignedIn);
        Assert.Equal(ServerRoles.User, server.Session.User?.Role);
        Assert.Equal(ServerOutcome.KeystoreRefused, result.Outcome);
        Assert.Empty(server.Handler.Requests);
        Assert.Equal("refresh-stored", server.Secrets.Entries[TestServer.RefreshReference].Password);
    }

    [Fact]
    public async Task EstablishAsync_ASignInWithoutMicrosoft_RemovesWhatAnEarlierOneLeft()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags));
        await server.SignedInAsync(entraState: "entra-state-1");

        await server.SignedInAsync(refresh: "refresh-2");

        Assert.False(server.Secrets.Entries.ContainsKey(TestServer.EntraReference));
        Assert.Equal("refresh-2", server.Secrets.Entries[TestServer.RefreshReference].Password);
    }

    [Fact]
    public async Task SignOutAsync_AfterAMicrosoftSignIn_LeavesNothingOfMicrosoftBehind()
    {
        using TestServer server = new(_ => Answers.NoContent());
        await server.SignedInAsync(entraState: "entra-state-1");

        await server.Connection.SignIn.SignOutAsync();

        Assert.Empty(server.Secrets.Entries);
    }
}
