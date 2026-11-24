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
