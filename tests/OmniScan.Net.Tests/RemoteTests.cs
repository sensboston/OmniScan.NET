using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OmniScan.Net.Core;
using OmniScan.Net.Remote;

namespace OmniScan.Net.Tests;

/// <summary>Server and client in one process, over loopback, with a fake scanner.</summary>
public sealed class RemoteTests
{
    private sealed class FakeScanner : IScannerBackend
    {
        public int Scans;
        public ScannerError? Fail;

        public string Name => "WIA";

        public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScannerDevice>>(
            [
                new ScannerDevice("wia:1", "Test scanner", "WIA"),
                new ScannerDevice("wia:2", "Vendor network scanner", "WIA", Attached: false),
            ]);

        public Task<ScanCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScanCapabilities([75, 300], [ColorMode.Color, ColorMode.Grayscale], [ScanSource.Flatbed], 8.5, 11.69));

        public Task<ScannedFrame> ScanAsync(string deviceId, ScanRequest request, CancellationToken cancellationToken = default)
        {
            if (Fail is { } error)
                throw new ScannerException(error, "fake failure");
            Interlocked.Increment(ref Scans);
            byte[] data = new byte[3_000_000];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i % 251);
            return Task.FromResult(new ScannedFrame(data, "image/bmp", request.Dpi));
        }
    }

    private static int _nextPort = 28600;

    private static async Task<(ScanServer Server, RemoteBackend Client, FakeScanner Scanner)> StartAsync(int rotation = 180)
    {
        FakeScanner scanner = new();
        int http = Interlocked.Add(ref _nextPort, 2), udp = http + 1;
        Dictionary<string, int> rotations = new() { ["wia:1"] = rotation };
        ScanServer server = new(scanner, id => rotations.GetValueOrDefault(id), (id, r) => rotations[id] = r);
        await server.StartAsync(http, udp);
        RemoteBackend client = new(TimeSpan.FromMilliseconds(500), udp, ignoreOwnServer: false);
        return (server, client, scanner);
    }

    [Fact]
    public async Task Discovery_FindsServerAndItsScanner()
    {
        (ScanServer server, RemoteBackend client, _) = await StartAsync();
        await using (server)
        using (client)
        {
            IReadOnlyList<ScannerDevice> devices = await client.GetDevicesAsync();

            ScannerDevice device = Assert.Single(devices);
            Assert.Equal(RemoteBackend.BackendName, device.Backend);
            Assert.Equal(RemoteBackend.MakeId(Environment.MachineName, "wia:1"), device.Id);
            Assert.Contains("Test scanner", device.Name);
        }
    }

    [Fact]
    public async Task Scan_ReturnsFrameWithServerRotation()
    {
        (ScanServer server, RemoteBackend client, FakeScanner scanner) = await StartAsync(rotation: 180);
        await using (server)
        using (client)
        {
            ScannerDevice device = (await client.GetDevicesAsync()).Single();

            ScanCapabilities caps = await client.GetCapabilitiesAsync(device.Id);
            ScannedFrame frame = await client.ScanAsync(device.Id, new ScanRequest(300, ColorMode.Color, ScanSource.Flatbed));

            Assert.Equal([75, 300], caps.Resolutions);
            Assert.Equal(3_000_000, frame.Data.Length);
            Assert.Equal(250, frame.Data[250]);
            Assert.Equal("image/bmp", frame.MediaType);
            Assert.Equal(300, frame.Dpi);
            Assert.Equal(180, frame.DeviceRotation);
            Assert.Equal(1, scanner.Scans);
        }
    }

    [Fact]
    public async Task Rotation_IsReportedAndCanBeChangedFromClient()
    {
        (ScanServer server, RemoteBackend client, _) = await StartAsync(rotation: 180);
        await using (server)
        using (client)
        {
            ScannerDevice device = (await client.GetDevicesAsync()).Single();
            Assert.Equal(180, client.KnownRotation(device.Id));

            await client.SetRotationAsync(device.Id, 0);

            ScannedFrame frame = await client.ScanAsync(device.Id, new ScanRequest(75, ColorMode.Color, ScanSource.Flatbed));
            Assert.Equal(0, frame.DeviceRotation);
            await client.GetDevicesAsync();
            Assert.Equal(0, client.KnownRotation(device.Id));
        }
    }

    [Fact]
    public async Task ScannerError_TravelsToClient()
    {
        (ScanServer server, RemoteBackend client, FakeScanner scanner) = await StartAsync();
        await using (server)
        using (client)
        {
            ScannerDevice device = (await client.GetDevicesAsync()).Single();
            scanner.Fail = ScannerError.CoverOpen;

            ScannerException e = await Assert.ThrowsAsync<ScannerException>(
                () => client.ScanAsync(device.Id, new ScanRequest(75, ColorMode.Color, ScanSource.Flatbed)));
            Assert.Equal(ScannerError.CoverOpen, e.Error);
        }
    }

    [Fact]
    public async Task OwnServer_IsIgnoredByDefault()
    {
        (ScanServer server, RemoteBackend _, FakeScanner _) = await StartAsync();
        await using (server)
        {
            using RemoteBackend client = new(TimeSpan.FromMilliseconds(400), 28598);
            client.AddServer(Environment.MachineName, new Uri($"http://127.0.0.1:{server.Port}"));
            Assert.Empty(await client.GetDevicesAsync());
        }
    }

    [Fact]
    public async Task UnknownHost_IsNotFound()
    {
        using RemoteBackend client = new(TimeSpan.FromMilliseconds(200), 28599, ignoreOwnServer: false);
        ScannerException e = await Assert.ThrowsAsync<ScannerException>(
            () => client.GetCapabilitiesAsync(RemoteBackend.MakeId("NO-SUCH-PC", "wia:1")));
        Assert.Equal(ScannerError.NotFound, e.Error);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.0.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:4860::8888", false)]
    public void LocalNetworkFilter(string address, bool expected)
    {
        Assert.Equal(expected, Protocol.IsLocalNetwork(IPAddress.Parse(address)));
    }

    [Fact]
    public void DeviceId_RoundTrips()
    {
        string id = RemoteBackend.MakeId("LIVING-ROOM", "wia:{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\\0003");
        Assert.Equal(("LIVING-ROOM", "wia:{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\\0003"), RemoteBackend.ParseId(id));
    }
}
