using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniScan.Net.Core;

namespace OmniScan.Net.Remote;

/// <summary>
/// Scanners shared by other OmniScan instances in the home network. Servers are found by a UDP
/// broadcast; device ids are "remote:{host}|{server device id}", so a saved choice survives a new
/// IP address of the server.
/// </summary>
public sealed class RemoteBackend : IScannerBackend, IDisposable
{
    public const string BackendName = "Remote";
    public const string IdPrefix = "remote:";

    private readonly HttpClient _http;
    private readonly TimeSpan _discoveryWindow;
    private readonly int _discoveryPort;
    private readonly ConcurrentDictionary<string, Uri> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _ignoreOwnServer;
    private readonly ConcurrentDictionary<string, int> _rotations = new(StringComparer.Ordinal);

    /// <param name="ignoreOwnServer">Skip the server of this very process (its scanners are local already).</param>
    public RemoteBackend(TimeSpan? discoveryWindow = null, int discoveryPort = Protocol.DiscoveryPort, bool ignoreOwnServer = true)
    {
        _ignoreOwnServer = ignoreOwnServer;
        _discoveryWindow = discoveryWindow ?? TimeSpan.FromMilliseconds(1200);
        _discoveryPort = discoveryPort;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            // A 1200 dpi color scan takes minutes on a slow flatbed.
            Timeout = TimeSpan.FromMinutes(15),
        };
    }

    public string Name => BackendName;

    /// <summary>Adds a server by address, for networks where broadcasts do not get through.</summary>
    public void AddServer(string host, Uri baseAddress) => _servers[host] = baseAddress;

    public async Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await DiscoverAsync(cancellationToken);

        List<ScannerDevice> devices = [];
        foreach ((string host, Uri server) in _servers.ToArray())
        {
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                ServerInfo? info = await _http.GetFromJsonAsync(new Uri(server, Protocol.InfoPath), ProtocolJson.Default.ServerInfo, timeout.Token);
                if (info is null || IsOwn(info))
                    continue;
                foreach (RemoteDevice d in info.Devices)
                {
                    string id = MakeId(host, d.Id);
                    _rotations[id] = d.Rotation;
                    devices.Add(new ScannerDevice(id, $"{d.Name} — {host}", BackendName));
                }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                // A server that went away since the probe: just not listed.
            }
        }

        return devices;
    }

    public async Task<ScanCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        (Uri server, string id) = await ResolveAsync(deviceId, cancellationToken);
        using HttpResponseMessage response = await Send(() =>
            _http.GetAsync(new Uri(server, $"{Protocol.CapabilitiesPath}?deviceId={Uri.EscapeDataString(id)}"), cancellationToken));
        await ThrowIfError(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync(ProtocolJson.Default.ScanCapabilities, cancellationToken)
            ?? throw new ScannerException(ScannerError.Other, "Empty capabilities.");
    }

    public async Task<ScannedFrame> ScanAsync(string deviceId, ScanRequest request, CancellationToken cancellationToken = default)
    {
        (Uri server, string id) = await ResolveAsync(deviceId, cancellationToken);
        RemoteScanRequest body = new(id, request.Dpi, request.ColorMode, request.Source);
        using HttpResponseMessage response = await Send(() =>
            _http.PostAsync(new Uri(server, Protocol.ScanPath), JsonBody(body, ProtocolJson.Default.RemoteScanRequest), cancellationToken));
        await ThrowIfError(response, cancellationToken);

        byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        int dpi = HeaderInt(response, Protocol.DpiHeader) ?? request.Dpi;
        int rotation = HeaderInt(response, Protocol.RotationHeader) ?? 0;
        string mediaType = response.Content.Headers.ContentType?.MediaType ?? "image/bmp";
        return new ScannedFrame(data, mediaType, dpi, rotation);
    }

    /// <summary>The "turn scans upside down" rotation the server reported for its scanner at the last device listing.</summary>
    public int? KnownRotation(string deviceId) => _rotations.TryGetValue(deviceId, out int r) ? r : null;

    /// <summary>Changes the "turn scans upside down" rotation of a shared scanner; it is stored on the computer it is attached to.</summary>
    public async Task SetRotationAsync(string deviceId, int rotation, CancellationToken cancellationToken = default)
    {
        (Uri server, string id) = await ResolveAsync(deviceId, cancellationToken);
        using HttpResponseMessage response = await Send(() =>
            _http.PostAsync(new Uri(server, Protocol.RotationPath),
                JsonBody(new RemoteRotation(id, rotation), ProtocolJson.Default.RemoteRotation), cancellationToken));
        await ThrowIfError(response, cancellationToken);
        _rotations[deviceId] = rotation;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A JSON body with a known length: the scan server reads Content-Length bodies only.</summary>
    private static ByteArrayContent JsonBody<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        ByteArrayContent content = new(JsonSerializer.SerializeToUtf8Bytes(value, type));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private bool IsOwn(ServerInfo info) => _ignoreOwnServer && info.InstanceId == Protocol.InstanceId;

    internal static string MakeId(string host, string deviceId) => IdPrefix + host + "|" + deviceId;

    internal static (string Host, string DeviceId) ParseId(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Not a remote device id.", nameof(id));
        int bar = id.IndexOf('|', IdPrefix.Length);
        if (bar < 0)
            throw new ArgumentException("Malformed remote device id.", nameof(id));
        return (id[IdPrefix.Length..bar], id[(bar + 1)..]);
    }

    private async Task<(Uri Server, string DeviceId)> ResolveAsync(string deviceId, CancellationToken ct)
    {
        (string host, string id) = ParseId(deviceId);
        if (!_servers.ContainsKey(host))
            await DiscoverAsync(ct);
        if (!_servers.TryGetValue(host, out Uri? server))
            throw new ScannerException(ScannerError.NotFound, $"OmniScan on {host} does not answer.");
        return (server, id);
    }

    /// <summary>Broadcasts a probe on every IPv4 interface and collects the answers for a moment.</summary>
    private async Task DiscoverAsync(CancellationToken ct)
    {
        using UdpClient udp = new(AddressFamily.InterNetwork) { EnableBroadcast = true };
        Protocol.IgnoreUdpConnectionReset(udp.Client);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        byte[] probe = Encoding.ASCII.GetBytes(Protocol.ProbeMessage);
        foreach (IPAddress target in BroadcastAddresses())
        {
            try
            {
                await udp.SendAsync(probe, new IPEndPoint(target, _discoveryPort), ct);
            }
            catch (SocketException)
            {
                // An interface without a route: ignore.
            }
        }

        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(_discoveryWindow);
        try
        {
            while (true)
            {
                UdpReceiveResult answer;
                try
                {
                    answer = await udp.ReceiveAsync(window.Token);
                }
                catch (SocketException)
                {
                    continue;
                }

                if (!Protocol.IsLocalNetwork(answer.RemoteEndPoint.Address))
                    continue;
                ServerInfo? info;
                try
                {
                    info = JsonSerializer.Deserialize(answer.Buffer, ProtocolJson.Default.ServerInfo);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (info is null || IsOwn(info))
                    continue;
                IPAddress address = answer.RemoteEndPoint.Address;
                _servers[info.Host] = new Uri($"http://{(address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString())}:{info.Port}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The discovery window is over.
        }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        HashSet<IPAddress> targets = [IPAddress.Broadcast, IPAddress.Loopback];
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (UnicastIPAddressInformation a in nic.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask is null)
                    continue;
                byte[] ip = a.Address.GetAddressBytes(), mask = a.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++)
                    ip[i] = (byte)(ip[i] | ~mask[i]);
                targets.Add(new IPAddress(ip));
            }
        }

        return targets;
    }

    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException e)
        {
            throw new ScannerException(ScannerError.Offline, e.Message, e);
        }
        catch (TaskCanceledException e) when (e.InnerException is TimeoutException)
        {
            throw new ScannerException(ScannerError.Offline, "The scan server did not answer in time.", e);
        }
    }

    private static async Task ThrowIfError(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        RemoteError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync(ProtocolJson.Default.RemoteError, ct);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
        }

        throw new ScannerException(error?.Error ?? ScannerError.Other,
            error?.Message ?? $"Scan server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : null;
}
