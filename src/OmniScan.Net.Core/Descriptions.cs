using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OmniScan.Net.Core;

/// <summary>
/// File descriptions in <c>descript.ion</c>, the file of 4DOS / Total Commander / Far Manager: one line per file,
/// <c>name description</c>, the name in quotes when it has spaces. Lines of other files are kept as they are.
/// The file is written in UTF-8 with a byte order mark (which those programs read) and is hidden, as
/// Total Commander makes it; an older file in the ANSI code page is read in that code page.
/// </summary>
public static class Descriptions
{
    public const string FileName = "descript.ion";

    private static readonly object Gate = new();

    /// <summary>Sets (or, with an empty text, removes) the description of <paramref name="file"/> in its folder.</summary>
    public static void Set(string file, string? description)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        string name = Path.GetFileName(file);
        string text = Clean(description);

        lock (Gate)
        {
            string path = Path.Combine(folder, FileName);
            List<string> lines = File.Exists(path) ? Read(path) : [];
            int at = lines.FindIndex(l => string.Equals(NameOf(l), name, StringComparison.OrdinalIgnoreCase));
            string? line = text.Length == 0 ? null : Quote(name) + " " + text;

            if (at >= 0 && line is null)
                lines.RemoveAt(at);
            else if (at >= 0)
                lines[at] = line!;
            else if (line is not null)
                lines.Add(line);
            else
                return;

            Write(path, lines);
        }
    }

    /// <summary>The description of <paramref name="file"/>, or null.</summary>
    public static string? Get(string file)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".", FileName);
        if (!File.Exists(path))
            return null;
        string name = Path.GetFileName(file);
        foreach (string line in Read(path))
        {
            if (string.Equals(NameOf(line), name, StringComparison.OrdinalIgnoreCase))
                return DescriptionOf(line);
        }

        return null;
    }

    /// <summary>The file name a line describes (quoted or up to the first space).</summary>
    internal static string NameOf(string line)
    {
        if (line.StartsWith('"'))
        {
            int close = line.IndexOf('"', 1);
            return close > 0 ? line[1..close] : line[1..];
        }

        int space = line.IndexOf(' ');
        return space < 0 ? line : line[..space];
    }

    internal static string DescriptionOf(string line)
    {
        int start = line.StartsWith('"') ? line.IndexOf('"', 1) + 1 : line.IndexOf(' ');
        return start <= 0 || start >= line.Length ? "" : line[start..].Trim();
    }

    private static string Quote(string name) => name.Contains(' ') ? "\"" + name + "\"" : name;

    /// <summary>One line, no control characters (a line break would start a new entry).</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        StringBuilder b = new(text.Length);
        foreach (char c in text.Trim())
            b.Append(char.IsControl(c) ? ' ' : c);
        return b.ToString().Trim();
    }

    private static List<string> Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = Ansi().GetString(bytes);
            }
        }

        List<string> lines = [];
        foreach (string line in text.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (l.Length > 0)
                lines.Add(l);
        }

        return lines;
    }

    private static void Write(string path, List<string> lines)
    {
        bool existed = File.Exists(path);
        byte[] body = Encoding.UTF8.GetBytes(lines.Count == 0 ? "" : string.Join("\r\n", lines) + "\r\n");

        // A hidden file cannot be re-created, only opened and truncated.
        using (FileStream f = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            f.SetLength(0);
            f.Write(Encoding.UTF8.Preamble);
            f.Write(body);
        }

        if (!existed)
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }

    private static Encoding Ansi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
    }
}
