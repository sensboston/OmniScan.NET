using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OmniScan.Net.App;

/// <summary>
/// UI text in the current language. Each property name is the key in the translation files
/// (Localization/*.json); a key missing from a translation falls back to English.
/// Values are read when a window is built, so a language change takes effect in new windows.
/// </summary>
public static class Strings
{
    private static IReadOnlyDictionary<string, string> _table = Localization.Load(null);

    /// <summary>Switches the language; null or empty follows the Windows display language.</summary>
    public static void SetLanguage(string? code) => _table = Localization.Load(code);

    private static string T([CallerMemberName] string key = "") =>
        _table.TryGetValue(key, out string? value) ? value : key;

    public static string AppTitle => "OmniScan";

    public static string Device => T();
    public static string SearchingDevices => T();
    public static string NoDevices => T();
    public static string Refresh => T();
    public static string Settings => T();

    public static string Source => T();
    public static string Flatbed => T();
    public static string Feeder => T();

    public static string Resolution => T();

    public static string Color => T();
    public static string ColorFull => T();
    public static string Grayscale => T();
    public static string BlackWhite => T();

    public static string Format => T();

    public static string Processing => T();
    public static string SplitPhotos => T();
    public static string AutoCrop => T();
    public static string Deskew => T();

    public static string Folder => T();
    public static string Browse => T();
    public static string OpenFolder => T();
    public static string ChooseFolder => T();

    public static string Preview => T();
    public static string Scan => T();
    public static string PreviewPlaceholder => T();

    public static string Ready => T();
    public static string Scanning => T();
    public static string Previewing => T();
    public static string ProcessingBusy => T();
    public static string PreviewReady => T();
    public static string SavedFormat => T();
    public static string PhotosFoundFormat => T();
    public static string SaveFailed => T();

    public static string ErrorBusy => T();
    public static string ErrorOffline => T();
    public static string ErrorNotFound => T();
    public static string ErrorCoverOpen => T();
    public static string ErrorPaperEmpty => T();
    public static string ErrorPaperJam => T();
    public static string ErrorWarmingUp => T();
    public static string ErrorOther => T();

    public static string SettingsTitle => T();
    public static string Language => T();
    public static string Theme => T();
    public static string ThemeLight => T();
    public static string ThemeDark => T();
    public static string JpegQuality => T();
    public static string JpegQualityHint => T();
    public static string LanguageSystem => T();
    public static string ServerMode => T();
    public static string ServerModeHint => T();
    public static string Ok => T();
    public static string Cancel => T();

    public static string RotateLeft => T();
    public static string RotateRight => T();
    public static string Delete => T();
    public static string ShowInFolder => T();
    public static string OpenFile => T();

    public static string PagesFormat => T();
    public static string SavePdf => T();
    public static string DiscardPages => T();
    public static string NextPageFormat => T();
    public static string ScanNow => T();
    public static string StopAuto => T();
    public static string PageUnchanged => T();
    public static string Rescan => T();
    public static string ReplaceArmedFormat => T();
    public static string MoveLeft => T();
    public static string MoveRight => T();
    public static string SavePdfPromptFormat => T();
    public static string AutoContinue => T();
    public static string AutoContinueOff => T();
    public static string SecondsFormat => T();
    public static string PdfSavedFormat => T();

    public static string TabGeneral => T();
    public static string TabScanning => T();
    public static string TabAssist => T();
    public static string AssistKey => T();
    public static string AssistCheck => T();
    public static string AssistChecking => T();
    public static string AssistConnected => T();
    public static string AssistUnknownKey => T();
    public static string AssistBadKey => T();
    public static string AssistNoModel => T();
    public static string AssistUnavailable => T();
    public static string AssistBadAnswer => T();
    public static string AssistKeyHint => T();
    public static string AssistWarning => T();
    public static string AssistOnline => T();
    public static string AssistGoogleNotice => T();
    public static string AssistOpenRouterNotice => T();
    public static string AssistUse => T();
    public static string AssistOrient => T();
    public static string AssistDescribe => T();
    public static string AssistDescribeHint => T();
    public static string PdfText => T();
    public static string PdfTextHint => T();
    public static string ReadingText => T();
    public static string ViewerHint => T();
    public static string ScannerTop => T();
    public static string TopWord => T();
    public static string TopLeft => T();
    public static string TopRight => T();
    public static string ScannerTopHint => T();
    public static string BlankNotSaved => T();
    public static string SaveAnyway => T();
    public static string LooksLikeDocument => T();
    public static string DocumentHintDetail => T();
    public static string DocumentHintSavedFormat => T();
    public static string KeepAsIs => T();
    public static string ScanToPdf => T();
    public static string RedoAsPdf => T();
    public static string PageAddedFormat => T();
    public static string ScanAgain => T();
    public static string Close => T();
    public static string AppendLastPdfFormat => T();
    public static string InsertHere => T();

    public static string TrayTooltip => T();
    public static string TrayOpen => T();
    public static string TrayExit => T();
}
