using System.Globalization;
using OpenVpnPilot.App.Services;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The log: one file per day, both streams in it, a limit per day, and old or excess files removed
/// on their own.
/// </summary>
/// <remarks>
/// Retention matters more than it looks. A log names profiles, hosts and file paths that carry the
/// user's account name, so a directory that grows for ever is a growing pile of that. The deletion
/// therefore has to work, and it has to be judged by the time in the name rather than by a
/// timestamp a copy would have reset. The size limits matter as much: a tunnel that logged a failure
/// for every packet wrote daily files of more than a gigabyte.
/// </remarks>
public sealed class LogHubTests : IAsyncLifetime
{
    private string directory = string.Empty;

    public Task InitializeAsync()
    {
        directory = Path.Combine(Path.GetTempPath(), "openvpnpilot-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives the test is not a failing test.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Appending_WritesOneFileNamedForTheDay()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        await using (LogHub hub = new(directory))
        {
            hub.Append(Entry(now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(DayFile(now));
        }

        Assert.Equal([DayFile(now)], Directory.EnumerateFiles(directory).Select(Path.GetFileName));
        Assert.Contains("Started", await File.ReadAllTextAsync(Path.Combine(directory, DayFile(now))));
    }

    [Fact]
    public async Task EntriesInDifferentHoursOfOneDay_ShareOneFile()
    {
        // Local times, so both fall on the same local day wherever the suite runs.
        DateTimeOffset morning = new(new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Local));
        DateTimeOffset evening = new(new DateTime(2026, 9, 15, 17, 0, 0, DateTimeKind.Local));

        await using (LogHub hub = new(directory) { RetentionDays = 0 })
        {
            hub.Append(Entry(morning, LogSource.Pilot, "Morning"));
            hub.Append(Entry(evening, LogSource.Pilot, "Evening"));
        }

        Assert.Equal([DayFile(morning)], Directory.EnumerateFiles(directory).Select(Path.GetFileName));

        string text = await File.ReadAllTextAsync(Path.Combine(directory, DayFile(morning)));
        Assert.Contains("Morning", text, StringComparison.Ordinal);
        Assert.Contains("Evening", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryOnTheNextDay_GoesIntoTheNextFile()
    {
        DateTimeOffset first = new(new DateTime(2026, 9, 15, 23, 59, 30, DateTimeKind.Local));
        DateTimeOffset second = new(new DateTime(2026, 9, 16, 0, 0, 30, DateTimeKind.Local));

        await using (LogHub hub = new(directory) { RetentionDays = 0 })
        {
            hub.Append(Entry(first, LogSource.Pilot, "Before"));
            hub.Append(Entry(second, LogSource.Pilot, "After"));
        }

        Assert.Contains("Before", await File.ReadAllTextAsync(Path.Combine(directory, DayFile(first))));
        Assert.Contains("After", await File.ReadAllTextAsync(Path.Combine(directory, DayFile(second))));
    }

    [Fact]
    public async Task Appending_KeepsBothStreamsInTheOrderTheyHappened()
    {
        await using LogHub hub = new(directory);

        hub.Append(Entry(DateTimeOffset.Now, LogSource.Pilot, "Connecting"));
        hub.Append(Entry(DateTimeOffset.Now, LogSource.OpenVpn, "TLS handshake failed"));

        Assert.Equal(
            [LogSource.Pilot, LogSource.OpenVpn],
            hub.Snapshot().Select(entry => entry.Source));
    }

    /// <summary>
    /// The hourly files earlier versions wrote expire by the same rule as the daily ones.
    /// </summary>
    [Fact]
    public async Task Appending_RemovesFilesOlderThanTheRetention_WhicheverWayTheyAreNamed()
    {
        DateTime now = DateTime.Now;

        string staleDay = Path.Combine(directory, now.AddDays(-9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        string staleHour = Path.Combine(directory, now.AddDays(-8).ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture) + ".log");
        string recentDay = Path.Combine(directory, now.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        string recentHour = Path.Combine(directory, now.AddDays(-1).ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture) + ".log");
        string foreign = Path.Combine(directory, "failure.log");

        foreach (string path in new[] { staleDay, staleHour, recentDay, recentHour, foreign })
        {
            await File.WriteAllTextAsync(path, "line");
        }

        await using (LogHub hub = new(directory) { RetentionDays = 7 })
        {
            hub.Append(Entry(DateTimeOffset.Now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(DayFile(DateTimeOffset.Now));
        }

        Assert.False(File.Exists(staleDay));
        Assert.False(File.Exists(staleHour));
        Assert.True(File.Exists(recentDay));
        Assert.True(File.Exists(recentHour));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task Appending_WithRetentionOff_KeepsEverything()
    {
        string ancient = Path.Combine(directory, "2020-01-01.log");
        await File.WriteAllTextAsync(ancient, "ancient");

        await using (LogHub hub = new(directory) { RetentionDays = 0 })
        {
            hub.Append(Entry(DateTimeOffset.Now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(DayFile(DateTimeOffset.Now));
        }

        Assert.True(File.Exists(ancient));
    }

    /// <summary>
    /// Over the limit, the oldest files go first, and recent ones stay.
    /// </summary>
    [Fact]
    public async Task OverTheSizeLimit_TheOldestFilesAreRemovedFirst()
    {
        DateTime now = DateTime.Now;
        byte[] megabyte = new byte[1024 * 1024];

        string oldest = Path.Combine(directory, now.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        string older = Path.Combine(directory, now.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        string recent = Path.Combine(directory, now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        await File.WriteAllBytesAsync(oldest, megabyte);
        await File.WriteAllBytesAsync(older, megabyte);
        await File.WriteAllBytesAsync(recent, megabyte);

        await using (LogHub hub = new(directory) { RetentionDays = 0, MaximumTotalBytes = megabyte.Length * 2 })
        {
            hub.Append(Entry(DateTimeOffset.Now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(DayFile(DateTimeOffset.Now));
        }

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(older));
        Assert.True(File.Exists(recent));
    }

    /// <summary>
    /// Past the day's limit only warnings and errors are written, and the file says so.
    /// </summary>
    [Fact]
    public async Task PastTheDailyLimit_OnlyWarningsAndErrorsAreWritten()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string path = Path.Combine(directory, DayFile(now));

        await using (LogHub hub = new(directory) { RetentionDays = 0, MaximumTotalBytes = 0, DailyLimitBytes = 2048 })
        {
            // Distinct lines, so the repetition filter lets every one of them through.
            for (int line = 0; line < 60; line++)
            {
                hub.Append(Entry(now, LogSource.OpenVpn, "Progress " + new string((char)('a' + (line % 26)), line + 1)));
            }

            hub.Append(new LogEntry(now, LogSource.OpenVpn, LogEntryLevel.Information, "Test", "An ordinary line after the limit"));
            hub.Append(new LogEntry(now, LogSource.OpenVpn, LogEntryLevel.Error, "Test", "An error after the limit"));
        }

        string text = await File.ReadAllTextAsync(path);

        Assert.Contains("reached its limit", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An ordinary line after the limit", text, StringComparison.Ordinal);
        Assert.Contains("An error after the limit", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A quarter past the limit nothing is written any more, whatever its level.
    /// </summary>
    [Fact]
    public async Task PastTheHardLimit_NothingMoreIsWritten()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string path = Path.Combine(directory, DayFile(now));

        await using (LogHub hub = new(directory) { RetentionDays = 0, MaximumTotalBytes = 0, DailyLimitBytes = 1024 })
        {
            for (int line = 0; line < 80; line++)
            {
                hub.Append(new LogEntry(now, LogSource.OpenVpn, LogEntryLevel.Error, "Test", "Failure " + new string((char)('a' + (line % 26)), line + 1)));
            }

            hub.Append(new LogEntry(now, LogSource.OpenVpn, LogEntryLevel.Fatal, "Test", "The very last word"));
        }

        string text = await File.ReadAllTextAsync(path);

        Assert.Contains("Nothing more is written to this file today", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The very last word", text, StringComparison.Ordinal);
        Assert.True(new FileInfo(path).Length < 4096);
    }

    /// <summary>
    /// A loop leaves a few lines and one summary in the file rather than itself.
    /// </summary>
    [Fact]
    public async Task ALineRepeatedThousandsOfTimes_IsWrittenAFewTimesAndSummarised()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string path = Path.Combine(directory, DayFile(now));

        await using (LogHub hub = new(directory) { RetentionDays = 0 })
        {
            for (int attempt = 0; attempt < 5000; attempt++)
            {
                hub.Append(Entry(now.AddMilliseconds(attempt), LogSource.OpenVpn, $"TLS Error: TLS handshake failed, attempt {attempt}"));
            }
        }

        string[] lines = await File.ReadAllLinesAsync(path);

        Assert.Equal(LogRepetitionFilter.PassedBeforeCollapsing + 1, lines.Length);
        Assert.Contains("Repeated 4995 more times", lines[^1], StringComparison.Ordinal);
    }

    private static string DayFile(DateTimeOffset when) =>
        when.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";

    /// <summary>
    /// Waits for the background writer, which is what makes the file appear and what applies the
    /// retention. Polled rather than assumed, because the writer runs on its own.
    /// </summary>
    private async Task WaitForFileAsync(string name)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(Path.Combine(directory, name)))
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static LogEntry Entry(DateTimeOffset when, LogSource source, string message) =>
        new(when, source, LogEntryLevel.Information, "Test", message);
}
