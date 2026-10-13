namespace OpenVpnPilot.App.Services;

/// <summary>
/// Knows whether a credential prompt is waiting for somebody.
/// </summary>
/// <remarks>
/// The tunnel reports the same state while the client answers from storage and while a person is
/// typing, so the difference is only known where the prompt is shown. It matters because a prompt
/// opened behind another window waits for ever, and the icon is where that can be seen.
/// </remarks>
public interface ICredentialPromptState
{
    /// <summary>
    /// True while at least one prompt is open.
    /// </summary>
    public bool IsWaiting { get; }

    /// <summary>
    /// Raised when <see cref="IsWaiting"/> may have changed.
    /// </summary>
    public event EventHandler? Changed;
}

/// <inheritdoc cref="ICredentialPromptState"/>
public sealed class CredentialPromptTracker : ICredentialPromptState
{
    private int open;

    public bool IsWaiting => Volatile.Read(ref open) > 0;

    public event EventHandler? Changed;

    /// <summary>
    /// Counts a prompt as open until the returned handle is disposed.
    /// </summary>
    public IDisposable Open()
    {
        Interlocked.Increment(ref open);
        Changed?.Invoke(this, EventArgs.Empty);
        return new Handle(this);
    }

    private void Close()
    {
        Interlocked.Decrement(ref open);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Handle(CredentialPromptTracker owner) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Close();
            }
        }
    }
}
