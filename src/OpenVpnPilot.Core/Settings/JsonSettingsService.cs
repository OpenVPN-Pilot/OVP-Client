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
/// contents can be recovered, and the defaults take over for this run. A file that cannot be opened
/// at all is never written during the run: the defaults apply in memory only, and a change made
/// meanwhile lasts until the application ends. Writing goes through a temporary file and a move, so
/// an interrupted save cannot leave a half written file behind.
/// </remarks>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = PilotSettingsTransfer.SerializerOptions;

    // Five attempts a tenth of a second apart outlast a scanner or a synchronisation client holding
    // the file, without holding up a start noticeably when the file stays out of reach.
    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly string path;
    private readonly ILogger<JsonSettingsService> logger;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    private PilotSettings current = new();
    private bool disposed;

    // Set when the file exists but could not be read, so nothing this run holds may replace it.
    private bool readOnly;

    public JsonSettingsService(string path, ILogger<JsonSettingsService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        this.path = path;
        this.logger = logger ?? NullLogger<JsonSettingsService>.Instance;
    }

    public PilotSettings Current => current;

    /// <summary>
    /// Whether the settings file was there when <see cref="LoadAsync"/> ran. Null before it ran.
    /// </summary>
    /// <remarks>
    /// This is the one signal for a true first start, and it is taken before the defaults are
    /// written, because writing them makes the file exist. An installation that updates to a build
    /// asking where the profiles should live already has a file and is therefore never asked. A file
    /// that was there but could not be read still counts as there: somebody used this installation.
    /// </remarks>
    public bool? FileExistedAtLoad { get; private set; }

    public event EventHandler<PilotSettings>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        FileExistedAtLoad = File.Exists(path);

        if (FileExistedAtLoad == false)
        {
            // First start. Writing the defaults out immediately makes the file discoverable, and
            // stamping the layout stops the next build from mistaking them for an older file.
            PilotSettings defaults = new() { SchemaVersion = PilotSettings.CurrentSchemaVersion };
            AssignInstallationId(defaults);

            await PersistAsync(defaults, cancellationToken);
            return;
        }

        PilotSettings? loaded = await ReadExistingFileAsync(cancellationToken);

        if (loaded is null)
        {
            // The file is there and holds what the user configured, so the defaults that stand in for
            // it must never reach the disk: not as a migration, not with a fresh identity, and not as
            // the base of a later save. No identity is minted either, because one that exists only for
            // this run would introduce this installation as a stranger to a server.
            readOnly = true;
            current = new PilotSettings { SchemaVersion = PilotSettings.CurrentSchemaVersion };
            Changed?.Invoke(this, current);
            return;
        }

        current = loaded;

        // A file from an older build is brought up to date and written back once, so the change is
        // visible in the file rather than being reapplied invisibly on every start.
        bool changed = current.Migrate();

        if (changed)
        {
            SettingsLog.Migrated(logger, PilotSettings.CurrentSchemaVersion);
        }

        // A file written before installations had an identity gets one now, once, and keeps it.
        if (current.Installation.Id is null || current.Installation.Id == Guid.Empty)
        {
            AssignInstallationId(current);
            changed = true;
        }

        if (changed)
        {
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

    /// <summary>
    /// Reads the settings file that exists, or returns null when it cannot be read at all.
    /// </summary>
    /// <remarks>
    /// A file another program holds open, a virus scanner or a synchronisation client for example, is
    /// usually free again a moment later, so a failure to open it is retried a few times before the
    /// run goes on without it. A file that opens but does not parse is a different matter: retrying
    /// cannot help, and it is moved aside so that the defaults can be written in its place.
    /// </remarks>
    private async Task<PilotSettings?> ReadExistingFileAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using FileStream stream = File.OpenRead(path);
                PilotSettings? loaded = await JsonSerializer.DeserializeAsync<PilotSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken);

                return loaded ?? new PilotSettings();
            }
            catch (JsonException exception)
            {
                SettingsLog.FileUnreadable(logger, path, exception);

                // A file that could not be moved aside would be replaced by the defaults.
                return QuarantineUnreadableFile() ? new PilotSettings() : null;
            }
            catch (IOException exception) when (attempt < ReadAttempts)
            {
                SettingsLog.ReadRetrying(logger, path, attempt, exception);
                await Task.Delay(ReadRetryDelay, cancellationToken);
            }
            catch (IOException exception)
            {
                SettingsLog.FileInaccessible(logger, path, exception);
                return null;
            }
            catch (UnauthorizedAccessException exception)
            {
                // Access is refused rather than briefly busy, so trying again would only delay the start.
                SettingsLog.FileInaccessible(logger, path, exception);
                return null;
            }
        }
    }

    private void AssignInstallationId(PilotSettings settings)
    {
        settings.Installation.Id = Guid.NewGuid();
        SettingsLog.InstallationIdCreated(logger);
    }

    private async Task PersistAsync(PilotSettings settings, CancellationToken cancellationToken)
    {
        // Whole settings objects arrive from screens and imports that know nothing of the identity.
        // Losing it would make this installation a stranger to the server it is signed in to.
        if (settings.Installation.Id is null && current.Installation.Id is { } existing)
        {
            settings.Installation.Id = existing;
        }

        if (readOnly)
        {
            // What is in memory started as the defaults, not as the file. Writing it, even with the
            // user's change applied, would replace everything else the file holds.
            SettingsLog.SaveSkipped(logger, path);
            current = settings;
            Changed?.Invoke(this, current);
            return;
        }

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
    /// <returns>Whether the file was moved, which leaves its place free for the defaults.</returns>
    private bool QuarantineUnreadableFile()
    {
        string target = path + ".invalid";

        try
        {
            File.Move(path, target, overwrite: true);
            SettingsLog.FileQuarantined(logger, target);
            return true;
        }
        catch (IOException exception)
        {
            SettingsLog.QuarantineFailed(logger, path, exception);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            SettingsLog.QuarantineFailed(logger, path, exception);
            return false;
        }
    }
}
