using System;
using System.Globalization;
using System.IO;

namespace OmniScan.Net.Core;

/// <summary>Day folders and auto-incremented file names (<c>root\2026-11-23\scan_001.png</c>).</summary>
public static class ScanOutput
{
    public const string DefaultPrefix = "scan";

    public static string DayFolder(string root, DateTime date) =>
        Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates the folder if needed and opens a new file named <c>{prefix}_{NNN}{extension}</c>,
    /// numbered after the highest existing number for this prefix and extension.
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
        for (int number = NextNumber(folder, prefix, extension); ; number++)
        {
            string path = Path.Combine(folder, FileName(prefix, number, extension));
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Taken between the scan and the create; try the next number.
            }
        }
    }

    internal static string FileName(string prefix, int number, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}_{number:D3}{extension}");

    /// <summary>One past the highest <c>{prefix}_{digits}{extension}</c> in the folder; gaps are not reused.</summary>
    internal static int NextNumber(string folder, string prefix, string extension)
    {
        int max = 0;
        string head = prefix + "_";
        foreach (string path in Directory.EnumerateFiles(folder, head + "*" + extension))
        {
            string name = Path.GetFileName(path);
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(head, StringComparison.OrdinalIgnoreCase))
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
