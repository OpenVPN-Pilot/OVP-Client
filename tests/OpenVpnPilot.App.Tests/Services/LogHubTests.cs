using OpenVpnPilot.App.Services;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The log: one file per day, both streams in it, and old files removed on their own.
/// </summary>
/// <remarks>
/// Retention matters more than it looks. A log names profiles, hosts and file paths that carry the
/// user's account name, so a directory that grows for ever is a growing pile of that. The deletion
/// therefore has to work, and it has to be judged by the date in the name rather than by a
/// timestamp a copy would have reset.
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
        await using (LogHub hub = new(directory))
        {
            hub.Append(Entry(DateTimeOffset.Now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(Today);
        }

        string expected = Today;

        Assert.Equal([expected], Directory.EnumerateFiles(directory).Select(Path.GetFileName));
        Assert.Contains("Started", await File.ReadAllTextAsync(Path.Combine(directory, expected)));
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

    [Fact]
    public async Task Appending_RemovesFilesOlderThanTheRetention()
    {
        string stale = Path.Combine(
            directory,
            DateTime.Now.AddDays(-9).ToString("yyyy-MM-dd") + ".log");

        string recent = Path.Combine(
            directory,
            DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd") + ".log");

        await File.WriteAllTextAsync(stale, "old");
        await File.WriteAllTextAsync(recent, "recent");

        await using (LogHub hub = new(directory) { RetentionDays = 7 })
        {
            hub.Append(Entry(DateTimeOffset.Now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(Today);
        }

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task Appending_WithRetentionOff_KeepsEverything()
    {
        string ancient = Path.Combine(directory, "2020-01-01.log");
        await File.WriteAllTextAsync(ancient, "ancient");

        await using (LogHub hub = new(directory) { RetentionDays = 0 })
        {
            hub.Append(Entry(DateTimeOffset.Now, LogSource.Pilot, "Started"));
            await WaitForFileAsync(Today);
        }

        Assert.True(File.Exists(ancient));
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd") + ".log";

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
