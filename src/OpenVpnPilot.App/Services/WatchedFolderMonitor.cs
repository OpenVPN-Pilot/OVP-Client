using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Keeps the profile store in step with the directories the user asked to watch.
/// </summary>
/// <remarks>
/// Two things have to happen for a watched directory to be useful. Files added while the application
/// runs are noticed by the file system watcher, and files added while it was closed are found by a
/// rescan at startup: a watcher alone would silently miss everything that happened overnight.
///
/// A file that appears is not read immediately. Whatever wrote it may still be writing, and a
/// configuration read half way through would be imported as broken and then recorded as a duplicate
/// once it was complete. Changes are therefore collected and acted on after a short quiet period.
///
/// A configuration that is already stored is recognised by its content, so a rescan of a directory
/// that has not changed imports nothing and a file edited in place is offered again.
/// </remarks>
public sealed class WatchedFolderMonitor : IAsyncDisposable
{
    /// <summary>
    /// How long a directory has to stay quiet before its changes are acted on.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    private readonly IWatchedFolderStore store;
    private readonly IProfileImportService importer;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WatchedFolderMonitor> logger;

    private readonly ConcurrentDictionary<Guid, FileSystemWatcher> watchers = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();

    private bool disposed;

    public WatchedFolderMonitor(
        IWatchedFolderStore store,
        IProfileImportService importer,
        TimeProvider timeProvider,
        ILogger<WatchedFolderMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.store = store;
        this.importer = importer;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Raised after an automatic import, with how many profiles were added.
    /// </summary>
    public event EventHandler<WatchedFolderImport>? Imported;

    /// <summary>
    /// Scans every watched directory and starts watching them.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (WatchedFolderRecord folder in await store.GetAllAsync(cancellationToken))
        {
            await ScanAsync(folder, cancellationToken);
            StartWatching(folder);
        }
    }

    /// <summary>
    /// Re-reads the configured directories, which a settings screen calls after a change.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        StopWatching();
        await StartAsync(cancellationToken);
    }

    /// <summary>
    /// Imports whatever a directory currently holds that the store does not.
    /// </summary>
    public async Task<int> ScanAsync(
        WatchedFolderRecord folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (!Directory.Exists(folder.Path))
        {
            WatchedFolderLog.DirectoryMissing(logger, folder.Path);
            return 0;
        }

        await scanGate.WaitAsync(cancellationToken);

        try
        {
            using ImportSelection selection = await importer.ExpandAsync([folder.Path], cancellationToken);

            if (selection.Files.Count == 0)
            {
                await store.MarkScannedAsync(folder.Id, cancellationToken);
                return 0;
            }

            IReadOnlyList<ImportCandidate> candidates =
                await importer.PrepareAsync(selection.Files, cancellationToken);

            int importable = candidates.Count(candidate => candidate.Outcome == ImportOutcome.Importable);

            if (importable == 0 || !folder.AutoImport)
            {
                await store.MarkScannedAsync(folder.Id, cancellationToken);

                if (importable > 0)
                {
                    // The user asked to be told rather than to have it done for them.
                    WatchedFolderLog.AwaitingConfirmation(logger, folder.Path, importable);
                }

                return 0;
            }

            int created = await importer.CommitAsync(
                candidates,
                folder.TargetFolderId,
                [],
                cancellationToken);

            await store.MarkScannedAsync(folder.Id, cancellationToken);

            if (created > 0)
            {
                WatchedFolderLog.Imported(logger, folder.Path, created);
                Imported?.Invoke(this, new WatchedFolderImport(folder.Path, created));
            }

            return created;
        }
        catch (IOException exception)
        {
            // A directory on a network share can disappear between the check and the read.
            WatchedFolderLog.ScanFailed(logger, folder.Path, exception);
            return 0;
        }
        catch (UnauthorizedAccessException exception)
        {
            WatchedFolderLog.ScanFailed(logger, folder.Path, exception);
            return 0;
        }
        finally
        {
            scanGate.Release();
        }
    }

    private void StartWatching(WatchedFolderRecord folder)
    {
        if (!Directory.Exists(folder.Path))
        {
            return;
        }

        try
        {
            FileSystemWatcher watcher = new(folder.Path, "*.ovpn")
            {
                IncludeSubdirectories = folder.IsRecursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            watcher.Created += (_, _) => Schedule(folder);
            watcher.Changed += (_, _) => Schedule(folder);
            watcher.Renamed += (_, _) => Schedule(folder);

            // A watcher that stops reporting is worse than none, because the directory would look
            // watched and quietly not be.
            watcher.Error += (_, args) => WatchedFolderLog.WatcherFailed(
                logger,
                folder.Path,
                args.GetException());

            watcher.EnableRaisingEvents = true;
            watchers[folder.Id] = watcher;
        }
        catch (IOException exception)
        {
            WatchedFolderLog.WatcherFailed(logger, folder.Path, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            WatchedFolderLog.WatcherFailed(logger, folder.Path, exception);
        }
    }

    /// <summary>
    /// Queues a scan once the directory has been quiet for a moment.
    /// </summary>
    private void Schedule(WatchedFolderRecord folder) => _ = SettleAndScanAsync(folder);

    private async Task SettleAndScanAsync(WatchedFolderRecord folder)
    {
        try
        {
            await Task.Delay(SettleDelay, timeProvider, lifetime.Token);
            await ScanAsync(folder, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void StopWatching()
    {
        foreach (Guid id in watchers.Keys.ToList())
        {
            if (watchers.TryRemove(id, out FileSystemWatcher? watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync();

        StopWatching();

        lifetime.Dispose();
        scanGate.Dispose();
    }
}

/// <summary>
/// Reports what an automatic import added.
/// </summary>
public sealed record WatchedFolderImport(string Path, int Count);

/// <summary>
/// Source generated log messages for <see cref="WatchedFolderMonitor"/>.
/// </summary>
internal static partial class WatchedFolderLog
{
    [LoggerMessage(
        EventId = 3400,
        Level = LogLevel.Information,
        Message = "Imported {Count} profile(s) from the watched directory {Path}.")]
    public static partial void Imported(ILogger logger, string path, int count);

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Information,
        Message = "The watched directory {Path} holds {Count} new profile(s) awaiting confirmation.")]
    public static partial void AwaitingConfirmation(ILogger logger, string path, int count);

    [LoggerMessage(
        EventId = 3402,
        Level = LogLevel.Warning,
        Message = "The watched directory {Path} does not exist and was skipped.")]
    public static partial void DirectoryMissing(ILogger logger, string path);

    [LoggerMessage(
        EventId = 3403,
        Level = LogLevel.Warning,
        Message = "The watched directory {Path} could not be scanned.")]
    public static partial void ScanFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(
        EventId = 3404,
        Level = LogLevel.Warning,
        Message = "Watching {Path} failed. Files added there will not be noticed until the next start.")]
    public static partial void WatcherFailed(ILogger logger, string path, Exception exception);
}
