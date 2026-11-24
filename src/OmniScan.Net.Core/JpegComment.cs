using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OmniScan.Net.Core;

/// <summary>
/// A text comment inside a JPEG file, where Windows Explorer shows it (Properties, Details: "Comments"
/// and "Subject"): the Windows XPComment and XPSubject tags of an Exif block. The image data is not
/// touched; an Exif block already in the file is replaced.
/// </summary>
public static class JpegComment
{
    private const ushort XpComment = 0x9C9C;
    private const ushort XpSubject = 0x9C9F;
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();

    /// <summary>The JPEG with the comment set (or with no Exif block when the text is empty).</summary>
    public static byte[] Set(byte[] jpeg, string? text)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("Not a JPEG file.");

        using MemoryStream output = new(jpeg.Length + 512);
        output.Write(jpeg, 0, 2);
        int pos = 2;
        bool written = string.IsNullOrEmpty(text);

        // Keep the segments before the image data, without old Exif; the new one goes after a JFIF header.
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            if (marker is 0xDA or 0xD9 || marker is < 0xE0 or > 0xEF && marker != 0xFE)
                break;
            int length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (pos + 2 + length > jpeg.Length)
                break;
            bool exif = marker == 0xE1 && StartsWith(jpeg, pos + 4, ExifHeader);
            if (!written && marker != 0xE0)
            {
                WriteExif(output, text!);
                written = true;
            }

            if (!exif)
                output.Write(jpeg, pos, 2 + length);
            pos += 2 + length;
        }

        if (!written)
            WriteExif(output, text!);
        output.Write(jpeg, pos, jpeg.Length - pos);
        return output.ToArray();
    }

    /// <summary>The comment of a JPEG, or null when it has none.</summary>
    public static string? Get(byte[] jpeg)
    {
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF && jpeg[pos + 1] != 0xDA)
        {
            int length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (jpeg[pos + 1] == 0xE1 && StartsWith(jpeg, pos + 4, ExifHeader))
                return ReadTag(jpeg, pos + 4 + ExifHeader.Length, Math.Min(jpeg.Length, pos + 2 + length), XpComment);
            pos += 2 + length;
        }

        return null;
    }

    private static void WriteExif(Stream output, string text)
    {
        byte[] value = Encoding.Unicode.GetBytes(text + "\0");
        (ushort Tag, byte[] Value)[] tags = [(XpComment, value), (XpSubject, value)];

        // Little-endian TIFF: header, one IFD (sorted tags, no next IFD), then the values.
        List<byte> tiff = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0];
        int dataOffset = 8 + 2 + tags.Length * 12 + 4;
        List<byte> data = [];
        Add16(tiff, tags.Length);
        foreach ((ushort tag, byte[] bytes) in tags)
        {
            Add16(tiff, tag);
            Add16(tiff, 1);                       // BYTE
            Add32(tiff, bytes.Length);
            Add32(tiff, dataOffset + data.Count);
            data.AddRange(bytes);
            if (data.Count % 2 == 1)
                data.Add(0);
        }

        Add32(tiff, 0);
        tiff.AddRange(data);

        int length = 2 + ExifHeader.Length + tiff.Count;
        if (length > 0xFFFF)
            throw new ArgumentException("The comment is too long.", nameof(text));
        output.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
        output.Write(ExifHeader);
        output.Write(tiff.ToArray());
    }

    private static string? ReadTag(byte[] b, int tiff, int end, ushort wanted)
    {
        if (tiff + 8 > end || b[tiff] != (byte)'I' || b[tiff + 1] != (byte)'I')
            return null;   // only what we write: little-endian
        int ifd = tiff + Read32(b, tiff + 4);
        if (ifd + 2 > end)
            return null;
        int count = Read16(b, ifd);
        for (int i = 0; i < count; i++)
        {
            int entry = ifd + 2 + i * 12;
            if (entry + 12 > end)
                return null;
            if (Read16(b, entry) != wanted)
                continue;
            int size = Read32(b, entry + 4);
            int at = size <= 4 ? entry + 8 : tiff + Read32(b, entry + 8);
            if (size < 0 || at + size > end)
                return null;
            return Encoding.Unicode.GetString(b, at, size).TrimEnd('\0');
        }

        return null;
    }

    private static bool StartsWith(byte[] b, int at, byte[] prefix) =>
        at + prefix.Length <= b.Length && b.AsSpan(at, prefix.Length).SequenceEqual(prefix);

    private static void Add16(List<byte> b, int v)
    {
        b.Add((byte)v);
        b.Add((byte)(v >> 8));
    }

    private static void Add32(List<byte> b, int v)
    {
        Add16(b, v);
        Add16(b, v >> 16);
    }

    private static int Read16(byte[] b, int at) => b[at] | (b[at + 1] << 8);

    private static int Read32(byte[] b, int at) => Read16(b, at) | (Read16(b, at + 2) << 16);
}
