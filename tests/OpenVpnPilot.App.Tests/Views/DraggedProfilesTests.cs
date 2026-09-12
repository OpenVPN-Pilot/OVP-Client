using Avalonia.Input;
using OpenVpnPilot.App.Views;

namespace OpenVpnPilot.App.Tests.Views;

/// <summary>
/// What a drag of profiles carries has to survive the platform, because on macOS a drag that
/// serializes to nothing ends the process rather than doing nothing.
/// </summary>
public sealed class DraggedProfilesTests
{
    /// <summary>
    /// The format is built in a static constructor, so an identifier Avalonia refuses is a window
    /// that cannot be created and an application that does not start. The first attempt used a
    /// slash, which is not one of the characters allowed, and did exactly that.
    /// </summary>
    [Fact]
    public void TheFormatIdentifierIsOneAvaloniaAccepts()
    {
        Assert.NotNull(DataFormat.CreateStringApplicationFormat(DraggedProfiles.FormatIdentifier));
    }

    [Fact]
    public void RoundTripsTheIdentifiers()
    {
        List<Guid> profileIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        Assert.Equal(profileIds, DraggedProfiles.Parse(DraggedProfiles.Format(profileIds)));
    }

    [Fact]
    public void WritesSomethingForASingleProfile()
    {
        Guid profileId = Guid.NewGuid();

        string written = DraggedProfiles.Format([profileId]);

        Assert.NotEmpty(written);
        Assert.Equal([profileId], DraggedProfiles.Parse(written));
    }

    [Fact]
    public void WritesNothingForNoProfiles()
    {
        Assert.Equal(string.Empty, DraggedProfiles.Format([]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an identifier")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    public void ReadsNothingOutOfWhatIsNotIdentifiers(string? dragged)
    {
        Assert.Empty(DraggedProfiles.Parse(dragged));
    }

    /// <summary>
    /// A drag can arrive from anywhere the platform lets it, so one bad entry loses that entry
    /// rather than the drop.
    /// </summary>
    [Fact]
    public void KeepsTheIdentifiersAmongTheRubbish()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        List<Guid> read = DraggedProfiles.Parse(
            $"{first:N} nonsense {second:N} 12345");

        Assert.Equal([first, second], read);
    }
}
