using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenVpnPilot.Core.Vpn;

namespace OpenVpnPilot.App.Converters;

/// <summary>
/// Value converters used by the views.
/// </summary>
/// <remarks>
/// Exposed as static instances so bindings can reference them without a resource dictionary entry.
/// </remarks>
public static class AppConverters
{
    /// <summary>
    /// Maps a connection state to the colour of the status dot.
    /// </summary>
    public static readonly IValueConverter StateBrush = new FuncValueConverter<VpnConnectionState, IBrush>(
        static state => state switch
        {
            VpnConnectionState.Connected => Brushes.MediumSeaGreen,
            VpnConnectionState.Connecting or VpnConnectionState.Launching or VpnConnectionState.Authenticating
                => Brushes.Goldenrod,
            VpnConnectionState.Reconnecting => Brushes.DarkOrange,
            VpnConnectionState.Disconnecting => Brushes.Goldenrod,
            VpnConnectionState.Failed => Brushes.IndianRed,
            _ => Brushes.Gray,
        });

    /// <summary>
    /// Formats a byte counter for display.
    /// </summary>
    public static readonly IValueConverter ByteSize = new FuncValueConverter<long, string>(FormatBytes);

    /// <summary>
    /// Renders the number of active connections, or nothing when there are none.
    /// </summary>
    public static readonly IValueConverter ActiveCount = new FuncValueConverter<int, string>(
        static count => count == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"{count} active"));

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
