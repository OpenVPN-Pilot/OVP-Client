using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Fills the application menu on a platform whose menu bar shows one.
/// </summary>
/// <remarks>
/// Left alone, the framework fills that menu with an entry about the framework, which is not what a
/// person opening the menu named after this application expects to find. The menu here carries the
/// application's own about entry and its settings under the shortcut every Mac application uses for
/// them.
///
/// Two things about the framework decide how. The platform reads the application's menu once, before
/// the application has finished starting, and never looks at the property again: a menu handed over
/// later stays unexported, and the framework's own entry is what the menu bar keeps showing. The menu
/// object therefore comes from the application, which sets it while it initialises, and this only
/// fills it. And the platform adds hiding, the services and quitting to that same object when it
/// first reads it. The entries here are therefore put in front of those and never replaced; a
/// language change rewords them in place, because clearing the menu to rebuild it would take the
/// platform's entries with it.
/// </remarks>
public sealed class ApplicationMenuController : IDisposable
{
    private readonly IApplicationMenu platform;
    private readonly ILocalizer localizer;

    private readonly NativeMenuItem about = new();

    private readonly NativeMenuItem settings = new()
    {
        Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
    };

    private bool disposed;

    public ApplicationMenuController(IApplicationMenu platform, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(localizer);

        this.platform = platform;
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

        about.Click += (_, _) => platform.ShowAbout();
        settings.Click += (_, _) => ActionRequested?.Invoke(this, TrayIconController.SettingsAction);

        Reword();

        menu.Items.Insert(0, about);
        menu.Items.Insert(1, new NativeMenuItemSeparator());
        menu.Items.Insert(2, settings);

        localizer.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Reword);

    private void Reword()
    {
        about.Header = localizer["app.menuAbout"];
        settings.Header = localizer["app.menuSettings"];
    }

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
