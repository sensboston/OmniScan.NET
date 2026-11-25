using System;
using System.ComponentModel;
using System.Windows;
using OmniScan.Net.Core;

namespace OmniScan.Net.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;

    public MainWindow(MainViewModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        RestorePlacement(model.Settings.Window);
    }

    private bool _noPrompt;

    /// <summary>Stores where the window is now (before it is replaced by a window in another language).</summary>
    public void SavePlacement() => _model.SaveNow(CapturePlacement());

    /// <summary>
    /// Keys of the PDF card: Enter scans now, Ctrl+S saves, Esc stops. Ctrl+S also works without the card.
    /// On the document card Enter accepts PDF and Esc keeps the image.
    /// </summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        if (ctrl && e.Key == System.Windows.Input.Key.S && _model.SavePdfCommand.CanExecute(null))
        {
            _model.SavePdfCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_model.Viewer.IsOpen)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.Escape:
                    _model.Viewer.CloseCommand.Execute(null);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Left or System.Windows.Input.Key.PageUp:
                    _model.Viewer.PreviousCommand.Execute(null);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Right or System.Windows.Input.Key.PageDown or System.Windows.Input.Key.Space:
                    _model.Viewer.NextCommand.Execute(null);
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Enter:
                    _model.Viewer.OpenCommand.Execute(null);
                    e.Handled = true;
                    break;
            }

            return;
        }

        if (_model.HasDocumentHint)
        {
            if (e.Key == System.Windows.Input.Key.Enter && _model.DocumentHintCommand.CanExecute(null))
            {
                _model.DocumentHintCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                _model.DismissDocumentHintCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (!_model.Session.IsCardVisible)
            return;
        if (e.Key == System.Windows.Input.Key.Enter && _model.Session.ScanNowCommand.CanExecute(null))
        {
            _model.Session.ScanNowCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            _model.Session.StopCommand.Execute(null);
            e.Handled = true;
        }
    }

    private const double MaxZoom = 8;
    private Point _dragFrom;
    private (double X, double Y) _shiftFrom;
    private bool _dragged;
    private bool _downOnImage;

    /// <summary>A new picture is shown whole, fitted to the window.</summary>
    private void OnViewerImageChanged(object? sender, System.Windows.Data.DataTransferEventArgs e) => SetZoom(1, 0, 0);

    private void SetZoom(double scale, double x, double y)
    {
        ViewerScale.ScaleX = ViewerScale.ScaleY = scale;
        ViewerShift.X = x;
        ViewerShift.Y = y;
    }

    /// <summary>The wheel zooms around the point under the cursor, which stays where it is.</summary>
    private void OnViewerWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        double scale = ViewerScale.ScaleX;
        double next = Math.Clamp(scale * Math.Pow(1.25, e.Delta / 120.0), 1, MaxZoom);
        Point p = e.GetPosition(ViewerImage);   // in the picture's own, unzoomed, coordinates
        if (next <= 1.0001)
            SetZoom(1, 0, 0);
        else
            SetZoom(next, ViewerShift.X + p.X * (scale - next), ViewerShift.Y + p.Y * (scale - next));
        e.Handled = true;
    }

    private void OnViewerDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _model.Viewer.OpenCommand.Execute(null);
            e.Handled = true;
            return;
        }

        _dragFrom = e.GetPosition(ViewerPane);
        _shiftFrom = (ViewerShift.X, ViewerShift.Y);
        _dragged = false;
        _downOnImage = ReferenceEquals(e.OriginalSource, ViewerImage);
        ViewerPane.CaptureMouse();
        e.Handled = true;
    }

    private void OnViewerMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!ViewerPane.IsMouseCaptured)
            return;
        Vector moved = e.GetPosition(ViewerPane) - _dragFrom;
        if (moved.Length > 4)
            _dragged = true;
        if (_dragged && ViewerScale.ScaleX > 1)
        {
            ViewerShift.X = _shiftFrom.X + moved.X;
            ViewerShift.Y = _shiftFrom.Y + moved.Y;
        }
    }

    /// <summary>A click on the dark around the picture (not a drag) closes the viewer.</summary>
    private void OnViewerUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ViewerPane.IsMouseCaptured)
            return;
        ViewerPane.ReleaseMouseCapture();
        if (!_dragged && !_downOnImage)
            _model.Viewer.CloseCommand.Execute(null);
    }

    /// <summary>Closes without asking about unsaved PDF pages: the same model lives on in a new window.</summary>
    public void CloseWithoutPrompt()
    {
        _noPrompt = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
            return;

        if (!_noPrompt && _model.Session.HasPages)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                string.Format(Strings.SavePdfPromptFormat, _model.Session.Pages.Count), Strings.AppTitle,
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                // Saving takes a moment: keep the window, save, then close for real.
                e.Cancel = true;
                if (await _model.SavePdfAsync())
                    CloseWithoutPrompt();
                return;
            }

            _model.Session.Clear();
        }

        _model.SaveNow(CapturePlacement());
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null)
            return;

        // Ignore a placement that is no longer on any screen (monitor removed, resolution changed).
        Rect screen = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Rect window = new(placement.Left, placement.Top, placement.Width, placement.Height);
        Rect visible = Rect.Intersect(screen, window);
        if (visible.IsEmpty || visible.Width < 100 || visible.Height < 50)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
            WindowState = WindowState.Maximized;
    }

    private WindowPlacement CapturePlacement()
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }
}
