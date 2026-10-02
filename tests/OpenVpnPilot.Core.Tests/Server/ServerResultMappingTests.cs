using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// Whatever the network or the server does arrives as one of a few outcomes; nothing arrives as an
/// exception except the caller's own cancellation.
/// </summary>
public sealed class ServerResultMappingTests
{
    [Fact]
    public async Task GetTagsAsync_ProblemDetails_AreReadIntoCodeStatusDetailAndRequestId()
    {
        using TestServer server = new(_ => Answers.Raw(
            """
            {"type":"urn:openvpnpilot:error:request.validation_failed","title":"request.validation_failed","status":400,
             "detail":"A field is wrong.","instance":"/api/v1/tags","code":"request.validation_failed",
             "requestId":"557d1488-153d-4f0e-9d6a-2f4c8f0a1b2c","errors":{"Name":["The Name field is required."]}}
            """,
            ServerProblem.MediaType,
            HttpStatusCode.BadRequest));
        await server.SignedInAsync();

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.Problem, result.Outcome);
        Assert.Equal(ServerErrorCodes.ValidationFailed, result.Code);
        Assert.Equal(400, result.Status);
        Assert.Equal("A field is wrong.", result.Problem!.Detail);
        Assert.Equal("557d1488-153d-4f0e-9d6a-2f4c8f0a1b2c", result.RequestId);
        Assert.Equal(["The Name field is required."], result.Problem.Errors!["Name"]);
    }

    [Fact]
    public async Task GetTagsAsync_AProxyPageInFront_IsAProblemWithoutACode()
    {
        using TestServer server = new(_ => Answers.Raw("<html>Bad Gateway</html>", "text/html", HttpStatusCode.BadGateway));
        await server.SignedInAsync();

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.Problem, result.Outcome);
        Assert.Null(result.Code);
        Assert.Equal(502, result.Status);
        Assert.NotNull(result.RequestId);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    public async Task GetTagsAsync_ServerUnreachable_IsOffline(HttpRequestError error)
    {
        using TestServer server = new(_ => Answers.Fail(new HttpRequestException(error, "unreachable")));
        await server.SignedInAsync();

        ServerResult result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.Offline, result.Outcome);
        Assert.Null(result.Status);
        Assert.NotNull(result.RequestId);
    }

    [Fact]
    public async Task GetServerInfoAsync_CertificateNotTrusted_IsTlsRefused()
    {
        using TestServer server = new(_ => Answers.Fail(new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid."))));

        ServerResult result = await server.Api.GetServerInfoAsync();

        Assert.Equal(ServerOutcome.TlsRefused, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_NoAnswerInTime_IsOffline()
    {
        using HttpClient client = new(new StubHandler(async request =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), request.Cancellation);
            return Answers.NoContent();
        }))
        {
            BaseAddress = TestServer.Address,
        };

        ServerTransport transport = new(client, new ServerWipeSignal(), TimeProvider.System, NullLogger.Instance);

        ServerResult result = await transport.SendAsync(
            new ServerRequest(HttpMethod.Get, "api/v1/tags", Timeout: TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);

        Assert.Equal(ServerOutcome.Offline, result.Outcome);
    }

    [Fact]
    public async Task GetTagsAsync_CancelledByTheCaller_ThrowsRatherThanReportingOffline()
    {
        using TestServer server = new(async request =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), request.Cancellation);
            return Answers.NoContent();
        });
        await server.SignedInAsync();

        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.Api.GetTagsAsync(cancel.Token));
    }

    [Fact]
    public async Task SignInAsync_TooManyAttempts_SaysHowLongToWait()
    {
        using TestServer server = new(_ =>
        {
            HttpResponseMessage response = Answers.Problem(HttpStatusCode.TooManyRequests, ServerErrorCodes.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(37));
            return response;
        });

        ServerResult result = await server.Connection.SignIn.SignInAsync("operator", "a password");

        Assert.Equal(ServerErrorCodes.TooManyRequests, result.Code);
        Assert.Equal(TimeSpan.FromSeconds(37), result.RetryAfter);
    }

    [Theory]
    [InlineData("<html>a captive portal</html>")]
    [InlineData("""[{"id":"7c9e6679-7425-40de-944b-e07fc1f90ae7","name":"Office","colour":null}]""")]
    public async Task GetTagsAsync_ASuccessThatIsNotTheContract_IsAnInvalidResponse(string body)
    {
        using TestServer server = new(_ => Answers.Raw(body, "application/json"));
        await server.SignedInAsync();

        ServerResult<IReadOnlyList<TagResponse>> result = await server.Api.GetTagsAsync();

        Assert.Equal(ServerOutcome.InvalidResponse, result.Outcome);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public async Task GetChangesAsync_TheContractsShape_IsReadCompletely()
    {
        using TestServer server = new(_ => Answers.Raw(
            """
            {"cursor":42,"full":true,
             "profiles":[{"id":"8f14e45f-ceea-467f-a8f4-2f2f2f2f2f2f","name":"Example Site A","remoteHost":"vpn.example.com",
               "remotePort":1194,"protocol":"udp","requiresCredentials":true,"hasUnsupportedOptions":false,"protectRoutes":null,
               "notes":null,"colour":"#3366ff","tags":["Office"],"contentHash":"49c2","changeSeq":40,"eTag":"\"40\"",
               "createdAt":"2026-09-18T09:23:09.4177416+00:00","createdBy":"operator",
               "updatedAt":"2026-09-18T09:23:09+00:00","updatedBy":"operator"}],
             "tags":[{"id":"7c9e6679-7425-40de-944b-e07fc1f90ae7","name":"Office","colour":null,"changeSeq":1}],
             "vaultEntries":[{"profileId":"8f14e45f-ceea-467f-a8f4-2f2f2f2f2f2f","realm":"Auth","username":"vpnuser",
               "password":"shared-secret","changeSeq":41,"createdAt":"2026-09-18T09:23:09+00:00","createdBy":"operator",
               "updatedAt":"2026-09-18T09:23:09+00:00","updatedBy":"operator"}],
             "deletedProfiles":[],"deletedTags":[],
             "deletedVaultEntries":[{"profileId":"8f14e45f-ceea-467f-a8f4-2f2f2f2f2f2f","realm":"Private Key"}]}
            """,
            "application/json"));
        await server.SignedInAsync();

        ServerResult<SyncChangesResponse> result = await server.Api.GetChangesAsync(0);

        Assert.True(result.IsSuccess);
        SyncChangesResponse changes = result.Value;
        Assert.Equal(42, changes.Cursor);
        Assert.True(changes.Full);
        Assert.Equal("vpn.example.com", Assert.Single(changes.Profiles).RemoteHost);
        Assert.Equal("Office", Assert.Single(changes.Tags).Name);
        Assert.Equal("shared-secret", Assert.Single(changes.VaultEntries).Password);
        Assert.Equal("Private Key", Assert.Single(changes.DeletedVaultEntries).Realm);
        Assert.Equal("?since=0", server.Handler.Requests[0].Query);

        // A vault entry printed by accident still does not show its secret.
        Assert.DoesNotContain("shared-secret", changes.VaultEntries[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddVaultEntryAsync_ARealmWithASlash_TravelsEscaped()
    {
        using TestServer server = new(_ => Answers.Problem(HttpStatusCode.Conflict, ServerErrorCodes.VaultEntryExists));
        await server.SignedInAsync();
        Guid profileId = Guid.Parse("8f14e45f-ceea-467f-a8f4-2f2f2f2f2f2f");

        ServerResult result = await server.Api.AddVaultEntryAsync(
            profileId,
            "key/one two",
            new VaultEntryRequest(null, "passphrase"));

        Assert.Equal(ServerErrorCodes.VaultEntryExists, result.Code);
        Assert.Equal(
            $"/api/v1/profiles/{profileId:D}/vault/key%2Fone%20two",
            server.Handler.Requests[0].Path);
    }

    [Fact]
    public async Task UpdateProfileAsync_PushedOffline_SendsIfMatchAny()
    {
        using TestServer server = new(_ => Answers.Problem(HttpStatusCode.NotFound, ServerErrorCodes.ProfileNotFound));
        await server.SignedInAsync();

        await server.Api.UpdateProfileAsync(
            Guid.NewGuid(),
            new ProfileUpdateRequest("Example Site A", null, null, null, null, ["Office"]),
            PilotHeaders.MatchAny);

        SentRequest sent = server.Handler.Requests[0];
        Assert.Equal("*", sent.Header("If-Match"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, sent.Json.GetProperty("configuration").ValueKind);
    }

    [Fact]
    public async Task PutSettingsAsync_WithoutAnETag_SendsNoIfMatch()
    {
        using TestServer server = new(_ => Answers.Raw(
            """{"schemaVersion":2,"document":{"appearance":{"theme":"Dark"}},"eTag":"\"7\"","updatedAt":"2026-09-18T09:23:09+00:00"}""",
            "application/json"));
        await server.SignedInAsync();

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse("""{"appearance":{"theme":"Dark"}}""");
        ServerResult<SettingsResponse> result = await server.Api.PutSettingsAsync(new SettingsRequest(2, document.RootElement.Clone()));

        Assert.True(result.IsSuccess);
        Assert.Equal("Dark", result.Value.Document.GetProperty("appearance").GetProperty("theme").GetString());
        Assert.Null(server.Handler.Requests[0].Header("If-Match"));
    }

    [Fact]
    public async Task DeleteProfileAsync_NoContent_IsASuccess()
    {
        using TestServer server = new(_ => Answers.NoContent());
        await server.SignedInAsync();

        ServerResult result = await server.Api.DeleteProfileAsync(Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal(204, result.Status);
        Assert.Equal(HttpMethod.Delete, server.Handler.Requests[0].Method);
    }

    [Fact]
    public void Create_AnAddressWithoutHttps_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => ServerHttpClientFactory.NormaliseBaseAddress(new Uri("http://pilot.example.com/")));
        Assert.Equal(
            new Uri("https://pilot.example.com/pilot/"),
            ServerHttpClientFactory.NormaliseBaseAddress(new Uri("https://pilot.example.com/pilot")));
    }
}
