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

    private DateOnly openFor = DateOnly.MinValue;
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
            DateOnly day = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);

            if (file is null || day != openFor)
            {
                Roll(day);
            }

            file?.WriteLine(entry.ToFileLine());
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
    /// Opens the file for one day, closing the previous one and clearing out what has expired.
    /// </summary>
    /// <remarks>
    /// One file per calendar day, named for that day. A client left running across midnight rolls
    /// here rather than continuing to write yesterday's file.
    /// </remarks>
    private void Roll(DateOnly day)
    {
        file?.Flush();
        file?.Dispose();

        System.IO.Directory.CreateDirectory(directory);

        // Cleared out before the new file is opened rather than after, so the directory is never
        // briefly holding both today's file and the ones that should already be gone.
        RemoveExpired(day);

        string path = Path.Combine(
            directory,
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        file = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        openFor = day;
    }

    /// <summary>
    /// Deletes the files older than the retention, judged by the date in the name.
    /// </summary>
    /// <remarks>
    /// The name rather than the timestamp on disk. A file copied or restored keeps its name and
    /// loses its timestamp, and the name is what the user reads when deciding what to send on.
    /// </remarks>
    private void RemoveExpired(DateOnly today)
    {
        if (RetentionDays <= 0)
        {
            return;
        }

        DateOnly oldest = today.AddDays(-RetentionDays + 1);

        foreach (string path in Files())
        {
            if (!DateOnly.TryParseExact(
                    Path.GetFileNameWithoutExtension(path),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly named)
                || named >= oldest)
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Held open by something else. It will be tried again on the next roll.
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
