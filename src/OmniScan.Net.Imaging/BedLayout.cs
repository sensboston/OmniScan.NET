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
                    if (clusters[a].Box.GapTo(clusters[b].Box) >= gap)
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
        // otherwise stand alone; never two photos together.
        foreach (BedObject photo in bed.Objects.Where(o => o.LooksLikePhoto))
        {
            // A speck of dust next to a photo must not turn the photo into a "sheet".
            List<Cluster> sheets = clusters
                .Where(c => !c.IsPhoto && c.Parts.Sum(p => p.Share) >= MinSheetShare && c.Box.GapTo(photo.Box) < gap)
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
