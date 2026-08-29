using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Registers the stored shortcuts and reports which of them could not be claimed.
/// </summary>
/// <remarks>
/// A shortcut that fails to register does nothing and says nothing, which is the worst possible
/// outcome for a feature whose entire value is that it works from anywhere. Every attempt is
/// therefore kept, successful or not, so the settings screen can show the ones another application
/// already owns.
/// </remarks>
public sealed class HotkeyCoordinator : IDisposable
{
    private readonly IGlobalHotkeyService hotkeys;
    private readonly IHotkeyStore store;
    private readonly ISettingsService settings;
    private readonly List<HotkeyRegistration> registrations = [];
    private bool disposed;

    public HotkeyCoordinator(
        IGlobalHotkeyService hotkeys,
        IHotkeyStore store,
        ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        this.hotkeys = hotkeys;
        this.store = store;
        this.settings = settings;
    }

    /// <summary>
    /// Raised with the action identifier when a registered combination is pressed.
    /// </summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>
    /// Raised after the shortcuts were registered, so a screen can refresh what it shows.
    /// </summary>
    public event EventHandler? RegistrationsChanged;

    /// <summary>
    /// Every attempt made, in the order the actions are listed.
    /// </summary>
    public IReadOnlyList<HotkeyRegistration> Registrations
    {
        get
        {
            lock (registrations)
            {
                return [.. registrations];
            }
        }
    }

    public bool IsAvailable => hotkeys.IsAvailable;

    /// <summary>
    /// Reads the bindings and claims them. Must be called from the user interface thread.
    /// </summary>
    public async Task AttachAsync(CancellationToken cancellationToken = default)
    {
        hotkeys.Pressed += OnPressed;

        if (!settings.Current.General.HotkeyDefaultsApplied)
        {
            await store.EnsureDefaultsAsync(cancellationToken);
            await settings.UpdateAsync(
                current => current.General.HotkeyDefaultsApplied = true,
                cancellationToken);
        }

        await ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// Releases every combination and claims the stored ones again.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!hotkeys.IsAvailable)
        {
            return;
        }

        IReadOnlyList<HotkeyBindingRecord> bindings = await store.GetBindingsAsync(cancellationToken);

        hotkeys.UnregisterAll();

        lock (registrations)
        {
            registrations.Clear();

            foreach (HotkeyBindingRecord binding in bindings)
            {
                if (!binding.IsEnabled)
                {
                    continue;
                }

                if (!HotkeyGesture.TryParse(binding.Gesture, out HotkeyGesture? gesture))
                {
                    registrations.Add(new HotkeyRegistration(
                        binding.ActionId,
                        new HotkeyGesture(HotkeyModifiers.None, binding.Gesture),
                        Succeeded: false,
                        $"'{binding.Gesture}' is not a valid combination."));

                    continue;
                }

                registrations.Add(hotkeys.Register(binding.ActionId, gesture));
            }
        }

        RegistrationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPressed(object? sender, string actionId) =>
        ActionRequested?.Invoke(this, actionId);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        hotkeys.Pressed -= OnPressed;
        hotkeys.Dispose();
    }
}
