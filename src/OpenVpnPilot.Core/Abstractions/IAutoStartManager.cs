namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Controls whether the application starts with the operating system.
/// </summary>
/// <remarks>
/// This is a setting the user turns on, never a default. It is expressed as a capability rather than
/// a file or a registry value so that each platform can use its own mechanism.
/// </remarks>
public interface IAutoStartManager
{
    /// <summary>
    /// False when autostart cannot be configured, for example because the entry is managed by
    /// policy. The setting is then shown as unavailable rather than as off.
    /// </summary>
    public bool IsSupported { get; }

    public bool IsEnabled();

    /// <summary>
    /// Turns autostart on or off.
    /// </summary>
    /// <returns>False when the change could not be made, with no state altered.</returns>
    public bool SetEnabled(bool enabled);
}
