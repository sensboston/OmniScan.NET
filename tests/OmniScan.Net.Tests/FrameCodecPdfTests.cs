using System;
using Leptonica;
using OmniScan.Net.Core;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.Tests;

public sealed class FrameCodecPdfTests
{
    /// <summary>A gray image with fine detail, so JPEG quality makes a visible difference in size.</summary>
    private static Pix Detailed()
    {
        Random random = new(7);
        Pix pix = Pix.Create(400, 300, 8);
        for (int y = 0; y < 300; y++)
            for (int x = 0; x < 400; x++)
                pix.SetPixel(x, y, (uint)((x * 3 + y * 2 + random.Next(60)) & 0xFF));
        pix.SetResolution(150, 150);
        return pix;
    }

    [Fact]
    public void Pdf_QualityChangesSize()
    {
        Pix pix = Detailed();

        byte[] low = FrameCodec.Pdf(pix, 150, 50);
        byte[] high = FrameCodec.Pdf(pix, 150, 100);

        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(low, 0, 4));
        Assert.True(low.Length < high.Length, $"{low.Length} vs {high.Length}");
    }

    [Fact]
    public void Encode_Pdf_UsesTheGivenQuality()
    {
        Pix pix = Detailed();
        Assert.Equal(FrameCodec.Pdf(pix, 150, 60).Length, FrameCodec.Encode(pix, OutputFormat.Pdf, 60).Length);
    }
}
