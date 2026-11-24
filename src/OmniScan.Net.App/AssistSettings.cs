using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniScan.Net.Assist;
using OmniScan.Net.Core;

namespace OmniScan.Net.App;

public enum KeyState
{
    None,
    Checking,
    Good,
    Bad,
}

/// <summary>
/// The online-service part of the settings: the key, checked as soon as it is entered (and once more when
/// the window opens), the warning about what is sent where, and the three uses, which can be turned on
/// only with a working key.
/// </summary>
public sealed class AssistSettingsModel : INotifyPropertyChanged
{
    private static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(700);

    private readonly string? _model;
    private string _key;
    private bool _ready;
    private KeyState _state;
    private string _status = "";
    private CancellationTokenSource? _check;

    public AssistSettingsModel(AppSettings settings)
    {
        _key = settings.AssistKey ?? "";
        _model = settings.AssistModel;
        // A saved key worked when it was saved; it stays usable unless the service now refuses it.
        _ready = _key.Length > 0;
        Orient = settings.AssistOrient;
        Describe = settings.AssistDescribe;
        PdfText = settings.AssistPdfText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key
    {
        get => _key;
        set
        {
            value = value.Trim();
            if (value == _key)
                return;
            _key = value;
            Ready = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Warning));
            OnPropertyChanged(nameof(CanCheck));
            Show(KeyState.None, "");
            // Pasted keys are checked by themselves; typing waits for a pause.
            if (AssistProviders.Detect(_key) is not null)
                _ = CheckAsync(CheckDelay);
            else if (_key.Length > 0)
                Cancel();
        }
    }

    /// <summary>The key works: the uses below can be turned on.</summary>
    public bool Ready
    {
        get => _ready;
        private set => Set(ref _ready, value);
    }

    public KeyState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>The result of the last check: the service and model, or what is wrong.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool CanCheck => _key.Length > 0 && _state != KeyState.Checking;

    /// <summary>What is sent where, with what the service may do with it.</summary>
    public string Warning
    {
        get
        {
            AssistProvider? p = AssistProviders.Detect(_key);
            string text = string.Format(Strings.AssistWarning, p is { } known ? AssistProviders.Name(known) : Strings.AssistOnline);
            return p switch
            {
                AssistProvider.Google => text + " " + Strings.AssistGoogleNotice,
                AssistProvider.OpenRouter => text + " " + Strings.AssistOpenRouterNotice,
                _ => text,
            };
        }
    }

    public bool Orient { get; set; }

    public bool Describe { get; set; }

    public bool PdfText { get; set; }

    /// <summary>Checks the key now (the button, and the window opening with a saved key).</summary>
    public Task CheckNowAsync() => _key.Length == 0 ? Task.CompletedTask : CheckAsync(TimeSpan.Zero);

    public void ApplyTo(AppSettings settings)
    {
        Cancel();
        settings.AssistKey = _key.Length == 0 ? null : _key;
        settings.AssistOrient = Orient;
        settings.AssistDescribe = Describe;
        settings.AssistPdfText = PdfText;
    }

    /// <summary>Stops a check in progress (the window closes).</summary>
    public void Cancel()
    {
        _check?.Cancel();
        _check = null;
    }

    private async Task CheckAsync(TimeSpan delay)
    {
        Cancel();
        CancellationTokenSource cts = new();
        _check = cts;
        string key = _key;
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cts.Token);
            Show(KeyState.Checking, Strings.AssistChecking);
            using AssistClient client = await AssistClient.ConnectAsync(key, _model, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            Ready = true;
            Show(KeyState.Good, string.Format(Strings.AssistConnected, AssistProviders.Name(client.Provider), client.Model));
        }
        catch (OperationCanceledException)
        {
            // A newer key or the window closed; nothing to show.
        }
        catch (AssistException e)
        {
            if (cts.IsCancellationRequested)
                return;
            // Without a connection a saved key is not wrong, only unchecked.
            if (e.Failure != AssistFailure.Unavailable)
                Ready = false;
            Show(KeyState.Bad, e.Failure switch
            {
                AssistFailure.UnknownKey => Strings.AssistUnknownKey,
                AssistFailure.BadKey => Strings.AssistBadKey,
                AssistFailure.NoModel => Strings.AssistNoModel,
                AssistFailure.Unavailable => Strings.AssistUnavailable,
                _ => Strings.AssistBadAnswer,
            });
        }
        finally
        {
            if (ReferenceEquals(_check, cts))
                _check = null;
            cts.Dispose();
        }
    }

    private void Show(KeyState state, string status)
    {
        State = state;
        Status = status;
        OnPropertyChanged(nameof(CanCheck));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
