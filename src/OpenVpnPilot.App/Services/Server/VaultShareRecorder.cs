using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Shares a sign in that was typed and worked, so nobody else using the server has to type it.
/// </summary>
/// <remarks>
/// <para>
/// Listens to the connection manager the way <see cref="SessionRecorder"/> does. On
/// <c>Connected</c> every answer typed for that attempt becomes a vault marker, which the push sends
/// as a request to add the entry; the server keeps the first one it is given and answers anybody
/// later that it has one. On <c>Failed</c> and <c>Disconnected</c> before that, what was typed is
/// forgotten, because it did not work or was never tried.
/// </para>
/// <para>
/// The markers are recorded through <see cref="IChangeRecorder"/>, which writes nothing on the
/// local library, and the ledger keeps nothing there in the first place.
/// </para>
/// </remarks>
public sealed class VaultShareRecorder : IAsyncDisposable
{
    private readonly ConnectionManager connections;
    private readonly ITypedCredentialLedger ledger;
    private readonly IHeldVaultSecrets held;
    private readonly IChangeRecorder recorder;
    private readonly ILogger<VaultShareRecorder> logger;

    private readonly Channel<ConnectionStatusChanged> pending =
        Channel.CreateUnbounded<ConnectionStatusChanged>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource lifetime = new();
    private Task? pump;
    private bool attached;
    private bool disposed;

    public VaultShareRecorder(
        ConnectionManager connections,
        ITypedCredentialLedger ledger,
        IHeldVaultSecrets held,
        IChangeRecorder recorder,
        ILogger<VaultShareRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(logger);

        this.connections = connections;
        this.ledger = ledger;
        this.held = held;
        this.recorder = recorder;
        this.logger = logger;
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        attached = true;
        pump = Task.Run(() => PumpAsync(lifetime.Token), CancellationToken.None);
        connections.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Acts on one change of a connection's state, in the order they arrived.
    /// </summary>
    internal async Task HandleAsync(ConnectionStatusChanged change, CancellationToken cancellationToken)
    {
        switch (change.Status.State)
        {
            case VpnConnectionState.Connected:
                foreach (TypedAnswer answer in ledger.TakeConnected(change.ProfileId))
                {
                    if (answer.Unremembered is { } secret)
                    {
                        held.Hold(answer.ProfileId, answer.Realm, secret);
                    }

                    await recorder.RecordAsync(PendingChangeKind.VaultAdd, answer.ProfileId, answer.Realm, cancellationToken);
                    VaultShareLog.Recorded(logger, answer.ProfileId, answer.Realm, answer.Unremembered is null);
                }

                break;

            case VpnConnectionState.Failed:
            case VpnConnectionState.Disconnected:
                ledger.Forget(change.ProfileId);
                break;

            default:
                break;
        }
    }

    private void OnStateChanged(object? sender, ConnectionStatusChanged change) => pending.Writer.TryWrite(change);

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ConnectionStatusChanged change in pending.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await HandleAsync(change, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Sharing is a courtesy to the next person, never a condition of the tunnel. The
                    // sign in is simply not shared, and that is worth a line in the log.
                    VaultShareLog.RecordFailed(logger, change.ProfileId, exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connections.StateChanged -= OnStateChanged;
        pending.Writer.TryComplete();
        await lifetime.CancelAsync();

        if (pump is not null)
        {
            await pump;
        }

        lifetime.Dispose();
    }
}
