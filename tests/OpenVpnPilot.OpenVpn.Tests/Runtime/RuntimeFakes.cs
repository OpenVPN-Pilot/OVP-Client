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
    /// unrelated process happens to hold it on the machine running the tests. No process ever has
    /// zero, so the lookup fails immediately and nothing is touched.
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
