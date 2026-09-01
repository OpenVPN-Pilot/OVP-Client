using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Vpn;

namespace OpenVpnPilot.App.Converters;

/// <summary>
/// Value converters used by the views.
/// </summary>
/// <remarks>
/// Exposed as static instances so bindings can reference them without a resource dictionary entry.
/// Colour lookups go through the application resources, so they follow the active theme variant.
/// </remarks>
public static class AppConverters
{
    /// <summary>
    /// Colour of the status dot on a profile row.
    /// </summary>
    public static readonly IValueConverter StateBrush =
        new FuncValueConverter<VpnConnectionState, IBrush?>(state => Resource(BrushKeyFor(state)));

    /// <summary>
    /// Background of the status pill, a muted version of the state colour.
    /// </summary>
    public static readonly IValueConverter StatePillBackground =
        new FuncValueConverter<VpnConnectionState, IBrush?>(state => Resource(SoftKeyFor(state)));

    /// <summary>
    /// Text colour of the status pill.
    /// </summary>
    public static readonly IValueConverter StatePillForeground =
        new FuncValueConverter<VpnConnectionState, IBrush?>(state => Resource(BrushKeyFor(state)));

    /// <summary>
    /// Formats a byte counter for display.
    /// </summary>
    public static readonly IValueConverter ByteSize = new FuncValueConverter<long, string>(FormatBytes);

    /// <summary>
    /// Renders a connection duration, or a dash when the tunnel is not up.
    /// </summary>
    public static readonly IValueConverter Uptime = new FuncValueConverter<DateTimeOffset?, string>(
        static since => since is { } start
            ? (DateTimeOffset.UtcNow - start).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : "-");

    /// <summary>
    /// Renders when a watched directory was last scanned.
    /// </summary>
    /// <remarks>
    /// A directory that has never been scanned says so rather than showing an empty cell, because an
    /// empty cell reads as "nothing found" rather than as "not looked at yet".
    /// </remarks>
    public static readonly IValueConverter LastScan = new FuncValueConverter<DateTimeOffset?, string>(
        static when => when is { } moment
            ? moment.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "-");

    /// <summary>
    /// Renders a count only when it is greater than zero, so empty badges stay hidden.
    /// </summary>
    public static readonly IValueConverter CountBadge = new FuncValueConverter<int, string>(
        static count => count == 0 ? string.Empty : count.ToString(CultureInfo.CurrentCulture));

    /// <summary>
    /// Colour of a log line, so a warning and a failure stand out of a long list.
    /// </summary>
    public static readonly IValueConverter LogLevelBrush =
        new FuncValueConverter<LogEntryLevel, IBrush?>(level => Resource(level switch
        {
            LogEntryLevel.Warning => "StatePending",
            LogEntryLevel.Error or LogEntryLevel.Fatal => "StateFailed",
            LogEntryLevel.Trace or LogEntryLevel.Debug => "TextTertiary",
            _ => "TextSecondary",
        }));

    private static string BrushKeyFor(VpnConnectionState state) => state switch
    {
        VpnConnectionState.Connected => "StateConnected",
        VpnConnectionState.Failed => "StateFailed",
        VpnConnectionState.Disconnected => "StateIdle",
        _ => "StatePending",
    };

    private static string SoftKeyFor(VpnConnectionState state) => state switch
    {
        VpnConnectionState.Connected => "StateConnectedSoft",
        VpnConnectionState.Failed => "StateFailedSoft",
        VpnConnectionState.Disconnected => "SurfaceCardHover",
        _ => "StatePendingSoft",
    };

    // Resolved against the live theme variant so the colours follow a light or dark switch.
    private static IBrush? Resource(string key)
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.TryGetResource(key, application.ActualThemeVariant, out object? value)
            ? value as IBrush
            : null;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }
}
