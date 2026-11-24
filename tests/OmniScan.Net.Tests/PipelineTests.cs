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
    public void FewLinesOrEmptyBed_GiveNoGuess()
    {
        Pix caption = Bed();
        TextBlock(caption, 1.0, 5.0, 5.0, 0.5);
        Assert.Equal(ContentKind.Unknown, ContentGuess.Classify(caption));
        Assert.Equal(ContentKind.Unknown, ContentGuess.Classify(Bed()));
    }
}
