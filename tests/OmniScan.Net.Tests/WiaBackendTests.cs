using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OmniScan.Net.Core;
using OmniScan.Net.Scanning;

namespace OmniScan.Net.Tests;

public sealed class WiaBackendTests
{
    [Theory]
    [InlineData(0x80210006u, ScannerError.Busy)]
    [InlineData(0x80210016u, ScannerError.CoverOpen)]
    [InlineData(0x80210015u, ScannerError.NotFound)]
    [InlineData(0x8021000Au, ScannerError.Offline)]
    [InlineData(0x80004005u, ScannerError.Other)]
    public void Translate_MapsWiaErrors(uint hresult, ScannerError expected)
    {
        ScannerException e = WiaBackend.Translate(new COMException("x", unchecked((int)hresult)));
        Assert.Equal(expected, e.Error);
    }

    [Fact]
    public async Task GetDevices_DoesNotThrow()
    {
        // Machines without scanners return an empty list; WIA itself is part of Windows.
        using WiaBackend wia = new();
        IReadOnlyList<ScannerDevice> devices = await wia.GetDevicesAsync();
        Assert.All(devices, d => Assert.StartsWith(WiaBackend.IdPrefix, d.Id));
    }

    /// <summary>Real scan; runs only where OMNISCAN_HARDWARE_TEST=1 (the PC with the scanner).</summary>
    [Fact]
    public async Task Hardware_ScanFirstDevice()
    {
        if (Environment.GetEnvironmentVariable("OMNISCAN_HARDWARE_TEST") != "1")
            return;

        using WiaBackend wia = new();
        IReadOnlyList<ScannerDevice> devices = await wia.GetDevicesAsync();
        Assert.NotEmpty(devices);

        ScanCapabilities caps = await wia.GetCapabilitiesAsync(devices[0].Id);
        Assert.Contains(75, caps.Resolutions);

        foreach (ColorMode mode in caps.ColorModes)
        {
            ScannedFrame frame = await wia.ScanAsync(devices[0].Id, new ScanRequest(75, mode, ScanSource.Flatbed));
            Leptonica.Pix pix = OmniScan.Net.Imaging.FrameCodec.Decode(frame);
            Assert.InRange(pix.Width, 600, 700);
            Assert.Equal(mode switch { ColorMode.Color => 32, ColorMode.Grayscale => 8, _ => 1 }, pix.Depth);
        }
    }
}
