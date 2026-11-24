using System;
using Leptonica;

namespace OmniScan.Net.Imaging;

/// <summary>Fast reads of Leptonica pixel words (32 bpp: R G B x from the high byte; 8 bpp: big-endian bytes in a word).</summary>
internal static class PixelAccess
{
    /// <summary>Fills luminance and saturation (max - min of RGB) for an 8 or 32 bpp image.</summary>
    public static void ReadGrayAndSaturation(Pix pix, Span<byte> gray, Span<byte> sat)
    {
        int w = pix.Width, h = pix.Height, wpl = pix.Wpl;
        uint[] data = pix.Data;
        if (pix.Depth == 32)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * wpl, o = y * w;
                for (int x = 0; x < w; x++)
                {
                    uint p = data[row + x];
                    int r = (int)(p >> 24), g = (int)(p >> 16) & 0xff, b = (int)(p >> 8) & 0xff;
                    gray[o + x] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                    sat[o + x] = (byte)(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)));
                }
            }
        }
        else if (pix.Depth == 8 && pix.Colormap is null)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * wpl, o = y * w;
                for (int x = 0; x < w; x++)
                {
                    gray[o + x] = (byte)(data[row + (x >> 2)] >> (24 - 8 * (x & 3)));
                    sat[o + x] = 0;
                }
            }
        }
        else
        {
            ReadGrayAndSaturation(pix.Depth == 8 ? pix.ConvertTo32() : pix.ConvertTo8(false), gray, sat);
        }
    }
}
