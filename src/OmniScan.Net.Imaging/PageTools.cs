using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Widest light fringe trimmed by <see cref="TrimFringe"/>, inches (about 0.6 mm).</summary>
    private const double MaxFringe = 0.025;

    /// <summary>How much lighter than the inside a line along an edge is to count as fringe.</summary>
    private const int FringeLighter = 20;

    /// <summary>
    /// Trims the thin light fringe a photo keeps along a side: the frame is found on a small copy of
    /// the bed, so one side may take in a few pixels of the paper's edge, a gradient from light to the
    /// picture that is not white enough for <see cref="TrimLightBorder"/>. Lines along each side are
    /// trimmed while they are clearly lighter than the lines just inside, at most <see cref="MaxFringe"/>.
    /// </summary>
    public static Pix TrimFringe(Pix image, int dpi)
    {
        if (image.Depth != 32 && image.Depth != 8)
            return image;
        int w = image.Width, h = image.Height;
        int max = Math.Max(1, (int)Math.Round(MaxFringe * dpi));
        const int inside = 12;
        if (w < 4 * (max + inside) || h < 4 * (max + inside))
            return image;

        byte[] gray = new byte[w * h];
        byte[] sat = new byte[w * h];
        PixelAccess.ReadGrayAndSaturation(image, gray, sat);
        double Row(int y)
        {
            long sum = 0;
            for (int x = 0; x < w; x++)
                sum += gray[y * w + x];
            return (double)sum / w;
        }

        double Col(int x)
        {
            long sum = 0;
            for (int y = 0; y < h; y++)
                sum += gray[y * w + x];
            return (double)sum / h;
        }

        int Count(Func<int, double> line)
        {
            double reference = 0;
            for (int i = max; i < max + inside; i++)
                reference += line(i);
            reference /= inside;
            int n = 0;
            while (n < max && line(n) > reference + FringeLighter)
                n++;
            return n;
        }

        int top = Count(Row), bottom = Count(y => Row(h - 1 - y));
        int left = Count(Col), right = Count(x => Col(w - 1 - x));
        if (top + bottom + left + right == 0)
            return image;
        return Clip(image, left, top, w - left - right, h - top - bottom);
    }

    /// <summary>Paper this colorful (max - min of its RGB) gives a gray result: see <see cref="WhitenPaper"/>.</summary>
    private const int ColoredPaper = 40;

    /// <summary>From this share of the paper's brightness on, a pixel is white (paper grain, show-through).</summary>
    private const double PaperWhite = 0.88;

    /// <summary>Up to this share of the paper's brightness, a pixel is black.</summary>
    private const double InkBlack = 0.08;

    /// <summary>A little more weight to the middle grays, so that tinted panels and drawings stay readable.</summary>
    private const double MidGray = 1.15;

    /// <summary>
    /// Paper of any color made white, the print on it kept. A flatbed lights the sheet evenly, so the
    /// paper has one color all over: it is found once (the most common color of the sheet) and every
    /// pixel is taken relative to it, then set like a copier does (near the paper: white; the rest
    /// stretched). Unlike a background estimated piece by piece, this keeps large tinted panels (a
    /// heading on a dark band) as they are. On clearly colored paper the result is gray, like a
    /// photocopy: the colors of the print cannot be told under it (yellow paper takes the blue away
    /// from everything, so blue and black look the same), and the channels are mixed in the measure the
    /// paper reflects them. On paper that is only tinted the colors are kept.
    /// </summary>
    public static Pix WhitenPaper(Pix sheet)
    {
        if (sheet.Depth is not (8 or 32) || sheet.Colormap is not null || sheet.Width < 64 || sheet.Height < 64
            || PaperColor(sheet) is not { } paper)
            return sheet;

        int w = sheet.Width, h = sheet.Height, swpl = sheet.Wpl;
        uint[] src = sheet.Data;
        bool colored = Math.Max(paper.R, Math.Max(paper.G, paper.B)) - Math.Min(paper.R, Math.Min(paper.G, paper.B)) >= ColoredPaper;
        if (sheet.Depth == 32 && !colored)
        {
            byte[] lr = Levels(paper.R), lg = Levels(paper.G), lb = Levels(paper.B);
            Pix color = Pix.Create(w, h, 32);
            color.SetResolution(sheet.XRes, sheet.YRes);
            uint[] dst = color.Data;
            int cwpl = color.Wpl;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    uint p = src[y * swpl + x];
                    dst[y * cwpl + x] = ((uint)lr[p >> 24] << 24) | ((uint)lg[(p >> 16) & 0xff] << 16) | ((uint)lb[(p >> 8) & 0xff] << 8);
                }
            }

            return color;
        }

        Pix gray = Pix.Create(w, h, 8);
        gray.SetResolution(sheet.XRes, sheet.YRes);
        uint[] g = gray.Data;
        int gwpl = gray.Wpl;
        if (sheet.Depth == 8)
        {
            byte[] lut = Levels(paper.R);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int shift = 24 - 8 * (x & 3);
                    uint v = (src[y * swpl + (x >> 2)] >> shift) & 0xff;
                    g[y * gwpl + (x >> 2)] |= (uint)lut[v] << shift;
                }
            }

            return gray;
        }

        // Each channel counts as much as the paper reflects it; relative to the paper, then set.
        double sum = Math.Max(1, paper.R + paper.G + paper.B);
        double[] rr = Relative(paper.R, paper.R / sum), rg = Relative(paper.G, paper.G / sum), rb = Relative(paper.B, paper.B / sum);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                uint p = src[y * swpl + x];
                double rel = rr[p >> 24] + rg[(p >> 16) & 0xff] + rb[(p >> 8) & 0xff];
                g[y * gwpl + (x >> 2)] |= (uint)Level(rel) << (24 - 8 * (x & 3));
            }
        }

        return gray;
    }

    /// <summary>A channel value as a share of the paper's, weighted.</summary>
    private static double[] Relative(int paper, double weight)
    {
        double[] t = new double[256];
        for (int v = 0; v < 256; v++)
            t[v] = weight * v / Math.Max(1, paper);
        return t;
    }

    private static byte[] Levels(int paper)
    {
        byte[] t = new byte[256];
        for (int v = 0; v < 256; v++)
            t[v] = Level((double)v / Math.Max(1, paper));
        return t;
    }

    /// <summary>Brightness relative to the paper (1 = paper) to an output level, as a copier sets it.</summary>
    private static byte Level(double relative)
    {
        double t = Math.Clamp((relative - InkBlack) / (PaperWhite - InkBlack), 0, 1);
        return (byte)Math.Round(Math.Pow(t, MidGray) * 255);
    }

    /// <summary>Share of a sheet that is its paper, at least (a photo is rarely so much of one even color).</summary>
    private const double PaperShare = 0.5;

    /// <summary>Most of the image is one even, light color: a sheet of paper (of any color), not a photo.</summary>
    public static bool LooksLikePaper(Pix image)
    {
        if (PaperColor(image) is not { } paper)
            return false;
        int near = 0, all = 0;
        foreach ((int r, int g, int b) in Samples(image))
        {
            near += Math.Abs(r - paper.R) + Math.Abs(g - paper.G) + Math.Abs(b - paper.B) < 60 ? 1 : 0;
            all++;
        }

        return all > 0 && near >= PaperShare * all;
    }

    /// <summary>
    /// The color of the paper: the most common light color of the sheet (the paper, not the lid around
    /// a smaller sheet or the print); null when nothing light is found.
    /// </summary>
    private static (int R, int G, int B)? PaperColor(Pix sheet)
    {
        Dictionary<int, List<(int R, int G, int B)>> bins = [];
        foreach ((int R, int G, int B) s in Samples(sheet))
        {
            if (Math.Max(s.R, Math.Max(s.G, s.B)) < 100)
                continue;
            int key = ((s.R >> 4) << 8) | ((s.G >> 4) << 4) | (s.B >> 4);
            if (!bins.TryGetValue(key, out List<(int R, int G, int B)>? list))
                bins[key] = list = [];
            list.Add(s);
        }

        if (bins.Count == 0)
            return null;
        List<(int R, int G, int B)> common = bins.Values.MaxBy(l => l.Count)!;
        int Median(Func<(int R, int G, int B), int> channel)
        {
            int[] values = common.Select(channel).Order().ToArray();
            return values[values.Length / 2];
        }

        return (Median(c => c.R), Median(c => c.G), Median(c => c.B));
    }

    /// <summary>Every fourth pixel in both directions, as R, G, B (gray images: the same value three times).</summary>
    private static IEnumerable<(int R, int G, int B)> Samples(Pix image)
    {
        if (image.Depth is not (8 or 32) || image.Colormap is not null)
            yield break;
        uint[] data = image.Data;
        int w = image.Width, h = image.Height, wpl = image.Wpl;
        for (int y = 0; y < h; y += 4)
        {
            for (int x = 0; x < w; x += 4)
            {
                if (image.Depth == 32)
                {
                    uint p = data[y * wpl + x];
                    yield return ((int)(p >> 24), (int)((p >> 16) & 0xff), (int)((p >> 8) & 0xff));
                }
                else
                {
                    int v = (int)((data[y * wpl + (x >> 2)] >> (24 - 8 * (x & 3))) & 0xff);
                    yield return (v, v, v);
                }
            }
        }
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
