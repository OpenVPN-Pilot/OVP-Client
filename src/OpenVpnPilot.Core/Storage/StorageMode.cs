namespace OpenVpnPilot.Core.Storage;

/// <summary>
/// Where the profiles a machine shows come from. Never both at once.
/// </summary>
public enum StorageMode
{
    /// <summary>
    /// The library on this computer, which is all there was before servers existed.
    /// </summary>
    Local,

    /// <summary>
    /// One configured server, worked on through a local copy that is kept in step with it.
    /// </summary>
    Server,
}
