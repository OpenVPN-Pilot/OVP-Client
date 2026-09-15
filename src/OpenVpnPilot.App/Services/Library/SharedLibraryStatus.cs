using OpenVpnPilot.Data.Library;

namespace OpenVpnPilot.App.Services.Library;

/// <summary>
/// Where the shared library stands.
/// </summary>
/// <param name="Condition">What the last attempt found.</param>
/// <param name="SynchronisedAt">When the library was last reconciled, or null when it never was.</param>
/// <param name="WaitingSince">
/// When changes made here first could not be written to the shared file, or null when none are waiting.
/// </param>
/// <param name="Detail">What the system said, for a condition that has something to add.</param>
public sealed record SharedLibraryStatus(
    SharedLibraryCondition Condition,
    DateTimeOffset? SynchronisedAt = null,
    DateTimeOffset? WaitingSince = null,
    string? Detail = null)
{
    public static SharedLibraryStatus NotShared { get; } = new(SharedLibraryCondition.NotShared);

    public bool IsShared => Condition != SharedLibraryCondition.NotShared;

    /// <summary>
    /// True for a condition somebody should be told about.
    /// </summary>
    public bool IsProblem => Condition is not (SharedLibraryCondition.NotShared
        or SharedLibraryCondition.Synchronised
        or SharedLibraryCondition.Synchronising);

    /// <summary>
    /// True when the only way forward is a passphrase from the person at this machine.
    /// </summary>
    public bool NeedsPassphrase => Condition is SharedLibraryCondition.PassphraseNeeded
        or SharedLibraryCondition.PassphraseRejected;
}

public enum SharedLibraryCondition
{
    /// <summary>
    /// No shared library is configured.
    /// </summary>
    NotShared,

    Synchronising,

    /// <summary>
    /// This machine and the shared file agree.
    /// </summary>
    Synchronised,

    /// <summary>
    /// The folder the file lives in cannot be reached, for example without a network.
    /// </summary>
    Unreachable,

    /// <summary>
    /// The folder is there and the file is not. It is not written again on its own, because the
    /// sync client may not have delivered it yet, or somebody may have removed it on purpose.
    /// </summary>
    FileMissing,

    /// <summary>
    /// No passphrase is stored on this machine.
    /// </summary>
    PassphraseNeeded,

    /// <summary>
    /// The stored passphrase no longer opens the file: it was changed, or the file was altered.
    /// </summary>
    PassphraseRejected,

    /// <summary>
    /// Another machine held the lock for longer than this one waited.
    /// </summary>
    Locked,

    /// <summary>
    /// A newer version wrote the file, and this one does not write back what it cannot fully read.
    /// </summary>
    TooNew,

    /// <summary>
    /// The file opened but its contents could not be read.
    /// </summary>
    Damaged,

    /// <summary>
    /// Anything else the file system or the store refused.
    /// </summary>
    Failed,
}

/// <summary>
/// What reconciling with the shared library changed on this machine.
/// </summary>
public sealed record SharedLibraryReport(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Removed,
    int CredentialsChanged,
    IReadOnlyList<LibraryConflict> Conflicts,
    int DeferredDeletions,
    IReadOnlyList<string> ConflictCopies)
{
    public bool ChangedHere => Added.Count > 0 || Updated.Count > 0 || Removed.Count > 0 || CredentialsChanged > 0;

    public bool IsWorthTelling => ChangedHere || Conflicts.Count > 0 || ConflictCopies.Count > 0;
}
