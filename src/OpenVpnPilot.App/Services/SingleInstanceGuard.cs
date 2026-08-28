using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Ensures only one copy of the application runs per user.
/// </summary>
/// <remarks>
/// A second copy would drive its own tunnels while writing to the same profile database, so the
/// later start hands over to the running one instead. The handover uses a named pipe rather than
/// only a mutex, so the first copy can bring its window forward even when it is hidden in the tray.
/// The names are scoped to the current user, which keeps separate sessions on a shared machine
/// independent of each other.
/// </remarks>
public sealed partial class SingleInstanceGuard : IDisposable
{
    private const string ActivateMessage = "activate";

    private readonly string mutexName;
    private readonly string pipeName;
    private Mutex? mutex;
    private CancellationTokenSource? listener;
    private bool disposed;

    public SingleInstanceGuard(string? identity = null)
    {
        string scope = identity ?? Environment.UserName;

        mutexName = $"Local\\OpenVpnPilot.Instance.{scope}";
        pipeName = $"OpenVpnPilot.Activate.{scope}";
    }

    /// <summary>
    /// Raised when another copy asked this one to come forward.
    /// </summary>
    public event EventHandler? ActivationRequested;

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
        string scope = identity ?? Environment.UserName;

        // Windows refuses to let a background process raise its own window. The copy that is
        // starting is allowed to, so it hands that right to the one that already runs before
        // asking it to come forward.
        GrantForegroundRightToRunningCopy();

        try
        {
            await using NamedPipeClientStream pipe = new(
                ".",
                $"OpenVpnPilot.Activate.{scope}",
                PipeDirection.Out);

            await pipe.ConnectAsync(timeout: 2000);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(ActivateMessage));
        }
        catch (TimeoutException)
        {
            // The other copy is not listening yet or is closing.
        }
        catch (IOException)
        {
            // Same reasoning as above.
        }
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
        byte[] buffer = new byte[64];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);

                int read = await pipe.ReadAsync(buffer, cancellationToken);
                string message = Encoding.UTF8.GetString(buffer, 0, read);

                if (message == ActivateMessage)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A broken connection only affects one handover attempt, so listening continues.
            }
        }
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
