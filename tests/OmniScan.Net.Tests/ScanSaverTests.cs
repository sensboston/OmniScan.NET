using System;
using System.IO;
using Leptonica;
using OmniScan.Net.Core;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.Tests;

public sealed class ScanSaverTests
{
    // A 300 x 200 color "scan" as the WIA driver would deliver it.
    private static ScannedFrame BmpFrame(int width = 300, int height = 200, int depth = 32)
    {
        Pix pix = Pix.Create(width, height, depth);
        pix.SetAllArbitrary(depth == 32 ? 0x40806000u : 1u);
        return new ScannedFrame(Pix.WriteMemBmp(pix), "image/bmp", 150);
    }

    [Theory]
    [InlineData(OutputFormat.Png, ".png", new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' })]
    [InlineData(OutputFormat.Jpeg, ".jpg", new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(OutputFormat.Pdf, ".pdf", new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F' })]
    public void Save_WritesFormatIntoDayFolder(OutputFormat format, string extension, byte[] magic)
    {
        using TempFolder temp = new();
        SaveOptions options = new(temp.Path, new DateTime(2026, 11, 23), format, 90, 0, PipelineOptions.None);

        SaveResult result = ScanSaver.Save(BmpFrame(), options);

        string expected = Path.Combine(temp.Path, "2026-11-23", "scan_001" + extension);
        Assert.Equal([expected], result.Files);
        byte[] data = File.ReadAllBytes(expected);
        Assert.Equal(magic, data[..magic.Length]);
        Assert.NotEmpty(result.PreviewBmp);
    }

    [Fact]
    public void BlankGlass_IsNotSaved_UnlessAsked()
    {
        using TempFolder temp = new();
        Pix lid = Pix.Create(400, 550, 32);
        lid.SetAllArbitrary(0xF5F5F500u);
        ScannedFrame blank = new(Pix.WriteMemBmp(lid), "image/bmp", 50);
        SaveOptions options = new(temp.Path, new DateTime(2026, 11, 23), OutputFormat.Jpeg, 90, 0, PipelineOptions.All);

        SaveResult skipped = ScanSaver.Save(blank, options);
        SaveResult forced = ScanSaver.Save(blank, options with { SaveBlank = true });

        Assert.Equal(PageKind.Empty, skipped.Kind);
        Assert.Empty(skipped.Files);
        Assert.Single(forced.Files);
    }

    [Fact]
    public void Save_TwiceIncrements()
    {
        using TempFolder temp = new();
        SaveOptions options = new(temp.Path, new DateTime(2026, 11, 23), OutputFormat.Png, 90, 0, PipelineOptions.None);

        ScanSaver.Save(BmpFrame(), options);
        SaveResult second = ScanSaver.Save(BmpFrame(), options);

        Assert.Equal("scan_002.png", Path.GetFileName(second.Files[0]));
    }

    [Theory]
    [InlineData(0, 300, 200)]
    [InlineData(90, 200, 300)]
    [InlineData(180, 300, 200)]
    [InlineData(270, 200, 300)]
    public void Prepare_RotatesAndKeepsResolution(int rotation, int width, int height)
    {
        Pix pix = ScanSaver.Prepare(BmpFrame(), rotation);

        Assert.Equal(width, pix.Width);
        Assert.Equal(height, pix.Height);
        Assert.Equal(150, pix.XRes);
    }

    [Fact]
    public void Prepare_Rotate180_MovesCornerPixel()
    {
        Pix source = Pix.Create(4, 2, 8);
        source.SetPixel(0, 0, 200);
        ScannedFrame frame = new(Pix.WriteMemBmp(source), "image/bmp", 300);

        Pix rotated = ScanSaver.Prepare(frame, 180);

        rotated.GetPixel(3, 1, out uint corner);
        rotated.GetPixel(0, 0, out uint origin);
        Assert.Equal(200u, corner);
        Assert.Equal(0u, origin);
    }

    [Fact]
    public void Save_BlackAndWhite_AsJpeg()
    {
        using TempFolder temp = new();
        SaveOptions options = new(temp.Path, DateTime.Today, OutputFormat.Jpeg, 90, 0, PipelineOptions.None);

        SaveResult result = ScanSaver.Save(BmpFrame(depth: 1), options);

        Pix back = Pix.Read(result.Files[0]);
        Assert.Equal(8, back.Depth);
    }

    [Fact]
    public void ManualFrames_FromPreview_AreCutAtScanResolution()
    {
        using TempFolder temp = new();

        // Drawn on a 75 dpi preview of a 300 x 200 px bed: a 100 x 50 px box at the center, turned 10 degrees.
        CutFrame drawn = new CutFrame(new RotatedRect(150, 100, 100, 50, 10), ItemKind.Photo, Manual: true).Normalize(300);
        // The scan is four times larger.
        ScannedFrame scan = BmpFrame(1200, 800);
        SaveOptions options = new(temp.Path, new DateTime(2026, 11, 23), OutputFormat.Png, 90, 0, PipelineOptions.All, [drawn]);

        SaveResult result = ScanSaver.Save(scan, options);

        Pix cut = Assert.Single(result.Images);
        Assert.InRange(cut.Width, 398, 402);
        Assert.InRange(cut.Height, 198, 202);
    }

    [Fact]
    public void Preview_ReturnsFramesInPageWidthUnits()
    {
        PreviewResult preview = ScanSaver.Preview(BmpFrame(), 0, PipelineOptions.All);
        Assert.All(preview.Frames, f => Assert.InRange(f.Rect.Cx, 0.0, 1.0));
    }

    [Fact]
    public void Preview_IsDownscaled()
    {
        byte[] bmp = ScanSaver.Preview(BmpFrame(3400, 2000), 0, PipelineOptions.None).PageBmp;

        Pix preview = Pix.ReadMemBmp(bmp);
        Assert.Equal(ScanSaver.PreviewMaxSide, preview.Width);
    }
}
