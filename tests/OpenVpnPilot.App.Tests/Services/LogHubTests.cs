using System.Globalization;
using OpenVpnPilot.App.Services;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The log: one file per hour, both streams in it, and old or excess files removed on their own.
/// </summary>
/// <remarks>
/// Retention matters more than it looks. A log names profiles, hosts and file paths that carry the
/// user's account name, so a directory that grows for ever is a growing pile of that. The deletion
/// therefore has to work, and it has to be judged by the time in the name rather than by a
/// timestamp a copy would have reset. The size limit matters as much: a tunnel that logged a failure
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
    public async Task Appending_WritesOneFileNamedForTheHour()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        await using (LogHub hub = new(directory))
        {
            hub.Append(Entry(now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(HourFile(now));
        }

        Assert.Equal([HourFile(now)], Directory.EnumerateFiles(directory).Select(Path.GetFileName));
        Assert.Contains("Started", await File.ReadAllTextAsync(Path.Combine(directory, HourFile(now))));
    }

    [Fact]
    public async Task AnEntryInTheNextHour_GoesIntoTheNextFile()
    {
        DateTimeOffset first = new(2026, 9, 15, 13, 59, 30, TimeSpan.Zero);
        DateTimeOffset second = first.AddMinutes(1);

        await using (LogHub hub = new(directory) { RetentionDays = 0 })
        {
            hub.Append(Entry(first, LogSource.Pilot, "Before"));
            hub.Append(Entry(second, LogSource.Pilot, "After"));
            await WaitForFileAsync(HourFile(second));
        }

        Assert.Contains("Before", await File.ReadAllTextAsync(Path.Combine(directory, HourFile(first))));
        Assert.Contains("After", await File.ReadAllTextAsync(Path.Combine(directory, HourFile(second))));
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
    /// The daily files an earlier version wrote expire by the same rule as the hourly ones.
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
            await WaitForFileAsync(HourFile(DateTimeOffset.Now));
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
            await WaitForFileAsync(HourFile(DateTimeOffset.Now));
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

        string oldest = Path.Combine(directory, now.AddHours(-3).ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture) + ".log");
        string older = Path.Combine(directory, now.AddHours(-2).ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture) + ".log");
        string recent = Path.Combine(directory, now.AddHours(-1).ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture) + ".log");

        await File.WriteAllBytesAsync(oldest, megabyte);
        await File.WriteAllBytesAsync(older, megabyte);
        await File.WriteAllBytesAsync(recent, megabyte);

        await using (LogHub hub = new(directory) { RetentionDays = 0, MaximumTotalBytes = megabyte.Length * 2 })
        {
            hub.Append(Entry(DateTimeOffset.Now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(HourFile(DateTimeOffset.Now));
        }

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(older));
        Assert.True(File.Exists(recent));
    }

    /// <summary>
    /// A flood within one hour continues in a second file, so the limit can be kept by removing files
    /// while the one being written stays open.
    /// </summary>
    [Fact]
    public async Task AFullHour_ContinuesInTheNextPart()
    {
        DateTimeOffset now = new(2026, 9, 15, 13, 10, 0, TimeSpan.Zero);
        string first = Path.Combine(directory, HourFile(now));

        // The smallest a part may be is a megabyte, so a megabyte already written fills it.
        await File.WriteAllBytesAsync(first, new byte[1024 * 1024]);

        await using (LogHub hub = new(directory) { RetentionDays = 0, MaximumTotalBytes = 1024 * 1024 })
        {
            hub.Append(Entry(now, LogSource.OpenVpn, "Authenticate/Decrypt packet error"));
            await WaitForFileAsync(PartFile(now, 2));
        }

        Assert.Contains(
            "Authenticate/Decrypt packet error",
            await File.ReadAllTextAsync(Path.Combine(directory, PartFile(now, 2))));
    }

    private static string HourFile(DateTimeOffset when) =>
        when.LocalDateTime.ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture) + ".log";

    private static string PartFile(DateTimeOffset when, int part) =>
        when.LocalDateTime.ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture)
        + "_" + part.ToString(CultureInfo.InvariantCulture) + ".log";

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
