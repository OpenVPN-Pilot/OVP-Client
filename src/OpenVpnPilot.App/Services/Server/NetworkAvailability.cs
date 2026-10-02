using System.Net.NetworkInformation;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Reports when the machine gains or loses a usable network.
/// </summary>
/// <remarks>
/// Behind an interface because the framework reports it through a static event, which a test could
/// neither raise nor isolate.
/// </remarks>
public interface INetworkAvailability
{
    /// <summary>
    /// Raised with true when a network became available and false when the last one went away.
    /// Raised on a thread of the system's choosing.
    /// </summary>
    public event EventHandler<bool>? AvailabilityChanged;
}

/// <summary>
/// The operating system's own report, through <see cref="NetworkChange"/>.
/// </summary>
internal sealed class SystemNetworkAvailability : INetworkAvailability, IDisposable
{
    private readonly Lock gate = new();
    private EventHandler<bool>? handlers;
    private bool subscribed;

    public event EventHandler<bool>? AvailabilityChanged
    {
        add
        {
            lock (gate)
            {
                handlers += value;

                // Subscribed on first use only, so nothing listens to the system in the local mode.
                if (!subscribed)
                {
                    NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
                    subscribed = true;
                }
            }
        }

        remove
        {
            lock (gate)
            {
                handlers -= value;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (subscribed)
            {
                NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
                subscribed = false;
            }

            handlers = null;
        }
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs arguments)
    {
        EventHandler<bool>? current;

        lock (gate)
        {
            current = handlers;
        }

        current?.Invoke(this, arguments.IsAvailable);
    }
}
