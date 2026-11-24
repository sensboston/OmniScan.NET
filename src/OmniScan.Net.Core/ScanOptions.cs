namespace OmniScan.Net.Core;

public enum ColorMode
{
    Color,
    Grayscale,
    BlackWhite,
}

public enum ScanSource
{
    Flatbed,
    Feeder,
}

/// <summary>Look of the windows: light (default) or dark.</summary>
public enum UiTheme
{
    Light,
    Dark,
}

public enum OutputFormat
{
    Png,
    Jpeg,
    Pdf,
}

public static class OutputFormats
{
    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.Png => ".png",
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.Pdf => ".pdf",
        _ => throw new System.ArgumentOutOfRangeException(nameof(format)),
    };
}
