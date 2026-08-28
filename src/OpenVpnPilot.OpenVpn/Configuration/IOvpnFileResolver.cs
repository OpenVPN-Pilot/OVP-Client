namespace OpenVpnPilot.OpenVpn.Configuration;

/// <summary>
/// Reads the auxiliary files a configuration references, such as certificates and keys.
/// </summary>
/// <remarks>
/// Abstracted so that inlining can be tested without touching the file system, and so that a future
/// import source such as an archive can supply the same content.
/// </remarks>
public interface IOvpnFileResolver
{
    /// <summary>
    /// Reads the referenced file, or returns null when it cannot be found.
    /// </summary>
    /// <param name="reference">The path exactly as written in the configuration.</param>
    /// <param name="baseDirectory">The directory the configuration itself came from.</param>
    public Task<byte[]?> ReadAsync(string reference, string baseDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves references against the real file system, relative to the configuration's own directory.
/// </summary>
public sealed class FileSystemOvpnFileResolver : IOvpnFileResolver
{
    public async Task<byte[]?> ReadAsync(string reference, string baseDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        string path = Path.IsPathRooted(reference)
            ? reference
            : Path.Combine(baseDirectory, reference);

        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }
}
