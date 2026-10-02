using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Where the stores report a change the server should hear about.
/// </summary>
/// <remarks>
/// The stores call this for every change unconditionally and it decides whether that means anything,
/// so the mode is asked in one place instead of in every store and view model. On the local library
/// it records nothing.
/// </remarks>
public interface IChangeRecorder
{
    /// <summary>
    /// Adds a marker to the caller's unit of work, which the caller then saves.
    /// </summary>
    public Task StageAsync(
        PilotDbContext context,
        PendingChangeKind kind,
        Guid? entityId = null,
        string? realm = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a marker on its own, for a change that is not kept in the database.
    /// </summary>
    /// <remarks>
    /// The settings are such a change: after the portable part of the settings was saved, the
    /// settings path calls this with <see cref="PendingChangeKind.Settings"/>. A shared sign in that
    /// worked is another, with <see cref="PendingChangeKind.VaultAdd"/>, the profile and the realm.
    /// </remarks>
    public Task RecordAsync(
        PendingChangeKind kind,
        Guid? entityId = null,
        string? realm = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Records into the outbox while the application runs against a server.
/// </summary>
public sealed class ChangeRecorder : IChangeRecorder
{
    private readonly IOutbox outbox;
    private readonly IStorageModeContext mode;

    public ChangeRecorder(IOutbox outbox, IStorageModeContext mode)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(mode);

        this.outbox = outbox;
        this.mode = mode;
    }

    public Task StageAsync(
        PilotDbContext context,
        PendingChangeKind kind,
        Guid? entityId = null,
        string? realm = null,
        CancellationToken cancellationToken = default) =>
        mode.IsServerMode
            ? outbox.StageAsync(context, kind, entityId, realm, cancellationToken)
            : Task.CompletedTask;

    public Task RecordAsync(
        PendingChangeKind kind,
        Guid? entityId = null,
        string? realm = null,
        CancellationToken cancellationToken = default) =>
        mode.IsServerMode
            ? outbox.RecordAsync(kind, entityId, realm, cancellationToken)
            : Task.CompletedTask;
}
