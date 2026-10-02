using System.Text.Json;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// The portable part of the settings, as the synchronisation sends and receives it.
/// </summary>
public interface IPortableSettings
{
    /// <summary>
    /// What <see cref="PilotSettingsTransfer.Export"/> makes of the current settings.
    /// </summary>
    public JsonElement Export();

    /// <summary>
    /// Takes the settings the server holds for the signed in person, keeping this machine's own
    /// values, without recording that as a change to send back.
    /// </summary>
    /// <returns>True when anything changed; false when it was the same or could not be read.</returns>
    public Task<bool> ApplyFromServerAsync(JsonElement document, CancellationToken cancellationToken = default);
}

/// <summary>
/// The settings service the application uses, which notes for the server when a person changed a
/// setting that follows them.
/// </summary>
/// <remarks>
/// <para>
/// Whether a save changed anything portable is decided by comparing what would be exported before
/// and after it. Saving the window position, or the settings screen without touching a field, is
/// therefore no change for the server, and only a real one records a marker. The recorder writes
/// nothing on the local library.
/// </para>
/// <para>
/// What the server sends is applied around the recording, not through it. Recording it would send
/// the server's own settings straight back after every synchronisation, forever. It is applied on
/// the interface thread, where a save from the settings screen happens too, because what listens to
/// the settings changes what the windows show.
/// </para>
/// </remarks>
public sealed class RecordingSettingsService : ISettingsService, IPortableSettings, IDisposable
{
    private readonly ISettingsService inner;
    private readonly IChangeRecorder changeRecorder;
    private readonly IUserInterfaceThread userInterface;

    // One save at a time, so the comparison of before and after is about that save alone.
    private readonly SemaphoreSlim gate = new(1, 1);

    public RecordingSettingsService(
        ISettingsService inner,
        IChangeRecorder changeRecorder,
        IUserInterfaceThread userInterface)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(changeRecorder);
        ArgumentNullException.ThrowIfNull(userInterface);

        this.inner = inner;
        this.changeRecorder = changeRecorder;
        this.userInterface = userInterface;
    }

    public PilotSettings Current => inner.Current;

    public event EventHandler<PilotSettings>? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);

    public Task UpdateAsync(Action<PilotSettings> change, CancellationToken cancellationToken = default) =>
        SaveRecordingAsync(() => inner.UpdateAsync(change, cancellationToken), cancellationToken);

    public Task ReplaceAsync(PilotSettings settings, CancellationToken cancellationToken = default) =>
        SaveRecordingAsync(() => inner.ReplaceAsync(settings, cancellationToken), cancellationToken);

    public JsonElement Export() => PilotSettingsTransfer.Export(inner.Current);

    public async Task<bool> ApplyFromServerAsync(JsonElement document, CancellationToken cancellationToken = default)
    {
        bool changed = false;

        await gate.WaitAsync(cancellationToken);

        try
        {
            await userInterface.InvokeAsync(
                async () =>
                {
                    PilotSettings current = inner.Current;

                    if (PilotSettingsTransfer.Import(document, current) is not { } incoming
                        || Portable(incoming) == Portable(current))
                    {
                        return;
                    }

                    await inner.ReplaceAsync(incoming, cancellationToken);
                    changed = true;
                },
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        return changed;
    }

    private async Task SaveRecordingAsync(Func<Task> save, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            string before = Portable(inner.Current);
            await save();

            if (Portable(inner.Current) != before)
            {
                await changeRecorder.RecordAsync(PendingChangeKind.Settings, cancellationToken: cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private static string Portable(PilotSettings settings) => PilotSettingsTransfer.Export(settings).GetRawText();
}
