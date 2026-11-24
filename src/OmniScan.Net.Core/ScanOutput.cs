using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OmniScan.Net.Core;

/// <summary>Day folders and auto-incremented file names (<c>root\2026-11-23\scan_001.png</c>).</summary>
public static class ScanOutput
{
    public const string DefaultPrefix = "scan";

    public static string DayFolder(string root, DateTime date) =>
        Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates the folder if needed and opens a new file named <c>{prefix}_{NNN}{extension}</c>.
    /// Numbering runs through all output formats: <c>scan_003.jpg</c> is followed by <c>scan_004.pdf</c>,
    /// so one number is one scan and sorting by name follows the order of scanning.
    /// Creation is atomic: a name taken meanwhile by another writer is skipped, nothing is overwritten.
    /// The caller owns the returned stream; its <see cref="FileStream.Name"/> is the full path.
    /// </summary>
    public static FileStream CreateNext(string folder, string extension, string prefix = DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (extension[0] != '.')
            extension = "." + extension;

        Directory.CreateDirectory(folder);
        string[] extensions = Extensions(extension);

        // All saves of this program (local and remote scans alike) go through here: choosing the number
        // under one lock keeps a JPEG and a PDF saved at the same moment from sharing a number.
        // CreateNew still guards the exact name against any other writer.
        lock (Gate)
        {
            for (int number = NextNumber(folder, prefix, extensions); ; number++)
            {
                if (extensions.Any(e => File.Exists(Path.Combine(folder, FileName(prefix, number, e)))))
                    continue;
                string path = Path.Combine(folder, FileName(prefix, number, extension));
                try
                {
                    return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Taken between the check and the create by another process; try the next number.
                }
            }
        }
    }

    private static readonly object Gate = new();

    internal static string FileName(string prefix, int number, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}_{number:D3}{extension}");

    /// <summary>The extensions that share one numbering: every output format, plus the one asked for.</summary>
    private static string[] Extensions(string extension) =>
        Enum.GetValues<OutputFormat>().Select(OutputFormats.Extension)
            .Append(extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// One past the highest <c>{prefix}_{digits}{ext}</c> in the folder over the given extensions;
    /// gaps are not reused.
    /// </summary>
    internal static int NextNumber(string folder, string prefix, IReadOnlyList<string> extensions)
    {
        int max = 0;
        string head = prefix + "_";
        foreach (string path in Directory.EnumerateFiles(folder, head + "*"))
        {
            string name = Path.GetFileName(path);
            string? extension = extensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (extension is null || !name.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                continue;

            ReadOnlySpan<char> digits = name.AsSpan(head.Length, name.Length - head.Length - extension.Length);
            if (digits.Length == 0 || !IsAllDigits(digits))
                continue;
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > max)
                max = n;
        }

        return max + 1;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c < '0' || c > '9')
                return false;
        }

        return true;
    }
}
