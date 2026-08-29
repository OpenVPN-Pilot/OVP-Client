namespace OpenVpnPilot.Core.Settings;

/// <summary>
/// Holds the current settings and persists changes.
/// </summary>
/// <remarks>
/// <see cref="Current"/> is the live instance every component reads from. Changes go through
/// <see cref="UpdateAsync"/> so that saving and notifying always happen together; assigning to the
/// object directly would leave listeners stale.
/// </remarks>
public interface ISettingsService
{
    public PilotSettings Current { get; }

    /// <summary>
    /// Raised after settings were loaded or changed, carrying the new values.
    /// </summary>
    public event EventHandler<PilotSettings>? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a change and writes it out.
    /// </summary>
    public Task UpdateAsync(Action<PilotSettings> change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces every value at once, as a settings screen does when its form is saved.
    /// </summary>
    public Task ReplaceAsync(PilotSettings settings, CancellationToken cancellationToken = default);
}
