using System.Globalization;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Data.Library;

namespace OpenVpnPilot.App.Services.Library;

/// <summary>
/// Words what the shared library is doing, the same way wherever it is shown.
/// </summary>
public static class SharedLibraryText
{
    /// <summary>
    /// One sentence for a status, with what is waiting added when something is.
    /// </summary>
    public static string Describe(SharedLibraryStatus status, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localizer);

        string text = status switch
        {
            { Condition: SharedLibraryCondition.Synchronised, SynchronisedAt: { } at } =>
                localizer.Translate("library.condition.Synchronised", Time(at)),

            // The machine holding the lock is worth naming when the lock file says which it is,
            // because that is the machine to look at when the lock does not go away.
            { Condition: SharedLibraryCondition.Locked, Detail: { Length: > 0 } holder } =>
                localizer.Translate("library.lockedBy", holder),

            _ => localizer.Translate("library.condition." + status.Condition, status.Detail ?? string.Empty),
        };

        return status.WaitingSince is { } since
            ? text + " " + localizer.Translate("library.waiting", Time(since))
            : text;
    }

    /// <summary>
    /// What a synchronisation changed here, in one line for the status bar.
    /// </summary>
    public static string Summarise(SharedLibraryReport report, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);

        return localizer.Translate(
            "library.reconciled",
            report.Added.Count,
            report.Updated.Count,
            report.Removed.Count,
            report.CredentialsChanged);
    }

    /// <summary>
    /// The conflicts and conflict copies a synchronisation found, one sentence each, or empty.
    /// </summary>
    public static string Conflicts(SharedLibraryReport report, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);

        const int Shown = 3;
        List<string> sentences = [];

        foreach (LibraryConflict conflict in report.Conflicts.Take(Shown))
        {
            string key = conflict.Kind switch
            {
                LibraryConflictKind.ChangedOnBothSides => conflict.KeptHere
                    ? "library.conflict.bothKeptHere"
                    : "library.conflict.bothKeptThere",
                LibraryConflictKind.ChangedHereDeletedThere => "library.conflict.changedHereDeletedThere",
                _ => "library.conflict.deletedHereChangedThere",
            };

            sentences.Add(localizer.Translate(key, conflict.ProfileName));
        }

        if (report.Conflicts.Count > Shown)
        {
            sentences.Add(localizer.Translate("library.conflict.more", report.Conflicts.Count - Shown));
        }

        if (report.ConflictCopies.Count > 0)
        {
            sentences.Add(localizer.Translate("library.conflictCopies", string.Join(", ", report.ConflictCopies)));
        }

        return string.Join(" ", sentences);
    }

    /// <summary>
    /// The sentence for something an action on the shared file threw, or null when it is not one of
    /// the things such an action is expected to run into.
    /// </summary>
    public static string? Refusal(Exception exception, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(localizer);

        return exception switch
        {
            System.Security.Cryptography.CryptographicException => localizer["library.passphraseWrong"],
            OpenVpnPilot.Data.Packaging.PackageTooNewException => localizer["library.condition.TooNew"],
            SharedLibraryExistsException => localizer["library.exists"],
            SharedLibraryUnavailableException unavailable => unavailable.Message,
            InvalidOperationException invalid => localizer.Translate("library.notALibrary", invalid.Message),
            IOException or UnauthorizedAccessException => localizer.Translate("library.unreachable", exception.Message),
            _ => null,
        };
    }

    private static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>
/// The shared library could not be reached in the state an action needs it in.
/// </summary>
public sealed class SharedLibraryUnavailableException : InvalidOperationException
{
    public SharedLibraryUnavailableException()
    {
    }

    public SharedLibraryUnavailableException(string message)
        : base(message)
    {
    }

    public SharedLibraryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
