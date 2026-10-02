namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// What a change that did not come from a screen touched.
/// </summary>
[Flags]
public enum LibraryChanges
{
    None = 0,

    /// <summary>
    /// Profiles, their tags, favourites or stored sign ins: what the list, the quick switcher and
    /// the favourite shortcuts read.
    /// </summary>
    Profiles = 1,

    /// <summary>
    /// The shortcut bindings, which have to be claimed again.
    /// </summary>
    Hotkeys = 2,
}

/// <summary>
/// Tells the screens that the database changed underneath them.
/// </summary>
/// <remarks>
/// A screen that changes something reloads what it shows itself. The synchronisation writes into the
/// same database from a background thread and has no screen to reload, so it says what it changed
/// here and whoever shows that data picks it up.
/// </remarks>
public interface ILibraryChangeNotifier
{
    /// <summary>
    /// Raised on the thread that reported the change. Handlers move to the interface thread themselves.
    /// </summary>
    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public void Notify(LibraryChanges changes);
}

public sealed class LibraryChangedEventArgs(LibraryChanges changes) : EventArgs
{
    public LibraryChanges Changes { get; } = changes;
}

public sealed class LibraryChangeNotifier : ILibraryChangeNotifier
{
    public event EventHandler<LibraryChangedEventArgs>? Changed;

    public void Notify(LibraryChanges changes)
    {
        if (changes != LibraryChanges.None)
        {
            Changed?.Invoke(this, new LibraryChangedEventArgs(changes));
        }
    }
}
