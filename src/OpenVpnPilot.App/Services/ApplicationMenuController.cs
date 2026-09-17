using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Puts the settings into the application menu, for the shortcut every Mac application has them
/// under.
/// </summary>
/// <remarks>
/// The menu itself is not shown any more: this application stays out of the Dock, and an application
/// that is not in the Dock has no menu bar of its own. What survives is what the menu is also for,
/// which is its keyboard shortcuts. The platform answers command and comma from this entry, exactly
/// as it answers command and Q from the one it adds itself, and there is nowhere else to put a
/// shortcut that works while any window of this application is in front. The panel about the
/// application moved to the menu bar entry, which is somewhere a person can actually click.
///
/// Two things about the framework decide how. The platform reads the application's menu once, before
/// the application has finished starting, and never looks at the property again, so the menu object
/// comes from the application, which sets it while it initialises, and this only fills it. And the
/// platform adds hiding, the services and quitting to that same object when it first reads it. The
/// entry here is therefore put in front of those and never replaced; a language change rewords it in
/// place, because clearing the menu to rebuild it would take the platform's entries with it.
/// </remarks>
public sealed class ApplicationMenuController : IDisposable
{
    private readonly ILocalizer localizer;

    private readonly NativeMenuItem settings = new()
    {
        Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
    };

    private bool disposed;

    public ApplicationMenuController(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        this.localizer = localizer;
    }

    /// <summary>
    /// Raised with the tray action an entry stands for, so the same code opens the same window
    /// whether it was asked for from the menu bar entry or from the application menu.
    /// </summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>
    /// Puts the entries into the application's menu. Must be called from the user interface thread.
    /// </summary>
    public void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        NativeMenu? menu = NativeMenu.GetMenu(application);

        if (menu is null)
        {
            menu = [];
            NativeMenu.SetMenu(application, menu);
        }

        settings.Click += (_, _) => ActionRequested?.Invoke(this, TrayIconController.SettingsAction);

        Reword();

        menu.Items.Insert(0, settings);
        menu.Items.Insert(1, new NativeMenuItemSeparator());

        localizer.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Reword);

    private void Reword() => settings.Header = localizer["app.menuSettings"];

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        localizer.LanguageChanged -= OnLanguageChanged;
    }
}
