using System.Buffers.Binary;
using OpenVpnPilot.Artwork;
using SkiaSharp;

// Draws everything visual the product is made of, into assets/artwork.
//
// One mark, one place it comes from, four things that need it: the macOS application icon, the
// Windows application icon, the menu bar template on macOS, and the background of the disk image
// window. All of it is geometry rather than pictures, because an icon drawn at every size is sharp at
// every size and an icon scaled up from one export is sharp at one.
//
// Run it by hand when the artwork changes, and commit what it writes:
//
//   dotnet run --project tools/artwork
//
// It runs on Windows and on macOS, which is why it is written here and not against either system's
// own drawing. It is deliberately not in the solution: producing artwork is not something an ordinary
// build should do, and the files it writes are committed.

// Found by walking up to the solution rather than by counting directories, so it stays right
// whatever configuration this was built in.
string output = args.Length > 0 ? args[0] : Path.Combine(Repository(), "assets", "artwork");

output = Path.GetFullPath(output);

static string Repository()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);

    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenVpnPilot.sln")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName
        ?? throw new InvalidOperationException("The repository could not be found above this build.");
}
Directory.CreateDirectory(output);

void Write(string name, byte[] content)
{
    File.WriteAllBytes(Path.Combine(output, name), content);
    Console.WriteLine($"wrote {Path.GetFileName(output)}/{name}");
}

Write("OpenVpnPilot.icns", AppleIcon.Build(Mark.DrawApplicationIcon));
Write("OpenVpnPilot.ico", WindowsIcon.Build(Mark.DrawApplicationIcon));
Write("status-item.png", Mark.MenuBarTemplate());
Write("dmg-background.png", DiskImageBackground.Render());

namespace OpenVpnPilot.Artwork
{
    /// <summary>
    /// The mark: a ring with a dot at its centre, and the tile an application icon sits on.
    /// </summary>
    /// <remarks>
    /// The radii are fractions of the edge, so the same numbers describe the mark at 16 points and at
    /// 1024. They are the measurements of the icon this was first drawn from, to the pixel.
    /// </remarks>
    internal static class Mark
    {
        /// <summary>The blue the interface uses for an active tunnel.</summary>
        private static readonly SKColor Blue = new(59, 130, 246);

        private const double OuterRadius = 0.4609375;
        private const double RingInnerRadius = 0.30078125;
        private const double DotRadius = 0.1640625;

        /// <summary>
        /// The menu bar wants more air around it than an application icon does, and it is the one
        /// place the mark is drawn at a fixed small size, so it carries its own proportions.
        /// </summary>
        private const double TemplateOuterRadius = 0.4166666666666667;
        private const double TemplateRingInnerRadius = 0.2777777777777778;
        private const double TemplateDotRadius = 0.16666666666666666;

        /// <summary>
        /// Apple's grid: the body of an icon fills 824 of 1024 with a corner radius of 185.4. The
        /// same shape is used for Windows, so the product looks like one product on both.
        /// </summary>
        private const double BodyFraction = 824.0 / 1024.0;

        private const double CornerFraction = 185.4 / 824.0;

        /// <summary>
        /// How much of the body the mark takes up inside it. Smaller than the body, because a mark
        /// that reaches the rounded corners reads as a sticker rather than as an icon.
        /// </summary>
        private const double MarkInTileFraction = 0.78;

        /// <summary>
        /// The size below which the tile is dropped and the mark is drawn on its own.
        /// </summary>
        /// <remarks>
        /// At 16 points a tile and a mark inside it leave the mark about ten points across, and the
        /// dot in its middle two. What survives at that size is the mark filling the square, which is
        /// what the shell shows in a list, a menu and a title bar.
        /// </remarks>
        private const double SmallestTile = 32.0;

        /// <summary>
        /// Draws the application icon at the given pixel size.
        /// </summary>
        /// <param name="shownAt">
        /// The size the icon is shown at, which decides whether there is room for a tile. It is not
        /// the pixel size on a display that doubles: a 16 point icon is 32 pixels and is still a 16
        /// point icon.
        /// </param>
        public static void DrawApplicationIcon(SKCanvas canvas, int pixels, double shownAt)
        {
            if (shownAt < SmallestTile)
            {
                FillMark(canvas, pixels, Blue, 1.0, OuterRadius, RingInnerRadius, DotRadius);
                return;
            }

            float body = (float)(pixels * BodyFraction);
            float inset = (pixels - body) / 2f;
            float corner = (float)(body * CornerFraction);
            SKRoundRect tile = new(SKRect.Create(inset, inset, body, body), corner, corner);

            // The shadow is what separates a light tile from a light background. It is drawn under an
            // opaque fill first, so the gradient that follows is not drawn through it.
            using (SKPaint shadow = new())
            {
                shadow.IsAntialias = true;
                shadow.Color = SKColors.White;
                shadow.ImageFilter = SKImageFilter.CreateDropShadowOnly(
                    dx: 0,
                    dy: (float)(pixels * 0.008),
                    sigmaX: (float)(pixels * 0.011),
                    sigmaY: (float)(pixels * 0.011),
                    color: new SKColor(0, 0, 0, 56));

                canvas.DrawRoundRect(tile, shadow);
            }

            using (SKPaint face = new())
            {
                face.IsAntialias = true;
                face.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, inset),
                    new SKPoint(0, inset + body),
                    [new SKColor(245, 245, 245), new SKColor(219, 219, 219)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp);

                canvas.DrawRoundRect(tile, face);
            }

            FillMark(canvas, pixels, Blue, BodyFraction * MarkInTileFraction, OuterRadius, RingInnerRadius, DotRadius);
        }

        /// <summary>
        /// The menu bar entry: black shapes on transparency, which the menu bar tints itself. Eighteen
        /// points at twice the resolution, which is what the menu bar gives an item.
        /// </summary>
        public static byte[] MenuBarTemplate() => Canvas.Render(36, canvas =>
            FillMark(canvas, 36, SKColors.Black, 1.0, TemplateOuterRadius, TemplateRingInnerRadius, TemplateDotRadius));

        private static void FillMark(
            SKCanvas canvas,
            int pixels,
            SKColor colour,
            double scale,
            double outer,
            double inner,
            double dot)
        {
            float centre = pixels / 2f;

            SKRect Circle(double radius)
            {
                float r = (float)(pixels * radius * scale);
                return new SKRect(centre - r, centre - r, centre + r, centre + r);
            }

            using SKPaint paint = new() { IsAntialias = true, Color = colour };

            // The ring is the outer circle and the hole in one path under the even-odd rule, so the
            // middle stays clear. Stroking a circle instead would put half the line outside the
            // measured radius.
            using SKPath ring = new() { FillType = SKPathFillType.EvenOdd };
            ring.AddOval(Circle(outer));
            ring.AddOval(Circle(inner));
            canvas.DrawPath(ring, paint);

            // The dot separately, because adding it to the same path would cancel the hole it sits in.
            canvas.DrawOval(Circle(dot), paint);
        }
    }

    /// <summary>
    /// Renders a square of the given pixel size and returns it as a PNG.
    /// </summary>
    internal static class Canvas
    {
        public static byte[] Render(int pixels, Action<SKCanvas> draw) =>
            Render(pixels, pixels, draw);

        public static byte[] Render(int width, int height, Action<SKCanvas> draw)
        {
            using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            surface.Canvas.Clear(SKColors.Transparent);
            draw(surface.Canvas);
            surface.Canvas.Flush();

            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }

    /// <summary>
    /// Writes an <c>icns</c>, which is a magic word, a length, and one entry per representation.
    /// </summary>
    /// <remarks>
    /// Written here rather than handed to <c>iconutil</c>, which exists only on macOS and would make
    /// the Windows icon unbuildable from Windows. Each entry is a four character type, the length
    /// including its own header, and the data, which since macOS 10.7 may be a PNG for every type
    /// used here.
    ///
    /// A type says both how many pixels an entry has and what size it stands for, and the two are not
    /// the same thing: ic11 is sixteen points on a display that doubles and ic12 is thirty-two, so
    /// the first is drawn without a tile and the second with one.
    ///
    /// The one point entries for sixteen and thirty-two, icp4 and icp5, are left out. They predate
    /// PNG in this format and are read as raw pixels by tools that expect the older meaning, iconutil
    /// among them, which turns them into noise. The system scales the two point entries down for a
    /// display that does not double, and that is the same drawing.
    /// </remarks>
    internal static class AppleIcon
    {
        private static readonly (string Type, int Pixels, double ShownAt)[] Representations =
        [
            ("ic11", 32, 16),
            ("ic12", 64, 32),
            ("ic07", 128, 128),
            ("ic13", 256, 128),
            ("ic08", 256, 256),
            ("ic14", 512, 256),
            ("ic09", 512, 512),
            ("ic10", 1024, 512),
        ];

        public static byte[] Build(Action<SKCanvas, int, double> draw)
        {
            ArgumentNullException.ThrowIfNull(draw);

            using MemoryStream body = new();

            foreach ((string type, int pixels, double shownAt) in Representations)
            {
                byte[] png = Canvas.Render(pixels, canvas => draw(canvas, pixels, shownAt));

                body.Write(System.Text.Encoding.ASCII.GetBytes(type));
                body.Write(BigEndian(png.Length + 8));
                body.Write(png);
            }

            using MemoryStream file = new();
            file.Write(System.Text.Encoding.ASCII.GetBytes("icns"));
            file.Write(BigEndian((int)body.Length + 8));
            body.Position = 0;
            body.CopyTo(file);

            return file.ToArray();
        }

        private static byte[] BigEndian(int value)
        {
            byte[] buffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            return buffer;
        }
    }

    /// <summary>
    /// Writes an <c>ico</c>, which is a small header, one directory entry per size, and the images one
    /// after another.
    /// </summary>
    /// <remarks>
    /// Since Windows Vista an entry may be a PNG rather than a bitmap, which is what every size here
    /// is: it keeps the alpha channel and the file small enough to embed in the executable. Nothing
    /// else in the toolchain draws one, which is why this is here.
    /// </remarks>
    internal static class WindowsIcon
    {
        private static readonly int[] Sizes = [16, 20, 24, 32, 48, 64, 128, 256];

        public static byte[] Build(Action<SKCanvas, int, double> draw)
        {
            ArgumentNullException.ThrowIfNull(draw);

            List<byte[]> images = Sizes
                .Select(size => Canvas.Render(size, canvas => draw(canvas, size, size)))
                .ToList();

            using MemoryStream file = new();
            using BinaryWriter writer = new(file);

            writer.Write((ushort)0);              // reserved
            writer.Write((ushort)1);              // an icon rather than a cursor
            writer.Write((ushort)Sizes.Length);

            int offset = 6 + (Sizes.Length * 16);

            for (int index = 0; index < Sizes.Length; index++)
            {
                // 256 is written as zero, which is the only way the one byte each dimension gets can
                // say it.
                writer.Write((byte)(Sizes[index] == 256 ? 0 : Sizes[index]));
                writer.Write((byte)(Sizes[index] == 256 ? 0 : Sizes[index]));
                writer.Write((byte)0);            // colours in the palette: none, this is true colour
                writer.Write((byte)0);            // reserved
                writer.Write((ushort)1);          // planes
                writer.Write((ushort)32);         // bits per pixel
                writer.Write(images[index].Length);
                writer.Write(offset);

                offset += images[index].Length;
            }

            foreach (byte[] image in images)
            {
                writer.Write(image);
            }

            writer.Flush();
            return file.ToArray();
        }
    }

    /// <summary>
    /// The background of the window the disk image opens.
    /// </summary>
    /// <remarks>
    /// The size and the two icon positions are the ones <c>installer/build-macos.sh</c> places the
    /// icons at, so the arrow drawn between them lands between them. Change one and the other has to
    /// change with it.
    ///
    /// One file at twice the resolution. The Finder reads the resolution a PNG records and lays the
    /// window out in points, so the background is sharp on a display that doubles and correct on one
    /// that does not, without the two versions a pair of files would have to be kept in step.
    /// </remarks>
    internal static class DiskImageBackground
    {
        private const int WindowWidth = 640;
        private const int WindowHeight = 400;
        private const float ApplicationSlotX = 170;
        private const float ApplicationsSlotX = 470;
        private const float SlotY = 205;

        /// <summary>
        /// The typefaces to look for, in order. The first two are what macOS has and the third what
        /// Windows has, so a background drawn on either is drawn in that system's interface face
        /// rather than in whatever the renderer falls back to.
        /// </summary>
        private static readonly string[] Faces = ["SF Pro Text", "Helvetica Neue", "Segoe UI", "Arial"];

        public static byte[] Render()
        {
            byte[] png = Canvas.Render(WindowWidth * 2, WindowHeight * 2, canvas =>
            {
                canvas.Scale(2);
                Draw(canvas);
            });

            return WithResolution(png, dotsPerMetre: 5669);
        }

        private static void Draw(SKCanvas canvas)
        {
            // The Finder does not redraw a disk image background for dark mode, so this is light and
            // says so rather than being a grey that looks broken under one of the two appearances.
            using (SKPaint ground = new())
            {
                ground.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(0, WindowHeight),
                    [new SKColor(249, 250, 252), new SKColor(234, 237, 242)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp);

                canvas.DrawRect(SKRect.Create(0, 0, WindowWidth, WindowHeight), ground);
            }

            SKColor ink = new(33, 38, 46);
            SKColor faded = new(97, 107, 120);

            Centre(canvas, "OpenVPN Pilot", 22, SKFontStyleWeight.Bold, ink, 44);
            Centre(canvas, "Drag the application onto the Applications folder.", 13, SKFontStyleWeight.Normal, faded, 76);

            // The helper is a second, separate installation and this window is the only place a person
            // is certain to look, so it says so rather than leaving it to the release notes.
            Centre(canvas, "Then install the helper package, which is what starts OpenVPN.", 11, SKFontStyleWeight.Normal, faded, 352);

            // The arrow sits between the two icon slots, clear of both by the width of half an icon.
            float from = ApplicationSlotX + 92;
            float to = ApplicationsSlotX - 92;

            using SKPaint stroke = new()
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                Color = faded.WithAlpha(140),
            };

            canvas.DrawLine(from, SlotY, to - 9, SlotY, stroke);

            using SKPath head = new();
            head.MoveTo(to - 18, SlotY - 8);
            head.LineTo(to, SlotY);
            head.LineTo(to - 18, SlotY + 8);
            canvas.DrawPath(head, stroke);
        }

        private static void Centre(
            SKCanvas canvas,
            string text,
            float size,
            SKFontStyleWeight weight,
            SKColor colour,
            float baselineFromTop)
        {
            using SKFontStyle style = new(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

            using SKTypeface typeface = Faces
                .Select(face => SKTypeface.FromFamilyName(face, style))
                .FirstOrDefault(found => found is not null)
                ?? SKTypeface.CreateDefault();

            using SKFont font = new(typeface, size);
            using SKPaint paint = new() { IsAntialias = true, Color = colour };

            canvas.DrawText(text, WindowWidth / 2f, baselineFromTop, SKTextAlign.Center, font, paint);
        }

        /// <summary>
        /// Stamps a physical resolution into a PNG, which is what tells the Finder the image is half
        /// its pixel size in points.
        /// </summary>
        /// <remarks>
        /// Skia writes no pHYs chunk, so one is inserted before the image data. 5669 dots per metre is
        /// 144 dots per inch, which is twice the 72 a point is, which is what makes a 1280 by 800
        /// image describe a 640 by 400 window.
        /// </remarks>
        private static byte[] WithResolution(byte[] png, int dotsPerMetre)
        {
            byte[] chunk = new byte[21];
            BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(0, 4), 9);
            "pHYs"u8.CopyTo(chunk.AsSpan(4, 4));
            BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(8, 4), dotsPerMetre);
            BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(12, 4), dotsPerMetre);
            chunk[16] = 1; // the unit is the metre
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17, 4), Crc32(chunk.AsSpan(4, 13)));

            // After the header chunk, which is always the first and always 25 bytes with the signature.
            const int afterHeader = 8 + 25;

            using MemoryStream result = new();
            result.Write(png.AsSpan(0, afterHeader));
            result.Write(chunk);
            result.Write(png.AsSpan(afterHeader));
            return result.ToArray();
        }

        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte value in data)
            {
                crc ^= value;

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFF;
        }
    }
}
