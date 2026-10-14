using System.Globalization;
using System.Text;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Collapses a line that keeps coming back into a count of how often it did.
/// </summary>
/// <remarks>
/// A tunnel that restarts as fast as it can repeats the same few dozen lines for every attempt, many
/// times a second, for as long as nobody notices. That is how a day of log grew past fifty megabytes
/// of the same handful of sentences, and why nobody could read it: the line that said what went
/// wrong was in there once and the repetition buried it.
///
/// A line is let through the first few times it appears. After that, while it keeps appearing, it is
/// counted instead of written, and the count is written as one line once the repetition stops, or
/// every few minutes while it goes on. A loop therefore costs a screenful at its start and a line per
/// interval afterwards, and the summary says how often and for how long.
///
/// Lines count as the same when they differ only in their numbers. A retry announces its attempt, a
/// handshake its port and a packet its sequence, and a line that merely counts up is still the same
/// line. The summary quotes the first one in full, so the numbers that mattered are still there.
///
/// This is pure logic over the timestamps of the entries, so it can be tested without a clock; the
/// hub decides when a quiet repetition is summarised by calling <see cref="Flush"/>.
/// </remarks>
public sealed class LogRepetitionFilter
{
    /// <summary>
    /// How many times a line is written before further repetitions are counted instead.
    /// </summary>
    internal const int PassedBeforeCollapsing = 5;

    /// <summary>
    /// How long a line has to stay away before it counts as new again.
    /// </summary>
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often a repetition that does not stop is summarised while it goes on.
    /// </summary>
    internal static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many distinct lines are tracked at once, so a stream of lines that never repeat cannot
    /// grow the table without bound.
    /// </summary>
    private const int MaximumTracked = 4096;

    private readonly Dictionary<LineKey, Tracker> trackers = [];
    private readonly Lock gate = new();

    /// <summary>
    /// Decides what one entry leaves in the log.
    /// </summary>
    /// <returns>
    /// The entries to keep, in order: a summary that became due, then the entry itself if it passes.
    /// Empty when the entry was counted.
    /// </returns>
    public IReadOnlyList<LogEntry> Admit(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        DateTimeOffset now = entry.Timestamp;
        LineKey key = new(entry.Source, entry.Level, entry.Scope, Normalise(entry.Message));
        List<LogEntry> kept = [];

        lock (gate)
        {
            if (trackers.TryGetValue(key, out Tracker? tracker))
            {
                if (now - tracker.LastSeen > QuietPeriod)
                {
                    // Back after a pause: whatever was counted before belongs to the earlier run.
                    AddSummary(tracker, now, kept);
                    tracker.Restart(entry);
                    kept.Add(entry);
                    return kept;
                }

                tracker.LastSeen = now;

                if (tracker.Passed < PassedBeforeCollapsing)
                {
                    tracker.Passed++;
                    kept.Add(entry);
                    return kept;
                }

                tracker.Count(now);

                if (now - tracker.CountingSince >= SummaryInterval)
                {
                    AddSummary(tracker, now, kept);
                }

                return kept;
            }

            if (trackers.Count >= MaximumTracked)
            {
                FlushLocked(now, everything: false, kept);

                if (trackers.Count >= MaximumTracked)
                {
                    FlushLocked(now, everything: true, kept);
                }
            }

            trackers[key] = new Tracker(entry);
            kept.Add(entry);
            return kept;
        }
    }

    /// <summary>
    /// Summarises the repetitions that have stopped, and forgets the lines that have gone quiet.
    /// </summary>
    /// <param name="now">The current time, against which quiet is judged.</param>
    /// <param name="everything">Summarise every repetition, quiet or not, as on the way out.</param>
    public IReadOnlyList<LogEntry> Flush(DateTimeOffset now, bool everything = false)
    {
        List<LogEntry> kept = [];

        lock (gate)
        {
            FlushLocked(now, everything, kept);
        }

        return kept;
    }

    private void FlushLocked(DateTimeOffset now, bool everything, List<LogEntry> kept)
    {
        foreach ((LineKey key, Tracker tracker) in trackers.ToList())
        {
            if (!everything && now - tracker.LastSeen <= QuietPeriod)
            {
                continue;
            }

            AddSummary(tracker, now, kept);
            trackers.Remove(key);
        }
    }

    private static void AddSummary(Tracker tracker, DateTimeOffset now, List<LogEntry> kept)
    {
        if (tracker.Suppressed == 0)
        {
            return;
        }

        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"Repeated {tracker.Suppressed} more times in {Describe(tracker.LastCounted - tracker.CountingSince)}, last at {tracker.LastCounted.LocalDateTime:HH:mm:ss}: {tracker.Sample.Message}");

        kept.Add(new LogEntry(now, tracker.Sample.Source, tracker.Sample.Level, tracker.Sample.Scope, text));
        tracker.ResetCount(now);
    }

    /// <summary>
    /// A duration the way a person says it: the two largest units and nothing finer.
    /// </summary>
    internal static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)span.TotalMilliseconds)} ms");
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds} s");
        }

        if (span < TimeSpan.FromHours(1))
        {
            return span.Seconds == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min")
                : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min {span.Seconds} s");
        }

        return span.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h {span.Minutes} min");
    }

    /// <summary>
    /// The line with every run of digits replaced, so lines that only count up compare equal.
    /// </summary>
    internal static string Normalise(string message)
    {
        StringBuilder builder = new(message.Length);
        bool inDigits = false;

        foreach (char character in message)
        {
            if (char.IsAsciiDigit(character))
            {
                if (!inDigits)
                {
                    builder.Append('#');
                    inDigits = true;
                }

                continue;
            }

            inDigits = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    private readonly record struct LineKey(LogSource Source, LogEntryLevel Level, string Scope, string Text);

    /// <summary>
    /// What is known about one line: how often it passed, how often it was counted, and since when.
    /// </summary>
    private sealed class Tracker
    {
        public Tracker(LogEntry first) => Restart(first);

        public LogEntry Sample { get; private set; } = null!;

        public DateTimeOffset LastSeen { get; set; }

        public int Passed { get; set; }

        public int Suppressed { get; private set; }

        public DateTimeOffset CountingSince { get; private set; }

        public DateTimeOffset LastCounted { get; private set; }

        public void Restart(LogEntry entry)
        {
            Sample = entry;
            LastSeen = entry.Timestamp;
            Passed = 1;
            Suppressed = 0;
        }

        public void Count(DateTimeOffset now)
        {
            if (Suppressed == 0)
            {
                CountingSince = now;
            }

            Suppressed++;
            LastCounted = now;
        }

        public void ResetCount(DateTimeOffset now)
        {
            Suppressed = 0;
            CountingSince = now;
        }
    }
}
