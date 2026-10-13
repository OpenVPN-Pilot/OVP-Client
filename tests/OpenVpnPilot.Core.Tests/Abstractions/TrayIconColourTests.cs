using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Core.Tests.Abstractions;

/// <summary>
/// The colour as the settings file writes it, and the recolouring both platforms draw with.
/// </summary>
public sealed class TrayIconColourTests
{
    [Theory]
    [InlineData("#22C55E", 0x22, 0xC5, 0x5E)]
    [InlineData("22c55e", 0x22, 0xC5, 0x5E)]
    [InlineData("  #000000 ", 0, 0, 0)]
    public void TryParse_ReadsSixHexDigits(string text, byte red, byte green, byte blue)
    {
        Assert.True(TrayIconColour.TryParse(text, out TrayIconColour colour));
        Assert.Equal(new TrayIconColour(red, green, blue), colour);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GG0000")]
    [InlineData("#22C55E80")]
    [InlineData("green")]
    public void TryParse_RefusesAnythingElse(string? text) =>
        Assert.False(TrayIconColour.TryParse(text, out _));

    [Fact]
    public void ToString_IsWhatTryParseReads()
    {
        TrayIconColour colour = new(0x06, 0xB6, 0xD4);

        Assert.Equal("#06B6D4", colour.ToString());
        Assert.True(TrayIconColour.TryParse(colour.ToString(), out TrayIconColour read));
        Assert.Equal(colour, read);
    }

    [Fact]
    public void Apply_KeepsTheShapeAndReplacesTheColour()
    {
        // Blue, green, red, alpha: an opaque blue pixel, a half transparent one and an empty one.
        byte[] pixels = [246, 130, 59, 255, 246, 130, 59, 128, 0, 0, 0, 0];

        bool shaped = TrayIconTint.Apply(pixels, new TrayIconColour(0x22, 0xC5, 0x5E), premultiplied: false);

        Assert.True(shaped);
        Assert.Equal<byte>([0x5E, 0xC5, 0x22, 255, 0x5E, 0xC5, 0x22, 128, 0x5E, 0xC5, 0x22, 0], pixels);
    }

    [Fact]
    public void Apply_MultipliesByAlphaForAPremultipliedSurface()
    {
        byte[] pixels = [0, 0, 0, 255, 0, 0, 0, 128, 0, 0, 0, 0];

        TrayIconTint.Apply(pixels, new TrayIconColour(255, 0, 200), premultiplied: true);

        Assert.Equal<byte>([200, 0, 255, 255, 100, 0, 128, 128, 0, 0, 0, 0], pixels);
    }

    [Fact]
    public void Apply_ReportsAnImageWithoutAlpha()
    {
        // Its shape is in a mask this cannot see, so recolouring it would paint a solid square.
        byte[] pixels = [246, 130, 59, 0, 0, 0, 0, 0];

        Assert.False(TrayIconTint.Apply(pixels, new TrayIconColour(1, 2, 3), premultiplied: false));
    }

    [Fact]
    public void Apply_RefusesAnImageThatIsNotFourBytesAPixel() =>
        Assert.Throws<ArgumentException>(() => TrayIconTint.Apply(new byte[6], default, premultiplied: false));

    [Fact]
    public void ABadge_IsADotInTheMiddle_WithClearCorners()
    {
        const int Pixels = 20;
        byte[] image = TrayBadge.Render(Pixels, new TrayIconColour(0x22, 0xC5, 0x5E), premultiplied: false);

        int centre = (((Pixels / 2) * Pixels) + (Pixels / 2)) * 4;

        Assert.Equal(Pixels * Pixels * 4, image.Length);
        Assert.Equal<byte>([0x5E, 0xC5, 0x22, 255], image[centre..(centre + 4)]);
        Assert.Equal(0, image[3]);
        Assert.Equal(0, image[^1]);
    }

    [Fact]
    public void ABadge_IsPremultipliedWhenAsked()
    {
        byte[] image = TrayBadge.Render(16, new TrayIconColour(255, 255, 255), premultiplied: true);

        for (int index = 0; index < image.Length; index += 4)
        {
            Assert.True(image[index] <= image[index + 3]);
        }
    }
}
