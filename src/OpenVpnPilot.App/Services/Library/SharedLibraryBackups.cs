using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App.Services.Library;

/// <summary>
/// Keeps earlier versions of the shared file: on this machine, and beside the file for everyone.
/// </summary>
/// <remarks>
/// There is no server behind a shared library that could keep its history, so every machine keeps
/// some. On its own disk it keeps the versions it saw, recent ones and one a day for a month, where
/// nothing that happens to the shared folder can reach them. Beside the file, in a folder of their
/// own, every machine keeps the version it last synchronised with and a note saying who it is and
/// when that was: a copy for everyone that survives the file itself being lost, and the answer to
/// which machines use the library.
///
/// Every copy is the shared file as it was, encrypted as it was. Nothing here is readable without
/// the passphrase, and nothing here is ever read back into the library except when somebody chooses
/// to restore it.
///
/// A copy that cannot be written is logged and skipped. Keeping history is worth a great deal, but
/// not a synchronisation that fails because a backup could not be kept.
/// </remarks>
internal sealed class SharedLibraryBackups
{
    /// <summary>
    /// The most recent versions kept whatever their age.
    /// </summary>
    internal const int RecentKept = 20;

    /// <summary>
    /// For how many days one version a day is kept beyond the recent ones.
    /// </summary>
    internal const int DaysKept = 30;

    private const string Extension = ".ovppkg";

    private static readonly JsonSerializerOptions PresenceOptions = new() { WriteIndented = true };

    private readonly string localDirectory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;

    public SharedLibraryBackups(string localDirectory, TimeProvider timeProvider, ILogger logger)
    {
        this.localDirectory = localDirectory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// The folder beside a shared file that holds every machine's copy of it.
    /// </summary>
    public static string MembersFolder(string libraryPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(libraryPath)) ?? string.Empty,
            Path.GetFileNameWithoutExtension(libraryPath) + ".backups");

    /// <summary>
    /// Keeps a version on this machine, once, and lets the oldest go beyond what is kept.
    /// </summary>
    public void KeepLocally(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            Directory.CreateDirectory(localDirectory);

            string hash = Convert.ToHexString(SHA256.HashData(content))[..16].ToLowerInvariant();

            if (Directory.EnumerateFiles(localDirectory, "*-" + hash + Extension).Any())
            {
                return;
            }

            string name = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + hash + Extension;

            File.WriteAllBytes(Path.Combine(localDirectory, name), content);

            foreach (string expendable in Expendable(
                Directory.EnumerateFiles(localDirectory, "*" + Extension).Select(path => (path, File.GetLastWriteTimeUtc(path))),
                timeProvider.GetUtcNow()))
            {
                File.Delete(expendable);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SharedLibraryLog.BackupNotKept(logger, localDirectory, exception);
        }
    }

    /// <summary>
    /// Which backups can go: everything that is neither among the most recent nor the newest of a
    /// day within the days kept.
    /// </summary>
    internal static IReadOnlyList<string> Expendable(IEnumerable<(string Path, DateTime WrittenUtc)> backups, DateTimeOffset now)
    {
        List<(string Path, DateTime WrittenUtc)> ordered = [.. backups.OrderByDescending(backup => backup.WrittenUtc)];
        HashSet<string> kept = new(ordered.Take(RecentKept).Select(backup => backup.Path), StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<DateTime, (string Path, DateTime WrittenUtc)> day in ordered
            .Where(backup => now.UtcDateTime - backup.WrittenUtc < TimeSpan.FromDays(DaysKept))
            .GroupBy(backup => backup.WrittenUtc.Date))
        {
            kept.Add(day.First().Path);
        }

        return [.. ordered.Where(backup => !kept.Contains(backup.Path)).Select(backup => backup.Path)];
    }

    /// <summary>
    /// Puts this machine's copy of the version it synchronised with beside the shared file, with the
    /// note saying who it is.
    /// </summary>
    public async Task ShareAsync(
        string libraryPath,
        byte[] content,
        SharedLibraryMember member,
        CancellationToken cancellationToken)
    {
        string folder = MembersFolder(libraryPath);

        try
        {
            Directory.CreateDirectory(folder);

            await new SharedLibraryFile(Path.Combine(folder, member.FileName + Extension), timeProvider)
                .WriteAsync(content, replace: true, cancellationToken);

            await WritePresenceAsync(folder, member, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SharedLibraryLog.BackupNotKept(logger, folder, exception);
        }
    }

    /// <summary>
    /// Notes that this machine stopped using the library. Its copy stays.
    /// </summary>
    public async Task MarkLeftAsync(string libraryPath, SharedLibraryMember member, CancellationToken cancellationToken)
    {
        string folder = MembersFolder(libraryPath);

        try
        {
            if (Directory.Exists(folder))
            {
                await WritePresenceAsync(folder, member with { LeftAt = timeProvider.GetUtcNow() }, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SharedLibraryLog.BackupNotKept(logger, folder, exception);
        }
    }

    /// <summary>
    /// The machines that left a note beside the file, the ones that stopped using it included.
    /// </summary>
    public IReadOnlyList<SharedLibraryMember> ReadMembers(string libraryPath)
    {
        string folder = MembersFolder(libraryPath);
        List<SharedLibraryMember> members = [];

        if (!Directory.Exists(folder))
        {
            return members;
        }

        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<SharedLibraryMember>(File.ReadAllBytes(path)) is { User: { Length: > 0 }, Machine: { Length: > 0 } } member)
                    {
                        members.Add(member);
                    }
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Somebody else's note, half written or not a note at all. The others still count.
                    SharedLibraryLog.BackupUnreadable(logger, path, exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SharedLibraryLog.BackupUnreadable(logger, folder, exception);
        }

        return members;
    }

    /// <summary>
    /// Every copy there is to restore from, on this machine and beside the file.
    /// </summary>
    public IReadOnlyList<SharedLibraryBackup> List(string? libraryPath)
    {
        List<SharedLibraryBackup> found = [];

        Collect(localDirectory, member: null);

        if (libraryPath is not null)
        {
            Dictionary<string, SharedLibraryMember> members = ReadMembers(libraryPath)
                .GroupBy(member => member.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            string folder = MembersFolder(libraryPath);

            if (Directory.Exists(folder))
            {
                try
                {
                    foreach (string path in Directory.EnumerateFiles(folder, "*" + Extension))
                    {
                        string name = Path.GetFileNameWithoutExtension(path);
                        found.Add(new SharedLibraryBackup(
                            path,
                            members.TryGetValue(name, out SharedLibraryMember? member) ? member.DisplayName : name,
                            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    SharedLibraryLog.BackupUnreadable(logger, folder, exception);
                }
            }
        }

        return [.. found.OrderByDescending(backup => backup.WrittenAt)];

        void Collect(string directory, string? member)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*" + Extension))
                {
                    found.Add(new SharedLibraryBackup(path, member, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SharedLibraryLog.BackupUnreadable(logger, directory, exception);
            }
        }
    }

    private async Task WritePresenceAsync(string folder, SharedLibraryMember member, CancellationToken cancellationToken) =>
        await new SharedLibraryFile(Path.Combine(folder, member.FileName + ".json"), timeProvider)
            .WriteAsync(JsonSerializer.SerializeToUtf8Bytes(member, PresenceOptions), replace: true, cancellationToken);
}

/// <summary>
/// A machine using a shared library, as the note beside the file describes it.
/// </summary>
/// <remarks>
/// The names are what the operating system calls the account and the machine, and are written as
/// JSON values, which escapes them. They reach a file name only through <see cref="FileName"/>.
/// </remarks>
public sealed record SharedLibraryMember
{
    [JsonPropertyName("user")]
    public string User { get; init; } = string.Empty;

    [JsonPropertyName("machine")]
    public string Machine { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("lastSynchronisedAt")]
    public DateTimeOffset? LastSynchronisedAt { get; init; }

    [JsonPropertyName("leftAt")]
    public DateTimeOffset? LeftAt { get; init; }

    [JsonIgnore]
    public string DisplayName => User + "@" + Machine;

    /// <summary>
    /// A name for this machine's files that is safe on every file system a sync client serves.
    /// </summary>
    /// <remarks>
    /// Letters, digits, a single dot, dash and underscore survive; everything else becomes an underscore. Two
    /// different names can become the same that way, so a name that had to be changed carries a few
    /// characters of a hash of the original.
    /// </remarks>
    [JsonIgnore]
    public string FileName
    {
        get
        {
            string user = Safe(User);
            string machine = Safe(Machine);
            string name = user + "@" + machine;

            if (!string.Equals(user, User, StringComparison.Ordinal) || !string.Equals(machine, Machine, StringComparison.Ordinal))
            {
                name += "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(User + "\n" + Machine)))[..6].ToLowerInvariant();
            }

            return name;
        }
    }

    internal static string Safe(string value)
    {
        StringBuilder safe = new(value.Length);

        foreach (char character in value)
        {
            // A dot only on its own, so no name can read as a step up the directory tree.
            bool allowed = char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_'
                || (character == '.' && (safe.Length == 0 || safe[^1] != '.'));

            safe.Append(allowed ? character : '_');
        }

        string result = safe.ToString().Trim('.');

        if (result.Length > 40)
        {
            result = result[..40];
        }

        return result.Length == 0 ? "unknown" : result;
    }
}

/// <summary>
/// A copy of the shared file to restore from.
/// </summary>
/// <param name="Member">Whose copy beside the file it is, or null for one kept on this machine.</param>
public sealed record SharedLibraryBackup(string Path, string? Member, DateTimeOffset WrittenAt);

/// <summary>
/// A copy, with how many profiles it holds when the passphrase stored now opens it.
/// </summary>
public sealed record SharedLibraryBackupSummary(SharedLibraryBackup Backup, int? ProfileCount);
