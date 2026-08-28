namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Writes a stored profile to disk so that OpenVPN can read it, and removes it afterwards.
/// </summary>
/// <remarks>
/// A materialised configuration carries the private key inline, so the file must be readable only by
/// the current user and by the account the tunnel runs under. The implementation is responsible for
/// applying that restriction; a plain temporary file is not acceptable.
/// </remarks>
public interface IProfileMaterializer
{
    /// <summary>
    /// Writes the configuration to a private file.
    /// </summary>
    /// <returns>A handle that deletes the file when disposed.</returns>
    public Task<MaterialisedProfile> MaterialiseAsync(
        Guid profileId,
        string configuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes configurations left behind by an earlier run.
    /// </summary>
    /// <remarks>
    /// Called at startup, when by definition nothing is connected yet. A crash or a forced exit
    /// skips the normal cleanup, and a materialised configuration contains a private key, so it must
    /// not survive the session that created it.
    /// </remarks>
    /// <returns>The number of files removed.</returns>
    public int RemoveStaleFiles();
}

/// <summary>
/// A configuration file that exists only for the lifetime of a connection.
/// </summary>
public sealed class MaterialisedProfile : IAsyncDisposable
{
    private readonly string path;

    public MaterialisedProfile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
    }

    public string Path => path;

    public string Directory => System.IO.Path.GetDirectoryName(path)
        ?? throw new InvalidOperationException("A materialised profile must live in a directory.");

    public ValueTask DisposeAsync()
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The file is removed on the next start instead. Failing a disconnect over a locked
            // temporary file would be worse than leaving it behind for one session.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as above.
        }

        return ValueTask.CompletedTask;
    }
}
