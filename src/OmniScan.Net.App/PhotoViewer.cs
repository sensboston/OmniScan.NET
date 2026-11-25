using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace OmniScan.Net.App;

/// <summary>
/// The built-in viewer of the files of the last scan: one file over the whole window, from its saved
/// file (so a turned photo is seen as it is now), with the previous and next ones a key away.
/// Zooming and panning are done by the window; the program Windows has for the file is a double-click away.
/// </summary>
public sealed class PhotoViewer : INotifyPropertyChanged
{
    private readonly ObservableCollection<ResultItem> _items;
    private ResultItem? _item;
    private BitmapSource? _image;
    private int _version;

    public PhotoViewer(ObservableCollection<ResultItem> items)
    {
        _items = items;
        CloseCommand = new RelayCommand(Close);
        NextCommand = new RelayCommand(() => Step(1), () => _items.Count > 1);
        PreviousCommand = new RelayCommand(() => Step(-1), () => _items.Count > 1);
        OpenCommand = new RelayCommand(() =>
        {
            ResultItem? item = _item;
            Close();
            item?.OpenCommand.Execute(null);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand CloseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }

    /// <summary>Opens the file in the program Windows has for it, and closes the viewer.</summary>
    public RelayCommand OpenCommand { get; }

    public bool IsOpen => _item is not null;

    public BitmapSource? Image
    {
        get => _image;
        private set
        {
            _image = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The file name, and where it is among the files when there are several.</summary>
    public string Title => _item is null ? ""
        : _items.Count > 1 ? $"{_item.FileName}   {_items.IndexOf(_item) + 1} / {_items.Count}"
        : _item.FileName;

    public bool HasSeveral => _items.Count > 1;

    public Task ShowAsync(ResultItem item)
    {
        _item = item;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasSeveral));
        return LoadAsync();
    }

    public void Close()
    {
        if (_item is null)
            return;
        _item = null;
        _version++;
        Image = null;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>The file shown was written again (turned): show it as it is now.</summary>
    public void Changed(ResultItem item)
    {
        if (ReferenceEquals(item, _item))
            _ = LoadAsync();
    }

    /// <summary>A file is gone: the viewer moves on to another one, or closes with the last.</summary>
    public void Removed(ResultItem item, int index)
    {
        if (!ReferenceEquals(item, _item))
        {
            OnPropertyChanged(nameof(Title));
            return;
        }

        if (_items.Count == 0)
            Close();
        else
            _ = ShowAsync(_items[Math.Min(index, _items.Count - 1)]);
    }

    private void Step(int by)
    {
        if (_item is null || _items.Count == 0)
            return;
        int index = (_items.IndexOf(_item) + by + _items.Count) % _items.Count;
        _ = ShowAsync(_items[index]);
    }

    private async Task LoadAsync()
    {
        if (_item is not { } item)
            return;
        int version = ++_version;
        string path = item.Path;
        BitmapSource? image;
        try
        {
            image = await Task.Run(() => Load(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            image = item.Thumbnail;
        }

        if (version == _version)
            Image = image;
    }

    /// <summary>The whole file, decoded once and not kept open (it may be written again when turned).</summary>
    private static BitmapSource Load(string path)
    {
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
