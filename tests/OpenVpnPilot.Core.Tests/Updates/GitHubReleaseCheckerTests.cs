using System.Net;
using System.Text;
using OpenVpnPilot.Core.Updates;

namespace OpenVpnPilot.Core.Tests.Updates;

public sealed class GitHubReleaseCheckerTests
{
    private static readonly Version Current = new(1, 2, 0);

    [Fact]
    public async Task CheckAsync_WithoutARepository_ContactsNothing()
    {
        StubHandler handler = new();
        GitHubReleaseChecker checker = new(new HttpClient(handler), repository: null, Current);

        UpdateCheckResult result = await checker.CheckAsync();

        Assert.False(checker.IsConfigured);
        Assert.Equal(UpdateOutcome.NotConfigured, result.Outcome);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CheckAsync_WithANewerRelease_ReportsItAndWhereToReadAboutIt()
    {
        StubHandler handler = new()
        {
            Body = """{"tag_name":"v1.3.0","html_url":"https://example.invalid/releases/v1.3.0"}""",
        };

        GitHubReleaseChecker checker = new(new HttpClient(handler), "example/repo", Current);

        UpdateCheckResult result = await checker.CheckAsync();

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(new Version(1, 3, 0), result.LatestVersion);
        Assert.Equal("https://example.invalid/releases/v1.3.0", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckAsync_WithTheSameRelease_ReportsUpToDate()
    {
        StubHandler handler = new() { Body = """{"tag_name":"v1.2.0"}""" };
        GitHubReleaseChecker checker = new(new HttpClient(handler), "example/repo", Current);

        Assert.Equal(UpdateOutcome.UpToDate, (await checker.CheckAsync()).Outcome);
    }

    [Fact]
    public async Task CheckAsync_WithNoReleasesYet_ReportsUpToDateRatherThanAFailure()
    {
        StubHandler handler = new() { Status = HttpStatusCode.NotFound };
        GitHubReleaseChecker checker = new(new HttpClient(handler), "example/repo", Current);

        Assert.Equal(UpdateOutcome.UpToDate, (await checker.CheckAsync()).Outcome);
    }

    [Fact]
    public async Task CheckAsync_WhenTheRequestFails_SaysSoRatherThanClaimingToBeUpToDate()
    {
        StubHandler handler = new() { Status = HttpStatusCode.ServiceUnavailable };
        GitHubReleaseChecker checker = new(new HttpClient(handler), "example/repo", Current);

        UpdateCheckResult result = await checker.CheckAsync();

        // Not knowing is different from being up to date, and the difference matters to someone
        // waiting for a fix.
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("503", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_SendsAUserAgentBecauseTheInterfaceRequiresOne()
    {
        StubHandler handler = new() { Body = """{"tag_name":"v1.2.0"}""" };
        GitHubReleaseChecker checker = new(new HttpClient(handler), "example/repo", Current);

        await checker.CheckAsync();

        Assert.Contains("OpenVpnPilot", handler.LastUserAgent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V2.0", "2.0")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData("v1.2.3+build7", "1.2.3")]
    public void TryParseVersion_ReadsTheUsualTagShapes(string tag, string expected)
    {
        Assert.True(GitHubReleaseChecker.TryParseVersion(tag, out Version? version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("")]
    [InlineData("v")]
    public void TryParseVersion_RejectsATagThatIsNotAVersion(string tag)
    {
        Assert.False(GitHubReleaseChecker.TryParseVersion(tag, out _));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public string Body { get; init; } = "{}";

        public int Requests { get; private set; }

        public string LastUserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            LastUserAgent = request.Headers.UserAgent.ToString();

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
