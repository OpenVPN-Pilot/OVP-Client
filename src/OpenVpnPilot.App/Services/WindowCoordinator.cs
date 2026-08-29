using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Opens the secondary windows on the main view model's behalf.
/// </summary>
/// <remarks>
/// A view model that constructs windows cannot be tested and cannot be reused from the tray or from
/// a shortcut. It therefore asks for a screen and this decides what that means, which also keeps
/// exactly one instance of each screen open at a time: a second settings window would let two copies
/// of the same values be edited independently.
/// </remarks>
public sealed class WindowCoordinator
{
    private readonly IServiceProvider services;
    private readonly MainWindow mainWindow;
    private readonly MainWindowViewModel viewModel;
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly Dictionary<AppScreen, Window> open = [];

    public WindowCoordinator(
        IServiceProvider services,
        MainWindow mainWindow,
        MainWindowViewModel viewModel,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(desktop);

        this.services = services;
        this.mainWindow = mainWindow;
        this.viewModel = viewModel;
        this.desktop = desktop;
    }

    public void Attach()
    {
        viewModel.ScreenRequested += (_, screen) => Open(screen);
        mainWindow.FilesDropped += (_, paths) => OpenImport(paths);

        // A copy that started with no window has none to own a dialog. The first time the window is
        // shown, from the notification area or a shortcut, it becomes the one dialogs belong to.
        mainWindow.Opened += (_, _) => desktop.MainWindow = mainWindow;

        mainWindow.Closing += (_, args) =>
        {
            // Closing keeps the tunnels running; the tray icon is the way back in.
            if (services.GetRequiredService<ISettingsService>().Current.General.CloseToTray)
            {
                args.Cancel = true;
                mainWindow.Hide();
            }
            else
            {
                desktop.Shutdown();
            }
        };
    }

    /// <summary>
    /// Brings a window forward from wherever it was, including from the tray.
    /// </summary>
    public static void Reveal(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void HandleTrayAction(string actionId, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);

        switch (actionId)
        {
            case TrayIconController.ShowWindowAction:
                Reveal(mainWindow);
                break;

            case TrayIconController.QuickSwitcherAction:
                Open(AppScreen.QuickSwitcher);
                break;

            case TrayIconController.SettingsAction:
                Reveal(mainWindow);
                Open(AppScreen.Settings);
                break;

            case TrayIconController.DisconnectAllAction:
                viewModel.DisconnectAllCommand.Execute(null);
                break;

            case TrayIconController.QuitAction:
                lifetime.Shutdown();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Opens the import wizard, optionally with sources already picked out for it.
    /// </summary>
    public void OpenImport(IReadOnlyList<string>? paths = null)
    {
        Open(AppScreen.Import);

        if (paths is { Count: > 0 }
            && open.TryGetValue(AppScreen.Import, out Window? window)
            && window.DataContext is ImportViewModel model)
        {
            // Fire and forget: examining reports its own outcome on the screen that is now open.
            _ = model.ExamineAsync(paths);
        }
    }

    public void Open(AppScreen screen)
    {
        if (screen == AppScreen.MainWindow)
        {
            Reveal(mainWindow);
            return;
        }

        if (open.TryGetValue(screen, out Window? existing))
        {
            Reveal(existing);
            return;
        }

        Window? window = Create(screen);

        if (window is null)
        {
            return;
        }

        open[screen] = window;
        window.Closed += (_, _) => open.Remove(screen);

        if (screen is AppScreen.QuickSwitcher or AppScreen.QuickDisconnect || !mainWindow.IsVisible)
        {
            window.Show();
        }
        else
        {
            window.Show(mainWindow);
        }
    }

    private Window? Create(AppScreen screen) => screen switch
    {
        AppScreen.QuickSwitcher => CreateQuickSwitcher(QuickSwitcherMode.Connect),
        AppScreen.QuickDisconnect => CreateQuickSwitcher(QuickSwitcherMode.Disconnect),
        AppScreen.Settings => CreateSettings(),
        AppScreen.History => CreateHistory(),
        AppScreen.Import => CreateImport(),
        AppScreen.Export => CreateExport(),
        AppScreen.ProfileEditor => CreateProfileEditor(),
        _ => null,
    };

    private QuickSwitcherWindow CreateQuickSwitcher(QuickSwitcherMode mode)
    {
        QuickSwitcherViewModel model = services.GetRequiredService<QuickSwitcherViewModel>();
        model.Reset(viewModel.AllProfiles, mode);

        QuickSwitcherWindow window = new() { DataContext = model };

        void OnAccepted(object? sender, QuickSwitcherChoice choice)
        {
            window.Close();

            if (choice.Mode == QuickSwitcherMode.Disconnect)
            {
                if (choice.ShowWindow)
                {
                    Reveal(mainWindow);
                    viewModel.SelectProfile(choice.Entries[0].ProfileId);
                }

                _ = viewModel.DisconnectByIdAsync(choice.Entries.Select(entry => entry.ProfileId).ToList());
                return;
            }

            // The palette is a switcher, so choosing what is already up takes you to it whether or
            // not you asked for the window.
            if (choice.Entries.Any(entry => entry.IsConnected) || choice.ShowWindow)
            {
                Reveal(mainWindow);
                viewModel.SelectProfile(choice.Entries[0].ProfileId);
            }

            _ = viewModel.ConnectByIdAsync(
                choice.Entries.Where(entry => !entry.IsConnected).Select(entry => entry.ProfileId).ToList());
        }

        void OnDismissed(object? sender, EventArgs e) => window.Close();

        model.Accepted += OnAccepted;
        model.Dismissed += OnDismissed;

        window.Closed += (_, _) =>
        {
            model.Accepted -= OnAccepted;
            model.Dismissed -= OnDismissed;
        };

        return window;
    }

    private SettingsWindow CreateSettings()
    {
        SettingsViewModel model = services.GetRequiredService<SettingsViewModel>();
        SettingsWindow window = new() { DataContext = model };

        model.Closed += (_, _) => window.Close();
        model.ScreenRequested += (_, screen) => Open(screen);
        model.ProfileReloadRequested += async (_, _) => await viewModel.LoadAsync();
        window.Opened += async (_, _) => await model.LoadAsync();

        return window;
    }

    private HistoryWindow CreateHistory()
    {
        HistoryViewModel model = services.GetRequiredService<HistoryViewModel>();
        HistoryWindow window = new() { DataContext = model };

        window.Opened += async (_, _) => await model.LoadAsync();

        return window;
    }

    private ImportWindow CreateImport()
    {
        ImportViewModel model = services.GetRequiredService<ImportViewModel>();
        ImportWindow window = new() { DataContext = model };

        model.Closed += async (_, imported) =>
        {
            window.Close();

            if (imported)
            {
                await viewModel.LoadAsync();
            }
        };

        window.Closed += (_, _) => model.Dispose();

        return window;
    }

    private ExportWindow CreateExport()
    {
        ExportViewModel model = services.GetRequiredService<ExportViewModel>();
        ExportWindow window = new() { DataContext = model };

        model.Closed += (_, _) => window.Close();
        window.Opened += async (_, _) => await model.LoadAsync();

        return window;
    }

    private ProfileEditorWindow? CreateProfileEditor()
    {
        if (viewModel.SelectedProfile is not { } profile)
        {
            return null;
        }

        ProfileEditorViewModel model = new(
            services.GetRequiredService<IProfileStore>(),
            services.GetRequiredService<Core.Localization.ILocalizer>(),
            profile);

        ProfileEditorWindow window = new() { DataContext = model };

        model.Closed += async (_, changed) =>
        {
            window.Close();

            if (changed)
            {
                await viewModel.LoadAsync();
            }
        };

        return window;
    }
}
