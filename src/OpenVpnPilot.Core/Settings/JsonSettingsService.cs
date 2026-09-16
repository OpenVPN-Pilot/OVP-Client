using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenVpnPilot.Core.Settings;

/// <summary>
/// Stores settings in one indented JSON file that a person can read and edit.
/// </summary>
/// <remarks>
/// A file that cannot be parsed is kept rather than overwritten: it is renamed with a suffix so the
/// contents can be recovered, and the defaults take over for this run. Writing goes through a
/// temporary file and a move, so an interrupted save cannot leave a half written file behind.
/// </remarks>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = PilotSettingsTransfer.SerializerOptions;

    private readonly string path;
    private readonly ILogger<JsonSettingsService> logger;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    private PilotSettings current = new();
    private bool disposed;

    public JsonSettingsService(string path, ILogger<JsonSettingsService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        this.path = path;
        this.logger = logger ?? NullLogger<JsonSettingsService>.Instance;
    }

    public PilotSettings Current => current;

    public event EventHandler<PilotSettings>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            // First start. Writing the defaults out immediately makes the file discoverable, and
            // stamping the layout stops the next build from mistaking them for an older file.
            await PersistAsync(
                new PilotSettings { SchemaVersion = PilotSettings.CurrentSchemaVersion },
                cancellationToken);

            return;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            PilotSettings? loaded = await JsonSerializer.DeserializeAsync<PilotSettings>(
                stream,
                SerializerOptions,
                cancellationToken);

            current = loaded ?? new PilotSettings();
        }
        catch (JsonException exception)
        {
            SettingsLog.FileUnreadable(logger, path, exception);
            QuarantineUnreadableFile();
            current = new PilotSettings();
        }
        catch (IOException exception)
        {
            // The defaults keep the application usable and the file is left untouched for next time.
            SettingsLog.FileUnreadable(logger, path, exception);
            current = new PilotSettings();
        }

        // A file from an older build is brought up to date and written back once, so the change is
        // visible in the file rather than being reapplied invisibly on every start.
        if (current.Migrate())
        {
            SettingsLog.Migrated(logger, PilotSettings.CurrentSchemaVersion);
            await PersistAsync(current, cancellationToken);
        }

        Changed?.Invoke(this, current);
    }

    public async Task UpdateAsync(Action<PilotSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        PilotSettings next = current.Clone();
        change(next);

        await PersistAsync(next, cancellationToken);
    }

    public Task ReplaceAsync(PilotSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return PersistAsync(settings.Clone(), cancellationToken);
    }

    private async Task PersistAsync(PilotSettings settings, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            string temporary = path + ".tmp";

            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, overwrite: true);

            current = settings;
        }
        catch (IOException exception)
        {
            // The change still applies for this run, so a locked file does not block the user.
            SettingsLog.SaveFailed(logger, path, exception);
            current = settings;
        }
        catch (UnauthorizedAccessException exception)
        {
            SettingsLog.SaveFailed(logger, path, exception);
            current = settings;
        }
        finally
        {
            writeGate.Release();
        }

        Changed?.Invoke(this, current);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writeGate.Dispose();
    }

    /// <summary>
    /// Moves a settings file that could not be parsed aside instead of overwriting it.
    /// </summary>
    private void QuarantineUnreadableFile()
    {
        string target = path + ".invalid";

        try
        {
            File.Move(path, target, overwrite: true);
            SettingsLog.FileQuarantined(logger, target);
        }
        catch (IOException exception)
        {
            SettingsLog.QuarantineFailed(logger, path, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            SettingsLog.QuarantineFailed(logger, path, exception);
        }
    }
}
