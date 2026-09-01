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
            LogSource.Pilot,
            Map(logEvent.Level),
            ScopeOf(logEvent),
            message));
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
