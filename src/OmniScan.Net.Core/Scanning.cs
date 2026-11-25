using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OmniScan.Net.Core;

/// <summary>
/// A scanner as shown in the device list. <see cref="Id"/> is stable across runs.
/// <see cref="Attached"/>: connected to this computer itself (USB), as opposed to a network
/// device reached through a vendor driver; only attached scanners are shared with other computers.
/// </summary>
public sealed record ScannerDevice(string Id, string Name, string Backend, bool Attached = true)
{
    /// <summary>What is known now about a shared scanner (in use on its computer); not part of its identity.</summary>
    public DeviceState State { get; } = new();

    public bool Equals(ScannerDevice? other) =>
        other is not null && Id == other.Id && Name == other.Name && Backend == other.Backend && Attached == other.Attached;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Backend, Attached);
}

/// <summary>Whether a shared scanner is in use on its computer: null when not known (or not shared).</summary>
public sealed class DeviceState : System.ComponentModel.INotifyPropertyChanged
{
    private bool? _inUse;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public bool? InUse
    {
        get => _inUse;
        set
        {
            if (_inUse == value)
                return;
            _inUse = value;
            foreach (string name in new[] { nameof(InUse), nameof(IsInUse), nameof(IsFree) })
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    public bool IsInUse => _inUse == true;

    public bool IsFree => _inUse == false;
}

/// <summary>What a device can do; the UI offers only these values.</summary>
public sealed record ScanCapabilities(
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<ColorMode> ColorModes,
    IReadOnlyList<ScanSource> Sources,
    double BedWidthInches,
    double BedHeightInches);

/// <summary>One scan of the whole bed (scan areas come later).</summary>
public sealed record ScanRequest(int Dpi, ColorMode ColorMode, ScanSource Source);

/// <summary>
/// An acquired page, still encoded as the device delivered it (BMP from WIA). <see cref="DeviceRotation"/>
/// is set when the frame comes from another machine, which knows how its scanner is mounted.
/// </summary>
public sealed record ScannedFrame(byte[] Data, string MediaType, int Dpi, int? DeviceRotation = null);

/// <summary>One way of talking to scanners (WIA, remote OmniScan server, ...).</summary>
public interface IScannerBackend
{
    string Name { get; }

    /// <summary>Lists devices. May take seconds; never call on the UI thread synchronously.</summary>
    Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<ScanCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans one page. Cancellation is honored before the device starts; a transfer already in
    /// progress may run to its end (WIA cannot abort it).
    /// </summary>
    Task<ScannedFrame> ScanAsync(string deviceId, ScanRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What went wrong, so the UI can show a plain-language message.</summary>
public enum ScannerError
{
    Other,
    NotAvailable,
    NotFound,
    Offline,
    Busy,

    /// <summary>
    /// A shared scanner someone is using at its computer: OmniScan is open there and was used lately.
    /// Unlike <see cref="Busy"/> (another program holds the device), the person there can be asked.
    /// </summary>
    InUse,
    WarmingUp,
    CoverOpen,
    PaperEmpty,
    PaperJam,
}

/// <summary>A backend failure; <see cref="Exception.Message"/> holds the technical details.</summary>
public sealed class ScannerException : Exception
{
    public ScannerException(ScannerError error, string message)
        : base(message)
    {
        Error = error;
    }

    public ScannerException(ScannerError error, string message, Exception inner)
        : base(message, inner)
    {
        Error = error;
    }

    public ScannerError Error { get; }
}
