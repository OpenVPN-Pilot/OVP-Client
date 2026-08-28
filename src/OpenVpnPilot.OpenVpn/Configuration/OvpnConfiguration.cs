using System.Collections.ObjectModel;

namespace OpenVpnPilot.OpenVpn.Configuration;

/// <summary>
/// A parsed OpenVPN configuration file.
/// </summary>
public sealed class OvpnConfiguration
{
    /// <summary>
    /// Directives that reference an external file and can be pulled inline to make a profile
    /// self contained.
    /// </summary>
    public static readonly IReadOnlySet<string> InlinableDirectives = new HashSet<string>(StringComparer.Ordinal)
    {
        "ca",
        "cert",
        "key",
        "dh",
        "extra-certs",
        "pkcs12",
        "crl-verify",
        "secret",
        "tls-auth",
        "tls-crypt",
        "tls-crypt-v2",
    };

    /// <summary>
    /// Directives that make OpenVPN run an external program. They are rejected by the interactive
    /// service for unauthorised callers and are worth surfacing to the user in any case.
    /// </summary>
    public static readonly IReadOnlySet<string> ScriptDirectives = new HashSet<string>(StringComparer.Ordinal)
    {
        "up",
        "down",
        "route-up",
        "route-pre-down",
        "ipchange",
        "tls-verify",
        "auth-user-pass-verify",
        "client-connect",
        "client-disconnect",
        "learn-address",
        "auth-user-pass-optional",
    };

    public OvpnConfiguration(
        IReadOnlyList<OvpnDirective> directives,
        IReadOnlyDictionary<string, OvpnInlineBlock> inlineBlocks)
    {
        ArgumentNullException.ThrowIfNull(directives);
        ArgumentNullException.ThrowIfNull(inlineBlocks);

        Directives = directives;
        InlineBlocks = inlineBlocks;
        Remotes = new ReadOnlyCollection<OvpnRemote>(ExtractRemotes(directives));
    }

    public IReadOnlyList<OvpnDirective> Directives { get; }

    public IReadOnlyDictionary<string, OvpnInlineBlock> InlineBlocks { get; }

    /// <summary>
    /// Every remote endpoint in declaration order. OpenVPN tries them in turn.
    /// </summary>
    public IReadOnlyList<OvpnRemote> Remotes { get; }

    /// <summary>
    /// True when the server expects interactive credentials and no credential file is referenced.
    /// </summary>
    public bool RequiresUserCredentials =>
        Directives.Any(directive => directive.Name == "auth-user-pass");

    /// <summary>
    /// Script directives present in this configuration, in declaration order.
    /// </summary>
    public IReadOnlyList<OvpnDirective> ScriptOptions =>
        Directives.Where(directive => ScriptDirectives.Contains(directive.Name)).ToList();

    /// <summary>
    /// Directives that still point at a file on disk and would break if the profile were moved.
    /// </summary>
    public IReadOnlyList<OvpnDirective> ExternalFileReferences =>
        Directives
            .Where(directive => InlinableDirectives.Contains(directive.Name))
            .Where(directive => directive.Arguments.Count > 0)
            .Where(directive => !InlineBlocks.ContainsKey(directive.Name))
            .ToList();

    /// <summary>
    /// True when nothing outside the configuration text is required to establish a connection.
    /// </summary>
    public bool IsSelfContained => ExternalFileReferences.Count == 0;

    /// <summary>
    /// OpenVPN refuses to start without a way to authenticate the server.
    /// </summary>
    public bool HasServerVerification =>
        InlineBlocks.ContainsKey("ca")
        || InlineBlocks.ContainsKey("pkcs12")
        || Directives.Any(directive => directive.Name is "ca" or "capath" or "peer-fingerprint" or "pkcs12");

    public OvpnDirective? FindDirective(string name) =>
        Directives.FirstOrDefault(directive => directive.Name == name);

    private static List<OvpnRemote> ExtractRemotes(IReadOnlyList<OvpnDirective> directives)
    {
        // A bare "proto" directive applies to every remote that does not carry its own.
        string? defaultProtocol = directives
            .FirstOrDefault(directive => directive.Name == "proto")?
            .FirstArgument;

        int? defaultPort = ParsePort(directives
            .FirstOrDefault(directive => directive.Name == "rport")?
            .FirstArgument);

        List<OvpnRemote> remotes = [];

        foreach (OvpnDirective directive in directives.Where(d => d.Name == "remote"))
        {
            if (directive.Arguments.Count == 0)
            {
                continue;
            }

            remotes.Add(new OvpnRemote(
                Host: directive.Arguments[0],
                Port: ParsePort(directive.ArgumentAt(1)) ?? defaultPort ?? 1194,
                Protocol: NormaliseProtocol(directive.ArgumentAt(2) ?? defaultProtocol)));
        }

        return remotes;
    }

    private static int? ParsePort(string? value) =>
        int.TryParse(value, out int port) && port is > 0 and <= 65535 ? port : null;

    // OpenVPN accepts udp, tcp, tcp-client, udp4, tcp6 and so on. Only the family matters here.
    private static OvpnProtocol NormaliseProtocol(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return OvpnProtocol.Udp;
        }

        return value.StartsWith("tcp", StringComparison.OrdinalIgnoreCase)
            ? OvpnProtocol.Tcp
            : OvpnProtocol.Udp;
    }
}

/// <summary>
/// One directive line, for example <c>remote vpn.example.com 1194 udp</c>.
/// </summary>
public sealed record OvpnDirective(string Name, IReadOnlyList<string> Arguments, int LineNumber)
{
    /// <summary>
    /// The first argument, or null when the directive is a bare flag.
    /// </summary>
    public string? FirstArgument => Arguments.Count > 0 ? Arguments[0] : null;

    /// <summary>
    /// The argument at <paramref name="index"/>, or null when the directive has fewer arguments.
    /// </summary>
    public string? ArgumentAt(int index) => index >= 0 && index < Arguments.Count ? Arguments[index] : null;

    public override string ToString() =>
        Arguments.Count == 0 ? Name : $"{Name} {string.Join(' ', Arguments)}";
}

/// <summary>
/// The contents of an inline block such as <c>&lt;ca&gt;...&lt;/ca&gt;</c>.
/// </summary>
public sealed record OvpnInlineBlock(string Name, string Content);

/// <summary>
/// A remote endpoint the client may connect to.
/// </summary>
public sealed record OvpnRemote(string Host, int Port, OvpnProtocol Protocol);

public enum OvpnProtocol
{
    Udp,
    Tcp,
}
