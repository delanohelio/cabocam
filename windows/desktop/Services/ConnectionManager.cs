using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaboCam.Protocol;
using CaboCam.Usbmux;

namespace CaboCam.Services;

public enum ConnectionState
{
    AppleServiceMissing,
    WaitingForDevice,
    Connecting,
    Connected,
    Streaming,
}

/// <summary>
/// Owns the lifetime of the link to the iPhone: usbmux discovery, TCP tunnel (or manual IP),
/// HELLO / capabilities / state, ping, auto start and reconnection with backoff.
/// Events are raised from background threads.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    public const int PhonePort = 7878;
    private const int PingIntervalMs = 2000;
    private const int RxTimeoutMs = 9000;

    private sealed record Target(UsbmuxDevice? Device, string? Host, int Port)
    {
        public string Describe => Device is not null ? $"iPhone (USB, {ShortSerial(Device.SerialNumber)})" : $"{Host}:{Port}";
    }

    private readonly SettingsStore _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, UsbmuxDevice> _devices = new();
    private readonly object _gate = new();

    private volatile bool _serviceAvailable = true;
    private volatile bool _inSession;
    private (string Host, int Port)? _manual;
    private CancellationTokenSource? _sessionCts;
    private ProtocolClient? _client;
    private string? _currentSerial;

    public ConnectionManager(SettingsStore settings) => _settings = settings;

    // ---- observable status -----------------------------------------------------------
    public ConnectionState State { get; private set; } = ConnectionState.WaitingForDevice;
    public string StatusDetail { get; private set; } = "";
    public string? DeviceName { get; private set; }
    public string? DeviceModel { get; private set; }
    public string? SystemVersion { get; private set; }
    public CapabilitiesMessage? Capabilities { get; private set; }
    public DeviceState? LastState { get; private set; }
    public int LatencyMs { get; private set; } = -1;

    // ---- Phone clock offset (phone capture clock ms - Environment.TickCount64) ------------
    private readonly object _clockGate = new();
    private readonly Queue<(long Rtt, long Offset)> _clockSamples = new();
    private long? _clockOffsetMs;

    /// <summary>
    /// Estimated phone-clock minus PC-clock (ms), from the lowest-RTT pong of the last ~20 s.
    /// Null until the phone app reports its clock (older app versions do not).
    /// </summary>
    public long? PhoneClockOffsetMs { get { lock (_clockGate) return _clockOffsetMs; } }

    private void UpdateClockOffset(long phoneMs, long sentAt, long rtt)
    {
        lock (_clockGate)
        {
            _clockSamples.Enqueue((rtt, phoneMs - (sentAt + rtt / 2)));
            while (_clockSamples.Count > 10) _clockSamples.Dequeue();
            var best = _clockSamples.MinBy(s => s.Rtt);
            _clockOffsetMs = best.Offset;
        }
    }

    private void ResetClockOffset()
    {
        lock (_clockGate)
        {
            _clockSamples.Clear();
            _clockOffsetMs = null;
        }
    }
    public bool IsManual => _manual is not null;

    public event Action? StatusChanged;
    public event Action<CapabilitiesMessage>? CapabilitiesReceived;
    public event Action<DeviceState>? DeviceStateReceived;
    public event Action<VideoPacket>? VideoReceived;
    /// <summary>Raised at the start of every new session so the video pipeline can reset.</summary>
    public event Action? SessionStarting;
    public event Action<string>? PhoneError;

    public void Start()
    {
        _ = Task.Run(() => WatchDevicesAsync(_cts.Token));
        _ = Task.Run(() => SessionLoopAsync(_cts.Token));
    }

    // ---- public commands -------------------------------------------------------------

    /// <summary>Connects to host:port (Wi-Fi fallback). Accepts "host" or "host:port".</summary>
    public void ConnectManual(string hostPort)
    {
        var (host, port) = ParseHostPort(hostPort);
        _settings.Current.LastHost = port == PhonePort ? host : $"{host}:{port}";
        _settings.SaveSoon();
        lock (_gate) { _manual = (host, port); }
        DropSession();
    }

    public void UseUsb()
    {
        lock (_gate) { _manual = null; }
        DropSession();
    }

    public static (string Host, int Port) ParseHostPort(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new FormatException("Endereço vazio");
        int port = PhonePort;
        string host = text;
        int idx = text.LastIndexOf(':');
        if (idx > 0 && text.IndexOf(':') == idx) // single colon => host:port (IPv6 not supported here)
        {
            host = text[..idx].Trim();
            if (!int.TryParse(text[(idx + 1)..], out port) || port is < 1 or > 65535)
                throw new FormatException("Porta inválida");
        }
        if (host.Length == 0) throw new FormatException("Endereço vazio");
        return (host, port);
    }

    public void Send(Dictionary<string, object?> message)
    {
        var client = _client;
        if (client is null) return;
        var ct = _sessionCts?.Token ?? CancellationToken.None;
        _ = SendSafeAsync(client, message, ct);
    }

    private async Task SendSafeAsync(ProtocolClient client, Dictionary<string, object?> message, CancellationToken ct)
    {
        try { await client.SendAsync(message, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Falha ao enviar '{message.GetValueOrDefault("type")}': {ex.Message}");
            DropSession();
        }
    }

    public void StartStream() => Send(Msg.Simple(MessageTypes.Start));
    public void StopStream() => Send(Msg.Simple(MessageTypes.Stop));
    public void RequestKeyframe() => Send(Msg.Simple(MessageTypes.RequestKeyframe));

    /// <summary>Sends a setControl and remembers its persistable fields for the active camera.</summary>
    public void SendControl(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var msg = Msg.SetControl(fields);
        _settings.RememberControls(_settings.Current.CameraId, msg);
        Send(msg);
    }

    /// <summary>Sends configure built from the current settings (validated against the capabilities).</summary>
    public void SendConfigure()
    {
        var s = _settings.Current;
        var caps = Capabilities;
        if (caps is not null)
        {
            var cam = caps.Cameras.FirstOrDefault(c => c.Id == s.CameraId) ?? caps.DefaultCamera();
            if (cam is not null)
            {
                s.CameraId = cam.Id;
                var (w, h, fps) = cam.PickFormat(s.Width, s.Height, s.Fps);
                s.Width = w; s.Height = h; s.Fps = fps;
            }
        }
        _settings.SaveSoon();
        Send(Msg.Configure(s.CameraId, s.Width, s.Height, s.Fps, s.Codec, s.BitrateKbps, s.Orientation, s.Mirror));
    }

    // ---- device watching -------------------------------------------------------------

    private async Task WatchDevicesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await UsbmuxClient.IsAvailableAsync(ct).ConfigureAwait(false))
                {
                    _serviceAvailable = false;
                    _devices.Clear();
                    UpdateIdleState();
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                _serviceAvailable = true;
                UpdateIdleState();
                await foreach (var ev in UsbmuxClient.ListenAsync(ct).ConfigureAwait(false))
                {
                    switch (ev.Kind)
                    {
                        case UsbmuxEventKind.Attached when ev.Device is not null:
                            _devices[ev.DeviceId] = ev.Device;
                            Log.Info($"iPhone anexado: {ev.Device.ConnectionType} {ev.Device.SerialNumber}");
                            break;
                        case UsbmuxEventKind.Detached:
                            _devices.TryRemove(ev.DeviceId, out _);
                            Log.Info($"Dispositivo removido: {ev.DeviceId}");
                            break;
                    }
                    UpdateIdleState();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Monitor usbmux: {ex.Message}");
                _devices.Clear();
                UpdateIdleState();
                try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void UpdateIdleState()
    {
        if (_inSession) return;
        if (_manual is not null) return;
        if (!_serviceAvailable)
            SetState(ConnectionState.AppleServiceMissing,
                "Instale o app 'Dispositivos Apple' da Microsoft Store ou o iTunes");
        else if (!_devices.Values.Any(d => d.IsUsb))
            SetState(ConnectionState.WaitingForDevice,
                "Conecte o iPhone por cabo, desbloqueie-o e toque em 'Confiar' se solicitado");
        else if (State is ConnectionState.AppleServiceMissing or ConnectionState.WaitingForDevice)
            SetState(ConnectionState.Connecting, "iPhone detectado");
    }

    private Target? ChooseTarget()
    {
        (string Host, int Port)? manual;
        lock (_gate) { manual = _manual; }
        if (manual is { } m) return new Target(null, m.Host, m.Port);

        var usb = _devices.Values.Where(d => d.IsUsb).OrderBy(d => d.DeviceId).ToList();
        if (usb.Count == 0) return null;
        var remembered = _settings.Current.LastDeviceSerial;
        var dev = usb.FirstOrDefault(d => d.SerialNumber == remembered) ?? usb[0];
        return new Target(dev, null, PhonePort);
    }

    // ---- session loop ----------------------------------------------------------------

    private async Task SessionLoopAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var target = ChooseTarget();
            if (target is null)
            {
                try { await Task.Delay(300, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate) { _sessionCts = sessionCts; }
            bool connected = false;
            string? failure = null;
            try
            {
                _inSession = true;
                connected = await RunSessionAsync(target, sessionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // dropped by us (manual switch/dispose) or lost
            }
            catch (UsbmuxException ex)
            {
                Log.Warn($"usbmux: {ex.Message}");
                failure = ex.Code == 3
                    ? "Abra o app CaboCam no iPhone e mantenha-o em primeiro plano"
                    : ex.Message;
            }
            catch (Exception ex)
            {
                Log.Warn($"Sessão encerrada: {ex.Message}");
                failure = target.Device is null ? $"Não foi possível conectar a {target.Describe}" : "Conexão com o iPhone perdida";
            }
            finally
            {
                lock (_gate) { _sessionCts = null; }
                _client = null;
                ResetSessionInfo();
                _inSession = false;
            }
            if (ct.IsCancellationRequested) break;

            attempt = connected ? 1 : attempt + 1;
            int delay = Math.Min(5000, 500 * (1 << Math.Min(attempt, 4)));
            if (ChooseTarget() is not null)
                SetState(ConnectionState.Connecting, (failure ?? "Reconectando…") + $" — nova tentativa em {delay / 1000.0:0.#} s");
            else
                UpdateIdleState();

            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> RunSessionAsync(Target target, CancellationToken ct)
    {
        SetState(ConnectionState.Connecting, $"Conectando a {target.Describe}…");
        SessionStarting?.Invoke();

        Stream stream;
        if (target.Device is { } dev)
        {
            _currentSerial = dev.SerialNumber;
            stream = await UsbmuxClient.ConnectAsync(dev.DeviceId, target.Port, ct).ConfigureAwait(false);
        }
        else
        {
            _currentSerial = null;
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(target.Host!, target.Port, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
            stream = tcp.GetStream();
        }

        using var client = new ProtocolClient(stream);
        using var local = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lt = local.Token;
        bool gotHello = false;
        bool autoStarted = false;

        client.HelloReceived += hello =>
        {
            gotHello = true;
            DeviceName = hello.DeviceName;
            DeviceModel = hello.Model;
            SystemVersion = hello.SystemVersion;
            if (_currentSerial is not null && _settings.Current.LastDeviceSerial != _currentSerial)
            {
                _settings.Current.LastDeviceSerial = _currentSerial;
                _settings.SaveSoon();
            }
            SetState(ConnectionState.Connected, "");
        };
        client.CapabilitiesReceived += caps =>
        {
            Capabilities = caps;
            CapabilitiesReceived?.Invoke(caps);
            if (!autoStarted)
            {
                autoStarted = true;
                _ = Task.Run(() => InitialSetupAsync(lt), lt);
            }
        };
        client.StateReceived += state =>
        {
            LastState = state;
            SetState(state.Streaming ? ConnectionState.Streaming : ConnectionState.Connected, "");
            DeviceStateReceived?.Invoke(state);
        };
        client.StreamStarted += _ => SetState(ConnectionState.Streaming, "");
        client.StreamStopped += () => SetState(ConnectionState.Connected, "");
        client.ErrorReceived += msg => { Log.Warn("Erro do iPhone: " + msg); PhoneError?.Invoke(msg); };
        client.PongReceived += (t, phoneUs) =>
        {
            long now = Environment.TickCount64;
            long rtt = Math.Max(0, now - t);
            LatencyMs = (int)rtt;
            if (phoneUs is { } pu) UpdateClockOffset(pu / 1000, t, rtt);
            StatusChanged?.Invoke();
        };
        client.VideoReceived += p => VideoReceived?.Invoke(p);

        _client = client;
        await client.SendHelloAsync(lt).ConfigureAwait(false);

        var readTask = client.RunReadLoopAsync(lt);
        var pingTask = Task.Run(async () =>
        {
            int ticks = 0;
            while (!lt.IsCancellationRequested)
            {
                await Task.Delay(PingIntervalMs, lt).ConfigureAwait(false);
                ticks++;
                if (Environment.TickCount64 - client.LastReceiveTick > RxTimeoutMs)
                    throw new IOException("Tempo esgotado aguardando o iPhone");
                if (Capabilities is null && ticks >= 2)
                    await client.SendAsync(Msg.Simple(MessageTypes.GetCapabilities), lt).ConfigureAwait(false);
                await client.SendAsync(Msg.Ping(Environment.TickCount64), lt).ConfigureAwait(false);
            }
        }, lt);

        try
        {
            var finished = await Task.WhenAny(readTask, pingTask).ConfigureAwait(false);
            await finished.ConfigureAwait(false); // propagate exceptions
        }
        finally
        {
            local.Cancel();
            try { await Task.WhenAll(readTask, pingTask).ConfigureAwait(false); } catch { /* already reported */ }
        }
        return gotHello;
    }

    private async Task InitialSetupAsync(CancellationToken ct)
    {
        try
        {
            SendConfigure();
            if (_settings.Current.AutoStart)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
                StartStream();
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
            ApplySavedControls();
            RequestKeyframe();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Configuração inicial", ex); }
    }

    private void ApplySavedControls()
    {
        var caps = Capabilities;
        var camId = _settings.Current.CameraId;
        var cam = caps?.Cameras.FirstOrDefault(c => c.Id == camId);
        var saved = _settings.SavedControls(camId);
        if (cam is null || saved is null || saved.Count == 0) return;

        var fields = new Dictionary<string, object?>();
        foreach (var (key, el) in saved)
        {
            switch (key)
            {
                case "zoom":
                    if (el.TryGetDouble(out var z)) fields[key] = Math.Clamp(z, cam.MinZoom, cam.MaxZoom);
                    break;
                case "exposureBias":
                    if (el.TryGetDouble(out var eb)) fields[key] = Math.Clamp(eb, cam.MinExposureBias, cam.MaxExposureBias);
                    break;
                case "lensPosition": case "iso": case "exposureDurationSec": case "temperature": case "tint":
                    if (el.TryGetDouble(out var d)) fields[key] = d;
                    break;
                case "hdr": case "lowLightBoost": case "centerStage":
                    if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) fields[key] = el.GetBoolean();
                    break;
                case "focusMode": case "exposureMode": case "whiteBalanceMode": case "stabilization":
                    if (el.ValueKind == JsonValueKind.String) fields[key] = el.GetString();
                    break;
            }
        }
        if (fields.TryGetValue("lowLightBoost", out _) && !cam.SupportsLowLightBoost) fields.Remove("lowLightBoost");
        if (fields.TryGetValue("centerStage", out _) && !(cam.IsFront && cam.CenterStage)) fields.Remove("centerStage");
        if (fields.Count == 0) return;
        Send(Msg.SetControl(fields));
    }

    private void ResetSessionInfo()
    {
        DeviceName = null;
        DeviceModel = null;
        SystemVersion = null;
        Capabilities = null;
        LastState = null;
        LatencyMs = -1;
        ResetClockOffset();
        StatusChanged?.Invoke();
    }

    private void DropSession()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _sessionCts; }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void SetState(ConnectionState state, string detail)
    {
        bool changed = State != state || StatusDetail != detail;
        State = state;
        StatusDetail = detail;
        if (changed) StatusChanged?.Invoke();
    }

    private static string ShortSerial(string serial) => serial.Length > 6 ? serial[^6..] : serial;

    public void Dispose()
    {
        _cts.Cancel();
        DropSession();
        _cts.Dispose();
    }
}
