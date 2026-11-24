using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Leptonica;

namespace OmniScan.Net.Imaging;

/// <summary>A recognized word: its text and its box on the page image, in pixels from the top left corner.</summary>
public sealed record TextWord(string Text, double X, double Y, double Width, double Height);

/// <summary>One PDF page: the scanned image, its resolution and, when recognized, the words on it.</summary>
public sealed record PdfSheet(Pix Image, int Dpi, IReadOnlyList<TextWord>? Words = null);

/// <summary>
/// Writes a PDF of scanned pages, each an image (G4 for black and white, JPEG for gray and color, as
/// Leptonica chooses) with an optional invisible text layer over it: every word is drawn in invisible
/// mode (3 Tr) with a font that has no visible glyphs, stretched to the width of its box, so that the
/// text can be searched, selected and copied where it is on the page. Characters are written as their
/// Unicode values (Identity-H with a ToUnicode map), so any language works the same way.
/// </summary>
public static class PdfWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <param name="pages">Taken one at a time, so that only one page image is in memory while writing.</param>
    public static byte[] Write(IEnumerable<PdfSheet> pages, int jpegQuality, string? title = null)
    {
        Document pdf = new();
        int catalog = pdf.Reserve(), pageTree = pdf.Reserve(), info = pdf.Reserve();
        int font = 0;

        List<int> kids = [];
        foreach (PdfSheet sheet in pages)
        {
            if (font == 0 && sheet.Words is { Count: > 0 })
                font = WriteFont(pdf);
            kids.Add(WritePage(pdf, sheet, pageTree, font, jpegQuality));
        }

        if (kids.Count == 0)
            throw new ArgumentException("No pages.", nameof(pages));

        pdf.Object(pageTree, $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");
        pdf.Object(catalog, $"<< /Type /Catalog /Pages {pageTree} 0 R >>");
        StringBuilder infoDict = new("<< /Producer (OmniScan)");
        if (!string.IsNullOrEmpty(title))
            infoDict.Append(" /Title ").Append(TextString(title));
        infoDict.Append(" /CreationDate (D:").Append(DateTime.Now.ToString("yyyyMMddHHmmss", Invariant)).Append(") >>");
        pdf.Object(info, infoDict.ToString());
        return pdf.Finish(catalog, info);
    }

    private static int WritePage(Document pdf, PdfSheet sheet, int parent, int font, int jpegQuality)
    {
        Pix image = sheet.Image;
        double scale = 72.0 / sheet.Dpi;
        double width = image.Width * scale, height = image.Height * scale;

        int imageObject = WriteImage(pdf, image, jpegQuality);

        StringBuilder content = new();
        content.Append(Invariant, $"q {N(width)} 0 0 {N(height)} 0 0 cm /Im0 Do Q\n");
        if (sheet.Words is { Count: > 0 } words && font != 0)
            AppendText(content, words, scale, height);
        int contents = pdf.Stream(Encoding.ASCII.GetBytes(content.ToString()), "", compress: true);

        string fonts = font != 0 ? $" /Font << /F0 {font} 0 R >>" : "";
        int page = pdf.Reserve();
        pdf.Object(page,
            $"<< /Type /Page /Parent {parent} 0 R /MediaBox [0 0 {N(width)} {N(height)}] " +
            $"/Resources << /XObject << /Im0 {imageObject} 0 R >>{fonts} >> /Contents {contents} 0 R >>");
        return page;
    }

    /// <summary>
    /// Each word at its box: the font size is the box height, the horizontal scaling makes the word as
    /// wide as the box, the baseline is a fifth of the height above the box bottom (room for descenders).
    /// </summary>
    private static void AppendText(StringBuilder content, IReadOnlyList<TextWord> words, double scale, double pageHeight)
    {
        content.Append("BT\n3 Tr\n");
        foreach (TextWord word in words)
        {
            string text = word.Text.Trim();
            if (text.Length == 0 || word.Width <= 0 || word.Height <= 0)
                continue;
            double size = word.Height * scale;
            double natural = text.Length * size * GlyphlessFont.Advance / GlyphlessFont.UnitsPerEm;
            double stretch = Math.Clamp(100 * word.Width * scale / natural, 1, 10000);
            double x = word.X * scale;
            double y = pageHeight - (word.Y + word.Height) * scale + size * 0.2;
            content.Append(Invariant, $"/F0 {N(size)} Tf {N(stretch)} Tz 1 0 0 1 {N(x)} {N(y)} Tm <");
            foreach (char c in text)
                content.Append(((int)c).ToString("X4", Invariant));
            content.Append("> Tj\n");
        }

        content.Append("ET\n");
    }

    /// <summary>The image compressed as Leptonica does for its own PDFs (no second JPEG generation for JPEG data).</summary>
    private static int WriteImage(Document pdf, Pix image, int jpegQuality)
    {
        int type = PdfIo.SelectDefaultPdfEncoding(image);
        L_Compressed_Data cid = image.GenerateCIData(type, jpegQuality, 0);
        byte[] data = cid.DataComp!.AsSpan(0, cid.NbytesComp).ToArray();

        string colors = cid.Spp switch
        {
            1 => "/DeviceGray",
            4 => "/DeviceCMYK",
            _ => "/DeviceRGB",
        };
        string dictionary;
        if (cid.Type == L.L_G4_ENCODE)
        {
            string blackIs1 = cid.MinisBlack != 0 ? "true" : "false";
            dictionary = $"/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode " +
                         $"/DecodeParms << /BlackIs1 {blackIs1} /K -1 /Columns {cid.W} >>";
        }
        else if (cid.Type == L.L_JPEG_ENCODE)
        {
            dictionary = $"/ColorSpace {colors} /BitsPerComponent 8 /Filter /DCTDecode";
        }
        else
        {
            if (cid.Ncolors > 0)
                colors = $"[/Indexed /DeviceRGB {cid.Ncolors - 1} {cid.CmapDataHex}]";
            string decode = cid.Ncolors == 0 && cid.Spp == 1 && cid.Bps == 1 ? " /Decode [1 0]" : "";
            string predictor = cid.Predictor != 0
                ? $" /DecodeParms << /Columns {cid.W} /Predictor 14 /Colors {cid.Spp} /BitsPerComponent {cid.Bps} >>"
                : "";
            dictionary = $"/ColorSpace {colors}{decode} /BitsPerComponent {cid.Bps} /Filter /FlateDecode{predictor}";
        }

        return pdf.Stream(data, $"/Type /XObject /Subtype /Image /Width {cid.W} /Height {cid.H} {dictionary}", compress: false);
    }

    /// <summary>The invisible-text font: Type0 over a CID TrueType font with every code drawn as glyph 1.</summary>
    private static int WriteFont(Document pdf)
    {
        byte[] ttf = GlyphlessFont.Data;
        int file = pdf.Stream(ttf, $"/Length1 {ttf.Length}", compress: true);
        int descriptor = pdf.Add(
            $"<< /Type /FontDescriptor /FontName /{GlyphlessFont.Name} /Flags 5 /FontBBox [0 -200 {GlyphlessFont.Advance} 800] " +
            $"/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 800 /StemV 80 /FontFile2 {file} 0 R >>");

        byte[] cidToGid = new byte[65536 * 2];
        for (int i = 1; i < cidToGid.Length; i += 2)
            cidToGid[i] = 1;
        int map = pdf.Stream(cidToGid, "", compress: true);

        int cidFont = pdf.Add(
            $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{GlyphlessFont.Name} " +
            $"/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            $"/FontDescriptor {descriptor} 0 R /DW {GlyphlessFont.Advance} /CIDToGIDMap {map} 0 R >>");

        const string toUnicode =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 beginbfrange\n<0000> <FFFF> <0000>\nendbfrange\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
        int unicode = pdf.Stream(Encoding.ASCII.GetBytes(toUnicode), "", compress: true);

        return pdf.Add(
            $"<< /Type /Font /Subtype /Type0 /BaseFont /{GlyphlessFont.Name} /Encoding /Identity-H " +
            $"/DescendantFonts [{cidFont} 0 R] /ToUnicode {unicode} 0 R >>");
    }

    /// <summary>A text string for the document information: UTF-16 with a byte order mark, in hex.</summary>
    private static string TextString(string text)
    {
        StringBuilder b = new("<FEFF");
        foreach (char c in text)
            b.Append(((int)c).ToString("X4", Invariant));
        return b.Append('>').ToString();
    }

    private static string N(double v) => Math.Round(v, 3).ToString("0.###", Invariant);

    /// <summary>The objects of the file, written as they come; the cross-reference table at the end.</summary>
    private sealed class Document
    {
        private readonly MemoryStream _out = new();
        private readonly List<long> _offsets = [0];

        public Document()
        {
            Write("%PDF-1.5\n%âãÏÓ\n");
        }

        public int Reserve()
        {
            _offsets.Add(-1);
            return _offsets.Count - 1;
        }

        public int Add(string dictionary)
        {
            int id = Reserve();
            Object(id, dictionary);
            return id;
        }

        public void Object(int id, string body)
        {
            _offsets[id] = _out.Position;
            Write($"{id} 0 obj\n{body}\nendobj\n");
        }

        public int Stream(byte[] data, string dictionary, bool compress)
        {
            byte[] body = data;
            string filter = "";
            if (compress)
            {
                using MemoryStream packed = new();
                using (ZLibStream z = new(packed, CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(data);
                body = packed.ToArray();
                filter = " /Filter /FlateDecode";
            }

            int id = Reserve();
            _offsets[id] = _out.Position;
            Write($"{id} 0 obj\n<< /Length {body.Length}{filter} {dictionary} >>\nstream\n");
            _out.Write(body);
            Write("\nendstream\nendobj\n");
            return id;
        }

        public byte[] Finish(int root, int info)
        {
            long xref = _out.Position;
            StringBuilder b = new();
            b.Append(Invariant, $"xref\n0 {_offsets.Count}\n0000000000 65535 f \n");
            for (int i = 1; i < _offsets.Count; i++)
                b.Append(Invariant, $"{_offsets[i]:D10} 00000 n \n");
            b.Append(Invariant, $"trailer\n<< /Size {_offsets.Count} /Root {root} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Write(b.ToString());
            return _out.ToArray();
        }

        private void Write(string s) => _out.Write(Encoding.Latin1.GetBytes(s));
    }
}
