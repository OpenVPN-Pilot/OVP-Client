using System.Net.NetworkInformation;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Says when the addresses of this computer's network interfaces change.
/// </summary>
/// <remarks>
/// A change of network, a cable pulled or a different wireless network joined, leaves a tunnel in
/// the same position as a sleep does: its packets now leave from somewhere the server does not
/// know. The tunnels coming up and going down change the addresses too, so a change is a reason to
/// check, never a verdict.
/// </remarks>
public interface INetworkChangeEvents
{
    /// <summary>
    /// Raised on a thread of the system's choosing whenever an interface address changed.
    /// </summary>
    public event EventHandler? AddressChanged;
}

/// <summary>
/// The address change notification .NET offers on every platform this runs on.
/// </summary>
internal sealed class SystemNetworkChangeEvents : INetworkChangeEvents, IDisposable
{
    private readonly Lock gate = new();
    private EventHandler? handlers;
    private bool subscribed;

    public event EventHandler? AddressChanged
    {
        add
        {
            lock (gate)
            {
                handlers += value;

                // Subscribed on first use only, so nothing listens to the system until it is needed.
                if (!subscribed)
                {
                    NetworkChange.NetworkAddressChanged += OnAddressChanged;
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

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        EventHandler? current;

        lock (gate)
        {
            current = handlers;
        }

        current?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (subscribed)
            {
                NetworkChange.NetworkAddressChanged -= OnAddressChanged;
                subscribed = false;
            }
        }
    }
}
