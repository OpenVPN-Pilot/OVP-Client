using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Core.Tests.Abstractions;

/// <summary>
/// The reference is the only link between a profile and its stored password.
/// </summary>
/// <remarks>
/// Reading one back matters for anything that walks the whole keystore rather than looking a single
/// entry up, such as collecting the sign ins that belong to an export. Getting it wrong there means
/// exporting somebody else's credential, so the shape is pinned down here.
/// </remarks>
public sealed class SecretReferenceTests
{
    private static readonly Guid ProfileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void AReferenceRoundTrips()
    {
        string reference = SecretReference.ForProfile(ProfileId, "Auth");

        Assert.True(SecretReference.TryParse(reference, out Guid profileId, out string realm));
        Assert.Equal(ProfileId, profileId);
        Assert.Equal("Auth", realm);
    }

    /// <summary>
    /// OpenVPN names a realm freely, and "Private Key" is one of the two the client actually sees.
    /// </summary>
    [Theory]
    [InlineData("Private Key")]
    [InlineData("Auth")]
    [InlineData("some/realm/with/slashes")]
    public void ARealmSurvivesWhateverItContains(string realm)
    {
        string reference = SecretReference.ForProfile(ProfileId, realm);

        Assert.True(SecretReference.TryParse(reference, out Guid profileId, out string parsed));
        Assert.Equal(ProfileId, profileId);
        Assert.Equal(realm, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("profile")]
    [InlineData("profile/not-a-guid/Auth")]
    [InlineData("something/11111111222233334444555555555555/Auth")]
    [InlineData("profile/11111111222233334444555555555555/")]
    public void AReferenceThisSchemeDidNotProduce_IsRefused(string reference)
    {
        Assert.False(SecretReference.TryParse(reference, out Guid profileId, out string realm));
        Assert.Equal(Guid.Empty, profileId);
        Assert.Equal(string.Empty, realm);
    }

    [Fact]
    public void BelongsToProfile_AgreesWithParsing()
    {
        string reference = SecretReference.ForProfile(ProfileId, "Auth");

        Assert.True(SecretReference.BelongsToProfile(reference, ProfileId));
        Assert.False(SecretReference.BelongsToProfile(reference, Guid.NewGuid()));
    }
}
