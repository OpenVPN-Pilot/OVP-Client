using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Follows everything the notification area icon's state depends on and says when it changes.
/// </summary>
/// <remarks>
/// <para>
/// The sources report on their own threads and at their own pace, so the state is recomputed from
/// all of them on every report and announced only when the result differs. Throughput, which every
/// tunnel reports each second, is not a source.
/// </para>
/// <para>
/// A failure is remembered after its tunnel is gone, because the connection manager retires a
/// connection the moment it ends and an icon that went red for a fraction of a second would say
/// nothing. It is forgotten when the profile is started again, when the retry logic takes it over,
/// and when <see cref="AcknowledgeFailures"/> says somebody has looked.
/// </para>
/// </remarks>
public sealed class TrayStatusMonitor : IDisposable
{
    private readonly ConnectionManager connections;
    private readonly ReconnectSupervisor reconnects;
    private readonly ICredentialPromptState prompts;
    private readonly EnvironmentGate environment;
    private readonly IServerStatusSource? server;

    private readonly Lock gate = new();
    private readonly HashSet<Guid> unseenFailures = [];
    private IReadOnlyList<TrayStatus> applicable = [TrayStatus.IdleLocal];
    private bool attached;
    private bool disposed;

    public TrayStatusMonitor(
        ConnectionManager connections,
        ReconnectSupervisor reconnects,
        ICredentialPromptState prompts,
        EnvironmentGate environment,
        IServerStatusSource? server = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(reconnects);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(environment);

        this.connections = connections;
        this.reconnects = reconnects;
        this.prompts = prompts;
        this.environment = environment;
        this.server = server;
    }

    /// <summary>
    /// Raised on a background thread after <see cref="Applicable"/> changed.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Every state that applies, most pressing first.
    /// </summary>
    public IReadOnlyList<TrayStatus> Applicable
    {
        get
        {
            lock (gate)
            {
                return applicable;
            }
        }
    }

    /// <summary>
    /// The most pressing state, whatever the colours say about it.
    /// </summary>
    public TrayStatus Current => Applicable[0];

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        attached = true;
        connections.StateChanged += OnStateChanged;
        reconnects.WaitingChanged += OnSourceChanged;
        prompts.Changed += OnSourceChanged;
        environment.Changed += OnSourceChanged;

        if (server is not null)
        {
            server.Changed += OnSourceChanged;
        }

        Recompute();
    }

    /// <summary>
    /// Forgets the failures shown so far, because somebody has seen them where they are explained.
    /// </summary>
    public void AcknowledgeFailures()
    {
        lock (gate)
        {
            if (unseenFailures.Count == 0)
            {
                return;
            }

            unseenFailures.Clear();
        }

        Recompute();
    }

    private void OnStateChanged(object? sender, ConnectionStatusChanged change)
    {
        lock (gate)
        {
            if (IsUnexpectedEnd(change.Status))
            {
                unseenFailures.Add(change.ProfileId);
            }
            else
            {
                unseenFailures.Remove(change.ProfileId);
            }
        }

        Recompute();
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Recompute();

    /// <summary>
    /// True for a tunnel that ended without anybody asking it to.
    /// </summary>
    private static bool IsUnexpectedEnd(VpnConnectionStatus status) =>
        status.State is VpnConnectionState.Failed or VpnConnectionState.Disconnected
        && status.Failure is not (VpnFailureKind.None or VpnFailureKind.UserRequested);

    private void Recompute()
    {
        IReadOnlyList<TrayStatus> next;

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            IReadOnlyCollection<Guid> active = connections.ActiveProfiles;
            IReadOnlyCollection<Guid> waiting = reconnects.Waiting;

            TrayStatusInputs inputs = new(
                Tunnels: [.. active.Select(profile => connections.GetStatus(profile).State)],
                WaitingToReconnect: waiting.Count(profile => !active.Contains(profile)),
                UnseenFailures: unseenFailures.Count(profile => !waiting.Contains(profile)),
                AwaitingInput: prompts.IsWaiting,
                EnvironmentBlocked: environment.IsBlocked,
                Server: server?.Current.State);

            next = TrayStatusResolver.Resolve(inputs);

            if (next.SequenceEqual(applicable))
            {
                return;
            }

            applicable = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (gate)
        {
            disposed = true;
        }

        if (!attached)
        {
            return;
        }

        connections.StateChanged -= OnStateChanged;
        reconnects.WaitingChanged -= OnSourceChanged;
        prompts.Changed -= OnSourceChanged;
        environment.Changed -= OnSourceChanged;

        if (server is not null)
        {
            server.Changed -= OnSourceChanged;
        }
    }
}
