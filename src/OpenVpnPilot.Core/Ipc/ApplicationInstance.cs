namespace OpenVpnPilot.Core.Ipc;

/// <summary>
/// The claim that makes one copy of the application the only one for its user.
/// </summary>
/// <remarks>
/// The application and the companion command have to agree on it exactly, one to take it and the
/// other to ask whether it is taken, so it is built in one place.
///
/// On Windows it is a mutex in the session's local namespace, as it always was. On Unix a local name
/// is scoped to the login session, and every terminal window and every application opened from the
/// Finder is a session of its own. Measured on macOS: two copies started from two terminals both
/// took the claim, both ran, and the second took the command socket from the first. There the claim
/// is a named mutex scoped to the user and not to the session, which .NET keeps in a directory only
/// that user can open, so another account cannot take it first either.
/// </remarks>
public static class ApplicationInstance
{
    /// <summary>
    /// Creates the claim for a user, or opens the one that already exists.
    /// </summary>
    /// <param name="initiallyOwned">True to take the claim when it is created.</param>
    /// <param name="identity">The user it is scoped to; the current user when null.</param>
    /// <param name="created">False when another copy already holds it.</param>
    public static Mutex CreateClaim(bool initiallyOwned, string? identity, out bool created)
    {
        string scope = identity ?? Environment.UserName;

        if (OperatingSystem.IsWindows())
        {
            return new Mutex(initiallyOwned, $"Local\\OpenVpnPilot.Instance.{scope}", out created);
        }

        return new Mutex(
            initiallyOwned,
            $"OpenVpnPilot.Instance.{scope}",
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false },
            out created);
    }
}
