using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>Reads a QR code from an image file (a screenshot or a photo of the code).</summary>
public static class QrImage
{
    /// <summary>Photos are scaled down to this many pixels on the long side: plenty for a code, and quick to scan.</summary>
    private const int MaxSide = 1600;

    /// <summary>The code's text, or null when the image has none (or isn't an image).</summary>
    public static string? Decode(byte[] image)
    {
        Bitmap bitmap;
        try { bitmap = new Bitmap(new MemoryStream(image)); }
        catch (Exception) { return null; }

        using (bitmap)
        {
            var size = bitmap.PixelSize;
            var scale = Math.Min(1.0, (double)MaxSide / Math.Max(size.Width, size.Height));
            var target = new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
            using var scaled = scale < 1 ? bitmap.CreateScaledBitmap(target, BitmapInterpolationMode.MediumQuality) : null;
            var source = scaled ?? bitmap;

            // Converted to a known layout whatever the file's own.
            using var buffer = new WriteableBitmap(target, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var locked = buffer.Lock();
            source.CopyPixels(locked);
            var stride = locked.RowBytes;
            var pixels = new byte[target.Width * 4 * target.Height];
            for (var y = 0; y < target.Height; y++)
                Marshal.Copy(locked.Address + y * stride, pixels, y * target.Width * 4, target.Width * 4);
            return QrCodes.Decode(QrCodes.Luminance(pixels, target.Width, target.Height, bgra: true), target.Width, target.Height);
        }
    }
}
