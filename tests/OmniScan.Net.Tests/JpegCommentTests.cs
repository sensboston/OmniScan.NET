using System.IO;
using Leptonica;
using OmniScan.Net.Core;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.Tests;

public sealed class JpegCommentTests
{
    private static byte[] Jpeg()
    {
        Pix pix = Pix.Create(64, 48, 32);
        pix.SetAllArbitrary(0x8040C000);
        return FrameCodec.Encode(pix, OutputFormat.Jpeg, 80);
    }

    [Fact]
    public void Set_ThenGet_ReturnsTheText_AndTheImageStillDecodes()
    {
        byte[] original = Jpeg();
        byte[] commented = JpegComment.Set(original, "Закат над домом и деревьями");

        Assert.Equal("Закат над домом и деревьями", JpegComment.Get(commented));
        Assert.Null(JpegComment.Get(original));

        using TempFolder temp = new();
        string path = Path.Combine(temp.Path, "a.jpg");
        File.WriteAllBytes(path, commented);
        Pix back = Pix.Read(path);
        Assert.Equal(64, back.Width);
        Assert.Equal(48, back.Height);
    }

    [Fact]
    public void Set_Again_ReplacesTheOldComment_WithoutGrowing()
    {
        byte[] once = JpegComment.Set(Jpeg(), "first");
        byte[] twice = JpegComment.Set(once, "second");

        Assert.Equal("second", JpegComment.Get(twice));
        Assert.Equal(once.Length + ("second".Length - "first".Length) * 2 * 2, twice.Length);
    }

    [Fact]
    public void Set_Empty_RemovesTheComment()
    {
        byte[] original = Jpeg();
        byte[] cleared = JpegComment.Set(JpegComment.Set(original, "text"), null);

        Assert.Null(JpegComment.Get(cleared));
        Assert.Equal(original, cleared);
    }

    [Fact]
    public void Set_NotAJpeg_Throws() =>
        Assert.Throws<InvalidDataException>(() => JpegComment.Set([1, 2, 3, 4], "x"));
}
