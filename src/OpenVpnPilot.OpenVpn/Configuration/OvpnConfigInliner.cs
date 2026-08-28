using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace OpenVpnPilot.OpenVpn.Configuration;

/// <summary>
/// Rewrites a configuration so that every referenced certificate or key becomes an inline block.
/// </summary>
/// <remarks>
/// A self contained profile is a single piece of text. That is what makes storing profiles in the
/// database, exporting them and moving them between machines work without carrying a directory of
/// certificates alongside. Everything that is not rewritten is preserved byte for byte.
/// </remarks>
public sealed class OvpnConfigInliner
{
    private readonly IOvpnFileResolver fileResolver;

    public OvpnConfigInliner(IOvpnFileResolver fileResolver)
    {
        ArgumentNullException.ThrowIfNull(fileResolver);
        this.fileResolver = fileResolver;
    }

    public async Task<OvpnInlineResult> InlineAsync(
        string content,
        string baseDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(baseDirectory);

        OvpnConfiguration configuration = OvpnConfigParser.Parse(content);
        IReadOnlyList<OvpnDirective> references = configuration.ExternalFileReferences;

        if (references.Count == 0)
        {
            return new OvpnInlineResult(content, [], []);
        }

        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        Dictionary<int, string> replacements = [];
        List<string> inlined = [];
        List<OvpnInlineFailure> failures = [];

        foreach (OvpnDirective directive in references)
        {
            string? reference = directive.FirstArgument;
            if (reference is null)
            {
                continue;
            }

            byte[]? bytes = await fileResolver.ReadAsync(reference, baseDirectory, cancellationToken);
            if (bytes is null)
            {
                failures.Add(new OvpnInlineFailure(directive.Name, reference, OvpnInlineFailureReason.NotFound));
                continue;
            }

            if (!TryRenderBlock(directive, bytes, out string? block, out OvpnInlineFailureReason reason))
            {
                failures.Add(new OvpnInlineFailure(directive.Name, reference, reason));
                continue;
            }

            // LineNumber is one based and counts every source line, so it maps straight onto the array.
            replacements[directive.LineNumber - 1] = block;
            inlined.Add(directive.Name);
        }

        if (replacements.Count == 0)
        {
            return new OvpnInlineResult(content, inlined, failures);
        }

        StringBuilder builder = new(content.Length + (replacements.Count * 2048));
        for (int index = 0; index < lines.Length; index++)
        {
            if (replacements.TryGetValue(index, out string? replacement))
            {
                builder.Append(replacement);
            }
            else
            {
                builder.Append(lines[index]);
            }

            if (index < lines.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return new OvpnInlineResult(builder.ToString(), inlined, failures);
    }

    private static bool TryRenderBlock(
        OvpnDirective directive,
        byte[] bytes,
        [NotNullWhen(true)] out string? block,
        out OvpnInlineFailureReason reason)
    {
        block = null;
        reason = OvpnInlineFailureReason.None;

        // pkcs12 is a binary container. OpenVPN accepts it inline as base64.
        string body = directive.Name == "pkcs12"
            ? WrapBase64(Convert.ToBase64String(bytes))
            : DecodeText(bytes);

        if (directive.Name != "pkcs12" && body.Contains('\0', StringComparison.Ordinal))
        {
            reason = OvpnInlineFailureReason.NotTextContent;
            return false;
        }

        StringBuilder builder = new();
        builder.Append('<').Append(directive.Name).Append(">\n");
        builder.Append(body.TrimEnd('\r', '\n')).Append('\n');
        builder.Append("</").Append(directive.Name).Append('>');

        // "tls-auth <file> <direction>" loses its direction argument when it becomes a block,
        // so the direction has to be re-stated as a separate directive.
        string? direction = directive.Name == "tls-auth" ? directive.ArgumentAt(1) : null;
        if (direction is not null)
        {
            builder.Append("\nkey-direction ").Append(direction);
        }

        block = builder.ToString();
        return true;
    }

    private static string DecodeText(byte[] bytes)
    {
        // Certificates are ASCII, but files exported from Windows tools often carry a BOM.
        return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿').ReplaceLineEndings("\n");
    }

    private static string WrapBase64(string base64)
    {
        StringBuilder builder = new(base64.Length + (base64.Length / 64) + 1);
        for (int offset = 0; offset < base64.Length; offset += 64)
        {
            builder.Append(base64.AsSpan(offset, Math.Min(64, base64.Length - offset))).Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary>
/// The outcome of an inlining pass.
/// </summary>
public sealed record OvpnInlineResult(
    string Content,
    IReadOnlyList<string> InlinedDirectives,
    IReadOnlyList<OvpnInlineFailure> Failures)
{
    /// <summary>
    /// True when every referenced file was pulled in and the result needs nothing else on disk.
    /// </summary>
    public bool IsComplete => Failures.Count == 0;
}

public sealed record OvpnInlineFailure(string Directive, string Reference, OvpnInlineFailureReason Reason);

public enum OvpnInlineFailureReason
{
    None,

    /// <summary>
    /// The referenced file does not exist next to the configuration.
    /// </summary>
    NotFound,

    /// <summary>
    /// The file is binary and the directive has no inline representation.
    /// </summary>
    NotTextContent,
}
