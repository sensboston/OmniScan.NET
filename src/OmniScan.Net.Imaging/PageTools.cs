using System;
using Leptonica;

namespace OmniScan.Net.Imaging;

/// <summary>Cutting rotated objects out of a page, deskew and text orientation.</summary>
public static class PageTools
{
    /// <summary>Cuts a rotated rectangle out of the page and turns it straight.</summary>
    public static Pix Extract(Pix page, RotatedRect r, bool straighten = true)
    {
        if (!straighten || Math.Abs(r.Angle) < 0.05)
        {
            (double X, double Y)[] corners = r.Corners();
            double minX = Math.Max(0, Min(corners, c => c.X)), minY = Math.Max(0, Min(corners, c => c.Y));
            double maxX = Math.Min(page.Width, Max(corners, c => c.X)), maxY = Math.Min(page.Height, Max(corners, c => c.Y));
            return Clip(page, (int)Math.Round(minX), (int)Math.Round(minY),
                (int)Math.Round(maxX - minX), (int)Math.Round(maxY - minY));
        }

        // Clip a square around the object first, so the rotation works on as few pixels as possible.
        double half = Math.Sqrt(r.Width * r.Width + r.Height * r.Height) / 2 + 4;
        int x0 = (int)Math.Max(0, r.Cx - half), y0 = (int)Math.Max(0, r.Cy - half);
        int x1 = (int)Math.Min(page.Width, r.Cx + half), y1 = (int)Math.Min(page.Height, r.Cy + half);
        Pix clip = Clip(page, x0, y0, x1 - x0, y1 - y0);

        double rad = r.Angle * Math.PI / 180;
        Pix rotated = Rotate(clip, -rad);

        // Rotation is about the clip center; find where the object center went.
        double dx = r.Cx - x0 - clip.Width / 2.0, dy = r.Cy - y0 - clip.Height / 2.0;
        double c = Math.Cos(-rad), s = Math.Sin(-rad);
        double nx = dx * c - dy * s + rotated.Width / 2.0;
        double ny = dx * s + dy * c + rotated.Height / 2.0;
        return Clip(rotated, (int)Math.Round(nx - r.Width / 2), (int)Math.Round(ny - r.Height / 2),
            (int)Math.Round(r.Width), (int)Math.Round(r.Height));
    }

    /// <summary>
    /// Cuts away a light fringe along the edges of a cut-out photo: the white border of a print or
    /// the lid showing through a slightly loose crop. A side is trimmed while its outermost row or
    /// column is mostly light and unsaturated, at most <paramref name="maxShare"/> of the size, so a
    /// bright sky reaching the edge is not eaten.
    /// </summary>
    public static Pix TrimLightBorder(Pix image, double maxShare = 0.03)
    {
        if (image.Depth != 32 && image.Depth != 8)
            return image;

        int w = image.Width, h = image.Height;
        byte[] gray = new byte[w * h];
        byte[] sat = new byte[w * h];
        PixelAccess.ReadGrayAndSaturation(image, gray, sat);

        bool Light(int x, int y) => gray[y * w + x] >= LightGray && sat[y * w + x] <= LightSaturation;

        double RowShare(int y, int x0, int x1)
        {
            int n = 0;
            for (int x = x0; x < x1; x++)
                n += Light(x, y) ? 1 : 0;
            return (double)n / Math.Max(1, x1 - x0);
        }

        double ColShare(int x, int y0, int y1)
        {
            int n = 0;
            for (int y = y0; y < y1; y++)
                n += Light(x, y) ? 1 : 0;
            return (double)n / Math.Max(1, y1 - y0);
        }

        int maxX = (int)(w * maxShare), maxY = (int)(h * maxShare);
        int top = 0, bottom = h, left = 0, right = w;
        while (top < maxY && RowShare(top, left, right) >= LightRowShare)
            top++;
        while (h - bottom < maxY && RowShare(bottom - 1, left, right) >= LightRowShare)
            bottom--;
        while (left < maxX && ColShare(left, top, bottom) >= LightRowShare)
            left++;
        while (w - right < maxX && ColShare(right - 1, top, bottom) >= LightRowShare)
            right--;

        if (top == 0 && left == 0 && bottom == h && right == w)
            return image;
        return Clip(image, left, top, right - left, bottom - top);
    }

    /// <summary>Paints a band of <paramref name="inches"/> along every edge white (the dark strips at the glass edges).</summary>
    public static Pix WhitenBorder(Pix page, double inches)
    {
        int dpi = page.XRes > 0 ? page.XRes : 300;
        int b = Math.Max(1, (int)(inches * dpi));
        // Palette images (8 bpp gray from some drivers) would get an index, not a color: go true color first.
        Pix copy = page.Colormap is null ? Pix.Copy(null, page) : page.ConvertTo32();
        int w = copy.Width, h = copy.Height, wpl = copy.Wpl;
        uint[] data = copy.Data;
        for (int y = 0; y < h; y++)
        {
            bool rowBand = y < b || y >= h - b;
            for (int x = 0; x < w; x++)
            {
                if (!rowBand && x >= b && x < w - b)
                {
                    x = w - b - 1;
                    continue;
                }

                switch (copy.Depth)
                {
                    case 32:
                        data[y * wpl + x] = 0xFFFFFF00u;
                        break;
                    case 8:
                        data[y * wpl + (x >> 2)] |= 0xFFu << (24 - 8 * (x & 3));
                        break;
                    case 1:
                        data[y * wpl + (x >> 5)] &= ~(0x80000000u >> (x & 31));
                        break;
                }
            }
        }

        return copy;
    }

    private const int LightGray = 215;
    private const int LightSaturation = 30;
    private const double LightRowShare = 0.6;

    /// <summary>Text-line skew in degrees (clockwise positive, the angle to rotate by), or 0 when unsure.</summary>
    public static double FindSkew(Pix page)
    {
        Pix binary = Binarize(page);
        if (!binary.FindSkew(out float angle, out float conf) || conf < 3.0f || Math.Abs(angle) < 0.1f)
            return 0;
        return angle;
    }

    /// <summary>Rotates by a small angle (degrees, clockwise positive) keeping the size, filling with white.</summary>
    public static Pix RotateSmall(Pix page, double degrees) =>
        Math.Abs(degrees) < 0.01 ? page : Rotate(page, degrees * Math.PI / 180);

    /// <summary>
    /// Clockwise rotation (0/90/180/270) that makes the text upright, or null when the page has too
    /// little text to tell. <paramref name="minConfidence"/>: Leptonica's 8 for pages, higher for photos.
    /// </summary>
    public static int? FindTextRotation(Pix page, float minConfidence = 8.0f)
    {
        Pix binary = Binarize(page);
        binary.OrientDetect(out float up, out float left, 0, 0);
        int decision = Pix.MakeOrientDecision(up, left, minConfidence, 0, 0);
        return decision switch
        {
            L.L_TEXT_ORIENT_UP => 0,
            L.L_TEXT_ORIENT_LEFT => 90,
            L.L_TEXT_ORIENT_DOWN => 180,
            L.L_TEXT_ORIENT_RIGHT => 270,
            _ => null,
        };
    }

    internal static Pix Binarize(Pix page)
    {
        if (page.Depth == 1)
            return page;
        Pix gray = page.ConvertTo8(false);
        Pix flat = gray.BackgroundNormSimple(null, null);
        return flat.ThresholdToBinary(130);
    }

    private static Pix Rotate(Pix pix, double radians)
    {
        // Area mapping needs 8 or 32 bpp; black & white pages are rotated by sampling.
        int type = pix.Depth == 1 ? L.L_ROTATE_SAMPLING : L.L_ROTATE_AREA_MAP;
        return pix.Rotate((float)radians, type, L.L_BRING_IN_WHITE, 0, 0);
    }

    private static Pix Clip(Pix pix, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, pix.Width - 1);
        y = Math.Clamp(y, 0, pix.Height - 1);
        w = Math.Clamp(w, 1, pix.Width - x);
        h = Math.Clamp(h, 1, pix.Height - y);
        Pix? clip = pix.ClipRectangle(Box.Create(x, y, w, h), out _);
        return clip ?? pix;
    }

    private static double Min((double X, double Y)[] a, Func<(double X, double Y), double> f)
    {
        double m = double.MaxValue;
        foreach ((double X, double Y) p in a)
            m = Math.Min(m, f(p));
        return m;
    }

    private static double Max((double X, double Y)[] a, Func<(double X, double Y), double> f)
    {
        double m = double.MinValue;
        foreach ((double X, double Y) p in a)
            m = Math.Max(m, f(p));
        return m;
    }
}
