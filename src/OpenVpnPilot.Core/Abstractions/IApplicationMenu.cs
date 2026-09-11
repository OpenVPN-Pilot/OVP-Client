namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// The application menu some platforms show in their own menu bar, named after the application.
/// </summary>
/// <remarks>
/// macOS expects every application to fill that menu, and fills it with somebody else's name when it
/// does not: left alone, the framework's default puts an entry about the framework where the entry
/// about the application belongs. The presentation layer describes the entries; what is left here
/// is what only the platform can do, which is showing its own standard panel about the application.
/// A platform without such a menu does not register this at all.
/// </remarks>
public interface IApplicationMenu
{
    /// <summary>
    /// Shows the platform's standard panel with the application's name, version and copyright.
    /// </summary>
    public void ShowAbout();
}
