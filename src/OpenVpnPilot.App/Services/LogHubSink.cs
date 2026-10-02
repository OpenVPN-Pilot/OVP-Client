using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Feeds everything the application logs into the hub.
/// </summary>
/// <remarks>
/// A sink rather than a second logging path, so nothing has to be written twice and nothing that
/// already logs has to know that a window exists. Serilog calls this on the thread that logged, so
/// it does as little as possible: the hub queues the entry and a background reader writes it.
/// </remarks>
public sealed class LogHubSink : ILogEventSink
{
    /// <summary>
    /// Renders the message with string values written plainly.
    /// </summary>
    /// <remarks>
    /// Rendering a template directly quotes every string property, so a version number arrives as
    /// <c>"1.1.0"</c> rather than as 1.1.0 and a log full of them is hard to read. The <c>l</c> flag
    /// is what the console and file sinks use for the same reason.
    /// </remarks>
    private static readonly MessageTemplateTextFormatter Formatter =
        new("{Message:lj}", CultureInfo.InvariantCulture);

    /// <summary>
    /// The event ids reserved for server mode, which mark a line as the server's wherever it was written.
    /// </summary>
    private const int FirstServerEventId = 3600;

    private const int LastServerEventId = 3999;

    /// <summary>
    /// The categories of the components that talk to a server.
    /// </summary>
    private static readonly string[] ServerCategories =
    [
        "OpenVpnPilot.Core.Server.",
        "OpenVpnPilot.App.Services.Server.",
    ];

    private readonly LogHub hub;

    public LogHubSink(LogHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        this.hub = hub;
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        using StringWriter writer = new(CultureInfo.InvariantCulture);
        Formatter.Format(logEvent, writer);

        string message = writer.ToString();

        if (logEvent.Exception is { } exception)
        {
            message = message + Environment.NewLine + exception;
        }

        hub.Append(new LogEntry(
            logEvent.Timestamp,
            SourceOf(logEvent),
            Map(logEvent.Level),
            ScopeOf(logEvent),
            message));
    }

    /// <summary>
    /// Whether a line concerns a server, which sets it apart from the rest of the application's.
    /// </summary>
    /// <remarks>
    /// Two signs, because neither covers everything. The components that talk to a server live in
    /// the server namespaces of the core and of the application, so their category says so. Some
    /// of what concerns a server is written elsewhere, though: a sign in from the first start
    /// window, or the startup reporting which store it opened. Those carry an event id from the
    /// block reserved for server mode, 3600 to 3999, which is the second sign.
    /// </remarks>
    internal static LogSource SourceOf(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? context)
            && context is ScalarValue { Value: string category }
            && ServerCategories.Any(prefix => category.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return LogSource.Server;
        }

        if (logEvent.Properties.TryGetValue("EventId", out LogEventPropertyValue? eventId)
            && eventId is StructureValue structure
            && structure.Properties.FirstOrDefault(property => property.Name == "Id")?.Value is ScalarValue { Value: int id }
            && id is >= FirstServerEventId and <= LastServerEventId)
        {
            return LogSource.Server;
        }

        return LogSource.Pilot;
    }

    /// <summary>
    /// The component that logged, shortened to its type name.
    /// </summary>
    /// <remarks>
    /// The full context is the namespace qualified type, which is most of the width of the window
    /// and the same prefix on nearly every line.
    /// </remarks>
    private static string ScopeOf(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? value))
        {
            return "Pilot";
        }

        string context = value.ToString().Trim('"');
        int lastDot = context.LastIndexOf('.');

        return lastDot >= 0 && lastDot < context.Length - 1 ? context[(lastDot + 1)..] : context;
    }

    private static LogEntryLevel Map(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogEntryLevel.Trace,
        LogEventLevel.Debug => LogEntryLevel.Debug,
        LogEventLevel.Information => LogEntryLevel.Information,
        LogEventLevel.Warning => LogEntryLevel.Warning,
        LogEventLevel.Error => LogEntryLevel.Error,
        _ => LogEntryLevel.Fatal,
    };
}
