using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Leptonica;
using OmniScan.Net.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OmniScan.Net.Ocr;

/// <summary>A line of recognized words, in reading order.</summary>
internal sealed record ReadLine(IReadOnlyList<TextWord> Words)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));

    public (double X, double Y, double Right, double Bottom) Box => (
        Words.Min(w => w.X), Words.Min(w => w.Y), Words.Max(w => w.X + w.Width), Words.Max(w => w.Y + w.Height));
}

/// <summary>
/// Reads the text of a page with the text recognition built into Windows: on this computer, nothing is
/// sent anywhere. A recognizer knows one language, and Windows has those of its installed language
/// packs. A language written in Latin letters (German, French) is read by its own recognizer alone: it
/// reads English as well. For another script (Russian, Greek) the page is read twice, in that language
/// and in English: a page mostly in the other script takes, line by line, the reading with mostly such
/// letters, and the English one for the rest (a Russian letter with an English address); a page mostly
/// in Latin letters is English, and the few look-alike letters ("Раде 1" for "Page 1") are ignored.
/// </summary>
public static class PageReader
{
    /// <summary>Some text recognizer is installed (Windows 10 and later have at least the display language).</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch (Exception e) when (e is COMException or TypeLoadException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The words on <paramref name="page"/> with their boxes in its pixels, or an empty list when no
    /// recognizer is installed. <paramref name="languageCode"/> is the user's language ("ru", "de").
    /// </summary>
    public static async Task<IReadOnlyList<TextWord>> ReadAsync(Pix page, string? languageCode, CancellationToken cancellationToken = default)
    {
        OcrEngine? native = string.IsNullOrEmpty(languageCode) || languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? null
            : Engine(languageCode);
        OcrEngine? english = native is not null && IsLatinScript(native) ? null : Engine("en-US") ?? Engine("en");
        if (english is null && native is null)
        {
            try
            {
                english = OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch (COMException)
            {
                english = null;
            }

            if (english is null)
                return [];
        }

        // Large pages are read smaller (the limit is about 10000 pixels a side); boxes are scaled back.
        double scale = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(page.Width, page.Height));
        using SoftwareBitmap bitmap = ToBitmap(scale < 1.0 ? page.Scale((float)scale, (float)scale) : page);

        IReadOnlyList<ReadLine> first = english is null ? [] : await ReadLinesAsync(english, bitmap, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ReadLine>? second = native is null ? null : await ReadLinesAsync(native, bitmap, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ReadLine> lines = english is null ? second ?? [] : second is null ? first : Merge(first, second);

        return lines.SelectMany((l, line) => l.Words.Select(w => scale < 1.0
                ? new TextWord(w.Text, w.X / scale, w.Y / scale, w.Width / scale, w.Height / scale, line)
                : w with { Line = line }))
            .ToList();
    }

    /// <summary>
    /// The English lines, with each line read in the other script put in place of the English lines it
    /// covers when it is mostly in that script; only on a page that is in that script at all
    /// (<see cref="MinPageShare"/>). English reading order is kept.
    /// </summary>
    internal static IReadOnlyList<ReadLine> Merge(IReadOnlyList<ReadLine> english, IReadOnlyList<ReadLine> native)
    {
        if (NonLatinShare(string.Concat(native.Select(l => l.Text))) < MinPageShare)
            return english;

        List<ReadLine?> result = [.. english];
        List<ReadLine> unplaced = [];
        foreach (ReadLine line in native.Where(l => NonLatinShare(l.Text) >= MinLineShare))
        {
            int first = -1;
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i] is { } other && Overlap(other, line))
                {
                    if (first < 0)
                        first = i;
                    result[i] = null;
                }
            }

            if (first >= 0)
                result[first] = line;
            else
                unplaced.Add(line);
        }

        List<ReadLine> lines = result.OfType<ReadLine>().ToList();
        // A line English did not see at all goes before the first line below it.
        foreach (ReadLine line in unplaced)
        {
            int at = lines.FindIndex(l => l.Box.Y > line.Box.Y);
            lines.Insert(at < 0 ? lines.Count : at, line);
        }

        return lines;
    }

    /// <summary>Share of letters outside the Latin script that a page needs to be read as a page in that script.</summary>
    internal const double MinPageShare = 0.25;

    /// <summary>Share of such letters that a line needs to be taken from the reading in that script.</summary>
    internal const double MinLineShare = 0.5;

    /// <summary>Which share of the letters are not Latin ones (accented Latin letters count as Latin).</summary>
    internal static double NonLatinShare(string text)
    {
        int letters = 0, other = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c))
                continue;
            letters++;
            if (c > 0x24F)
                other++;
        }

        return letters == 0 ? 0 : (double)other / letters;
    }

    private static bool IsLatinScript(OcrEngine engine)
    {
        try
        {
            return engine.RecognizerLanguage.Script == "Latn";
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>The boxes share at least a third of the smaller one.</summary>
    private static bool Overlap(ReadLine a, ReadLine b)
    {
        var (ax, ay, ar, ab) = a.Box;
        var (bx, by, br, bb) = b.Box;
        double w = Math.Min(ar, br) - Math.Max(ax, bx), h = Math.Min(ab, bb) - Math.Max(ay, by);
        if (w <= 0 || h <= 0)
            return false;
        double smaller = Math.Min((ar - ax) * (ab - ay), (br - bx) * (bb - by));
        return w * h >= smaller / 3;
    }

    private static OcrEngine? Engine(string code)
    {
        try
        {
            Language language = new(code);
            return OcrEngine.IsLanguageSupported(language) ? OcrEngine.TryCreateFromLanguage(language) : null;
        }
        catch (Exception e) when (e is ArgumentException or COMException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<ReadLine>> ReadLinesAsync(OcrEngine engine, SoftwareBitmap bitmap, CancellationToken cancellationToken)
    {
        OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return result.Lines
            .Select(l => new ReadLine(l.Words
                .Select(w => new TextWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
                .ToList()))
            .Where(l => l.Words.Count > 0)
            .ToList();
    }

    /// <summary>The page as an 8-bit gray bitmap, the cheapest form the recognizer takes.</summary>
    private static SoftwareBitmap ToBitmap(Pix page)
    {
        Pix gray = page.Depth switch
        {
            32 => page.ConvertRGBToLuminance(),
            8 when page.Colormap is null => page,
            _ => page.ConvertTo8(false),
        };
        if (gray.Colormap is not null)
            gray = gray.RemoveColormap(L.REMOVE_CMAP_TO_GRAYSCALE);

        int w = gray.Width, h = gray.Height, wpl = gray.Wpl;
        uint[] data = gray.Data;
        byte[] bytes = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * wpl, o = y * w;
            for (int x = 0; x < w; x++)
                bytes[o + x] = (byte)(data[row + (x >> 2)] >> (24 - 8 * (x & 3)));
        }

        SoftwareBitmap bitmap = new(BitmapPixelFormat.Gray8, w, h);
        bitmap.CopyFromBuffer(bytes.AsBuffer());
        return bitmap;
    }
}
