using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OmniScan.Net.Core;

/// <summary>Everything the user chose last time. Restored on start, saved on every change.</summary>
public sealed class AppSettings
{
    public static readonly int[] StandardDpi = [75, 150, 200, 300, 600, 1200];

    /// <summary>Backend-specific id of the last used device; null when none was chosen yet.</summary>
    public string? DeviceId { get; set; }

    public ScanSource Source { get; set; } = ScanSource.Flatbed;
    public int Dpi { get; set; } = 300;
    public ColorMode ColorMode { get; set; } = ColorMode.Color;
    public OutputFormat Format { get; set; } = OutputFormat.Jpeg;
    public const int MinJpegQuality = 50;
    public const int DefaultJpegQuality = 75;

    /// <summary>JPEG quality for JPEG files and for the gray and color images inside PDFs.</summary>
    public int JpegQuality { get; set; } = DefaultJpegQuality;
    public string RootFolder { get; set; } = DefaultRootFolder;

    public bool AutoCrop { get; set; } = true;
    public bool SplitPhotos { get; set; } = true;
    /// <summary>Straighten: deskew, and turn upright by the text when that is certain.</summary>
    public bool Deskew { get; set; } = true;

    /// <summary>Seconds before the next PDF page is scanned by itself; 0 turns it off.</summary>
    public int AutoContinueSeconds { get; set; } = 10;

    public bool ServerEnabled { get; set; }

    public UiTheme Theme { get; set; } = UiTheme.Light;

    /// <summary>UI language code ("de", "uk", ...); null follows the Windows display language.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Fixed clockwise rotation (0/90/180/270) applied to every frame of a device before any processing,
    /// for scanners whose "top" is not where people put the top of the page. Keyed by device id.
    /// </summary>
    public Dictionary<string, int> DeviceRotation { get; set; } = [];

    /// <summary>Thread-safe: the scan server asks from its own threads. Locks <see cref="DeviceRotation"/>.</summary>
    public int GetRotation(string? deviceId)
    {
        if (deviceId is null)
            return 0;
        lock (DeviceRotation)
            return DeviceRotation.TryGetValue(deviceId, out int r) ? r : 0;
    }

    /// <summary>Thread-safe, see <see cref="GetRotation"/>.</summary>
    public void SetRotation(string deviceId, int degrees)
    {
        lock (DeviceRotation)
        {
            if (degrees == 0)
                DeviceRotation.Remove(deviceId);
            else
                DeviceRotation[deviceId] = degrees;
        }
    }

    public static bool IsValidRotation(int degrees) => degrees is 0 or 90 or 180 or 270;

    public WindowPlacement? Window { get; set; }

    public static string DefaultRootFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Scans");

    /// <summary>Replaces out-of-range values (hand-edited or from an older version) with defaults.</summary>
    public void Normalize()
    {
        if (Dpi < 50 || Dpi > 4800)
            Dpi = 300;
        if (AutoContinueSeconds < 0 || AutoContinueSeconds > 120)
            AutoContinueSeconds = 10;
        if (JpegQuality < MinJpegQuality || JpegQuality > 100)
            JpegQuality = DefaultJpegQuality;
        if (string.IsNullOrWhiteSpace(RootFolder) || !Path.IsPathFullyQualified(RootFolder))
            RootFolder = DefaultRootFolder;
        if (!Enum.IsDefined(Source))
            Source = ScanSource.Flatbed;
        if (!Enum.IsDefined(ColorMode))
            ColorMode = ColorMode.Color;
        if (!Enum.IsDefined(Theme))
            Theme = UiTheme.Light;
        if (!Enum.IsDefined(Format))
            Format = OutputFormat.Jpeg;
        DeviceRotation ??= [];
        foreach (string id in DeviceRotation.Keys.ToArray())
        {
            if (!IsValidRotation(DeviceRotation[id]) || DeviceRotation[id] == 0)
                DeviceRotation.Remove(id);
        }

        if (Window is { } w && (w.Width < 200 || w.Height < 150 || double.IsNaN(w.Left) || double.IsNaN(w.Top)))
            Window = null;
    }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
