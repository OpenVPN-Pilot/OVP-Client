namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Tells whether the database in use is a server's copy rather than the local library.
/// </summary>
/// <remarks>
/// To be implemented by the storage mode resolution, which decides the mode before the services are
/// composed. The mode cannot change while the application runs, because switching restarts it, so
/// the answer is the same for the whole life of the process.
///
/// Only what records local changes for the server depends on this. Everything else works the same
/// on either database.
/// </remarks>
public interface IStorageModeContext
{
    /// <summary>
    /// True when the application runs against a server and its database is that server's copy.
    /// </summary>
    public bool IsServerMode { get; }
}
