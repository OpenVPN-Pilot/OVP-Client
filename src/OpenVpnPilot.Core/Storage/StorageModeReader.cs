using System.Text.Json;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.Core.Storage;

/// <summary>
/// Reads which store to open from the settings file, before anything else exists.
/// </summary>
/// <remarks>
/// The database a process works on is decided before the container is built, because every store
/// is composed against one file. The settings service cannot answer that: it is one of the things
/// being composed, and loading it writes defaults, migrates and moves an unreadable file aside.
///
/// This reads the same file the same way, and does nothing else. It reads the whole file with the
/// options the settings service uses rather than picking out two values, so the two cannot disagree:
/// a file the service would set aside as unreadable is read here as the defaults too, which is Local.
/// </remarks>
public static class StorageModeReader
{
    /// <summary>
    /// The mode and server address the settings file names, or Local when it names nothing usable.
    /// </summary>
    /// <param name="settingsPath">The settings file. It need not exist.</param>
    public static StorageSelection Read(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        PilotSettings? settings;

        try
        {
            if (!File.Exists(settingsPath))
            {
                return StorageSelection.Local;
            }

            using FileStream stream = new(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            settings = JsonSerializer.Deserialize<PilotSettings>(stream, PilotSettingsTransfer.SerializerOptions);
        }
        catch (JsonException)
        {
            // The settings service sets this file aside and starts from the defaults, which are Local.
            return StorageSelection.Local;
        }
        catch (IOException)
        {
            // The settings service uses the defaults for this run too, so both agree on Local.
            return StorageSelection.Local;
        }
        catch (UnauthorizedAccessException)
        {
            // Deciding the store is not the place to report this; loading the settings meets it again
            // and says so. Local is the store this machine had before modes existed.
            return StorageSelection.Local;
        }

        // A number where a name belongs is accepted by the reader and means nothing, so it is Local.
        return settings is null || !Enum.IsDefined(settings.Storage.Mode)
            ? StorageSelection.Local
            : new StorageSelection(settings.Storage.Mode, settings.Storage.ServerUrl);
    }
}

/// <summary>
/// What the settings file asks for: a mode and, for a server, its address as it was written.
/// </summary>
public sealed record StorageSelection(StorageMode Mode, string? ServerUrl)
{
    public static StorageSelection Local { get; } = new(StorageMode.Local, null);
}
