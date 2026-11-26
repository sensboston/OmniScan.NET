using System;
using System.Collections.Generic;
using System.Linq;
using Leptonica;

namespace OmniScan.Net.Imaging;

/// <summary>Which processing steps are on (the checkboxes of the main window and the settings).</summary>
/// <param name="Stickers">
/// Pictures that are not rectangles are cut along their outline with a white border, onto a transparent
/// background (see <see cref="Cutout"/>); photos and cards stay rectangles.
/// </param>
/// <param name="WhitePaper">
/// Sheets and pages printed on colored paper get a white background (photos and stickers keep their colors).
/// </param>
public sealed record PipelineOptions(bool AutoCrop, bool SplitPhotos, bool Deskew, bool AutoOrient, bool Stickers = false,
    bool WhitePaper = false)
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
    /// <summary>A sticker's outline, for the preview to show (the cut itself is decided again on the scan).</summary>
    public IReadOnlyList<(double X, double Y)>? Outline { get; init; }

    /// <summary>The frame in page-width units, so that it carries over between resolutions (preview → scan).</summary>
    public CutFrame Normalize(int pageWidth) => Scale(1.0 / pageWidth);

    public CutFrame Denormalize(int pageWidth) => Scale(pageWidth);

    private CutFrame Scale(double k) => this with
    {
        Rect = Rect.Scale(k),
        Outline = Outline?.Select(p => (p.X * k, p.Y * k)).ToList(),
    };
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
        if (options.Stickers)
            items = Separate(items, dpi);
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
            Pix whole = options.WhitePaper ? PageTools.WhitenPaper(page) : page;
            if (options.Deskew)
                whole = PageTools.RotateSmall(whole, PageTools.FindSkew(whole));
            if (options.AutoOrient && PageTools.FindTextRotation(whole) is int turn and not 0)
                whole = FrameCodec.Rotate(whole, turn);
            return new PipelineResult(options with { WhitePaper = false } == PipelineOptions.None ? PageKind.Document : PageKind.Empty, [whole], []);
        }

        // Stickers are looked for on a copy without the dark band along the edges of the glass, which
        // would otherwise join a sticker lying close to it.
        Pix shapes = options.Stickers ? WithoutGlassEdge(page) : page;
        List<Pix> images = frames.Select(f => f.Kind == ItemKind.Photo ? CutPhoto(page, shapes, f, options) : CutSheet(page, f, options)).ToList();
        PageKind kind = frames.Count > 1 ? PageKind.Photos : frames[0].Kind == ItemKind.Photo ? PageKind.Photo : PageKind.Document;
        return new PipelineResult(kind, images, frames);
    }

    /// <summary>
    /// The outline a frame would be cut along in the sticker mode, in page pixels; null when it is cut
    /// as a rectangle (a photo, a sheet, a frame drawn by hand).
    /// </summary>
    public static IReadOnlyList<(double X, double Y)>? OutlineOf(Pix page, CutFrame frame, PipelineOptions options)
    {
        if (!options.Stickers || frame.Manual || frame.Kind != ItemKind.Photo)
            return null;
        int dpi = page.XRes > 0 ? page.XRes : 300;
        RotatedRect around = frame.Rect.Shrink(-Cutout.SearchMargin * dpi);
        Pix region = PageTools.Extract(WithoutGlassEdge(page), around, options.Deskew);
        if (Cutout.Outline(region, dpi, (int)(Cutout.SearchMargin * dpi)) is not { } outline)
            return null;

        // Back from the cut-out piece to the page: turned back around the frame's center, or just shifted
        // when the piece was cut without turning.
        if (options.Deskew && Math.Abs(around.Angle) >= 0.05)
        {
            double rad = around.Angle * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
            return outline.Select(p =>
            {
                double u = p.X - region.Width / 2.0, v = p.Y - region.Height / 2.0;
                return (around.Cx + u * c - v * s, around.Cy + u * s + v * c);
            }).ToList();
        }

        (double X, double Y)[] corners = around.Corners();
        double x0 = Math.Round(Math.Max(0, corners.Min(p => p.X))), y0 = Math.Round(Math.Max(0, corners.Min(p => p.Y)));
        return outline.Select(p => (p.X + x0, p.Y + y0)).ToList();
    }

    /// <summary>
    /// Several stickers lie on the glass: at least two pictures that would be cut along their outline
    /// in the sticker mode (solid, not rectangles, on the white lid).
    /// </summary>
    public static bool LooksLikeStickers(Pix page)
    {
        // A preview-sized copy answers the same, many times faster.
        int dpi = page.XRes > 0 ? page.XRes : 300;
        if (dpi > LookDpi * 1.5)
        {
            double k = (double)LookDpi / dpi;
            page = page.ScaleToSize(Math.Max(1, (int)(page.Width * k)), Math.Max(1, (int)(page.Height * k)));
            page.SetResolution(LookDpi, LookDpi);
            dpi = LookDpi;
        }

        PipelineOptions options = new(true, true, true, false, Stickers: true);
        page = WithoutGlassEdge(page);
        IReadOnlyList<BedItem> items = Separate(BedLayout.Group(ContentGuess.MarkText(page, BedAnalyzer.Analyze(page))), dpi);
        int found = 0;
        foreach (BedItem item in items.Where(i => i.Kind == ItemKind.Photo))
        {
            CutFrame frame = FrameOf(item, page, dpi);
            if (TryCutout(page, frame.Rect.Shrink(-Cutout.SearchMargin * dpi), (int)(Cutout.SearchMargin * dpi), frame, options) is not null
                && ++found >= 2)
                return true;
        }

        return false;
    }

    /// <summary>Resolution at which the bed is looked at for stickers.</summary>
    private const int LookDpi = 75;

    /// <summary>Smallest sticker, inches: smaller parts of a group are its specks, not stickers.</summary>
    private const double MinSticker = 0.25;

    /// <summary>Share of its hull a sticker covers at least (stickers 0.75 and more, blocks of text 0.1 to 0.45).</summary>
    private const double StickerDensity = 0.6;

    /// <summary>
    /// Stickers: solid pictures that the layout takes for a sheet (a figure with gaps, or pictures close
    /// to each other) are taken one by one, each to be cut along its own outline.
    /// </summary>
    private static List<BedItem> Separate(IReadOnlyList<BedItem> items, int dpi)
    {
        List<BedItem> result = [];
        foreach (BedItem item in items)
        {
            // Stickers are solid pictures; a sheet has blocks of text or drawing that are mostly paper.
            if (item.Kind != ItemKind.Document
                || item.Parts.Any(p => Math.Min(p.Bounds.Width, p.Bounds.Height) >= MinSticker * dpi && p.Density < StickerDensity))
            {
                result.Add(item);
                continue;
            }

            foreach (BedObject part in item.Parts)
            {
                if (Math.Min(part.Bounds.Width, part.Bounds.Height) >= MinSticker * dpi)
                    result.Add(new BedItem(ItemKind.Photo, part.Bounds, part.Box, [part]));
            }
        }

        return result;
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

    /// <summary>Width of the dark band along the edges of the glass, inches (up to about 0.13 on the HP).</summary>
    private const double GlassEdge = 0.15;

    private static Pix WithoutGlassEdge(Pix page) => PageTools.WhitenBorder(page, GlassEdge);

    /// <param name="shapes">The page as stickers are looked for on it (see <see cref="WithoutGlassEdge"/>).</param>
    private static Pix CutPhoto(Pix page, Pix shapes, CutFrame frame, PipelineOptions options)
    {
        int dpi = page.XRes > 0 ? page.XRes : 300;
        if (TryCutout(shapes, frame.Rect.Shrink(-Cutout.SearchMargin * dpi), (int)(Cutout.SearchMargin * dpi), frame, options) is Pix shape)
            return shape;

        // A manual frame is straightened along the drawn rectangle whatever the Deskew option says:
        // the user rotated it on purpose.
        Pix image = PageTools.Extract(page, frame.Rect, options.Deskew || frame.Manual);

        // A whole sheet of colored paper looks solid, like a photo, to the layout: mostly one even color
        // (the paper), it is a sheet after all.
        if (options.WhitePaper && PageTools.LooksLikePaper(image))
            image = PageTools.WhitenPaper(image);
        if (options.AutoCrop && !frame.Manual)
            image = PageTools.TrimFringe(PageTools.TrimLightBorder(image), page.XRes > 0 ? page.XRes : 300);
        if (options.AutoOrient && PageTools.FindTextRotation(image, PhotoTextConfidence) is int turn and not 0)
            image = FrameCodec.Rotate(image, turn);
        return image;
    }

    /// <summary>
    /// A sticker or another shape that is not a rectangle, cut along its outline (see <see cref="Cutout"/>);
    /// null when cut-outs are off or the object is a rectangle (a photo, a sheet).
    /// </summary>
    /// <param name="margin">How far the object's own frame lies inside <paramref name="around"/>, pixels.</param>
    private static Pix? TryCutout(Pix page, RotatedRect around, int margin, CutFrame frame, PipelineOptions options)
    {
        if (!options.Stickers || frame.Manual)
            return null;
        int dpi = page.XRes > 0 ? page.XRes : 300;
        if (Cutout.Cut(PageTools.Extract(page, around, options.Deskew), dpi, margin) is not Pix shape)
            return null;
        if (options.AutoOrient && PageTools.FindTextRotation(shape, PhotoTextConfidence) is int turn and not 0)
            shape = FrameCodec.Rotate(shape, turn);
        return shape;
    }

    /// <summary>A sheet: cut out with its margin, then straightened by its text lines and turned upright.</summary>
    private static Pix CutSheet(Pix page, CutFrame frame, PipelineOptions options)
    {
        Pix sheet = PageTools.Extract(page, frame.Rect);
        if (options.WhitePaper)
            sheet = PageTools.WhitenPaper(sheet);
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
