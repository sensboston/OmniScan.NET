using System;
using System.Collections.Generic;
using System.Linq;
using Leptonica;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.Tests;

/// <summary>Synthetic flatbed scans: a light lid with colored "photos" lying on it at various angles.</summary>
public sealed class PipelineTests
{
    private const int Dpi = 150;

    /// <summary>Letter-size bed at 150 dpi, lid gray level with an optional top-to-bottom gradient.</summary>
    private static Pix Bed(int lidTop = 250, int lidBottom = 250)
    {
        int w = (int)(8.5 * Dpi), h = (int)(11.7 * Dpi);
        Pix pix = Pix.Create(w, h, 32);
        pix.SetResolution(Dpi, Dpi);
        uint[] data = pix.Data;
        int wpl = pix.Wpl;
        for (int y = 0; y < h; y++)
        {
            uint g = (uint)(lidTop + (lidBottom - lidTop) * y / h);
            uint v = (g << 24) | (g << 16) | (g << 8);
            for (int x = 0; x < w; x++)
                data[y * wpl + x] = v;
        }

        return pix;
    }

    /// <summary>Paints a textured colored rectangle: center, size in inches, angle in degrees.</summary>
    private static void Photo(Pix bed, double cxIn, double cyIn, double wIn, double hIn, double angle, int seed = 1)
    {
        RotatedRect r = new(cxIn * Dpi, cyIn * Dpi, wIn * Dpi, hIn * Dpi, angle);
        double rad = angle * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
        Random random = new(seed);
        uint[] data = bed.Data;
        int wpl = bed.Wpl;
        for (int y = 0; y < bed.Height; y++)
        {
            for (int x = 0; x < bed.Width; x++)
            {
                double dx = x - r.Cx, dy = y - r.Cy;
                double u = dx * c + dy * s, v = -dx * s + dy * c;
                if (Math.Abs(u) > r.Width / 2 || Math.Abs(v) > r.Height / 2)
                    continue;
                uint red = (uint)(60 + random.Next(120)), green = (uint)(40 + random.Next(100)), blue = (uint)(90 + random.Next(120));
                data[y * wpl + x] = (red << 24) | (green << 16) | (blue << 8);
            }
        }
    }

    /// <summary>Paints "text": short dark bars in lines, sparse like a printed page.</summary>
    private static void TextBlock(Pix bed, double xIn, double yIn, double wIn, double hIn)
    {
        uint[] data = bed.Data;
        int wpl = bed.Wpl;
        int x0 = (int)(xIn * Dpi), y0 = (int)(yIn * Dpi), x1 = (int)((xIn + wIn) * Dpi), y1 = (int)((yIn + hIn) * Dpi);
        for (int line = y0; line + 8 < y1; line += 20)
        {
            for (int word = x0; word + 30 < x1; word += 45)
            {
                for (int y = line; y < line + 8; y++)
                {
                    for (int x = word; x < word + 30; x++)
                        data[y * wpl + x] = 0x20202000u;
                }
            }
        }
    }

    [Fact]
    public void PhotoAndSeparateSheet_AreTwoFiles()
    {
        Pix bed = Bed();
        Photo(bed, 4.2, 2.5, 6.0, 4.0, 2.0);
        TextBlock(bed, 1.5, 6.0, 5.5, 4.0);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(PageKind.Photos, result.Kind);
        Assert.Equal(2, result.Images.Count);
        Assert.InRange(result.Images[0].Width, 880, 910);              // the photo, first (upper)
        Assert.InRange(result.Images[1].Width, 5.5 * Dpi, 6.0 * Dpi);   // the sheet with its margin
    }

    [Fact]
    public void PhotoNextToText_StaysOnTheSheet()
    {
        // An ID card or a letter with a picture: the photo is part of the document.
        Pix bed = Bed();
        TextBlock(bed, 1.0, 1.0, 6.0, 8.0);
        Photo(bed, 3.0, 5.0, 2.0, 2.5, 0);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(PageKind.Document, result.Kind);
        Assert.Single(result.Images);
    }

    [Fact]
    public void Paragraphs_StayOnePage()
    {
        Pix bed = Bed();
        TextBlock(bed, 1.0, 1.0, 6.0, 2.0);
        TextBlock(bed, 1.0, 3.3, 6.0, 2.0);   // 0.3 in apart: the same sheet
        TextBlock(bed, 1.0, 5.6, 6.0, 2.0);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(PageKind.Document, result.Kind);
        Pix page = Assert.Single(result.Images);
        Assert.InRange(page.Height, 6.6 * Dpi, 7.1 * Dpi);
    }

    [Fact]
    public void TwoTiltedPhotos_WhoseBoundingBoxesOverlap_AreTwoPhotos()
    {
        // Laid "any old way": at 30 degrees, 0.7 in apart, their bounding boxes overlap by 0.6 in.
        Pix bed = Bed();
        Photo(bed, 4.25, 3.0, 5.0, 3.5, 30, seed: 1);
        Photo(bed, 4.25, 7.9, 5.0, 3.5, 30, seed: 2);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(PageKind.Photos, result.Kind);
        Assert.Equal(2, result.Images.Count);
        foreach (Pix photo in result.Images)
        {
            Assert.InRange(Math.Max(photo.Width, photo.Height), 4.85 * Dpi, 5.1 * Dpi);
            Assert.InRange(Math.Min(photo.Width, photo.Height), 3.35 * Dpi, 3.6 * Dpi);
        }
    }

    [Fact]
    public void LightFringeAlongAnEdge_IsTrimmed_AndAPhotoWithoutIsLeftAlone()
    {
        // A cut photo that kept the paper's edge along its top: a light gradient over a dark picture
        // (as the frame found on the small copy of the bed can leave at 300 dpi).
        Pix photo = Pix.Create(1800, 1200, 32);
        photo.SetAllArbitrary(0x30303000);
        uint[] data = photo.Data;
        int wpl = photo.Wpl;
        int[] fringe = [200, 171, 138, 85];
        for (int y = 0; y < fringe.Length; y++)
        {
            uint g = (uint)fringe[y], v = (g << 24) | (g << 16) | (g << 8);
            for (int x = 0; x < photo.Width; x++)
                data[y * wpl + x] = v;
        }

        Pix trimmed = PageTools.TrimFringe(photo, 300);

        Assert.Equal(1800, trimmed.Width);
        Assert.Equal(1200 - fringe.Length, trimmed.Height);
        Pix plain = Pix.Create(1800, 1200, 32);
        plain.SetAllArbitrary(0x30303000);
        Assert.Same(plain, PageTools.TrimFringe(plain, 300));
    }

    [Fact]
    public void PhotoWithAWhiteBand_TakesBackItsCutOffStrip()
    {
        // A product shot: the photo, then white (the table) that runs into the white lid, then a cable
        // along the bottom edge of the print. The strip is a piece of the photo, not a sheet beside it.
        Pix bed = Bed();
        Photo(bed, 4.25, 3.6, 6.0, 3.2, 0);
        TextBlock(bed, 2.0, 5.45, 4.0, 0.3);   // sparse, like a cable and its shadow

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(PageKind.Photo, result.Kind);
        Pix photo = Assert.Single(result.Images);
        Assert.InRange(photo.Height, 3.5 * Dpi, 3.8 * Dpi);
    }

    [Fact]
    public void TiltedPhoto_DoesNotJoinASheetInTheCornerOfItsBoundingBox()
    {
        // A note lying in the empty corner of a tilted photo's bounding box, 1.4 in from the photo itself.
        Pix bed = Bed();
        Photo(bed, 4.25, 3.0, 5.0, 3.5, 30);
        TextBlock(bed, 1.4, 5.0, 1.0, 0.5);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(2, result.Images.Count);
    }

    [Fact]
    public void SparseTestPage_StaysOnePage()
    {
        // A test sheet: a header line, problems with room for the answers, a footer far below.
        Pix bed = Bed();
        TextBlock(bed, 0.6, 0.5, 7.2, 0.2);
        TextBlock(bed, 0.8, 2.5, 6.4, 0.5);
        TextBlock(bed, 0.8, 4.5, 2.6, 0.3);
        TextBlock(bed, 0.6, 10.8, 7.2, 0.2);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(PageKind.Document, result.Kind);
        Pix page = Assert.Single(result.Images);
        Assert.InRange(page.Height, 10.5 * Dpi, 11.2 * Dpi);
    }

    [Fact]
    public void NarrowSlips_StaySeparate()
    {
        // Receipts side by side, and two more one under the other: none is page-wide.
        Pix bed = Bed();
        TextBlock(bed, 0.5, 0.5, 2.6, 4.0);
        TextBlock(bed, 4.5, 0.5, 2.6, 4.0);
        TextBlock(bed, 0.5, 6.0, 3.0, 2.0);
        TextBlock(bed, 0.5, 9.0, 3.0, 2.0);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(4, result.Images.Count);
    }

    [Fact]
    public void PrintWithWhiteBorder_IsTrimmedToThePicture()
    {
        // A photo printed with a white border: the cut follows the paper, the trim removes the border.
        Pix bed = Bed(235, 235);
        Photo(bed, 4.2, 3.0, 6.0, 4.0, 0);
        Pix withBorder = Pix.Create(bed.Width, bed.Height, 32);
        uint[] src = bed.Data, dst = withBorder.Data;
        int wpl = bed.Wpl;
        for (int y = 0; y < bed.Height; y++)
        {
            for (int x = 0; x < bed.Width; x++)
            {
                double xi = (double)x / Dpi, yi = (double)y / Dpi;
                bool paper = xi >= 1.15 && xi <= 7.25 && yi >= 0.95 && yi <= 5.05;     // 0.05 in white border
                bool picture = xi >= 1.2 && xi <= 7.2 && yi >= 1.0 && yi <= 5.0;
                dst[y * wpl + x] = picture ? src[y * wpl + x] : paper ? 0xFFFFFF00u : 0xEBEBEB00u;
            }
        }

        withBorder.SetResolution(Dpi, Dpi);
        PipelineResult result = ScanPipeline.Process(withBorder, PipelineOptions.All);

        Pix photo = Assert.Single(result.Images);
        Assert.InRange(photo.Width, 880, 902);   // 6 in = 900 px, no white strips left
        Assert.InRange(photo.Height, 585, 602);
    }

    [Fact]
    public void PhotoNearGlassEdgeShadow_FrameStaysOnThePhoto()
    {
        // A dark strip along the top edge of the glass, a few millimetres above a photo.
        Pix bed = Bed();
        uint[] data = bed.Data;
        int wpl = bed.Wpl;
        for (int y = 14; y < 20; y++)
        {
            for (int x = 0; x < bed.Width; x++)
                data[y * wpl + x] = 0x50505000u;
        }

        Photo(bed, 4.3, 2.3, 6.0, 4.0, 6.0);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Pix photo = Assert.Single(result.Images);
        Assert.InRange(photo.Width, 880, 910);
        Assert.InRange(photo.Height, 580, 610);
    }

    [Fact]
    public void EmptyBed_IsEmpty()
    {
        PipelineResult result = ScanPipeline.Process(Bed(), PipelineOptions.All);
        Assert.Equal(PageKind.Empty, result.Kind);
        Assert.Single(result.Images);
    }

    [Fact]
    public void TwoSkewedPhotos_AreSplitAndStraightened()
    {
        Pix bed = Bed();
        Photo(bed, 4.2, 3.0, 6.0, 4.0, 3.0, seed: 1);
        Photo(bed, 4.3, 8.4, 6.0, 4.0, -5.0, seed: 2);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(PageKind.Photos, result.Kind);
        Assert.Equal(2, result.Images.Count);
        foreach (Pix image in result.Images)
        {
            // 6 x 4 in at 150 dpi, minus the edge trim; within ~2%.
            Assert.InRange(image.Width, 880, 910);
            Assert.InRange(image.Height, 580, 610);
        }

        // Reading order: the upper photo first.
        Assert.True(result.Outlines[0].Cy < result.Outlines[1].Cy);
        Assert.InRange(result.Outlines[0].Angle, 2.0, 4.0);
        Assert.InRange(result.Outlines[1].Angle, -6.0, -4.0);
    }

    [Fact]
    public void OneCardAtLargeAngle_OnShadedLid_IsCutOut()
    {
        // A thick card lifts the lid: the background darkens from 240 to 200 toward the bottom.
        Pix bed = Bed(240, 200);
        Photo(bed, 4.2, 5.5, 5.0, 5.2, 25.0);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.Equal(PageKind.Photo, result.Kind);
        Pix card = Assert.Single(result.Images);
        Assert.InRange(card.Width, 730, 760);
        Assert.InRange(card.Height, 760, 790);
        Assert.InRange(result.Outlines[0].Angle, 24.0, 26.0);
    }

    [Fact]
    public void SplitOff_SeveralPhotos_KeepsWholeBed()
    {
        Pix bed = Bed();
        Photo(bed, 4.2, 3.0, 6.0, 4.0, 0, seed: 1);
        Photo(bed, 4.2, 8.4, 6.0, 4.0, 0, seed: 2);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All with { SplitPhotos = false });

        Assert.Equal(PageKind.Document, result.Kind);
        Assert.Single(result.Images);
    }

    [Fact]
    public void NoProcessing_ReturnsPageUntouched()
    {
        Pix bed = Bed();
        Photo(bed, 4.2, 3.0, 6.0, 4.0, 3.0);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.None);

        Assert.Same(bed, Assert.Single(result.Images));
    }

    [Fact]
    public void MinAreaRect_FindsRotatedSquare()
    {
        RotatedRect truth = new(100, 80, 60, 40, 20);
        List<(double X, double Y)> hull = Geometry.ConvexHull(truth.Corners());

        RotatedRect found = Geometry.MinAreaRect(hull);

        Assert.Equal(truth.Cx, found.Cx, 6);
        Assert.Equal(truth.Cy, found.Cy, 6);
        Assert.Equal(truth.Angle, found.Angle, 6);
        Assert.Equal(truth.Width * truth.Height, found.Width * found.Height, 3);
    }

    [Theory]
    [InlineData(70.0)]
    [InlineData(-30.0)]
    public void MinAreaRect_AngleIsNormalized(double angle)
    {
        RotatedRect r = Geometry.MinAreaRect(Geometry.ConvexHull(new RotatedRect(0, 0, 50, 30, angle).Corners()));
        Assert.InRange(r.Angle, -45.0, 45.0);
    }

    [Fact]
    public void PreviewWithOutlines_IsColor()
    {
        Pix bed = Bed();
        Photo(bed, 4.2, 3.0, 6.0, 4.0, 3.0);
        byte[] bmp = ScanSaver.PreviewWithOutlines(bed, [new RotatedRect(600, 450, 900, 600, 3)]);
        Assert.True(Pix.ReadMemBmp(bmp).Depth >= 24);
    }

    [Fact]
    public void TextSheet_IsDocument()
    {
        Pix bed = Bed();
        TextBlock(bed, 1.0, 1.0, 6.5, 9.0);
        Assert.Equal(ContentKind.Document, ContentGuess.Classify(bed));
    }

    [Fact]
    public void ColoredBannerAboveText_IsStillDocument()
    {
        Pix bed = Bed();
        Photo(bed, 4.25, 1.4, 6.5, 1.2, 0);   // a solid colored header, like on a bill
        TextBlock(bed, 1.0, 2.3, 6.5, 7.5);
        Assert.Equal(ContentKind.Document, ContentGuess.Classify(bed));
    }

    [Fact]
    public void Photos_AreNotDocument()
    {
        Pix bed = Bed();
        Photo(bed, 2.5, 2.5, 4.0, 3.0, 3.0);
        Photo(bed, 4.5, 8.0, 5.0, 3.5, -2.0, seed: 2);
        Assert.Equal(ContentKind.Photos, ContentGuess.Classify(bed));
    }

    [Fact]
    public void ColorlessSheetWithFewLines_IsDocument()
    {
        // Handwriting, formulas, a sparse test: few lines that count, but a colorless, lightly covered sheet.
        Pix sheet = Bed();
        TextBlock(sheet, 1.0, 1.0, 0.5, 3.0);   // lines too short to count
        TextBlock(sheet, 1.0, 5.0, 6.0, 0.3);
        Assert.Equal(ContentKind.Document, ContentGuess.Classify(sheet));
    }

    [Fact]
    public void ColoredDrawing_GivesNoGuess()
    {
        Pix drawing = Bed();
        for (int i = 0; i < 6; i++)
            Photo(drawing, 2.0 + i * 0.9, 3.0 + (i % 2) * 2.0, 0.5, 0.4, 0, seed: i);
        Assert.Equal(ContentKind.Unknown, ContentGuess.Classify(drawing));
    }

    [Fact]
    public void FewLinesOrEmptyBed_GiveNoGuess()
    {
        Pix caption = Bed();
        TextBlock(caption, 1.0, 5.0, 5.0, 0.5);
        Assert.Equal(ContentKind.Unknown, ContentGuess.Classify(caption));
        Assert.Equal(ContentKind.Unknown, ContentGuess.Classify(Bed()));
    }

    [Fact]
    public void TintedSheetWithPiecesBelow_StaysOneDocument()
    {
        // Paper darker than the lid (or a colored bill) looks solid like a photo, but is full of text;
        // the stub below it, with an address and a code line, belongs to the same sheet.
        Pix bed = Bed();
        uint[] data = bed.Data;
        int wpl = bed.Wpl;
        for (int y = (int)(0.5 * Dpi); y < (int)(8.0 * Dpi); y++)
        {
            for (int x = (int)(0.6 * Dpi); x < (int)(7.9 * Dpi); x++)
                data[y * wpl + x] = 0xC8D2DC00u;
        }

        TextBlock(bed, 1.0, 1.0, 6.5, 6.5);
        TextBlock(bed, 1.0, 8.3, 3.0, 0.8);
        TextBlock(bed, 4.8, 8.3, 2.6, 0.6);
        TextBlock(bed, 4.5, 9.3, 3.0, 0.2);

        IReadOnlyList<CutFrame> frames = ScanPipeline.Plan(bed, PipelineOptions.All);

        Assert.Single(frames);
        Assert.Equal(ItemKind.Document, frames[0].Kind);
        Assert.Equal(ContentKind.Document, ContentGuess.Classify(bed));
    }

    /// <summary>Paints a colored disc with a thin white border (a sticker): center and radius in inches.</summary>
    private static void Sticker(Pix bed, double cxIn, double cyIn, double rIn, int seed = 2)
    {
        double cx = cxIn * Dpi, cy = cyIn * Dpi, r = rIn * Dpi, border = 0.05 * Dpi;
        Random random = new(seed);
        uint[] data = bed.Data;
        int wpl = bed.Wpl;
        for (int y = 0; y < bed.Height; y++)
        {
            for (int x = 0; x < bed.Width; x++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d > r + border)
                    continue;
                uint red = (uint)(180 + random.Next(60)), green = (uint)(30 + random.Next(60)), blue = (uint)(40 + random.Next(60));
                data[y * wpl + x] = d > r ? 0xF5F5F500u : (red << 24) | (green << 16) | (blue << 8);
            }
        }
    }

    private static int Alpha(Pix pix, int x, int y) => (int)(pix.Data[y * pix.Wpl + x] & 0xff);

    [Fact]
    public void Stickers_AreCutAlongTheirOutline_PhotosStayRectangles()
    {
        Pix bed = Bed(254, 254);
        Sticker(bed, 2.0, 2.0, 1.0);
        Sticker(bed, 5.5, 2.2, 0.8, seed: 3);
        Photo(bed, 4.2, 7.0, 5.0, 3.5, 4.0);
        PipelineOptions options = PipelineOptions.All with { Stickers = true };

        PipelineResult result = ScanPipeline.Process(bed, options);

        Assert.Equal(3, result.Images.Count);
        Pix[] stickers = result.Images.Where(i => i.Spp == 4).ToArray();
        Assert.Equal(2, stickers.Length);
        foreach (Pix s in stickers)
        {
            // Transparent in the corners, opaque in the middle, the border added around the disc.
            Assert.Equal(0, Alpha(s, 0, 0));
            Assert.Equal(0, Alpha(s, s.Width - 1, s.Height - 1));
            Assert.Equal(255, Alpha(s, s.Width / 2, s.Height / 2));
            Assert.Equal(255, Alpha(s, s.Width / 2, 6));
        }

        Pix photo = Assert.Single(result.Images, i => i.Spp != 4);
        Assert.InRange(photo.Width, 4.8 * Dpi, 5.1 * Dpi);
    }

    [Fact]
    public void Stickers_Off_CutsRectangles()
    {
        Pix bed = Bed(254, 254);
        Sticker(bed, 2.0, 2.0, 1.0);
        Sticker(bed, 5.5, 2.2, 0.8, seed: 3);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All);

        Assert.All(result.Images, i => Assert.NotEqual(4, i.Spp));
    }

    [Fact]
    public void Stickers_KeepTheirTransparency_WhenTurned()
    {
        Pix bed = Bed(254, 254);
        Sticker(bed, 3.0, 3.0, 1.0);
        Sticker(bed, 3.0, 7.0, 1.0);
        Pix sticker = ScanPipeline.Process(bed, PipelineOptions.All with { Stickers = true }).Images[0];

        Pix turned = FrameCodec.Rotate(sticker, 90);

        Assert.Equal(4, turned.Spp);
    }

    [Fact]
    public void Stickers_AreRecognized_PhotosAndSheetsAreNot()
    {
        Pix stickers = Bed(254, 254);
        Sticker(stickers, 2.0, 2.0, 1.0);
        Sticker(stickers, 5.5, 2.2, 0.8, seed: 3);
        Pix photos = Bed(254, 254);
        Photo(photos, 4.2, 2.5, 6.0, 4.0, 2.0);
        Photo(photos, 4.2, 7.5, 6.0, 4.0, -3.0, seed: 2);
        Pix sheet = Bed(254, 254);
        TextBlock(sheet, 1.0, 1.0, 6.5, 8.0);

        Assert.True(ScanPipeline.LooksLikeStickers(stickers));
        Assert.False(ScanPipeline.LooksLikeStickers(photos));
        Assert.False(ScanPipeline.LooksLikeStickers(sheet));
    }

    [Fact]
    public void Stickers_LightGrayPartsOfAPicture_AreNotPaintedWhite()
    {
        // A photo with a silvery strip along its edge, a sticker over its corner: one shape, not a rectangle.
        Pix bed = Bed(254, 254);
        Photo(bed, 4.0, 4.0, 4.0, 3.0, 0);
        uint[] data = bed.Data;
        int wpl = bed.Wpl;
        int x0 = (int)(2.0 * Dpi), strip = (int)(0.1 * Dpi);
        for (int y = (int)(2.5 * Dpi); y < (int)(5.5 * Dpi); y++)
        {
            for (int x = x0; x < x0 + strip; x++)
                data[y * wpl + x] = 0xC8C8C800u;
        }

        Sticker(bed, 6.0, 5.5, 0.7);
        Sticker(bed, 2.0, 9.0, 0.8, seed: 3);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All with { Stickers = true });

        Pix shape = result.Images.OrderByDescending(i => i.Width * i.Height).First();
        Assert.Equal(4, shape.Spp);
        int gray = 0;
        for (int y = 0; y < shape.Height; y++)
        {
            for (int x = 0; x < shape.Width; x++)
            {
                uint p = shape.Data[y * shape.Wpl + x];
                gray += (p & 0xff) == 255 && (p >> 8) == 0xC8C8C8u ? 1 : 0;
            }
        }

        // The strip is kept as it is, not painted over with the white border.
        Assert.True(gray > strip * 3.0 * Dpi * 0.7, $"{gray} gray pixels kept");
    }

    [Fact]
    public void Stickers_TheDarkEdgeOfTheGlass_IsNotPartOfASticker()
    {
        Pix bed = Bed(254, 254);
        uint[] data = bed.Data;
        int wpl = bed.Wpl;
        int band = (int)(0.12 * Dpi);
        for (int y = bed.Height - band; y < bed.Height; y++)
        {
            for (int x = 0; x < bed.Width; x++)
                data[y * wpl + x] = 0x78787800u;
        }

        double r = 0.8, cy = 11.7 - 0.12 - 0.02 - r;
        Sticker(bed, 3.0, cy, r);
        Sticker(bed, 3.0, 3.0, r, seed: 3);

        PipelineResult result = ScanPipeline.Process(bed, PipelineOptions.All with { Stickers = true });

        Assert.Equal(2, result.Images.Count);
        Assert.All(result.Images, i =>
        {
            Assert.Equal(4, i.Spp);
            int band = 0;
            for (int y = 0; y < i.Height; y++)
            {
                for (int x = 0; x < i.Width; x++)
                {
                    uint p = i.Data[y * i.Wpl + x];
                    band += (p & 0xff) != 0 && (p >> 8) == 0x787878u ? 1 : 0;
                }
            }

            Assert.Equal(0, band);
        });
    }

    private static uint Gray(Pix pix, int x, int y) => (pix.Data[y * pix.Wpl + (x >> 2)] >> (24 - 8 * (x & 3))) & 0xff;

    [Fact]
    public void WhitePaper_MakesColoredPaperWhite_AndKeepsThePrint()
    {
        // A whole yellow page with lines of text: kept as one page (no frames), as a PDF page is.
        Pix bed = Bed(254, 254);
        uint[] data = bed.Data;
        int wpl = bed.Wpl;
        for (int y = 0; y < bed.Height; y++)
        {
            for (int x = 0; x < bed.Width; x++)
                data[y * wpl + x] = 0xF5DC3C00u;   // yellow paper
        }

        // A heading on a darker band, as wide as a sheet: a panel, not paper.
        for (int y = (int)(9.5 * Dpi); y < (int)(10.3 * Dpi); y++)
        {
            for (int x = (int)(1.0 * Dpi); x < (int)(7.5 * Dpi); x++)
                data[y * wpl + x] = 0xAA962800u;
        }

        TextBlock(bed, 1.0, 1.0, 6.5, 8.0);
        PipelineOptions options = new(false, false, false, false, WhitePaper: true);

        Pix page = ScanPipeline.Cut(bed, [], options).Images[0];

        // Clearly colored paper: gray like a photocopy (the colors of the print cannot be told under it).
        Assert.Equal(8, page.Depth);
        uint paper = Gray(page, (int)(0.5 * Dpi), (int)(0.5 * Dpi));
        Assert.True(paper > 235, $"paper {paper}");
        uint print = Gray(page, (int)(1.0 * Dpi) + 10, (int)(1.0 * Dpi) + 4);
        Assert.True(print < 100, $"print {print}");
        uint panel = Gray(page, (int)(4.0 * Dpi), (int)(9.9 * Dpi));
        Assert.InRange(panel, 60u, 200u);   // the band stays a gray band
    }

    [Fact]
    public void WhitePaper_LeavesPhotosAsTheyAre()
    {
        Pix bed = Bed(254, 254);
        Photo(bed, 4.2, 2.5, 6.0, 4.0, 0);
        Photo(bed, 4.2, 7.5, 6.0, 4.0, 0, seed: 2);

        PipelineResult plain = ScanPipeline.Process(bed, PipelineOptions.All);
        PipelineResult white = ScanPipeline.Process(bed, PipelineOptions.All with { WhitePaper = true });

        Assert.Equal(plain.Images.Count, white.Images.Count);
        for (int i = 0; i < plain.Images.Count; i++)
            Assert.Equal(plain.Images[i].Data, white.Images[i].Data);
    }
}
