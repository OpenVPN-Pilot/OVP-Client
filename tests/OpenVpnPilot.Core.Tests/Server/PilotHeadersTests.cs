using System.Text.RegularExpressions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// The server refuses an API call without its headers, and asks for none on the two anonymous ones.
/// </summary>
public sealed partial class PilotHeadersTests
{
    private static readonly string[] KnownPlatforms = ["windows", "macos", "linux"];

    private static readonly string[] MandatoryHeaders =
    [
        PilotHeaders.ClientVersion,
        PilotHeaders.ApiVersion,
        PilotHeaders.ClientId,
        PilotHeaders.Platform,
        PilotHeaders.Timestamp,
        PilotHeaders.RequestId,
    ];

    [Fact]
    public async Task GetTagsAsync_AnApiCall_CarriesEveryMandatoryHeader()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags));
        await server.SignedInAsync();

        ServerResult result = await server.Api.GetTagsAsync();

        SentRequest sent = Assert.Single(server.Handler.Requests);
        Assert.True(result.IsSuccess);
        Assert.Equal("1.9.0", sent.Header(PilotHeaders.ClientVersion));
        Assert.Equal("1", sent.Header(PilotHeaders.ApiVersion));
        Assert.Equal(TestServer.InstallationId.ToString("D"), sent.Header(PilotHeaders.ClientId));
        Assert.Equal("windows", sent.Header(PilotHeaders.Platform));
        Assert.Equal(TestServer.Timestamp(TestServer.Start), sent.Header(PilotHeaders.Timestamp));
        Assert.Matches(SafeRequestId(), sent.Header(PilotHeaders.RequestId)!);
        Assert.Equal("access-1", sent.Bearer);
    }

    [Fact]
    public async Task GetTagsAsync_ATimestamp_IsTheMomentOfSending()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags));
        await server.SignedInAsync();

        server.Time.Now = TestServer.Start.AddSeconds(42);
        await server.Api.GetTagsAsync();

        Assert.Equal(
            TestServer.Timestamp(TestServer.Start.AddSeconds(42)),
            server.Handler.Requests[0].Header(PilotHeaders.Timestamp));
    }

    [Fact]
    public async Task GetServerInfoAsync_TheAnonymousCall_CarriesNoPilotHeaderAndNoToken()
    {
        using TestServer server = new(_ => Answers.Json(Answers.Info()));
        await server.SignedInAsync();

        await server.Api.GetServerInfoAsync();

        SentRequest sent = Assert.Single(server.Handler.Requests);
        Assert.Equal("/api/v1/server/info", sent.Path);
        Assert.DoesNotContain(sent.Headers.Keys, name => name.StartsWith("X-Pilot-", StringComparison.OrdinalIgnoreCase));
        Assert.Null(sent.Bearer);
    }

    [Fact]
    public async Task GetReadinessAsync_TheHealthCall_CarriesNoPilotHeader()
    {
        using TestServer server = new(_ => Answers.Raw("Healthy", "text/plain"));

        ServerResult result = await server.Api.GetReadinessAsync();

        SentRequest sent = Assert.Single(server.Handler.Requests);
        Assert.True(result.IsSuccess);
        Assert.Equal("/health/ready", sent.Path);
        Assert.DoesNotContain(sent.Headers.Keys, name => name.StartsWith("X-Pilot-", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/api/v1/server/info", true)]
    [InlineData("/health", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/health/live", true)]
    [InlineData("/healthy", false)]
    [InlineData("/api/v1/auth/login", false)]
    [InlineData("/api/v1/server/info/more", false)]
    public void IsAnonymous_UnderAPathPrefix_JudgesBelowIt(string path, bool anonymous)
    {
        Uri prefixed = new("https://pilot.example.com/pilot/");

        Assert.Equal(anonymous, PilotHeadersHandler.IsAnonymous(prefixed, new Uri(prefixed, path.TrimStart('/'))));
        Assert.Equal(anonymous, PilotHeadersHandler.IsAnonymous(TestServer.Address, new Uri(TestServer.Address, path.TrimStart('/'))));
    }

    [Fact]
    public async Task SignInAsync_TheLoginCall_CarriesTheMandatoryHeaders()
    {
        using TestServer server = new(_ => Answers.Tokens("access-1", "refresh-1", TestServer.Start.AddMinutes(15)));

        await server.Connection.SignIn.SignInAsync("operator", "a password");

        SentRequest sent = Assert.Single(server.Handler.Requests);
        Assert.All(MandatoryHeaders, name => Assert.NotNull(sent.Header(name)));
    }

    [Fact]
    public async Task GetTagsAsync_EveryCall_HasAFreshRequestId()
    {
        using TestServer server = new(_ => Answers.Json(Answers.NoTags));
        await server.SignedInAsync();

        await server.Api.GetTagsAsync();
        await server.Api.GetTagsAsync();
        await server.Api.GetTagsAsync();

        string?[] ids = [.. server.Handler.Requests.Select(request => request.Header(PilotHeaders.RequestId))];
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public async Task GetTagsAsync_ACallRepeatedAfterARefresh_IsANewRequest()
    {
        using TestServer server = new(request => request.Path switch
        {
            "/api/v1/auth/refresh" => Answers.Tokens("access-2", "refresh-2", TestServer.Start.AddMinutes(15)),
            _ when request.Bearer == "access-1" => Answers.Problem(System.Net.HttpStatusCode.Unauthorized, ServerErrorCodes.TokenExpired),
            _ => Answers.Json(Answers.NoTags),
        });
        await server.SignedInAsync();

        await server.Api.GetTagsAsync();

        string?[] ids = [.. server.Handler.Requests.Select(request => request.Header(PilotHeaders.RequestId))];
        Assert.Equal(3, ids.Length);
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public async Task CreateProfileAsync_ABody_IsSentWithoutAskingToContinue()
    {
        using TestServer server = new(_ => Answers.Problem(System.Net.HttpStatusCode.Conflict, ServerErrorCodes.ProfileDuplicate));
        await server.SignedInAsync();

        await server.Api.CreateProfileAsync(new ProfileCreateRequest("Example Site A", "client\n", null, null, null, null));

        Assert.NotEqual(true, server.Handler.Requests[0].ExpectContinue);
    }

    [Fact]
    public void Version_FromAnAssembly_HasThreeParts()
    {
        AssemblyClientVersionProvider provider = new(typeof(AssemblyClientVersionProvider).Assembly);

        Assert.Matches(@"^\d+\.\d+\.\d+$", provider.Version.ToString());
        Assert.Equal(-1, provider.Version.Revision);
    }

    [Fact]
    public void Platform_OnThisMachine_IsOneTheServerKnows()
    {
        Assert.Contains(ClientPlatform.Current, KnownPlatforms);
    }

    // The server takes a request id only when it is a plain token of 8 to 64 characters.
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeRequestId();
}
