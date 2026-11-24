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
/// Tells a document from photos without looking at colors (bills are often colorful):
/// photos are solid rectangles standing apart on the glass, a document is a sheet with
/// many lines of text. When neither is clear the answer is <see cref="ContentKind.Unknown"/>.
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

    public static ContentKind Classify(Pix page) => Classify(page, BedLayout.Group(BedAnalyzer.Analyze(page)));

    /// <summary>Same, reusing the items found by <see cref="BedLayout.Group"/>.</summary>
    public static ContentKind Classify(Pix page, IReadOnlyList<BedItem> items)
    {
        if (items.Count == 0)
            return ContentKind.Unknown;
        if (items.Any(i => i.Kind == ItemKind.Photo))
            return ContentKind.Photos;

        PixelBox sheet = items.Select(i => i.Box).Aggregate((a, b) => a.Union(b));
        return CountTextLines(page, sheet) >= MinTextLines ? ContentKind.Document : ContentKind.Unknown;
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
