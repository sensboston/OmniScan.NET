using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.App;

/// <summary>A page of the PDF being scanned, as a thumbnail with its actions.</summary>
public sealed class PdfPageItem : INotifyPropertyChanged
{
    private readonly PdfSession _session;
    private PdfPage _page;
    private BitmapSource _thumbnail;
    private int _number;
    private bool _isReplaceTarget;
    private bool _isInsertBefore;

    public PdfPageItem(PdfSession session, PdfPage page, BitmapSource thumbnail)
    {
        _session = session;
        _page = page;
        _thumbnail = thumbnail;
        RotateLeftCommand = new RelayCommand(() => _ = _session.RotateAsync(this, 270), () => !_session.IsBusy);
        RotateRightCommand = new RelayCommand(() => _ = _session.RotateAsync(this, 90), () => !_session.IsBusy);
        DeleteCommand = new RelayCommand(() => _session.Delete(this), () => !_session.IsBusy);
        MoveLeftCommand = new RelayCommand(() => _session.Move(this, -1), () => !_session.IsBusy && Number > 1);
        MoveRightCommand = new RelayCommand(() => _session.Move(this, +1), () => !_session.IsBusy && Number < _session.Pages.Count);
        RescanCommand = new RelayCommand(() => _session.ArmReplace(this), () => !_session.IsBusy);
        ShowCommand = new RelayCommand(() => _ = _session.ShowAsync(this));
        InsertBeforeCommand = new RelayCommand(() => _session.ToggleInsert(Number - 1));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PdfPage Page
    {
        get => _page;
        set => Set(ref _page, value);
    }

    public BitmapSource Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    public int Number
    {
        get => _number;
        set => Set(ref _number, value);
    }

    /// <summary>The next scan replaces this page.</summary>
    public bool IsReplaceTarget
    {
        get => _isReplaceTarget;
        set => Set(ref _isReplaceTarget, value);
    }

    public RelayCommand RotateLeftCommand { get; }
    public RelayCommand RotateRightCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand MoveLeftCommand { get; }
    public RelayCommand MoveRightCommand { get; }
    public RelayCommand RescanCommand { get; }
    public RelayCommand ShowCommand { get; }

    /// <summary>The next scan is inserted before this page (the marker in the gap to its left).</summary>
    public bool IsInsertBefore
    {
        get => _isInsertBefore;
        set => Set(ref _isInsertBefore, value);
    }

    public RelayCommand InsertBeforeCommand { get; }

    internal void RaiseCommands()
    {
        foreach (RelayCommand c in new[] { RotateLeftCommand, RotateRightCommand, DeleteCommand, MoveLeftCommand, MoveRightCommand, RescanCommand })
            c.RaiseCanExecuteChanged();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// A multi-page PDF being scanned: the pages so far, which one the next scan replaces, and the
/// countdown that starts the next scan by itself.
/// </summary>
public sealed class PdfSession : INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private TimeSpan _countdown;
    private int _secondsLeft;
    private bool _isBusy;
    private bool _isCardVisible;
    private bool _isCardCountdown;
    private string _cardTitle = "";
    private BitmapSource? _cardThumbnail;
    private double _countdownFraction;
    private int? _insertAt;

    public PdfSession()
    {
        _timer.Tick += (_, _) => Tick();
        ScanNowCommand = new RelayCommand(() =>
        {
            HideCard();
            ScanRequested?.Invoke(true);
        });
        StopCommand = new RelayCommand(HideCard);
        InsertAtEndCommand = new RelayCommand(() => ToggleInsert(null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The countdown ran out (or "Scan now" was pressed): scan the next page; true = automatic.</summary>
    public event Action<bool>? ScanRequested;

    /// <summary>A page should be shown large in the preview.</summary>
    public event Action<BitmapSource>? ShowRequested;

    public ObservableCollection<PdfPageItem> Pages { get; } = [];

    /// <summary>When the first page was scanned: the PDF goes into that day's folder.</summary>
    public DateTime Started { get; private set; }

    /// <summary>Set when the pages of an already saved PDF were brought back to add to it: saving overwrites that file.</summary>
    public string? TargetPath { get; private set; }

    /// <summary>Brings back the pages of a PDF saved earlier in this run, to add or replace pages.</summary>
    public void Restore(IEnumerable<(PdfPage Page, BitmapSource Thumbnail)> pages, string path, DateTime started)
    {
        Clear();
        foreach ((PdfPage page, BitmapSource thumbnail) in pages)
            Pages.Add(new PdfPageItem(this, page, thumbnail));
        TargetPath = path;
        Started = started;
        Changed();
    }

    public bool HasPages => Pages.Count > 0;
    public string Summary => string.Format(Strings.PagesFormat, Pages.Count);

    public PdfPageItem? ReplaceTarget => Pages.FirstOrDefault(p => p.IsReplaceTarget);
    public bool HasReplaceTarget => ReplaceTarget is not null;
    public string ReplaceText => ReplaceTarget is { } t ? string.Format(Strings.ReplaceArmedFormat, t.Number) : "";

    public bool IsCountingDown => _timer.IsEnabled;
    public string CountdownText => string.Format(Strings.NextPageFormat, _secondsLeft);

    /// <summary>The card in the middle of the window after a page: what was scanned and what comes next.</summary>
    public bool IsCardVisible
    {
        get => _isCardVisible;
        private set => Set(ref _isCardVisible, value);
    }

    /// <summary>The card counts down to the next page (false: "the page was not changed").</summary>
    public bool IsCardCountdown
    {
        get => _isCardCountdown;
        private set => Set(ref _isCardCountdown, value);
    }

    public string CardTitle
    {
        get => _cardTitle;
        private set => Set(ref _cardTitle, value);
    }

    public BitmapSource? CardThumbnail
    {
        get => _cardThumbnail;
        private set => Set(ref _cardThumbnail, value);
    }

    /// <summary>1 when the countdown starts, 0 when it runs out: drives the progress bar.</summary>
    public double CountdownFraction
    {
        get => _countdownFraction;
        private set => Set(ref _countdownFraction, value);
    }

    /// <summary>Shows the card for a page just added; counts down when <paramref name="seconds"/> &gt; 0.</summary>
    public void ShowPageCard(int number, BitmapSource thumbnail, int seconds)
    {
        CardTitle = string.Format(Strings.PageAddedFormat, number);
        CardThumbnail = thumbnail;
        IsCardCountdown = seconds > 0;
        IsCardVisible = true;
        StartCountdown(seconds);
    }

    /// <summary>The automatic scan found the previous sheet again: say so, no countdown.</summary>
    public void ShowUnchangedCard()
    {
        StopCountdown();
        CardTitle = Strings.PageUnchanged;
        CardThumbnail = LastThumbnail;
        IsCardCountdown = false;
        IsCardVisible = true;
    }

    public void HideCard()
    {
        StopCountdown();
        IsCardVisible = false;
    }

    private BitmapSource? LastThumbnail => Pages.Count > 0 ? Pages[^1].Thumbnail : null;

    public RelayCommand ScanNowCommand { get; }
    public RelayCommand InsertAtEndCommand { get; }

    /// <summary>The marker is at the end: the next scan is appended (the usual case).</summary>
    public bool IsInsertAtEnd => _insertAt is null && ReplaceTarget is null;
    public RelayCommand StopCommand { get; }

    /// <summary>A scan or a page operation is running: page actions wait.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            foreach (PdfPageItem p in Pages)
                p.RaiseCommands();
        }
    }

    /// <summary>Adds a page, or replaces the page marked for rescanning. Returns true when it replaced one.</summary>
    public bool Put(PdfPage page, BitmapSource thumbnail)
    {
        if (ReplaceTarget is { } target)
        {
            target.Page = page;
            target.Thumbnail = thumbnail;
            target.IsReplaceTarget = false;
            Changed();
            return true;
        }

        if (Pages.Count == 0)
            Started = DateTime.Now;
        PdfPageItem added = new(this, page, thumbnail);
        if (_insertAt is int at && at < Pages.Count)
            Pages.Insert(at, added);
        else
            Pages.Add(added);

        // After an insertion the marker goes back to the end.
        _insertAt = null;
        Changed();
        return false;
    }

    /// <summary>The page the next scan follows (for the "same sheet" check).</summary>
    public PdfPage? LastPage => _insertAt is int at && at > 0 && at <= Pages.Count ? Pages[at - 1].Page
        : Pages.Count > 0 ? Pages[^1].Page : null;

    /// <summary>Number the next scanned page gets.</summary>
    public int NextNumber => ReplaceTarget?.Number ?? (_insertAt is int at ? at + 1 : Pages.Count + 1);

    /// <summary>Moves the insertion marker before page <paramref name="index"/> (0-based), or to the end; again: back to the end.</summary>
    internal void ToggleInsert(int? index)
    {
        HideCard();
        foreach (PdfPageItem p in Pages)
            p.IsReplaceTarget = false;
        _insertAt = index is int i && i < Pages.Count && _insertAt != i ? i : null;
        Changed();
    }

    public void Clear()
    {
        HideCard();
        _insertAt = null;
        TargetPath = null;
        Pages.Clear();
        Changed();
    }

    public void StartCountdown(int seconds)
    {
        if (seconds <= 0)
            return;
        _countdown = TimeSpan.FromSeconds(seconds);
        _secondsLeft = seconds;
        CountdownFraction = 1;
        _clock.Restart();
        _timer.Start();
        OnPropertyChanged(nameof(IsCountingDown));
        OnPropertyChanged(nameof(CountdownText));
    }

    public void StopCountdown()
    {
        if (!_timer.IsEnabled)
            return;
        _timer.Stop();
        OnPropertyChanged(nameof(IsCountingDown));
    }

    internal async Task RotateAsync(PdfPageItem item, int degrees)
    {
        IsBusy = true;
        try
        {
            PdfPage source = item.Page;
            (PdfPage turned, BitmapSource thumb, BitmapSource full) = await Task.Run(() =>
            {
                PdfPage t = PdfPages.Rotate(source, degrees);
                return (t, ResultItem.LoadBitmap(t.ThumbnailBmp), ResultItem.LoadBitmap(PdfPages.PreviewBmp(t)));
            });
            item.Page = turned;
            item.Thumbnail = thumb;
            ShowRequested?.Invoke(full);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void Delete(PdfPageItem item)
    {
        _insertAt = null;
        Pages.Remove(item);
        Changed();
    }

    internal void Move(PdfPageItem item, int by)
    {
        int from = Pages.IndexOf(item), to = from + by;
        if (from < 0 || to < 0 || to >= Pages.Count)
            return;
        Pages.Move(from, to);
        Changed();
    }

    internal void ArmReplace(PdfPageItem item)
    {
        HideCard();
        bool on = !item.IsReplaceTarget;
        foreach (PdfPageItem p in Pages)
            p.IsReplaceTarget = false;
        item.IsReplaceTarget = on;
        _insertAt = null;
        Changed();
    }

    internal async Task ShowAsync(PdfPageItem item)
    {
        PdfPage page = item.Page;
        BitmapSource full = await Task.Run(() => ResultItem.LoadBitmap(PdfPages.PreviewBmp(page)));
        ShowRequested?.Invoke(full);
    }

    private void Tick()
    {
        TimeSpan left = _countdown - _clock.Elapsed;
        CountdownFraction = Math.Max(0, left.TotalMilliseconds / _countdown.TotalMilliseconds);
        int seconds = (int)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        if (seconds != _secondsLeft)
        {
            _secondsLeft = seconds;
            OnPropertyChanged(nameof(CountdownText));
        }

        if (left > TimeSpan.Zero)
            return;
        HideCard();
        ScanRequested?.Invoke(true);
    }

    private void Changed()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].Number = i + 1;
            Pages[i].IsInsertBefore = _insertAt == i;
            Pages[i].RaiseCommands();
        }

        OnPropertyChanged(nameof(IsInsertAtEnd));

        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ReplaceTarget));
        OnPropertyChanged(nameof(HasReplaceTarget));
        OnPropertyChanged(nameof(ReplaceText));
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnPropertyChanged(name!);
    }
}
