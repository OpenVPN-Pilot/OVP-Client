using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// One shortcut in the settings screen, with its combination and whatever is wrong with it.
/// </summary>
/// <remarks>
/// Recording is a mode rather than a dialog: the row is armed, the next combination is captured, and
/// the mode ends. That keeps the whole list editable without a window per shortcut.
/// </remarks>
public sealed partial class HotkeyEditorViewModel : ViewModelBase
{
    private readonly ILocalizer localizer;

    public HotkeyEditorViewModel(string actionId, string? gesture, ILocalizer localizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(localizer);

        ActionId = actionId;
        this.localizer = localizer;
        Gesture = gesture ?? string.Empty;
    }

    public string ActionId { get; }

    /// <summary>
    /// The action's name in the user's language.
    /// </summary>
    public string DisplayName => localizer["hotkey." + ActionId];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGesture))]
    [NotifyPropertyChangedFor(nameof(GestureDisplay))]
    public partial string Gesture { get; set; }

    /// <summary>
    /// Why this shortcut cannot be used, or null when it is fine.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    public bool HasGesture => Gesture.Length > 0;

    public bool HasProblem => Problem is { Length: > 0 };

    /// <summary>
    /// The combination as the platform writes it, or a note that there is none.
    /// </summary>
    /// <remarks>
    /// The stored form names the modifiers the way Windows does, and that form is kept because it is
    /// what the database holds. Only the display is translated: macOS writes a combination as the
    /// symbols printed on its keys, and a Mac user reading Windows+Alt+V would reasonably wonder
    /// which key that is.
    /// </remarks>
    public string GestureDisplay => Gesture.Length > 0 ? Describe(Gesture) : localizer["hotkey.unbound"];

    private string Describe(string stored)
    {
        if (!HotkeyGesture.TryParse(stored, out HotkeyGesture? gesture))
        {
            return stored;
        }

        string text = string.Empty;

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            text += localizer["hotkey.modifierControl"];
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            text += localizer["hotkey.modifierAlt"];
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            text += localizer["hotkey.modifierShift"];
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            text += localizer["hotkey.modifierWindows"];
        }

        return text + gesture.Key;
    }

    [RelayCommand]
    private void StartRecording()
    {
        Problem = null;
        IsRecording = true;
    }

    [RelayCommand]
    private void Clear()
    {
        Gesture = string.Empty;
        Problem = null;
        IsRecording = false;
    }

    /// <summary>
    /// Accepts a captured combination.
    /// </summary>
    /// <returns>False when the combination cannot be used as a system wide shortcut.</returns>
    public bool Record(HotkeyModifiers modifiers, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        HotkeyGesture captured = new(modifiers, key);

        if (!captured.IsUsable)
        {
            // A bare key would be taken away from every other application on the machine.
            Problem = localizer["hotkey.needsModifier"];
            return false;
        }

        Gesture = captured.ToString();
        Problem = null;
        IsRecording = false;
        return true;
    }

    public void CancelRecording() => IsRecording = false;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(GestureDisplay));
    }
}
