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

        // Photos join a sheet they lie next to, otherwise stand alone; never two photos together.
        foreach (BedObject photo in bed.Objects.Where(o => o.LooksLikePhoto))
        {
            // A speck of dust next to a photo must not turn the photo into a "sheet".
            Cluster? sheet = clusters
                .Where(c => !c.IsPhoto && c.Parts.Sum(p => p.Share) >= MinSheetShare)
                .FirstOrDefault(c => c.Box.GapTo(photo.Box) < gap);
            if (sheet is not null)
                sheet.Add(photo);
            else
                clusters.Add(new Cluster(photo) { IsPhoto = true });
        }

        return clusters
            .Where(c => c.IsPhoto || c.Parts.Sum(p => p.Share) >= MinSheetShare)
            .Select(c => c.ToItem())
            .ToList();
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
