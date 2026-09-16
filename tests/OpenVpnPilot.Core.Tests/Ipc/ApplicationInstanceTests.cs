using OpenVpnPilot.Core.Ipc;

namespace OpenVpnPilot.Core.Tests.Ipc;

/// <summary>
/// The claim that keeps a second copy of the application from running beside the first.
/// </summary>
/// <remarks>
/// Each test claims a name of its own, so a copy of the application running on the machine that
/// runs the tests is neither disturbed nor mistaken for the one under test.
/// </remarks>
public sealed class ApplicationInstanceTests
{
    [Fact]
    public void CreateClaim_ForAUserNobodyClaimed_IsCreated()
    {
        using Mutex claim = ApplicationInstance.CreateClaim(initiallyOwned: true, UniqueIdentity(), out bool created);

        Assert.True(created);
        claim.ReleaseMutex();
    }

    [Fact]
    public void CreateClaim_WhileAnotherCopyHoldsIt_ReportsItAsTaken()
    {
        string identity = UniqueIdentity();

        using Mutex first = ApplicationInstance.CreateClaim(initiallyOwned: true, identity, out bool firstCreated);
        using Mutex second = ApplicationInstance.CreateClaim(initiallyOwned: false, identity, out bool secondCreated);

        Assert.True(firstCreated);
        Assert.False(secondCreated);

        first.ReleaseMutex();
    }

    [Fact]
    public void CreateClaim_ForAnotherIdentity_IsIndependent()
    {
        using Mutex first = ApplicationInstance.CreateClaim(initiallyOwned: true, UniqueIdentity(), out bool firstCreated);
        using Mutex second = ApplicationInstance.CreateClaim(initiallyOwned: true, UniqueIdentity(), out bool secondCreated);

        Assert.True(firstCreated);
        Assert.True(secondCreated);

        first.ReleaseMutex();
        second.ReleaseMutex();
    }

    private static string UniqueIdentity() => "test-" + Guid.NewGuid().ToString("N");
}
