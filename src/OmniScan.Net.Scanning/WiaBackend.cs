using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OmniScan.Net.Core;

namespace OmniScan.Net.Scanning;

/// <summary>
/// Local scanners through the WIA Automation Layer (wiaaut.dll, part of Windows).
/// All COM calls run on one STA thread; the objects are late-bound.
/// </summary>
public sealed class WiaBackend : IScannerBackend, IDisposable
{
    public const string IdPrefix = "wia:";

    private const int ScannerDeviceType = 1;

    // Device / item property ids (wiadef.h).
    private const int PropDeviceName = 7;
    private const int PropPort = 6;
    private const int PropDocumentHandlingCapabilities = 3086;
    private const int PropDocumentHandlingSelect = 3088;
    private const int PropHorizontalBedSize = 3074;
    private const int PropVerticalBedSize = 3075;
    private const int PropDataType = 4103;
    private const int PropHorizontalResolution = 6147;
    private const int PropVerticalResolution = 6148;
    private const int PropHorizontalStart = 6149;
    private const int PropVerticalStart = 6150;
    private const int PropHorizontalExtent = 6151;
    private const int PropVerticalExtent = 6152;

    // WIA_DPS_DOCUMENT_HANDLING_* flags.
    private const int Feeder = 0x001;
    private const int Flatbed = 0x002;

    // WIA_IPA_DATATYPE values.
    private const int DataTypeThreshold = 0;
    private const int DataTypeGrayscale = 2;
    private const int DataTypeColor = 3;

    // WiaSubType.
    private const int RangeSubType = 1;
    private const int ListSubType = 2;

    private readonly StaThread _sta = new("WIA");

    public string Name => "WIA";

    public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        _sta.Run<IReadOnlyList<ScannerDevice>>(() => Wrap(EnumerateDevices), cancellationToken);

    public Task<ScanCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default) =>
        _sta.Run(() => Wrap(() => ReadCapabilities(deviceId)), cancellationToken);

    public Task<ScannedFrame> ScanAsync(string deviceId, ScanRequest request, CancellationToken cancellationToken = default) =>
        _sta.Run(() => Wrap(() => Scan(deviceId, request)), cancellationToken);

    public void Dispose() => _sta.Dispose();

    private static List<ScannerDevice> EnumerateDevices()
    {
        dynamic manager = CreateDeviceManager();
        dynamic infos = manager.DeviceInfos;
        List<(ScannerDevice Device, bool Local)> found = [];
        int count = infos.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic info = infos.Item(i);
            if ((int)info.Type != ScannerDeviceType)
                continue;

            string id = (string)info.DeviceID;
            string name = PropertyValue(info.Properties, PropDeviceName) as string ?? id;
            string port = PropertyValue(info.Properties, PropPort) as string ?? "";
            bool attached = port.StartsWith(@"\\.\", StringComparison.Ordinal);
            // Only scanners plugged into this computer. Vendor network drivers (WIA over Wi-Fi) are left out
            // on purpose: unreliable, and the same device is usually also attached by USB.
            if (!attached)
                continue;
            found.Add((new ScannerDevice(IdPrefix + id, name, "WIA", attached), attached));
        }

        return found.OrderBy(d => d.Device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => d.Device).ToList();
    }

    private static ScanCapabilities ReadCapabilities(string deviceId)
    {
        dynamic device = Connect(deviceId);
        dynamic item = device.Items.Item(1);

        object? resolutionProp = FindProperty(item.Properties, PropHorizontalResolution);
        List<object> resolutionValues = ValuesOf(resolutionProp);
        List<int> resolutions = resolutionValues
            .Select(v => Convert.ToInt32(v)).Where(v => v > 0).Distinct().Order().ToList();

        List<ColorMode> colorModes = [];
        object? dataTypeProp = FindProperty(item.Properties, PropDataType);
        List<object> dataTypeValues = ValuesOf(dataTypeProp);
        List<int> dataTypes = dataTypeValues.Select(v => Convert.ToInt32(v)).ToList();
        if (dataTypes.Count == 0 || dataTypes.Contains(DataTypeColor))
            colorModes.Add(ColorMode.Color);
        if (dataTypes.Count == 0 || dataTypes.Contains(DataTypeGrayscale))
            colorModes.Add(ColorMode.Grayscale);
        if (dataTypes.Count == 0 || dataTypes.Contains(DataTypeThreshold))
            colorModes.Add(ColorMode.BlackWhite);

        int handling = Convert.ToInt32(PropertyValue(device.Properties, PropDocumentHandlingCapabilities) ?? Flatbed);
        List<ScanSource> sources = [];
        if ((handling & Flatbed) != 0 || (handling & Feeder) == 0)
            sources.Add(ScanSource.Flatbed);
        if ((handling & Feeder) != 0)
            sources.Add(ScanSource.Feeder);

        double width = Convert.ToDouble(PropertyValue(device.Properties, PropHorizontalBedSize) ?? 8500) / 1000.0;
        double height = Convert.ToDouble(PropertyValue(device.Properties, PropVerticalBedSize) ?? 11690) / 1000.0;

        if (resolutions.Count == 0)
            resolutions = [.. AppSettings.StandardDpi];
        return new ScanCapabilities(resolutions, colorModes, sources, width, height);
    }

    private static ScannedFrame Scan(string deviceId, ScanRequest request)
    {
        dynamic device = Connect(deviceId);

        if (request.Source == ScanSource.Feeder)
            SetProperty(device.Properties, PropDocumentHandlingSelect, Feeder);

        dynamic item = device.Items.Item(1);
        dynamic props = item.Properties;
        SetProperty(props, PropDataType, request.ColorMode switch
        {
            ColorMode.Grayscale => DataTypeGrayscale,
            ColorMode.BlackWhite => DataTypeThreshold,
            _ => DataTypeColor,
        });
        SetProperty(props, PropHorizontalResolution, request.Dpi);
        SetProperty(props, PropVerticalResolution, request.Dpi);
        SetProperty(props, PropHorizontalStart, 0);
        SetProperty(props, PropVerticalStart, 0);

        // The extent range depends on the resolution set above, so it is read afterwards.
        SetProperty(props, PropHorizontalExtent, MaxOf(FindProperty(props, PropHorizontalExtent)));
        SetProperty(props, PropVerticalExtent, MaxOf(FindProperty(props, PropVerticalExtent)));

        // Transfer() with no argument uses the driver's preferred format (BMP, uncompressed).
        // Passing a format id explicitly returns nothing with some drivers.
        dynamic image = item.Transfer();
        if (image is null)
            throw new ScannerException(ScannerError.Other, "The scanner returned no image.");

        byte[] data = (byte[])image.FileData.BinaryData;
        string extension = ((string)image.FileExtension).ToLowerInvariant();
        string mediaType = extension switch
        {
            "bmp" => "image/bmp",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "tif" or "tiff" => "image/tiff",
            _ => "application/octet-stream",
        };
        return new ScannedFrame(data, mediaType, request.Dpi);
    }

    private static dynamic CreateDeviceManager()
    {
        Type? type = Type.GetTypeFromProgID("WIA.DeviceManager");
        if (type is null)
            throw new ScannerException(ScannerError.NotAvailable, "Windows Image Acquisition is not available.");
        return Activator.CreateInstance(type)!;
    }

    private static dynamic Connect(string deviceId)
    {
        if (!deviceId.StartsWith(IdPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Not a WIA device id.", nameof(deviceId));
        string wiaId = deviceId[IdPrefix.Length..];

        dynamic infos = CreateDeviceManager().DeviceInfos;
        int count = infos.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic info = infos.Item(i);
            if ((string)info.DeviceID == wiaId)
                return info.Connect();
        }

        throw new ScannerException(ScannerError.NotFound, "The scanner is not connected.");
    }

    private static dynamic? FindProperty(dynamic properties, int id)
    {
        int count = properties.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic p = properties.Item(i);
            if ((int)p.PropertyID == id)
                return p;
        }

        return null;
    }

    private static object? PropertyValue(dynamic properties, int id) => FindProperty(properties, id)?.Value;

    private static void SetProperty(dynamic properties, int id, int value)
    {
        dynamic? p = FindProperty(properties, id);
        if (p is not null)
            p.Value = value;
    }

    private static int MaxOf(dynamic? property)
    {
        if (property is null)
            return 0;
        if ((int)property.SubType == RangeSubType)
            return (int)property.SubTypeMax;
        return Convert.ToInt32(property.Value);
    }

    private static List<object> ValuesOf(object? propertyObject)
    {
        List<object> values = [];
        dynamic? property = propertyObject;
        if (property is null || (int)property.SubType != ListSubType)
            return values;
        dynamic vector = property.SubTypeValues;
        int count = vector.Count;
        for (int i = 1; i <= count; i++)
            values.Add((object)vector.Item(i));
        return values;
    }

    private static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (COMException e)
        {
            throw Translate(e);
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException e)
        {
            throw new ScannerException(ScannerError.Other, "Unexpected WIA response: " + e.Message, e);
        }
    }

    internal static ScannerException Translate(COMException e)
    {
        ScannerError kind = unchecked((uint)e.HResult) switch
        {
            0x80210002 => ScannerError.PaperJam,
            0x80210003 => ScannerError.PaperEmpty,
            0x80210005 => ScannerError.Offline,
            0x80210006 => ScannerError.Busy,
            0x80210007 => ScannerError.WarmingUp,
            0x8021000A => ScannerError.Offline,
            0x80210015 => ScannerError.NotFound,
            0x80210016 => ScannerError.CoverOpen,
            _ => ScannerError.Other,
        };
        return new ScannerException(kind, $"{e.Message} (0x{e.HResult:X8})", e);
    }
}
