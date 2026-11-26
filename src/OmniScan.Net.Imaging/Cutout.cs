using System;
using System.Collections.Generic;
using System.Linq;
using Leptonica;

namespace OmniScan.Net.Imaging;

/// <summary>
/// Stickers: each picture on the glass cut along its outline onto a transparent background (PNG),
/// with a white border around it, as a sticker has.
/// <para>
/// A sticker's own white border can hardly be told from the white lid (a few levels apart, and the lid
/// next to the edge is a little shaded by the sticker's thickness). The colored picture stands out
/// clearly, so the outline is found around it and the border is drawn anew at a fixed width, plain white:
/// the scan there shows the edge of the sticker's own border, which must not show through. White parts
/// of the picture at its edge go into that border unnoticed. A photo or card (a rectangle) is cut as usual.
/// </para>
/// </summary>
public static class Cutout
{
    /// <summary>Taken around a detected object before looking for its outline, inches (room for the border).</summary>
    public const double SearchMargin = 0.15;

    /// <summary>Width of the white border around the picture, inches (about 1.5 mm).</summary>
    private const double Border = 0.06;

    /// <summary>A shape filling this much of its straightened bounding box is a photo or card: cut as a rectangle.</summary>
    private const double PhotoFill = 0.9;

    /// <summary>
    /// A shape running along this much of the sides of its bounding box is a photo or card too, even with
    /// a white corner (snow, sky) that the outline cut off; a sticker only touches its box here and there.
    /// </summary>
    private const double PhotoSides = 0.6;

    /// <summary>Average saturation below which a thin line is the gray cut edge of a sticker's own border.</summary>
    private const int GrayLine = 25;

    /// <summary>Neutral gray lighter than this is the cut edge of a sticker's border, not a line of its picture.</summary>
    private const int EdgeGray = 150;

    /// <summary>
    /// Half the width (pixels at 300 dpi) a light gray part must have to be picture: the shaded cut edge
    /// of a sticker's border is up to about 12 pixels wide.
    /// </summary>
    private const int GrayArea = 8;

    /// <summary>The lid around a sticker is white: a thin sticker does not lift it.</summary>
    private const int MinLid = 240;

    /// <summary>Levels below the lid that count as the picture.</summary>
    private const int BelowLid = 30;

    /// <summary>
    /// Cuts the picture in <paramref name="image"/> (cut out with <see cref="SearchMargin"/> around it)
    /// along its outline with a white border: a 32 bpp image with alpha, white where transparent.
    /// Null when the object is a rectangle (a photo or card), or no picture is found.
    /// </summary>
    /// <param name="margin">How far the object's frame lies inside the image on every side, pixels.</param>
    public static Pix? Cut(Pix image, int dpi, int margin = 0)
    {
        if (Find(image, dpi, margin) is not { } shape)
            return null;
        (bool[] mask, bool[] picture, int x0, int y0, int x1, int y1) = shape;
        int w = image.Width, h = image.Height;

        // A slightly soft edge: no stairs along the outline.
        float[] alpha = new float[w * h];
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = mask[i] ? 1 : 0;
        alpha = BoxMean(alpha, w, h, 1);

        Pix color = image.Depth == 32 ? image : image.ConvertTo32();
        int pad = 2;
        int cx0 = Math.Max(0, x0 - pad), cy0 = Math.Max(0, y0 - pad);
        int cx1 = Math.Min(w - 1, x1 + pad), cy1 = Math.Min(h - 1, y1 + pad);
        int ow = cx1 - cx0 + 1, oh = cy1 - cy0 + 1;
        Pix result = Pix.Create(ow, oh, 32);
        result.SetResolution(image.XRes, image.YRes);
        result.Spp = 4;
        uint[] src = color.Data, dst = result.Data;
        int swpl = color.Wpl, dwpl = result.Wpl;
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                int i = (y + cy0) * w + x + cx0;
                int a = (int)Math.Round(alpha[i] * 255);
                dst[y * dwpl + x] = a == 0 ? 0xffffff00u
                    : picture[i] ? (src[(y + cy0) * swpl + x + cx0] & 0xffffff00u) | (uint)a
                    : 0xffffff00u | (uint)a;
            }
        }

        return result;
    }

    /// <summary>
    /// The outline <see cref="Cut"/> would cut along, as a polygon in the pixels of <paramref name="image"/>;
    /// null when the object is not cut as a sticker.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)>? Outline(Pix image, int dpi, int margin = 0)
    {
        // A preview is too coarse for the brushes to keep their shape (a few pixels each): look at it
        // enlarged. It shows no more detail, but the outline comes out round where the sticker is round.
        double k = 1;
        if (dpi < OutlineDpi)
        {
            k = (double)OutlineDpi / dpi;
            image = image.ScaleToSize((int)Math.Round(image.Width * k), (int)Math.Round(image.Height * k));
            dpi = OutlineDpi;
            margin = (int)Math.Round(margin * k);
        }

        if (Find(image, dpi, margin) is not { } shape)
            return null;
        List<(double X, double Y)> outline = Smooth(Simplify(Trace(shape.Mask, image.Width, image.Height), 1.0));
        return outline.Select(p => (p.X / k, p.Y / k)).ToList();
    }

    /// <summary>The least resolution an outline is found at (a preview is enlarged to it).</summary>
    private const int OutlineDpi = 150;

    /// <summary>Two rounds of corner cutting (Chaikin): no visible steps or spikes along the outline.</summary>
    private static List<(double X, double Y)> Smooth(List<(double X, double Y)> points)
    {
        for (int round = 0; round < 2 && points.Count > 2; round++)
        {
            List<(double X, double Y)> next = new(points.Count * 2);
            for (int i = 0; i < points.Count; i++)
            {
                (double ax, double ay) = points[i];
                (double bx, double by) = points[(i + 1) % points.Count];
                next.Add((0.75 * ax + 0.25 * bx, 0.75 * ay + 0.25 * by));
                next.Add((0.25 * ax + 0.75 * bx, 0.25 * ay + 0.75 * by));
            }

            points = next;
        }

        return points;
    }

    /// <summary>
    /// The sticker's shape with its border (<c>Mask</c>), where the scan is kept (<c>Picture</c>) and the
    /// shape's bounds; null for a photo or card, or when nothing is found.
    /// </summary>
    private static (bool[] Mask, bool[] Picture, int X0, int Y0, int X1, int Y1)? Find(Pix image, int dpi, int margin)
    {
        if (image.Depth is not (8 or 32) || image.Colormap is not null || image.Width < 16 || image.Height < 16)
            return null;
        int w = image.Width, h = image.Height;
        double k = dpi / 300.0;
        byte[] min = MinChannel(image);

        // The lid level along the edges of the piece.
        List<byte> ring = new(2 * (w + h));
        for (int x = 0; x < w; x++)
        {
            ring.Add(min[x]);
            ring.Add(min[(h - 1) * w + x]);
        }

        for (int y = 0; y < h; y++)
        {
            ring.Add(min[y * w]);
            ring.Add(min[y * w + w - 1]);
        }

        ring.Sort();
        // A gray lid around the object: it is thick (a card lifting the lid), not a sticker.
        int lid = ring[ring.Count / 2];
        if (lid < MinLid)
            return null;

        // The picture: clearly darker or more colored than the lid (the darkest of R, G, B), over a few pixels.
        float[] level = new float[w * h];
        for (int i = 0; i < level.Length; i++)
            level[i] = min[i];
        float[] mean = BoxMean(level, w, h, Radius(2, k));
        byte[] saturation = Saturation(image);
        float[] sat = new float[w * h];
        for (int i = 0; i < sat.Length; i++)
            sat[i] = saturation[i];
        sat = BoxMean(sat, w, h, Radius(2, k));

        // A thin light neutral gray line is the cut edge of the sticker's own border (its shadow), not the
        // picture: the lines of a picture are dark or colored. Light gray areas of some width (a silvery
        // spacecraft on a photo under a sticker) are picture.
        bool[] mask = new bool[w * h];
        bool[] lightGray = new bool[w * h];
        for (int i = 0; i < mask.Length; i++)
        {
            bool ink = mean[i] <= lid - BelowLid;
            bool gray = mean[i] > EdgeGray && sat[i] < GrayLine;
            mask[i] = ink && !gray;
            lightGray[i] = ink && gray;
        }

        bool[] grayAreas = Dilate(Erode(lightGray, w, h, Radius(GrayArea, k)), w, h, Radius(GrayArea, k));
        for (int i = 0; i < mask.Length; i++)
            mask[i] |= grayAreas[i];

        // Thin lines go: the cut edge of the sticker's own border closes around the picture and would
        // bring that border in when the inside is filled. On the shaded side it is thicker; a thin gray
        // part with no solid core is that edge, while thin colored lines of the picture stay.
        mask = Dilate(Erode(mask, w, h, Radius(2, k)), w, h, Radius(2, k));
        DropGrayLines(mask, Erode(mask, w, h, Radius(4, k)), saturation, w, h);
        mask = Erode(Dilate(mask, w, h, Radius(8, k)), w, h, Radius(8, k));
        FillHoles(mask, w, h);
        DropSpecks(mask, w, h, (int)(900 * k * k));

        // A photo or card fills its frame with picture (even with a shaded lid or a shadow around it).
        int inFrame = 0, frameArea = 0;
        for (int y = margin; y < h - margin; y++)
        {
            for (int x = margin; x < w - margin; x++)
            {
                frameArea++;
                inFrame += mask[y * w + x] ? 1 : 0;
            }
        }

        if (frameArea > 0 && inFrame >= PhotoFill * frameArea)
            return null;

        // The border around it, rounded off. The scan is kept just around the picture (its soft edges).
        bool[] picture = Dilate(mask, w, h, Radius(2, k));
        mask = Dilate(mask, w, h, Radius(Border * 300, k));
        FillHoles(mask, w, h);
        float[] soft = new float[w * h];
        for (int i = 0; i < soft.Length; i++)
            soft[i] = mask[i] ? 1 : 0;
        int r = Radius(4, k);
        soft = BoxMean(BoxMean(BoxMean(soft, w, h, r), w, h, r), w, h, r);
        for (int i = 0; i < mask.Length; i++)
            mask[i] = soft[i] > 0.5f;

        (int area, int x0, int y0, int x1, int y1) = KeepLargest(mask, w, h);
        if (area < 900 * k * k)
            return null;
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        if (area >= PhotoFill * bw * bh || StraightSides(mask, w, x0, y0, x1, y1, Radius(12, k)) >= PhotoSides)
            return null;
        return (mask, picture, x0, y0, x1, y1);
    }

    private static readonly (int Dx, int Dy)[] Around = [(-1, 0), (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1)];

    /// <summary>The outer boundary of the (single) shape in the set, pixel centers, clockwise (Moore tracing).</summary>
    private static List<(double X, double Y)> Trace(bool[] set, int w, int h)
    {
        List<(double X, double Y)> points = [];
        int start = Array.IndexOf(set, true);
        if (start < 0)
            return points;
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && set[y * w + x];

        int px = start % w, py = start / w;
        int back = 0;   // the first pixel in raster order: its west neighbor is outside
        int sx = px, sy = py, startBack = back;
        for (int steps = 0; steps < 4 * set.Length; steps++)
        {
            points.Add((px + 0.5, py + 0.5));
            int next = -1;
            for (int t = 1; t <= 8; t++)
            {
                int d = (back + t) % 8;
                if (In(px + Around[d].Dx, py + Around[d].Dy))
                {
                    next = d;
                    break;
                }
            }

            if (next < 0)
                break;   // a single pixel

            // The neighbor checked just before the found one is outside: it is the new backtrack.
            int prev = (next + 7) % 8;
            int bx = px + Around[prev].Dx, by = py + Around[prev].Dy;
            px += Around[next].Dx;
            py += Around[next].Dy;
            back = Array.IndexOf(Around, (bx - px, by - py));
            if (px == sx && py == sy && back == startBack)
                break;
        }

        return points;
    }

    /// <summary>Douglas-Peucker on a closed polygon: points closer than <paramref name="tolerance"/> to the line go.</summary>
    private static List<(double X, double Y)> Simplify(List<(double X, double Y)> points, double tolerance)
    {
        if (points.Count < 4)
            return points;
        bool[] keep = new bool[points.Count];
        int far = 0;
        double farthest = -1;
        for (int i = 1; i < points.Count; i++)
        {
            double d = Math.Pow(points[i].X - points[0].X, 2) + Math.Pow(points[i].Y - points[0].Y, 2);
            if (d > farthest)
            {
                farthest = d;
                far = i;
            }
        }

        keep[0] = keep[far] = true;
        Stack<(int A, int B)> spans = new([(0, far), (far, points.Count - 1)]);
        while (spans.Count > 0)
        {
            (int a, int b) = spans.Pop();
            (double ax, double ay) = points[a];
            (double bx, double by) = points[b];
            double len = Math.Max(1e-9, Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
            int worst = -1;
            double max = tolerance;
            for (int i = a + 1; i < b; i++)
            {
                double d = Math.Abs((bx - ax) * (ay - points[i].Y) - (ax - points[i].X) * (by - ay)) / len;
                if (d > max)
                {
                    max = d;
                    worst = i;
                }
            }

            if (worst < 0)
                continue;
            keep[worst] = true;
            spans.Push((a, worst));
            spans.Push((worst, b));
        }

        keep[^1] = true;
        List<(double X, double Y)> result = [];
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }

        return result;
    }

    /// <summary>
    /// How much of the four sides of the box the shape runs along (within <paramref name="depth"/> of
    /// each side), on average: 1 for a rectangle.
    /// </summary>
    private static double StraightSides(bool[] mask, int w, int x0, int y0, int x1, int y1, int depth)
    {
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        int top = 0, bottom = 0, left = 0, right = 0;
        for (int x = x0; x <= x1; x++)
        {
            bool t = false, b = false;
            for (int d = 0; d < depth && d < bh; d++)
            {
                t |= mask[(y0 + d) * w + x];
                b |= mask[(y1 - d) * w + x];
            }

            top += t ? 1 : 0;
            bottom += b ? 1 : 0;
        }

        for (int y = y0; y <= y1; y++)
        {
            bool l = false, r = false;
            for (int d = 0; d < depth && d < bw; d++)
            {
                l |= mask[y * w + x0 + d];
                r |= mask[y * w + x1 - d];
            }

            left += l ? 1 : 0;
            right += r ? 1 : 0;
        }

        return ((double)(top + bottom) / bw + (double)(left + right) / bh) / 4;
    }

    /// <summary>Clears the parts of the set with no pixel in <paramref name="core"/> whose color is gray on average.</summary>
    private static void DropGrayLines(bool[] set, bool[] core, byte[] saturation, int w, int h)
    {
        bool[] seen = new bool[set.Length];
        int[] queue = new int[set.Length];
        for (int start = 0; start < set.Length; start++)
        {
            if (!set[start] || seen[start])
                continue;
            int head = 0, tail = 0;
            bool solid = false;
            long sat = 0;
            seen[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                int i = queue[head++], x = i % w;
                solid |= core[i];
                sat += saturation[i];
                foreach (int j in (ReadOnlySpan<int>)[x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, i >= w ? i - w : -1, i < (h - 1) * w ? i + w : -1])
                {
                    if (j >= 0 && set[j] && !seen[j])
                    {
                        seen[j] = true;
                        queue[tail++] = j;
                    }
                }
            }

            if (!solid && sat < GrayLine * tail)
            {
                for (int q = 0; q < tail; q++)
                    set[queue[q]] = false;
            }
        }
    }

    /// <summary>max - min of R, G and B per pixel (0 for 8 bpp).</summary>
    private static byte[] Saturation(Pix pix)
    {
        int w = pix.Width, h = pix.Height, wpl = pix.Wpl;
        byte[] sat = new byte[w * h];
        if (pix.Depth != 32)
            return sat;
        uint[] data = pix.Data;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                uint p = data[y * wpl + x];
                uint r = p >> 24, g = (p >> 16) & 0xff, b = (p >> 8) & 0xff;
                sat[y * w + x] = (byte)(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)));
            }
        }

        return sat;
    }

    /// <summary>Clears 4-connected parts of the set smaller than <paramref name="minArea"/> (dust on the glass).</summary>
    private static void DropSpecks(bool[] set, int w, int h, int minArea)
    {
        bool[] seen = new bool[set.Length];
        int[] queue = new int[set.Length];
        for (int start = 0; start < set.Length; start++)
        {
            if (!set[start] || seen[start])
                continue;
            int head = 0, tail = 0;
            seen[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                int i = queue[head++], x = i % w;
                foreach (int j in (ReadOnlySpan<int>)[x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, i >= w ? i - w : -1, i < (h - 1) * w ? i + w : -1])
                {
                    if (j >= 0 && set[j] && !seen[j])
                    {
                        seen[j] = true;
                        queue[tail++] = j;
                    }
                }
            }

            if (tail < minArea)
            {
                for (int q = 0; q < tail; q++)
                    set[queue[q]] = false;
            }
        }
    }

    private static int Radius(double at300, double k) => Math.Max(1, (int)Math.Round(at300 * k));

    /// <summary>The darkest of R, G and B per pixel (the gray value for 8 bpp).</summary>
    private static byte[] MinChannel(Pix pix)
    {
        int w = pix.Width, h = pix.Height, wpl = pix.Wpl;
        uint[] data = pix.Data;
        byte[] min = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * wpl, o = y * w;
            for (int x = 0; x < w; x++)
            {
                if (pix.Depth == 32)
                {
                    uint p = data[row + x];
                    min[o + x] = (byte)Math.Min(p >> 24, Math.Min((p >> 16) & 0xff, (p >> 8) & 0xff));
                }
                else
                {
                    min[o + x] = (byte)(data[row + (x >> 2)] >> (24 - 8 * (x & 3)));
                }
            }
        }

        return min;
    }

    /// <summary>Mean over a (2r+1)² window, clipped at the edges.</summary>
    private static float[] BoxMean(float[] src, int w, int h, int r)
    {
        float[] tmp = new float[w * h], dst = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            double sum = 0;
            for (int x = 0; x <= Math.Min(r, w - 1); x++)
                sum += src[o + x];
            for (int x = 0; x < w; x++)
            {
                int lo = Math.Max(0, x - r), hi = Math.Min(w - 1, x + r);
                tmp[o + x] = (float)(sum / (hi - lo + 1));
                if (x + r + 1 < w)
                    sum += src[o + x + r + 1];
                if (x - r >= 0)
                    sum -= src[o + x - r];
            }
        }

        for (int x = 0; x < w; x++)
        {
            double sum = 0;
            for (int y = 0; y <= Math.Min(r, h - 1); y++)
                sum += tmp[y * w + x];
            for (int y = 0; y < h; y++)
            {
                int lo = Math.Max(0, y - r), hi = Math.Min(h - 1, y + r);
                dst[y * w + x] = (float)(sum / (hi - lo + 1));
                if (y + r + 1 < h)
                    sum += tmp[(y + r + 1) * w + x];
                if (y - r >= 0)
                    sum -= tmp[(y - r) * w + x];
            }
        }

        return dst;
    }

    /// <summary>Pixels within <paramref name="r"/> of the set (a round brush, by the exact distance).</summary>
    internal static bool[] Dilate(bool[] set, int w, int h, int r)
    {
        float[] d = SquaredDistance(set, w, h);
        bool[] result = new bool[set.Length];
        float r2 = r * r;
        for (int i = 0; i < result.Length; i++)
            result[i] = d[i] <= r2;
        return result;
    }

    /// <summary>Pixels farther than <paramref name="r"/> from anything outside the set (the image edge does not count).</summary>
    internal static bool[] Erode(bool[] set, int w, int h, int r)
    {
        bool[] outside = new bool[set.Length];
        for (int i = 0; i < set.Length; i++)
            outside[i] = !set[i];
        bool[] grown = Dilate(outside, w, h, r);
        for (int i = 0; i < grown.Length; i++)
            grown[i] = !grown[i];
        return grown;
    }

    /// <summary>Squared Euclidean distance to the nearest pixel of the set (Felzenszwalb and Huttenlocher).</summary>
    private static float[] SquaredDistance(bool[] set, int w, int h)
    {
        const float Far = 1e20f;
        float[] d = new float[w * h];
        for (int i = 0; i < d.Length; i++)
            d[i] = set[i] ? 0 : Far;
        int n = Math.Max(w, h);
        float[] f = new float[n], line = new float[n], z = new float[n + 1];
        int[] v = new int[n];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
                f[y] = d[y * w + x];
            Transform(f, h, line, v, z);
            for (int y = 0; y < h; y++)
                d[y * w + x] = line[y];
        }

        for (int y = 0; y < h; y++)
        {
            Array.Copy(d, y * w, f, 0, w);
            Transform(f, w, line, v, z);
            Array.Copy(line, 0, d, y * w, w);
        }

        return d;
    }

    private static void Transform(float[] f, int n, float[] d, int[] v, float[] z)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
            while (s <= z[k])
            {
                k--;
                s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;
            float dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    /// <summary>Sets every pixel not reachable from the image edge through pixels outside the set.</summary>
    internal static void FillHoles(bool[] set, int w, int h)
    {
        bool[] reached = new bool[set.Length];
        int[] queue = new int[set.Length];
        int head = 0, tail = 0;
        void Seed(int i)
        {
            if (!set[i] && !reached[i])
            {
                reached[i] = true;
                queue[tail++] = i;
            }
        }

        for (int x = 0; x < w; x++)
        {
            Seed(x);
            Seed((h - 1) * w + x);
        }

        for (int y = 0; y < h; y++)
        {
            Seed(y * w);
            Seed(y * w + w - 1);
        }

        while (head < tail)
        {
            int i = queue[head++], x = i % w;
            if (x > 0) Seed(i - 1);
            if (x < w - 1) Seed(i + 1);
            if (i >= w) Seed(i - w);
            if (i < (h - 1) * w) Seed(i + w);
        }

        for (int i = 0; i < set.Length; i++)
            set[i] = !reached[i];
    }

    /// <summary>Keeps only the largest 4-connected part of the set; returns its area and bounds.</summary>
    internal static (int Area, int X0, int Y0, int X1, int Y1) KeepLargest(bool[] set, int w, int h)
    {
        int[] label = new int[set.Length];
        int[] queue = new int[set.Length];
        int best = 0, bestArea = 0, next = 0;
        for (int start = 0; start < set.Length; start++)
        {
            if (!set[start] || label[start] != 0)
                continue;
            int id = ++next, head = 0, tail = 0;
            label[start] = id;
            queue[tail++] = start;
            while (head < tail)
            {
                int i = queue[head++], x = i % w;
                foreach (int j in (ReadOnlySpan<int>)[x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, i >= w ? i - w : -1, i < (h - 1) * w ? i + w : -1])
                {
                    if (j >= 0 && set[j] && label[j] == 0)
                    {
                        label[j] = id;
                        queue[tail++] = j;
                    }
                }
            }

            if (tail > bestArea)
            {
                bestArea = tail;
                best = id;
            }
        }

        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int i = 0; i < set.Length; i++)
        {
            set[i] = best != 0 && label[i] == best;
            if (!set[i])
                continue;
            int x = i % w, y = i / w;
            x0 = Math.Min(x0, x);
            x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y);
            y1 = Math.Max(y1, y);
        }

        return (bestArea, x0, y0, x1, y1);
    }
}
