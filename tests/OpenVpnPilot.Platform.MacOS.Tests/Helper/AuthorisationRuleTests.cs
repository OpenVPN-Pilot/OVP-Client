using OpenVpnPilot.Platform.MacOS.Helper.Security;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Tests.Helper;

/// <summary>
/// Who may start a configuration of their own.
/// </summary>
/// <remarks>
/// The same rule the Windows interactive service applies: the administrators group, or a group named
/// for this purpose. Everyone else is left with the configurations an administrator installed. The
/// groups are asked of a stand in here, so the outcome does not depend on the groups of whoever runs
/// the tests.
/// </remarks>
public sealed class AuthorisationRuleTests
{
    [Fact]
    public void Decide_Root_IsAuthorised()
    {
        Assert.Equal(CallerAuthorisation.Root, AuthorisationRule.Decide(0, new FakeGroups()));
    }

    [Fact]
    public void Decide_AnAdministrator_IsAuthorised()
    {
        FakeGroups groups = new() { Memberships = { [501] = [HelperInstallation.AdministratorsGroupId] } };

        Assert.Equal(CallerAuthorisation.Administrator, AuthorisationRule.Decide(501, groups));
    }

    [Fact]
    public void Decide_AMemberOfTheHelpersOwnGroup_IsAuthorised()
    {
        FakeGroups groups = new()
        {
            ByName = { [HelperInstallation.AuthorisedGroup] = 700 },
            Memberships = { [501] = [700] },
        };

        Assert.Equal(CallerAuthorisation.Group, AuthorisationRule.Decide(501, groups));
    }

    [Fact]
    public void Decide_AnAccountInNeitherGroup_IsNotAuthorised()
    {
        FakeGroups groups = new() { ByName = { [HelperInstallation.AuthorisedGroup] = 700 } };

        Assert.Equal(CallerAuthorisation.None, AuthorisationRule.Decide(501, groups));
    }

    /// <summary>
    /// The group is not created by the installer unless it is needed, so it is usually absent.
    /// </summary>
    [Fact]
    public void Decide_WhenTheGroupDoesNotExist_IsNotAuthorised()
    {
        Assert.Equal(CallerAuthorisation.None, AuthorisationRule.Decide(501, new FakeGroups()));
    }

    /// <summary>
    /// The administrators group is asked for by number: a directory service group called admin must
    /// not be able to decide this.
    /// </summary>
    [Fact]
    public void Decide_AGroupNamedAdmin_DoesNotCount()
    {
        FakeGroups groups = new()
        {
            ByName = { ["admin"] = 900 },
            Memberships = { [501] = [900] },
        };

        Assert.Equal(CallerAuthorisation.None, AuthorisationRule.Decide(501, groups));
    }

    private sealed class FakeGroups : IGroupMembership
    {
        public Dictionary<string, uint> ByName { get; } = [];

        public Dictionary<uint, uint[]> Memberships { get; } = [];

        public bool IsMember(uint userId, uint groupId) =>
            Memberships.TryGetValue(userId, out uint[]? groups) && groups.Contains(groupId);

        public uint? ResolveGroup(string name) =>
            ByName.TryGetValue(name, out uint group) ? group : null;
    }
}
