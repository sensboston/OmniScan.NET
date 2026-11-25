using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OmniScan.Net.Core;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.App;

public sealed record Option<T>(T Value, string Label);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private const int PreviewDpi = 75;

    // Built per instance: the window (and this model) is rebuilt when the UI language changes.
    private Option<ScanSource>[] AllSources = [];

    private static Option<ScanSource>[] SourceOptions() =>
    [
        new(ScanSource.Flatbed, Strings.Flatbed),
        new(ScanSource.Feeder, Strings.Feeder),
    ];

    private Option<ColorMode>[] AllColorModes = [];

    private static Option<ColorMode>[] ColorModeOptions() =>
    [
        new(ColorMode.Color, Strings.ColorFull),
        new(ColorMode.Grayscale, Strings.Grayscale),
        new(ColorMode.BlackWhite, Strings.BlackWhite),
    ];

    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<IScannerBackend> _backends;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _busyTimer;
    private readonly Stopwatch _busyClock = new();
    private readonly PageTexts _texts = new();
    private string _busyText = "";
    private ScannerDevice? _device;
    private string _deviceStatus = "";
    private string _status = Strings.Ready;
    private bool _isBusy;
    private bool _isSearching;
    private BitmapSource? _previewImage;
    private IReadOnlyList<Option<ScanSource>> _sources;
    private IReadOnlyList<int> _dpiValues = AppSettings.StandardDpi;
    private IReadOnlyList<Option<ColorMode>> _colorModes;
    private string? _lastSavedFile;
    private ScannedFrame? _previewFrame;
    private bool _framesEdited;

    // The last scan that found nothing on the glass, kept for "save anyway" until the next scan.
    private ScannedFrame? _blankFrame;
    private SaveOptions? _blankOptions;
    private int _blankRotation;

    // "Looks like a document": offered once per run when a sheet of text is scanned into an image file.
    private bool _documentHintUsed;
    private bool _hasDocumentHint;
    private ScannedFrame? _hintFrame;   // set when the image is already saved: it is redone as a PDF page
    private int _hintRotation;

    // The last PDF saved in this run, kept so that pages can still be added to it.
    private (string Path, PdfPage[] Pages, DateTime Started)? _lastPdf;
    private int _previewRotation;
    private int _previewVersion;

    public MainViewModel(SettingsStore store, AppSettings settings, IReadOnlyList<IScannerBackend> backends)
    {
        _store = store;
        _settings = settings;
        AllSources = SourceOptions();
        AllColorModes = ColorModeOptions();
        _sources = AllSources;
        _colorModes = AllColorModes;
        _backends = backends;
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => SaveNow();
        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _busyTimer.Tick += (_, _) => UpdateBusyStatus();

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        FramesEditedCommand = new RelayCommand(() => _framesEdited = true);
        SavePdfCommand = new RelayCommand(() => _ = SavePdfAsync(), () => Session.HasPages && !IsBusy);
        SaveAnywayCommand = new RelayCommand(() => _ = SaveAnywayAsync(), () => HasBlank && !IsBusy);
        AppendLastPdfCommand = new RelayCommand(() => _ = AppendLastPdfAsync(), () => CanAppendLastPdf && !IsBusy);
        DiscardPdfCommand = new RelayCommand(DiscardPdf, () => Session.HasPages && !IsBusy);
        DocumentHintCommand = new RelayCommand(() => _ = AcceptDocumentHintAsync(), () => HasDocumentHint && !IsBusy);
        DismissDocumentHintCommand = new RelayCommand(HideDocumentHint);
        Session.ScanRequested += automatic =>
        {
            if (CanScan())
                _ = ScanAsync(preview: false, automatic);
        };
        Session.ShowRequested += image => PreviewImage = image;
        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PdfSession.HasPages))
            {
                OnPropertyChanged(nameof(CanAppendLastPdf));
                AppendLastPdfCommand.RaiseCanExecuteChanged();
                SavePdfCommand.RaiseCanExecuteChanged();
                DiscardPdfCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsFormatSelectable));
            }
        };
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        OpenFolderCommand = new RelayCommand(OpenTodayFolder);
        RefreshCommand = new RelayCommand(() => _ = RefreshDevicesAsync(), () => !_isSearching && !IsBusy);
        ScanCommand = new RelayCommand(() => _ = ScanAsync(preview: false), CanScan);
        PreviewCommand = new RelayCommand(() => _ = ScanAsync(preview: true), CanScan);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppSettings Settings => _settings;

    public ObservableCollection<ScannerDevice> Devices { get; } = [];

    /// <summary>Frames to cut, shown on the preview and editable by the user.</summary>
    public ObservableCollection<EditableFrame> PreviewFrames { get; } = [];

    /// <summary>The user changed the frames: the next scan cuts exactly those.</summary>
    public ICommand FramesEditedCommand { get; }

    /// <summary>A preview is on screen: its frames can be changed.</summary>
    public bool CanEditFrames => _previewFrame is not null && !IsBusy;

    /// <summary>Files saved by the last scan, with buttons to turn each one.</summary>
    public ObservableCollection<ResultItem> Results { get; } = [];

    public IReadOnlyList<Option<ScanSource>> Sources
    {
        get => _sources;
        private set => Set(ref _sources, value);
    }

    public IReadOnlyList<int> DpiValues
    {
        get => _dpiValues;
        private set => Set(ref _dpiValues, value);
    }

    public IReadOnlyList<Option<ColorMode>> ColorModes
    {
        get => _colorModes;
        private set => Set(ref _colorModes, value);
    }

    public IReadOnlyList<Option<OutputFormat>> Formats { get; } =
    [
        new(OutputFormat.Jpeg, "JPEG"),
        new(OutputFormat.Png, "PNG"),
        new(OutputFormat.Pdf, "PDF"),
    ];

    public ICommand BrowseFolderCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand PreviewCommand { get; }

    public ScannerDevice? Device
    {
        get => _device;
        set
        {
            if (!Set(ref _device, value))
                return;
            _previewFrame = null;
            _framesEdited = false;
            PreviewFrames.Clear();
            if (value is not null)
            {
                _settings.DeviceId = value.Id;
                ScheduleSave();
                _ = LoadCapabilitiesAsync(value);
            }

            OnPropertyChanged(nameof(Rotation));
            OnPropertyChanged(nameof(HasDevice));
            RaiseCommands();
        }
    }

    public bool HasDevice => Device is not null;

    /// <summary>Fixed rotation of the selected device (set in the settings dialog).</summary>
    public int Rotation => _settings.GetRotation(Device?.Id);

    /// <summary>Raised when the user asks for the settings dialog; the application owns it.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Re-reads the option texts after a language change (the window itself is rebuilt by the app).</summary>
    public void LanguageChanged()
    {
        AllSources = SourceOptions();
        AllColorModes = ColorModeOptions();
        Sources = AllSources.Where(o => Sources.Any(old => old.Value == o.Value)).ToArray();
        ColorModes = AllColorModes.Where(o => ColorModes.Any(old => old.Value == o.Value)).ToArray();
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(ColorMode));
        OnPropertyChanged(nameof(Session));

        // Messages already shown are in the old language.
        Status = Session.HasPages ? Session.Summary : Strings.Ready;
    }

    /// <summary>The settings dialog changed something the window shows or uses.</summary>
    public void SettingsApplied()
    {
        OnPropertyChanged(nameof(Rotation));
        _ = RedrawPreviewAsync();
    }

    public string DeviceStatus
    {
        get => _deviceStatus;
        private set => Set(ref _deviceStatus, value);
    }

    public ScanSource Source
    {
        get => _settings.Source;
        set => SetSetting(_settings.Source, value, v => _settings.Source = v);
    }

    public int Dpi
    {
        get => _settings.Dpi;
        set => SetSetting(_settings.Dpi, value, v => _settings.Dpi = v);
    }

    public ColorMode ColorMode
    {
        get => _settings.ColorMode;
        set => SetSetting(_settings.ColorMode, value, v => _settings.ColorMode = v);
    }

    public OutputFormat Format
    {
        get => _settings.Format;
        set
        {
            if (!SetSetting(_settings.Format, value, v => _settings.Format = v))
                return;
            OnPropertyChanged(nameof(IsPdf));
            OnPropertyChanged(nameof(IsImageFormat));
            OnPropertyChanged(nameof(CanAppendLastPdf));
            AppendLastPdfCommand.RaiseCanExecuteChanged();
            Session.HideCard();
            HideDocumentHint();
            _ = RedrawPreviewAsync();
        }
    }

    /// <summary>The scan looks like a document while an image format is chosen.</summary>
    public bool HasDocumentHint => _hasDocumentHint;

    /// <summary>"Scan to PDF" after a preview, "Redo as PDF" when the image is already saved.</summary>
    public string DocumentHintAction => _hintFrame is null ? Strings.ScanToPdf : Strings.RedoAsPdf;

    /// <summary>What happens: the sheet is scanned into a PDF, or the saved file is redone as a PDF without scanning again.</summary>
    public string DocumentHintText => _hintFrame is null
        ? Strings.DocumentHintDetail
        : string.Format(Strings.DocumentHintSavedFormat, Results.Count > 0 ? Results[0].FileName : "");

    public RelayCommand DocumentHintCommand { get; private set; } = null!;
    public RelayCommand DismissDocumentHintCommand { get; private set; } = null!;

    /// <summary>Whether to look at the content of the next scan (image formats, until the hint was shown once).</summary>
    private bool WantContentGuess => IsImageFormat && !_documentHintUsed;

    private void ShowDocumentHint(ScannedFrame? savedFrame, int rotation)
    {
        _documentHintUsed = true;
        _hasDocumentHint = true;
        _hintFrame = savedFrame;
        _hintRotation = rotation;
        OnPropertyChanged(nameof(HasDocumentHint));
        OnPropertyChanged(nameof(DocumentHintAction));
        OnPropertyChanged(nameof(DocumentHintText));
        DocumentHintCommand.RaiseCanExecuteChanged();
    }

    private void HideDocumentHint()
    {
        if (!_hasDocumentHint)
            return;
        _hasDocumentHint = false;
        _hintFrame = null;
        OnPropertyChanged(nameof(HasDocumentHint));
        DocumentHintCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Switches to PDF and goes on as a PDF session: after a preview the sheet is scanned as page 1;
    /// when the scan was already saved as an image, that file is removed and the same scan becomes
    /// page 1. Either way the page card follows, with the countdown to the next page.
    /// </summary>
    private async Task AcceptDocumentHintAsync()
    {
        ScannedFrame? frame = _hintFrame;
        int rotation = _hintRotation;
        string[] saved = Results.Select(r => r.Path).ToArray();
        Format = OutputFormat.Pdf;   // also hides the hint
        if (frame is null)
        {
            if (CanScan())
                await ScanAsync(preview: false);
            return;
        }

        BeginBusy(Strings.ProcessingBusy);
        try
        {
            bool deskew = Deskew, orient = Deskew;
            (PdfPage page, BitmapSource thumb, BitmapSource full) = await Task.Run(() =>
            {
                PdfPage p = PdfPages.Make(frame, rotation, deskew, orient, null, allowBlank: true).Page!;
                return (p, LoadBitmap(p.ThumbnailBmp), LoadBitmap(PdfPages.PreviewBmp(p)));
            });

            foreach (string path in saved)
                File.Delete(path);
            if (saved.Contains(_lastSavedFile))
                _lastSavedFile = null;
            ClearResults();

            int number = Session.NextNumber;
            Session.Put(page, thumb);
            ReadText(page);
            PreviewImage = full;
            Status = Session.Summary;
            Session.ShowPageCard(number, thumb, _settings.AutoContinueSeconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = Strings.SaveFailed + " " + e.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Scanning into a PDF: whole pages, collected into one file.</summary>
    public bool IsPdf => Format == OutputFormat.Pdf;

    /// <summary>Splitting and cropping apply to pictures; a PDF keeps whole pages.</summary>
    public bool IsImageFormat => !IsPdf;

    /// <summary>The format cannot change while PDF pages wait to be saved.</summary>
    public bool IsFormatSelectable => !Session.HasPages && !IsBusy;

    /// <summary>The PDF being scanned.</summary>
    public PdfSession Session { get; } = new();

    public RelayCommand SavePdfCommand { get; private set; } = null!;

    /// <summary>The last scan was blank and was not saved; it can still be saved.</summary>
    public bool HasBlank => _blankFrame is not null;

    public RelayCommand SaveAnywayCommand { get; private set; } = null!;

    /// <summary>The last saved PDF can be reopened to add pages (only while no other PDF is being scanned).</summary>
    public bool CanAppendLastPdf => _lastPdf is not null && !Session.HasPages && IsPdf;

    public string AppendLastPdfText => _lastPdf is { } last ? string.Format(Strings.AppendLastPdfFormat, Path.GetFileName(last.Path)) : "";

    public RelayCommand AppendLastPdfCommand { get; private set; } = null!;
    public RelayCommand DiscardPdfCommand { get; private set; } = null!;

    public bool AutoCrop
    {
        get => _settings.AutoCrop;
        set
        {
            if (SetSetting(_settings.AutoCrop, value, v => _settings.AutoCrop = v))
                _ = RedrawPreviewAsync();
        }
    }

    public bool SplitPhotos
    {
        get => _settings.SplitPhotos;
        set
        {
            if (SetSetting(_settings.SplitPhotos, value, v => _settings.SplitPhotos = v))
                _ = RedrawPreviewAsync();
        }
    }

    public bool Deskew
    {
        get => _settings.Deskew;
        set
        {
            if (SetSetting(_settings.Deskew, value, v => _settings.Deskew = v))
                _ = RedrawPreviewAsync();
        }
    }

    public string RootFolder
    {
        get => _settings.RootFolder;
        set => SetSetting(_settings.RootFolder, value, v => _settings.RootFolder = v);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanEditFrames));
                RaiseCommands();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (Set(ref _previewImage, value))
                OnPropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasPreview => PreviewImage is not null;

    /// <summary>Called once the window is visible: device enumeration never delays the first paint.</summary>
    public async Task RefreshDevicesAsync()
    {
        if (_isSearching)
            return;
        _isSearching = true;
        RaiseCommands();
        DeviceStatus = Strings.SearchingDevices;
        try
        {
            // Backends answer at different speeds (USB at once, the network after a broadcast round):
            // show each one's devices as soon as they arrive.
            string? wanted = _device?.Id ?? _settings.DeviceId;
            Devices.Clear();
            List<string> errors = [];
            List<Task<IReadOnlyList<ScannerDevice>>> pending = _backends.Select(b => b.GetDevicesAsync()).ToList();
            while (pending.Count > 0)
            {
                Task<IReadOnlyList<ScannerDevice>> done = await Task.WhenAny(pending);
                pending.Remove(done);
                try
                {
                    foreach (ScannerDevice d in await done)
                        Devices.Add(d);
                }
                catch (ScannerException e)
                {
                    errors.Add(e.Message);
                }

                if (Device is null || (Device.Id != wanted && Devices.Any(d => d.Id == wanted)))
                    Device = Devices.FirstOrDefault(d => d.Id == wanted) ?? Devices.FirstOrDefault();
            }

            DeviceStatus = Devices.Count == 0
                ? (errors.Count > 0 ? errors[0] : Strings.NoDevices)
                : "";
        }
        finally
        {
            _isSearching = false;
            RaiseCommands();
        }
    }

    /// <summary>Stores the window placement and writes settings immediately (on close).</summary>
    public void SaveNow(WindowPlacement? placement = null)
    {
        _saveTimer.Stop();
        if (placement is not null)
            _settings.Window = placement;
        try
        {
            _store.Save(_settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = e.Message;
        }
    }

    private async Task LoadCapabilitiesAsync(ScannerDevice device)
    {
        try
        {
            ScanCapabilities caps = await BackendOf(device).GetCapabilitiesAsync(device.Id);
            if (!ReferenceEquals(device, _device))
                return;

            // Replacing a list makes its ComboBox drop the selection; restore the stored values after.
            ScanSource source = Source;
            int dpi = Dpi;
            ColorMode colorMode = ColorMode;

            Sources = AllSources.Where(o => caps.Sources.Contains(o.Value)).ToArray();
            DpiValues = caps.Resolutions;
            ColorModes = AllColorModes.Where(o => caps.ColorModes.Contains(o.Value)).ToArray();

            Source = caps.Sources.Contains(source) ? source : caps.Sources.FirstOrDefault();
            Dpi = caps.Resolutions.Contains(dpi) ? dpi : Nearest(caps.Resolutions, dpi);
            ColorMode = caps.ColorModes.Contains(colorMode) ? colorMode : caps.ColorModes.FirstOrDefault();
            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(Dpi));
            OnPropertyChanged(nameof(ColorMode));
        }
        catch (ScannerException e)
        {
            DeviceStatus = ErrorText(e);
        }
    }

    private bool CanScan() => Device is not null && !IsBusy;

    private async Task ScanAsync(bool preview, bool automatic = false)
    {
        if (Device is not { } device)
            return;
        SetBlank(null, null, 0);
        HideDocumentHint();

        if (!preview && IsPdf)
        {
            await ScanPdfPageAsync(device, automatic);
            return;
        }

        ScanRequest request = new(preview ? PreviewDpi : Dpi, ColorMode, Source);
        PipelineOptions processing = EffectiveProcessing();
        // Frames adjusted on the preview are cut from the full scan; the paper must not have moved.
        IReadOnlyList<CutFrame>? frames = !preview && _framesEdited && _previewFrame is not null
            ? PreviewFrames.Select(f => f.ToCutFrame()).ToList()
            : null;
        SaveOptions options = new(RootFolder, DateTime.Now, Format,
            _settings.JpegQuality, Rotation, processing, frames, GuessContent: WantContentGuess);

        BeginBusy(preview ? Strings.Previewing : Strings.Scanning);
        try
        {
            ScannedFrame frame = await BackendOf(device).ScanAsync(device.Id, request);
            BeginBusy(Strings.ProcessingBusy);

            // A frame from another computer comes with the rotation configured there for its scanner.
            if (frame.DeviceRotation is int remoteRotation)
                options = options with { Rotation = remoteRotation };

            if (preview)
            {
                Session.HideCard();

                // Kept, so that changing a processing option redraws the preview without scanning again.
                _previewFrame = frame;
                _previewRotation = options.Rotation;
                ClearResults();
                await ShowPreviewAsync(frame, options.Rotation, options.Processing, ++_previewVersion);
                Status = Strings.PreviewReady;
            }
            else
            {
                _previewFrame = null;
                _framesEdited = false;
                PreviewFrames.Clear();
                OnPropertyChanged(nameof(CanEditFrames));
                SaveResult result = await Task.Run(() => ScanSaver.Save(frame, options));
                PreviewImage = await Task.Run(() => LoadBitmap(result.PreviewBmp));
                if (result.Files.Count == 0)
                {
                    ClearResults();
                    SetBlank(frame, options, options.Rotation);
                    Status = Strings.BlankNotSaved;
                    return;
                }

                await ShowResultsAsync(result, options);
                _lastSavedFile = result.Files[^1];
                string files = result.Files.Count <= 3
                    ? string.Join(", ", result.Files.Select(Path.GetFileName))
                    : $"{Path.GetFileName(result.Files[0])} … {Path.GetFileName(result.Files[^1])}";
                string found = result.Kind == PageKind.Photos ? string.Format(Strings.PhotosFoundFormat, result.Files.Count) + " " : "";
                Status = found + string.Format(Strings.SavedFormat, files, _busyClock.Elapsed.TotalSeconds);
                if (result.Content == ContentKind.Document && result.Files.Count == 1 && WantContentGuess)
                    ShowDocumentHint(frame, options.Rotation);
            }
        }
        catch (ScannerException e)
        {
            Status = ErrorText(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = Strings.SaveFailed + " " + e.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    private static BitmapSource LoadBitmap(byte[] bmp) => ResultItem.LoadBitmap(bmp);

    /// <summary>The processing switches as they apply to the chosen format: a PDF is never split or cropped.</summary>
    private PipelineOptions EffectiveProcessing() =>
        IsPdf ? new PipelineOptions(false, false, Deskew, Deskew) : new PipelineOptions(AutoCrop, SplitPhotos, Deskew, Deskew);

    /// <summary>
    /// Scans one PDF page (or the replacement of a page). An automatic scan that finds the same sheet as
    /// the previous page, or an empty glass, is thrown away and stops the automatic sequence.
    /// </summary>
    private async Task ScanPdfPageAsync(ScannerDevice device, bool automatic)
    {
        Session.HideCard();
        BeginBusy(Strings.Scanning);
        Session.IsBusy = true;
        try
        {
            ScannedFrame frame = await BackendOf(device).ScanAsync(device.Id, new ScanRequest(Dpi, ColorMode, Source));
            BeginBusy(Strings.ProcessingBusy);
            int rotation = frame.DeviceRotation ?? Rotation;
            PdfPage? previous = automatic && !Session.HasReplaceTarget ? Session.LastPage : null;
            bool deskew = Deskew, orient = Deskew;
            (PdfPage? page, PageCheck check, BitmapSource? thumb, BitmapSource? full) = await Task.Run(() =>
            {
                (PdfPage? p, PageCheck c) = PdfPages.Make(frame, rotation, deskew, orient, previous);
                return p is null
                    ? (p, c, (BitmapSource?)null, (BitmapSource?)null)
                    : (p, c, LoadBitmap(p.ThumbnailBmp), LoadBitmap(PdfPages.PreviewBmp(p)));
            });

            _previewFrame = null;
            PreviewFrames.Clear();
            OnPropertyChanged(nameof(CanEditFrames));
            if (page is null)
            {
                if (check == PageCheck.Empty)
                {
                    SetBlank(frame, null, rotation);
                    Status = Strings.BlankNotSaved;
                }
                else
                {
                    Status = Strings.PageUnchanged;
                    Session.ShowUnchangedCard();
                }

                return;
            }

            int number = Session.NextNumber;
            bool replaced = Session.Put(page, thumb!);
            ReadText(page);
            PreviewImage = full;
            ClearResults();
            Status = Session.Summary;
            if (!replaced)
                Session.ShowPageCard(number, thumb!, _settings.AutoContinueSeconds);
        }
        catch (ScannerException e)
        {
            Status = ErrorText(e);
        }
        finally
        {
            Session.IsBusy = false;
            EndBusy();
        }
    }

    /// <summary>Writes the collected pages as one PDF into the day folder of the first page.</summary>
    public async Task<bool> SavePdfAsync()
    {
        if (!Session.HasPages)
            return true;
        Session.HideCard();
        BeginBusy(Strings.ProcessingBusy);
        Session.IsBusy = true;
        try
        {
            PdfPage[] pages = Session.Pages.Select(p => p.Page).ToArray();
            IReadOnlyList<TextWord>?[]? text = null;
            if (_settings.PdfText)
            {
                // Usually read already, while the next pages were scanned.
                BeginBusy(Strings.ReadingText);
                PageText?[] read = await Task.WhenAll(pages.Select(p => _texts.Get(p, TextLanguage, Deskew)));
                // A page found upside down that was not turned in the strip yet (saved right after scanning).
                for (int i = 0; i < pages.Length; i++)
                {
                    if (read[i] is { Turn: not 0 } r)
                    {
                        PdfPage source = pages[i];
                        pages[i] = await Task.Run(() => PdfPages.Rotate(source, r.Turn));
                    }
                }

                text = read.Select(r => r?.Words).ToArray();
                BeginBusy(Strings.ProcessingBusy);
            }

            string root = RootFolder;
            DateTime date = Session.Started;
            string? target = Session.TargetPath;
            string path = target ?? "";
            if (target is null)
                path = await Task.Run(() => PdfPages.Save(pages, root, date, _settings.JpegQuality, text));
            else
                await Task.Run(() => PdfPages.SaveTo(pages, target, _settings.JpegQuality, text));
            _lastSavedFile = path;
            _lastPdf = (path, pages, date);
            Session.Clear();
            OnPropertyChanged(nameof(CanAppendLastPdf));
            OnPropertyChanged(nameof(AppendLastPdfText));
            Status = string.Format(Strings.PdfSavedFormat, Path.GetFileName(path), pages.Length);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = Strings.SaveFailed + " " + e.Message;
            return false;
        }
        finally
        {
            Session.IsBusy = false;
            EndBusy();
        }
    }

    private void SetBlank(ScannedFrame? frame, SaveOptions? options, int rotation)
    {
        _blankFrame = frame;
        _blankOptions = options;
        _blankRotation = rotation;
        OnPropertyChanged(nameof(HasBlank));
        SaveAnywayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Saves the last blank scan after all: as an image file, or as the next PDF page.</summary>
    private async Task SaveAnywayAsync()
    {
        if (_blankFrame is not { } frame)
            return;
        SaveOptions? options = _blankOptions;
        int rotation = _blankRotation;
        SetBlank(null, null, 0);
        BeginBusy(Strings.ProcessingBusy);
        try
        {
            if (options is null)
            {
                bool deskew = Deskew, orient = Deskew;
                (PdfPage page, BitmapSource thumb, BitmapSource full) = await Task.Run(() =>
                {
                    PdfPage p = PdfPages.Make(frame, rotation, deskew, orient, null, allowBlank: true).Page!;
                    return (p, LoadBitmap(p.ThumbnailBmp), LoadBitmap(PdfPages.PreviewBmp(p)));
                });
                Session.Put(page, thumb);
                ReadText(page);
                PreviewImage = full;
                Status = Session.Summary;
            }
            else
            {
                SaveResult result = await Task.Run(() => ScanSaver.Save(frame, options with { SaveBlank = true }));
                PreviewImage = await Task.Run(() => LoadBitmap(result.PreviewBmp));
                await ShowResultsAsync(result, options);
                _lastSavedFile = result.Files[^1];
                Status = string.Format(Strings.SavedFormat, string.Join(", ", result.Files.Select(Path.GetFileName)), _busyClock.Elapsed.TotalSeconds);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = Strings.SaveFailed + " " + e.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>Puts the pages of the last saved PDF back into the strip; saving then overwrites that file.</summary>
    private async Task AppendLastPdfAsync()
    {
        if (_lastPdf is not { } last)
            return;
        List<(PdfPage, BitmapSource)> pages = await Task.Run(() =>
            last.Pages.Select(p => (p, LoadBitmap(p.ThumbnailBmp))).ToList());
        Session.Restore(pages, last.Path, last.Started);
        if (pages.Count > 0)
            PreviewImage = pages[^1].Item2;
        Status = Session.Summary;
        OnPropertyChanged(nameof(CanAppendLastPdf));
    }

    private void DiscardPdf()
    {
        Session.Clear();
        PreviewImage = null;
        Status = Strings.Ready;
    }

    /// <summary>Re-runs the processing of the cached preview frame with the current options.</summary>
    private async Task RedrawPreviewAsync()
    {
        if (_previewFrame is not { } frame || IsBusy)
            return;
        await ShowPreviewAsync(frame, _previewRotation, EffectiveProcessing(), ++_previewVersion);
    }

    /// <summary>Shows the bed and the planned frames; a newer request wins over an older one.</summary>
    private async Task ShowPreviewAsync(ScannedFrame frame, int rotation, PipelineOptions processing, int version)
    {
        bool guess = WantContentGuess;
        (BitmapSource image, PreviewResult result) = await Task.Run(() =>
        {
            PreviewResult r = ScanSaver.Preview(frame, rotation, processing, guess);
            return (LoadBitmap(r.PageBmp), r);
        });
        if (version != _previewVersion || _previewFrame != frame)
            return;

        PreviewImage = image;
        OnPropertyChanged(nameof(CanEditFrames));
        PreviewFrames.Clear();
        foreach (CutFrame f in result.Frames)
            PreviewFrames.Add(new EditableFrame(f.Rect, f.Kind));
        _framesEdited = false;
        if (result.Content == ContentKind.Document && WantContentGuess)
            ShowDocumentHint(null, rotation);
    }

    private async Task ShowResultsAsync(SaveResult result, SaveOptions options)
    {
        ClearResults();
        for (int i = 0; i < result.Files.Count; i++)
        {
            Leptonica.Pix image = result.Images[i];
            BitmapSource thumb = await Task.Run(() => LoadBitmap(ScanSaver.Thumbnail(image, ResultItem.ThumbnailSide)));
            ResultItem item = new(result.Files[i], image, thumb, options.Format, options.JpegQuality);
            item.Rotated += (it, full) =>
            {
                if (Results.Count == 1)
                    PreviewImage = full;
                Status = it.FileName + " ✓";
            };
            item.Failed += (_, message) => Status = Strings.SaveFailed + " " + message;
            item.Deleted += it =>
            {
                Results.Remove(it);
                OnPropertyChanged(nameof(HasResults));
                Status = it.FileName + " ✕";
            };
            Results.Add(item);
        }

        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>Starts reading the text of a PDF page in the background (when that is on).</summary>
    private void ReadText(PdfPage page)
    {
        if (_settings.PdfText)
            _ = TurnWhenReadAsync(page);
    }

    private string TextLanguage => Localization.Resolve(_settings.Language);

    /// <summary>
    /// When reading shows that a page is still upside down or sideways (its letter shapes did not tell
    /// when it was straightened), the page turns itself in the strip, keeping the words read.
    /// </summary>
    private async Task TurnWhenReadAsync(PdfPage page)
    {
        if (await _texts.Get(page, TextLanguage, Deskew) is not { Turn: not 0 } read)
            return;
        if (await Session.TurnQuietlyAsync(page, read.Turn) is { } turned)
            _texts.Known(turned, read.Words);
    }

    private void ClearResults()
    {
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
    }

    public bool HasResults => Results.Count > 0;

    private void BeginBusy(string text)
    {
        if (!IsBusy)
            _busyClock.Restart();
        _busyText = text;
        IsBusy = true;
        UpdateBusyStatus();
        _busyTimer.Start();
    }

    private void EndBusy()
    {
        _busyTimer.Stop();
        _busyClock.Stop();
        IsBusy = false;
    }

    private void UpdateBusyStatus()
    {
        TimeSpan t = _busyClock.Elapsed;
        Status = $"{_busyText} {(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private static string ErrorText(ScannerException e) => e.Error switch
    {
        ScannerError.Busy => Strings.ErrorBusy,
        ScannerError.Offline => Strings.ErrorOffline,
        ScannerError.NotFound => Strings.ErrorNotFound,
        ScannerError.CoverOpen => Strings.ErrorCoverOpen,
        ScannerError.PaperEmpty => Strings.ErrorPaperEmpty,
        ScannerError.PaperJam => Strings.ErrorPaperJam,
        ScannerError.WarmingUp => Strings.ErrorWarmingUp,
        _ => Strings.ErrorOther + " " + e.Message,
    };

    private IScannerBackend BackendOf(ScannerDevice device) =>
        _backends.First(b => b.Name == device.Backend);

    private static int Nearest(IReadOnlyList<int> values, int target) =>
        values.Count == 0 ? target : values.MinBy(v => Math.Abs(v - target));

    private void RaiseCommands()
    {
        ScanCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        SettingsCommand.RaiseCanExecuteChanged();
        SavePdfCommand?.RaiseCanExecuteChanged();
        SaveAnywayCommand?.RaiseCanExecuteChanged();
        DocumentHintCommand?.RaiseCanExecuteChanged();
        AppendLastPdfCommand?.RaiseCanExecuteChanged();
        DiscardPdfCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsFormatSelectable));
    }

    private void BrowseFolder()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = Strings.ChooseFolder,
            InitialDirectory = Directory.Exists(RootFolder) ? RootFolder : null,
        };
        if (dialog.ShowDialog() == true)
            RootFolder = dialog.FolderName;
    }

    private void OpenTodayFolder()
    {
        try
        {
            string day = ScanOutput.DayFolder(RootFolder, DateTime.Today);
            string target = Directory.Exists(day) ? day : RootFolder;
            Directory.CreateDirectory(target);

            // Select the last saved file when it is in that folder.
            string args = _lastSavedFile is not null && File.Exists(_lastSavedFile)
                && string.Equals(Path.GetDirectoryName(_lastSavedFile), target, StringComparison.OrdinalIgnoreCase)
                ? $"/select,\"{_lastSavedFile}\""
                : $"\"{target}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Status = e.Message;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool SetSetting<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return false;
        assign(value);
        OnPropertyChanged(name);
        ScheduleSave();
        return true;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
