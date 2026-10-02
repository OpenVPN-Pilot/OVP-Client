using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenVpnPilot.Server.IntegrationTests;

/// <summary>
/// Where the server under test is and how to reach it, from the environment.
/// </summary>
/// <remarks>
/// <c>OVP_TEST_SERVER_URL</c> turns the tests on. <c>OVP_TEST_SERVER_CERT</c> names the certificate
/// the server presents; it is the only one the tests accept. <c>OVP_TEST_API_CONTAINER</c> and
/// <c>OVP_TEST_DATABASE_CONTAINER</c> name the containers, for the tests that stop the server or
/// reach into its database; without them those tests are skipped.
/// </remarks>
internal static class TestServer
{
    public const string UrlVariable = "OVP_TEST_SERVER_URL";
    public const string CertificateVariable = "OVP_TEST_SERVER_CERT";
    public const string ApiContainerVariable = "OVP_TEST_API_CONTAINER";
    public const string DatabaseContainerVariable = "OVP_TEST_DATABASE_CONTAINER";

    public const string Administrator = "alice";
    public const string AdministratorPassword = "Alice-Test-Pass-2026";
    public const string User = "bob";
    public const string UserPassword = "Bob-Test-Pass-2026";
    public const string Revocable = "carol";
    public const string RevocablePassword = "Carol-Test-Pass-2026";

    public static Uri Address => new(Environment.GetEnvironmentVariable(UrlVariable)
        ?? throw new InvalidOperationException($"{UrlVariable} is not set."));

    public static string? ApiContainer => Environment.GetEnvironmentVariable(ApiContainerVariable);

    public static string? DatabaseContainer => Environment.GetEnvironmentVariable(DatabaseContainerVariable);

    /// <summary>
    /// The certificate's text, which doubles as the certificate authority of the profiles the tests
    /// upload: the server wants a configuration that carries one.
    /// </summary>
    public static string CertificatePem => File.ReadAllText(CertificatePath).Trim();

    private static string CertificatePath => Environment.GetEnvironmentVariable(CertificateVariable)
        ?? throw new InvalidOperationException($"{CertificateVariable} is not set.");

    /// <summary>
    /// The innermost handler: the network, trusting the test server's certificate and nothing else.
    /// </summary>
    /// <remarks>
    /// Test code only. The application has no such switch and must never have one: there the
    /// operating system decides, and a certificate it does not trust is refused.
    /// </remarks>
    public static HttpMessageHandler CreateNetwork()
    {
        using X509Certificate2 expected = X509Certificate2.CreateFromPem(CertificatePem);
        byte[] thumbprint = expected.GetCertHash(HashAlgorithmName.SHA256);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), thumbprint),
            },
        };
    }

    /// <summary>
    /// Runs the Docker command line, for the tests that stop the server or change its database.
    /// </summary>
    public static async Task DockerAsync(params string[] arguments)
    {
        ProcessStartInfo start = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("docker could not be started.");
        string error = await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', arguments)} failed: {error}");
        }
    }

    /// <summary>
    /// A configuration the server accepts, unique to the name, so no two tests ever upload a duplicate.
    /// </summary>
    public static string Configuration(string name) =>
        $"client\ndev tun\nproto udp\nremote {name}.example.com 1194\nnobind\nverb 3\n<ca>\n{CertificatePem}\n</ca>\n";

    /// <summary>
    /// A name no other test and no earlier run uses.
    /// </summary>
    public static string UniqueName(string what) => $"it-{what}-{Guid.NewGuid():N}";
}

/// <summary>
/// A test that needs the server, skipped with the reason when the environment does not name one.
/// </summary>
public sealed class ServerFactAttribute : FactAttribute
{
    public ServerFactAttribute(string? alsoNeeds = null)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestServer.UrlVariable)))
        {
            Skip = $"No test server: set {TestServer.UrlVariable} and {TestServer.CertificateVariable}, see docs/development.md.";
        }
        else if (alsoNeeds is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(alsoNeeds)))
        {
            Skip = $"Needs {alsoNeeds}, see docs/development.md.";
        }
    }
}
