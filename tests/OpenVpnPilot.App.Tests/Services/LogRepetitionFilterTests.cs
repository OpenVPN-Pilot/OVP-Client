using OpenVpnPilot.App.Services;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// A line that keeps coming back is written a few times and then counted, and the count is said.
/// </summary>
public sealed class LogRepetitionFilterTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ALineThatRepeats_PassesAFewTimesAndIsThenCounted()
    {
        LogRepetitionFilter filter = new();
        int kept = 0;

        for (int index = 0; index < 100; index++)
        {
            kept += filter.Admit(Entry(Start.AddMilliseconds(index), "Restarting")).Count;
        }

        Assert.Equal(LogRepetitionFilter.PassedBeforeCollapsing, kept);
    }

    [Fact]
    public void ARepetitionThatStops_IsSummarisedOnceItHasBeenQuiet()
    {
        LogRepetitionFilter filter = new();

        for (int index = 0; index < 100; index++)
        {
            filter.Admit(Entry(Start.AddMilliseconds(index * 10), "Restarting"));
        }

        Assert.Empty(filter.Flush(Start.AddSeconds(5)));

        LogEntry summary = Assert.Single(filter.Flush(Start.AddMinutes(1)));

        Assert.Contains("Repeated 95 more times", summary.Message, StringComparison.Ordinal);
        Assert.EndsWith(": Restarting", summary.Message, StringComparison.Ordinal);
        Assert.Equal(LogSource.OpenVpn, summary.Source);
        Assert.Equal("example-site", summary.Scope);
    }

    /// <summary>
    /// A retry counts up, a handshake names its port; a line that only differs in numbers is the same line.
    /// </summary>
    [Fact]
    public void LinesThatDifferOnlyInTheirNumbers_CountAsTheSameLine()
    {
        LogRepetitionFilter filter = new();
        int kept = 0;

        for (int attempt = 0; attempt < 50; attempt++)
        {
            kept += filter.Admit(Entry(Start.AddSeconds(attempt), $"TLS handshake with 203.0.113.10:{1194 + attempt} failed, attempt {attempt}")).Count;
        }

        Assert.Equal(LogRepetitionFilter.PassedBeforeCollapsing, kept);
    }

    [Fact]
    public void DifferentLines_AreNeverCollapsedTogether()
    {
        LogRepetitionFilter filter = new();

        Assert.Single(filter.Admit(Entry(Start, "Connecting")));
        Assert.Single(filter.Admit(Entry(Start, "Authenticating")));
        Assert.Single(filter.Admit(Entry(Start, "Connected")));
    }

    /// <summary>
    /// A cycle of several lines repeating, which is what a restart loop really looks like, collapses too.
    /// </summary>
    [Fact]
    public void ACycleOfSeveralLines_CollapsesLikeASingleLine()
    {
        LogRepetitionFilter filter = new();
        string[] cycle = ["RECONNECTING", "WAIT", "AUTH", "GET_CONFIG", "Restart pause"];
        int kept = 0;

        for (int round = 0; round < 200; round++)
        {
            foreach (string line in cycle)
            {
                kept += filter.Admit(Entry(Start.AddMilliseconds(round * 5), line)).Count;
            }
        }

        Assert.Equal(cycle.Length * LogRepetitionFilter.PassedBeforeCollapsing, kept);
        Assert.Equal(cycle.Length, filter.Flush(Start.AddMinutes(5)).Count);
    }

    /// <summary>
    /// A line that comes back after a pause is news again, and what was counted before is said first.
    /// </summary>
    [Fact]
    public void ALineBackAfterAPause_IsWrittenAgainAfterTheEarlierSummary()
    {
        LogRepetitionFilter filter = new();

        for (int index = 0; index < 20; index++)
        {
            filter.Admit(Entry(Start.AddMilliseconds(index), "Restarting"));
        }

        IReadOnlyList<LogEntry> kept = filter.Admit(Entry(Start.AddMinutes(2), "Restarting"));

        Assert.Equal(2, kept.Count);
        Assert.Contains("Repeated 15 more times", kept[0].Message, StringComparison.Ordinal);
        Assert.Equal("Restarting", kept[1].Message);
    }

    /// <summary>
    /// A loop that never stops is still summarised every so often, so the file shows it going on.
    /// </summary>
    [Fact]
    public void ARepetitionThatNeverStops_IsSummarisedAtIntervals()
    {
        LogRepetitionFilter filter = new();
        int summaries = 0;

        // One line a second for an hour.
        for (int second = 0; second < 3600; second++)
        {
            summaries += filter.Admit(Entry(Start.AddSeconds(second), "Restarting"))
                .Count(entry => entry.Message.StartsWith("Repeated", StringComparison.Ordinal));
        }

        Assert.InRange(summaries, 5, 6);
    }

    [Fact]
    public void Flush_OfEverything_SummarisesWhatIsStillGoingOn()
    {
        LogRepetitionFilter filter = new();

        for (int index = 0; index < 10; index++)
        {
            filter.Admit(Entry(Start.AddMilliseconds(index), "Restarting"));
        }

        LogEntry summary = Assert.Single(filter.Flush(Start.AddSeconds(1), everything: true));
        Assert.Contains("Repeated 5 more times", summary.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(850, "850 ms")]
    [InlineData(14_000, "14 s")]
    [InlineData(120_000, "2 min")]
    [InlineData(134_000, "2 min 14 s")]
    [InlineData(3_900_000, "1 h 5 min")]
    public void Describe_SaysADurationTheWayAPersonWould(int milliseconds, string expected) =>
        Assert.Equal(expected, LogRepetitionFilter.Describe(TimeSpan.FromMilliseconds(milliseconds)));

    private static LogEntry Entry(DateTimeOffset when, string message) =>
        new(when, LogSource.OpenVpn, LogEntryLevel.Information, "example-site", message);
}
