using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// Makes a copy of an icon in another colour.
/// </summary>
/// <remarks>
/// The copy is made from the icon the shell already chose for the notification area, at the size it
/// chose, so the recoloured mark has the same pixels as the original and nothing is drawn twice.
/// Its colour bitmap carries the shape in its alpha channel, which is what <see cref="TrayIconTint"/>
/// keeps; the mask is reused as it is.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class IconTinting
{
    /// <summary>
    /// Creates the recoloured icon, which the caller destroys.
    /// </summary>
    /// <returns>Zero when the icon has no colour bitmap with alpha to recolour.</returns>
    public static nint Create(nint source, TrayIconColour colour)
    {
        if (!NativeMethods.GetIconInfo(source, out NativeMethods.IconInfo info))
        {
            return nint.Zero;
        }

        nint deviceContext = nint.Zero;
        nint tinted = nint.Zero;

        try
        {
            // An icon without a colour bitmap is monochrome and has no alpha to keep.
            if (info.hbmColor == nint.Zero
                || NativeMethods.GetObject(info.hbmColor, Marshal.SizeOf<NativeMethods.BitmapObject>(), out NativeMethods.BitmapObject bitmap) == 0
                || bitmap.bmWidth <= 0
                || bitmap.bmHeight <= 0)
            {
                return nint.Zero;
            }

            // A negative height asks for the rows top down, which is the order the pixels are used in.
            NativeMethods.BitmapInfo format = new()
            {
                biSize = 40,
                biWidth = bitmap.bmWidth,
                biHeight = -bitmap.bmHeight,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BiRgb,
            };

            byte[] pixels = new byte[bitmap.bmWidth * bitmap.bmHeight * 4];
            deviceContext = NativeMethods.GetDC(nint.Zero);

            if (deviceContext == nint.Zero
                || NativeMethods.GetDIBits(deviceContext, info.hbmColor, 0, (uint)bitmap.bmHeight, pixels, ref format, NativeMethods.DibRgbColors) != bitmap.bmHeight)
            {
                return nint.Zero;
            }

            // An icon bitmap keeps its colour straight, not multiplied by alpha.
            if (!TrayIconTint.Apply(pixels, colour, premultiplied: false))
            {
                return nint.Zero;
            }

            tinted = NativeMethods.CreateDIBSection(deviceContext, ref format, NativeMethods.DibRgbColors, out nint bits, nint.Zero, 0);

            if (tinted == nint.Zero || bits == nint.Zero)
            {
                return nint.Zero;
            }

            Marshal.Copy(pixels, 0, bits, pixels.Length);

            NativeMethods.IconInfo copy = new()
            {
                fIcon = 1,
                hbmMask = info.hbmMask,
                hbmColor = tinted,
            };

            // The icon takes copies of both bitmaps, so they are released below either way.
            return NativeMethods.CreateIconIndirect(ref copy);
        }
        finally
        {
            if (deviceContext != nint.Zero)
            {
                // Nothing to act on: a screen context that will not go back leaks one handle, and the
                // icon has been made either way.
                _ = NativeMethods.ReleaseDC(nint.Zero, deviceContext);
            }

            if (tinted != nint.Zero)
            {
                NativeMethods.DeleteObject(tinted);
            }

            // GetIconInfo hands out its own bitmaps, which belong to the caller.
            if (info.hbmColor != nint.Zero)
            {
                NativeMethods.DeleteObject(info.hbmColor);
            }

            if (info.hbmMask != nint.Zero)
            {
                NativeMethods.DeleteObject(info.hbmMask);
            }
        }
    }
}
