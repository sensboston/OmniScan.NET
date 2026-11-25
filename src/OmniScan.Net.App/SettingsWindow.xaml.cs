using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using OmniScan.Net.Core;

namespace OmniScan.Net.App;

/// <summary>Rarely changed settings; edits apply only on OK.</summary>
public sealed class SettingsModel
{
    /// <param name="device">The scanner selected in the main window; the 180 degree option applies to it.</param>
    /// <param name="rotation">Its current rotation (for a shared scanner, as reported by its computer).</param>
    public SettingsModel(AppSettings settings, ScannerDevice? device, int rotation)
    {
        Device = device;
        Flip = rotation == 180;
        _originalFlip = Flip;
        Language = settings.Language ?? "";
        ServerEnabled = settings.ServerEnabled;
        AutoContinueSeconds = settings.AutoContinueSeconds;
        Theme = settings.Theme;
        JpegQuality = settings.JpegQuality;
        Assist = new AssistSettingsModel(settings);
        int[] seconds = [0, 5, 10, 15, 20, 30, 45, 60];
        if (!seconds.Contains(AutoContinueSeconds))
            seconds = [.. seconds.Append(AutoContinueSeconds).Order()];
        AutoContinueChoices = seconds
            .Select(n => new Option<int>(n, n == 0 ? Strings.AutoContinueOff : string.Format(Strings.SecondsFormat, n)))
            .ToList();
        // English and Russian first for convenience, then the rest alphabetically.
        string[] pinned = ["en", "ru"];
        Languages =
        [
            new Option<string>("", Strings.LanguageSystem),
            .. pinned.Select(code => Localization.Available.FirstOrDefault(l => l.Code == code))
                .OfType<LanguageInfo>()
                .Select(l => new Option<string>(l.Code, l.NativeName)),
            .. Localization.Available.Where(l => !pinned.Contains(l.Code)).Select(l => new Option<string>(l.Code, l.NativeName)),
        ];
    }

    public IReadOnlyList<Option<string>> Languages { get; }

    public AssistSettingsModel Assist { get; }

    public IReadOnlyList<Option<UiTheme>> Themes { get; } =
    [
        new(UiTheme.Light, Strings.ThemeLight),
        new(UiTheme.Dark, Strings.ThemeDark),
    ];

    public UiTheme Theme { get; set; }

    public int JpegQuality { get; set; }

    /// <summary>Recognize the text of PDF pages (on this computer).</summary>
    public string Language { get; set; }
    public bool ServerEnabled { get; set; }

    private readonly bool _originalFlip;

    /// <summary>The scanner the 180 degree option is about.</summary>
    public ScannerDevice? Device { get; }

    public bool HasDevice => Device is not null;

    /// <summary>
    /// The top of the page is on the right of this scanner (not on the left, as on most): its scans are
    /// turned by 180 degrees.
    /// </summary>
    public bool Flip { get; set; }

    /// <summary>The other radio button of the pair; only its "checked" matters.</summary>
    public bool TopOnLeft
    {
        get => !Flip;
        set
        {
            if (value)
                Flip = false;
        }
    }

    /// <summary>Seconds before the next PDF page is scanned by itself; 0 = off.</summary>
    public int AutoContinueSeconds { get; set; }

    public IReadOnlyList<Option<int>> AutoContinueChoices { get; }


    /// <summary>
    /// Copies the edits into the settings; returns what changed. A shared scanner's rotation is not
    /// stored here: <paramref name="setRemote"/> sends it to the computer it is attached to.
    /// </summary>
    public (bool LanguageChanged, bool ServerChanged) ApplyTo(AppSettings settings, string remoteBackend, System.Action<string, int> setRemote)
    {
        string? language = string.IsNullOrEmpty(Language) ? null : Language;
        bool languageChanged = language != settings.Language;
        bool serverChanged = ServerEnabled != settings.ServerEnabled;
        settings.Language = language;
        settings.ServerEnabled = ServerEnabled;
        settings.AutoContinueSeconds = AutoContinueSeconds;
        settings.Theme = Theme;
        settings.JpegQuality = JpegQuality;
        Assist.ApplyTo(settings);
        if (Device is { } d)
        {
            int rotation = Flip ? 180 : 0;
            if (d.Backend != remoteBackend)
                settings.SetRotation(d.Id, rotation);
            else if (Flip != _originalFlip)
                setRemote(d.Id, rotation);
        }

        return (languageChanged, serverChanged);
    }
}

public partial class SettingsWindow : Window
{
    private readonly SettingsModel _model;

    public SettingsWindow(SettingsModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        // A PasswordBox does not bind: the key goes in here and comes out in OnKeyChanged.
        KeyBox.Password = model.Assist.Key;
        Loaded += (_, _) =>
        {
            EvenOutTabs();
            _ = model.Assist.CheckNowAsync();
        };
        Closed += (_, _) => model.Assist.Cancel();
    }

    /// <summary>
    /// Gives every tab the height of the tallest one (it depends on the language), so the window does not
    /// jump when switching tabs. Each tab is shown once before the window first appears, to be measured.
    /// </summary>
    private void EvenOutTabs()
    {
        FrameworkElement[] pages = Tabs.Items.OfType<System.Windows.Controls.TabItem>()
            .Select(t => t.Content).OfType<FrameworkElement>().ToArray();
        int selected = Tabs.SelectedIndex;
        double tallest = 0;
        for (int i = 0; i < pages.Length; i++)
        {
            Tabs.SelectedIndex = i;
            Tabs.UpdateLayout();
            tallest = Math.Max(tallest, pages[i].ActualHeight);
        }

        Tabs.SelectedIndex = selected;
        foreach (FrameworkElement page in pages)
            page.MinHeight = tallest;
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e) => _model.Assist.Key = KeyBox.Password;

    private void OnCheckKey(object sender, RoutedEventArgs e) => _ = _model.Assist.CheckNowAsync();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
