using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Leptonica;
using OmniScan.Net.Core;
using OmniScan.Net.Imaging;

namespace OmniScan.Net.App;

/// <summary>One file saved by the last scan, shown as a thumbnail that can be turned.</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    public const int ThumbnailSide = 240;

    private readonly OutputFormat _format;
    private readonly int _jpegQuality;
    private Pix _image;
    private BitmapSource _thumbnail;
    private bool _busy;
    private bool _turnedByHand;
    private string? _description;

    public ResultItem(string path, Pix image, BitmapSource thumbnail, OutputFormat format, int jpegQuality)
    {
        Path = path;
        _image = image;
        _thumbnail = thumbnail;
        _format = format;
        _jpegQuality = jpegQuality;
        RotateLeftCommand = new RelayCommand(() => _ = RotateByHandAsync(270), () => !_busy);
        RotateRightCommand = new RelayCommand(() => _ = RotateByHandAsync(90), () => !_busy);
        DeleteCommand = new RelayCommand(Delete, () => !_busy);
        ShowInFolderCommand = new RelayCommand(() => Shell("explorer.exe", $"/select,\"{Path}\""));
        OpenCommand = new RelayCommand(() => Shell(Path, null));
        ViewCommand = new RelayCommand(() => ViewRequested?.Invoke(this));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after a rotation was written, with the new full preview.</summary>
    public event Action<ResultItem, BitmapSource>? Rotated;

    /// <summary>Raised when the file could not be written again, deleted or opened.</summary>
    public event Action<ResultItem, string>? Failed;

    /// <summary>Raised after the file was deleted.</summary>
    public event Action<ResultItem>? Deleted;

    public string Path { get; }

    /// <summary>The file is gone (deleted here); late answers about it are dropped.</summary>
    public bool IsDeleted { get; private set; }

    /// <summary>A short caption, kept in descript.ion and (for JPEG) inside the file; shown as a tooltip.</summary>
    public string? Description
    {
        get => _description;
        private set
        {
            _description = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        }
    }
    public string FileName => System.IO.Path.GetFileName(Path);
    public RelayCommand RotateLeftCommand { get; }
    public RelayCommand RotateRightCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ShowInFolderCommand { get; }
    public RelayCommand OpenCommand { get; }

    /// <summary>Shows the file in the built-in viewer.</summary>
    public RelayCommand ViewCommand { get; }

    /// <summary>Raised when the file is to be shown in the built-in viewer.</summary>
    public event Action<ResultItem>? ViewRequested;

    public BitmapSource Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public static BitmapSource LoadBitmap(byte[] bmp)
    {
        using MemoryStream stream = new(bmp);
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Applies what the online service found: the turn (unless the photo was already turned by hand)
    /// and the caption. Runs while the user may be looking at the results.
    /// </summary>
    public async Task ApplyAssistAsync(int? rotation, string? description)
    {
        if (IsDeleted)
            return;
        if (!string.IsNullOrWhiteSpace(description))
        {
            Description = description;
            try
            {
                string path = Path;
                await Task.Run(() => Descriptions.Set(path, description));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Failed?.Invoke(this, e.Message);
            }
        }

        if (rotation is int turn and not 0 && !_turnedByHand)
            await RotateAsync(turn);
        else if (Description is not null && _format == OutputFormat.Jpeg)
            await WriteCommentAsync();
    }

    private Task RotateByHandAsync(int degrees)
    {
        _turnedByHand = true;
        return RotateAsync(degrees);
    }

    private async Task WriteCommentAsync()
    {
        if (_busy || IsDeleted)
            return;
        SetBusy(true);
        try
        {
            string path = Path, comment = Description!;
            await Task.Run(() => ScanSaver.Comment(path, comment));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Failed?.Invoke(this, e.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RotateAsync(int degrees)
    {
        if (_busy || IsDeleted)
            return;
        SetBusy(true);
        try
        {
            Pix source = _image;
            (Pix rotated, BitmapSource thumb, BitmapSource full) = await Task.Run(() =>
            {
                Pix r = ScanSaver.Resave(Path, source, degrees, _format, _jpegQuality, Description);
                return (r, LoadBitmap(ScanSaver.Thumbnail(r, ThumbnailSide)), LoadBitmap(ScanSaver.Thumbnail(r, ScanSaver.PreviewMaxSide)));
            });
            _image = rotated;
            Thumbnail = thumb;
            Rotated?.Invoke(this, full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke(this, e.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Delete()
    {
        try
        {
            File.Delete(Path);
            IsDeleted = true;
            if (Description is not null)
                Descriptions.Set(Path, null);
            Deleted?.Invoke(this);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke(this, e.Message);
        }
    }

    private void Shell(string file, string? arguments)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, arguments ?? "") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Failed?.Invoke(this, e.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RotateLeftCommand.RaiseCanExecuteChanged();
        RotateRightCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }
}
