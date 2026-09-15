using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenVpnPilot.Data.Packaging;

/// <summary>
/// Reads and writes the package file itself.
/// </summary>
/// <remarks>
/// The file is a small binary container rather than an archive, because there is exactly one payload.
/// A ZIP would invite people to open it and edit the contents by hand, which is precisely what must
/// not happen to a file that carries private keys.
///
/// A package is always encrypted. It exists to be moved between machines, which means it will sit in
/// a download folder or an inbox at some point, and a single file carrying every private key in the
/// set is not something to leave readable. The payload is encrypted with AES in Galois counter mode
/// under a key derived from the passphrase with a random salt. The mode is authenticated, so a
/// package that was altered fails to open rather than opening with quietly different contents.
///
/// Reading still accepts an unencrypted payload, so a package written before this was required can
/// still be opened.
/// </remarks>
public static class ProfilePackageFile
{
    /// <summary>
    /// Identifies the file and its layout. A reader that does not recognise it refuses to guess.
    /// </summary>
    private static readonly byte[] Magic = "OVPPKG"u8.ToArray();

    private const byte PlainPayload = 0;
    private const byte EncryptedPayload = 1;

    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    /// <summary>
    /// Work factor for deriving the key. High enough to be a real obstacle, low enough that opening
    /// a package does not feel broken.
    /// </summary>
    private const int Iterations = 210_000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Writes a package, encrypted with the given passphrase.
    /// </summary>
    /// <exception cref="ArgumentException">The passphrase is missing or empty.</exception>
    public static async Task WriteAsync(
        string path,
        ProfilePackageContent content,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] encoded = Encode(content, passphrase);
        await File.WriteAllBytesAsync(path, encoded, cancellationToken);
    }

    /// <summary>
    /// The bytes of a package, encrypted with the given passphrase.
    /// </summary>
    /// <remarks>
    /// Separate from writing, because a shared library is written to a temporary file beside the
    /// real one and moved over it, and has to know the hash of exactly what it put there.
    /// </remarks>
    public static byte[] Encode(ProfilePackageContent content, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(content, SerializerOptions);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] cipher = new byte[payload.Length];
        byte[] tag = new byte[TagLength];

        byte[] key = DeriveKey(passphrase, salt);

        try
        {
            using AesGcm aes = new(key, TagLength);
            aes.Encrypt(nonce, payload, cipher, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
        }

        byte[] encoded = new byte[Magic.Length + 1 + SaltLength + NonceLength + TagLength + cipher.Length];
        Span<byte> span = encoded;

        Magic.CopyTo(span);
        span[Magic.Length] = EncryptedPayload;
        salt.CopyTo(span[(Magic.Length + 1)..]);
        nonce.CopyTo(span[(Magic.Length + 1 + SaltLength)..]);
        tag.CopyTo(span[(Magic.Length + 1 + SaltLength + NonceLength)..]);
        cipher.CopyTo(span[(Magic.Length + 1 + SaltLength + NonceLength + TagLength)..]);

        return encoded;
    }

    /// <summary>
    /// Reads a package.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file is not a package, or a passphrase is needed and was not supplied.
    /// </exception>
    /// <exception cref="CryptographicException">
    /// The passphrase is wrong, or the file was altered after it was written.
    /// </exception>
    public static async Task<ProfilePackageContent> ReadAsync(
        string path,
        string? passphrase = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] raw = await File.ReadAllBytesAsync(path, cancellationToken);
        return Decode(raw, passphrase);
    }

    /// <summary>
    /// Reads a package from its bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The bytes are not a package, a passphrase is needed and was not supplied, or a newer version
    /// wrote it.
    /// </exception>
    /// <exception cref="CryptographicException">
    /// The passphrase is wrong, or the bytes were altered after they were written.
    /// </exception>
    public static ProfilePackageContent Decode(byte[] raw, string? passphrase)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.Length < Magic.Length + 1 || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidOperationException("This file is not an OpenVpnPilot package.");
        }

        byte kind = raw[Magic.Length];
        ReadOnlySpan<byte> body = raw.AsSpan(Magic.Length + 1);

        byte[] payload = kind switch
        {
            PlainPayload => body.ToArray(),
            EncryptedPayload => Decrypt(body, passphrase),
            _ => throw new InvalidOperationException(
                "This package was written by a newer version and cannot be read."),
        };

        try
        {
            ProfilePackageContent content = JsonSerializer.Deserialize<ProfilePackageContent>(payload, SerializerOptions)
                ?? throw new InvalidOperationException("The package is empty.");

            // A newer layout may mean something this build would misread, and a shared library
            // written back from a misreading would take the newer version's data with it.
            if (content.FormatVersion > ProfilePackageContent.CurrentFormatVersion)
            {
                throw new PackageTooNewException(content.FormatVersion, content.WrittenBy);
            }

            return content;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The package could be opened but its contents could not be read.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// True when the file needs a passphrase, so a caller can ask before trying.
    /// </summary>
    public static async Task<bool> IsEncryptedAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using FileStream stream = File.OpenRead(path);

        byte[] header = new byte[Magic.Length + 1];
        int read = await stream.ReadAsync(header, cancellationToken);

        return read == header.Length
            && header.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            && header[Magic.Length] == EncryptedPayload;
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> body, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException("This package is protected by a passphrase.");
        }

        if (body.Length < SaltLength + NonceLength + TagLength)
        {
            throw new InvalidOperationException("The package is truncated.");
        }

        ReadOnlySpan<byte> salt = body[..SaltLength];
        ReadOnlySpan<byte> nonce = body.Slice(SaltLength, NonceLength);
        ReadOnlySpan<byte> tag = body.Slice(SaltLength + NonceLength, TagLength);
        ReadOnlySpan<byte> cipher = body[(SaltLength + NonceLength + TagLength)..];

        byte[] payload = new byte[cipher.Length];
        byte[] key = DeriveKey(passphrase, salt);

        try
        {
            using AesGcm aes = new(key, TagLength);
            aes.Decrypt(nonce, cipher, tag, payload);
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyLength);
}

/// <summary>
/// A package written in a layout newer than this build understands.
/// </summary>
public sealed class PackageTooNewException : InvalidOperationException
{
    public PackageTooNewException()
        : base("This package was written by a newer version and cannot be read.")
    {
    }

    public PackageTooNewException(string message)
        : base(message)
    {
    }

    public PackageTooNewException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PackageTooNewException(int formatVersion, string writtenBy)
        : base($"This package uses format {formatVersion}, written by version {writtenBy}, and this version reads up to {ProfilePackageContent.CurrentFormatVersion}.")
    {
        FormatVersion = formatVersion;
        WrittenBy = writtenBy;
    }

    public int FormatVersion { get; }

    public string WrittenBy { get; } = string.Empty;
}
