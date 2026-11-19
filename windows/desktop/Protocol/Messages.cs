using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaboCam.Protocol;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

public static class MessageTypes
{
    public const string GetCapabilities = "getCapabilities";
    public const string Configure = "configure";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string RequestKeyframe = "requestKeyframe";
    public const string SetControl = "setControl";
    public const string Ping = "ping";

    public const string Capabilities = "capabilities";
    public const string State = "state";
    public const string StreamStarted = "streamStarted";
    public const string StreamStopped = "streamStopped";
    public const string Error = "error";
    public const string Pong = "pong";
}

// ---- Phone -> desktop -------------------------------------------------------------

public sealed class HelloMessage
{
    public int Protocol { get; set; }
    public string? App { get; set; }
    public string? Version { get; set; }
    public string? DeviceName { get; set; }
    public string? Model { get; set; }
    public string? SystemVersion { get; set; }
}

public sealed class FormatInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double MaxFps { get; set; }
    public bool Hdr { get; set; }
}

public sealed class CameraInfo
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Position { get; set; }
    public string? DeviceType { get; set; }
    public List<FormatInfo> Formats { get; set; } = new();
    public double MinZoom { get; set; } = 1;
    public double MaxZoom { get; set; } = 1;
    public List<double> ZoomSwitchOver { get; set; } = new();
    public double ZoomDisplayMultiplier { get; set; } = 1;
    public bool HasTorch { get; set; }
    public List<string> FocusModes { get; set; } = new();
    public bool SupportsPointOfInterest { get; set; }
    public bool SupportsCustomExposure { get; set; }
    public double MinExposureBias { get; set; } = -2;
    public double MaxExposureBias { get; set; } = 2;
    public bool SupportsCustomWhiteBalance { get; set; }
    public bool SupportsLowLightBoost { get; set; }
    public bool CenterStage { get; set; }

    public bool IsFront => string.Equals(Position, "front", StringComparison.OrdinalIgnoreCase);
}

public sealed class CapabilitiesMessage
{
    public List<CameraInfo> Cameras { get; set; } = new();
}

public sealed class StatsInfo
{
    public double Fps { get; set; }
    public double BitrateKbps { get; set; }
    public double DroppedFrames { get; set; }
}

public sealed class DeviceState
{
    public bool Streaming { get; set; }
    public string? CameraId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public string? Codec { get; set; }
    public double BitrateKbps { get; set; }
    public string? Orientation { get; set; }
    public bool Mirror { get; set; }
    public double Zoom { get; set; } = 1;
    public string? FocusMode { get; set; }
    public double LensPosition { get; set; }
    public string? ExposureMode { get; set; }
    public double Iso { get; set; }
    public double ExposureDurationSec { get; set; }
    public double MinISO { get; set; }
    public double MaxISO { get; set; }
    public double MinExposureDurationSec { get; set; }
    public double MaxExposureDurationSec { get; set; }
    public double ExposureBias { get; set; }
    public string? WhiteBalanceMode { get; set; }
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Torch { get; set; }
    public string? Stabilization { get; set; }
    public string? ActiveStabilization { get; set; }
    public bool Hdr { get; set; }
    public bool HdrSupported { get; set; }
    public bool LowLightBoost { get; set; }
    public bool CenterStage { get; set; }
    public bool DimScreen { get; set; }
    public string? ThermalState { get; set; }
    public double BatteryLevel { get; set; } = -1;
    public bool Charging { get; set; }
    public StatsInfo? Stats { get; set; }
}

public sealed class StreamStartedMessage
{
    public string? Codec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
}

/// <summary>A VIDEO packet (payload: flags u8, pts i64 LE microseconds, Annex-B access unit).</summary>
public sealed class VideoPacket
{
    public const int HeaderSize = 9;

    public VideoPacket(byte[] payload)
    {
        Payload = payload;
        Flags = payload.Length > 0 ? payload[0] : (byte)0;
        PtsUs = payload.Length >= HeaderSize ? BitConverter.ToInt64(payload, 1) : 0; // little-endian hosts only (Windows)
    }

    public byte[] Payload { get; }
    public byte Flags { get; }
    public long PtsUs { get; }
    public bool IsKeyframe => (Flags & 0x01) != 0;
    public bool IsHevc => (Flags & 0x02) != 0;
    public int DataOffset => HeaderSize;
    public int DataLength => Math.Max(0, Payload.Length - HeaderSize);
}

// ---- Desktop -> phone builders ---------------------------------------------------

public static class Msg
{
    public static Dictionary<string, object?> Simple(string type) => new() { ["type"] = type };

    public static Dictionary<string, object?> Ping(long t) => new() { ["type"] = MessageTypes.Ping, ["t"] = t };

    public static Dictionary<string, object?> Configure(string? cameraId, int width, int height, int fps, string codec,
        int bitrateKbps, string orientation, bool mirror, int keyframeIntervalSec = 2)
    {
        var m = new Dictionary<string, object?>
        {
            ["type"] = MessageTypes.Configure,
            ["width"] = width,
            ["height"] = height,
            ["fps"] = fps,
            ["codec"] = codec,
            ["bitrateKbps"] = bitrateKbps,
            ["keyframeIntervalSec"] = keyframeIntervalSec,
            ["orientation"] = orientation,
            ["mirror"] = mirror,
        };
        if (!string.IsNullOrEmpty(cameraId)) m["cameraId"] = cameraId;
        return m;
    }

    public static Dictionary<string, object?> SetControl(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var m = new Dictionary<string, object?> { ["type"] = MessageTypes.SetControl };
        foreach (var kv in fields)
        {
            if (kv.Value is not null) m[kv.Key] = kv.Value;
        }
        return m;
    }
}

// ---- Helpers over capabilities ------------------------------------------------------

public static class CapabilityHelpers
{
    private static readonly int[] CommonFps = { 15, 24, 30, 60, 120 };

    /// <summary>Distinct "WxH" resolutions of a camera, descending by area.</summary>
    public static IEnumerable<(int Width, int Height)> Resolutions(this CameraInfo cam) =>
        cam.Formats.Select(f => (f.Width, f.Height)).Distinct().OrderByDescending(r => (long)r.Width * r.Height);

    /// <summary>fps options supported for a resolution (common values up to the format's max fps).</summary>
    public static IEnumerable<int> FpsOptions(this CameraInfo cam, int width, int height)
    {
        var max = cam.Formats.Where(f => f.Width == width && f.Height == height).Select(f => f.MaxFps).DefaultIfEmpty(30).Max();
        var set = new SortedSet<int>(CommonFps.Where(f => f <= max + 0.01));
        if (set.Count == 0) set.Add((int)Math.Max(1, Math.Floor(max)));
        return set.Reverse();
    }

    /// <summary>Picks the supported resolution/fps closest to the request (aspect, then area, then fps).</summary>
    public static (int Width, int Height, int Fps) PickFormat(this CameraInfo cam, int width, int height, int fps)
    {
        if (cam.Formats.Count == 0) return (width, height, fps);
        double aspect = height == 0 ? 16.0 / 9 : (double)width / height;
        var best = cam.Formats
            .OrderBy(f => Math.Abs((double)f.Width / Math.Max(1, f.Height) - aspect) > 0.02 ? 1 : 0)
            .ThenBy(f => Math.Abs((long)f.Width * f.Height - (long)width * height))
            .First();
        int pickedFps = (int)Math.Min(fps, Math.Floor(cam.Formats.Where(f => f.Width == best.Width && f.Height == best.Height).Max(f => f.MaxFps)));
        if (pickedFps < 1) pickedFps = 1;
        return (best.Width, best.Height, pickedFps);
    }

    /// <summary>Portuguese label like "Traseira 3× (Teleobjetiva)".</summary>
    public static string Label(this CameraInfo cam, IReadOnlyList<CameraInfo> all)
    {
        string type = cam.DeviceType ?? "";
        if (cam.IsFront || type == "trueDepth")
        {
            int fronts = all.Count(c => c.IsFront);
            return fronts > 1 && !string.IsNullOrWhiteSpace(cam.Name) ? $"Frontal ({cam.Name})" : "Frontal";
        }

        switch (type)
        {
            case "ultraWide": return "Traseira 0.5× (Ultra-angular)";
            case "wide": return "Traseira 1×";
            case "telephoto":
            {
                // Derive magnification from a virtual device that switches over to the telephoto lens.
                var virt = all.FirstOrDefault(c => !c.IsFront && (c.DeviceType == "triple" || c.DeviceType == "dual") && c.ZoomSwitchOver.Count > 0);
                if (virt is not null)
                {
                    double x = virt.ZoomSwitchOver.Max() * virt.ZoomDisplayMultiplier;
                    return $"Traseira {FormatZoom(x)}× (Teleobjetiva)";
                }
                return "Traseira (Teleobjetiva)";
            }
            case "triple": return "Traseira Tripla (automática)";
            case "dual": return "Traseira Dupla (automática)";
            case "dualWide": return "Traseira Dupla Ultra-angular (automática)";
            default: return string.IsNullOrWhiteSpace(cam.Name) ? cam.Id : cam.Name!;
        }
    }

    public static string FormatZoom(double x) =>
        Math.Abs(x - Math.Round(x)) < 0.05
            ? Math.Round(x).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Default camera: back wide, else triple, else first back, else first.</summary>
    public static CameraInfo? DefaultCamera(this CapabilitiesMessage caps) =>
        caps.Cameras.FirstOrDefault(c => !c.IsFront && c.DeviceType == "wide")
        ?? caps.Cameras.FirstOrDefault(c => !c.IsFront && c.DeviceType == "triple")
        ?? caps.Cameras.FirstOrDefault(c => !c.IsFront)
        ?? caps.Cameras.FirstOrDefault();
}
