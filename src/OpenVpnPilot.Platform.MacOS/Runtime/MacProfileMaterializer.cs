using System.Runtime.Versioning;
using System.Text;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.MacOS.Runtime;

/// <summary>
/// Writes configurations to a directory only the current user can open, one file per connection.
/// </summary>
/// <remarks>
/// A materialised configuration carries the private key inline, so the directory is created with
/// 0700 and every file with 0600, both before a single byte is written. The mode is given when the
/// file is created rather than set afterwards, which leaves no moment in which the key sits in a
/// file anyone else could open.
///
/// The directory lives under the application's own data directory. Unlike the Windows interactive
/// service, the macOS helper never opens this file: the launcher reads it and hands the text over,
/// and the helper keeps its own copy where only root can read it. Nothing here therefore needs to be
/// reachable by another account, and nothing is.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacProfileMaterializer : IProfileMaterializer
{
    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string rootDirectory;

    public MacProfileMaterializer(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = rootDirectory;
    }

    public async Task<MaterialisedProfile> MaterialiseAsync(
        Guid profileId,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        EnsurePrivateDirectory();

        string path = Path.Combine(rootDirectory, $"{profileId:N}.ovpn");

        // A leftover keeps the mode it was created with, and truncating it would keep that mode too.
        // Removing it first means the file written below is always a new one with the mode above.
        File.Delete(path);

        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = PrivateFile,
            Options = FileOptions.Asynchronous,
        };

        try
        {
            await using FileStream stream = new(path, options);
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(configuration), cancellationToken);
        }
        catch
        {
            // Never leave a configuration behind that was not written completely.
            TryDelete(path);
            throw;
        }

        return new MaterialisedProfile(path);
    }

    public int RemoveStaleFiles()
    {
        if (!Directory.Exists(rootDirectory))
        {
            return 0;
        }

        int removed = 0;

        foreach (string path in Directory.EnumerateFiles(rootDirectory, "*.ovpn"))
        {
            if (TryDelete(path))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Creates the directory with owner only access, or takes an existing one back to it.
    /// </summary>
    /// <remarks>
    /// A symbolic link in its place is refused rather than followed: it would have the key written
    /// wherever the link pointed, with whatever access that place grants.
    /// </remarks>
    private void EnsurePrivateDirectory()
    {
        DirectoryInfo directory = new(rootDirectory);

        if (directory.Exists && directory.LinkTarget is not null)
        {
            throw new IOException($"{rootDirectory} is a symbolic link, so no configuration is written there.");
        }

        if (!directory.Exists)
        {
            Directory.CreateDirectory(rootDirectory, PrivateDirectory);
        }

        if (File.GetUnixFileMode(rootDirectory) != PrivateDirectory)
        {
            File.SetUnixFileMode(rootDirectory, PrivateDirectory);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            // Removed on the next start instead.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as above.
            return false;
        }
    }
}
