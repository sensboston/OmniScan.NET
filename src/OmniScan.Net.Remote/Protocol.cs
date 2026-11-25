using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using OmniScan.Net.Core;

namespace OmniScan.Net.Remote;

/// <summary>Wire format shared by the server and the client. Plain JSON over HTTP; frames as a binary body.</summary>
public static class Protocol
{
    /// <summary>UDP port the servers listen on for discovery probes.</summary>
    public const int DiscoveryPort = 18524;

    /// <summary>First HTTP port tried by a server; the next few are tried when it is taken.</summary>
    public const int HttpPort = 18523;

    public const string ProbeMessage = "OMNISCAN-DISCOVER-1";

    /// <summary>Clockwise rotation of the frame as configured on the server for that scanner.</summary>
    public const string RotationHeader = "X-OmniScan-Rotation";

    public const string DpiHeader = "X-OmniScan-Dpi";

    public const string InfoPath = "/api/v1/info";
    public const string CapabilitiesPath = "/api/v1/capabilities";
    public const string ScanPath = "/api/v1/scan";
    public const string RotationPath = "/api/v1/rotation";
    public const string ReleasePath = "/api/v1/release";

    /// <summary>Identifies this process, so a machine that is client and server ignores its own answer.</summary>
    public static Guid InstanceId { get; } = Guid.NewGuid();

    /// <summary>
    /// Windows reports an ICMP "port unreachable" for an earlier datagram as WSAECONNRESET on the next
    /// receive of a UDP socket; a probe to a computer without OmniScan would break discovery. Turn that off.
    /// </summary>
    public static void IgnoreUdpConnectionReset(Socket socket)
    {
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        if (OperatingSystem.IsWindows())
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }

    /// <summary>Loopback, private (RFC 1918), link-local and unique-local addresses: the home network.</summary>
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;

        byte[] b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }
}

/// <summary>Answer to a discovery probe and to GET info.</summary>
public sealed record ServerInfo(Guid InstanceId, string Host, int Port, IReadOnlyList<RemoteDevice> Devices);

/// <summary>
/// A scanner attached to a server; <see cref="Id"/> is the server's local device id,
/// <see cref="Rotation"/> its "turn scans upside down" rotation setting there (0 or 180).
/// </summary>
/// <param name="InUse">Someone is using the scanner at that computer now (see <see cref="ScannerError.InUse"/>).</param>
public sealed record RemoteDevice(string Id, string Name, int Rotation = 0, bool InUse = false);

/// <summary>Asks the person at the scanner's computer to let it go; <see cref="From"/> is the asking computer.</summary>
public sealed record RemoteRelease(string From);

/// <summary>Changes the "turn scans upside down" rotation of a shared scanner from another computer.</summary>
public sealed record RemoteRotation(string DeviceId, int Rotation);

public sealed record RemoteScanRequest(string DeviceId, int Dpi, ColorMode ColorMode, ScanSource Source);

public sealed record RemoteError(ScannerError Error, string Message);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(ScanCapabilities))]
[JsonSerializable(typeof(RemoteScanRequest))]
[JsonSerializable(typeof(RemoteError))]
[JsonSerializable(typeof(RemoteRotation))]
[JsonSerializable(typeof(RemoteRelease))]
internal sealed partial class ProtocolJson : JsonSerializerContext
{
}
