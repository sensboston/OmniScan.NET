using System;
using Leptonica;
using OmniScan.Net.Core;

namespace OmniScan.Net.Imaging;

/// <summary>Decoding device frames, fixed rotation and encoding to the output formats.</summary>
public static class FrameCodec
{
    public static Pix Decode(ScannedFrame frame)
    {
        Pix pix = frame.MediaType switch
        {
            "image/bmp" => Pix.ReadMemBmp(frame.Data),
            "image/jpeg" => Pix.ReadMemJpeg(frame.Data),
            "image/png" => Pix.ReadMemPng(frame.Data),
            _ => throw new NotSupportedException($"Unsupported frame type {frame.MediaType}."),
        };
        if (frame.Dpi > 0)
            pix.SetResolution(frame.Dpi, frame.Dpi);
        return pix;
    }

    /// <summary>Rotates clockwise by 0, 90, 180 or 270 degrees; returns the same object for 0.</summary>
    public static Pix Rotate(Pix pix, int degrees) => degrees switch
    {
        0 => pix,
        90 => pix.Rotate90(1),
        180 => Pix.Rotate180(null, pix),
        270 => pix.Rotate90(-1),
        _ => throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Only multiples of 90 degrees."),
    };

    public static byte[] Encode(Pix pix, OutputFormat format, int jpegQuality, string? title = null)
    {
        int dpi = pix.XRes > 0 ? pix.XRes : 300;
        switch (format)
        {
            case OutputFormat.Png:
                return Pix.WriteMemPng(pix, 0);

            case OutputFormat.Jpeg:
                // JPEG has no 1 bpp: black & white pages are stored as 8 bpp gray.
                Pix jpegSource = pix.Depth is 8 or 32 ? pix : pix.ConvertTo8(false);
                return Pix.WriteMemJpeg(jpegSource, jpegQuality, 0);

            case OutputFormat.Pdf:
                return Pdf(pix, dpi, jpegQuality, title);

            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>A one-page PDF: G4 for black &amp; white, JPEG of the given quality for gray and color.</summary>
    public static byte[] Pdf(Pix pix, int dpi, int jpegQuality, string? title = null)
    {
        Pixa one = Pixa.Create(1);
        one.AddPix(pix, L.L_CLONE);
        return one.ConvertToPdfData(dpi, 1.0f, L.L_DEFAULT_ENCODE, jpegQuality, title);
    }

    /// <summary>A small BMP for the preview pane (BMP is the cheapest format for WPF to load).</summary>
    public static byte[] PreviewBmp(Pix pix, int maxSide)
    {
        int w = pix.Width;
        int h = pix.Height;
        Pix scaled = pix;
        if (Math.Max(w, h) > maxSide)
        {
            double k = (double)maxSide / Math.Max(w, h);
            scaled = pix.ScaleToSize(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)));
        }

        return Pix.WriteMemBmp(scaled);
    }
}
