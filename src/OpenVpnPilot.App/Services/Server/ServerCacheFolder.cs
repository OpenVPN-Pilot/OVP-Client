using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Removes the folder a server's copy is kept in.
/// </summary>
/// <remarks>
/// Windows refuses to delete a file that is open, and the database can be open for a moment longer
/// than the last query: the pool keeps connections, and a view that was reading as the removal began
/// still holds one. The pools are emptied before every attempt, so a second attempt finds the file
/// closed. This is about the local file system only; nothing here talks to a server.
/// </remarks>
internal static class ServerCacheFolder
{
    /// <summary>
    /// How often removing the folder is attempted.
    /// </summary>
    private const int Attempts = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Deletes the folder and everything in it, trying again while a file in it is still open.
    /// </summary>
    /// <returns>True when the folder is gone, including when it was never there.</returns>
    /// <exception cref="IOException">The folder could still not be removed on the last attempt.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for a file the system would not release.</exception>
    public static async Task<bool> DeleteAsync(string folder, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(logger);

        for (int attempt = 1; ; attempt++)
        {
            // Pooled connections keep the database file open after the last context is gone.
            SqliteConnection.ClearAllPools();

            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                return true;
            }
            catch (IOException exception) when (attempt < Attempts)
            {
                ServerWipeLog.FolderBusy(logger, attempt, exception.Message);
            }
            catch (UnauthorizedAccessException exception) when (attempt < Attempts)
            {
                ServerWipeLog.FolderBusy(logger, attempt, exception.Message);
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }
    }
}
