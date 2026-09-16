using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenVpnPilot.Platform.MacOS.Helper.Native;

/// <summary>
/// The system calls the helper makes itself rather than through .NET.
/// </summary>
/// <remarks>
/// Processes are started with posix_spawn and reaped with waitpid instead of through
/// <see cref="System.Diagnostics.Process"/>. The helper has to control exactly which descriptors a
/// root process inherits, which the class does not offer, and the class installs a child signal
/// handler that reaps children it did not start, which would race the helper's own waits.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe partial class Libc
{
    private const string Library = "libc";

    public const int SigTerm = 15;
    public const int SigKill = 9;

    public const int NoHang = 1;

    public const int EChild = 10;
    public const int EIntr = 4;
    public const int ESrch = 3;

    /// <summary>
    /// SOL_LOCAL and LOCAL_PEERPID: the process at the other end of a local socket.
    /// </summary>
    public const int SolLocal = 0;
    public const int LocalPeerPid = 2;

    /// <summary>
    /// Closes every descriptor the file actions do not name, which is macOS's own flag and the
    /// reason a spawned OpenVPN inherits nothing by accident.
    /// </summary>
    public const short SpawnCloseExecDefault = 0x4000;
    public const short SpawnSetSignalDefault = 0x04;
    public const short SpawnSetSignalMask = 0x08;

    [LibraryImport(Library, SetLastError = true)]
    public static partial int getpeereid(int socket, out uint effectiveUserId, out uint effectiveGroupId);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int getsockopt(int socket, int level, int name, out int value, ref uint length);

    [LibraryImport(Library)]
    public static partial uint geteuid();

    [LibraryImport(Library)]
    public static partial int getppid();

    [LibraryImport(Library, SetLastError = true)]
    public static partial int kill(int processId, int signal);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int waitpid(int processId, out int status, int options);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int waitid(int identifierType, uint identifier, byte* information, int options);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int pipe(int* descriptors);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int close(int descriptor);

    /// <summary>
    /// Duplicates a descriptor onto the lowest free number.
    /// </summary>
    /// <remarks>
    /// Used where fcntl with F_DUPFD_CLOEXEC would be the one call that says it. That call cannot be
    /// made from here: fcntl is variadic, and on arm64 macOS the ABI passes variadic arguments on the
    /// stack, so a declaration with a fixed third parameter hands it a register the callee never
    /// reads. Measured: the same call succeeds in C and fails through a fixed signature.
    /// </remarks>
    [LibraryImport(Library, SetLastError = true)]
    public static partial int dup(int descriptor);

    [LibraryImport(Library, SetLastError = true)]
    public static partial nint write(int descriptor, byte* buffer, nint count);

    [LibraryImport(Library)]
    public static partial uint umask(uint mask);

    [LibraryImport(Library)]
    public static partial int posix_spawn_file_actions_init(nint* actions);

    [LibraryImport(Library)]
    public static partial int posix_spawn_file_actions_destroy(nint* actions);

    [LibraryImport(Library)]
    public static partial int posix_spawn_file_actions_adddup2(nint* actions, int descriptor, int target);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int posix_spawn_file_actions_addopen(nint* actions, int descriptor, string path, int flags, int mode);

    [LibraryImport(Library)]
    public static partial int posix_spawnattr_init(nint* attributes);

    [LibraryImport(Library)]
    public static partial int posix_spawnattr_destroy(nint* attributes);

    [LibraryImport(Library)]
    public static partial int posix_spawnattr_setflags(nint* attributes, short flags);

    [LibraryImport(Library)]
    public static partial int posix_spawnattr_setsigdefault(nint* attributes, uint* signals);

    [LibraryImport(Library)]
    public static partial int posix_spawnattr_setsigmask(nint* attributes, uint* signals);

    [LibraryImport(Library)]
    public static partial int posix_spawn(int* processId, byte* path, nint* actions, nint* attributes, byte** arguments, byte** environment);

    [LibraryImport(Library)]
    public static partial int mbr_uid_to_uuid(uint userId, byte* uuid);

    [LibraryImport(Library)]
    public static partial int mbr_gid_to_uuid(uint groupId, byte* uuid);

    [LibraryImport(Library)]
    public static partial int mbr_check_membership(byte* user, byte* group, out int isMember);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int getgrnam_r(string name, Group* group, byte* buffer, nuint length, Group** result);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int launch_activate_socket(string name, int** descriptors, nuint* count);

    [LibraryImport(Library)]
    public static partial void free(void* pointer);

    /// <summary>
    /// open(2) flags for the null device a spawned process gets as standard input.
    /// </summary>
    public const int OpenReadOnly = 0;

    public static bool Exited(int status) => (status & 0x7F) == 0;

    public static int ExitCode(int status) => (status >> 8) & 0xFF;

    public static bool Signalled(int status) => (status & 0x7F) is not (0 or 0x7F);

    public static int TerminatingSignal(int status) => status & 0x7F;

    /// <summary>
    /// The layout of struct group, which getgrnam_r fills.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Group
    {
        public byte* Name;
        public byte* Password;
        public uint GroupId;
        public byte** Members;
    }

    /// <summary>
    /// A null terminated array of null terminated UTF-8 strings, as execve and posix_spawn take.
    /// </summary>
    public sealed class StringArray : IDisposable
    {
        private readonly List<nint> strings = [];

        public StringArray(IEnumerable<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            foreach (string value in values)
            {
                if (value.Contains('\0', StringComparison.Ordinal))
                {
                    Dispose();
                    throw new ArgumentException("An argument or variable carries a NUL character.", nameof(values));
                }

                byte[] bytes = Encoding.UTF8.GetBytes(value);
                nint memory = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                Marshal.WriteByte(memory, bytes.Length, 0);
                strings.Add(memory);
            }

            Pointer = Marshal.AllocHGlobal((strings.Count + 1) * IntPtr.Size);

            for (int index = 0; index < strings.Count; index++)
            {
                Marshal.WriteIntPtr(Pointer, index * IntPtr.Size, strings[index]);
            }

            Marshal.WriteIntPtr(Pointer, strings.Count * IntPtr.Size, 0);
        }

        public nint Pointer { get; private set; }

        public void Dispose()
        {
            foreach (nint memory in strings)
            {
                Marshal.FreeHGlobal(memory);
            }

            strings.Clear();

            if (Pointer != 0)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = 0;
            }
        }
    }
}
