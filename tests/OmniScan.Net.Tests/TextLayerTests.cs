using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Leptonica;
using OmniScan.Net.Imaging;
using OmniScan.Net.Ocr;

namespace OmniScan.Net.Tests;

/// <summary>The invisible text layer of PDFs and the choice between two readings of a page.</summary>
public sealed class TextLayerTests
{
    private static ReadLine Line(string text, double y, double x = 100) =>
        new(text.Split(' ').Select((w, i) => new TextWord(w, x + i * 120, y, 100, 30)).ToList());

    [Fact]
    public void RussianPage_TakesRussianLines_AndKeepsTheEnglishOne()
    {
        IReadOnlyList<ReadLine> english = [Line("Aoroeop apeHAbl", 100), Line("Contact: office", 200), Line("HacTOHLuMü noroeop", 300)];
        IReadOnlyList<ReadLine> russian = [Line("Договор аренды", 100), Line("Contact: оffice", 200), Line("Настоящий договор", 300)];

        string[] text = PageReader.Merge(english, russian).Select(l => l.Text).ToArray();

        Assert.Equal(["Договор аренды", "Contact: office", "Настоящий договор"], text);
    }

    [Fact]
    public void EnglishPage_IgnoresLookAlikeLetters()
    {
        IReadOnlyList<ReadLine> english = [Line("Privacy Statement", 100), Line("Page 1 of 2", 200), Line("December 3, 2026", 300)];
        IReadOnlyList<ReadLine> russian = [Line("Privacy Statement", 100), Line("Раде 1 of 2", 200), Line("December З 2026", 300)];

        Assert.Same(english, PageReader.Merge(english, russian));
    }

    [Theory]
    [InlineData("Привет", 1.0)]
    [InlineData("Größe", 0.0)]
    [InlineData("12 345", 0.0)]
    [InlineData("Раде 1 of 2", 4.0 / 6)]
    public void NonLatinShare_CountsLettersOnly(string text, double expected) =>
        Assert.Equal(expected, PageReader.NonLatinShare(text), 3);

    [Fact]
    public void Pdf_HasEveryPage_ValidOffsets_AndInvisibleUnicodeText()
    {
        Pix bw = Pix.Create(850, 1100, 1);
        bw.SetResolution(100, 100);
        Pix color = Pix.Create(850, 1100, 32);
        color.SetAllArbitrary(0xE0D0C000);
        List<PdfSheet> sheets =
        [
            new(bw, 100, [new TextWord("Привет", 100, 100, 200, 30), new TextWord("world", 320, 100, 150, 30)]),
            new(color, 100),
        ];

        byte[] pdf = PdfWriter.Write(sheets, 75, "Тест");
        string text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.5", text);
        Assert.Equal(2, Regex.Matches(text, @"/Type /Page\b(?!s)").Count);
        Assert.Contains("/CCITTFaxDecode", text);   // black and white page
        Assert.Contains("/DCTDecode", text);        // color page
        Assert.Contains("/FontFile2", text);

        // Every cross-reference entry points at the start of its object.
        Match xref = Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$");
        Assert.True(xref.Success);
        int at = int.Parse(xref.Groups[1].Value, CultureInfo.InvariantCulture);
        Match table = Regex.Match(text[at..], @"^xref\n0 (\d+)\n0000000000 65535 f \n");
        Assert.True(table.Success);
        int count = int.Parse(table.Groups[1].Value, CultureInfo.InvariantCulture);
        string[] entries = text[(at + table.Length)..].Split('\n').Take(count - 1).ToArray();
        for (int id = 1; id < count; id++)
        {
            int offset = int.Parse(entries[id - 1][..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{id} 0 obj\n", text[offset..]);
        }

        // The words themselves are in a compressed content stream: look for them there.
        string contents = string.Join("\n", Streams(pdf));
        Assert.Contains("3 Tr", contents);
        Assert.Contains("<041F04400438043204350442> Tj", contents);   // "Привет" as Unicode values
        Assert.Contains("<0077006F0072006C0064> Tj", contents);       // "world"
    }

    [Fact]
    public void Pdf_WithoutText_HasNoFont()
    {
        Pix page = Pix.Create(100, 100, 8);
        string text = Encoding.Latin1.GetString(PdfWriter.Write([new PdfSheet(page, 100)], 75));

        Assert.DoesNotContain("/Font", text);
    }

    /// <summary>The Flate streams of a PDF, unpacked and read as Latin-1 text.</summary>
    private static IEnumerable<string> Streams(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        foreach (Match m in Regex.Matches(text, @"<< /Length (\d+) /Filter /FlateDecode [^>]*>>\nstream\n"))
        {
            int length = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            using System.IO.MemoryStream packed = new(pdf, m.Index + m.Length, length);
            using System.IO.Compression.ZLibStream z = new(packed, System.IO.Compression.CompressionMode.Decompress);
            using System.IO.MemoryStream plain = new();
            z.CopyTo(plain);
            yield return Encoding.Latin1.GetString(plain.ToArray());
        }
    }
}
