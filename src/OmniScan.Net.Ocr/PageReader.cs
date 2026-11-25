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
        // The user's language; with English, another script the user has installed in Windows (an
        // English-speaking user who also reads Russian has the Russian recognizer).
        OcrEngine? native = string.IsNullOrEmpty(languageCode) || languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? OtherScriptEngine()
            : Engine(languageCode) ?? OtherScriptEngine();
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
                ? new TextWord(FixMixedScript(w.Text), w.X / scale, w.Y / scale, w.Width / scale, w.Height / scale, line)
                : w with { Text = FixMixedScript(w.Text), Line = line }))
            .ToList();
    }

    /// <summary>
    /// A Cyrillic word with a Latin "i" or a Serbian "Ј" in it is the Ukrainian "і" misread (no recognizer
    /// knows Ukrainian; the Russian one reads it): no real word mixes the two, so the letter is put right.
    /// </summary>
    internal static string FixMixedScript(string word)
    {
        if (word.IndexOfAny(['i', 'I', 'Ј', 'ј']) < 0 || !word.Any(c => c is >= 'А' and <= 'я'))
            return word;
        if (word.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' && c is not ('i' or 'I')))
            return word;   // other Latin letters too: not a Cyrillic word with one wrong letter
        // A capital in the middle of a word is a small letter read too big.
        char[] letters = word.ToCharArray();
        for (int i = 0; i < letters.Length; i++)
        {
            if (letters[i] is 'i' or 'ј' or 'I' or 'Ј')
                letters[i] = i == 0 && letters[i] is 'I' or 'Ј' ? 'І' : 'і';
        }

        return new string(letters);
    }

    /// <summary>
    /// Reads the page and checks which way up it is by what was read: text read upside down or sideways
    /// comes out as letter garbage, without the small common words every text has ("the", "and", "и",
    /// "не"). The page is read as it is and turned over, and sideways too when neither has such words;
    /// another turn is taken only when it has clearly more of them. Catches the pages whose letter shapes
    /// did not tell (tables, figures, sparse text). <c>Turn</c> is the clockwise turn the words belong to.
    /// </summary>
    public static async Task<(int Turn, IReadOnlyList<TextWord> Words)> ReadUprightAsync(Pix page, string? languageCode,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TextWord> asIs = await ReadAsync(page, languageCode, cancellationToken).ConfigureAwait(false);
        int score = CommonWordCount(asIs);
        if (score >= SureCommonWords)
            return (0, asIs);

        (int Turn, IReadOnlyList<TextWord> Words, int Score) best = (0, asIs, score);
        foreach (int turn in new[] { 180, 90, 270 })
        {
            // Sideways only when neither way up reads like text at all.
            if (turn != 180 && Math.Max(score, best.Score) >= MinCommonWords)
                break;
            IReadOnlyList<TextWord> words = await ReadAsync(FrameCodec.Rotate(page, turn), languageCode, cancellationToken).ConfigureAwait(false);
            int s = CommonWordCount(words);
            if (s > best.Score)
                best = (turn, words, s);
        }

        return best.Turn != 0 && best.Score >= MinCommonWords && best.Score >= 2 * score + 1
            ? (best.Turn, best.Words)
            : (0, asIs);
    }

    /// <summary>Common words that make a reading certain at once: the page is not checked turned.</summary>
    internal const int SureCommonWords = 8;

    /// <summary>Common words another turn needs (and twice as many as the page as it is, plus one).</summary>
    internal const int MinCommonWords = 3;

    /// <summary>
    /// Small words of English, Russian and Ukrainian, two letters and more: one-letter ones ("a", "и",
    /// "в") come out of garbage too often to count.
    /// </summary>
    private static readonly HashSet<string> CommonWords = new(
        ("the of and to in is for that on with as by are be this it from or at your you not an will if all can we may " +
         "our has have was which their more any other such no do " +
         "не на что он как то это по но из за от так для же все бы ты мы вы до если или только уже его она они был " +
         "при про над под без " +
         "що та це від ми ви він вона але як щоб або вже які")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    /// <summary>How many of the words read are such common words.</summary>
    internal static int CommonWordCount(IEnumerable<TextWord> words)
    {
        int count = 0;
        foreach (TextWord word in words)
        {
            string w = word.Text.Trim().Trim(Punctuation).ToLowerInvariant();
            if (w.Length >= 2 && CommonWords.Contains(w))
                count++;
        }

        return count;
    }

    private static readonly char[] Punctuation = ".,;:!?\"'«»()[]{}".ToCharArray();

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

    /// <summary>A recognizer for the first installed language not written in Latin letters, or null.</summary>
    private static OcrEngine? OtherScriptEngine()
    {
        try
        {
            Language? other = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.Script is { Length: > 0 } s && s != "Latn");
            return other is null ? null : OcrEngine.TryCreateFromLanguage(other);
        }
        catch (COMException)
        {
            return null;
        }
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
