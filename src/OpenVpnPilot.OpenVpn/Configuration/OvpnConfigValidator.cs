namespace OpenVpnPilot.OpenVpn.Configuration;

/// <summary>
/// Says what is wrong with a configuration before OpenVPN is asked to read it.
/// </summary>
/// <remarks>
/// A configuration that OpenVPN refuses fails while its options are parsed, before the management
/// interface listens, so what the client observes is a process that ends at once and says nothing it
/// can pass on. An editor that saves such a file has only moved the failure to the next connection.
/// These are the mistakes that are made by hand and that OpenVPN is known to refuse: no server, a
/// port or protocol it cannot read, nothing to verify the server with, a block that was never closed
/// and a certificate or key pasted into the wrong block.
///
/// Findings are codes with arguments rather than sentences, because this layer is shared by every
/// interface and the wording belongs to whichever one shows it.
/// </remarks>
public static class OvpnConfigValidator
{
    private static readonly HashSet<string> ClientProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "udp", "udp4", "udp6",
        "tcp", "tcp4", "tcp6",
        "tcp-client", "tcp4-client", "tcp6-client",
    };

    private static readonly HashSet<string> CertificateBlocks = new(StringComparer.Ordinal)
    {
        "ca", "cert", "extra-certs",
    };

    private static readonly HashSet<string> StaticKeyBlocks = new(StringComparer.Ordinal)
    {
        "tls-auth", "tls-crypt", "secret",
    };

    public static IReadOnlyList<OvpnConfigIssue> Validate(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        OvpnConfiguration configuration = OvpnConfigParser.Parse(content);
        List<OvpnConfigIssue> issues = [];

        if (configuration.UnclosedBlock is { } unclosed)
        {
            issues.Add(OvpnConfigIssue.Error(
                OvpnConfigIssueCode.UnclosedBlock,
                unclosed.LineNumber,
                unclosed.Name));
        }

        foreach (OvpnDirective directive in configuration.Directives)
        {
            if (directive.Name.StartsWith("</", StringComparison.Ordinal))
            {
                issues.Add(OvpnConfigIssue.Error(
                    OvpnConfigIssueCode.StrayClosingTag,
                    directive.LineNumber,
                    directive.Name.Trim('<', '/', '>')));
            }
        }

        CheckRemotes(configuration, issues);
        CheckProtocolDirective(configuration, issues);
        CheckPortDirectives(configuration, issues);

        if (!configuration.HasServerVerification)
        {
            issues.Add(OvpnConfigIssue.Error(OvpnConfigIssueCode.NoServerVerification, 0));
        }

        CheckBlockContents(configuration, issues);

        foreach (OvpnDirective reference in configuration.ExternalFileReferences)
        {
            issues.Add(OvpnConfigIssue.Warning(
                OvpnConfigIssueCode.ExternalFile,
                reference.LineNumber,
                reference.Name,
                reference.FirstArgument ?? string.Empty));
        }

        foreach (OvpnDirective script in configuration.ScriptOptions)
        {
            issues.Add(OvpnConfigIssue.Warning(
                OvpnConfigIssueCode.ScriptDirective,
                script.LineNumber,
                script.Name));
        }

        return issues.OrderBy(issue => issue.Severity).ThenBy(issue => issue.LineNumber).ToList();
    }

    private static void CheckRemotes(OvpnConfiguration configuration, List<OvpnConfigIssue> issues)
    {
        List<OvpnDirective> remotes = configuration.Directives
            .Where(directive => directive.Name == "remote")
            .ToList();

        if (remotes.Count == 0 || remotes.All(remote => remote.Arguments.Count == 0))
        {
            issues.Add(OvpnConfigIssue.Error(OvpnConfigIssueCode.NoRemote, remotes.FirstOrDefault()?.LineNumber ?? 0));
            return;
        }

        foreach (OvpnDirective remote in remotes)
        {
            if (remote.ArgumentAt(1) is { } port && !IsPort(port))
            {
                issues.Add(OvpnConfigIssue.Error(OvpnConfigIssueCode.InvalidPort, remote.LineNumber, port));
            }

            if (remote.ArgumentAt(2) is { } protocol && !ClientProtocols.Contains(protocol))
            {
                issues.Add(OvpnConfigIssue.Error(OvpnConfigIssueCode.InvalidProtocol, remote.LineNumber, protocol));
            }
        }
    }

    private static void CheckProtocolDirective(OvpnConfiguration configuration, List<OvpnConfigIssue> issues)
    {
        foreach (OvpnDirective proto in configuration.Directives.Where(directive => directive.Name == "proto"))
        {
            string value = proto.FirstArgument ?? string.Empty;

            if (!ClientProtocols.Contains(value))
            {
                issues.Add(OvpnConfigIssue.Error(OvpnConfigIssueCode.InvalidProtocol, proto.LineNumber, value));
            }
        }
    }

    private static void CheckPortDirectives(OvpnConfiguration configuration, List<OvpnConfigIssue> issues)
    {
        foreach (OvpnDirective port in configuration.Directives.Where(directive => directive.Name is "port" or "rport"))
        {
            string value = port.FirstArgument ?? string.Empty;

            if (!IsPort(value))
            {
                issues.Add(OvpnConfigIssue.Error(OvpnConfigIssueCode.InvalidPort, port.LineNumber, value));
            }
        }
    }

    /// <summary>
    /// Checks that a block holds the kind of material its name promises.
    /// </summary>
    /// <remarks>
    /// Pasting a key where a certificate belongs is the most likely slip when a key is replaced by
    /// hand, and OpenVPN reports it as a failure to read the file rather than naming the block.
    /// Only the armour lines are looked at; whether what is between them is valid is OpenVPN's job.
    /// </remarks>
    private static void CheckBlockContents(OvpnConfiguration configuration, List<OvpnConfigIssue> issues)
    {
        foreach (OvpnInlineBlock block in configuration.InlineBlocks.Values)
        {
            OvpnConfigIssueCode? problem = block.Name switch
            {
                _ when CertificateBlocks.Contains(block.Name) =>
                    IsArmoured(block.Content, "CERTIFICATE") ? null : OvpnConfigIssueCode.NotACertificate,
                "key" =>
                    IsArmoured(block.Content, "PRIVATE KEY") ? null : OvpnConfigIssueCode.NotAPrivateKey,
                _ when StaticKeyBlocks.Contains(block.Name) =>
                    IsArmoured(block.Content, "OpenVPN Static key V1") ? null : OvpnConfigIssueCode.NotAStaticKey,
                "tls-crypt-v2" =>
                    IsArmoured(block.Content, "OpenVPN tls-crypt-v2 client key") ? null : OvpnConfigIssueCode.NotATlsCryptV2Key,
                _ => null,
            };

            if (problem is { } code)
            {
                issues.Add(OvpnConfigIssue.Error(code, 0, block.Name));
            }
        }
    }

    /// <summary>
    /// True when the text opens and closes at least one armoured section whose label ends as given.
    /// </summary>
    /// <remarks>
    /// Ends with rather than equals, because a private key is labelled RSA, EC or ENCRYPTED in
    /// front of the words that matter here, and every one of those is a private key.
    /// </remarks>
    private static bool IsArmoured(string content, string label)
    {
        int begins = 0;
        int ends = 0;

        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("-----BEGIN ", StringComparison.Ordinal)
                && line.EndsWith(label + "-----", StringComparison.Ordinal))
            {
                begins++;
            }
            else if (line.StartsWith("-----END ", StringComparison.Ordinal)
                && line.EndsWith(label + "-----", StringComparison.Ordinal))
            {
                ends++;
            }
        }

        return begins > 0 && begins == ends;
    }

    private static bool IsPort(string value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port)
        && port is > 0 and <= 65535;
}

/// <summary>
/// One thing a configuration gets wrong.
/// </summary>
/// <param name="Severity">Whether OpenVPN would refuse it, or merely deserves to be told about it.</param>
/// <param name="Code">What is wrong.</param>
/// <param name="LineNumber">The line it was found on, or zero when it concerns the file as a whole.</param>
/// <param name="Arguments">The values the description names, such as a directive or a port.</param>
public sealed record OvpnConfigIssue(
    OvpnConfigIssueSeverity Severity,
    OvpnConfigIssueCode Code,
    int LineNumber,
    IReadOnlyList<string> Arguments)
{
    public bool IsError => Severity == OvpnConfigIssueSeverity.Error;

    public static OvpnConfigIssue Error(OvpnConfigIssueCode code, int lineNumber, params string[] arguments) =>
        new(OvpnConfigIssueSeverity.Error, code, lineNumber, arguments);

    public static OvpnConfigIssue Warning(OvpnConfigIssueCode code, int lineNumber, params string[] arguments) =>
        new(OvpnConfigIssueSeverity.Warning, code, lineNumber, arguments);
}

public enum OvpnConfigIssueSeverity
{
    /// <summary>
    /// OpenVPN would refuse to start with this configuration.
    /// </summary>
    Error,

    /// <summary>
    /// The configuration starts, but something about it will not work the way it reads.
    /// </summary>
    Warning,
}

public enum OvpnConfigIssueCode
{
    NoRemote,
    InvalidPort,
    InvalidProtocol,
    NoServerVerification,
    UnclosedBlock,
    StrayClosingTag,
    NotACertificate,
    NotAPrivateKey,
    NotAStaticKey,
    NotATlsCryptV2Key,
    ExternalFile,
    ScriptDirective,
}
