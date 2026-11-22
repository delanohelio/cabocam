using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace CaboCam.Services;

public sealed class AppSettings
{
    public string? LastDeviceSerial { get; set; }
    public string? LastHost { get; set; }
    public string? CameraId { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    public string Codec { get; set; } = "h264";
    public int BitrateKbps { get; set; } = 12000;
    public string Orientation { get; set; } = "landscape";
    public bool Mirror { get; set; }
    public bool AutoStart { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>Show every setting (false = basic mode with the essentials only).</summary>
    public bool AdvancedMode { get; set; }

    /// <summary>GitHub "owner/repo" whose latest Release carries CaboCam-iOS-unsigned.ipa.</summary>
    public string IosReleaseRepo { get; set; } = "delanohelio/cabocam";
    /// <summary>Last successful sign + install of the iOS app (free Apple ID profile lasts 7 days).</summary>
    public DateTimeOffset? IosSignedAt { get; set; }
    public string? IosSignedVersion { get; set; }
    public string? IosSignedUdid { get; set; }

    /// <summary>Last setControl values per cameraId (zoom, focus, exposure, white balance, stabilization, hdr...).</summary>
    public Dictionary<string, Dictionary<string, JsonElement>> Controls { get; set; } = new();
}

/// <summary>%AppData%\CaboCam\settings.json</summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Control keys worth restoring on the next connection.
    private static readonly HashSet<string> Persisted = new()
    {
        "zoom", "focusMode", "lensPosition", "exposureMode", "iso", "exposureDurationSec", "exposureBias",
        "whiteBalanceMode", "temperature", "tint", "stabilization", "hdr", "lowLightBoost", "centerStage",
    };

    private readonly object _gate = new();
    private readonly Timer _saveTimer;

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaboCam", "settings.json");
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath { get; }
    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null) Current = loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao ler settings.json; usando padrões", ex);
            Current = new AppSettings();
        }
        Current.Controls ??= new();
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao salvar settings.json", ex);
            }
        }
    }

    /// <summary>Debounced save (500 ms).</summary>
    public void SaveSoon() => _saveTimer.Change(500, Timeout.Infinite);

    /// <summary>Remembers the persistable fields of a setControl message for a camera.</summary>
    public void RememberControls(string? cameraId, IReadOnlyDictionary<string, object?> message)
    {
        if (string.IsNullOrEmpty(cameraId)) return;
        lock (_gate)
        {
            if (!Current.Controls.TryGetValue(cameraId, out var saved))
                Current.Controls[cameraId] = saved = new Dictionary<string, JsonElement>();

            foreach (var kv in message)
            {
                if (!Persisted.Contains(kv.Key) || kv.Value is null) continue;
                if (kv.Key == "focusMode" && kv.Value is string fm && fm == "auto") continue; // one-shot, not a state
                saved[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
            }

            // Automatic modes make the manual values meaningless.
            if (message.TryGetValue("focusMode", out var f) && f as string == "continuous") saved.Remove("lensPosition");
            if (message.TryGetValue("exposureMode", out var e) && e as string == "continuous") { saved.Remove("iso"); saved.Remove("exposureDurationSec"); }
            if (message.TryGetValue("whiteBalanceMode", out var w) && w as string == "continuous") { saved.Remove("temperature"); saved.Remove("tint"); }
        }
        SaveSoon();
    }

    public Dictionary<string, JsonElement>? SavedControls(string? cameraId)
    {
        if (string.IsNullOrEmpty(cameraId)) return null;
        lock (_gate)
        {
            return Current.Controls.TryGetValue(cameraId, out var saved) ? new Dictionary<string, JsonElement>(saved) : null;
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Save();
    }
}
