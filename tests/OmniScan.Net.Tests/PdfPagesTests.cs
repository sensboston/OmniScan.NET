using System;
using System.IO;
using Leptonica;
using OmniScan.Net.Core;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.Tests;

public sealed class PdfPagesTests
{
    private const int Dpi = 100;

    /// <summary>A letter-size bed with dark glass-edge strips and, optionally, "text" bars in a pattern.</summary>
    private static ScannedFrame Sheet(int pattern)
    {
        int w = (int)(8.5 * Dpi), h = (int)(11.7 * Dpi);
        Pix pix = Pix.Create(w, h, 32);
        uint[] data = pix.Data;
        int wpl = pix.Wpl;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool edge = x < 3 || y > h - 4;
                bool text = pattern > 0 && x > 80 && x < w - 80 && y > 100 && y < h - 150
                            && ((y / 14) % 2 == 0) && (((x + y / 14 * pattern * 37) / 40) % 3 != 0);
                uint g = edge ? 40u : text ? 30u : 250u;
                data[y * wpl + x] = (g << 24) | (g << 16) | (g << 8);
            }
        }

        return new ScannedFrame(Pix.WriteMemBmp(pix), "image/bmp", Dpi);
    }

    [Fact]
    public void EmptyGlass_IsReportedAsEmpty()
    {
        (PdfPage? page, PageCheck check) = PdfPages.Make(Sheet(0), 0, true, true, null);
        Assert.Null(page);
        Assert.Equal(PageCheck.Empty, check);
    }

    [Fact]
    public void EmptyGlass_CanBeAddedAnyway()
    {
        (PdfPage? page, PageCheck check) = PdfPages.Make(Sheet(0), 0, true, true, null, allowBlank: true);
        Assert.NotNull(page);
        Assert.Equal(PageCheck.Page, check);
    }

    [Fact]
    public void SameSheetTwice_IsUnchanged_DifferentSheet_IsANewPage()
    {
        (PdfPage? first, PageCheck c1) = PdfPages.Make(Sheet(1), 0, false, false, null);
        (PdfPage? again, PageCheck c2) = PdfPages.Make(Sheet(1), 0, false, false, first);
        (PdfPage? other, PageCheck c3) = PdfPages.Make(Sheet(2), 0, false, false, first);

        Assert.Equal(PageCheck.Page, c1);
        Assert.NotNull(first);
        Assert.Equal(PageCheck.Unchanged, c2);
        Assert.Null(again);
        Assert.Equal(PageCheck.Page, c3);
        Assert.NotNull(other);
    }

    [Fact]
    public void Page_KeepsTheWholeBedAndWhitensTheEdges()
    {
        (PdfPage? page, _) = PdfPages.Make(Sheet(1), 0, false, false, null);
        Pix pix = Pix.ReadMemPng(page!.Png);

        Assert.Equal((int)(8.5 * Dpi), pix.Width);
        Assert.Equal((int)(11.7 * Dpi), pix.Height);
        pix.GetRGBPixel(1, 500, out int r, out _, out _);
        Assert.Equal(255, r);
    }

    [Fact]
    public void Rotate_SwapsSides()
    {
        (PdfPage? page, _) = PdfPages.Make(Sheet(1), 0, false, false, null);
        PdfPage turned = PdfPages.Rotate(page!, 90);
        Pix pix = Pix.ReadMemPng(turned.Png);
        Assert.Equal((int)(11.7 * Dpi), pix.Width);
    }

    [Fact]
    public void Save_WritesOneMultiPagePdf()
    {
        using TempFolder temp = new();
        (PdfPage? a, _) = PdfPages.Make(Sheet(1), 0, false, false, null);
        (PdfPage? b, _) = PdfPages.Make(Sheet(2), 0, false, false, a);
        (PdfPage? c, _) = PdfPages.Make(Sheet(3), 0, false, false, b);

        string path = PdfPages.Save([a!, b!, c!], temp.Path, new DateTime(2026, 11, 23), 90);

        Assert.Equal(Path.Combine(temp.Path, "2026-11-23", "scan_001.pdf"), path);
        Assert.Equal(3, PdfIo.GetPageCount(path));
    }

    [Fact]
    public void SaveTo_OverwritesTheSamePdfWithMorePages()
    {
        using TempFolder temp = new();
        (PdfPage? a, _) = PdfPages.Make(Sheet(1), 0, false, false, null);
        (PdfPage? b, _) = PdfPages.Make(Sheet(2), 0, false, false, a);
        string path = PdfPages.Save([a!], temp.Path, new DateTime(2026, 11, 23), 90);

        PdfPages.SaveTo([a!, b!], path, 90);

        Assert.Equal(2, PdfIo.GetPageCount(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    /// <summary>The same sheet printed on yellow paper.</summary>
    private static ScannedFrame YellowSheet()
    {
        Pix pix = Pix.ReadMem(Sheet(3).Data);
        uint[] data = pix.Data;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] >> 24 == 250)
                data[i] = 0xF5DC3C00u;
        }

        return new ScannedFrame(Pix.WriteMemBmp(pix), "image/bmp", Dpi);
    }

    [Fact]
    public void WhitePaper_IsSwitchedOnAndOff_WithoutScanningAgain()
    {
        PdfPage plain = PdfPages.Make(YellowSheet(), 0, false, false, null).Page!;
        PdfPage white = PdfPages.Make(YellowSheet(), 0, false, false, null, whitePaper: true).Page!;

        Assert.False(plain.IsWhitePaper);
        Assert.True(white.IsWhitePaper);
        Assert.Equal(8, PdfPages.Decode(white).Depth);   // colored paper: gray, like a photocopy

        PdfPage back = PdfPages.WithWhitePaper(white, false);
        Assert.False(back.IsWhitePaper);
        Assert.Equal(plain.Png, back.Png);

        PdfPage again = PdfPages.WithWhitePaper(back, true);
        Assert.True(again.IsWhitePaper);
        Assert.Same(again, PdfPages.WithWhitePaper(again, true));
    }

    [Fact]
    public void WhitePaper_IsKept_WhenThePageIsTurned()
    {
        PdfPage white = PdfPages.Make(YellowSheet(), 0, false, false, null, whitePaper: true).Page!;

        PdfPage turned = PdfPages.Rotate(white, 90);
        PdfPage back = PdfPages.WithWhitePaper(turned, false);

        Assert.True(turned.IsWhitePaper);
        Pix plain = PdfPages.Decode(back);
        Assert.Equal(PdfPages.Decode(white).Height, plain.Width);
        Assert.Equal(32, plain.Depth);
    }
}
