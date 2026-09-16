using OpenVpnPilot.Core.Settings;
using Serilog.Core;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Applies the log settings to the running logger, whenever they change.
/// </summary>
/// <remarks>
/// The level and the retention were both stored, both shown in the settings screen and neither was
/// ever read: the logger fixed its own minimum when the container was built, and the file sink
/// counted files rather than days. Both are settings the user is invited to change, so both have to
/// mean something.
///
/// This exists because the settings are loaded after the logger. Rather than delaying the logger
/// until a file has been read, the logger starts at its default and is adjusted here, which also
/// makes a change take effect without restarting.
/// </remarks>
public sealed class LogSettingsApplier : IDisposable
{
    private readonly ISettingsService settings;
    private readonly LoggingLevelSwitch levelSwitch;
    private readonly LogHub hub;
    private bool disposed;

    public LogSettingsApplier(ISettingsService settings, LoggingLevelSwitch levelSwitch, LogHub hub)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(levelSwitch);
        ArgumentNullException.ThrowIfNull(hub);

        this.settings = settings;
        this.levelSwitch = levelSwitch;
        this.hub = hub;
    }

    public void Attach()
    {
        Apply(settings.Current);
        settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PilotSettings changed) => Apply(changed);

    private void Apply(PilotSettings current)
    {
        levelSwitch.MinimumLevel = AppHost.ParseLevel(current.Advanced.LogLevel);
        hub.RetentionDays = Math.Clamp(current.Advanced.LogRetentionDays, 0, 365);
        hub.MaximumTotalBytes = Math.Clamp(current.Advanced.LogMaximumMegabytes, 0, 100_000) * 1024L * 1024L;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        settings.Changed -= OnSettingsChanged;
    }
}
