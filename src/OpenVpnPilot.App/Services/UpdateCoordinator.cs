using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Updates;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Asks GitHub whether a newer release exists, when the user has asked for that to happen.
/// </summary>
/// <remarks>
/// The checker itself was written, tested and never wired to anything. This is the wiring, and it is
/// where the two decisions that are not the checker's business live: whether to ask at all, and
/// which repository to ask about. Both come from the settings and both can change while the
/// application runs, so the checker is built per check rather than held.
///
/// Nothing is downloaded and nothing is installed. A newer release is reported with a link, and what
/// to do about it stays with the person reading it. An installer that replaces itself while tunnels
/// are running is a different feature with a much longer list of ways to go wrong.
/// </remarks>
public sealed class UpdateCoordinator : IDisposable
{
    /// <summary>
    /// How long a check may take. It runs at startup, behind the window, and must never be the
    /// reason something waits.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ISettingsService settings;
    private readonly ILogger<UpdateCoordinator> logger;
    private readonly HttpClient client = new() { Timeout = Timeout };
    private bool disposed;

    public UpdateCoordinator(ISettingsService settings, ILogger<UpdateCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>
    /// The version this build reports, to three parts.
    /// </summary>
    /// <remarks>
    /// Three rather than four. An assembly version always carries a revision and a release tag never
    /// does, so comparing them unequalled would make 1.2.0.0 newer than the tag v1.2.0 that produced
    /// it, and every build would report itself as being ahead of its own release.
    /// </remarks>
    public static Version CurrentVersion
    {
        get
        {
            Version version = typeof(UpdateCoordinator).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    /// <summary>
    /// True when the user has asked for the check and a repository is named.
    /// </summary>
    public bool IsEnabled =>
        settings.Current.Advanced.CheckForUpdates
        && !string.IsNullOrWhiteSpace(settings.Current.Advanced.UpdateRepository);

    /// <summary>
    /// Checks, whether or not the automatic check is switched on.
    /// </summary>
    /// <remarks>
    /// Used by the button in the settings screen, which is a person asking directly and therefore
    /// does not need the preference consulted. The startup check goes through
    /// <see cref="CheckIfEnabledAsync"/> instead.
    /// </remarks>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        GitHubReleaseChecker checker = new(
            client,
            settings.Current.Advanced.UpdateRepository,
            CurrentVersion);

        UpdateCheckResult result = await checker.CheckAsync(cancellationToken);

        UpdateLog.Checked(logger, result.Outcome.ToString(), result.LatestVersion?.ToString() ?? "-");

        return result;
    }

    /// <summary>
    /// Checks only when the user has switched it on, and reports nothing otherwise.
    /// </summary>
    public async Task<UpdateCheckResult> CheckIfEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return UpdateCheckResult.NotConfigured;
        }

        return await CheckAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        client.Dispose();
    }
}

/// <summary>
/// Source generated log messages for <see cref="UpdateCoordinator"/>.
/// </summary>
internal static partial class UpdateLog
{
    [LoggerMessage(
        EventId = 3500,
        Level = LogLevel.Information,
        Message = "The update check reported {Outcome}, latest {Version}.")]
    public static partial void Checked(ILogger logger, string outcome, string version);
}
