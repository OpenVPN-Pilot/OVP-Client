using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.OpenVpn.Tests.Runtime;

/// <summary>
/// The stand ins the supervisor and the manager are driven with.
/// </summary>
/// <remarks>
/// Shared so both suites exercise the same behaviour rather than two hand written approximations of
/// it, in the same way the management transport is shared.
/// </remarks>
internal sealed class FakeLauncher : IOpenVpnLauncher
{
    /// <summary>
    /// Reported as the process identifier.
    /// </summary>
    /// <remarks>
    /// Zero on purpose. Tearing a connection down confirms the process ended and terminates it if it
    /// did not, so a plausible identifier would have the suite look for, and possibly end, whatever
    /// unrelated process happens to hold it on the machine running the tests. Zero names no single
    /// process on any system, and the supervisor refuses to look it up at all: on Unix the lookup
    /// succeeds, because zero addresses the caller's own process group.
    /// </remarks>
    public OpenVpnLaunchResult Result { get; set; } = OpenVpnLaunchResult.Started(0);

    public OpenVpnLaunchRequest? LastRequest { get; private set; }

    public Task<OpenVpnLaunchResult> LaunchAsync(
        OpenVpnLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeChannelFactory : IManagementChannelFactory
{
    private readonly Func<Stream> next;

    public FakeChannelFactory(Stream stream) => next = () => stream;

    public FakeChannelFactory(Func<Stream> next) => this.next = next;

    public Task<Stream> ConnectAsync(int port, CancellationToken cancellationToken) =>
        Task.FromResult(next());
}

internal sealed class FakeCredentialProvider : ICredentialProvider
{
    /// <summary>
    /// Answer used when <see cref="Responses"/> is empty.
    /// </summary>
    public VpnCredentials? Response { get; set; }

    /// <summary>
    /// Answers for consecutive requests, so a rejected first attempt can be modelled.
    /// </summary>
    public Queue<VpnCredentials?> Responses { get; } = new();

    public List<CredentialRequest> Requests { get; } = [];

    public Task<VpnCredentials?> RequestAsync(CredentialRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : Response);
    }
}

/// <summary>
/// Writes nothing. The manager only needs a path and a directory to hand to the launcher.
/// </summary>
internal sealed class FakeMaterializer : IProfileMaterializer
{
    public Task<MaterialisedProfile> MaterialiseAsync(
        Guid profileId,
        string configuration,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MaterialisedProfile(
            Path.Combine(Path.GetTempPath(), $"openvpnpilot-test-{profileId:N}.ovpn")));

    public int RemoveStaleFiles() => 0;
}

internal sealed class FixedPortAllocator : IPortAllocator
{
    public int Reserve() => 25340;
}

/// <summary>
/// Keeps the identifier of every event logged, so a test can assert what was and was not reported.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<int> eventIds = new();

    public IReadOnlyCollection<int> EventIds => eventIds;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => eventIds.Enqueue(eventId.Id);
}

/// <summary>
/// Records which processes it was asked to end, and ends none of them.
/// </summary>
internal sealed class RecordingTerminator : IOpenVpnProcessTerminator
{
    private readonly ConcurrentQueue<int> processIds = new();

    public IReadOnlyCollection<int> ProcessIds => processIds;

    public Task EnsureExitedAsync(int processId, TimeSpan grace, CancellationToken cancellationToken = default)
    {
        processIds.Enqueue(processId);
        return Task.CompletedTask;
    }
}
