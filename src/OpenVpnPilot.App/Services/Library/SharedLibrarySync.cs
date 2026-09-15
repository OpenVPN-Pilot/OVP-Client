using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Library;
using OpenVpnPilot.Data.Packaging;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services.Library;

/// <summary>
/// Keeps this machine's profile library in step with a shared file.
/// </summary>
/// <remarks>
/// The shared file is an encrypted package in a folder other machines write to as well, usually a
/// SharePoint library a sync client keeps on every disk. This machine always works on its own store,
/// so it starts, connects and edits without the folder, and reconciles with the file whenever it can:
/// on start, a few seconds after something here changed, every minute to hear about changes made
/// elsewhere, when asked, and once more on the way out. What was changed here while the file could
/// not be reached is not lost and not overwritten when it can be reached again, because reconciling
/// is a three way merge against what the file held last time rather than a download.
///
/// A synchronisation reads the file, merges it into the store, and writes the file back only when the
/// merge produced something the file does not already hold. The write happens under a lock beside
/// the file and only when the file is still what was merged; when another machine wrote it in the
/// meantime, the merge is simply done again against what it wrote. Unchanged size and time, and an
/// unchanged store, are enough to skip reading the file on the regular checks.
///
/// The passphrase is entered once on each machine and kept in the operating system's protected
/// storage beside the sign ins. When it stops opening the file, somebody changed it, and this
/// machine asks again rather than guessing.
///
/// A failed attempt is retried on its own, sooner at first and at most every five minutes, and what
/// is waiting to be written is recorded, so the next start reconciles before anything else happens.
/// </remarks>
public sealed class SharedLibrarySync : IAsyncDisposable
{
    // Short, so that a change made here shows as being synchronised while it is still in mind. What
    // is checked is a digest of a few columns, which costs next to nothing.
    private static readonly TimeSpan LocalCheckInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RemoteCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many times a merge is redone because the file changed while it was being merged.
    /// </summary>
    private const int ReconcileAttempts = 3;

    /// <summary>
    /// How many steps are kept for a person to look back on.
    /// </summary>
    private const int ActivityKept = 200;

    private static readonly JsonSerializerOptions RecordOptions = new() { WriteIndented = true };

    private readonly ISettingsService settings;
    private readonly IDbContextFactory<PilotDbContext> contexts;
    private readonly ObservedSecretStore secrets;
    private readonly ConnectionManager connections;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SharedLibrarySync> logger;
    private readonly string recordDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly LinkedList<SharedLibraryActivity> activity = new();
    private readonly Lock activityGate = new();

    private Task? loop;
    private int secretChanges;
    private int secretChangesSynchronised;
    private int failures;
    private DateTimeOffset retryAfter = DateTimeOffset.MinValue;
    private bool reconciling;
    private bool disposed;

    public SharedLibrarySync(
        ISettingsService settings,
        IDbContextFactory<PilotDbContext> contexts,
        ObservedSecretStore secrets,
        ConnectionManager connections,
        IApplicationPaths paths,
        TimeProvider timeProvider,
        ILogger<SharedLibrarySync> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.settings = settings;
        this.contexts = contexts;
        this.secrets = secrets;
        this.connections = connections;
        this.timeProvider = timeProvider;
        this.logger = logger;

        recordDirectory = Path.Combine(paths.DataDirectory, "library");
        ActiveProfiles = () => connections.ActiveProfiles;
    }

    /// <summary>
    /// Raised whenever the status changes, on whichever thread noticed.
    /// </summary>
    public event EventHandler<SharedLibraryStatus>? StatusChanged;

    /// <summary>
    /// Raised after a synchronisation that changed this machine's library, found a conflict or found
    /// a conflicting copy the sync client kept.
    /// </summary>
    public event EventHandler<SharedLibraryReport>? Reconciled;

    /// <summary>
    /// Raised for every step recorded, on whichever thread took it.
    /// </summary>
    public event EventHandler<SharedLibraryActivity>? ActivityRecorded;

    /// <summary>
    /// The steps taken since the application started, oldest first, up to a limit.
    /// </summary>
    public IReadOnlyList<SharedLibraryActivity> RecentActivity
    {
        get
        {
            lock (activityGate)
            {
                return [.. activity];
            }
        }
    }

    public SharedLibraryStatus Status { get; private set; } = SharedLibraryStatus.NotShared;

    /// <summary>
    /// How long another machine's lock is waited for before trying again later.
    /// </summary>
    /// <remarks>
    /// Long enough for a write in progress to finish over a slow connection.
    /// </remarks>
    internal TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The profiles whose tunnel runs now, which a deletion made elsewhere waits for.
    /// </summary>
    internal Func<IReadOnlyCollection<Guid>> ActiveProfiles { get; init; }

    public string? SharedPath => settings.Current.Library.SharedPath is { Length: > 0 } path ? path : null;

    private string RecordPath => Path.Combine(recordDirectory, "state.json");

    private string AncestorPath => Path.Combine(recordDirectory, "ancestor.ovppkg");

    /// <summary>
    /// Starts reconciling in the background: once now, then whenever something is due.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (loop is not null)
        {
            return;
        }

        secrets.ProfileSecretsChanged += OnSecretsChanged;
        loop = Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None);
    }

    /// <summary>
    /// Reconciles now, reading the file whatever its size and time say.
    /// </summary>
    public async Task<SharedLibraryStatus> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            return await SynchroniseAsync(quickCheck: false, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reconciles once more when something made here has not reached the file yet, for the way out.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (SharedPath is null)
        {
            return;
        }

        if (Status.WaitingSince is not null || await DueHereAsync(LoadRecord(SharedPath), cancellationToken))
        {
            await SyncNowAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Starts sharing this machine's library as a new file.
    /// </summary>
    /// <exception cref="SharedLibraryExistsException">A file is already there.</exception>
    public async Task<SharedLibraryStatus> CreateAsync(string path, string passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        SharedLibraryFile file = new(path, timeProvider);

        if (File.Exists(file.Path))
        {
            throw new SharedLibraryExistsException(file.Path);
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            ProfilePackageContent content;

            await using (PilotDbContext context = await contexts.CreateDbContextAsync(cancellationToken))
            {
                content = await new LibraryMerger(context, secrets, timeProvider).ReadLocalAsync(cancellationToken);
            }

            byte[] encoded = ProfilePackageFile.Encode(content, passphrase);

            try
            {
                await file.WriteAsync(encoded, replace: false, cancellationToken);
            }
            catch (IOException) when (File.Exists(file.Path))
            {
                // Another machine created one in the moment between looking and writing.
                throw new SharedLibraryExistsException(file.Path);
            }

            await secrets.WriteAsync(SecretReference.LibraryPassphrase, new StoredSecret(null, passphrase), cancellationToken);

            SyncRecord record = new() { Path = file.Path };
            await CompleteAsync(file, record, encoded, cancellationToken);
            await settings.UpdateAsync(current => current.Library.SharedPath = file.Path, cancellationToken);

            Record(SharedLibraryActivityKind.Created, content.Profiles.Count);
            Record(SharedLibraryActivityKind.Written, (long)encoded.Length);

            return Publish(SharedLibraryCondition.Synchronised, record);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Starts using an existing shared file in place of this machine's own library.
    /// </summary>
    /// <remarks>
    /// The profiles this machine had are replaced, not added to the file. A machine uses a shared
    /// library or its own, and a mixture would put profiles in front of everybody that one person
    /// kept for themselves. The screen that leads here says so and offers an export first.
    /// </remarks>
    /// <exception cref="CryptographicException">The passphrase does not open the file.</exception>
    /// <exception cref="PackageTooNewException">A newer version wrote the file.</exception>
    /// <exception cref="SharedLibraryInUseException">A tunnel is connected.</exception>
    /// <exception cref="InvalidOperationException">The file is not a package.</exception>
    public async Task<SharedLibraryStatus> JoinAsync(string path, string passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        // A connected profile may be one that is about to be replaced, and a tunnel whose profile is
        // gone can neither reconnect nor be found in the list to disconnect.
        if (ActiveProfiles().Count > 0)
        {
            throw new SharedLibraryInUseException();
        }

        SharedLibraryFile file = new(path, timeProvider);

        // Taken before reading, so a file written in between reads as changed at the next check.
        SharedFileStamp? stamp = file.Probe();

        // Opened first, so a wrong passphrase is said on the spot and nothing is stored or changed.
        byte[] bytes = await file.ReadAsync(cancellationToken);
        ProfilePackageContent remote = ProfilePackageFile.Decode(bytes, passphrase);

        await gate.WaitAsync(cancellationToken);

        try
        {
            LibraryMergeResult result;

            await using (PilotDbContext context = await contexts.CreateDbContextAsync(cancellationToken))
            {
                reconciling = true;

                try
                {
                    result = await new LibraryMerger(context, secrets, timeProvider).AdoptAsync(remote, cancellationToken);
                }
                finally
                {
                    reconciling = false;
                }
            }

            await secrets.WriteAsync(SecretReference.LibraryPassphrase, new StoredSecret(null, passphrase), cancellationToken);
            ForgetRecord();

            SyncRecord record = new() { Path = file.Path };
            await CompleteAsync(file, record, bytes, cancellationToken, stamp);
            secretChangesSynchronised = secretChanges;
            await settings.UpdateAsync(current => current.Library.SharedPath = file.Path, cancellationToken);

            Record(SharedLibraryActivityKind.Read, (long)bytes.Length);
            Record(SharedLibraryActivityKind.Joined, remote.Profiles.Count, result.Removed.Count);

            SharedLibraryReport report = new(
                result.Added,
                result.Updated,
                result.Removed,
                result.CredentialsChanged,
                [],
                0,
                []);

            if (report.IsWorthTelling)
            {
                Reconciled?.Invoke(this, report);
            }

            return Publish(SharedLibraryCondition.Synchronised, record);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// How many profiles this machine holds, which joining a library would replace.
    /// </summary>
    public async Task<int> CountLocalProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.Profiles.CountAsync(cancellationToken);
    }

    /// <summary>
    /// Stores the passphrase the file now opens with, after checking that it does.
    /// </summary>
    /// <exception cref="CryptographicException">The passphrase does not open the file.</exception>
    public async Task<SharedLibraryStatus> ProvidePassphraseAsync(string passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        string path = SharedPath ?? throw new InvalidOperationException("No shared library is configured.");
        SharedLibraryFile file = new(path, timeProvider);

        ProfilePackageFile.Decode(await file.ReadAsync(cancellationToken), passphrase);

        await secrets.WriteAsync(SecretReference.LibraryPassphrase, new StoredSecret(null, passphrase), cancellationToken);
        ResetRetry();
        Record(SharedLibraryActivityKind.PassphraseStored);

        return await SyncNowAsync(cancellationToken);
    }

    /// <summary>
    /// Encrypts the shared file with a new passphrase, which every other machine is then asked for.
    /// </summary>
    /// <remarks>
    /// What someone who left the team can still open is the file as it was, and older versions of
    /// it the folder may keep. Changing the passphrase stops them opening anything written from now on.
    /// </remarks>
    public async Task<SharedLibraryStatus> ChangePassphraseAsync(string passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        await gate.WaitAsync(cancellationToken);

        try
        {
            SharedLibraryStatus status = await SynchroniseAsync(quickCheck: false, cancellationToken);

            if (status.Condition != SharedLibraryCondition.Synchronised || SharedPath is not { } path)
            {
                return status;
            }

            string old = (await secrets.TryReadAsync(SecretReference.LibraryPassphrase, cancellationToken))?.Password
                ?? throw new InvalidOperationException("No passphrase is stored.");

            SharedLibraryFile file = new(path, timeProvider);
            SyncRecord record = LoadRecord(path);

            await using (await file.LockAsync(LockWait, cancellationToken))
            {
                byte[] current = await file.ReadAsync(cancellationToken);

                if (!string.Equals(Hash(current), record.RemoteHash, StringComparison.Ordinal))
                {
                    // Written by another machine since the synchronisation a moment ago. Nothing is
                    // changed, and asking again reconciles with what it wrote first.
                    return Publish(SharedLibraryCondition.Locked, record);
                }

                byte[] encoded = ProfilePackageFile.Encode(ProfilePackageFile.Decode(current, old), passphrase);
                await file.WriteAsync(encoded, replace: true, cancellationToken);
                await secrets.WriteAsync(SecretReference.LibraryPassphrase, new StoredSecret(null, passphrase), cancellationToken);
                await CompleteAsync(file, record, encoded, cancellationToken);
                Record(SharedLibraryActivityKind.PassphraseChanged);
            }

            return Publish(SharedLibraryCondition.Synchronised, record);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stops sharing. The passphrase and the record go; the profiles stay or go as asked.
    /// </summary>
    /// <param name="keepProfiles">
    /// True to go on with the shared profiles as this machine's own library, false to start again
    /// with an empty one. The shared file is left alone either way.
    /// </param>
    /// <exception cref="SharedLibraryInUseException">Removing was asked for while a tunnel is connected.</exception>
    public async Task LeaveAsync(bool keepProfiles, CancellationToken cancellationToken = default)
    {
        if (!keepProfiles && ActiveProfiles().Count > 0)
        {
            throw new SharedLibraryInUseException();
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            int removed = 0;

            if (!keepProfiles)
            {
                removed = await RemoveLocalLibraryAsync(cancellationToken);
            }

            await settings.UpdateAsync(current => current.Library.SharedPath = null, cancellationToken);
            await secrets.DeleteAsync(SecretReference.LibraryPassphrase, cancellationToken);
            ForgetRecord();
            ResetRetry();

            if (keepProfiles)
            {
                Record(SharedLibraryActivityKind.LeftKeepingProfiles);
            }
            else
            {
                Record(SharedLibraryActivityKind.LeftRemovingProfiles, removed);
            }

            Status = SharedLibraryStatus.NotShared;
            StatusChanged?.Invoke(this, Status);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SyncNowAsync(cancellationToken);

            using PeriodicTimer timer = new(LocalCheckInterval, timeProvider);
            DateTimeOffset lastRemoteCheck = timeProvider.GetUtcNow();

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (SharedPath is not { } path)
                {
                    if (Status.IsShared)
                    {
                        Status = SharedLibraryStatus.NotShared;
                        StatusChanged?.Invoke(this, Status);
                    }

                    continue;
                }

                DateTimeOffset now = timeProvider.GetUtcNow();

                if (now < retryAfter || Status.NeedsPassphrase)
                {
                    continue;
                }

                bool remoteDue = now - lastRemoteCheck >= RemoteCheckInterval;

                if (!remoteDue && !await DueHereAsync(LoadRecord(path), cancellationToken))
                {
                    continue;
                }

                lastRemoteCheck = now;

                await gate.WaitAsync(cancellationToken);

                try
                {
                    await SynchroniseAsync(quickCheck: true, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The application is closing, and the way out has its own last attempt.
        }
    }

    /// <summary>
    /// Removes every profile, its sign ins and the tags nothing carries any more.
    /// </summary>
    /// <remarks>
    /// Sign ins first: a sign in left behind by a profile that is gone belongs to nothing that could
    /// ever use it or clear it.
    /// </remarks>
    private async Task<int> RemoveLocalLibraryAsync(CancellationToken cancellationToken)
    {
        foreach (string reference in await secrets.ListAsync(cancellationToken))
        {
            if (SecretReference.TryParse(reference, out _, out _))
            {
                await secrets.DeleteAsync(reference, cancellationToken);
            }
        }

        await using PilotDbContext context = await contexts.CreateDbContextAsync(cancellationToken);
        int removed = await context.Profiles.ExecuteDeleteAsync(cancellationToken);
        await context.Tags.ExecuteDeleteAsync(cancellationToken);

        return removed;
    }

    /// <summary>
    /// Keeps a step for the record, dropping the oldest once there are enough.
    /// </summary>
    private void Record(SharedLibraryActivityKind kind, params object[] arguments) =>
        Record(new SharedLibraryActivity(timeProvider.GetUtcNow(), kind, arguments));

    private void Record(SharedLibraryActivity entry)
    {
        lock (activityGate)
        {
            activity.AddLast(entry);

            while (activity.Count > ActivityKept)
            {
                activity.RemoveFirst();
            }
        }

        ActivityRecorded?.Invoke(this, entry);
    }

    /// <summary>
    /// One synchronisation. The caller holds the gate.
    /// </summary>
    private async Task<SharedLibraryStatus> SynchroniseAsync(bool quickCheck, CancellationToken cancellationToken)
    {
        if (SharedPath is not { } path)
        {
            Status = SharedLibraryStatus.NotShared;
            StatusChanged?.Invoke(this, Status);
            return Status;
        }

        SyncRecord record = LoadRecord(path);
        SharedLibraryFile file = new(path, timeProvider);

        try
        {
            string? passphrase = (await secrets.TryReadAsync(SecretReference.LibraryPassphrase, cancellationToken))?.Password;

            if (string.IsNullOrEmpty(passphrase))
            {
                return await FailAsync(SharedLibraryCondition.PassphraseNeeded, record, null, retry: false, cancellationToken);
            }

            if (!file.FolderExists)
            {
                SharedLibraryLog.Unreachable(logger, path, null);
                return await FailAsync(SharedLibraryCondition.Unreachable, record, null, retry: true, cancellationToken);
            }

            if (file.Probe() is not { } stamp)
            {
                SharedLibraryLog.FileMissing(logger, path);
                return await FailAsync(SharedLibraryCondition.FileMissing, record, null, retry: true, cancellationToken);
            }

            int secretsBefore = secretChanges;
            bool changedThere = record.RemoteHash is null
                || stamp.Length != record.RemoteLength
                || stamp.LastWriteUtc != record.RemoteWriteUtc;
            bool dueHere = await DueHereAsync(record, cancellationToken);

            if (quickCheck && !changedThere && record.WaitingSince is null && !dueHere)
            {
                return Publish(SharedLibraryCondition.Synchronised, record);
            }

            // Only what did something is recorded. A check every few seconds that finds nothing to do
            // would bury the steps somebody opens the record to find.
            Record(
                !quickCheck ? SharedLibraryActivityKind.Checking
                : dueHere ? SharedLibraryActivityKind.ChangedHere
                : changedThere ? SharedLibraryActivityKind.ChangedThere
                : SharedLibraryActivityKind.Checking);

            Publish(SharedLibraryCondition.Synchronising, record);

            for (int attempt = 1; attempt <= ReconcileAttempts; attempt++)
            {
                byte[] bytes = await file.ReadAsync(cancellationToken);
                string hash = Hash(bytes);
                Record(SharedLibraryActivityKind.Read, (long)bytes.Length);
                ProfilePackageContent remote = ProfilePackageFile.Decode(bytes, passphrase);
                ProfilePackageContent? ancestor = record.RemoteHash is null ? null : LoadAncestor(passphrase);

                LibraryMergeResult result;

                await using (PilotDbContext context = await contexts.CreateDbContextAsync(cancellationToken))
                {
                    reconciling = true;

                    try
                    {
                        result = await new LibraryMerger(context, secrets, timeProvider).MergeAsync(
                            remote,
                            ancestor,
                            ActiveProfiles().ToHashSet(),
                            cancellationToken);
                    }
                    finally
                    {
                        reconciling = false;
                    }
                }

                byte[] ancestorBytes = bytes;

                if (result.SharedChanged)
                {
                    byte[] encoded = ProfilePackageFile.Encode(result.Shared, passphrase);

                    await using (await file.LockAsync(LockWait, cancellationToken))
                    {
                        if (!string.Equals(Hash(await file.ReadAsync(cancellationToken)), hash, StringComparison.Ordinal))
                        {
                            SharedLibraryLog.ChangedMeanwhile(logger, path);
                            Record(SharedLibraryActivityKind.ChangedMeanwhile);
                            continue;
                        }

                        await file.WriteAsync(encoded, replace: true, cancellationToken);
                    }

                    Record(SharedLibraryActivityKind.Written, (long)encoded.Length);

                    ancestorBytes = encoded;
                }

                List<string> copies = NewConflictCopies(file, record);
                record.DeferredProfiles = [.. result.DeferredProfiles];
                await CompleteAsync(file, record, ancestorBytes, cancellationToken);
                secretChangesSynchronised = secretsBefore;

                SharedLibraryLog.Reconciled(
                    logger,
                    path,
                    result.Added.Count,
                    result.Updated.Count,
                    result.Removed.Count,
                    result.Conflicts.Count,
                    result.SharedChanged);

                SharedLibraryReport report = new(
                    result.Added,
                    result.Updated,
                    result.Removed,
                    result.CredentialsChanged,
                    result.Conflicts,
                    result.DeferredDeletions,
                    copies);

                if (result.ChangedHere)
                {
                    Record(
                        SharedLibraryActivityKind.Received,
                        result.Added.Count,
                        result.Updated.Count,
                        result.Removed.Count,
                        result.CredentialsChanged);
                }

                if (result.Conflicts.Count > 0)
                {
                    Record(SharedLibraryActivityKind.ConflictsResolved, result.Conflicts.Count);
                }

                if (copies.Count > 0)
                {
                    Record(SharedLibraryActivityKind.ConflictCopiesFound, string.Join(", ", copies));
                }

                if (!result.ChangedHere && !result.SharedChanged)
                {
                    Record(SharedLibraryActivityKind.UpToDate);
                }

                if (report.IsWorthTelling)
                {
                    Reconciled?.Invoke(this, report);
                }

                return Publish(SharedLibraryCondition.Synchronised, record);
            }

            // Written by another machine every time this one was about to. It is busy, so later.
            return await FailAsync(SharedLibraryCondition.Locked, record, null, retry: true, cancellationToken);
        }
        catch (SharedLibraryLockedException exception)
        {
            SharedLibraryLog.Locked(logger, path, exception.Holder);
            return await FailAsync(SharedLibraryCondition.Locked, record, exception.Holder, retry: true, cancellationToken);
        }
        catch (PackageTooNewException)
        {
            SharedLibraryLog.TooNew(logger, path);
            return await FailAsync(SharedLibraryCondition.TooNew, record, null, retry: true, cancellationToken);
        }
        catch (CryptographicException)
        {
            // Authenticated encryption: the passphrase changed, or the file was altered.
            SharedLibraryLog.PassphraseRejected(logger, path);
            return await FailAsync(SharedLibraryCondition.PassphraseRejected, record, null, retry: false, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return await FailAsync(SharedLibraryCondition.Damaged, record, exception.Message, retry: true, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What a share that went away, a sync client that is offline and a permission taken away
            // all look like from here.
            SharedLibraryLog.Unreachable(logger, path, exception);
            return await FailAsync(
                file.FolderExists && File.Exists(file.Path) ? SharedLibraryCondition.Unreachable : SharedLibraryCondition.FileMissing,
                record,
                exception.Message,
                retry: true,
                cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            SharedLibraryLog.Failed(logger, path, exception);
            return await FailAsync(SharedLibraryCondition.Failed, record, exception.GetBaseException().Message, retry: true, cancellationToken);
        }
    }

    /// <summary>
    /// Records a finished synchronisation: what the file holds now becomes the next ancestor.
    /// </summary>
    /// <param name="readStamp">
    /// The size and time the file had before it was read, when nothing was written since. Without
    /// one they are taken now, which is right after a write of this machine's own.
    /// </param>
    private async Task CompleteAsync(
        SharedLibraryFile file,
        SyncRecord record,
        byte[] ancestor,
        CancellationToken cancellationToken,
        SharedFileStamp? readStamp = null)
    {
        Directory.CreateDirectory(recordDirectory);
        await File.WriteAllBytesAsync(AncestorPath, ancestor, cancellationToken);

        SharedFileStamp? stamp = readStamp ?? file.Probe();

        record.Path = file.Path;
        record.RemoteHash = Hash(ancestor);
        record.RemoteLength = stamp?.Length ?? ancestor.Length;
        record.RemoteWriteUtc = stamp?.LastWriteUtc;
        record.SynchronisedAt = timeProvider.GetUtcNow();
        record.LocalFingerprint = await FingerprintAsync(cancellationToken);
        record.WaitingSince = null;

        SaveRecord(record);
        ResetRetry();
    }

    private async Task<SharedLibraryStatus> FailAsync(
        SharedLibraryCondition condition,
        SyncRecord record,
        string? detail,
        bool retry,
        CancellationToken cancellationToken)
    {
        if (retry)
        {
            failures++;
            TimeSpan wait = TimeSpan.FromTicks(Math.Min(
                LongestRetry.Ticks,
                FirstRetry.Ticks * (1L << Math.Min(failures - 1, 10))));

            retryAfter = timeProvider.GetUtcNow() + wait;
        }

        if (record.WaitingSince is null && await LocalChangedAsync(record, cancellationToken))
        {
            record.WaitingSince = timeProvider.GetUtcNow();
            SaveRecord(record);
        }

        SharedLibraryStatus status = Publish(condition, record, detail);
        Record(new SharedLibraryActivity(timeProvider.GetUtcNow(), SharedLibraryActivityKind.Failed, [], status));

        if (status.RetryAt is { } at)
        {
            Record(SharedLibraryActivityKind.RetryScheduled, at);
        }

        return status;
    }

    private SharedLibraryStatus Publish(SharedLibraryCondition condition, SyncRecord record, string? detail = null)
    {
        DateTimeOffset? retryAt = retryAfter > timeProvider.GetUtcNow() ? retryAfter : null;
        SharedLibraryStatus next = new(condition, record.SynchronisedAt, record.WaitingSince, detail, retryAt);

        if (next != Status)
        {
            Status = next;
            StatusChanged?.Invoke(this, next);
        }

        return next;
    }

    private void ResetRetry()
    {
        failures = 0;
        retryAfter = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// True when this machine has something to reconcile that looking at the file would not show.
    /// </summary>
    /// <remarks>
    /// A deletion that waited for a tunnel is one of those. The file already says the profile is
    /// gone and nothing here changes when the tunnel ends, so without asking about it the profile
    /// would stay until somebody happened to change something else.
    /// </remarks>
    private async Task<bool> DueHereAsync(SyncRecord record, CancellationToken cancellationToken) =>
        record.DeferredProfiles.Except(ActiveProfiles()).Any()
        || await LocalChangedAsync(record, cancellationToken);

    /// <summary>
    /// The same question for a test, about the record as it is stored.
    /// </summary>
    internal Task<bool> IsDueHereAsync(CancellationToken cancellationToken = default) =>
        SharedPath is { } path ? DueHereAsync(LoadRecord(path), cancellationToken) : Task.FromResult(false);

    /// <summary>
    /// True when this machine's library changed since the last synchronisation.
    /// </summary>
    private async Task<bool> LocalChangedAsync(SyncRecord record, CancellationToken cancellationToken) =>
        secretChanges != secretChangesSynchronised
        || record.LocalFingerprint is null
        || !string.Equals(await FingerprintAsync(cancellationToken), record.LocalFingerprint, StringComparison.Ordinal);

    /// <summary>
    /// A digest of everything shared about the profiles, cheap enough to take every few seconds.
    /// </summary>
    /// <remarks>
    /// Taken from the store rather than from what this application did, so a change made by the
    /// companion command in a terminal is noticed as well.
    /// </remarks>
    private async Task<string> FingerprintAsync(CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contexts.CreateDbContextAsync(cancellationToken);

        var profiles = await context.Profiles
            .AsNoTracking()
            .Select(profile => new
            {
                profile.Id,
                profile.Name,
                profile.ContentHash,
                profile.Notes,
                profile.ProtectRoutes,
                profile.Colour,
                Tags = profile.Tags.Select(link => link.Tag!.Name).ToList(),
            })
            .ToListAsync(cancellationToken);

        StringBuilder builder = new();

        foreach (var profile in profiles.OrderBy(profile => profile.Id))
        {
            builder
                .Append(profile.Id.ToString("N")).Append('')
                .Append(profile.Name).Append('')
                .Append(profile.ContentHash).Append('')
                .Append(profile.Notes).Append('')
                .Append(profile.ProtectRoutes?.ToString(CultureInfo.InvariantCulture)).Append('')
                .Append(profile.Colour).Append('')
                .AppendJoin(',', profile.Tags.Order(StringComparer.OrdinalIgnoreCase))
                .Append('');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static List<string> NewConflictCopies(SharedLibraryFile file, SyncRecord record)
    {
        List<string> fresh = [];

        foreach (string copy in file.ConflictCopies(record.SynchronisedAt ?? DateTimeOffset.MinValue))
        {
            if (!record.ReportedCopies.Contains(copy, StringComparer.OrdinalIgnoreCase))
            {
                record.ReportedCopies.Add(copy);
                fresh.Add(copy);
            }
        }

        return fresh;
    }

    private ProfilePackageContent? LoadAncestor(string passphrase)
    {
        try
        {
            return File.Exists(AncestorPath)
                ? ProfilePackageFile.Decode(File.ReadAllBytes(AncestorPath), passphrase)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            // Without an ancestor the merge still works, only coarser: the later version of a whole
            // profile wins where it would have taken each field from whoever changed it.
            SharedLibraryLog.RecordUnusable(logger, exception);
            return null;
        }
    }

    private SyncRecord LoadRecord(string path)
    {
        string full = Path.GetFullPath(path);

        try
        {
            if (File.Exists(RecordPath)
                && JsonSerializer.Deserialize<SyncRecord>(File.ReadAllBytes(RecordPath), RecordOptions) is { } record
                && string.Equals(record.Path, full, StringComparison.OrdinalIgnoreCase))
            {
                record.ReportedCopies ??= [];
                return record;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SharedLibraryLog.RecordUnusable(logger, exception);
        }

        // A record for another file, or none: this one has never been synchronised from here.
        return new SyncRecord { Path = full };
    }

    private void SaveRecord(SyncRecord record)
    {
        try
        {
            Directory.CreateDirectory(recordDirectory);
            string temporary = RecordPath + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(record, RecordOptions));
            File.Move(temporary, RecordPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next synchronisation reads the file again in full, which is slower and still right.
            SharedLibraryLog.RecordUnusable(logger, exception);
        }
    }

    private void ForgetRecord()
    {
        foreach (string path in new[] { RecordPath, AncestorPath })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SharedLibraryLog.RecordUnusable(logger, exception);
            }
        }
    }

    private void OnSecretsChanged(object? sender, EventArgs e)
    {
        // What a synchronisation itself writes is not a change to write back.
        if (!reconciling)
        {
            Interlocked.Increment(ref secretChanges);
        }
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        secrets.ProfileSecretsChanged -= OnSecretsChanged;
        await lifetime.CancelAsync();

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // A synchronisation in the middle of a slow write. The next start reconciles.
            }
        }

        lifetime.Dispose();
        gate.Dispose();
    }

    /// <summary>
    /// What this machine remembers about its last synchronisation with the shared file.
    /// </summary>
    private sealed class SyncRecord
    {
        public string? Path { get; set; }

        public string? RemoteHash { get; set; }

        public long RemoteLength { get; set; }

        public DateTime? RemoteWriteUtc { get; set; }

        public DateTimeOffset? SynchronisedAt { get; set; }

        public string? LocalFingerprint { get; set; }

        public DateTimeOffset? WaitingSince { get; set; }

        /// <summary>
        /// Profiles the file says are deleted and that stay here while their tunnel runs.
        /// </summary>
        public List<Guid> DeferredProfiles { get; set; } = [];

        public List<string> ReportedCopies { get; set; } = [];
    }
}

/// <summary>
/// A tunnel is connected, and what was asked for would replace or remove the profiles it belongs to.
/// </summary>
public sealed class SharedLibraryInUseException : InvalidOperationException
{
    public SharedLibraryInUseException()
        : base("Disconnect every tunnel before replacing or removing the profiles on this machine.")
    {
    }

    public SharedLibraryInUseException(string message)
        : base(message)
    {
    }

    public SharedLibraryInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A shared library cannot be created where a file already is.
/// </summary>
public sealed class SharedLibraryExistsException : IOException
{
    public SharedLibraryExistsException()
    {
    }

    public SharedLibraryExistsException(string message)
        : base(message)
    {
    }

    public SharedLibraryExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
