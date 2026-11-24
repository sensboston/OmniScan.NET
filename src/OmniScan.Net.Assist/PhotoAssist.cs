using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Leptonica;
using OmniScan.Net.Core;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.Assist;

/// <summary>What the service said about a photo; a field is null when it was not asked or not sure.</summary>
/// <param name="Rotation">Clockwise turn (0/90/180/270) that makes the photo upright.</param>
/// <param name="Description">A short caption in the asked language.</param>
public sealed record PhotoReport(int? Rotation, string? Description);

/// <summary>
/// Questions about a photo: which way is up, and what is on it. For the orientation the photo is sent
/// four times, turned each way, and the service picks the upright copy: models tell "which of these
/// looks right" much more reliably than "how many degrees is this turned".
/// </summary>
public static class PhotoAssist
{
    /// <summary>Longest side of each image sent, pixels: enough to see the scene, cheap to send.</summary>
    public const int ImageSide = 384;

    private static readonly int[] Turns = [0, 90, 180, 270];

    public static async Task<PhotoReport> LookAsync(AssistClient client, Pix photo, bool orient, bool describe,
        string? languageCode, CancellationToken cancellationToken = default)
    {
        if (!orient && !describe)
            return new PhotoReport(null, null);

        Pix small = Shrink(photo, ImageSide);
        List<byte[]> images = [];
        if (orient)
        {
            foreach (int turn in Turns)
                images.Add(Jpeg(FrameCodec.Rotate(small, turn)));
        }
        else
        {
            images.Add(Jpeg(small));
        }

        string prompt = Prompt(orient, describe, LanguageName(languageCode));
        string? answer = await client.AskAsync(prompt, images, 400, cancellationToken).ConfigureAwait(false);
        return Parse(answer, orient, describe);
    }

    internal static string Prompt(bool orient, bool describe, string language)
    {
        string fields;
        string rules = "";
        if (orient)
        {
            rules += "The 4 images are the same scanned photo turned 4 different ways (numbered 1 to 4 in the order given). " +
                     "Decide which one is upright, the way it was meant to be seen: people and buildings standing, " +
                     "sky and ceilings on top, text readable. If you cannot tell, use null. ";
        }

        if (describe)
        {
            rules += $"Write a short caption of what the photo shows, in {language}, at most 12 words, " +
                     "no \"photo of\" or \"image of\", no guesses about names. ";
        }

        fields = (orient, describe) switch
        {
            (true, true) => "{\"upright\": <1-4 or null>, \"caption\": \"...\"}",
            (true, false) => "{\"upright\": <1-4 or null>}",
            _ => "{\"caption\": \"...\"}",
        };
        return rules + "Answer with JSON only, exactly: " + fields;
    }

    internal static PhotoReport Parse(string? answer, bool orient, bool describe)
    {
        JsonObject? json = FindJson(answer);
        if (json is null)
            return new PhotoReport(null, null);

        int? rotation = null;
        if (orient && json["upright"] is JsonValue v && v.TryGetValue(out int n) && n is >= 1 and <= 4)
            rotation = Turns[n - 1];
        else if (orient && json["upright"] is JsonValue s && s.TryGetValue(out string? text)
                 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m is >= 1 and <= 4)
            rotation = Turns[m - 1];

        string? caption = null;
        if (describe && json["caption"] is JsonValue c && c.TryGetValue(out string? t) && !string.IsNullOrWhiteSpace(t))
            caption = t.Trim().TrimEnd('.');

        return new PhotoReport(rotation, caption);
    }

    /// <summary>The JSON object in an answer, tolerating code fences and words around it.</summary>
    internal static JsonObject? FindJson(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
            return null;
        int start = answer.IndexOf('{'), end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return AssistClient.ParseJson(answer[start..(end + 1)]) as JsonObject;
    }

    /// <summary>English name of a UI language for the prompt ("ru" → "Russian"); English when unknown.</summary>
    internal static string LanguageName(string? code)
    {
        if (string.IsNullOrEmpty(code))
            code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(code);
            return culture.IsNeutralCulture ? culture.EnglishName : culture.Parent.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return "English";
        }
    }

    internal static Pix Shrink(Pix pix, int maxSide)
    {
        int side = Math.Max(pix.Width, pix.Height);
        if (side <= maxSide)
            return pix;
        float k = (float)maxSide / side;
        Pix source = pix.Depth == 1 ? pix.ConvertTo8(false) : pix;
        return source.ScaleAreaMap(k, k);
    }

    internal static byte[] Jpeg(Pix pix) => FrameCodec.Encode(pix, OutputFormat.Jpeg, 80);
}
