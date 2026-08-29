using System.Globalization;

namespace OpenVpnPilot.OpenVpn.Runtime;

/// <summary>
/// What the server told the client to apply, read from the push reply.
/// </summary>
/// <remarks>
/// The management interface has no command that reports these, so the only place they appear is the
/// log stream. They matter to the user for two reasons: the routes say what the tunnel actually
/// carries, and the gateway is the address worth measuring a round trip against.
/// </remarks>
/// <param name="Routes">Routes the server pushed, formatted as network and mask.</param>
/// <param name="DnsServers">Name servers the server pushed.</param>
/// <param name="Gateway">The tunnel gateway, when one was pushed.</param>
/// <param name="RedirectsDefaultRoute">
/// True when the server asked for all traffic. Worth showing, because a pull filter may have
/// refused it and the user would otherwise not know the server had tried.
/// </param>
/// <param name="RequestsCompression">
/// True when the server pushed a compression setting. A current client with data channel offload
/// refuses any of them, including one that turns compression off, and then abandons the whole push
/// reply. The tunnel reconnects forever with a reason nobody can act on, so the cause is recorded
/// here and said plainly instead.
/// </param>
public sealed record PushedOptions(
    IReadOnlyList<string> Routes,
    IReadOnlyList<string> DnsServers,
    string? Gateway,
    bool RedirectsDefaultRoute,
    bool RequestsCompression = false)
{
    public static PushedOptions None { get; } = new([], [], null, false);

    public bool HasAnything => Routes.Count > 0 || DnsServers.Count > 0 || Gateway is not null;
}

/// <summary>
/// Reads the push reply OpenVPN logs when a server sends its options.
/// </summary>
/// <remarks>
/// The line looks like:
/// <c>PUSH: Received control message: 'PUSH_REPLY,route 10.8.0.0 255.255.255.0,route-gateway
/// 10.8.0.1,dhcp-option DNS 10.8.0.1,redirect-gateway def1'</c>
///
/// Options are comma separated inside a quoted payload, and a comma cannot appear inside an option,
/// so splitting on commas is exact rather than a guess.
/// </remarks>
public static class PushReplyParser
{
    private const string Marker = "PUSH: Received control message: '";
    private const string Prefix = "PUSH_REPLY,";

    /// <summary>
    /// True when the line is a push reply worth parsing.
    /// </summary>
    public static bool IsPushReply(string logText)
    {
        ArgumentNullException.ThrowIfNull(logText);
        return logText.Contains(Marker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the options out of a push reply, or returns null when the line is not one.
    /// </summary>
    public static PushedOptions? Parse(string logText)
    {
        ArgumentNullException.ThrowIfNull(logText);

        int start = logText.IndexOf(Marker, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        string payload = logText[(start + Marker.Length)..];
        int end = payload.LastIndexOf('\'');

        if (end >= 0)
        {
            payload = payload[..end];
        }

        if (!payload.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        List<string> routes = [];
        List<string> dns = [];
        string? gateway = null;
        bool redirect = false;
        bool compression = false;

        foreach (string option in payload[Prefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = option.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            switch (parts[0])
            {
                case "route" when parts.Length >= 3:
                    routes.Add(string.Create(CultureInfo.InvariantCulture, $"{parts[1]} {parts[2]}"));
                    break;

                case "route" when parts.Length == 2:
                    routes.Add(parts[1]);
                    break;

                case "route-ipv6" when parts.Length >= 2:
                    routes.Add(parts[1]);
                    break;

                case "route-gateway" when parts.Length >= 2:
                    gateway = parts[1];
                    break;

                case "ifconfig" when parts.Length >= 3 && gateway is null:
                    // A server without an explicit gateway still names the peer address, which is
                    // the far end of the tunnel and therefore the useful thing to measure against.
                    gateway = parts[2];
                    break;

                case "dhcp-option" when parts.Length >= 3
                    && string.Equals(parts[1], "DNS", StringComparison.OrdinalIgnoreCase):
                    dns.Add(parts[2]);
                    break;

                case "redirect-gateway":
                    redirect = true;
                    break;

                // Both spellings, and both values. A client that cannot apply the setting cannot
                // apply "off" either, so there is no case here that is harmless.
                case "comp-lzo":
                case "compress":
                    compression = true;
                    break;

                default:
                    break;
            }
        }

        return new PushedOptions(routes, dns, gateway, redirect, compression);
    }
}
