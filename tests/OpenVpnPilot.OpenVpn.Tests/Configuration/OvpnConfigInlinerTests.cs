using System.Text;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.OpenVpn.Tests.Configuration;

public sealed class OvpnConfigInlinerTests
{
    private const string CaText = "-----BEGIN CERTIFICATE-----\nMIIDQjCCAiqgAwIB\n-----END CERTIFICATE-----";
    private const string CertText = "-----BEGIN CERTIFICATE-----\nMIIDSjCCAjKgAwIB\n-----END CERTIFICATE-----";
    private const string KeyText = "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkq\n-----END PRIVATE KEY-----";
    private const string TlsAuthText = "-----BEGIN OpenVPN Static key V1-----\n6d1d2b1e\n-----END OpenVPN Static key V1-----";

    [Fact]
    public async Task InlineAsync_ExternalCertificates_BecomeInlineBlocks()
    {
        FakeFileResolver resolver = new()
        {
            ["ca.crt"] = CaText,
            ["client.crt"] = CertText,
            ["client.key"] = KeyText,
        };

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("""
            client
            remote vpn.example.com 1194 udp
            ca ca.crt
            cert client.crt
            key client.key
            verb 3
            """, @"C:\profiles");

        Assert.True(result.IsComplete);
        Assert.Equal(3, result.InlinedDirectives.Count);

        OvpnConfiguration parsed = OvpnConfigParser.Parse(result.Content);
        Assert.True(parsed.IsSelfContained);
        Assert.Equal(CaText, parsed.InlineBlocks["ca"].Content);
        Assert.Equal(CertText, parsed.InlineBlocks["cert"].Content);
        Assert.Equal(KeyText, parsed.InlineBlocks["key"].Content);
    }

    [Fact]
    public async Task InlineAsync_PreservesUnrelatedDirectivesAndTheirOrder()
    {
        FakeFileResolver resolver = new() { ["ca.crt"] = CaText };

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("""
            client
            dev tun
            ca ca.crt
            verb 3
            """, @"C:\profiles");

        OvpnConfiguration parsed = OvpnConfigParser.Parse(result.Content);

        Assert.Equal(["client", "dev", "verb"], parsed.Directives.Select(d => d.Name));
        Assert.StartsWith("client\ndev tun\n<ca>", result.Content.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.EndsWith("</ca>\nverb 3", result.Content.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InlineAsync_TlsAuthDirection_IsRestatedAsKeyDirection()
    {
        FakeFileResolver resolver = new() { ["ta.key"] = TlsAuthText };

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("""
            client
            tls-auth ta.key 1
            """, @"C:\profiles");

        OvpnConfiguration parsed = OvpnConfigParser.Parse(result.Content);

        Assert.Equal(TlsAuthText, parsed.InlineBlocks["tls-auth"].Content);

        OvpnDirective keyDirection = Assert.Single(parsed.Directives, d => d.Name == "key-direction");
        Assert.Equal("1", keyDirection.FirstArgument);
    }

    [Fact]
    public async Task InlineAsync_TlsAuthWithoutDirection_AddsNoKeyDirection()
    {
        FakeFileResolver resolver = new() { ["ta.key"] = TlsAuthText };

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("""
            client
            tls-auth ta.key
            """, @"C:\profiles");

        OvpnConfiguration parsed = OvpnConfigParser.Parse(result.Content);

        Assert.DoesNotContain(parsed.Directives, d => d.Name == "key-direction");
    }

    [Fact]
    public async Task InlineAsync_MissingFile_IsReportedAndTheDirectiveIsLeftAlone()
    {
        FakeFileResolver resolver = new() { ["ca.crt"] = CaText };

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("""
            client
            ca ca.crt
            cert missing.crt
            """, @"C:\profiles");

        Assert.False(result.IsComplete);

        OvpnInlineFailure failure = Assert.Single(result.Failures);
        Assert.Equal("cert", failure.Directive);
        Assert.Equal("missing.crt", failure.Reference);
        Assert.Equal(OvpnInlineFailureReason.NotFound, failure.Reason);

        OvpnConfiguration parsed = OvpnConfigParser.Parse(result.Content);
        Assert.True(parsed.InlineBlocks.ContainsKey("ca"));
        Assert.Contains(parsed.Directives, d => d.Name == "cert");
    }

    [Fact]
    public async Task InlineAsync_AlreadySelfContained_ReturnsInputUnchanged()
    {
        const string original = """
            client
            <ca>
            -----BEGIN CERTIFICATE-----
            MIIDQjCCAiqgAwIB
            -----END CERTIFICATE-----
            </ca>
            """;

        OvpnInlineResult result = await new OvpnConfigInliner(new FakeFileResolver()).InlineAsync(original, @"C:\p");

        Assert.True(result.IsComplete);
        Assert.Empty(result.InlinedDirectives);
        Assert.Equal(original, result.Content);
    }

    [Fact]
    public async Task InlineAsync_StripsByteOrderMarkFromCertificateFiles()
    {
        FakeFileResolver resolver = new();
        resolver.SetBytes("ca.crt", new UTF8Encoding(true).GetBytes(CaText));

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("ca ca.crt", @"C:\p");

        OvpnConfiguration parsed = OvpnConfigParser.Parse(result.Content);
        Assert.Equal(CaText, parsed.InlineBlocks["ca"].Content);
    }

    [Fact]
    public async Task InlineAsync_NormalisesWindowsLineEndingsInsideBlocks()
    {
        FakeFileResolver resolver = new();
        resolver.SetBytes("ca.crt", Encoding.UTF8.GetBytes(CaText.ReplaceLineEndings("\r\n")));

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("ca ca.crt", @"C:\p");

        OvpnConfiguration parsed = OvpnConfigParser.Parse(result.Content);
        Assert.Equal(CaText, parsed.InlineBlocks["ca"].Content);
    }

    [Fact]
    public async Task InlineAsync_Pkcs12_IsEmbeddedAsBase64()
    {
        byte[] binary = [0x30, 0x82, 0x00, 0x01, 0xFF, 0xFE];
        FakeFileResolver resolver = new();
        resolver.SetBytes("client.p12", binary);

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("pkcs12 client.p12", @"C:\p");

        Assert.True(result.IsComplete);

        OvpnConfiguration parsed = OvpnConfigParser.Parse(result.Content);
        Assert.Equal(Convert.ToBase64String(binary), parsed.InlineBlocks["pkcs12"].Content);
    }

    [Fact]
    public async Task InlineAsync_BinaryContentForATextDirective_IsRefusedRatherThanCorrupted()
    {
        FakeFileResolver resolver = new();
        resolver.SetBytes("ca.crt", [0x00, 0x01, 0x02, 0x00]);

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("ca ca.crt", @"C:\p");

        Assert.False(result.IsComplete);
        Assert.Equal(OvpnInlineFailureReason.NotTextContent, Assert.Single(result.Failures).Reason);
    }

    [Fact]
    public async Task InlineAsync_QuotedPathWithSpaces_IsResolved()
    {
        FakeFileResolver resolver = new() { [@"certs\example ca.crt"] = CaText };

        OvpnInlineResult result = await new OvpnConfigInliner(resolver).InlineAsync("""
            ca "certs\example ca.crt"
            """, @"C:\p");

        Assert.True(result.IsComplete);
        Assert.Equal(CaText, OvpnConfigParser.Parse(result.Content).InlineBlocks["ca"].Content);
    }

    private sealed class FakeFileResolver : IOvpnFileResolver
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);

        public string this[string reference]
        {
            set => files[reference] = Encoding.UTF8.GetBytes(value);
        }

        public void SetBytes(string reference, byte[] content) => files[reference] = content;

        public Task<byte[]?> ReadAsync(string reference, string baseDirectory, CancellationToken cancellationToken)
        {
            return Task.FromResult(files.GetValueOrDefault(reference));
        }
    }
}
