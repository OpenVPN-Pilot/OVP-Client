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
/// one file per day, so they can be looked at afterwards and in the order they actually happened.
/// Two files would put the answer in one and the question in the other.
///
/// What made the files unreadable was never their period but their content: a tunnel restarting as
/// fast as it could wrote the same few lines thousands of times, and daily files passed a gigabyte.
/// Hourly files only cut that into smaller pieces of the same repetition. Two things hold it now.
/// A line that keeps coming back is collapsed into a count by <see cref="LogRepetitionFilter"/>, so
/// a loop leaves a screenful and a summary instead of itself. And a day has a limit of its own:
/// past it only warnings and errors are written, and past a quarter more nothing at all, each
/// announced by a line in the file, so a day can never again outgrow anything that opens it.
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

    /// <summary>
    /// How often repetitions that have stopped are written as their summary.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private readonly string directory;
    private readonly TimeProvider timeProvider;
    private readonly LogRepetitionFilter repetitions = new();
    private readonly ConcurrentQueue<LogEntry> recent = new();

    private readonly Channel<LogEntry> pending =
        Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource lifetime = new();
    private readonly Task writer;
    private readonly ITimer flushTimer;

    private DateOnly openDay;
    private string? openPath;
    private long openBytes;
    private DayRestriction restriction;
    private StreamWriter? file;
    private bool disposed;

    public LogHub(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        this.directory = directory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(directory);

        writer = Task.Run(() => WriteAsync(lifetime.Token), CancellationToken.None);
        flushTimer = this.timeProvider.CreateTimer(_ => FlushRepetitions(), null, FlushInterval, FlushInterval);
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
    /// How large one day's file may grow before only warnings and errors are written to it. Zero
    /// sets no limit.
    /// </summary>
    public long DailyLimitBytes { get; set; } = 10L * 1024 * 1024;

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

        foreach (LogEntry kept in repetitions.Admit(entry))
        {
            Keep(kept);
        }
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

    private void Keep(LogEntry entry)
    {
        recent.Enqueue(entry);

        while (recent.Count > Capacity && recent.TryDequeue(out _))
        {
            // Bounded on purpose: a client left running for a week must not grow until it is closed.
        }

        pending.Writer.TryWrite(entry);
        Appended?.Invoke(this, entry);
    }

    private void FlushRepetitions()
    {
        if (disposed)
        {
            return;
        }

        foreach (LogEntry summary in repetitions.Flush(timeProvider.GetUtcNow()))
        {
            Keep(summary);
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
            DateOnly day = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);

            if (file is null || day != openDay)
            {
                Open(day);
            }

            if (restriction == DayRestriction.Stopped
                || (restriction == DayRestriction.WarningsOnly && entry.Level < LogEntryLevel.Warning))
            {
                return;
            }

            WriteLine(entry.ToFileLine());
            ApplyDailyLimit(entry.Timestamp);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written is not a reason to stop the application, and reporting it
            // through the logger would be the same failure again. The ring still holds the entry, so
            // the window shows it either way.
            file = null;
        }
    }

    private void WriteLine(string line)
    {
        file?.WriteLine(line);
        openBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
    }

    /// <summary>
    /// Narrows what the day's file accepts once it has grown past its limit, and says so in it.
    /// </summary>
    /// <remarks>
    /// Warnings and errors are kept past the first limit because they are what someone opening the
    /// file is looking for, and they are rare unless something is wrong. Something wrong enough to
    /// fill another quarter of the limit with them is said once more and then the day is closed.
    /// The window is not affected; it shows everything either way.
    /// </remarks>
    private void ApplyDailyLimit(DateTimeOffset when)
    {
        if (DailyLimitBytes <= 0)
        {
            return;
        }

        if (restriction == DayRestriction.None && openBytes >= DailyLimitBytes)
        {
            restriction = DayRestriction.WarningsOnly;
            WriteLine(Notice(when, string.Create(
                CultureInfo.InvariantCulture,
                $"Today's log reached its limit of {Megabytes(DailyLimitBytes)} MB. From here on only warnings and errors are written to this file; the log window still shows everything.")));
        }
        else if (restriction == DayRestriction.WarningsOnly && openBytes >= HardLimitBytes)
        {
            restriction = DayRestriction.Stopped;
            WriteLine(Notice(when, string.Create(
                CultureInfo.InvariantCulture,
                $"Today's log reached {Megabytes(HardLimitBytes)} MB with warnings and errors alone. Nothing more is written to this file today; the log window still shows everything.")));
        }
    }

    private long HardLimitBytes => DailyLimitBytes + (DailyLimitBytes / 4);

    private static string Megabytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Notice(DateTimeOffset when, string text) =>
        new LogEntry(when, LogSource.Pilot, LogEntryLevel.Warning, nameof(LogHub), text).ToFileLine();

    /// <summary>
    /// Opens the file for one day, closing the previous one and clearing out what has expired.
    /// </summary>
    /// <remarks>
    /// A client left running across midnight rolls here rather than continuing to write the
    /// previous day's file. A file that already exists, from a copy that ran earlier the same day,
    /// is appended to, and how full it already is decides what it still accepts.
    /// </remarks>
    private void Open(DateOnly day)
    {
        file?.Flush();
        file?.Dispose();
        file = null;

        System.IO.Directory.CreateDirectory(directory);

        LogFileName name = LogFileName.ForDay(day);
        string path = Path.Combine(directory, name.ToFileName());

        // Cleared out before the new file is opened rather than after, so the directory is never
        // briefly holding both the new file and the ones that should already be gone.
        RemoveExpired(day, path);

        file = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        openDay = day;
        openPath = path;
        openBytes = new FileInfo(path).Length;

        restriction = DailyLimitBytes <= 0 ? DayRestriction.None
            : openBytes >= HardLimitBytes ? DayRestriction.Stopped
            : openBytes >= DailyLimitBytes ? DayRestriction.WarningsOnly
            : DayRestriction.None;
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
                if (DateOnly.FromDateTime(name.Start) < oldest && TryDelete(path))
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

        await flushTimer.DisposeAsync();

        // A repetition still being counted is written as its summary, or the last thing a loop did
        // before the application closed would be missing from the record.
        foreach (LogEntry summary in repetitions.Flush(timeProvider.GetUtcNow(), everything: true))
        {
            Keep(summary);
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

    /// <summary>
    /// How much of what arrives the day's file still accepts.
    /// </summary>
    private enum DayRestriction
    {
        None,
        WarningsOnly,
        Stopped,
    }
}

/// <summary>
/// The name of one log file: when it starts, whether it covers a day or an hour, and which part.
/// </summary>
/// <remarks>
/// Written as <c>2026-09-15.log</c>, one file per day, so the name says when without opening the
/// file. Earlier versions wrote one file per hour, <c>2026-09-15_14.log</c>, with <c>_2</c> and
/// onwards before the extension for the parts after the first. Those are still read, so retention
/// and the limit apply to them in the same order until they have expired.
/// </remarks>
internal readonly record struct LogFileName(DateTime Start, int Part, bool Daily) : IComparable<LogFileName>
{
    public static LogFileName ForDay(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), 1, true);

    public string ToFileName() => Daily
        ? Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log"
        : Part <= 1
            ? Start.ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture) + ".log"
            : string.Create(CultureInfo.InvariantCulture, $"{Start:yyyy-MM-dd_HH}_{Part}.log");

    public int CompareTo(LogFileName other)
    {
        int byStart = Start.CompareTo(other.Start);
        return byStart != 0 ? byStart : Part.CompareTo(other.Part);
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
            name = new LogFileName(day, 1, true);
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

        name = new LogFileName(day.AddHours(hour), part, false);
        return true;
    }
}

/// <summary>
/// One line of either log.
/// </summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Source">Which stream it came from, and for the application's own, whether it concerns a server.</param>
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

    /// <summary>
    /// The application's own record of what it did with a server: calls, sign in, synchronisation,
    /// the changes waiting to be sent and switching the store.
    /// </summary>
    /// <remarks>
    /// Part of what the application writes, set apart so that server traffic can be read on its
    /// own. <see cref="LogHubSink"/> decides which lines belong here.
    /// </remarks>
    Server,
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
