using System.Net;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// The directive can arrive on any answer and the header decides, not the status. It is announced
/// once, and nothing is sent to that server afterwards.
/// </summary>
public sealed class WipeDirectiveTests
{
    [Fact]
    public async Task GetTagsAsync_AnswerCarriesTheDirective_AnnouncesItAndSendsNothingMore()
    {
        using TestServer server = new(_ => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.AccountRevoked, wipe: true));
        await server.SignedInAsync();

        List<ServerWipeDirective> announced = [];
        server.Connection.Wipe.WipeRequested += (_, args) => announced.Add(args.Directive);

        ServerResult first = await server.Api.GetTagsAsync();
        ServerResult second = await server.Api.GetFavouritesAsync();

        Assert.Equal(ServerOutcome.Wiped, first.Outcome);
        Assert.Equal(ServerErrorCodes.AccountRevoked, first.Code);
        Assert.Equal(ServerOutcome.Wiped, second.Outcome);

        ServerWipeDirective directive = Assert.Single(announced);
        Assert.Equal("/api/v1/tags", directive.Path);
        Assert.Equal(401, directive.Status);
        Assert.Equal(first.RequestId, directive.RequestId);
        Assert.Equal(TestServer.Address, directive.BaseAddress);

        // No refresh, no retry: the one call that received it is the only one sent.
        Assert.Single(server.Handler.Requests);
        Assert.True(server.Connection.Wipe.IsRequested);
    }

    [Fact]
    public async Task GetTagsAsync_ASuccessCarryingTheDirective_StillWipes()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags, wipe: true));
        await server.SignedInAsync();

        int announced = 0;
        server.Connection.Wipe.WipeRequested += (_, _) => announced++;

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.Wiped, result.Outcome);
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task Refresh_AnswerCarriesTheDirective_AnnouncesItWithoutRetrying()
    {
        using TestServer server = new(request => request.Path == "/api/v1/auth/refresh"
            ? Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.AccountRevoked, wipe: true)
            : Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired));
        await server.SignedInAsync();

        int announced = 0;
        server.Connection.Wipe.WipeRequested += (_, _) => announced++;

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.Wiped, result.Outcome);
        Assert.Equal(1, announced);
        Assert.Equal(["/api/v1/tags", "/api/v1/auth/refresh"], server.Handler.Requests.Select(r => r.Path));
        Assert.Equal("/api/v1/auth/refresh", server.Connection.Wipe.Directive!.Path);
    }

    [Fact]
    public async Task SignInAsync_ACorrectPasswordForASwitchedOffAccount_AnnouncesTheWipeAndSignsNobodyIn()
    {
        using TestServer server = new(_ => Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.AccountRevoked, wipe: true));

        int announced = 0;
        server.Connection.Wipe.WipeRequested += (_, _) => announced++;

        ServerResult<CurrentUserResponse> result = await server.Connection.SignIn.SignInAsync("operator", "a password");

        Assert.Equal(ServerOutcome.Wiped, result.Outcome);
        Assert.Equal(1, announced);
        Assert.False(server.Session.IsSignedIn);
        Assert.Empty(server.Secrets.Entries);
    }

    [Fact]
    public async Task ParallelCalls_AllCarryingTheDirective_AnnounceItOnce()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrived = 0;

        using TestServer server = new(async _ =>
        {
            if (Interlocked.Increment(ref arrived) == 5)
            {
                release.SetResult();
            }

            await release.Task;
            return Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.AccountRevoked, wipe: true);
        });
        await server.SignedInAsync();

        int announced = 0;
        server.Connection.Wipe.WipeRequested += (_, _) => Interlocked.Increment(ref announced);

        ServerResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => Task.Run<ServerResult>(async () => await server.Api.GetTagsAsync())));

        Assert.All(results, result => Assert.Equal(ServerOutcome.Wiped, result.Outcome));
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task GetServerInfoAsync_AfterAWipe_IsNotSentEither()
    {
        using TestServer server = new(request => request.Path == "/api/v1/server/info"
            ? Answers.Json(Answers.Info())
            : Answers.Problem(HttpStatusCode.Unauthorized, ServerErrorCodes.AccountRevoked, wipe: true));
        await server.SignedInAsync();

        await server.Api.GetTagsAsync();
        ServerResult info = await server.Api.GetServerInfoAsync();

        Assert.Equal(ServerOutcome.Wiped, info.Outcome);
        Assert.Single(server.Handler.Requests);
    }
}
