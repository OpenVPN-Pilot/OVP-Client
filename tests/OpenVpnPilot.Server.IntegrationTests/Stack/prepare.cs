// Writes what the integration test server needs and the repository must not hold: a certificate for
// localhost with its key, and an .env with a database password and keys generated here.
//
//     dotnet run tests/OpenVpnPilot.Server.IntegrationTests/Stack/prepare.cs
//
// A file that already exists is left alone, so running it again changes nothing. Delete certs/ and
// .env to start over; the database volume then has to go too (docker compose down -v), because it
// keeps the password it was created with.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

string stack = Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string
    ?? throw new InvalidOperationException("Run this with dotnet run <path to prepare.cs>.")))!;

string certificates = Path.Combine(stack, "certs");
string certificatePath = Path.Combine(certificates, "server.crt");
string keyPath = Path.Combine(certificates, "server.key");
string environmentPath = Path.Combine(stack, ".env");

Directory.CreateDirectory(certificates);

if (File.Exists(certificatePath) && File.Exists(keyPath))
{
    Console.WriteLine($"Kept {certificatePath}");
}
else
{
    using RSA key = RSA.Create(2048);
    CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    SubjectAlternativeNameBuilder names = new();
    names.AddDnsName("localhost");
    names.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(names.Build());
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

    using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));

    File.WriteAllText(certificatePath, certificate.ExportCertificatePem() + "\n");
    File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem() + "\n");
    Console.WriteLine($"Wrote {certificatePath}");
}

if (File.Exists(environmentPath))
{
    Console.WriteLine($"Kept {environmentPath}");
}
else
{
    string[] lines =
    [
        "# Written by prepare.cs for the integration test server only. Never copy it anywhere else.",
        $"OVP_DB_PASSWORD={Convert.ToHexString(RandomNumberGenerator.GetBytes(24))}",
        "OVP_DB_NAME=ovp",
        "OVP_DB_USER=ovp",
        "OVP_DB_MIGRATE_ON_START=true",
        $"OVP_DATA_KEY={Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}",
        $"OVP_JWT_SIGNING_KEY={Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))}",
        "OVP_SWAGGER_ENABLED=false",
        "OVP_TLS_MODE=kestrel",
        "OVP_HTTPS_PORT=8443",
        "OVP_TLS_CERT_PATH=/app/certs/server.crt",
        "OVP_TLS_KEY_PATH=/app/certs/server.key",
        "OVP_AUTH_MODE=file",
        "OVP_AUTH_FILE=/app/config/users.yaml",
        "OVP_LOGIN_ATTEMPTS_PER_MINUTE=120",
        "OVP_MIN_CLIENT_VERSION=0.0.0",
        "OVP_LOG_LEVEL=Information",
        "TZ=UTC",
    ];

    File.WriteAllLines(environmentPath, lines);
    Console.WriteLine($"Wrote {environmentPath}");
}

Console.WriteLine();
Console.WriteLine("Then, from that folder:  docker compose up -d --build --wait");
Console.WriteLine($"And for the tests:       OVP_TEST_SERVER_URL=https://localhost:18443/  OVP_TEST_SERVER_CERT={certificatePath}");
