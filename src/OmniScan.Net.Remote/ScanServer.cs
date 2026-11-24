using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using OmniScan.Net.Core;

namespace OmniScan.Net.Remote;

/// <summary>
/// Shares the scanners of one backend with the home network: a small HTTP endpoint for scanning and
/// a UDP responder for discovery. Requests from outside the local network are refused.
/// The HTTP part is deliberately minimal (one request per connection, Content-Length bodies only):
/// the only client is OmniScan itself, and a full web server would triple the size of the program.
/// </summary>
public sealed class ScanServer : IAsyncDisposable
{
    private const int PortAttempts = 10;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxConnections = 32;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _connections = new(MaxConnections);

    private readonly IScannerBackend _backend;
    private readonly Func<string, int> _rotationOf;
    private readonly Action<string, int>? _setRotation;
    private readonly string _host = Environment.MachineName;
    private TcpListener? _listener;
    private UdpClient? _udp;
    private CancellationTokenSource? _stop;
    private Task? _acceptLoop;
    private Task? _discoveryLoop;

    /// <param name="backend">The local scanners to share.</param>
    /// <param name="rotationOf">Fixed rotation configured on this machine for a device id.</param>
    /// <param name="setRotation">Stores a rotation changed from another computer; null makes it read-only.</param>
    public ScanServer(IScannerBackend backend, Func<string, int> rotationOf, Action<string, int>? setRotation = null)
    {
        _backend = backend;
        _rotationOf = rotationOf;
        _setRotation = setRotation;
    }

    /// <summary>The HTTP port actually in use, once started.</summary>
    public int Port { get; private set; }

    public Task StartAsync(int preferredPort = Protocol.HttpPort, int discoveryPort = Protocol.DiscoveryPort)
    {
        SocketException? last = null;
        for (int port = preferredPort; port < preferredPort + PortAttempts; port++)
        {
            TcpListener listener = new(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                last = e;
                listener.Dispose();
            }
        }

        if (_listener is null)
            throw new IOException("No free port for the scan server.", last);

        _stop = new CancellationTokenSource();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        Protocol.IgnoreUdpConnectionReset(_udp.Client);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));

        _acceptLoop = AcceptAsync(_listener, _stop.Token);
        _discoveryLoop = AnswerProbesAsync(_udp, _stop.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _stop?.Cancel();
        _listener?.Stop();
        _udp?.Dispose();
        foreach (Task? loop in new[] { _acceptLoop, _discoveryLoop })
        {
            if (loop is null)
                continue;
            try
            {
                await loop;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _stop?.Dispose();
        _connections.Dispose();
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                // A connection reset before it was accepted: keep serving.
                continue;
            }

            // Beyond the limit, a connection waits for a free slot instead of eating resources.
            await _connections.WaitAsync(ct);
            _ = ServeAsync(client, ct).ContinueWith(_ => _connections.Release(), TaskScheduler.Default);
        }
    }

    /// <summary>One request on one connection, then the connection is closed.</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                if (!Protocol.IsLocalNetwork((client.Client.RemoteEndPoint as IPEndPoint)?.Address))
                    return;

                NetworkStream stream = client.GetStream();
                Request? request;
                using (CancellationTokenSource readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readTimeout.CancelAfter(RequestReadTimeout);
                    request = await ReadRequestAsync(stream, readTimeout.Token);
                }

                Response response = request is null
                    ? Response.Status(400, "Bad Request")
                    : await HandleAsync(request, ct);
                using CancellationTokenSource writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                writeTimeout.CancelAfter(ResponseWriteTimeout);
                await WriteResponseAsync(stream, response, writeTimeout.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that went away or never finished its request.
            }
        }
    }

    private async Task<Response> HandleAsync(Request r, CancellationToken ct)
    {
        try
        {
            switch (r.Method, r.Path)
            {
                case ("GET", Protocol.InfoPath):
                    return Response.Json(await InfoAsync(ct), ProtocolJson.Default.ServerInfo);

                case ("GET", Protocol.CapabilitiesPath):
                {
                    string? deviceId = r.Query.GetValueOrDefault("deviceId");
                    if (string.IsNullOrEmpty(deviceId))
                        return Response.Status(400, "Bad Request");
                    ScanCapabilities caps = await _backend.GetCapabilitiesAsync(deviceId, ct);
                    return Response.Json(caps, ProtocolJson.Default.ScanCapabilities);
                }

                case ("POST", Protocol.RotationPath):
                {
                    RemoteRotation? change = JsonSerializer.Deserialize(r.Body, ProtocolJson.Default.RemoteRotation);
                    if (change is null || change.Rotation is not (0 or 180))
                        return Response.Status(400, "Bad Request");
                    if (_setRotation is null)
                        return Response.Status(403, "Forbidden");
                    _setRotation(change.DeviceId, change.Rotation);
                    return Response.Status(204, "No Content");
                }

                case ("POST", Protocol.ScanPath):
                {
                    RemoteScanRequest? scan = JsonSerializer.Deserialize(r.Body, ProtocolJson.Default.RemoteScanRequest);
                    if (scan is null)
                        return Response.Status(400, "Bad Request");
                    ScannedFrame frame = await _backend.ScanAsync(scan.DeviceId,
                        new ScanRequest(scan.Dpi, scan.ColorMode, scan.Source), ct);

                    // Uncompressed BMP from WIA: gzip at the fastest level shrinks it several times in a fraction of a second.
                    using MemoryStream packed = new();
                    using (GZipStream gzip = new(packed, CompressionLevel.Fastest, leaveOpen: true))
                        gzip.Write(frame.Data);
                    return new Response(200, "OK", frame.MediaType, packed.ToArray())
                    {
                        Gzip = true,
                        Headers =
                        {
                            [Protocol.RotationHeader] = _rotationOf(scan.DeviceId).ToString(CultureInfo.InvariantCulture),
                            [Protocol.DpiHeader] = frame.Dpi.ToString(CultureInfo.InvariantCulture),
                        },
                    };
                }

                default:
                    return Response.Status(404, "Not Found");
            }
        }
        catch (ScannerException e)
        {
            int code = e.Error == ScannerError.Busy ? 409 : 503;
            return Response.Json(new RemoteError(e.Error, e.Message), ProtocolJson.Default.RemoteError, code,
                code == 409 ? "Conflict" : "Service Unavailable");
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return Response.Status(400, "Bad Request");
        }
    }

    private async Task<ServerInfo> InfoAsync(CancellationToken ct)
    {
        IReadOnlyList<ScannerDevice> devices;
        try
        {
            // A stuck driver must not hang every client's device list.
            devices = await _backend.GetDevicesAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception e) when (e is ScannerException or TimeoutException)
        {
            devices = [];
        }

        // Network devices behind a vendor driver are reachable from the other computers directly (or not
        // reliably at all); share only what is plugged into this one.
        return new ServerInfo(Protocol.InstanceId, _host, Port,
            devices.Where(d => d.Attached).Select(d => new RemoteDevice(d.Id, d.Name, _rotationOf(d.Id))).ToList());
    }

    private async Task AnswerProbesAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult probe;
            try
            {
                probe = await udp.ReceiveAsync(ct);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }

            if (!Protocol.IsLocalNetwork(probe.RemoteEndPoint.Address)
                || Encoding.ASCII.GetString(probe.Buffer) != Protocol.ProbeMessage)
                continue;

            // The answer is small (no device list): the client asks for details over HTTP.
            ServerInfo info = new(Protocol.InstanceId, _host, Port, []);
            byte[] reply = JsonSerializer.SerializeToUtf8Bytes(info, ProtocolJson.Default.ServerInfo);
            await udp.SendAsync(reply, probe.RemoteEndPoint, ct);
        }
    }

    // ---- minimal HTTP/1.1 ----

    internal sealed record Request(string Method, string Path, Dictionary<string, string> Query, byte[] Body);

    internal sealed record Response(int Code, string Reason, string ContentType, byte[] Body)
    {
        public bool Gzip { get; init; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static Response Status(int code, string reason) => new(code, reason, "text/plain", []);

        public static Response Json<T>(T value, JsonTypeInfo<T> type, int code = 200, string reason = "OK") =>
            new(code, reason, "application/json", JsonSerializer.SerializeToUtf8Bytes(value, type));
    }

    /// <summary>Reads the request line, the headers and a Content-Length body; null for anything malformed.</summary>
    internal static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxHeaderBytes];
        int filled = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length)
                return null;
            int n = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (n == 0)
                return null;
            filled += n;
            headerEnd = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;

        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength > MaxBodyBytes))
                return null;
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        byte[] body = new byte[contentLength];
        int already = Math.Min(filled - (headerEnd + 4), contentLength);
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        if (already < contentLength)
            await stream.ReadExactlyAsync(body.AsMemory(already), ct);

        string target = first[1];
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        if (q >= 0)
        {
            foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                string value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                query[key] = value;
            }
        }

        return new Request(first[0].ToUpperInvariant(), path, query, body);
    }

    private static async Task WriteResponseAsync(Stream stream, Response r, CancellationToken ct)
    {
        StringBuilder head = new();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {r.Code} {r.Reason}\r\n");
        if (r.Code != 204)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Type: {r.ContentType}\r\n");
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {r.Body.Length}\r\n");
        }

        if (r.Gzip)
            head.Append("Content-Encoding: gzip\r\n");
        foreach ((string name, string value) in r.Headers)
            head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        head.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (r.Code != 204 && r.Body.Length > 0)
            await stream.WriteAsync(r.Body, ct);
        await stream.FlushAsync(ct);
    }
}
