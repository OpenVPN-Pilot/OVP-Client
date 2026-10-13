using System.Globalization;

namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// A colour the notification area icon is drawn in, opaque and without a colour space.
/// </summary>
/// <remarks>
/// Kept apart from any user interface framework's colour type, because the platforms that draw the
/// icon do not share one and the settings file stores it as text.
/// </remarks>
public readonly record struct TrayIconColour(byte Red, byte Green, byte Blue)
{
    /// <summary>
    /// Reads <c>#RRGGBB</c>, the form the settings file uses. The leading hash is optional.
    /// </summary>
    public static bool TryParse(string? text, out TrayIconColour colour)
    {
        colour = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> digits = text.AsSpan().Trim();

        if (digits.StartsWith("#", StringComparison.Ordinal))
        {
            digits = digits[1..];
        }

        if (digits.Length != 6
            || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            return false;
        }

        colour = new TrayIconColour((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"#{Red:X2}{Green:X2}{Blue:X2}");
}

/// <summary>
/// Recolours the mark while keeping its shape.
/// </summary>
/// <remarks>
/// The mark is one colour on transparency, on Windows blue and in the menu bar black, so its shape
/// lives entirely in the alpha channel. Replacing the colour channels and keeping alpha therefore
/// gives the same mark, with the same edges, in another colour. Recolouring what the platform already
/// draws keeps <c>assets/artwork</c> the only place the mark is described.
/// </remarks>
public static class TrayIconTint
{
    /// <summary>
    /// Replaces the colour of every pixel of a 32 bit image laid out blue, green, red, alpha.
    /// </summary>
    /// <param name="pixels">The image, four bytes a pixel, changed in place.</param>
    /// <param name="colour">The colour to draw in.</param>
    /// <param name="premultiplied">
    /// True when the colour channels are stored already multiplied by alpha, as a framework surface
    /// keeps them. An icon bitmap on Windows keeps them straight.
    /// </param>
    /// <returns>
    /// False when no pixel carries any alpha at all. Such an image has its shape in a separate mask
    /// that this cannot see, and recolouring it would paint a solid square.
    /// </returns>
    public static bool Apply(Span<byte> pixels, TrayIconColour colour, bool premultiplied)
    {
        if (pixels.Length % 4 != 0)
        {
            throw new ArgumentException("The image must have four bytes a pixel.", nameof(pixels));
        }

        bool anyAlpha = false;

        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte alpha = pixels[index + 3];

            if (alpha != 0)
            {
                anyAlpha = true;
            }

            if (premultiplied)
            {
                pixels[index] = Multiply(colour.Blue, alpha);
                pixels[index + 1] = Multiply(colour.Green, alpha);
                pixels[index + 2] = Multiply(colour.Red, alpha);
            }
            else
            {
                pixels[index] = colour.Blue;
                pixels[index + 1] = colour.Green;
                pixels[index + 2] = colour.Red;
            }
        }

        return anyAlpha;
    }

    private static byte Multiply(byte channel, byte alpha) => (byte)(((channel * alpha) + 127) / 255);
}

/// <summary>
/// Draws the dot a menu entry carries beside its label.
/// </summary>
/// <remarks>
/// Drawn here rather than by either platform, because a filled circle is the same arithmetic on both
/// and neither menu has a way to colour text. The edge is smoothed by sampling each pixel sixteen
/// times, which is plenty at the sixteen to thirty-two pixels a menu asks for.
/// </remarks>
public static class TrayBadge
{
    private const int Samples = 4;

    /// <summary>
    /// A square image of the given size, four bytes a pixel laid out blue, green, red, alpha.
    /// </summary>
    /// <param name="premultiplied">
    /// True to store the colour multiplied by alpha, which is what a Windows menu bitmap expects.
    /// </param>
    public static byte[] Render(int pixels, TrayIconColour colour, bool premultiplied)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels, 1);

        byte[] image = new byte[pixels * pixels * 4];

        // A dot that fills the square reads as a block next to text; a margin of a fifth matches the
        // size of the glyphs beside it.
        double radius = pixels * 0.3;
        double centre = pixels / 2.0;

        for (int y = 0; y < pixels; y++)
        {
            for (int x = 0; x < pixels; x++)
            {
                int inside = 0;

                for (int sy = 0; sy < Samples; sy++)
                {
                    for (int sx = 0; sx < Samples; sx++)
                    {
                        double dx = x + ((sx + 0.5) / Samples) - centre;
                        double dy = y + ((sy + 0.5) / Samples) - centre;

                        if ((dx * dx) + (dy * dy) <= radius * radius)
                        {
                            inside++;
                        }
                    }
                }

                int index = ((y * pixels) + x) * 4;
                image[index + 3] = (byte)((inside * 255) / (Samples * Samples));
            }
        }

        TrayIconTint.Apply(image, colour, premultiplied);
        return image;
    }
}
