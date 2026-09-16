using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenVpnPilot.Platform.MacOS.Helper.Native;

namespace OpenVpnPilot.Platform.MacOS.Helper.Tunnels;

/// <summary>
/// One process the helper started, waited for and, when asked, ended.
/// </summary>
/// <remarks>
/// The process inherits exactly three things: the null device as standard input, a pipe for its
/// output, and a pipe carrying the management password as descriptor three. Everything else is closed
/// by the spawn itself, so no socket of another session and no file of another tunnel can leak into
/// a process that runs as root.
///
/// The wait does not reap. The process stays a zombie until it is reaped under the same lock that
/// guards sending it a signal, so a signal can never reach a process that was given the same
/// identifier after this one ended.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class SpawnedProcess
{
    /// <summary>
    /// The descriptor the management password arrives on, named as /dev/fd/3 on the command line.
    /// </summary>
    public const int SecretDescriptor = 3;

    private const int WaitExited = 0x04;
    private const int WaitNoWait = 0x20;
    private const int IdentifierTypeProcess = 1;

    private readonly Lock gate = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> recentOutput = new();
    private bool reaped;

    private SpawnedProcess(int processId)
    {
        ProcessId = processId;
    }

    public int ProcessId { get; }

    /// <summary>
    /// Completes with the raw wait status once the process has ended and been reaped.
    /// </summary>
    public Task<int> Exited => exited.Task;

    /// <summary>
    /// Raised for every line the process writes, on the thread that reads them.
    /// </summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>
    /// Completes once everything the process wrote has been read.
    /// </summary>
    /// <remarks>
    /// A process can have ended while its last lines are still in the pipe, so anything that reads
    /// what it said has to wait for this and not for <see cref="Exited"/>.
    /// </remarks>
    public Task OutputDrained => drained.Task;

    /// <summary>
    /// The last lines the process wrote, for explaining why it ended.
    /// </summary>
    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (gate)
            {
                return [.. recentOutput];
            }
        }
    }

    /// <summary>
    /// Starts a process with a fixed environment and a secret on descriptor three.
    /// </summary>
    /// <param name="executable">An absolute path. Nothing is looked up on a search path.</param>
    /// <param name="arguments">The arguments after the executable, each one element of argv.</param>
    /// <param name="environment">The complete environment, name=value.</param>
    /// <param name="secret">Written to descriptor three and never anywhere else.</param>
    public static unsafe SpawnedProcess Start(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> environment,
        string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(secret);

        if (!Path.IsPathRooted(executable))
        {
            throw new ArgumentException("The executable must be named by an absolute path.", nameof(executable));
        }

        int* secretPipe = stackalloc int[2];
        int* outputPipe = stackalloc int[2];

        if (Libc.pipe(secretPipe) != 0)
        {
            throw new IOException($"A pipe could not be created: errno {Marshal.GetLastPInvokeError()}.");
        }

        if (Libc.pipe(outputPipe) != 0)
        {
            _ = Libc.close(secretPipe[0]);
            _ = Libc.close(secretPipe[1]);
            throw new IOException($"A pipe could not be created: errno {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            // Above the three descriptors the child's file actions fill, so no dup2 in them can
            // overwrite a descriptor another one of them still has to copy.
            for (int index = 0; index < 2; index++)
            {
                secretPipe[index] = MoveAboveStandardDescriptors(secretPipe[index]);
                outputPipe[index] = MoveAboveStandardDescriptors(outputPipe[index]);
            }

            WriteSecret(secretPipe[1], secret);
            _ = Libc.close(secretPipe[1]);
            secretPipe[1] = -1;

            int processId = Spawn(executable, arguments, environment, secretPipe[0], outputPipe[1]);

            SpawnedProcess process = new(processId);
            _ = Libc.close(outputPipe[1]);
            outputPipe[1] = -1;

            process.ReadOutput(outputPipe[0]);
            outputPipe[0] = -1;

            process.Wait();
            return process;
        }
        finally
        {
            foreach (int descriptor in new[] { secretPipe[0], secretPipe[1], outputPipe[0], outputPipe[1] })
            {
                if (descriptor >= 0)
                {
                    _ = Libc.close(descriptor);
                }
            }
        }
    }

    /// <summary>
    /// Sends a signal, unless the process has already been reaped.
    /// </summary>
    /// <returns>False when there was no process left to signal.</returns>
    public bool Signal(int signal)
    {
        lock (gate)
        {
            return !reaped && Libc.kill(ProcessId, signal) == 0;
        }
    }

    /// <summary>
    /// Asks the process to end, and ends it when it does not within the grace period.
    /// </summary>
    public async Task<int> TerminateAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        if (!Exited.IsCompleted)
        {
            Signal(Libc.SigTerm);

            try
            {
                return await Exited.WaitAsync(grace, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Ignored the request. What follows cannot be ignored.
            }

            Signal(Libc.SigKill);
        }

        return await Exited.WaitAsync(cancellationToken);
    }

    private static unsafe int Spawn(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> environment,
        int secretDescriptor,
        int outputDescriptor)
    {
        nint actions = 0;
        nint attributes = 0;

        Check(Libc.posix_spawn_file_actions_init(&actions), "file actions");

        try
        {
            Check(Libc.posix_spawnattr_init(&attributes), "attributes");

            try
            {
                Check(Libc.posix_spawn_file_actions_addopen(&actions, 0, "/dev/null", Libc.OpenReadOnly, 0), "standard input");
                Check(Libc.posix_spawn_file_actions_adddup2(&actions, outputDescriptor, 1), "standard output");
                Check(Libc.posix_spawn_file_actions_adddup2(&actions, outputDescriptor, 2), "standard error");
                Check(Libc.posix_spawn_file_actions_adddup2(&actions, secretDescriptor, CommandLine.SecretDescriptor), "secret");

                uint allSignals = uint.MaxValue;
                uint noSignals = 0;

                Check(
                    Libc.posix_spawnattr_setflags(
                        &attributes,
                        (short)(Libc.SpawnCloseExecDefault | Libc.SpawnSetSignalDefault | Libc.SpawnSetSignalMask)),
                    "flags");

                Check(Libc.posix_spawnattr_setsigdefault(&attributes, &allSignals), "signal defaults");
                Check(Libc.posix_spawnattr_setsigmask(&attributes, &noSignals), "signal mask");

                using Libc.StringArray argv = new([executable, .. arguments]);
                using Libc.StringArray envp = new(environment);

                byte[] path = [.. Encoding.UTF8.GetBytes(executable), 0];
                int processId;

                fixed (byte* pathPointer = path)
                {
                    int result = Libc.posix_spawn(
                        &processId,
                        pathPointer,
                        &actions,
                        &attributes,
                        (byte**)argv.Pointer,
                        (byte**)envp.Pointer);

                    if (result != 0)
                    {
                        throw new IOException($"{executable} could not be started: errno {result}.");
                    }
                }

                return processId;
            }
            finally
            {
                _ = Libc.posix_spawnattr_destroy(&attributes);
            }
        }
        finally
        {
            _ = Libc.posix_spawn_file_actions_destroy(&actions);
        }
    }

    /// <summary>
    /// Duplicates a descriptor to a number of ten or above and closes the original.
    /// </summary>
    /// <remarks>
    /// Ten or above, so that no dup2 among the child's file actions can overwrite a descriptor
    /// another one of them still has to copy from.
    ///
    /// One fcntl with F_DUPFD_CLOEXEC would be this whole method, and it is unreachable from managed
    /// code on this architecture; <see cref="Libc.dup"/> says why. dup takes the lowest free number
    /// instead, so the low ones are held until a high enough one comes back and then given up again.
    /// At most ten are held, because each one taken makes the next dup return a higher number.
    ///
    /// Close on exec is not set, and is not needed: every process this class starts is spawned with
    /// POSIX_SPAWN_CLOEXEC_DEFAULT, so a descriptor reaches a child only when a file action names it.
    /// </remarks>
    private static int MoveAboveStandardDescriptors(int descriptor)
    {
        const int floor = 10;

        List<int> held = [];

        try
        {
            while (true)
            {
                int moved = Libc.dup(descriptor);

                if (moved < 0)
                {
                    throw new IOException($"A descriptor could not be moved: errno {Marshal.GetLastPInvokeError()}.");
                }

                if (moved >= floor)
                {
                    _ = Libc.close(descriptor);
                    return moved;
                }

                held.Add(moved);
            }
        }
        finally
        {
            foreach (int low in held)
            {
                _ = Libc.close(low);
            }
        }
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new IOException($"The spawn {what} could not be prepared: errno {result}.");
        }
    }

    private static unsafe void WriteSecret(int descriptor, string secret)
    {
        // The line feed stands in for the return key after the password is typed.
        byte[] bytes = Encoding.UTF8.GetBytes(secret + "\n");

        try
        {
            fixed (byte* pointer = bytes)
            {
                // A pipe holds far more than a password, so one write never blocks here.
                if (Libc.write(descriptor, pointer, bytes.Length) != bytes.Length)
                {
                    throw new IOException("The management password could not be handed over.");
                }
            }
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private void ReadOutput(int descriptor)
    {
        SafeFileHandle handle = new(descriptor, ownsHandle: true);

        Thread reader = new(() =>
        {
            try
            {
                using FileStream stream = new(handle, FileAccess.Read, bufferSize: 1);
                using StreamReader lines = new(stream, Encoding.UTF8);

                while (lines.ReadLine() is { } line)
                {
                    lock (gate)
                    {
                        recentOutput.Enqueue(line);

                        while (recentOutput.Count > 40)
                        {
                            recentOutput.Dequeue();
                        }
                    }

                    OutputReceived?.Invoke(this, line);
                }
            }
            finally
            {
                drained.TrySetResult(true);
            }
        })
        {
            IsBackground = true,
            Name = $"openvpn {ProcessId} output",
        };

        reader.Start();
    }

    private unsafe void Wait()
    {
        Thread waiter = new(() =>
        {
            byte* information = stackalloc byte[256];

            // Wait for the end without reaping, so the identifier stays taken until the lock is held.
            while (Libc.waitid(IdentifierTypeProcess, (uint)ProcessId, information, WaitExited | WaitNoWait) != 0)
            {
                if (Marshal.GetLastPInvokeError() != Libc.EIntr)
                {
                    break;
                }
            }

            int status;

            lock (gate)
            {
                while (Libc.waitpid(ProcessId, out status, 0) < 0 && Marshal.GetLastPInvokeError() == Libc.EIntr)
                {
                    // Interrupted by a signal, which says nothing about the process. Wait again.
                }

                reaped = true;
            }

            exited.TrySetResult(status);
        })
        {
            IsBackground = true,
            Name = $"openvpn {ProcessId} wait",
        };

        waiter.Start();
    }
}
