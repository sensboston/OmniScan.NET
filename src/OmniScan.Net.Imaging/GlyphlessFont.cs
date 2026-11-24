using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OmniScan.Net.Imaging;

/// <summary>
/// A TrueType font with no visible glyphs: two empty glyphs, each half an em wide. It carries the
/// invisible text layer of a scanned PDF, where only the positions of the characters matter (for
/// selecting, copying and searching), so that no real font has to be embedded or found by the viewer.
/// </summary>
internal static class GlyphlessFont
{
    public const int UnitsPerEm = 1000;
    public const int Advance = 500;
    public const string Name = "GlyphLessFont";

    private const uint Created = 3850070400;

    private static readonly Lazy<byte[]> Bytes = new(Build);

    public static byte[] Data => Bytes.Value;

    private static byte[] Build()
    {
        SortedDictionary<string, byte[]> tables = new(StringComparer.Ordinal)
        {
            ["OS/2"] = Os2(),
            ["cmap"] = Cmap(),
            ["glyf"] = new byte[4],   // both glyphs are empty (equal loca offsets); a table is still there
            ["head"] = Head(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Table(w => { for (int i = 0; i < 2; i++) { w.U16(Advance); w.U16(0); } }),
            ["loca"] = Table(w => { for (int i = 0; i < 3; i++) w.U16(0); }),
            ["maxp"] = Maxp(),
            ["name"] = NameTable(),
            ["post"] = Table(w => { w.U32(0x00030000); w.U32(0); w.U16(unchecked((ushort)-100)); w.U16(50); w.U32(1); w.U32(0); w.U32(0); w.U32(0); w.U32(0); }),
        };

        int count = tables.Count;
        int entrySelector = (int)Math.Floor(Math.Log2(count));
        int searchRange = (1 << entrySelector) * 16;
        Writer font = new();
        font.U32(0x00010000);
        font.U16(count);
        font.U16(searchRange);
        font.U16(entrySelector);
        font.U16(count * 16 - searchRange);

        int offset = 12 + count * 16;
        foreach ((string tag, byte[] data) in tables)
        {
            font.Raw(Encoding.ASCII.GetBytes(tag));
            font.U32(Checksum(data));
            font.U32(offset);
            font.U32(data.Length);
            offset += Padded(data.Length);
        }

        int headOffset = 0;
        foreach ((string tag, byte[] data) in tables)
        {
            if (tag == "head")
                headOffset = font.Length;
            font.Raw(data);
            font.Raw(new byte[Padded(data.Length) - data.Length]);
        }

        byte[] bytes = font.ToArray();
        uint adjustment = unchecked(0xB1B0AFBA - Checksum(bytes));
        bytes[headOffset + 8] = (byte)(adjustment >> 24);
        bytes[headOffset + 9] = (byte)(adjustment >> 16);
        bytes[headOffset + 10] = (byte)(adjustment >> 8);
        bytes[headOffset + 11] = (byte)adjustment;
        return bytes;
    }

    private static byte[] Head() => Table(w =>
    {
        w.U32(0x00010000);          // version
        w.U32(0x00010000);          // font revision
        w.U32(0);                   // checksum adjustment, set at the end
        w.U32(0x5F0F3CF5);          // magic
        w.U16(0x000B);              // flags: baseline at 0, integer scaling
        w.U16(UnitsPerEm);
        w.U32(0); w.U32(Created);   // created: 2026-01-01, seconds since 1904
        w.U32(0); w.U32(Created);   // modified
        w.U16(0); w.U16(unchecked((ushort)-200)); w.U16(Advance); w.U16(800);   // bounding box
        w.U16(0);                   // mac style
        w.U16(3);                   // smallest readable size
        w.U16(2);                   // font direction hint
        w.U16(0);                   // short loca offsets
        w.U16(0);                   // glyph data format
    });

    private static byte[] Hhea() => Table(w =>
    {
        w.U32(0x00010000);
        w.U16(800); w.U16(unchecked((ushort)-200)); w.U16(0);   // ascender, descender, line gap
        w.U16(Advance);                                       // largest advance
        w.U16(0); w.U16(0); w.U16(0);                         // min side bearings, max extent
        w.U16(1); w.U16(0); w.U16(0);                         // caret: vertical
        for (int i = 0; i < 4; i++)
            w.U16(0);
        w.U16(0);                                             // metric data format
        w.U16(2);                                             // advances listed for both glyphs
    });

    private static byte[] Maxp() => Table(w =>
    {
        w.U32(0x00010000);
        w.U16(2);                   // glyphs
        for (int i = 0; i < 4; i++)
            w.U16(0);               // points, contours, composite points and contours
        w.U16(2);                   // zones
        for (int i = 0; i < 8; i++)
            w.U16(0);
    });

    private static byte[] Cmap() => Table(w =>
    {
        w.U16(0); w.U16(1);                     // version, one subtable
        w.U16(3); w.U16(1); w.U32(12);          // Windows, Unicode BMP
        // Format 4 with only the closing segment: nothing is mapped (the PDF maps codes to glyphs itself).
        w.U16(4); w.U16(24); w.U16(0);
        w.U16(2); w.U16(2); w.U16(0); w.U16(0); // one segment
        w.U16(0xFFFF); w.U16(0);                // end codes, pad
        w.U16(0xFFFF);                          // start codes
        w.U16(1);                               // delta
        w.U16(0);                               // range offsets
    });

    private static byte[] Os2() => Table(w =>
    {
        w.U16(1);                               // version
        w.U16(Advance);                         // average width
        w.U16(400); w.U16(5);                   // weight, width class
        w.U16(0);                               // embedding: installable
        for (int i = 0; i < 10; i++)
            w.U16(0);                           // sub/superscript, strikeout metrics
        w.U16(0);                               // family class
        w.Raw(new byte[10]);                    // PANOSE
        for (int i = 0; i < 4; i++)
            w.U32(0);                           // Unicode ranges
        w.Raw("NONE"u8.ToArray());              // vendor
        w.U16(0x0040);                          // regular
        w.U16(0); w.U16(0xFFFF);                // first, last character
        w.U16(800); w.U16(unchecked((ushort)-200)); w.U16(0);   // typographic ascender, descender, gap
        w.U16(800); w.U16(200);                 // Windows ascent, descent
        w.U32(1); w.U32(0);                     // code pages: Latin 1
    });

    private static byte[] NameTable()
    {
        (int Id, string Text)[] names = [(1, Name), (2, "Regular"), (4, Name), (6, Name)];
        byte[][] strings = names.Select(n => Encoding.BigEndianUnicode.GetBytes(n.Text)).ToArray();
        return Table(w =>
        {
            w.U16(0);
            w.U16(names.Length);
            w.U16(6 + names.Length * 12);
            int at = 0;
            for (int i = 0; i < names.Length; i++)
            {
                w.U16(3); w.U16(1); w.U16(0x0409);
                w.U16(names[i].Id);
                w.U16(strings[i].Length);
                w.U16(at);
                at += strings[i].Length;
            }

            foreach (byte[] s in strings)
                w.Raw(s);
        });
    }

    private static int Padded(int length) => (length + 3) & ~3;

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++)
                word = (word << 8) | (i + k < data.Length ? data[i + k] : 0u);
            sum = unchecked(sum + word);
        }

        return sum;
    }

    private static byte[] Table(Action<Writer> write)
    {
        Writer w = new();
        write(w);
        return w.ToArray();
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public int Length => (int)_stream.Length;

        public void U16(int v)
        {
            _stream.WriteByte((byte)(v >> 8));
            _stream.WriteByte((byte)v);
        }

        public void U32(uint v)
        {
            U16((int)(v >> 16));
            U16((int)(v & 0xFFFF));
        }

        public void U32(int v) => U32(unchecked((uint)v));

        public void Raw(byte[] bytes) => _stream.Write(bytes);

        public byte[] ToArray() => _stream.ToArray();
    }
}
