using System.Text.Json;

namespace OpenVpnPilot.App.Services.Library;

/// <summary>
/// The shared file itself, in a folder that other machines write to as well.
/// </summary>
/// <remarks>
/// The folder is expected to be one a sync client keeps in step, such as a SharePoint library
/// synchronised by OneDrive, so everything here assumes that another machine can write the same file
/// at any moment, and that what it wrote may arrive here seconds later.
///
/// Three things follow. Writing goes to a temporary file beside the real one and is moved over it,
/// so nobody ever reads half a file. A lock file beside it tells a machine that is about to write that
/// another one is in the middle of doing so; a sync client carries it with a delay, so it narrows the
/// window rather than closing it, and what closes it is that the file is read again under the lock
/// and a write is abandoned when it changed since it was merged. And a lock left by a machine that
/// crashed or lost its connection is taken over once it is old enough that nobody can still be
/// writing.
/// </remarks>
public sealed class SharedLibraryFile
{
    /// <summary>
    /// How old a lock has to be before it is taken to belong to nobody.
    /// </summary>
    public static readonly TimeSpan StaleLock = TimeSpan.FromMinutes(2);

    private const int ReadAttempts = 5;

    private readonly TimeProvider timeProvider;

    public SharedLibraryFile(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = System.IO.Path.GetFullPath(path);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Path { get; }

    public string LockPath => Path + ".lock";

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? Path;

    /// <summary>
    /// True when the folder is there, which is what separates a missing file from a missing share.
    /// </summary>
    public bool FolderExists => Directory.Exists(Folder);

    /// <summary>
    /// The size and time of the file, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Read from the directory rather than the file, so a file the sync client keeps only online is
    /// not downloaded just to learn that it has not changed.
    /// </remarks>
    public SharedFileStamp? Probe()
    {
        FileInfo info = new(Path);
        info.Refresh();

        return info.Exists ? new SharedFileStamp(info.Length, info.LastWriteTimeUtc) : null;
    }

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(
                    Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 81920,
                    useAsync: true);

                using MemoryStream buffer = new();
                await stream.CopyToAsync(buffer, cancellationToken);
                return buffer.ToArray();
            }
            catch (IOException) when (attempt < ReadAttempts && File.Exists(Path))
            {
                // Held for a moment by whoever is writing it, or by the sync client replacing it.
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), timeProvider, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Writes the file whole, by moving a complete temporary copy over it.
    /// </summary>
    /// <param name="replace">False to refuse when the file already exists, for creating a new library.</param>
    public async Task WriteAsync(byte[] content, bool replace = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        string temporary = System.IO.Path.Combine(
            Folder,
            "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, Path, overwrite: replace);
                    return;
                }
                catch (IOException) when (replace && attempt < ReadAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), timeProvider, cancellationToken);
                }
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Takes the lock beside the file, waiting for another machine's for at most as long as given.
    /// </summary>
    /// <exception cref="SharedLibraryLockedException">Another machine still holds it.</exception>
    public async Task<IAsyncDisposable> LockAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + wait;
        string token = Guid.NewGuid().ToString("N");

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using (FileStream stream = new(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        new LockRecord(token, Environment.MachineName, timeProvider.GetUtcNow()),
                        cancellationToken: cancellationToken);
                }

                return new Held(LockPath, token);
            }
            catch (IOException) when (File.Exists(LockPath))
            {
                LockRecord? holder = TryReadLock();
                DateTimeOffset since = holder?.Since ?? new DateTimeOffset(File.GetLastWriteTimeUtc(LockPath), TimeSpan.Zero);

                if (timeProvider.GetUtcNow() - since > StaleLock)
                {
                    // Left by a machine that stopped before it could remove it.
                    TryDelete(LockPath);
                    continue;
                }

                if (timeProvider.GetUtcNow() >= deadline)
                {
                    throw new SharedLibraryLockedException("Another machine is writing the shared library.")
                    {
                        Holder = holder?.Holder ?? string.Empty,
                    };
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(4000, 250 * (1 << Math.Min(attempt, 4)))), timeProvider, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Copies of the file a sync client kept because two machines changed it at once.
    /// </summary>
    /// <remarks>
    /// OneDrive keeps the losing side of a simultaneous change beside the file under the file's name
    /// with the machine's name appended. What was in it is also in the library unless both writes
    /// crossed within seconds, so it is reported rather than read: whoever owns the folder decides.
    /// </remarks>
    public IReadOnlyList<string> ConflictCopies(DateTimeOffset newerThan)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(Path);
        string extension = System.IO.Path.GetExtension(Path);

        try
        {
            return
            [
                .. Directory
                    .EnumerateFiles(Folder, stem + "-*" + extension)
                    .Where(candidate => File.GetLastWriteTimeUtc(candidate) > newerThan.UtcDateTime)
                    .Select(candidate => System.IO.Path.GetFileName(candidate))
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder went away while it was listed. The next synchronisation will say so.
            return [];
        }
    }

    private LockRecord? TryReadLock()
    {
        try
        {
            return JsonSerializer.Deserialize<LockRecord>(File.ReadAllBytes(LockPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Being written or removed right now, or not ours to read. Its age decides instead.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the next attempt, which treats a leftover the same way.
        }
    }

    private sealed record LockRecord(string Token, string Holder, DateTimeOffset Since);

    private sealed class Held(string path, string token) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try
            {
                // Removed only when it is still this machine's, so a lock taken over as stale by
                // another machine is not pulled out from under it.
                LockRecord? current = JsonSerializer.Deserialize<LockRecord>(File.ReadAllBytes(path));

                if (current?.Token == token)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Gone already, or unreadable. Either way it ages into a stale lock.
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// The size and modification time of the shared file, enough to tell that it has not changed.
/// </summary>
public sealed record SharedFileStamp(long Length, DateTime LastWriteUtc);

/// <summary>
/// Another machine holds the lock on the shared file.
/// </summary>
public sealed class SharedLibraryLockedException : IOException
{
    public SharedLibraryLockedException()
    {
    }

    public SharedLibraryLockedException(string message)
        : base(message)
    {
    }

    public SharedLibraryLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The name of the machine that holds the lock, when its lock file could be read.
    /// </summary>
    public string Holder { get; init; } = string.Empty;
}
