using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniScan.Net.Imaging;

public enum ItemKind
{
    /// <summary>A photo, card or other solid picture: cut along its own (possibly rotated) edges.</summary>
    Photo,

    /// <summary>A sheet of text, a drawing, a form: cropped to its content with a margin.</summary>
    Document,
}

/// <summary>One thing the user put on the glass, made of one or more blobs.</summary>
public sealed record BedItem(ItemKind Kind, RotatedRect Bounds, PixelBox Box, IReadOnlyList<BedObject> Parts);

/// <summary>
/// Groups the blobs on the bed into the items the user put there:
/// - text and drawings closer than <see cref="ItemGap"/> to each other are one sheet;
/// - blocks stacked in one column at least <see cref="PageWidth"/> wide are one page however far apart
///   (a test with room for the answers: the white sheet's own edges do not show on a white lid);
/// - a photo with no other content within <see cref="ItemGap"/> is an item of its own,
///   while a photo next to text (an ID card, a letter with a picture) belongs to that sheet;
/// - two photos are never merged with each other, however close.
/// </summary>
public static class BedLayout
{
    /// <summary>Inches between separate items (about 13 mm).</summary>
    public const double ItemGap = 0.5;

    /// <summary>A lone sheet with less content than this share of the bed is a speck or a stray mark.</summary>
    public const double MinSheetShare = 0.003;

    /// <summary>
    /// Inches: blocks in one column this wide together are parts of one page. Receipts, cards and
    /// other slips are narrower, and slips side by side do not share a column.
    /// </summary>
    public const double PageWidth = 5.0;

    public static IReadOnlyList<BedItem> Group(BedAnalysis bed)
    {
        int gap = (int)(ItemGap * bed.Dpi);

        // Sheets: everything that is not a photo, chained by proximity (paragraphs, drawings).
        List<Cluster> clusters = bed.Objects.Where(o => !o.LooksLikePhoto).Select(o => new Cluster(o)).ToList();
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int a = 0; a < clusters.Count && !merged; a++)
            {
                for (int b = a + 1; b < clusters.Count && !merged; b++)
                {
                    if (clusters[a].GapTo(clusters[b]) >= gap)
                        continue;
                    foreach (BedObject part in clusters[b].Parts)
                        clusters[a].Add(part);
                    clusters.RemoveAt(b);
                    merged = true;
                }
            }
        }

        // Blocks of one page: stacked in a common column, the page wide together. Specks do not count.
        int pageWidth = (int)(PageWidth * bed.Dpi);
        merged = true;
        while (merged)
        {
            merged = false;
            for (int a = 0; a < clusters.Count && !merged; a++)
            {
                for (int b = a + 1; b < clusters.Count && !merged; b++)
                {
                    if (!IsBlock(clusters[a]) || !IsBlock(clusters[b]) || !OneColumn(clusters[a].Box, clusters[b].Box, pageWidth))
                        continue;
                    foreach (BedObject part in clusters[b].Parts)
                        clusters[a].Add(part);
                    clusters.RemoveAt(b);
                    merged = true;
                }
            }
        }

        // Photos join the sheets they lie next to (all of them, which then become one sheet),
        // otherwise stand alone; never two photos together. First a photo takes back the pieces of
        // itself that a white area cut off (a white sky or table running into the white lid).
        foreach (BedObject found in bed.Objects.Where(o => o.LooksLikePhoto))
        {
            BedObject photo = found;
            foreach (Cluster piece in clusters.Where(c => !c.IsPhoto && IsPieceOf(c, photo, gap)).ToList())
            {
                photo = Absorb(photo, piece.Parts);
                clusters.Remove(piece);
            }

            // A speck of dust next to a photo must not turn the photo into a "sheet".
            List<Cluster> sheets = clusters
                .Where(c => !c.IsPhoto && c.Parts.Sum(p => p.Share) >= MinSheetShare && c.GapTo(photo) < gap)
                .ToList();
            if (sheets.Count == 0)
            {
                clusters.Add(new Cluster(photo) { IsPhoto = true });
                continue;
            }

            sheets[0].Add(photo);
            foreach (Cluster other in sheets.Skip(1))
            {
                foreach (BedObject part in other.Parts)
                    sheets[0].Add(part);
                clusters.Remove(other);
            }
        }

        return clusters
            .Where(c => c.IsPhoto || c.Parts.Sum(p => p.Share) >= MinSheetShare)
            .Select(c => c.ToItem())
            .ToList();
    }

    /// <summary>Degrees within which a piece lies "the same way" as the photo.</summary>
    private const double PieceAngle = 4;

    /// <summary>
    /// A piece of a photo cut off by a white area: close to it, smaller than half of it, turned the
    /// same way (its parts' sides parallel to the photo's) and lying in the band of the photo's sides,
    /// as if the photo went on. A note or a receipt next to a photo is turned another way or lies
    /// beside it; a sheet with a photo on it (an ID card, a letter) is bigger than the photo.
    /// </summary>
    private static bool IsPieceOf(Cluster piece, BedObject photo, int gap)
    {
        if (piece.GapTo(photo) >= gap || piece.Parts.Sum(p => p.Share) > photo.Share / 2)
            return false;
        foreach (BedObject part in piece.Parts)
        {
            double turn = Math.Abs(part.Bounds.Angle - photo.Bounds.Angle) % 90;
            if (Math.Min(turn, 90 - turn) > PieceAngle && Math.Min(part.Bounds.Width, part.Bounds.Height) > gap)
                return false;   // thin slivers have no reliable angle
        }

        // In the band of the photo's width or of its height: projected on that axis, the piece lies
        // within the photo's extent.
        (double U0, double U1, double V0, double V1) p = Extent(photo.Bounds, photo.Bounds.Corners());
        (double U0, double U1, double V0, double V1) q = Extent(photo.Bounds, piece.Parts.SelectMany(x => x.Bounds.Corners()).ToArray());
        double slack = gap / 2.0;
        bool inWidth = q.U0 >= p.U0 - slack && q.U1 <= p.U1 + slack;
        bool inHeight = q.V0 >= p.V0 - slack && q.V1 <= p.V1 + slack;
        return inWidth || inHeight;
    }

    /// <summary>The photo grown to cover the pieces, keeping its angle.</summary>
    private static BedObject Absorb(BedObject photo, IReadOnlyList<BedObject> pieces)
    {
        (double X, double Y)[] corners = [.. photo.Bounds.Corners(), .. pieces.SelectMany(p => p.Bounds.Corners())];
        (double u0, double u1, double v0, double v1) = Extent(photo.Bounds, corners);
        double rad = photo.Bounds.Angle * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
        double cu = (u0 + u1) / 2, cv = (v0 + v1) / 2;
        RotatedRect grown = new(cu * c - cv * s, cu * s + cv * c, u1 - u0, v1 - v0, photo.Bounds.Angle);
        PixelBox box = pieces.Aggregate(photo.Box, (b, p) => b.Union(p.Box));
        return photo with { Bounds = grown, Box = box, Share = photo.Share + pieces.Sum(p => p.Share) };
    }

    /// <summary>Extents of points along the rectangle's own axes (u along its width, v along its height).</summary>
    private static (double U0, double U1, double V0, double V1) Extent(RotatedRect r, (double X, double Y)[] points)
    {
        double rad = r.Angle * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
        foreach ((double x, double y) in points)
        {
            double u = x * c + y * s, v = -x * s + y * c;
            u0 = Math.Min(u0, u);
            u1 = Math.Max(u1, u);
            v0 = Math.Min(v0, v);
            v1 = Math.Max(v1, v);
        }

        return (u0, u1, v0, v1);
    }

    private static bool IsBlock(Cluster c) => c.Parts.Sum(p => p.Share) >= MinSheetShare;

    /// <summary>
    /// The two boxes overlap across at least half of the narrower one (one under the other, a common
    /// margin) and together are at least <paramref name="width"/> wide.
    /// </summary>
    private static bool OneColumn(PixelBox a, PixelBox b, int width)
    {
        int overlap = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        int span = Math.Max(a.Right, b.Right) - Math.Min(a.X, b.X);
        return overlap * 2 >= Math.Min(a.Width, b.Width) && span >= width;
    }

    private sealed class Cluster
    {
        private readonly List<BedObject> _parts = [];

        public Cluster(BedObject first)
        {
            _parts.Add(first);
            Box = first.Box;
        }

        public bool IsPhoto { get; init; }
        public PixelBox Box { get; private set; }
        public IReadOnlyList<BedObject> Parts => _parts;

        /// <summary>
        /// The gap to another thing on the glass, between the outlines of their parts (rotated rectangles),
        /// not their bounding boxes: those of a print at an angle reach far past its corners.
        /// </summary>
        public double GapTo(Cluster other) => _parts.Min(p => other._parts.Min(q => Geometry.Distance(p.Bounds, q.Bounds)));

        public double GapTo(BedObject o) => _parts.Min(p => Geometry.Distance(p.Bounds, o.Bounds));

        public void Add(BedObject o)
        {
            _parts.Add(o);
            Box = Box.Union(o.Box);
        }

        public BedItem ToItem()
        {
            if (IsPhoto)
                return new BedItem(ItemKind.Photo, _parts[0].Bounds, Box, _parts);
            RotatedRect axis = new(Box.X + Box.Width / 2.0, Box.Y + Box.Height / 2.0, Box.Width, Box.Height, 0);
            return new BedItem(ItemKind.Document, axis, Box, _parts);
        }
    }
}
