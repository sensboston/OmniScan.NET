using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace OmniScan.Net.App;

/// <summary>A language the UI is translated into: its code and its name in that language.</summary>
public sealed record LanguageInfo(string Code, string NativeName);

/// <summary>Translation tables embedded as Localization/{code}.json (flat "Key": "text" objects).</summary>
public static class Localization
{
    public const string Fallback = "en";

    private const string Prefix = "OmniScan.Localization.";
    private const string NameKey = "_name";

    /// <summary>All embedded languages, sorted by native name.</summary>
    public static IReadOnlyList<LanguageInfo> Available { get; } = FindLanguages();

    /// <summary>English overlaid with the requested language (or the Windows language when null/empty).</summary>
    public static IReadOnlyDictionary<string, string> Load(string? code)
    {
        Dictionary<string, string> table = Read(Fallback) ?? [];
        string resolved = Resolve(code);
        if (resolved != Fallback && Read(resolved) is { } translated)
        {
            foreach ((string key, string value) in translated)
                table[key] = value;
        }

        return table;
    }

    /// <summary>The language actually used for a setting value: an exact match, the parent language, or English.</summary>
    public static string Resolve(string? code)
    {
        CultureInfo culture = string.IsNullOrEmpty(code) ? CultureInfo.CurrentUICulture : SafeCulture(code);
        for (CultureInfo c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (Available.Any(l => string.Equals(l.Code, c.Name, StringComparison.OrdinalIgnoreCase)))
                return Available.First(l => string.Equals(l.Code, c.Name, StringComparison.OrdinalIgnoreCase)).Code;
        }

        // Chinese cultures are named zh-CN / zh-Hans; the table is "zh".
        if (culture.TwoLetterISOLanguageName is { } two && Available.Any(l => l.Code == two))
            return two;
        return Fallback;
    }

    private static CultureInfo SafeCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static List<LanguageInfo> FindLanguages()
    {
        List<LanguageInfo> found = [];
        foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
                continue;
            string code = resource[Prefix.Length..^".json".Length];
            string name = Read(code)?.GetValueOrDefault(NameKey) ?? code;
            found.Add(new LanguageInfo(code, name));
        }

        return found.OrderBy(l => l.NativeName, StringComparer.InvariantCultureIgnoreCase).ToList();
    }

    private static Dictionary<string, string>? Read(string code)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + code + ".json");
        if (stream is null)
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stream);
            Dictionary<string, string> table = new(StringComparer.Ordinal);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    table[p.Name] = p.Value.GetString()!;
            }

            return table;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
