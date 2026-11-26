using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Leptonica;
using OmniScan.Net.Core;

namespace OmniScan.Net.Imaging;

/// <summary>
/// One page of a PDF being scanned. Kept losslessly compressed (PNG) so that it can still be turned,
/// with a small thumbnail and a tiny gray signature for telling whether two scans show the same sheet.
/// </summary>
public sealed class PdfPage
{
    internal PdfPage(byte[] png, int dpi, byte[] thumbnailBmp, double[] signature)
    {
        Png = png;
        Dpi = dpi;
        ThumbnailBmp = thumbnailBmp;
        Signature = signature;
    }

    public byte[] Png { get; }
    public int Dpi { get; }
    public byte[] ThumbnailBmp { get; }
    internal double[] Signature { get; }

    /// <summary>The page as scanned, while <see cref="Png"/> has the paper made white; null otherwise.</summary>
    internal byte[]? PlainPng { get; init; }

    /// <summary>The paper of this page is made white (it can be undone: the page as scanned is kept).</summary>
    public bool IsWhitePaper => PlainPng is not null;
}

/// <summary>What came off the glass for a PDF page.</summary>
public enum PageCheck
{
    /// <summary>A new page.</summary>
    Page,

    /// <summary>Nothing on the glass.</summary>
    Empty,

    /// <summary>The same sheet as the previous page (it was not changed in time).</summary>
    Unchanged,
}

/// <summary>Scanning into a multi-page PDF: whole pages, deskewed, of the same size, assembled at the end.</summary>
public static class PdfPages
{
    public const int ThumbnailSide = 240;

    /// <summary>Pages whose signatures correlate at least this much are the same sheet scanned twice.</summary>
    public const double SameSheetCorrelation = 0.97;

    /// <summary>Width of the band along the edges of the glass that is painted white (its dark strips), inches.</summary>
    private const double EdgeBand = 0.08;

    private const int SignatureWidth = 48;
    private const int SignatureHeight = 64;

    /// <summary>
    /// Makes a page from a whole-bed frame: rotated as the scanner is mounted, edge strips whitened,
    /// straightened and turned upright by its text if enabled. Never cropped, so all pages have the bed's size.
    /// </summary>
    public static (PdfPage? Page, PageCheck Check) Make(ScannedFrame frame, int rotation, bool deskew, bool autoOrient,
        PdfPage? previous, bool allowBlank = false, bool whitePaper = false)
    {
        Pix page = ScanSaver.Prepare(frame, rotation);
        if (!allowBlank && BedAnalyzer.Analyze(page).Objects.Count == 0)
            return (null, PageCheck.Empty);

        page = PageTools.WhitenBorder(page, EdgeBand);
        PipelineResult result = ScanPipeline.Cut(page, [], new PipelineOptions(false, false, deskew, autoOrient));
        Pix done = result.Images[0];

        double[] signature = Signature(done);
        if (previous is not null && Correlation(signature, previous.Signature) >= SameSheetCorrelation)
            return (null, PageCheck.Unchanged);

        int dpi = done.XRes > 0 ? done.XRes : frame.Dpi;
        return (Create(done, dpi, signature, whitePaper), PageCheck.Page);
    }

    /// <summary>A page from the image as scanned (and straightened), with its paper made white if asked.</summary>
    private static PdfPage Create(Pix plain, int dpi, double[] signature, bool whitePaper)
    {
        byte[] plainPng = Pix.WriteMemPng(plain, 0);
        if (!whitePaper)
            return new PdfPage(plainPng, dpi, FrameCodec.PreviewBmp(plain, ThumbnailSide), signature);
        Pix white = PageTools.WhitenPaper(plain);
        return new PdfPage(Pix.WriteMemPng(white, 0), dpi, FrameCodec.PreviewBmp(white, ThumbnailSide), signature) { PlainPng = plainPng };
    }

    /// <summary>The page with its paper made white, or back as scanned (the same page when it is so already).</summary>
    public static PdfPage WithWhitePaper(PdfPage page, bool whitePaper)
    {
        if (page.IsWhitePaper == whitePaper)
            return page;
        if (whitePaper)
            return Create(Decode(page), page.Dpi, page.Signature, true);
        Pix plain = Pix.ReadMemPng(page.PlainPng!);
        return new PdfPage(page.PlainPng!, page.Dpi, FrameCodec.PreviewBmp(plain, ThumbnailSide), page.Signature);
    }

    /// <summary>The page turned by a multiple of 90 degrees (lossless); a page with white paper keeps it.</summary>
    public static PdfPage Rotate(PdfPage page, int degrees)
    {
        int turn = ((degrees % 360) + 360) % 360;
        Pix turned = FrameCodec.Rotate(Decode(page), turn);
        byte[]? plain = page.PlainPng is { } p ? Pix.WriteMemPng(FrameCodec.Rotate(Pix.ReadMemPng(p), turn), 0) : null;
        return new PdfPage(Pix.WriteMemPng(turned, 0), page.Dpi, FrameCodec.PreviewBmp(turned, ThumbnailSide), Signature(turned))
        {
            PlainPng = plain,
        };
    }

    /// <summary>A larger view of a page for the preview pane.</summary>
    public static byte[] PreviewBmp(PdfPage page) => FrameCodec.PreviewBmp(Decode(page), ScanSaver.PreviewMaxSide);

    /// <summary>
    /// Writes the pages as one new PDF into the day folder; returns its path. <paramref name="text"/> holds
    /// the recognized words of each page (null for a page without them): an invisible text layer.
    /// </summary>
    public static string Save(IReadOnlyList<PdfPage> pages, string rootFolder, DateTime date, int jpegQuality,
        IReadOnlyList<IReadOnlyList<TextWord>?>? text = null)
    {
        byte[] pdf = Build(pages, jpegQuality, text, null);
        string folder = ScanOutput.DayFolder(rootFolder, date);
        using FileStream file = ScanOutput.CreateNext(folder, ".pdf");
        file.Write(pdf);
        return file.Name;
    }

    /// <summary>Writes the pages over an existing PDF made by this program (adding pages to it later).</summary>
    /// <param name="title">The document's title (shown by viewers on their tab or title bar).</param>
    public static void SaveTo(IReadOnlyList<PdfPage> pages, string path, int jpegQuality,
        IReadOnlyList<IReadOnlyList<TextWord>?>? text = null, string? title = null)
    {
        byte[] pdf = Build(pages, jpegQuality, text, title);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, pdf);
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] Build(IReadOnlyList<PdfPage> pages, int jpegQuality, IReadOnlyList<IReadOnlyList<TextWord>?>? text,
        string? title)
    {
        if (pages.Count == 0)
            throw new ArgumentException("No pages.", nameof(pages));

        // Pages are decoded one at a time, as the writer comes to them: a 300 dpi color page is ~27 MB unpacked.
        IEnumerable<PdfSheet> sheets = pages
            .Select((page, i) => new PdfSheet(Decode(page), page.Dpi, text is not null && i < text.Count ? text[i] : null));
        return PdfWriter.Write(sheets, jpegQuality, title);
    }

    /// <summary>The page image as it goes into the PDF (and to the text recognition).</summary>
    public static Pix Decode(PdfPage page)
    {
        Pix pix = Pix.ReadMemPng(page.Png);
        pix.SetResolution(page.Dpi, page.Dpi);
        return pix;
    }

    /// <summary>A coarse gray thumbnail as numbers: enough to tell two sheets apart, blind to scanner noise.</summary>
    internal static double[] Signature(Pix page)
    {
        Pix small = page.ScaleToSize(SignatureWidth, SignatureHeight);
        byte[] gray = new byte[SignatureWidth * SignatureHeight];
        byte[] sat = new byte[SignatureWidth * SignatureHeight];
        PixelAccess.ReadGrayAndSaturation(small.Depth == 1 ? small.ConvertTo8(false) : small, gray, sat);
        double[] s = new double[gray.Length];
        for (int i = 0; i < s.Length; i++)
            s[i] = gray[i];
        return s;
    }

    /// <summary>Pearson correlation of two signatures; two blank pages count as the same.</summary>
    internal static double Correlation(double[] a, double[] b)
    {
        if (a.Length != b.Length)
            return 0;
        double ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            ma += a[i];
            mb += b[i];
        }

        ma /= a.Length;
        mb /= b.Length;
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db;
            saa += da * da;
            sbb += db * db;
        }

        if (saa < 1e-6 || sbb < 1e-6)
            return Math.Abs(ma - mb) < 8 ? 1 : 0;
        return sab / Math.Sqrt(saa * sbb);
    }
}
