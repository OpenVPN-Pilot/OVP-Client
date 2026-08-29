using System.Globalization;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Keeps the active language in step with the setting, and resolves what following the system means.
/// </summary>
/// <remarks>
/// A null language setting means follow the operating system. The system reports a regional code
/// such as de-DE, which is reduced to the closest catalogue that actually exists rather than being
/// rejected outright.
/// </remarks>
public sealed class LanguageCoordinator : IDisposable
{
    private readonly LocalizationManager localizer;
    private readonly ISettingsService settings;
    private bool disposed;

    public LanguageCoordinator(LocalizationManager localizer, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(settings);

        this.localizer = localizer;
        this.settings = settings;
    }

    /// <summary>
    /// Applies the stored preference and follows it from then on.
    /// </summary>
    public void Attach()
    {
        Apply(settings.Current);
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>
    /// The language the operating system asks for, reduced to one that is available.
    /// </summary>
    public string SystemLanguage =>
        localizer.ResolveBestMatch(CultureInfo.CurrentUICulture.Name is { Length: > 0 } name
            ? name
            : LocalizationManager.FallbackLanguage);

    private void OnSettingsChanged(object? sender, PilotSettings changed) => Apply(changed);

    private void Apply(PilotSettings current)
    {
        string language = current.General.Language ?? SystemLanguage;

        if (!localizer.TrySetLanguage(language))
        {
            // The chosen language file was removed. English is always compiled into the fallback.
            localizer.TrySetLanguage(LocalizationManager.FallbackLanguage);
        }
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
