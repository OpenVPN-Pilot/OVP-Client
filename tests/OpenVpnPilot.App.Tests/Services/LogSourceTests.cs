using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.ViewModels;
using Serilog;
using Serilog.Extensions.Logging;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Which lines count as the server's, and the log window's filter for them.
/// </summary>
/// <remarks>
/// The lines go through the same pipeline the application uses, the logging abstraction over
/// Serilog into the hub, because the category and the event id are what decides, and both are
/// shaped by that pipeline rather than by anything a test could write by hand.
/// </remarks>
public sealed class LogSourceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "openvpnpilot-logsource-" + Guid.NewGuid().ToString("N"));

    public LogSourceTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives the test is not a failing test.
        }
    }

    [Theory]
    [InlineData("OpenVpnPilot.App.Services.Server.SyncEngine")]
    [InlineData("OpenVpnPilot.App.Services.Server.Outbox")]
    [InlineData("OpenVpnPilot.Core.Server.ServerHttp")]
    [InlineData("OpenVpnPilot.Core.Server.ServerSession")]
    public async Task Emit_ServerCategory_IsTheServersLine(string category)
    {
        await using LogHub hub = new(directory);
        using SerilogLoggerFactory loggers = Pipeline(hub);

        Write(loggers.CreateLogger(category), default);

        Assert.Equal(LogSource.Server, Assert.Single(hub.Snapshot()).Source);
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(3827)]
    [InlineData(3999)]
    public async Task Emit_ServerEventIdFromAnotherComponent_IsTheServersLine(int eventId)
    {
        await using LogHub hub = new(directory);
        using SerilogLoggerFactory loggers = Pipeline(hub);

        Write(loggers.CreateLogger("OpenVpnPilot.App.ViewModels.FirstRunViewModel"), new EventId(eventId, "SignedIn"));

        Assert.Equal(LogSource.Server, Assert.Single(hub.Snapshot()).Source);
    }

    [Theory]
    [InlineData(3599)]
    [InlineData(4000)]
    [InlineData(0)]
    public async Task Emit_OtherEventId_IsTheApplicationsLine(int eventId)
    {
        await using LogHub hub = new(directory);
        using SerilogLoggerFactory loggers = Pipeline(hub);

        Write(loggers.CreateLogger("OpenVpnPilot.App.Services.SessionRecorder"), new EventId(eventId));

        Assert.Equal(LogSource.Pilot, Assert.Single(hub.Snapshot()).Source);
    }

    [Fact]
    public async Task Emit_CategoryThatOnlyResemblesTheServers_IsTheApplicationsLine()
    {
        await using LogHub hub = new(directory);
        using SerilogLoggerFactory loggers = Pipeline(hub);

        Write(loggers.CreateLogger("OpenVpnPilot.App.Services.ServerLikeName"), default);

        Assert.Equal(LogSource.Pilot, Assert.Single(hub.Snapshot()).Source);
    }

    [Fact]
    public async Task ShowSource_Server_ShowsOnlyTheServersLines()
    {
        await using LogHub hub = new(directory);
        AppendOneOfEach(hub);
        LogViewModel model = new(hub, new StubLocalizer());

        model.ShowSource(LogSource.Server);

        Assert.Equal(LogSource.Server, model.SelectedSource?.Source);
        Assert.Equal(["server"], model.Rows.Select(row => row.SourceDisplay));
    }

    [Fact]
    public async Task ShowSource_Application_IncludesTheServersLines()
    {
        await using LogHub hub = new(directory);
        AppendOneOfEach(hub);
        LogViewModel model = new(hub, new StubLocalizer());

        model.ShowSource(LogSource.Pilot);

        Assert.Equal(["pilot", "server"], model.Rows.Select(row => row.SourceDisplay));
    }

    [Fact]
    public async Task ShowSource_Null_ShowsEveryLine()
    {
        await using LogHub hub = new(directory);
        AppendOneOfEach(hub);
        LogViewModel model = new(hub, new StubLocalizer());
        model.ShowSource(LogSource.OpenVpn);

        model.ShowSource(null);

        Assert.Null(model.SelectedSource?.Source);
        Assert.Equal(["pilot", "openvpn", "server"], model.Rows.Select(row => row.SourceDisplay));
    }

    [Fact]
    public async Task Sources_OfferTheServerOnItsOwn()
    {
        await using LogHub hub = new(directory);
        LogViewModel model = new(hub, new StubLocalizer());

        Assert.Contains(model.Sources, choice => choice.Source == LogSource.Server && choice.Name == "log.sourceServer");
    }

    private static SerilogLoggerFactory Pipeline(LogHub hub) => new(
        new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new LogHubSink(hub)).CreateLogger(),
        dispose: true);

    // The logging abstraction's own entry point, which every generated log method calls too.
    private static void Write(Microsoft.Extensions.Logging.ILogger logger, EventId eventId) =>
        logger.Log(LogLevel.Information, eventId, "A line.", null, (state, _) => state);

    private static void AppendOneOfEach(LogHub hub)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        hub.Append(new LogEntry(now, LogSource.Pilot, LogEntryLevel.Information, "App", "Started."));
        hub.Append(new LogEntry(now, LogSource.OpenVpn, LogEntryLevel.Information, "example-site", "Initialization Sequence Completed"));
        hub.Append(new LogEntry(now, LogSource.Server, LogEntryLevel.Information, "SyncEngine", "Pulled the feed."));
    }
}
