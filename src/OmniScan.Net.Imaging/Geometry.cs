using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniScan.Net.Imaging;

/// <summary>A rectangle of any rotation: center, size and the angle of its width side in degrees (clockwise).</summary>
public readonly record struct RotatedRect(double Cx, double Cy, double Width, double Height, double Angle)
{
    public RotatedRect Scale(double k) => new(Cx * k, Cy * k, Width * k, Height * k, Angle);

    public RotatedRect Shrink(double by) => new(Cx, Cy, Math.Max(1, Width - 2 * by), Math.Max(1, Height - 2 * by), Angle);

    /// <summary>The four corners, clockwise from the top-left of the unrotated rectangle.</summary>
    public (double X, double Y)[] Corners()
    {
        double rad = Angle * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
        double hw = Width / 2, hh = Height / 2;
        double cx = Cx, cy = Cy;
        (double, double)[] local = [(-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh)];
        return local.Select(p => (cx + p.Item1 * c - p.Item2 * s, cy + p.Item1 * s + p.Item2 * c)).ToArray();
    }
}

internal static class Geometry
{
    /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise, without collinear points.</summary>
    public static List<(double X, double Y)> ConvexHull(IEnumerable<(double X, double Y)> points)
    {
        List<(double X, double Y)> p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3)
            return p;

        List<(double X, double Y)> hull = [];
        foreach ((double X, double Y) q in p)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], q) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(q);
        }

        int lower = hull.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p[i]) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p[i]);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    public static double PolygonArea(IReadOnlyList<(double X, double Y)> poly)
    {
        double a = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            (double X, double Y) p = poly[i];
            (double X, double Y) q = poly[(i + 1) % poly.Count];
            a += p.X * q.Y - q.X * p.Y;
        }

        return Math.Abs(a) / 2;
    }

    /// <summary>
    /// Minimum-area enclosing rectangle of a convex hull (rotating calipers over hull edges).
    /// The angle is normalized to (-45, 45] degrees, so the object keeps its rough orientation.
    /// </summary>
    public static RotatedRect MinAreaRect(IReadOnlyList<(double X, double Y)> hull)
    {
        if (hull.Count == 0)
            return default;

        double best = double.MaxValue;
        RotatedRect result = default;
        for (int i = 0; i < Math.Max(1, hull.Count); i++)
        {
            (double X, double Y) p = hull[i];
            (double X, double Y) q = hull[(i + 1) % hull.Count];
            double angle = hull.Count == 1 ? 0 : Math.Atan2(q.Y - p.Y, q.X - p.X);
            while (angle <= -Math.PI / 4)
                angle += Math.PI / 2;
            while (angle > Math.PI / 4)
                angle -= Math.PI / 2;

            double c = Math.Cos(angle), s = Math.Sin(angle);
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach ((double X, double Y) t in hull)
            {
                double u = t.X * c + t.Y * s;
                double v = -t.X * s + t.Y * c;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }

            double area = (maxU - minU) * (maxV - minV);
            if (area < best)
            {
                best = area;
                double mu = (minU + maxU) / 2, mv = (minV + maxV) / 2;
                result = new RotatedRect(mu * c - mv * s, mu * s + mv * c, maxU - minU, maxV - minV, angle * 180 / Math.PI);
            }
        }

        return result;
    }

    private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
}
