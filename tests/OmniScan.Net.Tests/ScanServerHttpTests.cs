using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmniScan.Net.Core;
using OmniScan.Net.Remote;

namespace OmniScan.Net.Tests;

/// <summary>The scan server's own HTTP handling: parsing, limits and misbehaving clients.</summary>
public sealed class ScanServerHttpTests
{
    private sealed class OneScanner : IScannerBackend
    {
        public string Name => "WIA";

        public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScannerDevice>>([new ScannerDevice("wia:1", "Scanner", "WIA")]);

        public Task<ScanCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScanCapabilities([300], [ColorMode.Color], [ScanSource.Flatbed], 8.5, 11.69));

        public Task<ScannedFrame> ScanAsync(string deviceId, ScanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScannedFrame(new byte[100], "image/bmp", request.Dpi));
    }

    private static int _nextPort = 28800;

    private static async Task<ScanServer> StartAsync()
    {
        int port = Interlocked.Add(ref _nextPort, 2);
        ScanServer server = new(new OneScanner(), _ => 0);
        await server.StartAsync(port, port + 1);
        return server;
    }

    /// <summary>Sends raw bytes and returns the status line (or "" when the server just closed).</summary>
    private static async Task<string> RawAsync(int port, byte[] request, int pauseAfterBytes = -1)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream s = client.GetStream();
        if (pauseAfterBytes > 0)
        {
            await s.WriteAsync(request.AsMemory(0, pauseAfterBytes));
            await Task.Delay(150);
            await s.WriteAsync(request.AsMemory(pauseAfterBytes));
        }
        else
        {
            await s.WriteAsync(request);
        }

        using StreamReader reader = new(s, Encoding.ASCII);
        return await reader.ReadLineAsync() ?? "";
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public async Task Info_Works()
    {
        await using ScanServer server = await StartAsync();
        string status = await RawAsync(server.Port, Ascii("GET /api/v1/info HTTP/1.1\r\nHost: x\r\n\r\n"));
        Assert.Equal("HTTP/1.1 200 OK", status);
    }

    [Fact]
    public async Task RequestSplitAcrossPackets_Works()
    {
        await using ScanServer server = await StartAsync();
        byte[] body = Ascii("""{"deviceId":"wia:1","dpi":300,"colorMode":"Color","source":"Flatbed"}""");
        byte[] request = [.. Ascii($"POST /api/v1/scan HTTP/1.1\r\nContent-Length: {body.Length}\r\n\r\n"), .. body];

        string status = await RawAsync(server.Port, request, pauseAfterBytes: 20);

        Assert.Equal("HTTP/1.1 200 OK", status);
    }

    [Theory]
    [InlineData("garbage\r\n\r\n", "HTTP/1.1 400 Bad Request")]
    [InlineData("GET /api/v1/info\r\n\r\n", "HTTP/1.1 400 Bad Request")]
    [InlineData("GET /nowhere HTTP/1.1\r\n\r\n", "HTTP/1.1 404 Not Found")]
    [InlineData("POST /api/v1/scan HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", "HTTP/1.1 400 Bad Request")]
    [InlineData("POST /api/v1/scan HTTP/1.1\r\nContent-Length: 999999999\r\n\r\n", "HTTP/1.1 400 Bad Request")]
    [InlineData("POST /api/v1/scan HTTP/1.1\r\nContent-Length: 5\r\n\r\nnope!", "HTTP/1.1 400 Bad Request")]
    [InlineData("GET /api/v1/capabilities HTTP/1.1\r\n\r\n", "HTTP/1.1 400 Bad Request")]
    public async Task BadRequests_GetAnErrorNotAHang(string request, string expected)
    {
        await using ScanServer server = await StartAsync();
        Assert.Equal(expected, await RawAsync(server.Port, Ascii(request)));
    }

    [Fact]
    public async Task OversizedHeaders_AreRefused()
    {
        await using ScanServer server = await StartAsync();
        string huge = "GET /api/v1/info HTTP/1.1\r\nX-Junk: " + new string('a', 40_000) + "\r\n\r\n";
        string status;
        try
        {
            status = await RawAsync(server.Port, Ascii(huge));
        }
        catch (IOException)
        {
            status = "closed";   // the server may close while the client is still sending
        }

        Assert.True(status is "HTTP/1.1 400 Bad Request" or "closed" or "", status);
    }

    [Fact]
    public async Task SilentClient_DoesNotBlockOthers()
    {
        await using ScanServer server = await StartAsync();

        // A few connections that never send anything.
        List<TcpClient> idle = [];
        for (int i = 0; i < 5; i++)
        {
            TcpClient c = new();
            await c.ConnectAsync(IPAddress.Loopback, server.Port);
            idle.Add(c);
        }

        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
            HttpResponseMessage r = await http.GetAsync($"http://127.0.0.1:{server.Port}{Protocol.InfoPath}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        finally
        {
            foreach (TcpClient c in idle)
                c.Dispose();
        }
    }

    [Fact]
    public async Task ManyParallelRequests_AllAnswered()
    {
        await using ScanServer server = await StartAsync();
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

        HttpResponseMessage[] answers = await Task.WhenAll(Enumerable.Range(0, 80)
            .Select(_ => http.GetAsync($"http://127.0.0.1:{server.Port}{Protocol.InfoPath}")));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
    }

    [Fact]
    public async Task RandomGarbage_ServerKeepsWorking()
    {
        await using ScanServer server = await StartAsync();
        Random random = new(42);
        for (int i = 0; i < 30; i++)
        {
            byte[] junk = new byte[random.Next(1, 3000)];
            random.NextBytes(junk);
            if (i % 3 == 0)
                junk = [.. junk, .. Ascii("\r\n\r\n")];
            try
            {
                using TcpClient c = new();
                await c.ConnectAsync(IPAddress.Loopback, server.Port);
                await c.GetStream().WriteAsync(junk);
            }
            catch (IOException)
            {
            }
        }

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        HttpResponseMessage r = await http.GetAsync($"http://127.0.0.1:{server.Port}{Protocol.InfoPath}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
