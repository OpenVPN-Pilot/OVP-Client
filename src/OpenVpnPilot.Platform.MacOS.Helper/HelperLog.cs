using System.Globalization;
using System.Text;

namespace OpenVpnPilot.Platform.MacOS.Helper;

/// <summary>
/// The helper's own record: one line per decision, never a secret.
/// </summary>
/// <remarks>
/// A root process that refuses a request has to say why somewhere the administrator can read, and
/// the caller only ever hears the part of the reason that concerns it. What is written is who asked,
/// what was decided and what OpenVPN did. Configurations, passwords and management passwords are
/// never written, not even in part.
///
/// The file is kept small: past a megabyte it is moved aside once, so the helper can run for years
/// without filling a disk.
/// </remarks>
internal sealed class HelperLog
{
    private const long RotateAtBytes = 1024 * 1024;

    private readonly string? path;
    private readonly Lock gate = new();

    /// <param name="path">The log file, or null to write to standard error.</param>
    public HelperLog(string? path)
    {
        this.path = path;
    }

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");

        lock (gate)
        {
            if (path is null)
            {
                Console.Error.Write(line);
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                if (File.Exists(path) && new FileInfo(path).Length > RotateAtBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException exception)
            {
                // The record is lost, the decision is not. Standard error goes to launchd's own log.
                Console.Error.Write($"{line}(the helper log could not be written: {exception.Message}){Environment.NewLine}");
            }
            catch (UnauthorizedAccessException exception)
            {
                Console.Error.Write($"{line}(the helper log could not be written: {exception.Message}){Environment.NewLine}");
            }
        }
    }
}
