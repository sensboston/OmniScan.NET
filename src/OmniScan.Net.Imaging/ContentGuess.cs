using System;
using System.Collections.Generic;
using System.Linq;
using Leptonica;

namespace OmniScan.Net.Imaging;

/// <summary>What a scan most likely shows, for a hint about the output format.</summary>
public enum ContentKind
{
    /// <summary>Not sure: an empty bed, a drawing, a magazine page. No hint is given.</summary>
    Unknown,

    /// <summary>A sheet with lines of text: a letter, a bill (also in color), a form.</summary>
    Document,

    /// <summary>One or more photos or cards lying on the glass.</summary>
    Photos,
}

/// <summary>
/// Tells a document from photos: photos are solid rectangles standing apart on the glass, a document
/// is a sheet with many lines of text (also in color: bills often are). A sheet with fewer lines
/// (handwriting, formulas, a form, a sparse test) is a document too when it is colorless and lightly
/// covered, as printed and written pages are. When neither is clear the answer is <see cref="ContentKind.Unknown"/>.
/// </summary>
public static class ContentGuess
{
    /// <summary>Text lines needed to call a sheet a document.</summary>
    public const int MinTextLines = 8;

    private const int WorkDpi = 150;
    private const double MinLineHeight = 0.04;   // inches: about 3 pt
    private const double MaxLineHeight = 0.35;   // inches: a large heading
    private const double MinLineWidth = 0.6;     // inches
    private const double MinLineAspect = 5;

    // A colorless, lightly covered sheet (measured on the corpus: printed pages 2-15% covered and
    // 0-2% colored; photos 65-94% and 33-65%; a child's drawing 20% and 77%).
    private const double MinInk = 0.005;
    private const double MaxInk = 0.30;
    private const double MaxColorShare = 0.10;
    private const double MinInkSheetSide = 1.5;   // inches: not a caption strip or a label

    public static ContentKind Classify(Pix page) =>
        Classify(page, BedLayout.Group(MarkText(page, BedAnalyzer.Analyze(page))));

    /// <summary>
    /// Marks the solid-looking blobs that are full of text lines (<see cref="BedObject.HasText"/>), so that
    /// a colored bill or a sheet darker than the lid is kept whole instead of being cut out as a photo.
    /// </summary>
    public static BedAnalysis MarkText(Pix page, BedAnalysis bed)
    {
        if (!bed.Objects.Any(o => o.LooksLikePhoto))
            return bed;
        List<BedObject> marked = bed.Objects
            .Select(o => o.LooksLikePhoto && CountTextLines(page, o.Box) >= MinTextLines ? o with { HasText = true } : o)
            .ToList();
        return bed with { Objects = marked };
    }

    /// <summary>Same, reusing the items found by <see cref="BedLayout.Group"/>.</summary>
    public static ContentKind Classify(Pix page, IReadOnlyList<BedItem> items)
    {
        if (items.Count == 0)
            return ContentKind.Unknown;
        if (items.Any(i => i.Kind == ItemKind.Photo))
            return ContentKind.Photos;

        PixelBox sheet = items.Select(i => i.Box).Aggregate((a, b) => a.Union(b));
        if (CountTextLines(page, sheet) >= MinTextLines)
            return ContentKind.Document;

        int dpi = page.XRes > 0 ? page.XRes : 300;
        if (Math.Min(sheet.Width, sheet.Height) < MinInkSheetSide * dpi)
            return ContentKind.Unknown;
        (double coverage, double color) = Ink(page, sheet);
        return coverage is >= MinInk and <= MaxInk && color <= MaxColorShare ? ContentKind.Document : ContentKind.Unknown;
    }

    /// <summary>
    /// How much of <paramref name="area"/> is covered by anything darker than paper (<c>Coverage</c>), and
    /// which share of that is colored (<c>ColorShare</c>). Measured at a low resolution.
    /// </summary>
    public static (double Coverage, double ColorShare) Ink(Pix page, PixelBox area)
    {
        int dpi = page.XRes > 0 ? page.XRes : 300;
        Pix? clip = page.ClipRectangle(Box.Create(area.X, area.Y, area.Width, area.Height), out _);
        if (clip is null)
            return (0, 0);
        float k = Math.Min(1f, 75f / dpi);
        Pix work = clip.Depth == 32 ? clip : clip.ConvertTo32();
        if (k < 1f)
            work = work.ScaleAreaMap(k, k);

        uint[] data = work.Data;
        int wpl = work.Wpl, w = work.Width, h = work.Height;
        long content = 0, colored = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                uint v = data[y * wpl + x];
                int r = (int)(v >> 24), g = (int)((v >> 16) & 0xFF), bl = (int)((v >> 8) & 0xFF);
                int max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
                if (max >= 200 && min >= 200)
                    continue;   // paper (or the lid)
                content++;
                if (max - min > 40)
                    colored++;
            }
        }

        return ((double)content / Math.Max(1, (long)w * h), content == 0 ? 0 : (double)colored / content);
    }

    /// <summary>
    /// Lines of text inside <paramref name="area"/>: letters closed into horizontal bars of
    /// text height. Solid blocks (a colored banner, a picture) become tall blobs and do not count.
    /// </summary>
    public static int CountTextLines(Pix page, PixelBox area)
    {
        int dpi = page.XRes > 0 ? page.XRes : 300;
        Pix? clip = page.ClipRectangle(Box.Create(area.X, area.Y, area.Width, area.Height), out _);
        if (clip is null)
            return 0;

        float k = Math.Min(1f, (float)WorkDpi / dpi);
        Pix work = clip.Depth == 1 ? clip.ConvertTo8(false) : clip;
        if (k < 1f)
            work = work.ScaleAreaMap(k, k);
        int wdpi = (int)Math.Round(dpi * k);

        Pix binary = PageTools.Binarize(work);
        int join = Math.Max(3, wdpi / 8);    // ~3 mm: letters and words into one bar
        Pix bars = binary.MorphCompSequence($"c{join}.1 + o{Math.Max(3, join / 2)}.1", 0);
        Boxa boxa = bars.ConnComp(8);

        double minH = MinLineHeight * wdpi, maxH = MaxLineHeight * wdpi, minW = MinLineWidth * wdpi;
        int lines = 0;
        for (int i = 0; i < boxa.Count; i++)
        {
            boxa.GetBoxGeometry(i, out _, out _, out int bw, out int bh);
            if (bh >= minH && bh <= maxH && bw >= minW && bw >= MinLineAspect * bh)
                lines++;
        }

        return lines;
    }
}
