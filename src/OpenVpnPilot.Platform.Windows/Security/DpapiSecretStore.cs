using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Security;

/// <summary>
/// Protects credentials with the Windows data protection interface, one file per secret.
/// </summary>
/// <remarks>
/// The blob is bound to the current user, so it cannot be read by another account and does not
/// survive being copied to another machine. The reference is mixed into the additional entropy as
/// well, which means a file renamed to another reference fails to decrypt rather than silently
/// answering with the wrong credentials.
///
/// The file name is a hash of the reference rather than the reference itself, because a reference is
/// free form text and a profile name has no business becoming a path. The reference is stored inside
/// the protected payload, which is what makes listing possible without leaking it on disk.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private const string FileExtension = ".secret";

    /// <summary>
    /// Fixed entropy component. It is not a secret; it separates these blobs from any other data this
    /// user protects, so an unrelated blob cannot be substituted for one of ours.
    /// </summary>
    private static readonly byte[] ApplicationEntropy =
        Encoding.UTF8.GetBytes("OpenVpnPilot.SecretStore.v1");

    private readonly string directory;

    public DpapiSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = directory;
    }

    public bool IsAvailable => OperatingSystem.IsWindows();

    public async Task<StoredSecret?> TryReadAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        string path = PathFor(reference);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            Payload? payload = Unprotect(protectedBytes, reference);

            // A payload whose reference does not match means the file was moved or tampered with.
            return payload is null || !string.Equals(payload.Reference, reference, StringComparison.Ordinal)
                ? null
                : new StoredSecret(payload.Username, payload.Password);
        }
        catch (CryptographicException)
        {
            // Written by another user or another machine. Treated as absent so the caller prompts.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task WriteAsync(
        string reference,
        StoredSecret secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(secret);

        Directory.CreateDirectory(directory);

        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(reference, secret.Username, secret.Password));

        byte[] protectedBytes = ProtectedData.Protect(
            plain,
            EntropyFor(reference),
            DataProtectionScope.CurrentUser);

        CryptographicOperations.ZeroMemory(plain);

        string path = PathFor(reference);
        string temporary = path + ".tmp";

        await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        TryDelete(PathFor(reference));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<string> references = [];

        foreach (string path in Directory.EnumerateFiles(directory, "*" + FileExtension))
        {
            string? reference = await TryReadReferenceAsync(path, cancellationToken);

            if (reference is not null)
            {
                references.Add(reference);
            }
        }

        references.Sort(StringComparer.Ordinal);
        return references;
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> references = await ListAsync(cancellationToken);

        foreach (string reference in references)
        {
            TryDelete(PathFor(reference));
        }

        return references.Count;
    }

    /// <summary>
    /// Recovers the reference from a file by decrypting it, which is the only place it is kept.
    /// </summary>
    /// <remarks>
    /// The entropy is derived from the file name, and the file name is derived from the reference,
    /// so the blob can be opened without knowing the reference in advance.
    /// </remarks>
    private static async Task<string?> TryReadReferenceAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);

            byte[] plain = ProtectedData.Unprotect(
                protectedBytes,
                EntropyForFileName(Path.GetFileNameWithoutExtension(path)),
                DataProtectionScope.CurrentUser);

            try
            {
                return JsonSerializer.Deserialize<Payload>(plain)?.Reference;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static Payload? Unprotect(byte[] protectedBytes, string reference)
    {
        byte[] plain = ProtectedData.Unprotect(
            protectedBytes,
            EntropyFor(reference),
            DataProtectionScope.CurrentUser);

        try
        {
            return JsonSerializer.Deserialize<Payload>(plain);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private string PathFor(string reference) =>
        Path.Combine(directory, FileNameFor(reference) + FileExtension);

    private static string FileNameFor(string reference) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))).ToLowerInvariant();

    /// <summary>
    /// Entropy bound to the reference, so a blob only decrypts under the key it was written for.
    /// </summary>
    private static byte[] EntropyFor(string reference) => EntropyForFileName(FileNameFor(reference));

    private static byte[] EntropyForFileName(string fileName)
    {
        byte[] name = Encoding.UTF8.GetBytes(fileName);
        byte[] entropy = new byte[ApplicationEntropy.Length + name.Length];

        ApplicationEntropy.CopyTo(entropy, 0);
        name.CopyTo(entropy, ApplicationEntropy.Length);

        return entropy;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The secret stays until the next attempt. Failing a settings save over it would be worse.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as above.
        }
    }

    /// <summary>
    /// What is actually encrypted. The reference travels with the secret so listing can recover it.
    /// </summary>
    private sealed record Payload(string Reference, string? Username, string Password);
}
