using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// How the server answers: the round trip of <c>server/info</c>, and <c>/health/ready</c> deciding
/// whether a server that answers is degraded.
/// </summary>
public sealed class ServerStatusSourceTests
{
    [Fact]
    public async Task Probe_ServerAnswersButIsNotReady_IsDegraded()
    {
        using TestConnection server = new(request => request.RequestUri!.AbsolutePath == "/health/ready"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : ServerAnswers.Json(ServerAnswers.Info()));
        using ServerStatusSource source = Source(server);

        Assert.True(await source.ProbeAsync());

        Assert.True(source.Current.Reachability.Degraded);
        Assert.NotNull(source.Current.Reachability.Latency);
        Assert.Equal(["GET /api/v1/server/info", "GET /health/ready"], server.Network.Requests);
        Assert.Equal(ServerStatusSource.Interval, source.NextDelay);
    }

    [Fact]
    public async Task Probe_ServerUnreachable_IsOfflineAndAskedLessOften()
    {
        using TestConnection server = new(_ => throw new HttpRequestException("refused"));
        using ServerStatusSource source = Source(server);

        Assert.False(await source.ProbeAsync());
        Assert.False(await source.ProbeAsync());

        Assert.False(source.Current.Reachability.Reachable);
        Assert.Equal(ServerStatusSource.Backoff[1], source.NextDelay);
    }

    private static ServerStatusSource Source(TestConnection server) => new(
        server.Connection.Api,
        server.Connection.Session,
        new FakeSyncEngine(),
        server.Connection.Wipe,
        TimeProvider.System,
        NullLogger<ServerStatusSource>.Instance);
}
