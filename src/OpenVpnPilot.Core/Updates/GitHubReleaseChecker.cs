using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OpenVpnPilot.Core.Updates;

/// <summary>
/// Reads the latest release of a GitHub repository and compares it with the running version.
/// </summary>
/// <remarks>
/// Only the release list is read, over the public interface, without credentials. Nothing is
/// downloaded and nothing is installed: the check reports that a newer version exists and leaves
/// what to do about it to the person reading it.
///
/// A tag is expected to be a version, optionally prefixed with a v. A tag that is not one is treated
/// as a check that could not be made rather than as being up to date, because the two mean different
/// things to whoever is waiting for a fix.
/// </remarks>
public sealed class GitHubReleaseChecker : IUpdateChecker
{
    private readonly HttpClient client;
    private readonly string? repository;
    private readonly Version currentVersion;

    /// <param name="repository">In the form owner/name. Null or empty disables the check.</param>
    /// <param name="currentVersion">The version this build reports.</param>
    public GitHubReleaseChecker(HttpClient client, string? repository, Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(currentVersion);

        this.client = client;
        this.repository = string.IsNullOrWhiteSpace(repository) ? null : repository.Trim('/', ' ');
        this.currentVersion = currentVersion;
    }

    public bool IsConfigured => repository is not null;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (repository is null)
        {
            return UpdateCheckResult.NotConfigured;
        }

        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                $"https://api.github.com/repos/{repository}/releases/latest");

            // The interface refuses a request without one, and it identifies the caller honestly.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
                "OpenVpnPilot",
                currentVersion.ToString(3)));

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A repository with no releases yet answers this way, which is not a fault.
                return UpdateCheckResult.UpToDate;
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(
                    "The release list answered with "
                    + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }

            ReleaseResponse? release = await response.Content.ReadFromJsonAsync<ReleaseResponse>(cancellationToken);

            if (release?.TagName is not { Length: > 0 } tag)
            {
                return UpdateCheckResult.Failed("The latest release has no tag.");
            }

            if (!TryParseVersion(tag, out Version? published))
            {
                return UpdateCheckResult.Failed($"The tag '{tag}' is not a version.");
            }

            return published > currentVersion
                ? UpdateCheckResult.Available(published, release.HtmlUrl)
                : UpdateCheckResult.UpToDate;
        }
        catch (HttpRequestException exception)
        {
            return UpdateCheckResult.Failed(exception.Message);
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Failed("The check timed out.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            return UpdateCheckResult.Failed(exception.Message);
        }
    }

    /// <summary>
    /// Reads a release tag such as v1.2.3, version-1.2.3 or 1.2.3 as a version.
    /// </summary>
    /// <remarks>
    /// The longer prefix is tested first and it has to be. Stripping a single leading v from
    /// "version-1.2.3" leaves "ersion-1.2.3", which is then truncated at the hyphen and read as a
    /// tag that is not a version at all. That is not hypothetical: it is the shape of every tag this
    /// project published before the convention changed, and the check reported each of them as a
    /// failure rather than as a release.
    /// </remarks>
    internal static bool TryParseVersion(string tag, out Version? version)
    {
        ArgumentNullException.ThrowIfNull(tag);

        string trimmed = tag.Trim();

        if (trimmed.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["version-".Length..];
        }
        else if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        // A pre-release suffix is not part of the version, and comparing it as one would be wrong.
        int suffix = trimmed.IndexOfAny(['-', '+']);

        if (suffix > 0)
        {
            trimmed = trimmed[..suffix];
        }

        return Version.TryParse(trimmed, out version);
    }

    /// <summary>
    /// The part of a release the check reads.
    /// </summary>
    private sealed record ReleaseResponse(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}
