using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// The settings screen. Also the shortcut recorder, which has to happen at the view level.
/// </summary>
/// <remarks>
/// Recording captures the next combination pressed anywhere in the window and hands it to whichever
/// row is armed. The key event is intercepted before the focused control sees it, otherwise the key
/// would also be typed into a text box or move the tab selection while it is being recorded.
/// </remarks>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Tunnelling, so an armed row wins over whatever currently has the focus.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        this.FindControl<Button>("DiagnosticsButton")!.Click += async (_, _) => await WriteDiagnosticsAsync();
        this.FindControl<Button>("JoinLibraryButton")!.Click += async (_, _) => await JoinLibraryAsync();
        this.FindControl<Button>("CreateLibraryButton")!.Click += async (_, _) => await CreateLibraryAsync();
        this.FindControl<Button>("LibraryPassphraseButton")!.Click += async (_, _) => await AskForLibraryPassphraseAsync();
    }

    private static readonly FilePickerFileType PackageType = new("OpenVpnPilot package") { Patterns = ["*.ovppkg"] };

    /// <summary>
    /// Picks a shared file and asks for the passphrase it opens with.
    /// </summary>
    private async Task JoinLibraryAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [PackageType],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await PassphraseWindow.AskAsync(this, ViewModel.CreateJoinPrompt(path));
        }
    }

    /// <summary>
    /// Picks where the shared file goes and asks for the passphrase it is to be written with.
    /// </summary>
    private async Task CreateLibraryAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "openvpnpilot-library.ovppkg",
            DefaultExtension = "ovppkg",
            FileTypeChoices = [PackageType],
            ShowOverwritePrompt = false,
        });

        if (target?.TryGetLocalPath() is { } path)
        {
            await PassphraseWindow.AskAsync(this, ViewModel.CreateCreatePrompt(path));
        }
    }

    private async Task AskForLibraryPassphraseAsync()
    {
        if (ViewModel is not null)
        {
            await PassphraseWindow.AskAsync(this, ViewModel.CreatePassphrasePrompt());
        }
    }

    /// <summary>
    /// Asks where the diagnostics bundle should go and writes it there.
    /// </summary>
    private async Task WriteDiagnosticsAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = ViewModel.DiagnosticsFileName,
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }],
        });

        if (target?.TryGetLocalPath() is { } path)
        {
            await ViewModel.WriteDiagnosticsAsync(path);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        HotkeyEditorViewModel? recording = ViewModel?.Hotkeys.FirstOrDefault(editor => editor.IsRecording);

        if (recording is null)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            recording.CancelRecording();
            return;
        }

        // A modifier on its own is half a combination, so it is ignored until a real key follows.
        if (IsModifier(e.Key))
        {
            return;
        }

        recording.Record(ToModifiers(e.KeyModifiers), KeyName(e.Key));
    }

    private static bool IsModifier(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;

    private static HotkeyModifiers ToModifiers(KeyModifiers modifiers)
    {
        HotkeyModifiers result = HotkeyModifiers.None;

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }

    /// <summary>
    /// The portable name a binding is stored under.
    /// </summary>
    /// <remarks>
    /// Avalonia names the number row D0 to D9, which is also what the platform layer understands, so
    /// the enumeration name is used directly rather than being translated twice.
    /// </remarks>
    private static string KeyName(Key key) => key.ToString();
}
