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
/// them; hiding and quitting are added by the platform after these entries, as they are everywhere.
///
/// The menu object stays the same for the life of the application and only its entries change,
/// because the exporter behind it refuses to be handed a different menu.
/// </remarks>
public sealed class ApplicationMenuController : IDisposable
{
    private readonly IApplicationMenu platform;
    private readonly ILocalizer localizer;
    private readonly NativeMenu menu = [];
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
    /// Installs the menu. Must be called from the user interface thread.
    /// </summary>
    public void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        Rebuild();
        NativeMenu.SetMenu(application, menu);
        localizer.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        menu.Items.Clear();

        NativeMenuItem about = new(localizer["app.menuAbout"]);
        about.Click += (_, _) => platform.ShowAbout();

        NativeMenuItem settings = new(localizer["app.menuSettings"])
        {
            Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
        };

        settings.Click += (_, _) => ActionRequested?.Invoke(this, TrayIconController.SettingsAction);

        menu.Items.Add(about);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(settings);
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
