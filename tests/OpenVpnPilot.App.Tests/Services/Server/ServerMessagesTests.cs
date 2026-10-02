using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// What the server writes as a role or a way of signing in is shown in words, not as it was sent.
/// </summary>
public sealed class ServerMessagesTests
{
    [Theory]
    [InlineData(ServerRoles.Admin, "signIn.roleAdmin")]
    [InlineData(ServerRoles.User, "signIn.roleUser")]
    [InlineData("auditor", "auditor")]
    public void Role_KnownRoles_AreTranslatedAndOthersShownAsTheyAre(string role, string expected) =>
        Assert.Equal(expected, ServerMessages.Role(new StubLocalizer(), role));

    [Theory]
    [InlineData(ServerAuthModes.None, "signIn.modeNone")]
    [InlineData(ServerAuthModes.File, "signIn.modeFile")]
    [InlineData(ServerAuthModes.Ldap, "signIn.modeLdap")]
    [InlineData(ServerAuthModes.Entra, "signIn.modeEntra")]
    [InlineData("saml", "saml")]
    public void Provider_KnownProviders_AreTranslatedAndOthersShownAsTheyAre(string provider, string expected) =>
        Assert.Equal(expected, ServerMessages.Provider(new StubLocalizer(), provider));
}
