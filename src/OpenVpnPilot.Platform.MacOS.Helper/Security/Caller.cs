using System.Net.Sockets;
using System.Runtime.Versioning;
using OpenVpnPilot.Platform.MacOS.Helper.Native;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Helper.Security;

/// <summary>
/// Who is at the other end of a session, as the kernel reports it.
/// </summary>
/// <remarks>
/// The identity comes from the socket, never from anything the caller says. getpeereid reports the
/// effective user and group the peer had when it connected, which is the account whose rights it
/// is exercising; the process identifier is kept for the log only, because a process identifier can
/// be reused and decides nothing.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed record Caller(uint UserId, uint GroupId, int ProcessId, CallerAuthorisation Authorisation)
{
    /// <summary>
    /// True when the caller may start configurations of its own.
    /// </summary>
    public bool IsAuthorised => Authorisation is not CallerAuthorisation.None;

    /// <summary>
    /// Identifies the peer of a connected local socket.
    /// </summary>
    public static Caller Identify(Socket socket, IGroupMembership membership)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(membership);

        int descriptor = (int)socket.SafeHandle.DangerousGetHandle();

        if (Libc.getpeereid(descriptor, out uint userId, out uint groupId) != 0)
        {
            throw new InvalidOperationException("The peer of a helper connection could not be identified.");
        }

        uint length = sizeof(int);
        int processId = Libc.getsockopt(descriptor, Libc.SolLocal, Libc.LocalPeerPid, out int pid, ref length) == 0
            ? pid
            : 0;

        return new Caller(userId, groupId, processId, AuthorisationRule.Decide(userId, membership));
    }

    /// <summary>
    /// How the authorisation is reported to the caller.
    /// </summary>
    public string Describe() => Authorisation switch
    {
        CallerAuthorisation.Root => "root",
        CallerAuthorisation.Administrator => "a member of admin",
        CallerAuthorisation.Group => $"a member of {HelperInstallation.AuthorisedGroup}",
        _ => "not authorised",
    };
}

internal enum CallerAuthorisation
{
    None,
    Group,
    Administrator,
    Root,
}

/// <summary>
/// Group membership as the directory services see it.
/// </summary>
/// <remarks>
/// An interface so the decision can be tested without depending on the groups of whoever runs the
/// tests.
/// </remarks>
internal interface IGroupMembership
{
    public bool IsMember(uint userId, uint groupId);

    public uint? ResolveGroup(string name);
}

/// <summary>
/// Membership through the membership API, which follows nested groups and directory services the way
/// every other authorisation decision on the Mac does.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class DirectoryGroupMembership : IGroupMembership
{
    public bool IsMember(uint userId, uint groupId)
    {
        byte* user = stackalloc byte[16];
        byte* group = stackalloc byte[16];

        return Libc.mbr_uid_to_uuid(userId, user) == 0
            && Libc.mbr_gid_to_uuid(groupId, group) == 0
            && Libc.mbr_check_membership(user, group, out int isMember) == 0
            && isMember != 0;
    }

    public uint? ResolveGroup(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        const int bufferLength = 16 * 1024;
        byte* buffer = stackalloc byte[bufferLength];

        Libc.Group entry;
        Libc.Group* result;

        return Libc.getgrnam_r(name, &entry, buffer, bufferLength, &result) == 0 && result != null
            ? result->GroupId
            : null;
    }
}

/// <summary>
/// The rule that decides what a caller may start.
/// </summary>
/// <remarks>
/// Kept apart from the socket it is applied to, so it can be tested without depending on the groups
/// of whoever runs the tests, and on any system.
/// </remarks>
internal static class AuthorisationRule
{
    /// <summary>
    /// The same rule the Windows interactive service applies, with the macOS groups.
    /// </summary>
    /// <remarks>
    /// Root, a member of admin, or a member of the helper's own group may start configurations of
    /// their own. Anyone else may start only what an administrator installed. admin is addressed by
    /// its group number, which cannot be shadowed by a directory service group of the same name.
    /// </remarks>
    public static CallerAuthorisation Decide(uint userId, IGroupMembership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);

        if (userId == 0)
        {
            return CallerAuthorisation.Root;
        }

        if (membership.IsMember(userId, HelperInstallation.AdministratorsGroupId))
        {
            return CallerAuthorisation.Administrator;
        }

        return membership.ResolveGroup(HelperInstallation.AuthorisedGroup) is { } group
            && membership.IsMember(userId, group)
                ? CallerAuthorisation.Group
                : CallerAuthorisation.None;
    }
}
