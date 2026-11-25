using System;
using System.Collections.Generic;
using System.Linq;
using Leptonica;

namespace OmniScan.Net.Imaging;

/// <summary>Which processing steps are on (the checkboxes of the main window and the settings).</summary>
public sealed record PipelineOptions(bool AutoCrop, bool SplitPhotos, bool Deskew, bool AutoOrient)
{
    public static readonly PipelineOptions None = new(false, false, false, false);
    public static readonly PipelineOptions All = new(true, true, true, true);
}

public enum PageKind
{
    Empty,
    Document,
    Photo,
    Photos,
}

/// <summary>
/// One cut to make on the page: where, and how to treat the piece.
/// <see cref="Manual"/> frames were drawn or adjusted by the user and are cut exactly as drawn.
/// </summary>
public sealed record CutFrame(RotatedRect Rect, ItemKind Kind, bool Manual = false)
{
    /// <summary>The frame in page-width units, so that it carries over between resolutions (preview → scan).</summary>
    public CutFrame Normalize(int pageWidth) => this with { Rect = Rect.Scale(1.0 / pageWidth) };

    public CutFrame Denormalize(int pageWidth) => this with { Rect = Rect.Scale(pageWidth) };
}

/// <summary>The images to save and the frames they were cut from (for the preview).</summary>
public sealed record PipelineResult(PageKind Kind, IReadOnlyList<Pix> Images, IReadOnlyList<CutFrame> Frames)
{
    public IReadOnlyList<RotatedRect> Outlines => Frames.Select(f => f.Rect).ToList();
}

/// <summary>
/// Turns a whole-bed scan into what the user put on the glass, in two steps:
/// <see cref="Plan"/> decides the frames (one per photo or sheet, or one around everything), and
/// <see cref="Cut"/> cuts them out: photos straightened along their edges, sheets deskewed by their
/// text lines, everything turned upright by its text when that is certain. The user can adjust the
/// planned frames between the two steps.
/// </summary>
public static class ScanPipeline
{
    /// <summary>Margin kept around document content, inches.</summary>
    private const double DocumentMargin = 0.2;

    /// <summary>Trimmed from every side of a detected photo, inches (the light fringe along its edge).</summary>
    private const double PhotoTrim = 0.02;

    /// <summary>Photos are turned by their text only when it is unmistakable.</summary>
    private const float PhotoTextConfidence = 15.0f;

    public static PipelineResult Process(Pix page, PipelineOptions options) => Cut(page, Plan(page, options), options);

    /// <summary>Decides what to cut. An empty list means: keep the page as it is.</summary>
    public static IReadOnlyList<CutFrame> Plan(Pix page, PipelineOptions options)
    {
        if (options == PipelineOptions.None)
            return [];

        int dpi = page.XRes > 0 ? page.XRes : 300;
        IReadOnlyList<BedItem> items = BedLayout.Group(ContentGuess.MarkText(page, BedAnalyzer.Analyze(page)));
        if (items.Count == 0)
            return [];

        // Several things on the glass: one frame each.
        if (items.Count > 1 && options.SplitPhotos)
            return OrderForReading(items).Select(i => FrameOf(i, page, dpi)).ToList();

        if (!options.AutoCrop)
            return [];

        // A single photo or card, or a document (possibly several things kept together on purpose).
        if (items.Count == 1)
            return [FrameOf(items[0], page, dpi)];
        PixelBox all = items.Select(i => i.Box).Aggregate((a, b) => a.Union(b));
        return [SheetFrame(all, page, dpi)];
    }

    /// <summary>Cuts the frames out of the page and finishes each piece.</summary>
    public static PipelineResult Cut(Pix page, IReadOnlyList<CutFrame> frames, PipelineOptions options)
    {
        if (frames.Count == 0)
        {
            Pix whole = page;
            if (options.Deskew)
                whole = PageTools.RotateSmall(whole, PageTools.FindSkew(whole));
            if (options.AutoOrient && PageTools.FindTextRotation(whole) is int turn and not 0)
                whole = FrameCodec.Rotate(whole, turn);
            return new PipelineResult(options == PipelineOptions.None ? PageKind.Document : PageKind.Empty, [whole], []);
        }

        List<Pix> images = frames.Select(f => f.Kind == ItemKind.Photo ? CutPhoto(page, f, options) : CutSheet(page, f, options)).ToList();
        PageKind kind = frames.Count > 1 ? PageKind.Photos : frames[0].Kind == ItemKind.Photo ? PageKind.Photo : PageKind.Document;
        return new PipelineResult(kind, images, frames);
    }

    private static CutFrame FrameOf(BedItem item, Pix page, int dpi) =>
        item.Kind == ItemKind.Photo
            ? new CutFrame(item.Bounds.Shrink(PhotoTrim * dpi), ItemKind.Photo)
            : SheetFrame(item.Box, page, dpi);

    private static CutFrame SheetFrame(PixelBox box, Pix page, int dpi)
    {
        PixelBox c = WithMargin(box, (int)(DocumentMargin * dpi), page);
        return new CutFrame(new RotatedRect(c.X + c.Width / 2.0, c.Y + c.Height / 2.0, c.Width, c.Height, 0), ItemKind.Document);
    }

    private static Pix CutPhoto(Pix page, CutFrame frame, PipelineOptions options)
    {
        // A manual frame is straightened along the drawn rectangle whatever the Deskew option says:
        // the user rotated it on purpose.
        Pix image = PageTools.Extract(page, frame.Rect, options.Deskew || frame.Manual);
        if (options.AutoCrop && !frame.Manual)
            image = PageTools.TrimFringe(PageTools.TrimLightBorder(image), page.XRes > 0 ? page.XRes : 300);
        if (options.AutoOrient && PageTools.FindTextRotation(image, PhotoTextConfidence) is int turn and not 0)
            image = FrameCodec.Rotate(image, turn);
        return image;
    }

    /// <summary>A sheet: cut out with its margin, then straightened by its text lines and turned upright.</summary>
    private static Pix CutSheet(Pix page, CutFrame frame, PipelineOptions options)
    {
        Pix sheet = PageTools.Extract(page, frame.Rect);
        if (options.Deskew)
            sheet = PageTools.RotateSmall(sheet, PageTools.FindSkew(sheet));
        if (options.AutoOrient && PageTools.FindTextRotation(sheet) is int turn and not 0)
            sheet = FrameCodec.Rotate(sheet, turn);
        return sheet;
    }

    private static PixelBox WithMargin(PixelBox b, int m, Pix page)
    {
        int x = Math.Max(0, b.X - m), y = Math.Max(0, b.Y - m);
        int r = Math.Min(page.Width, b.Right + m), bottom = Math.Min(page.Height, b.Bottom + m);
        return new PixelBox(x, y, Math.Max(1, r - x), Math.Max(1, bottom - y));
    }

    /// <summary>Top to bottom, then left to right, so file numbers follow the layout on the glass.</summary>
    private static IEnumerable<BedItem> OrderForReading(IReadOnlyList<BedItem> items)
    {
        double rowTolerance = Math.Max(1, items.Min(o => Math.Min(o.Bounds.Width, o.Bounds.Height)) / 2);
        return items.OrderBy(o => Math.Round(o.Bounds.Cy / rowTolerance)).ThenBy(o => o.Bounds.Cx);
    }
}
