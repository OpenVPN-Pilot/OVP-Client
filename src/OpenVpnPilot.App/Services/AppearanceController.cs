using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Applies the theme preference to the running application.
/// </summary>
/// <remarks>
/// The palette already defines a light and a dark variant, so this only decides which of the two is
/// requested. Following the system is Avalonia's default variant rather than a value this class
/// computes, which keeps the application in step when the operating system theme changes while it
/// runs.
/// </remarks>
public sealed class AppearanceController : IDisposable
{
    private readonly ISettingsService settings;
    private Application? application;
    private bool disposed;

    public AppearanceController(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
    }

    public void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        this.application = application;
        Apply(settings.Current);

        settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PilotSettings changed)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply(changed);
            return;
        }

        Dispatcher.UIThread.Post(() => Apply(changed));
    }

    private void Apply(PilotSettings current)
    {
        if (application is null)
        {
            return;
        }

        application.RequestedThemeVariant = current.Appearance.Theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        settings.Changed -= OnSettingsChanged;
        application = null;
    }
}
