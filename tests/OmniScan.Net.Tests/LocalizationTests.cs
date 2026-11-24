using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmniScan.Net.Tests;

/// <summary>Checks the translation files in src/OmniScan.Net.App/Localization against English and Strings.cs.</summary>
public sealed partial class LocalizationTests
{
    private static readonly string AppDir = FindAppDir();

    public static TheoryData<string> Languages()
    {
        TheoryData<string> data = [];
        foreach (string file in Directory.GetFiles(Path.Combine(AppDir, "Localization"), "*.json"))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    [Fact]
    public void ManyLanguagesArePresent()
    {
        Assert.True(Directory.GetFiles(Path.Combine(AppDir, "Localization"), "*.json").Length >= 25);
    }

    [Fact]
    public void EveryStringsPropertyHasAnEnglishText()
    {
        string source = File.ReadAllText(Path.Combine(AppDir, "Strings.cs"));
        string[] keys = KeyPattern().Matches(source).Select(m => m.Groups[1].Value).ToArray();
        Dictionary<string, string> en = Read("en");

        Assert.NotEmpty(keys);
        string[] missing = keys.Where(k => !en.ContainsKey(k)).ToArray();
        string[] unused = en.Keys.Where(k => k != "_name" && !keys.Contains(k)).ToArray();
        Assert.True(missing.Length == 0, "Missing in en.json: " + string.Join(", ", missing));
        Assert.True(unused.Length == 0, "Unused in en.json: " + string.Join(", ", unused));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TranslationMatchesEnglish(string code)
    {
        Dictionary<string, string> en = Read("en");
        Dictionary<string, string> tr = Read(code);

        Assert.Equal(en.Keys.Order(), tr.Keys.Order());
        Assert.All(tr, p => Assert.False(string.IsNullOrWhiteSpace(p.Value), p.Key));
        foreach ((string key, string text) in en)
        {
            foreach (Match placeholder in PlaceholderPattern().Matches(text))
                Assert.Contains(placeholder.Value, tr[key]);
        }

        // The format strings must stay usable with string.Format.
        _ = string.Format(tr["SavedFormat"], "scan_001.jpg", 12.0);
        _ = string.Format(tr["PhotosFoundFormat"], 3);
    }

    private static Dictionary<string, string> Read(string code)
    {
        using FileStream stream = File.OpenRead(Path.Combine(AppDir, "Localization", code + ".json"));
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    private static string FindAppDir()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "src", "OmniScan.Net.App");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("src/OmniScan.Net.App not found above the test output folder.");
    }

    [GeneratedRegex(@"public static string (\w+) => T\(\);")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\{\d(:[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
