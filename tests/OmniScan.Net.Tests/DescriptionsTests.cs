using System.IO;
using System.Text;
using OmniScan.Net.Core;

namespace OmniScan.Net.Tests;

public sealed class DescriptionsTests
{
    private static string Ion(TempFolder t) => Path.Combine(t.Path, Descriptions.FileName);

    [Fact]
    public void Set_CreatesHiddenUtf8FileWithBom()
    {
        using TempFolder temp = new();
        Descriptions.Set(Path.Combine(temp.Path, "scan_001.jpg"), "Кот на диване");

        byte[] bytes = File.ReadAllBytes(Ion(temp));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("scan_001.jpg Кот на диване\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.True(File.GetAttributes(Ion(temp)).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Set_ReplacesOwnLine_KeepsOthers_EvenWhenHidden()
    {
        using TempFolder temp = new();
        File.WriteAllText(Ion(temp), "\"my file.txt\" kept as is\r\nSCAN_002.JPG old text\r\nother.pdf also kept\r\n");
        File.SetAttributes(Ion(temp), FileAttributes.Hidden);

        Descriptions.Set(Path.Combine(temp.Path, "scan_002.jpg"), "new text");

        Assert.Equal(
            ["\"my file.txt\" kept as is", "scan_002.jpg new text", "other.pdf also kept"],
            File.ReadAllLines(Ion(temp)));
        Assert.Equal("kept as is", Descriptions.Get(Path.Combine(temp.Path, "my file.txt")));
    }

    [Fact]
    public void Set_QuotesNamesWithSpaces_AndFlattensLineBreaks()
    {
        using TempFolder temp = new();
        Descriptions.Set(Path.Combine(temp.Path, "my scan.pdf"), "Счёт\r\nза свет  ");

        Assert.Equal(["\"my scan.pdf\" Счёт  за свет"], File.ReadAllLines(Ion(temp)));
        Assert.Equal("Счёт  за свет", Descriptions.Get(Path.Combine(temp.Path, "my scan.pdf")));
    }

    [Fact]
    public void Set_Empty_RemovesTheLine()
    {
        using TempFolder temp = new();
        Descriptions.Set(Path.Combine(temp.Path, "a.jpg"), "one");
        Descriptions.Set(Path.Combine(temp.Path, "b.jpg"), "two");

        Descriptions.Set(Path.Combine(temp.Path, "a.jpg"), null);

        Assert.Equal(["b.jpg two"], File.ReadAllLines(Ion(temp)));
        Assert.Null(Descriptions.Get(Path.Combine(temp.Path, "a.jpg")));
    }

    [Fact]
    public void Set_OldAnsiFile_IsReadInItsCodePage()
    {
        using TempFolder temp = new();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        File.WriteAllBytes(Ion(temp), ansi.GetBytes("old.jpg café\r\n"));

        Descriptions.Set(Path.Combine(temp.Path, "new.jpg"), "x");

        Assert.Equal(ansi.GetString(ansi.GetBytes("café")), Descriptions.Get(Path.Combine(temp.Path, "old.jpg")));
    }
}
