using System.Net;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// A refresh token is single use, and two refreshes with one token end the session for good. These
/// pin the lock, the order of storing and using, and what a failed refresh does.
/// </summary>
public sealed class ServerSessionTests
{
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string TagsPath = "/api/v1/tags";

    [Fact]
    public async Task GetTagsAsync_RefusedWithTokenExpired_RefreshesOnceAndRepeats()
    {
        using TestServer server = new(request => request.Path switch
        {
            RefreshPath => Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(15)),
            _ when request.Bearer == "access-1" => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
            _ => Answers.Json(Answers.NoTags),
        });
        await server.SignedInAsync();

        ServerResult<IReadOnlyList<TagResponse>> result = await server.Api.GetTagsAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, server.Handler.Count(RefreshPath));
        Assert.Equal(["access-1", "access-2"], server.Handler.Requests.Where(r => r.Path == TagsPath).Select(r => r.Bearer));
        Assert.Equal("refresh-1", server.Handler.Requests.Single(r => r.Path == RefreshPath).Json.GetProperty("refreshToken").GetString());
    }

    [Theory]
    [InlineData(ServerErrorCodes.TokenRevoked)]
    [InlineData(ServerErrorCodes.TokenInvalid)]
    public async Task GetTagsAsync_RefusedForAnotherTokenReason_AlsoRefreshesOnce(string code)
    {
        using TestServer server = new(request => request.Path switch
        {
            RefreshPath => Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(15)),
            _ when request.Bearer == "access-1" => Answers.Problem(HttpStatusCode.Unauthorized, code),
            _ => Answers.Json(Answers.NoTags),
        });
        await server.SignedInAsync();

        Assert.True((await server.Api.GetTagsAsync()).IsSuccess);
        Assert.Equal(1, server.Handler.Count(RefreshPath));
    }

    [Fact]
    public async Task GetTagsAsync_RefusedAgainAfterTheRefresh_ReturnsTheRefusalWithoutAnotherRefresh()
    {
        using TestServer server = new(request => request.Path == RefreshPath
            ? Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(15))
            : Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired));
        await server.SignedInAsync();

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.Problem, result.Outcome);
        Assert.Equal(ServerErrorCodes.TokenExpired, result.Code);
        Assert.Equal(1, server.Handler.Count(RefreshPath));
        Assert.Equal(2, server.Handler.Count(TagsPath));
    }

    [Fact]
    public async Task GetTagsAsync_RefusedForAnotherReason_DoesNotRefresh()
    {
        using TestServer server = new(_ => Answers.Problem(HttpStatusCode.Forbidden, ServerErrorCodes.Forbidden));
        await server.SignedInAsync();

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerErrorCodes.Forbidden, result.Code);
        Assert.Equal(0, server.Handler.Count(RefreshPath));
    }

    [Fact]
    public async Task TenParallelCalls_WithAnExpiredToken_CauseExactlyOneRefresh()
    {
        TaskCompletionSource releaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using TestServer server = new(async request =>
        {
            if (request.Path == RefreshPath)
            {
                // Held open until every caller is waiting, so they genuinely overlap.
                await releaseRefresh.Task;
                return Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(30));
            }

            return request.Bearer == "access-2"
                ? Answers.Json(Answers.NoTags)
                : Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired);
        });

        await server.SignedInAsync(validFor: TimeSpan.FromMinutes(15));
        server.Time.Now = TestServer.Start.AddMinutes(16);

        Task<ServerResult<IReadOnlyList<TagResponse>>>[] calls =
            [.. Enumerable.Range(0, 10).Select(_ => Task.Run(() => server.Api.GetTagsAsync()))];

        await WaitUntilAsync(() => server.Handler.Count(RefreshPath) == 1);
        await Task.Delay(100);
        releaseRefresh.SetResult();

        ServerResult<IReadOnlyList<TagResponse>>[] results = await Task.WhenAll(calls);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, server.Handler.Count(RefreshPath));
        Assert.All(server.Handler.Requests.Where(r => r.Path == TagsPath), r => Assert.Equal("access-2", r.Bearer));
    }

    [Fact]
    public async Task TenParallelCalls_AllRefusedWithTokenExpired_CauseExactlyOneRefresh()
    {
        TaskCompletionSource releaseRefusals = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refused = 0;

        using TestServer server = new(async request =>
        {
            if (request.Path == RefreshPath)
            {
                return Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(30));
            }

            if (request.Bearer == "access-1")
            {
                // Every call is refused at the same moment, the way a token expiring on the server
                // meets a burst of calls.
                if (Interlocked.Increment(ref refused) == 10)
                {
                    releaseRefusals.SetResult();
                }

                await releaseRefusals.Task;
                return Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired);
            }

            return Answers.Json(Answers.NoTags);
        });

        await server.SignedInAsync();

        ServerResult<IReadOnlyList<TagResponse>>[] results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => Task.Run(() => server.Api.GetTagsAsync())));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, server.Handler.Count(RefreshPath));
    }

    [Fact]
    public async Task Refresh_TheNewRefreshToken_IsStoredBeforeTheNewAccessTokenIsUsed()
    {
        using TestServer server = new(request => request.Path switch
        {
            RefreshPath => Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(30)),
            _ when request.Bearer == "access-1" => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
            _ => Answers.Json(Answers.NoTags),
        });
        await server.SignedInAsync();

        await server.Api.GetTagsAsync();

        int stored = server.Journal.IndexOf($"store {TestServer.RefreshReference} refresh-2");
        int used = server.Journal.IndexOf($"send {TagsPath} access-2");

        Assert.True(stored >= 0, "The new refresh token was never stored.");
        Assert.True(used > stored, "The new access token was used before the refresh token was stored.");
        Assert.Equal("refresh-2", server.Secrets.Entries[TestServer.RefreshReference].Password);
    }

    [Fact]
    public async Task Refresh_KeystoreRefusesTheNewToken_RemovesTheUsedOneAndCarriesOnFromMemory()
    {
        using TestServer server = new(request => request.Path switch
        {
            RefreshPath => Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(30)),
            _ when request.Bearer == "access-1" => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
            _ => Answers.Json(Answers.NoTags),
        });
        await server.SignedInAsync();
        server.Secrets.RefusesWrites = true;

        ServerResult result = await server.Api.GetTagsAsync();

        // This run goes on with the new pair; the next start finds no token and asks for a sign in,
        // instead of presenting one the server has already seen used.
        Assert.True(result.IsSuccess);
        Assert.True(server.Session.IsSignedIn);
        Assert.False(server.Secrets.Entries.ContainsKey(TestServer.RefreshReference));

        int refused = server.Journal.IndexOf($"refuse {TestServer.RefreshReference}");
        int removed = server.Journal.IndexOf($"delete {TestServer.RefreshReference}");
        int used = server.Journal.IndexOf($"send {TagsPath} access-2");
        Assert.True(refused >= 0 && removed > refused, "The used refresh token was left in the keystore.");
        Assert.True(used > removed, "The new access token was used before the used refresh token was removed.");
    }

    [Theory]
    [InlineData(ServerErrorCodes.RefreshTokenInvalid)]
    [InlineData(ServerErrorCodes.RefreshTokenReused)]
    [InlineData(ServerErrorCodes.ReauthenticationRequired)]
    [InlineData(ServerErrorCodes.ClientMismatch)]
    public async Task Refresh_Refused_SignsOutAndTouchesNothingElse(string code)
    {
        using TestServer server = new(request => request.Path == RefreshPath
            ? Answers.Problem(HttpStatusCode.Unauthorized, code)
            : Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired));

        string profileSecret = SecretReference.ForProfile(Guid.NewGuid(), "Auth");
        await server.Secrets.WriteAsync(profileSecret, new StoredSecret("vpnuser", "kept"));
        await server.SignedInAsync();

        List<ServerSessionChangedEventArgs> changes = [];
        server.Session.Changed += (_, change) => changes.Add(change);

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(code, result.Code);
        Assert.False(server.Session.IsSignedIn);
        Assert.Null(server.Session.User);
        Assert.False(server.Secrets.Entries.ContainsKey(TestServer.RefreshReference));
        Assert.True(server.Secrets.Entries.ContainsKey(profileSecret));

        ServerSessionChangedEventArgs ended = Assert.Single(changes);
        Assert.Equal(ServerSessionChange.SessionEnded, ended.Change);
        Assert.Equal(code, ended.Code);
        Assert.NotNull(ended.PreviousUser);

        Assert.Equal(ServerOutcome.NotSignedIn, (await server.Api.GetTagsAsync()).Outcome);
        Assert.Equal(1, server.Handler.Count(RefreshPath));
    }

    [Fact]
    public async Task Refresh_WhileOffline_KeepsTheSession()
    {
        using TestServer server = new(request => request.Path == RefreshPath
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
            : Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired));
        await server.SignedInAsync();

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.Offline, result.Outcome);
        Assert.True(server.Session.IsSignedIn);
        Assert.Equal("refresh-1", server.Secrets.Entries[TestServer.RefreshReference].Password);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithinAMinuteOfExpiry_RefreshesBeforeCalling()
    {
        using TestServer server = new(request => request.Path == RefreshPath
            ? Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(30))
            : Answers.Json(Answers.NoTags));
        await server.SignedInAsync(validFor: TimeSpan.FromMinutes(15));

        server.Time.Now = TestServer.Start.AddMinutes(14).AddSeconds(5);
        await server.Api.GetTagsAsync();

        Assert.Equal(1, server.Handler.Count(RefreshPath));
        Assert.Equal("access-2", server.Handler.Requests.Single(r => r.Path == TagsPath).Bearer);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WellBeforeExpiry_DoesNotRefresh()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags));
        await server.SignedInAsync(validFor: TimeSpan.FromMinutes(15));

        server.Time.Now = TestServer.Start.AddMinutes(13);
        await server.Api.GetTagsAsync();

        Assert.Equal(0, server.Handler.Count(RefreshPath));
    }

    [Fact]
    public async Task GetTagsAsync_WithoutASession_SendsNothing()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags));

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.NotSignedIn, result.Outcome);
        Assert.Empty(server.Handler.Requests);
    }

    [Fact]
    public async Task RestoreAsync_AStoredRefreshToken_RefreshesOnTheFirstCall()
    {
        using TestServer server = new(request => request.Path == RefreshPath
            ? Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(15))
            : Answers.Json(Answers.NoTags));
        await server.Secrets.WriteAsync(TestServer.RefreshReference, new StoredSecret(null, "refresh-stored"));

        bool restored = await server.Session.RestoreAsync(Answers.User(ServerRoles.User));
        await server.Api.GetTagsAsync();

        Assert.True(restored);
        Assert.Equal("refresh-stored", server.Handler.Requests[0].Json.GetProperty("refreshToken").GetString());
        Assert.Equal("access-2", server.Handler.Requests[1].Bearer);
    }

    [Fact]
    public async Task RestoreAsync_NothingStored_StaysSignedOut()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags));

        Assert.False(await server.Session.RestoreAsync(null));
        Assert.False(server.Session.IsSignedIn);
    }

    [Fact]
    public async Task Refresh_ANewRole_IsAnnounced()
    {
        using TestServer server = new(request => request.Path == RefreshPath
            ? Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(15), ServerRoles.User)
            : Answers.Json(Answers.NoTags));
        await server.SignedInAsync(validFor: TimeSpan.Zero);

        List<ServerSessionChangedEventArgs> changes = [];
        server.Session.Changed += (_, change) => changes.Add(change);

        await server.Api.GetTagsAsync();

        ServerSessionChangedEventArgs changed = Assert.Single(changes);
        Assert.Equal(ServerSessionChange.UserChanged, changed.Change);
        Assert.Equal(ServerRoles.Admin, changed.PreviousUser!.Role);
        Assert.Equal(ServerRoles.User, changed.User!.Role);
        Assert.False(server.Session.User!.IsAdministrator);
    }

    [Fact]
    public async Task SessionTraffic_NeverWritesATokenToTheLog()
    {
        using TestServer server = new(request => request.Path switch
        {
            "/api/v1/auth/login" => Answers.Tokens("secret-access-1", "secret-refresh-1", TestServer.Start.AddMinutes(15)),
            RefreshPath => Answers.Tokens("secret-access-2", "secret-refresh-2", TestServer.Start.AddMinutes(30)),
            _ when request.Bearer == "secret-access-1" => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
            "/api/v1/auth/logout" => Answers.NoContent(),
            _ => Answers.Json(Answers.NoTags),
        });

        await server.Connection.SignIn.SignInAsync("operator", "secret-password");
        await server.Api.GetTagsAsync();
        await server.Connection.SignIn.SignOutAsync();

        Assert.NotEmpty(server.Logs.Lines);
        Assert.All(server.Logs.Lines, line => Assert.DoesNotContain("secret-", line, StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
