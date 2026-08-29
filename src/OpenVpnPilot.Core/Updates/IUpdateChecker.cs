namespace OpenVpnPilot.Core.Updates;

/// <summary>
/// Asks whether a newer release exists.
/// </summary>
/// <remarks>
/// Checking is a request to a third party, so it is off unless the user turns it on and no
/// repository is assumed. An application that quietly contacts a server the user never named is not
/// something this project does.
/// </remarks>
public interface IUpdateChecker
{
    /// <summary>
    /// False when no repository is configured, in which case nothing is contacted.
    /// </summary>
    public bool IsConfigured { get; }

    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What a check found.
/// </summary>
/// <param name="Outcome">Whether an update exists, none does, or the check could not be made.</param>
/// <param name="LatestVersion">The version published, when one was found.</param>
/// <param name="ReleaseUrl">Where to read about it.</param>
/// <param name="Detail">
/// Why a check failed, in factual terms. Empty otherwise. The wording shown to the user is localized
/// in the presentation layer.
/// </param>
public sealed record UpdateCheckResult(
    UpdateOutcome Outcome,
    Version? LatestVersion = null,
    string? ReleaseUrl = null,
    string Detail = "")
{
    public static UpdateCheckResult NotConfigured { get; } = new(UpdateOutcome.NotConfigured);

    public static UpdateCheckResult UpToDate { get; } = new(UpdateOutcome.UpToDate);

    public static UpdateCheckResult Failed(string detail) => new(UpdateOutcome.Failed, Detail: detail);

    public static UpdateCheckResult Available(Version version, string? url) =>
        new(UpdateOutcome.UpdateAvailable, version, url);
}

public enum UpdateOutcome
{
    /// <summary>
    /// No repository was named, so nothing was contacted.
    /// </summary>
    NotConfigured,

    UpToDate,

    UpdateAvailable,

    /// <summary>
    /// The check could not be completed. Not knowing is different from being up to date.
    /// </summary>
    Failed,
}
