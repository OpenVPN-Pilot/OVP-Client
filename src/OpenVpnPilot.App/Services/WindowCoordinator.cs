using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenVpnPilot.App.Services.Library;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
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
    private readonly ISettingsService settings;
    private readonly MainWindow mainWindow;
    private readonly MainWindowViewModel viewModel;
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly Dictionary<AppScreen, Window> open = [];

    /// <summary>
    /// The platform's list of running applications, on a platform that keeps one apart from windows.
    /// </summary>
    private readonly IDockPresence? dock;
    private readonly List<string> pendingImports = [];
    private bool importQueued;

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

        // Read once, because the window is also closed while the application is tearing down and
        // the container that answers this is one of the things being disposed.
        settings = services.GetRequiredService<ISettingsService>();
        dock = services.GetService<IDockPresence>();

        this.mainWindow = mainWindow;
        this.viewModel = viewModel;
        this.desktop = desktop;
    }

    public void Attach()
    {
        viewModel.ScreenRequested += (_, screen) => Open(screen);
        viewModel.LibraryPassphraseRequested += async (_, _) => await AskForLibraryPassphraseAsync();
        mainWindow.FilesDropped += (_, paths) => QueueImport(paths);
        mainWindow.ProfilesDroppedOnTag += async (_, drop) =>
            await viewModel.AssignTagAsync(drop.ProfileIds, drop.TagName);

        // A copy that started with no window has none to own a dialog. The first time the window is
        // shown, from the notification area or a shortcut, it becomes the one dialogs belong to.
        mainWindow.Opened += (_, _) =>
        {
            desktop.MainWindow = mainWindow;
            ScreenPlacement.Restore(
                mainWindow,
                settings.Current.General.MainWindow,
                ScreenInfo.From(mainWindow.Screens.All));
        };

        mainWindow.Closing += (_, args) =>
        {
            // First, because the window may be about to be hidden, closed or taken away with the
            // session, and where it was is worth the same in all three cases.
            RememberMainWindowPlacement();

            switch (DecideClose(args.CloseReason, settings.Current.General.CloseToTray))
            {
                case CloseIntent.HideToTray:
                    args.Cancel = true;
                    mainWindow.Hide();
                    break;

                case CloseIntent.Quit:
                    // The shutdown closes this window, which raises this handler again. Calling it
                    // from inside the handler therefore re-enters it until the stack runs out, so
                    // the request is posted and this close is refused; the window goes a moment
                    // later, as the window of an application that is shutting down.
                    args.Cancel = true;
                    Dispatcher.UIThread.Post(() => desktop.Shutdown());
                    break;

                case CloseIntent.Proceed:
                default:
                    break;
            }
        };

        mainWindow.PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.IsVisibleProperty)
            {
                UpdateDockPresence();
            }
        };

        // Once at the start as well, for a copy that starts with its window hidden and so never
        // reports the window changing.
        UpdateDockPresence();
    }

    /// <summary>
    /// Lists the application among the running ones while a window of its own is open.
    /// </summary>
    /// <remarks>
    /// The palettes do not count. They are there for a moment, over whatever else is in front, and an
    /// icon that appears and vanishes with each of them would be noise.
    ///
    /// Posted rather than applied at once. The first call comes before the platform has finished
    /// launching the application, and launching sets the same thing again from its own options,
    /// which would undo a decision made earlier than that.
    /// </remarks>
    private void UpdateDockPresence()
    {
        if (dock is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            bool anyWindow = mainWindow.IsVisible
                || open.Any(entry => entry.Key is not (AppScreen.QuickSwitcher or AppScreen.QuickDisconnect)
                    && entry.Value.IsVisible);

            dock.SetListed(anyWindow);
        });
    }

    /// <summary>
    /// Asks for the passphrase the shared library opens with now, over the main window.
    /// </summary>
    private async Task AskForLibraryPassphraseAsync()
    {
        SharedLibrarySync library = services.GetRequiredService<SharedLibrarySync>();
        ILocalizer localizer = services.GetRequiredService<ILocalizer>();

        PassphrasePromptViewModel prompt = new(
            localizer,
            localizer["library.enterPassphrase"],
            localizer["library.enterPassphraseMessage"],
            isNew: false,
            async passphrase =>
            {
                try
                {
                    await library.ProvidePassphraseAsync(passphrase);
                    return null;
                }
                catch (Exception exception) when (SharedLibraryText.Refusal(exception, localizer) is not null)
                {
                    // Said in the prompt, which stays open for another try.
                    return SharedLibraryText.Refusal(exception, localizer);
                }
            });

        Reveal(mainWindow);
        await PassphraseWindow.AskAsync(mainWindow, prompt);
    }

    /// <summary>
    /// Records where the main window is, so the next start opens it there.
    /// </summary>
    /// <remarks>
    /// Written without being waited for. This runs while the window is closing, and the alternative
    /// is blocking the user interface thread on a file write in the middle of a shutdown. A
    /// placement that does not make it to disk costs the user one window position; a shutdown that
    /// waits on a locked file costs them the fault dialog on the shutdown screen.
    /// </remarks>
    private void RememberMainWindowPlacement()
    {
        WindowPlacementSettings placement = ScreenPlacement.Capture(
            mainWindow,
            settings.Current.General.MainWindow);

        _ = settings.UpdateAsync(current => current.General.MainWindow = placement);
    }

    /// <summary>
    /// What closing the main window is meant to do.
    /// </summary>
    public enum CloseIntent
    {
        /// <summary>
        /// Let the window close and leave the application running.
        /// </summary>
        Proceed,

        /// <summary>
        /// Keep the application where it is, reachable from the notification area.
        /// </summary>
        HideToTray,

        /// <summary>
        /// Close the window and end the application with it.
        /// </summary>
        Quit,
    }

    /// <summary>
    /// Decides what a request to close the main window means.
    /// </summary>
    /// <remarks>
    /// Only a person closing the window is expressing a preference. A close that comes from the
    /// application shutting down, or from Windows ending the session, is not a request that may be
    /// refused: refusing the last one answers <c>WM_QUERYENDSESSION</c> with a veto, which Windows
    /// reports as this application preventing the machine from shutting down.
    /// </remarks>
    public static CloseIntent DecideClose(WindowCloseReason reason, bool closeToTray)
    {
        if (reason is not WindowCloseReason.WindowClosing)
        {
            return CloseIntent.Proceed;
        }

        // Closing keeps the tunnels running; the tray icon is the way back in.
        return closeToTray ? CloseIntent.HideToTray : CloseIntent.Quit;
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
    /// Collects files to import and opens the wizard once, with all of them.
    /// </summary>
    /// <remarks>
    /// Several files arrive as several requests. The shell sends one process, or one command, per
    /// file when more than one is opened at a time, and examining a selection replaces the previous
    /// one, so handling each as it arrives would import only the last. They are therefore gathered
    /// and handed over together on the next turn of the dispatcher, by which time the whole batch
    /// has landed.
    /// </remarks>
    public void QueueImport(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        pendingImports.AddRange(paths);

        if (importQueued || pendingImports.Count == 0)
        {
            return;
        }

        importQueued = true;

        Dispatcher.UIThread.Post(
            () =>
            {
                importQueued = false;
                string[] batch = [.. pendingImports];
                pendingImports.Clear();

                if (batch.Length > 0)
                {
                    OpenImport(batch);
                }
            },
            DispatcherPriority.Background);
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
        window.Closed += (_, _) =>
        {
            open.Remove(screen);
            UpdateDockPresence();
        };

        if (screen is AppScreen.QuickSwitcher or AppScreen.QuickDisconnect || !mainWindow.IsVisible)
        {
            window.Show();
        }
        else
        {
            window.Show(mainWindow);
        }

        UpdateDockPresence();
    }

    private Window? Create(AppScreen screen) => screen switch
    {
        AppScreen.QuickSwitcher => CreateQuickSwitcher(QuickSwitcherMode.Connect),
        AppScreen.QuickDisconnect => CreateQuickSwitcher(QuickSwitcherMode.Disconnect),
        AppScreen.Settings => CreateSettings(),
        AppScreen.History => CreateHistory(),
        AppScreen.Log => CreateLog(),
        AppScreen.Import => CreateImport(),
        AppScreen.Export => CreateExport(),
        AppScreen.ProfileEditor => CreateProfileEditor(),
        _ => null,
    };

    private QuickSwitcherWindow CreateQuickSwitcher(QuickSwitcherMode mode)
    {
        QuickSwitcherViewModel model = services.GetRequiredService<QuickSwitcherViewModel>();
        model.Reset(viewModel.AllProfiles, mode);

        QuickSwitcherWindow window = new()
        {
            DataContext = model,
            PreferredScreen = settings.Current.General.QuickMenuScreen,
        };

        // Written as soon as it is chosen rather than when the palette closes: the palette closes by
        // losing the focus, which is also what happens when the machine is locked or the session
        // ends, and a choice that is only saved on the way out is a choice that is regularly lost.
        window.ScreenChosen += async (_, identity) =>
            await settings.UpdateAsync(current => current.General.QuickMenuScreen = identity);

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
        window.Closed += (_, _) => model.Dispose();

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

    private LogWindow CreateLog()
    {
        LogViewModel model = services.GetRequiredService<LogViewModel>();
        LogWindow window = new() { DataContext = model };

        // Attached when the window opens rather than when the view model is built, so a screen that
        // was closed is not still copying every log line into a list nobody is looking at.
        window.Opened += (_, _) => model.Attach();
        window.Closed += (_, _) => model.Dispose();

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
            profile,
            startsInPlainText: settings.Current.General.ProfileEditor == ProfileEditorView.PlainText);

        ProfileEditorWindow window = new() { DataContext = model };

        window.Opened += async (_, _) => await model.LoadAsync();

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
