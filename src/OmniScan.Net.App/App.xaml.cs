using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using System.Threading.Tasks;
using OmniScan.Net.Core;
using OmniScan.Net.Remote;
using OmniScan.Net.Scanning;

namespace OmniScan.Net.App;

/// <summary>
/// One process per user session. Without the server option it lives as long as its window.
/// With it, the process also keeps a tray icon and outlives the window; starting OmniScan.exe
/// again only asks the running process to open its window, so the scanner is never driven by two
/// processes at once.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\OmniScan.Instance";
    private const string ShowWindowEventName = @"Local\OmniScan.ShowWindow";

    private Mutex? _instance;
    private EventWaitHandle? _showWindow;
    private SettingsStore _store = null!;
    private AppSettings _settings = null!;
    private WiaBackend? _wia;
    private RemoteBackend? _remote;
    private ScanServer? _server;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _model;

    /// <summary>
    /// The scanners here count as in use for this long after the window was opened or brought back,
    /// or after the last scan or preview here. Then a scan from another computer may take them.
    /// </summary>
    private static readonly TimeSpan InUseFor = TimeSpan.FromMinutes(3);

    private long _lastLocalUse = DateTime.UtcNow.Ticks;
    private volatile bool _windowShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool serverStart = e.Args.Contains(Autostart.ServerArgument, StringComparer.OrdinalIgnoreCase);
        WaitForUpdatedProcess(e.Args);

        _instance = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            // Already running: let that process show its window (a server autostart just leaves).
            if (!serverStart && EventWaitHandle.TryOpenExisting(ShowWindowEventName, out EventWaitHandle? other))
            {
                other.Set();
                other.Dispose();
            }

            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _store = new SettingsStore(SettingsStore.DefaultPath);
        _settings = _store.Load();
        Strings.SetLanguage(_settings.Language);
        ApplyTheme(_settings.Theme);
        _wia = new WiaBackend();
        _remote = new RemoteBackend();
        ListenForShowRequests();

        if (_settings.ServerEnabled)
        {
            ShowTray();
            _ = StartServerAsync();
        }

        if (serverStart)
        {
            if (!_settings.ServerEnabled)
            {
                // A stale autostart entry: clean it up and leave.
                TryAutostart(false);
                Shutdown();
            }

            return;
        }

        ShowMainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Never block the UI thread on the server: a request still running could be waiting for it.
        // Give the server a moment to stop cleanly; the process ends anyway.
        Task.Run(StopServerAsync).Wait(TimeSpan.FromSeconds(2));
        _remote?.Dispose();
        _tray?.Dispose();
        _wia?.Dispose();
        _showWindow?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Started by an update: the old process still holds the single-instance lock for a moment, so wait
    /// for it to end, then remove its file.
    /// </summary>
    private static void WaitForUpdatedProcess(string[] args)
    {
        int at = Array.FindIndex(args, a => string.Equals(a, Updates.AfterUpdateArgument, StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int pid))
        {
            try
            {
                using System.Diagnostics.Process old = System.Diagnostics.Process.GetProcessById(pid);
                old.WaitForExit(TimeSpan.FromSeconds(20));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        if (Environment.ProcessPath is { } exe)
            Updates.CleanUp(exe);
    }

    /// <summary>
    /// Once a day at most, when the window is open: a newer release on GitHub is offered (Yes/No; "No"
    /// is remembered for that version). "Yes" downloads it with progress in the status line, swaps the
    /// program file and starts the new one. Never in the middle of work: unsaved PDF pages or a running
    /// scan put it off to the next start.
    /// </summary>
    private async Task CheckForUpdateAsync(MainViewModel model)
    {
#if DEBUG
        // A development build is not replaced by a release, unless a test feed is set.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Updates.FeedVariable)))
            return;
#endif
        if (!_settings.CheckUpdates || Environment.ProcessPath is not { } exe
            || (_settings.UpdateChecked is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(1)))
            return;

        using System.Net.Http.HttpClient http = Updates.CreateClient();
        UpdateInfo? update;
        try
        {
            update = await Updates.CheckAsync(http, AppInfo.ReleaseFile, AppInfo.CurrentVersion);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return;   // offline or GitHub busy: next start
        }

        _settings.UpdateChecked = DateTime.UtcNow;
        SaveSettings();
        string version = update?.Version.ToString(3) ?? "";
        if (update is null || version == _settings.SkipVersion)
            return;

        // Right after the start the scanner is still being asked about: wait for it (a scan is not cut short).
        for (int i = 0; i < 120 && model.IsBusy; i++)
            await Task.Delay(500);
        if (_window is null || model.IsBusy || model.Session.HasPages)
            return;

        MessageBoxResult answer = MessageBox.Show(_window, string.Format(Strings.UpdateAvailableFormat, version, AppInfo.Version),
            Strings.AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            _settings.SkipVersion = version;
            SaveSettings();
            return;
        }

        // A copy in a folder without write access (Program Files): the release page instead.
        if (!Updates.CanWriteNextTo(exe))
        {
            OpenPage(update.Page);
            return;
        }

        try
        {
            Progress<int> progress = new(p => model.Status = string.Format(Strings.UpdateDownloadingFormat, p));
            string downloaded = await Updates.DownloadAsync(http, update, exe, progress);
            if (model.IsBusy || model.Session.HasPages)
            {
                Updates.CleanUp(exe);
                _settings.UpdateChecked = null;
                SaveSettings();
                model.Status = Strings.UpdatePostponed;
                return;
            }

            Updates.Swap(exe, downloaded);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                ArgumentList = { Updates.AfterUpdateArgument, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                UseShellExecute = false,
            });
            Shutdown();
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
                                      or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Updates.CleanUp(exe);
            model.Status = Strings.UpdateFailed + " " + e.Message;
        }
    }

    private void SaveSettings()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void OpenPage(Uri page)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public void ShowMainWindow()
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
            return;
        }

        MainViewModel model = new(_store, _settings, [_wia!, _remote!]);
        _model = model;
        model.LocalScan += NoteLocalUse;
        model.ReleaseAccepted += () => _window?.Close();
        model.SettingsRequested += OnSettingsRequested;
        OpenWindow(model, refreshDevices: true);
    }

    /// <summary>A window for the model; after a language change the same model gets a new window.</summary>
    private void OpenWindow(MainViewModel model, bool refreshDevices)
    {
        MainWindow window = new(model);
        if (refreshDevices)
        {
            window.ContentRendered += async (_, _) =>
            {
                await model.RefreshDevicesAsync();
                await CheckForUpdateAsync(model);
            };
        }
        // In use while it is open and not minimized (closed or minimized: nobody is scanning here).
        void Track()
        {
            bool shown = window.IsVisible && window.WindowState != WindowState.Minimized;
            if (shown && !_windowShown)
                NoteLocalUse();
            _windowShown = shown;
        }

        window.IsVisibleChanged += (_, _) => Track();
        window.StateChanged += (_, _) => Track();
        window.Closed += (_, _) =>
        {
            _windowShown = false;
            if (ReferenceEquals(_window, window))
                _window = null;
            if (_window is null && _tray is null)
                Shutdown();
        };
        _window = window;
        MainWindow = window;
        window.Show();
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        if (_window is null || sender is not MainViewModel model)
            return;

        // The 180 degree option is about the selected scanner; a shared scanner's setting lives on its computer.
        ScannerDevice? device = model.Device;
        int rotation = device is null ? 0
            : device.Backend == RemoteBackend.BackendName ? _remote!.KnownRotation(device.Id) ?? 0
            : _settings.GetRotation(device.Id);
        // Sharing makes sense with a scanner attached here (shared ones of other computers do not count).
        bool canShare = model.IsSearchingDevices || model.Devices.Any(d => d.Backend != RemoteBackend.BackendName);
        SettingsModel settingsModel = new(_settings, device, rotation, canShare);
        SettingsWindow dialog = new(settingsModel) { Owner = _window };
        if (dialog.ShowDialog() != true)
            return;

        (bool languageChanged, bool serverChanged) = settingsModel.ApplyTo(_settings, RemoteBackend.BackendName,
            (id, r) => _ = SetRemoteRotationAsync(model, id, r));
        model.SaveNow();
        model.SettingsApplied();
        ApplyTheme(_settings.Theme);

        if (serverChanged)
        {
            TryAutostart(_settings.ServerEnabled);
            if (_settings.ServerEnabled)
            {
                ShowTray();
                _ = StartServerAsync();
            }
            else
            {
                HideTray();
                _ = StopServerAsync();
            }
        }

        if (languageChanged)
        {
            // Text is read when a window is built: rebuild the main window in place.
            Strings.SetLanguage(_settings.Language);
            if (_tray is not null)
            {
                HideTray();
                ShowTray();
            }

            // Same model, new window: the preview, the frames and the PDF pages stay.
            MainWindow old = _window;
            old.SavePlacement();
            _window = null;
            model.LanguageChanged();
            OpenWindow(model, refreshDevices: false);
            old.CloseWithoutPrompt();
        }
    }

    /// <summary>Switches the Fluent theme of all windows at once (no restart needed).</summary>
    private void ApplyTheme(UiTheme theme) => ThemeMode = theme switch
    {
        UiTheme.Dark => ThemeMode.Dark,
        _ => ThemeMode.Light,
    };

    private async Task SetRemoteRotationAsync(MainViewModel model, string deviceId, int rotation)
    {
        try
        {
            await _remote!.SetRotationAsync(deviceId, rotation);
        }
        catch (ScannerException e)
        {
            model.Status = Strings.ErrorOther + " " + e.Message;
        }
    }

    private void NoteLocalUse() => Interlocked.Exchange(ref _lastLocalUse, DateTime.UtcNow.Ticks);

    /// <summary>
    /// Someone is using the scanners at this computer: a scan is running here, or the window is open and
    /// was opened, or used to scan, within <see cref="InUseFor"/>. Called on server threads.
    /// </summary>
    private bool InUseHere() =>
        (_model?.ScanningHere ?? false)
        || (_windowShown && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastLocalUse) < InUseFor.Ticks);

    /// <summary>Another computer asks for the scanner: the person here answers on a card (or it goes by itself).</summary>
    private void OnReleaseRequested(string from)
    {
        if (_window is null || _model is null || !_windowShown)
            return;
        // Unsaved PDF pages would be lost by closing the window on its own: then only the person decides.
        _model.ShowReleaseRequest(from, autoAccept: !_model.Session.HasPages);
        _window.Activate();
    }

    private async Task StartServerAsync()
    {
        if (_server is not null || _wia is null)
            return;

        // Requests arrive on server threads. Rotations are thread-safe in AppSettings; the file is written
        // on the UI thread later, without making the request wait for it (no Dispatcher.Invoke here:
        // a blocked UI thread must never hang the server, nor the server the UI thread).
        ScanServer server = new(_wia,
            id => _settings.GetRotation(id),
            (id, rotation) =>
            {
                _settings.SetRotation(id, rotation);
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _store.Save(_settings);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                    }
                });
            },
            InUseHere,
            from => Dispatcher.BeginInvoke(() => OnReleaseRequested(from)));
        try
        {
            await server.StartAsync();
            _server = server;
            if (_tray is not null)
                _tray.ToolTipText = $"{Strings.TrayTooltip} ({Environment.MachineName}:{server.Port})";
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            await server.DisposeAsync();
            if (_tray is not null)
                _tray.ToolTipText = $"{Strings.TrayTooltip}: {e.Message}";
        }
    }

    private async Task StopServerAsync()
    {
        ScanServer? server = _server;
        _server = null;
        if (server is not null)
            await server.DisposeAsync();
    }

    private void ShowTray()
    {
        if (_tray is not null)
            return;

        MenuItem open = new() { Header = Strings.TrayOpen, FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowMainWindow();
        MenuItem exit = new() { Header = Strings.TrayExit };
        exit.Click += (_, _) => Shutdown();

        _tray = new TaskbarIcon
        {
            ToolTipText = Strings.TrayTooltip,
            IconSource = new BitmapImage(new Uri("pack://application:,,,/OmniScan;component/Assets/OmniScan.ico")),
            ContextMenu = new ContextMenu { Items = { open, new Separator(), exit } },
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowMainWindow();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private void HideTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    private static void TryAutostart(bool enabled)
    {
        try
        {
            Autostart.Set(enabled);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Not fatal: the option still works for this session.
        }
    }

    private void ListenForShowRequests()
    {
        _showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        Thread listener = new(() =>
        {
            while (_showWindow.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        })
        {
            IsBackground = true,
            Name = "Show window requests",
        };
        listener.Start();
    }
}
