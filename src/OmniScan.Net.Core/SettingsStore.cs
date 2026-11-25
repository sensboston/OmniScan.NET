using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OmniScan.Net.Core;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as a plain INI file. Never throws on a missing or broken
/// file: unknown lines are ignored and out-of-range values fall back to defaults.
/// </summary>
public sealed class SettingsStore
{
    public const string FileName = "OmniScan.ini";

    public SettingsStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>
    /// Portable by default: the INI file next to the program. When that folder is not writable
    /// (for example under Program Files) the file lives in %AppData%\OmniScan instead.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            string? exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir is not null && IsWritable(exeDir))
                return System.IO.Path.Combine(exeDir, FileName);
            return System.IO.Path.Combine(AppDataFolder, FileName);
        }
    }

    private static string AppDataFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniScan");

    public AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(Path))
                settings = Parse(File.ReadAllLines(Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            settings = null;
        }

        settings ??= new AppSettings();
        settings.Normalize();
        return settings;
    }

    /// <summary>Writes to a temporary file and moves it over the old one, so a crash never leaves half a file.</summary>
    public void Save(AppSettings settings)
    {
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string temp = Path + ".tmp";
        File.WriteAllText(temp, Format(settings), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, Path, overwrite: true);
    }

    internal static string Format(AppSettings s)
    {
        StringBuilder b = new();
        b.AppendLine("; OmniScan settings. Edit with care; unknown lines are ignored.");
        Section(b, "Scan");
        Key(b, "Device", s.DeviceId ?? "");
        Key(b, "Source", s.Source.ToString());
        Key(b, "Dpi", s.Dpi);
        Key(b, "Color", s.ColorMode.ToString());
        Key(b, "Format", s.Format.ToString());
        Key(b, "JpegQuality", s.JpegQuality);
        Key(b, "Folder", s.RootFolder);
        Section(b, "Processing");
        Key(b, "SeparatePhotos", s.SplitPhotos);
        Key(b, "CropMargins", s.AutoCrop);
        Key(b, "Straighten", s.Deskew);
        Section(b, "Pdf");
        Key(b, "AutoContinueSeconds", s.AutoContinueSeconds);
        Key(b, "RecognizeText", s.PdfText);
        Section(b, "App");
        Key(b, "Language", s.Language ?? "");
        Key(b, "Theme", s.Theme.ToString());
        Key(b, "ShareScanners", s.ServerEnabled);
        Section(b, "Assist");
        Key(b, "Key", string.IsNullOrEmpty(s.AssistKey) ? "" : SecretText.Protect(s.AssistKey));
        // The tested models, so that the line can be filled in without looking anything up. The dearer choice
        // goes after the line, commented out: uncommented, it is the last "Model" and wins.
        b.AppendLine("; Model: the model to use first; empty = chosen by the program for the key's service.");
        b.AppendLine("; A busy or closed model gives way to the next one. Tested, chosen by default:");
        b.AppendLine(";   OpenAI     gpt-6-luna");
        b.AppendLine(";   Google     gemini-3.5-flash-lite   (works with a free key)");
        b.AppendLine(";   DeepSeek   deepseek-flash");
        b.AppendLine("; Not tested yet: Anthropic, xAI, OpenRouter.");
        Key(b, "Model", s.AssistModel ?? "");
        if (!string.Equals(s.AssistModel, "gpt-6.1-terra", StringComparison.OrdinalIgnoreCase))
        {
            b.AppendLine("; OpenAI: turns more photos right (also those where only a sign shows which way is up),");
            b.AppendLine("; at ten times the price:");
            b.AppendLine("; Model = gpt-6.1-terra");
        }
        Key(b, "TurnPhotos", s.AssistOrient);
        Key(b, "DescribePhotos", s.AssistDescribe);
        if (s.Window is { } w)
        {
            Section(b, "Window");
            Key(b, "Left", w.Left);
            Key(b, "Top", w.Top);
            Key(b, "Width", w.Width);
            Key(b, "Height", w.Height);
            Key(b, "Maximized", w.Maximized);
        }

        // Device ids contain no '=' or line breaks, so they serve as keys as they are.
        KeyValuePair<string, int>[] flips;
        lock (s.DeviceRotation)
            flips = s.DeviceRotation.Where(p => p.Value != 0).ToArray();
        if (flips.Length > 0)
        {
            Section(b, "Rotation");
            foreach ((string id, int degrees) in flips)
                Key(b, id, degrees);
        }

        if (s.OtherSections.Count > 0)
        {
            b.AppendLine();
            foreach (string line in s.OtherSections)
                b.AppendLine(line);
        }

        return b.ToString();
    }

    /// <summary>The sections this version reads; any other is kept as it is (<see cref="AppSettings.OtherSections"/>).</summary>
    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Scan", "Processing", "Pdf", "App", "Window", "Rotation", "Assist",
    };

    internal static AppSettings Parse(IEnumerable<string> lines)
    {
        AppSettings s = new();
        string section = "";
        bool other = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length > 1 && line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                other = !KnownSections.Contains(section);
                if (other)
                    s.OtherSections.Add(line);
                continue;
            }

            if (other)
            {
                if (line.Length > 0)
                    s.OtherSections.Add(raw.TrimEnd());
                continue;
            }

            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();

            switch (section.ToLowerInvariant(), key.ToLowerInvariant())
            {
                case ("scan", "device"): s.DeviceId = value.Length == 0 ? null : value; break;
                case ("scan", "source"): s.Source = Enum<ScanSource>(value, s.Source); break;
                case ("scan", "dpi"): s.Dpi = Int(value, s.Dpi); break;
                case ("scan", "color"): s.ColorMode = Enum<ColorMode>(value, s.ColorMode); break;
                case ("scan", "format"): s.Format = Enum<OutputFormat>(value, s.Format); break;
                case ("scan", "jpegquality"): s.JpegQuality = Int(value, s.JpegQuality); break;
                case ("scan", "folder"): s.RootFolder = value; break;
                case ("processing", "separatephotos"): s.SplitPhotos = Bool(value, s.SplitPhotos); break;
                case ("processing", "cropmargins"): s.AutoCrop = Bool(value, s.AutoCrop); break;
                case ("processing", "straighten"): s.Deskew = Bool(value, s.Deskew); break;
                case ("pdf", "autocontinueseconds"): s.AutoContinueSeconds = Int(value, s.AutoContinueSeconds); break;
                case ("pdf", "recognizetext"): s.PdfText = Bool(value, s.PdfText); break;
                case ("app", "language"): s.Language = value.Length == 0 ? null : value; break;
                case ("app", "theme"): s.Theme = Enum<UiTheme>(value, s.Theme); break;
                case ("app", "sharescanners"): s.ServerEnabled = Bool(value, s.ServerEnabled); break;
                case ("assist", "key"): s.AssistKey = value.Length == 0 ? null : SecretText.Unprotect(value); break;
                case ("assist", "model"): s.AssistModel = value.Length == 0 ? null : value; break;
                case ("assist", "turnphotos"): s.AssistOrient = Bool(value, s.AssistOrient); break;
                case ("assist", "describephotos"): s.AssistDescribe = Bool(value, s.AssistDescribe); break;
                case ("window", "left"): (s.Window ??= new WindowPlacement()).Left = Double(value); break;
                case ("window", "top"): (s.Window ??= new WindowPlacement()).Top = Double(value); break;
                case ("window", "width"): (s.Window ??= new WindowPlacement()).Width = Double(value); break;
                case ("window", "height"): (s.Window ??= new WindowPlacement()).Height = Double(value); break;
                case ("window", "maximized"): (s.Window ??= new WindowPlacement()).Maximized = Bool(value, false); break;
                case ("rotation", _): s.SetRotation(key, Int(value, 0)); break;
            }
        }

        return s;
    }

    private static void Section(StringBuilder b, string name) => b.AppendLine().Append('[').Append(name).AppendLine("]");

    private static void Key(StringBuilder b, string key, string value) => b.Append(key).Append(" = ").AppendLine(value);

    private static void Key(StringBuilder b, string key, int value) => Key(b, key, value.ToString(CultureInfo.InvariantCulture));

    private static void Key(StringBuilder b, string key, double value) => Key(b, key, value.ToString("0.##", CultureInfo.InvariantCulture));

    private static void Key(StringBuilder b, string key, bool value) => Key(b, key, value ? "true" : "false");

    private static int Int(string v, int fallback) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static double Double(string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;

    private static bool Bool(string v, bool fallback) => v.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => fallback,
    };

    private static T Enum<T>(string v, T fallback)
        where T : struct, System.Enum =>
        System.Enum.TryParse(v, ignoreCase: true, out T e) && System.Enum.IsDefined(e) ? e : fallback;

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = System.IO.Path.Combine(dir, $".omniscan-write-test-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

