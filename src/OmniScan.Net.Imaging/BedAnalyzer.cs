using System;
using System.Collections.Generic;
using System.Linq;
using Leptonica;

namespace OmniScan.Net.Imaging;

/// <summary>An axis-aligned box in full-resolution page pixels.</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public PixelBox Union(PixelBox o)
    {
        int x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new PixelBox(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }

    /// <summary>Distance between the boxes along the axis where they are apart the most (0 when touching).</summary>
    public int GapTo(PixelBox o) =>
        Math.Max(Math.Max(0, Math.Max(X, o.X) - Math.Min(Right, o.Right)), Math.Max(0, Math.Max(Y, o.Y) - Math.Min(Bottom, o.Bottom)));
}

/// <summary>One blob of ink on the glass (a photo, a block of text, a drawing), in full-resolution pixels.</summary>
/// <param name="Bounds">The minimum-area rectangle around it.</param>
/// <param name="Box">Its axis-aligned bounds.</param>
/// <param name="Density">Share of its hull that differs from the lid: photos ~0.9+, text and drawings well below.</param>
/// <param name="Share">Its box area as a share of the whole bed.</param>
public sealed record BedObject(RotatedRect Bounds, PixelBox Box, double Density, double Share)
{
    public const double PhotoDensity = 0.75;
    public const double PhotoMinShare = 0.01;

    public bool LooksLikePhoto => Density >= PhotoDensity && Share >= PhotoMinShare;
}

/// <summary>The result of looking at a whole-bed scan.</summary>
/// <param name="Objects">Every blob that is not a speck or a paper-edge line, largest first.</param>
/// <param name="Content">Bounds of all objects, or null when the bed is empty.</param>
/// <param name="Dpi">Resolution of the page, for distances in inches.</param>
public sealed record BedAnalysis(IReadOnlyList<BedObject> Objects, PixelBox? Content, int Dpi);

/// <summary>
/// Finds the objects on a flatbed scan. Works on a 75 dpi copy: the lid is modelled as a smooth
/// surface fitted to a ring along the edges (so a lid lifted by a thick card, with its shading,
/// is still "background"), and everything clearly darker, brighter or more colorful than that
/// surface is ink. Ink blobs closer than 5 mm are grouped into objects.
/// </summary>
public static class BedAnalyzer
{
    private const int WorkDpi = 75;
    private const int GrayDelta = 25;        // levels away from the lid model
    private const int SaturationLimit = 30;  // max - min of RGB
    private const int RingInner = 6;         // px at 75 dpi: skip the dark strip at the glass edge
    private const int RingOuter = 22;
    private const int BorderClear = 12;      // px at 75 dpi (4 mm): the dark strips at the glass edges
    private const int GroupGap = 15;         // 5 mm
    private const int MinBlobArea = 40;      // px at 75 dpi: smaller is dust
    private const int EdgeLineThickness = 6; // px at 75 dpi (2 mm)
    private const int EdgeLineLength = 40;   // px at 75 dpi (13 mm)

    public static BedAnalysis Analyze(Pix page)
    {
        int dpi = page.XRes > 0 ? page.XRes : 300;
        float k = Math.Min(1f, (float)WorkDpi / dpi);
        Pix small = page.Depth == 1 ? page.ConvertTo8(false) : page;
        if (k < 1f)
            small = small.ScaleAreaMap(k, k);
        double toFull = 1.0 / k;

        int w = small.Width, h = small.Height;
        byte[] gray = new byte[w * h];
        byte[] sat = new byte[w * h];
        PixelAccess.ReadGrayAndSaturation(small, gray, sat);

        double[] lid = FitLid(gray, sat, w, h);
        Pix ink = Pix.Create(w, h, 1);
        uint[] inkData = ink.Data;
        int wpl = ink.Wpl;
        for (int y = 0; y < h; y++)
        {
            double v = (double)y / h;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double u = (double)x / w;
                double bg = lid[0] + lid[1] * u + lid[2] * v + lid[3] * u * u + lid[4] * v * v + lid[5] * u * v;
                if (Math.Abs(gray[i] - bg) > GrayDelta || sat[i] > SaturationLimit)
                    inkData[y * wpl + (x >> 5)] |= 0x80000000u >> (x & 31);
            }
        }

        ink.SetOrClearBorder(BorderClear, BorderClear, BorderClear, BorderClear, L.PIX_CLR);
        Pix closed = Pix.CloseBrick(null, ink, 7, 7);
        Boxa boxa = closed.ConnComp(out Pixa comps, 8);

        List<Group> groups = GroupBoxes(boxa);
        List<BedObject> objects = [];
        PixelBox? content = null;
        double bedArea = (double)w * h;
        foreach (Group g in groups)
        {
            int gw = g.X1 - g.X0 + 1, gh = g.Y1 - g.Y0 + 1;
            if (gw * gh < MinBlobArea)
                continue;

            if (IsEdgeLine(gw, gh))
                continue;

            List<(double X, double Y)> points = [];
            long inkCount = 0;
            foreach (int ci in g.Members)
            {
                boxa.GetBox(ci, L.L_CLONE).GetGeometry(out int bx, out int by, out _, out _);
                inkCount += AddRowExtremes(comps.GetPix(ci, L.L_CLONE), bx, by, points);
            }

            List<(double X, double Y)> hull = Geometry.ConvexHull(points);
            double hullArea = Geometry.PolygonArea(hull);
            if (hullArea <= 0)
                continue;
            RotatedRect rect = Geometry.MinAreaRect(hull);
            PixelBox box = new((int)(g.X0 * toFull), (int)(g.Y0 * toFull), (int)Math.Ceiling(gw * toFull), (int)Math.Ceiling(gh * toFull));
            objects.Add(new BedObject(rect.Scale(toFull), box, Math.Min(1.0, inkCount / hullArea), gw * gh / bedArea));
            content = content is { } c ? c.Union(box) : box;
        }

        objects.Sort((a, b) => (b.Box.Width * (long)b.Box.Height).CompareTo(a.Box.Width * (long)a.Box.Height));
        return new BedAnalysis(objects, content, dpi);
    }

    /// <summary>Least-squares quadratic surface through low-saturation pixels of a ring along the edges.</summary>
    internal static double[] FitLid(byte[] gray, byte[] sat, int w, int h)
    {
        List<(double U, double V, double G)> samples = [];
        for (int y = RingInner; y < h - RingInner; y += 2)
        {
            for (int x = RingInner; x < w - RingInner; x += 2)
            {
                int d = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y));
                if (d > RingOuter || sat[y * w + x] > SaturationLimit)
                    continue;
                samples.Add(((double)x / w, (double)y / h, gray[y * w + x]));
            }
        }

        if (samples.Count < 12)
            return [255, 0, 0, 0, 0, 0];

        double[] c = Solve(samples);
        // Objects touching the ring pull the fit down; refit without them.
        List<(double U, double V, double G)> clean = samples.Where(s => s.G > Eval(c, s.U, s.V) - GrayDelta).ToList();
        return clean.Count >= 12 ? Solve(clean) : c;
    }

    private static double Eval(double[] c, double u, double v) =>
        c[0] + c[1] * u + c[2] * v + c[3] * u * u + c[4] * v * v + c[5] * u * v;

    private static double[] Solve(List<(double U, double V, double G)> samples)
    {
        const int n = 6;
        double[,] a = new double[n, n + 1];
        Span<double> r = stackalloc double[n];
        foreach ((double u, double v, double g) in samples)
        {
            r[0] = 1; r[1] = u; r[2] = v; r[3] = u * u; r[4] = v * v; r[5] = u * v;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                    a[i, j] += r[i] * r[j];
                a[i, n] += r[i] * g;
            }
        }

        for (int i = 0; i < n; i++)
        {
            int p = i;
            for (int k = i + 1; k < n; k++)
            {
                if (Math.Abs(a[k, i]) > Math.Abs(a[p, i]))
                    p = k;
            }

            for (int j = 0; j <= n; j++)
                (a[i, j], a[p, j]) = (a[p, j], a[i, j]);
            if (Math.Abs(a[i, i]) < 1e-12)
                return [255, 0, 0, 0, 0, 0];
            for (int k = 0; k < n; k++)
            {
                if (k == i)
                    continue;
                double f = a[k, i] / a[i, i];
                for (int j = i; j <= n; j++)
                    a[k, j] -= f * a[i, j];
            }
        }

        double[] c = new double[n];
        for (int i = 0; i < n; i++)
            c[i] = a[i, n] / a[i, i];
        return c;
    }

    private sealed class Group
    {
        public List<int> Members { get; } = [];
        public int X0 { get; set; }
        public int Y0 { get; set; }
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int Area => (X1 - X0 + 1) * (Y1 - Y0 + 1);
    }

    /// <summary>The shadow of a paper or lid edge: long and hair-thin. Not content.</summary>
    private static bool IsEdgeLine(int w, int h) => Math.Min(w, h) <= EdgeLineThickness && Math.Max(w, h) >= EdgeLineLength;

    private static List<Group> GroupBoxes(Boxa boxa)
    {
        List<Group> groups = [];
        for (int i = 0; i < boxa.Count; i++)
        {
            boxa.GetBox(i, L.L_CLONE).GetGeometry(out int x, out int y, out int bw, out int bh);
            if (bw * bh < 40)
                continue;

            // Shadows of paper or lid edges are dropped before grouping: next to a photo they would
            // otherwise merge with it and stretch its frame to the edge of the glass.
            if (IsEdgeLine(bw, bh))
                continue;
            Group g = new() { X0 = x, Y0 = y, X1 = x + bw - 1, Y1 = y + bh - 1 };
            g.Members.Add(i);
            groups.Add(g);
        }

        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int a = 0; a < groups.Count && !merged; a++)
            {
                for (int b = a + 1; b < groups.Count && !merged; b++)
                {
                    Group g1 = groups[a], g2 = groups[b];
                    int gx = Math.Max(0, Math.Max(g1.X0, g2.X0) - Math.Min(g1.X1, g2.X1));
                    int gy = Math.Max(0, Math.Max(g1.Y0, g2.Y0) - Math.Min(g1.Y1, g2.Y1));
                    if (gx > GroupGap || gy > GroupGap)
                        continue;
                    g1.X0 = Math.Min(g1.X0, g2.X0);
                    g1.Y0 = Math.Min(g1.Y0, g2.Y0);
                    g1.X1 = Math.Max(g1.X1, g2.X1);
                    g1.Y1 = Math.Max(g1.Y1, g2.Y1);
                    g1.Members.AddRange(g2.Members);
                    groups.RemoveAt(b);
                    merged = true;
                }
            }
        }

        return groups;
    }

    /// <summary>Adds the corners of the first and last set pixel of each row; returns the set pixel count.</summary>
    private static long AddRowExtremes(Pix comp, int ox, int oy, List<(double X, double Y)> points)
    {
        uint[] data = comp.Data;
        int wpl = comp.Wpl, w = comp.Width, h = comp.Height;
        long count = 0;
        for (int y = 0; y < h; y++)
        {
            int first = -1, last = -1, row = y * wpl;
            for (int x = 0; x < w; x++)
            {
                if ((data[row + (x >> 5)] & (0x80000000u >> (x & 31))) == 0)
                    continue;
                if (first < 0)
                    first = x;
                last = x;
                count++;
            }

            if (first < 0)
                continue;
            points.Add((ox + first, oy + y));
            points.Add((ox + last + 1, oy + y));
            points.Add((ox + first, oy + y + 1));
            points.Add((ox + last + 1, oy + y + 1));
        }

        return count;
    }
}
