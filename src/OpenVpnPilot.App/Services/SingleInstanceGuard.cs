using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using OpenVpnPilot.Core.Ipc;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Ensures only one copy of the application runs per user, and carries commands to it.
/// </summary>
/// <remarks>
/// A second copy would drive its own tunnels while writing to the same profile database, so the
/// later start hands over to the running one instead. The handover uses a named pipe rather than
/// only a mutex, so the first copy can bring its window forward even when it is hidden in the tray.
/// The names are scoped to the current user, which keeps separate sessions on a shared machine
/// independent of each other.
///
/// The same pipe carries the commands the companion command sends, so a connection asked for from a
/// terminal drives the tunnels the window already shows rather than starting a second set.
/// </remarks>
public sealed partial class SingleInstanceGuard : IDisposable
{
    private readonly string mutexName;
    private readonly string pipeName;
    private Mutex? mutex;
    private CancellationTokenSource? listener;
    private bool disposed;

    public SingleInstanceGuard(string? identity = null)
    {
        string scope = identity ?? Environment.UserName;

        mutexName = $"Local\\OpenVpnPilot.Instance.{scope}";
        pipeName = PilotCommandClient.PipeNameFor(scope);
    }

    /// <summary>
    /// Raised when another copy asked this one to come forward.
    /// </summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// Answers a command another process sent. The reply is returned to the sender.
    /// </summary>
    public Func<string, Task<string>>? CommandHandler { get; set; }

    /// <summary>
    /// Claims ownership for this process.
    /// </summary>
    /// <returns>True when this is the only copy, false when another one already runs.</returns>
    public bool TryClaim()
    {
        mutex = new Mutex(initiallyOwned: true, mutexName, out bool created);

        if (!created)
        {
            mutex.Dispose();
            mutex = null;
            return false;
        }

        listener = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(listener.Token), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Asks the copy that already runs to show itself. Failure is not an error worth reporting: the
    /// other copy may be shutting down, and this one exits either way.
    /// </summary>
    public static async Task RequestActivationAsync(string? identity = null)
    {
        // Windows refuses to let a background process raise its own window. The copy that is
        // starting is allowed to, so it hands that right to the one that already runs before
        // asking it to come forward.
        GrantForegroundRightToRunningCopy();

        await PilotCommandClient.SendAsync(PilotCommands.Activate, identity);
    }

    private static void GrantForegroundRightToRunningCopy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using Process current = Process.GetCurrentProcess();

        foreach (Process other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id != current.Id)
                {
                    AllowSetForegroundWindow(other.Id);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);

                int read = await pipe.ReadAsync(buffer, cancellationToken);
                string message = Encoding.UTF8.GetString(buffer, 0, read);

                string reply = await HandleAsync(message);

                if (reply.Length > 0)
                {
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply), cancellationToken);
                    await pipe.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A broken connection only affects one exchange, so listening continues.
            }
        }
    }

    private async Task<string> HandleAsync(string message)
    {
        if (message == PilotCommands.Activate)
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
            return "ok";
        }

        Func<string, Task<string>>? handler = CommandHandler;

        return handler is null ? string.Empty : await handler(message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        listener?.Cancel();
        listener?.Dispose();

        mutex?.ReleaseMutex();
        mutex?.Dispose();
    }
}
