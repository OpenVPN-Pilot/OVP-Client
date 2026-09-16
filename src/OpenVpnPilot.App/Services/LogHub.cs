using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Where the two log streams meet: everything the application records and everything OpenVPN says.
/// </summary>
/// <remarks>
/// They are genuinely two streams and neither answers the other's questions. The application log
/// says what this client decided; the OpenVPN log says what the tunnel did, and it is the only place
/// several things appear at all, including the pushed options and the reason a handshake was
/// refused. Until now the second one was read for the push reply and then discarded, so a connection
/// that failed left nothing behind to look at.
///
/// Both are kept here in a bounded ring, so the window can show them live, and both are written to
/// one file per hour, so they can be looked at afterwards and in the order they actually happened.
/// Two files would put the answer in one and the question in the other.
///
/// One file per hour rather than per day, and a limit on the whole directory as well as on its age.
/// A tunnel that repeats the same complaint for every packet, or a verbosity turned up and forgotten,
/// wrote daily files of more than a gigabyte, and a week of those is a disk. An hour of it is a file
/// that still opens, and the limit removes the oldest files before the directory outgrows it. An hour
/// that writes more than a share of the limit continues in a second file, so the limit holds even
/// while the file being written is the large one.
///
/// Writing happens on a single background reader rather than on whichever thread logged. A log
/// statement must never be the thing that blocks a connection, and the OpenVPN lines arrive on the
/// management pump, which has a tunnel waiting behind it.
/// </remarks>
public sealed class LogHub : IAsyncDisposable
{
    /// <summary>
    /// How many entries the window can scroll back through.
    /// </summary>
    /// <remarks>
    /// A tunnel at verbosity three produces a few dozen lines while it comes up, so this is a long
    /// session's worth for a handful of tunnels. The file is the record; this is the view.
    /// </remarks>
    private const int Capacity = 5000;

    private readonly string directory;
    private readonly ConcurrentQueue<LogEntry> recent = new();

    private readonly Channel<LogEntry> pending =
        Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource lifetime = new();
    private readonly Task writer;

    /// <summary>
    /// The smallest a file is allowed to grow to before the hour continues in another one.
    /// </summary>
    private const long MinimumPartBytes = 1024 * 1024;

    /// <summary>
    /// How large a file may grow when the directory has no limit.
    /// </summary>
    private const long UnlimitedPartBytes = 256 * 1024 * 1024;

    private LogFileName openFor;
    private string? openPath;
    private long openBytes;
    private StreamWriter? file;
    private bool disposed;

    public LogHub(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        this.directory = directory;
        Directory.CreateDirectory(directory);

        writer = Task.Run(() => WriteAsync(lifetime.Token), CancellationToken.None);
    }

    /// <summary>
    /// Raised for every entry, on the thread that logged it.
    /// </summary>
    /// <remarks>
    /// The window marshals to the user interface thread itself. Doing it here would put a dispatcher
    /// post on the path of every log statement, including the ones written before there is a window.
    /// </remarks>
    public event EventHandler<LogEntry>? Appended;

    /// <summary>
    /// Where the files are written, so the window can offer to open it.
    /// </summary>
    public string DirectoryPath => directory;

    /// <summary>
    /// How many days of files to keep. Zero keeps everything.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// How large the files may be together, in bytes. Zero sets no limit.
    /// </summary>
    public long MaximumTotalBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// The entries still in the ring, oldest first.
    /// </summary>
    public IReadOnlyList<LogEntry> Snapshot() => [.. recent];

    public void Append(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (disposed)
        {
            return;
        }

        recent.Enqueue(entry);

        while (recent.Count > Capacity && recent.TryDequeue(out _))
        {
            // Bounded on purpose: a client left running for a week must not grow until it is closed.
        }

        pending.Writer.TryWrite(entry);
        Appended?.Invoke(this, entry);
    }

    /// <summary>
    /// The files currently in the log directory, newest first.
    /// </summary>
    public IReadOnlyList<string> Files()
    {
        try
        {
            return [.. System.IO.Directory
                .EnumerateFiles(directory, "*.log")
                .OrderByDescending(path => path, StringComparer.Ordinal)];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (LogEntry entry in pending.Reader.ReadAllAsync(cancellationToken))
            {
                WriteOne(entry);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Whatever is left in the channel is a few lines about shutting down.
        }
        finally
        {
            file?.Flush();
            file?.Dispose();
            file = null;
        }
    }

    private void WriteOne(LogEntry entry)
    {
        try
        {
            DateTime local = entry.Timestamp.LocalDateTime;
            DateTime hour = new(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);

            if (file is null || hour != openFor.Hour)
            {
                Roll(new LogFileName(hour, 1));
            }
            else if (openBytes >= PartBytes)
            {
                Roll(openFor with { Part = openFor.Part + 1 });
            }

            string line = entry.ToFileLine();
            file?.WriteLine(line);
            openBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written is not a reason to stop the application, and reporting it
            // through the logger would be the same failure again. The ring still holds the entry, so
            // the window shows it either way.
            file = null;
        }
    }

    /// <summary>
    /// How large one file may grow before the hour continues in the next.
    /// </summary>
    /// <remarks>
    /// A twentieth of the limit, so the directory can be brought back under it by removing whole
    /// files while the one being written is left alone.
    /// </remarks>
    private long PartBytes => MaximumTotalBytes > 0
        ? Math.Max(MinimumPartBytes, MaximumTotalBytes / 20)
        : UnlimitedPartBytes;

    /// <summary>
    /// Opens the file for one hour, or the next part of it, closing the previous one and clearing
    /// out what has expired.
    /// </summary>
    /// <remarks>
    /// A client left running across the hour rolls here rather than continuing to write the previous
    /// hour's file. A part that already exists, from a copy that ran earlier in the same hour, is
    /// appended to until it is full rather than started again.
    /// </remarks>
    private void Roll(LogFileName name)
    {
        file?.Flush();
        file?.Dispose();
        file = null;

        System.IO.Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, name.ToFileName());

        while (File.Exists(path) && new FileInfo(path).Length >= PartBytes)
        {
            name = name with { Part = name.Part + 1 };
            path = Path.Combine(directory, name.ToFileName());
        }

        // Cleared out before the new file is opened rather than after, so the directory is never
        // briefly holding both the new file and the ones that should already be gone.
        RemoveExpired(DateOnly.FromDateTime(name.Hour), path);

        file = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        openFor = name;
        openPath = path;
        openBytes = new FileInfo(path).Length;
    }

    /// <summary>
    /// Deletes the files older than the retention, then the oldest files while the directory is over
    /// its limit, judged by the time in the name.
    /// </summary>
    /// <remarks>
    /// The name rather than the timestamp on disk. A file copied or restored keeps its name and
    /// loses its timestamp, and the name is what the user reads when deciding what to send on. Files
    /// whose name this did not write, such as the record of a failed start, are left alone.
    /// </remarks>
    private void RemoveExpired(DateOnly today, string opening)
    {
        List<(string Path, LogFileName Name, long Bytes)> ours = [];

        foreach (string path in Files())
        {
            if (LogFileName.TryParse(Path.GetFileName(path), out LogFileName name))
            {
                ours.Add((path, name, LengthOf(path)));
            }
        }

        ours.Sort((left, right) => left.Name.CompareTo(right.Name));

        if (RetentionDays > 0)
        {
            DateOnly oldest = today.AddDays(-RetentionDays + 1);

            foreach ((string path, LogFileName name, _) in ours.ToList())
            {
                if (DateOnly.FromDateTime(name.Hour) < oldest && TryDelete(path))
                {
                    ours.RemoveAll(item => item.Path == path);
                }
            }
        }

        if (MaximumTotalBytes <= 0)
        {
            return;
        }

        long total = ours.Sum(item => item.Bytes);

        foreach ((string path, _, long bytes) in ours)
        {
            if (total <= MaximumTotalBytes)
            {
                break;
            }

            if (string.Equals(path, opening, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, openPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryDelete(path))
            {
                total -= bytes;
            }
        }
    }

    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Gone or unreadable since it was listed; it counts for nothing either way.
            return 0;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Held open by something else. It will be tried again on the next roll.
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        pending.Writer.TryComplete();

        try
        {
            await writer.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // The last few lines are worth two seconds and no more. The application is closing.
        }

        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}

/// <summary>
/// The name of one log file: the hour it covers, and which part of that hour it is.
/// </summary>
/// <remarks>
/// Written as <c>2026-09-15_14.log</c>, with <c>_2</c> and onwards before the extension for the
/// parts after the first, so the name says when without opening the file. A name of the form
/// <c>2026-09-15.log</c> is a whole day written by an earlier version, and is read as its first hour
/// so that retention and the limit apply to it in the same order.
/// </remarks>
internal readonly record struct LogFileName(DateTime Hour, int Part) : IComparable<LogFileName>
{
    public string ToFileName() => Part <= 1
        ? Hour.ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture) + ".log"
        : string.Create(CultureInfo.InvariantCulture, $"{Hour:yyyy-MM-dd_HH}_{Part}.log");

    public int CompareTo(LogFileName other)
    {
        int byHour = Hour.CompareTo(other.Hour);
        return byHour != 0 ? byHour : Part.CompareTo(other.Part);
    }

    public static bool TryParse(string fileName, out LogFileName name)
    {
        name = default;

        if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = fileName[..^4];
        string[] pieces = stem.Split('_');

        if (!DateTime.TryParseExact(pieces[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
        {
            return false;
        }

        if (pieces.Length == 1)
        {
            name = new LogFileName(day, 1);
            return true;
        }

        if (!int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out int hour) || hour > 23 || pieces[1].Length != 2)
        {
            return false;
        }

        int part = 1;

        if (pieces.Length == 3
            && !(int.TryParse(pieces[2], NumberStyles.None, CultureInfo.InvariantCulture, out part) && part >= 2))
        {
            return false;
        }

        if (pieces.Length > 3)
        {
            return false;
        }

        name = new LogFileName(day.AddHours(hour), part);
        return true;
    }
}

/// <summary>
/// One line of either log.
/// </summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Source">Which of the two streams it came from.</param>
/// <param name="Level">Its severity, in the application's own terms.</param>
/// <param name="Scope">
/// The profile for an OpenVPN line, the component for an application line. This is what makes a
/// combined log readable when several tunnels are up at once.
/// </param>
/// <param name="Message">The text.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogSource Source,
    LogEntryLevel Level,
    string Scope,
    string Message)
{
    /// <summary>
    /// The form written to the file: sortable time, level, source, scope, then the text.
    /// </summary>
    public string ToFileLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} [{Abbreviation}] [{Source}] {Scope}: {Message}");

    private string Abbreviation => Level switch
    {
        LogEntryLevel.Trace => "TRC",
        LogEntryLevel.Debug => "DBG",
        LogEntryLevel.Information => "INF",
        LogEntryLevel.Warning => "WRN",
        LogEntryLevel.Error => "ERR",
        _ => "FTL",
    };
}

/// <summary>
/// Which log a line came from.
/// </summary>
public enum LogSource
{
    /// <summary>
    /// The application's own record of what it decided.
    /// </summary>
    Pilot,

    /// <summary>
    /// OpenVPN's log stream, read from the management interface.
    /// </summary>
    OpenVpn,
}

/// <summary>
/// Severity, in one set of names for both streams.
/// </summary>
/// <remarks>
/// OpenVPN has its own severities and so does the application. Showing both under one filter means
/// mapping them onto a single scale, which is what this is.
/// </remarks>
public enum LogEntryLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}
