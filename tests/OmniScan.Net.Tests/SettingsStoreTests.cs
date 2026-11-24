using System.IO;
using OmniScan.Net.Core;

namespace OmniScan.Net.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using TempFolder temp = new();
        SettingsStore store = new(Path.Combine(temp.Path, "none", "OmniScan.ini"));

        AppSettings settings = store.Load();

        Assert.Equal(300, settings.Dpi);
        Assert.Equal(AppSettings.DefaultRootFolder, settings.RootFolder);
        Assert.Null(settings.DeviceId);
        Assert.Null(settings.Language);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEverything()
    {
        using TempFolder temp = new();
        SettingsStore store = new(Path.Combine(temp.Path, "sub", "OmniScan.ini"));
        AppSettings original = new()
        {
            DeviceId = "wia:{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\\0001",
            Source = ScanSource.Feeder,
            Dpi = 600,
            ColorMode = ColorMode.Grayscale,
            Format = OutputFormat.Pdf,
            JpegQuality = 75,
            RootFolder = temp.Path,
            AutoCrop = false,
            SplitPhotos = false,
            Deskew = false,
            AutoContinueSeconds = 25,
            ServerEnabled = true,
            Language = "uk",
            Window = new WindowPlacement { Left = -1200, Top = 40.5, Width = 900, Height = 640, Maximized = true },
        };
        original.SetRotation("wia:{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\\0003", 180);

        store.Save(original);
        AppSettings loaded = store.Load();

        Assert.Equal(original.DeviceId, loaded.DeviceId);
        Assert.Equal(original.Source, loaded.Source);
        Assert.Equal(original.Dpi, loaded.Dpi);
        Assert.Equal(original.ColorMode, loaded.ColorMode);
        Assert.Equal(original.Format, loaded.Format);
        Assert.Equal(original.JpegQuality, loaded.JpegQuality);
        Assert.Equal(original.RootFolder, loaded.RootFolder);
        Assert.False(loaded.AutoCrop);
        Assert.False(loaded.SplitPhotos);
        Assert.False(loaded.Deskew);
        Assert.Equal(25, loaded.AutoContinueSeconds);
        Assert.True(loaded.ServerEnabled);
        Assert.Equal("uk", loaded.Language);
        Assert.NotNull(loaded.Window);
        Assert.Equal(-1200, loaded.Window.Left);
        Assert.Equal(40.5, loaded.Window.Top);
        Assert.True(loaded.Window.Maximized);
        Assert.Equal(180, loaded.GetRotation("wia:{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\\0003"));
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void File_IsPlainIni()
    {
        using TempFolder temp = new();
        SettingsStore store = new(Path.Combine(temp.Path, "OmniScan.ini"));

        store.Save(new AppSettings { ColorMode = ColorMode.BlackWhite, Dpi = 200 });
        string text = File.ReadAllText(store.Path);

        Assert.Contains("[Scan]", text);
        Assert.Contains("Color = BlackWhite", text);
        Assert.Contains("Dpi = 200", text);
    }

    [Fact]
    public void Load_HandEditedFile_ToleratesCaseCommentsAndJunk()
    {
        using TempFolder temp = new();
        string path = Path.Combine(temp.Path, "OmniScan.ini");
        File.WriteAllText(path, """
            ; comment
            # another comment
            junk line without equals
            [scan]
            dpi=600
            COLOR = grayscale
            Format = Sepia
            [Processing]
            Straighten = no
            [Unknown]
            Whatever = 1
            """);

        AppSettings settings = new SettingsStore(path).Load();

        Assert.Equal(600, settings.Dpi);
        Assert.Equal(ColorMode.Grayscale, settings.ColorMode);
        Assert.Equal(OutputFormat.Jpeg, settings.Format);   // unknown value: default kept
        Assert.False(settings.Deskew);
    }

    [Fact]
    public void Load_OutOfRangeValues_AreNormalized()
    {
        using TempFolder temp = new();
        string path = Path.Combine(temp.Path, "OmniScan.ini");
        File.WriteAllText(path, """
            [Scan]
            Dpi = 99999
            JpegQuality = 0
            Folder = relative\path
            [Window]
            Left = 0
            Top = 0
            Width = 10
            Height = 10
            [Rotation]
            usb = 180
            net = 45
            """);

        AppSettings settings = new SettingsStore(path).Load();

        Assert.Equal(300, settings.Dpi);
        Assert.Equal(75, settings.JpegQuality);
        Assert.Equal(AppSettings.DefaultRootFolder, settings.RootFolder);
        Assert.Null(settings.Window);
        Assert.Equal(180, settings.GetRotation("usb"));
        Assert.Equal(0, settings.GetRotation("net"));
    }

    [Theory]
    [InlineData("System", UiTheme.Light)]
    [InlineData("Dark", UiTheme.Dark)]
    [InlineData("nonsense", UiTheme.Light)]
    public void Load_Theme_UnknownFallsBackToLight(string value, UiTheme expected)
    {
        using TempFolder temp = new();
        string path = Path.Combine(temp.Path, "OmniScan.ini");
        File.WriteAllText(path, $"[App]\nTheme = {value}\n");

        Assert.Equal(expected, new SettingsStore(path).Load().Theme);
    }

    [Theory]
    [InlineData(49, 75)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(101, 75)]
    public void Load_JpegQuality_KeptWithinRange(int value, int expected)
    {
        using TempFolder temp = new();
        string path = Path.Combine(temp.Path, "OmniScan.ini");
        File.WriteAllText(path, $"[Scan]\nJpegQuality = {value}\n");

        Assert.Equal(expected, new SettingsStore(path).Load().JpegQuality);
    }


}
