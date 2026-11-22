using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CaboCam.Protocol;
using CaboCam.Services;
using CaboCam.Video;

namespace CaboCam.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Brush OkBrush = Frozen("#22C55E");
    private static readonly Brush WarnBrush = Frozen("#F59E0B");
    private static readonly Brush BadBrush = Frozen("#EF4444");
    private static readonly Brush IdleBrush = Frozen("#8A92A0");
    private static readonly Brush BusyBrush = Frozen("#3B82F6");

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private readonly Dispatcher _ui;
    private readonly SettingsStore _settings;
    private readonly VirtualCameraService _vcam;
    private readonly ConnectionManager _conn;
    private readonly VideoPipeline _video;
    private readonly DispatcherTimer _statsTimer;
    private readonly Dictionary<string, DispatcherTimer> _debouncers = new();
    private readonly Dictionary<string, long> _touched = new();
    private readonly HashSet<string> _dragging = new();

    private CapabilitiesMessage? _caps;
    private DeviceState? _lastState;
    private WriteableBitmap? _bitmap;
    private bool _syncing;
    private bool _disposed;
    private long _lastFramesDecoded;
    private long _lastFrameTick;

    public MainViewModel(SettingsStore settings, VirtualCameraService vcam)
    {
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _settings = settings;
        _vcam = vcam;

        var s = _settings.Current;
        _bitrateMbps = Math.Clamp(s.BitrateKbps / 1000.0, 2, 50);
        _codecKey = s.Codec;
        _orientationKey = s.Orientation;
        _mirror = s.Mirror;

        _conn = new ConnectionManager(settings);
        _video = new VideoPipeline();
        IosApp = new IosAppViewModel(settings);
        _video.PhoneClockOffsetMs = () => _conn.PhoneClockOffsetMs;

        ToggleStreamCommand = new RelayCommand(ToggleStream, () => IsConnected);
        ConnectByIpCommand = new RelayCommand(ConnectByIp);
        UseUsbCommand = new RelayCommand(() => { LastError = ""; _conn.UseUsb(); });
        InstallDriverCommand = new RelayCommand(InstallDriver, () => !_installingDriver);
        ZoomPresetCommand = new RelayCommand(p => { if (p is ZoomPreset zp) SetZoomRaw(zp.Raw, ramp: true); });

        _conn.StatusChanged += () => PostUi(RefreshConnection);
        _conn.CapabilitiesReceived += caps => PostUi(() => ApplyCapabilities(caps));
        _conn.DeviceStateReceived += st => PostUi(() => ApplyState(st));
        _conn.PhoneError += msg => PostUi(() => LastError = msg);
        _conn.SessionStarting += () => { _video.Reset(); PostUi(() => LastError = ""); };
        _conn.VideoReceived += _video.Enqueue;

        _video.KeyframeNeeded += () => _conn.RequestKeyframe();
        _video.Error += msg => PostUi(() => LastError = msg);
        _video.PreviewReady += OnPreviewReady;

        _vcam.Changed += () => PostUi(RefreshVcam);

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => RefreshStats();

        RebuildOptionLists();
        RefreshConnection();
        RefreshVcam();
    }

    public void Start()
    {
        _statsTimer.Start();
        _conn.Start();
        IosApp.Start();
    }

    /// <summary>"App do iPhone" card (sign + install the iOS app with an Apple ID).</summary>
    public IosAppViewModel IosApp { get; }

    private void PostUi(Action action)
    {
        if (_disposed) return;
        try { _ui.BeginInvoke(action); } catch { /* shutting down */ }
    }

    // =============================================================================
    // Commands
    // =============================================================================
    public RelayCommand ToggleStreamCommand { get; }
    public ICommand ConnectByIpCommand { get; }
    public ICommand UseUsbCommand { get; }
    public RelayCommand InstallDriverCommand { get; }
    public ICommand ZoomPresetCommand { get; }

    /// <summary>Set by the window: shows the "host:port" prompt, returns null if cancelled.</summary>
    public Func<string, string?>? AskHostPort { get; set; }

    private void ConnectByIp()
    {
        var answer = AskHostPort?.Invoke(_settings.Current.LastHost ?? "");
        if (string.IsNullOrWhiteSpace(answer)) return;
        try
        {
            LastError = "";
            _conn.ConnectManual(answer);
        }
        catch (FormatException ex)
        {
            LastError = "Endereço inválido: " + ex.Message;
        }
    }

    private void ToggleStream()
    {
        if (IsStreaming)
        {
            _conn.StopStream();
        }
        else
        {
            _conn.SendConfigure();
            _conn.StartStream();
            _conn.RequestKeyframe();
        }
    }

    // =============================================================================
    // Connection card
    // =============================================================================
    private string _statusText = "";
    private string _statusDetail = "";
    private Brush _statusBrush = IdleBrush;
    private string _deviceLine = "";
    private string _batteryText = "—";
    private string _thermalText = "—";
    private string _latencyText = "—";
    private string _lastError = "";
    private bool _isConnected;
    private bool _isStreaming;
    private bool _isManual;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string DeviceLine { get => _deviceLine; private set { Set(ref _deviceLine, value); OnPropertyChanged(nameof(HasDevice)); } }
    public bool HasDevice => _deviceLine.Length > 0;
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }
    public string ThermalText { get => _thermalText; private set => Set(ref _thermalText, value); }
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }
    public string LastError { get => _lastError; set { Set(ref _lastError, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _lastError.Length > 0;
    public bool IsManual { get => _isManual; private set => Set(ref _isManual, value); }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!Set(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(ControlsEnabled));
            ToggleStreamCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (!Set(ref _isStreaming, value)) return;
            OnPropertyChanged(nameof(StreamButtonText));
        }
    }

    public string StreamButtonText => IsStreaming ? "Parar" : "Iniciar";
    public bool ControlsEnabled => IsConnected && _caps is not null;

    public bool AutoStart
    {
        get => _settings.Current.AutoStart;
        set { _settings.Current.AutoStart = value; _settings.SaveSoon(); OnPropertyChanged(); }
    }

    public bool StartMinimized
    {
        get => _settings.Current.StartMinimized;
        set { _settings.Current.StartMinimized = value; _settings.SaveSoon(); OnPropertyChanged(); }
    }

    public bool CloseToTray
    {
        get => _settings.Current.CloseToTray;
        set { _settings.Current.CloseToTray = value; _settings.SaveSoon(); OnPropertyChanged(); }
    }

    private void RefreshConnection()
    {
        var st = _conn.State;
        (StatusText, StatusBrush) = st switch
        {
            ConnectionState.AppleServiceMissing => ("Serviço Apple não encontrado", BadBrush),
            ConnectionState.WaitingForDevice => ("Aguardando iPhone…", WarnBrush),
            ConnectionState.Connecting => ("Conectando…", BusyBrush),
            ConnectionState.Connected => ("Conectado", OkBrush),
            ConnectionState.Streaming => ("Transmitindo", OkBrush),
            _ => ("", IdleBrush),
        };
        StatusDetail = _conn.StatusDetail;
        IsManual = _conn.IsManual;
        IsConnected = st is ConnectionState.Connected or ConnectionState.Streaming;

        var name = _conn.DeviceName;
        var model = _conn.DeviceModel;
        DeviceLine = string.IsNullOrEmpty(name) ? "" : string.IsNullOrEmpty(model) ? name : $"{name} · {model}";
        LatencyText = _conn.LatencyMs >= 0 ? $"{_conn.LatencyMs} ms" : "—";

        if (!IsConnected)
        {
            // session over: reset device-dependent UI
            if (_caps is not null && st != ConnectionState.Connecting) { /* keep lists while reconnecting */ }
            IsStreaming = false;
            BatteryText = "—";
            ThermalText = "—";
            ClearPreview();
            if (st is ConnectionState.AppleServiceMissing or ConnectionState.WaitingForDevice)
            {
                _caps = null;
                _lastState = null;
                OnPropertyChanged(nameof(ControlsEnabled));
            }
        }
        else if (st == ConnectionState.Streaming)
        {
            IsStreaming = true;
        }
        else
        {
            IsStreaming = false;
        }
    }

    // =============================================================================
    // Virtual camera
    // =============================================================================
    private string _vcamText = "";
    private Brush _vcamBrush = IdleBrush;
    private bool _vcamInUse;

    public string VcamText { get => _vcamText; private set => Set(ref _vcamText, value); }
    public Brush VcamBrush { get => _vcamBrush; private set => Set(ref _vcamBrush, value); }
    public bool VcamInUse { get => _vcamInUse; private set => Set(ref _vcamInUse, value); }

    private void RefreshVcam()
    {
        VcamText = _vcam.Message;
        VcamBrush = _vcam.Status switch
        {
            VcamStatus.Active => OkBrush,
            VcamStatus.Unknown => IdleBrush,
            _ => BadBrush,
        };
        VcamInUse = _vcam.InUse;
        NeedsDriver = _vcam.Status is VcamStatus.NotInstalled or VcamStatus.NoAccess;
    }

    private bool _needsDriver;
    public bool NeedsDriver { get => _needsDriver; private set => Set(ref _needsDriver, value); }

    private bool _installingDriver;

    private async void InstallDriver()
    {
        if (_installingDriver) return;
        _installingDriver = true;
        InstallDriverCommand.RaiseCanExecuteChanged();
        try
        {
            VcamText = "Instalando o driver (confirme a permissão de administrador)…";
            var err = await System.Threading.Tasks.Task.Run(() => _vcam.InstallDriverAsync());
            RefreshVcam();
            if (err is not null) VcamText = err;
        }
        finally
        {
            _installingDriver = false;
            InstallDriverCommand.RaiseCanExecuteChanged();
        }
    }

    // =============================================================================
    // Preview
    // =============================================================================
    private ImageSource? _previewSource;
    private string _overlayText = "";

    public ImageSource? PreviewSource { get => _previewSource; private set { Set(ref _previewSource, value); OnPropertyChanged(nameof(HasPreview)); } }
    public bool HasPreview => _previewSource is not null;
    public string OverlayText { get => _overlayText; private set => Set(ref _overlayText, value); }

    private void OnPreviewReady(byte[] bgra, int w, int h)
    {
        try
        {
            _ui.BeginInvoke(DispatcherPriority.Render, () =>
            {
                try
                {
                    if (_disposed || _conn.State != ConnectionState.Streaming) return;
                    if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                    {
                        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                        PreviewSource = _bitmap;
                    }
                    _bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
                }
                finally
                {
                    _video.AckPreview();
                }
            });
        }
        catch
        {
            _video.AckPreview();
        }
    }

    private void ClearPreview()
    {
        _bitmap = null;
        if (_previewSource is not null) PreviewSource = null;
    }

    private void RefreshStats()
    {
        long frames = _video.FramesDecoded;
        long now = Environment.TickCount64;
        if (frames != _lastFramesDecoded) { _lastFramesDecoded = frames; _lastFrameTick = now; }
        double fps = now - _lastFrameTick > 1500 ? 0 : _video.DecodedFps;

        var st = _lastState;
        double mbps = (st?.Stats?.BitrateKbps ?? st?.BitrateKbps ?? 0) / 1000.0;
        string codec = string.Equals(st?.Codec, "hevc", StringComparison.OrdinalIgnoreCase) ? "HEVC" : "H.264";
        OverlayText = IsStreaming && _video.FrameWidth > 0
            ? $"{fps:0.0} fps · {mbps:0.0} Mbps · {_video.FrameWidth}×{_video.FrameHeight} · {codec} · decodificação {_video.AvgDecodeMs:0} ms"
              + (_video.CaptureToDecodeMs >= 0 ? $" · atraso câmera→PC {_video.CaptureToDecodeMs} ms" : "")
              + (_video.LatencyDrops > 0 ? $" · {_video.LatencyDrops} descartes p/ atraso" : "")
            : "";

        LatencyText = _conn.LatencyMs >= 0 ? $"{_conn.LatencyMs} ms" : "—";
        VcamInUse = _vcam.InUse;
    }

    /// <summary>Tap on the preview: x,y normalized (0,0 top-left) in output-frame coordinates.</summary>
    public void SetPointOfInterest(double x, double y)
    {
        var cam = CurrentCamera;
        if (cam is null || !cam.SupportsPointOfInterest) return;
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        _conn.SendControl(new Dictionary<string, object?>
        {
            ["pointOfInterest"] = new Dictionary<string, double> { ["x"] = x, ["y"] = y },
        });
    }

    // =============================================================================
    // Slider drag tracking / helpers
    // =============================================================================
    public void BeginDrag(string key) { _dragging.Add(key); Touch(key); }
    public void EndDrag(string key) { _dragging.Remove(key); Touch(key); }

    private void Touch(string key) => _touched[key] = Environment.TickCount64;

    private bool Busy(string key) =>
        _dragging.Contains(key) || (_touched.TryGetValue(key, out var t) && Environment.TickCount64 - t < 1500);

    private readonly Dictionary<string, Action> _pending = new();

    private void Debounce(string key, int ms, Action action)
    {
        _pending[key] = action;
        if (!_debouncers.TryGetValue(key, out var timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Normal, _ui);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_pending.TryGetValue(key, out var run)) run();
            };
            _debouncers[key] = timer;
        }
        timer.Interval = TimeSpan.FromMilliseconds(ms);
        timer.Stop();
        timer.Start();
    }

    private void Control(params (string Key, object? Value)[] fields) =>
        _conn.SendControl(fields.Select(f => new KeyValuePair<string, object?>(f.Key, f.Value)));

    private void DebouncedControl(string key, Func<(string, object?)[]> fields) =>
        Debounce(key, 50, () => Control(fields()));

    private void QueueConfigure() => Debounce("configure", 250, () => _conn.SendConfigure());

    private static double LogValue(double pos, double min, double max)
    {
        if (min <= 0 || max <= min) return min;
        return min * Math.Pow(max / min, Math.Clamp(pos, 0, 1));
    }

    private static double LogPos(double value, double min, double max)
    {
        if (min <= 0 || max <= min) return 0;
        return Math.Clamp(Math.Log(Math.Max(value, min) / min) / Math.Log(max / min), 0, 1);
    }

    // =============================================================================
    // Capabilities / camera card
    // =============================================================================
    public ObservableCollection<CameraItem> Cameras { get; } = new();
    public ObservableCollection<FormatItem> Formats { get; } = new();
    public ObservableCollection<Option> Codecs { get; } = new();
    public ObservableCollection<Option> Orientations { get; } = new();

    private string? _cameraId;
    private string? _formatKey;
    private string _codecKey;
    private string _orientationKey;
    private bool _mirror;
    private double _bitrateMbps;

    public CameraInfo? CurrentCamera => _caps?.Cameras.FirstOrDefault(c => c.Id == _cameraId);

    public string? CameraId
    {
        get => _cameraId;
        set
        {
            if (value is null || !Set(ref _cameraId, value)) return;
            _settings.Current.CameraId = value;
            RebuildFormats();
            ApplyCameraCaps();
            if (_syncing) return;
            Touch("camera");
            QueueConfigure();
            // controls saved for the new camera are restored by the phone state; nothing to send here
        }
    }

    public string? FormatKey
    {
        get => _formatKey;
        set
        {
            if (value is null || !Set(ref _formatKey, value)) return;
            var f = Formats.FirstOrDefault(x => x.Key == value);
            if (f is null) return;
            _settings.Current.Width = f.Width;
            _settings.Current.Height = f.Height;
            _settings.Current.Fps = f.Fps;
            if (_syncing) return;
            Touch("format");
            QueueConfigure();
        }
    }

    public string CodecKey
    {
        get => _codecKey;
        set
        {
            if (value is null || !Set(ref _codecKey, value)) return;
            _settings.Current.Codec = value;
            if (_syncing) return;
            Touch("codec");
            QueueConfigure();
        }
    }

    public string OrientationKey
    {
        get => _orientationKey;
        set
        {
            if (value is null || !Set(ref _orientationKey, value)) return;
            _settings.Current.Orientation = value;
            if (_syncing) return;
            Touch("orient");
            QueueConfigure();
        }
    }

    public bool Mirror
    {
        get => _mirror;
        set
        {
            if (!Set(ref _mirror, value)) return;
            _settings.Current.Mirror = value;
            if (_syncing) return;
            Touch("mirror");
            QueueConfigure();
        }
    }

    public double BitrateMbps
    {
        get => _bitrateMbps;
        set
        {
            value = Math.Round(value * 2) / 2; // 0.5 Mbps steps
            if (!Set(ref _bitrateMbps, value)) return;
            OnPropertyChanged(nameof(BitrateLabel));
            _settings.Current.BitrateKbps = (int)Math.Round(value * 1000);
            if (_syncing) return;
            Touch("bitrate");
            QueueConfigure();
        }
    }

    public string BitrateLabel => $"{_bitrateMbps.ToString("0.#", Inv)} Mbps";

    private void RebuildOptionLists()
    {
        Codecs.Clear();
        Codecs.Add(new Option("h264", "H.264"));
        Codecs.Add(new Option("hevc", "HEVC (H.265)"));
        Orientations.Clear();
        Orientations.Add(new Option("landscape", "Paisagem"));
        Orientations.Add(new Option("portrait", "Retrato"));
        StabilizationOptions.Clear();
        foreach (var o in new[]
                 {
                     new Option("auto", "Automática (baixo atraso)"), new Option("off", "Desligada (menor atraso)"),
                     new Option("standard", "Padrão"),
                     new Option("cinematic", "Cinema (atraso ~1 s)"), new Option("cinematicExtended", "Cinema estendido (atraso ~1,5 s)"),
                 })
            StabilizationOptions.Add(o);
        FocusOptions.Add(new Option("continuous", "Contínuo"));
        ExposureOptions.Add(new Option("continuous", "Automática"));
        WbOptions.Add(new Option("continuous", "Automático"));
    }

    private void ApplyCapabilities(CapabilitiesMessage caps)
    {
        _caps = caps;
        _syncing = true;
        try
        {
            Cameras.Clear();
            foreach (var c in caps.Cameras) Cameras.Add(new CameraItem(c.Id, c.Label(caps.Cameras)));

            var wanted = _settings.Current.CameraId;
            var cam = caps.Cameras.FirstOrDefault(c => c.Id == wanted) ?? caps.DefaultCamera();
            _cameraId = null;
            CameraId = cam?.Id;
            OnPropertyChanged(nameof(CameraId));
        }
        finally
        {
            _syncing = false;
        }
        OnPropertyChanged(nameof(ControlsEnabled));
    }

    private void RebuildFormats()
    {
        Formats.Clear();
        var cam = CurrentCamera;
        if (cam is null) return;

        foreach (var (w, h) in cam.Resolutions())
            foreach (var fps in cam.FpsOptions(w, h))
                Formats.Add(new FormatItem(w, h, fps));

        var s = _settings.Current;
        var (pw, ph, pfps) = cam.PickFormat(s.Width, s.Height, s.Fps);
        SelectFormat(pw, ph, pfps);
    }

    private void SelectFormat(int w, int h, double fps)
    {
        if (Formats.Count == 0) return;
        var best = Formats
            .OrderBy(f => f.Width == w && f.Height == h ? 0 : 1)
            .ThenBy(f => Math.Abs((long)f.Width * f.Height - (long)w * h))
            .ThenBy(f => Math.Abs(f.Fps - fps))
            .First();
        _formatKey = null; // force change notification
        FormatKey = best.Key;
        OnPropertyChanged(nameof(FormatKey));
    }

    // =============================================================================
    // Adjustments card: capabilities-dependent state
    // =============================================================================
    public ObservableCollection<ZoomPreset> ZoomPresets { get; } = new();
    public ObservableCollection<Option> FocusOptions { get; } = new();
    public ObservableCollection<Option> ExposureOptions { get; } = new();
    public ObservableCollection<Option> WbOptions { get; } = new();
    public ObservableCollection<Option> StabilizationOptions { get; } = new();

    private double _minZoom = 1, _maxZoom = 1, _zoomMult = 1;
    private double _biasMin = -2, _biasMax = 2;
    private bool _hasTorch, _lowLightSupported, _centerStageSupported, _hdrSupported;

    public double BiasMin { get => _biasMin; private set => Set(ref _biasMin, value); }
    public double BiasMax { get => _biasMax; private set => Set(ref _biasMax, value); }
    public bool ZoomEnabled => _maxZoom > _minZoom + 0.001;
    public bool TorchEnabled => _hasTorch;
    public bool HdrEnabled => _hdrSupported;
    public bool LowLightVisible => _lowLightSupported;
    public bool CenterStageVisible => _centerStageSupported;

    private void ApplyCameraCaps()
    {
        var cam = CurrentCamera;
        if (cam is null) return;

        _minZoom = Math.Max(0.01, cam.MinZoom);
        _maxZoom = Math.Max(_minZoom, cam.MaxZoom);
        _zoomMult = cam.ZoomDisplayMultiplier <= 0 ? 1 : cam.ZoomDisplayMultiplier;
        if (_zoom < _minZoom || _zoom > _maxZoom) _zoom = Math.Clamp(_zoom, _minZoom, _maxZoom);

        ZoomPresets.Clear();
        var raws = new List<double> { _minZoom };
        double oneX = 1.0 / _zoomMult;
        if (oneX > _minZoom && oneX <= _maxZoom) raws.Add(oneX);
        raws.AddRange(cam.ZoomSwitchOver.Where(z => z > _minZoom && z <= _maxZoom));
        foreach (var r in raws.OrderBy(x => x))
        {
            if (ZoomPresets.Any(p => Math.Abs(p.Raw - r) < 0.01)) continue;
            ZoomPresets.Add(new ZoomPreset(r, CapabilityHelpers.FormatZoom(r * _zoomMult) + "×"));
        }

        FocusOptions.Clear();
        var modes = cam.FocusModes.Count > 0 ? cam.FocusModes : new List<string> { "continuous" };
        if (modes.Contains("continuous")) FocusOptions.Add(new Option("continuous", "Contínuo"));
        if (modes.Contains("auto")) FocusOptions.Add(new Option("auto", "Automático"));
        if (modes.Contains("locked")) FocusOptions.Add(new Option("locked", "Travado"));
        if (FocusOptions.Count == 0) FocusOptions.Add(new Option("continuous", "Contínuo"));

        ExposureOptions.Clear();
        ExposureOptions.Add(new Option("continuous", "Automática"));
        ExposureOptions.Add(new Option("locked", "Travada"));
        if (cam.SupportsCustomExposure) ExposureOptions.Add(new Option("custom", "Manual"));

        WbOptions.Clear();
        WbOptions.Add(new Option("continuous", "Automático"));
        WbOptions.Add(new Option("locked", "Travado"));
        if (cam.SupportsCustomWhiteBalance) WbOptions.Add(new Option("custom", "Manual"));

        BiasMin = cam.MinExposureBias;
        BiasMax = Math.Max(cam.MinExposureBias + 0.1, cam.MaxExposureBias);
        _hasTorch = cam.HasTorch;
        _lowLightSupported = cam.SupportsLowLightBoost;
        _centerStageSupported = cam.IsFront && cam.CenterStage;

        // Re-select current values after the item lists were rebuilt.
        OnPropertyChanged(nameof(FocusMode));
        OnPropertyChanged(nameof(ExposureMode));
        OnPropertyChanged(nameof(WhiteBalanceMode));
        OnPropertyChanged(nameof(ZoomPos));
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(ZoomEnabled));
        OnPropertyChanged(nameof(TorchEnabled));
        OnPropertyChanged(nameof(HdrEnabled));
        OnPropertyChanged(nameof(LowLightVisible));
        OnPropertyChanged(nameof(CenterStageVisible));
        OnPropertyChanged(nameof(ControlsEnabled));
    }

    // ---- Zoom -------------------------------------------------------------------------
    private double _zoom = 1;

    public double ZoomPos
    {
        get => LogPos(_zoom, _minZoom, _maxZoom);
        set
        {
            double z = LogValue(value, _minZoom, _maxZoom);
            if (Math.Abs(z - _zoom) < 1e-6) return;
            _zoom = z;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomLabel));
            if (_syncing) return;
            Touch("zoom");
            DebouncedControl("zoom", () => new (string, object?)[] { ("zoom", Math.Round(_zoom, 3)), ("rampZoom", false) });
        }
    }

    public string ZoomLabel => CapabilityHelpers.FormatZoom(_zoom * _zoomMult) + "×";

    private void SetZoomRaw(double raw, bool ramp)
    {
        _zoom = Math.Clamp(raw, _minZoom, _maxZoom);
        OnPropertyChanged(nameof(ZoomPos));
        OnPropertyChanged(nameof(ZoomLabel));
        Touch("zoom");
        Control(("zoom", Math.Round(_zoom, 3)), ("rampZoom", ramp));
    }

    // ---- Focus ------------------------------------------------------------------------
    private string _focusMode = "continuous";
    private double _lensPosition = 0.5;

    public string? FocusMode
    {
        get => _focusMode;
        set
        {
            if (value is null || !Set(ref _focusMode, value)) return;
            OnPropertyChanged(nameof(LensEnabled));
            if (_syncing) return;
            Touch("focus");
            Control(("focusMode", value));
        }
    }

    public bool LensEnabled => _focusMode == "locked";

    public double LensPosition
    {
        get => _lensPosition;
        set
        {
            if (!Set(ref _lensPosition, value)) return;
            if (_syncing) return;
            Touch("lens");
            Touch("focus");
            DebouncedControl("lens", () => new (string, object?)[] { ("lensPosition", Math.Round(_lensPosition, 3)) });
        }
    }

    // ---- Exposure ---------------------------------------------------------------------
    private string _exposureMode = "continuous";
    private double _iso = 100, _minIso = 32, _maxIso = 3200;
    private double _shutter = 1.0 / 60, _minShutter = 1.0 / 8000, _maxShutter = 1.0 / 30;
    private double _bias;

    public string? ExposureMode
    {
        get => _exposureMode;
        set
        {
            if (value is null || !Set(ref _exposureMode, value)) return;
            OnPropertyChanged(nameof(ManualExposureEnabled));
            if (_syncing) return;
            Touch("exposure");
            if (value == "custom") SendManualExposure();
            else Control(("exposureMode", value));
        }
    }

    public bool ManualExposureEnabled => _exposureMode == "custom";

    public double IsoPos
    {
        get => LogPos(_iso, _minIso, _maxIso);
        set
        {
            double v = LogValue(value, _minIso, _maxIso);
            if (Math.Abs(v - _iso) < 1e-6) return;
            _iso = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsoLabel));
            if (_syncing) return;
            Touch("exposure");
            Debounce("exposure", 50, SendManualExposure);
        }
    }

    public double ShutterPos
    {
        // slider left = fast (short) shutter, right = slow
        get => LogPos(_shutter, _minShutter, _maxShutter);
        set
        {
            double v = LogValue(value, _minShutter, _maxShutter);
            if (Math.Abs(v - _shutter) < 1e-9) return;
            _shutter = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShutterLabel));
            if (_syncing) return;
            Touch("exposure");
            Debounce("exposure", 50, SendManualExposure);
        }
    }

    public string IsoLabel => $"ISO {Math.Round(_iso).ToString(Inv)}";

    public string ShutterLabel => _shutter >= 0.5
        ? $"{_shutter.ToString("0.#", Inv)} s"
        : $"1/{Math.Round(1.0 / Math.Max(_shutter, 1e-6)).ToString(Inv)} s";

    private void SendManualExposure() =>
        Control(("exposureMode", "custom"), ("iso", Math.Round(_iso)), ("exposureDurationSec", _shutter));

    public double ExposureBias
    {
        get => _bias;
        set
        {
            value = Math.Round(value * 10) / 10;
            if (!Set(ref _bias, value)) return;
            OnPropertyChanged(nameof(BiasLabel));
            if (_syncing) return;
            Touch("bias");
            DebouncedControl("bias", () => new (string, object?)[] { ("exposureBias", _bias) });
        }
    }

    public string BiasLabel => $"EV {(_bias >= 0 ? "+" : "")}{_bias.ToString("0.0", Inv)}";

    // ---- White balance ----------------------------------------------------------------
    private string _wbMode = "continuous";
    private double _temperature = 5000;
    private double _tint;

    public string? WhiteBalanceMode
    {
        get => _wbMode;
        set
        {
            if (value is null || !Set(ref _wbMode, value)) return;
            OnPropertyChanged(nameof(ManualWbEnabled));
            if (_syncing) return;
            Touch("wb");
            if (value == "custom") SendManualWb();
            else Control(("whiteBalanceMode", value));
        }
    }

    public bool ManualWbEnabled => _wbMode == "custom";

    public double Temperature
    {
        get => _temperature;
        set
        {
            value = Math.Round(value / 50) * 50;
            if (!Set(ref _temperature, value)) return;
            OnPropertyChanged(nameof(TemperatureLabel));
            if (_syncing) return;
            Touch("wb");
            Debounce("wb", 50, SendManualWb);
        }
    }

    public double Tint
    {
        get => _tint;
        set
        {
            value = Math.Round(value);
            if (!Set(ref _tint, value)) return;
            OnPropertyChanged(nameof(TintLabel));
            if (_syncing) return;
            Touch("wb");
            Debounce("wb", 50, SendManualWb);
        }
    }

    public string TemperatureLabel => $"{Math.Round(_temperature).ToString(Inv)} K";
    public string TintLabel => $"Matiz {(_tint >= 0 ? "+" : "")}{Math.Round(_tint).ToString(Inv)}";

    private void SendManualWb() =>
        Control(("whiteBalanceMode", "custom"), ("temperature", _temperature), ("tint", _tint));

    // ---- Torch, stabilization, toggles ------------------------------------------------
    private double _torch;
    private string _stabilization = "auto";
    private bool _hdr, _lowLight, _centerStage, _dimScreen;

    public double Torch
    {
        get => _torch;
        set
        {
            value = Math.Round(value * 20) / 20;
            if (!Set(ref _torch, value)) return;
            OnPropertyChanged(nameof(TorchLabel));
            if (_syncing) return;
            Touch("torch");
            DebouncedControl("torch", () => new (string, object?)[] { ("torch", _torch) });
        }
    }

    public string TorchLabel => _torch <= 0 ? "Desligada" : $"{Math.Round(_torch * 100).ToString(Inv)} %";

    public string? Stabilization
    {
        get => _stabilization;
        set
        {
            if (value is null || !Set(ref _stabilization, value)) return;
            if (_syncing) return;
            Touch("stab");
            Control(("stabilization", value));
        }
    }

    public bool Hdr
    {
        get => _hdr;
        set { if (Set(ref _hdr, value) && !_syncing) { Touch("hdr"); Control(("hdr", value)); } }
    }

    public bool LowLightBoost
    {
        get => _lowLight;
        set { if (Set(ref _lowLight, value) && !_syncing) { Touch("lowlight"); Control(("lowLightBoost", value)); } }
    }

    public bool CenterStage
    {
        get => _centerStage;
        set { if (Set(ref _centerStage, value) && !_syncing) { Touch("center"); Control(("centerStage", value)); } }
    }

    public bool DimScreen
    {
        get => _dimScreen;
        set { if (Set(ref _dimScreen, value) && !_syncing) { Touch("dim"); Control(("dimScreen", value)); } }
    }

    // =============================================================================
    // Incoming phone state
    // =============================================================================
    private void ApplyState(DeviceState s)
    {
        if (_caps is null) return; // wait for capabilities
        _lastState = s;
        _syncing = true;
        try
        {
            IsStreaming = s.Streaming;
            BatteryText = s.BatteryLevel >= 0
                ? $"{Math.Round(s.BatteryLevel * 100).ToString(Inv)} %{(s.Charging ? " · carregando" : "")}"
                : "—";
            ThermalText = s.ThermalState switch
            {
                "nominal" => "Normal",
                "fair" => "Moderada",
                "serious" => "Alta (fps reduzido)",
                "critical" => "Crítica",
                null or "" => "—",
                var other => other,
            };

            if (!Busy("camera") && s.CameraId is { Length: > 0 } cid && Cameras.Any(c => c.Id == cid) && cid != _cameraId)
                CameraId = cid;
            if (!Busy("format") && s.Width > 0 && s.Height > 0) SelectFormat(s.Width, s.Height, s.Fps);
            if (!Busy("codec") && s.Codec is { Length: > 0 } codec && Codecs.Any(c => c.Key == codec)) CodecKey = codec;
            if (!Busy("bitrate") && s.BitrateKbps > 0) BitrateMbps = Math.Clamp(s.BitrateKbps / 1000.0, 2, 50);
            if (!Busy("orient") && s.Orientation is { Length: > 0 } ori) OrientationKey = ori;
            if (!Busy("mirror")) Mirror = s.Mirror;

            // ranges of the active format
            if (s.MaxISO > s.MinISO && s.MinISO > 0) { _minIso = s.MinISO; _maxIso = s.MaxISO; }
            if (s.MaxExposureDurationSec > s.MinExposureDurationSec && s.MinExposureDurationSec > 0)
            {
                _minShutter = s.MinExposureDurationSec;
                _maxShutter = s.MaxExposureDurationSec;
            }
            _hdrSupported = s.HdrSupported;

            if (!Busy("zoom") && s.Zoom > 0) { _zoom = Math.Clamp(s.Zoom, _minZoom, _maxZoom); OnPropertyChanged(nameof(ZoomPos)); OnPropertyChanged(nameof(ZoomLabel)); }

            if (!Busy("focus") && !string.IsNullOrEmpty(s.FocusMode) && FocusOptions.Any(o => o.Key == s.FocusMode)) FocusMode = s.FocusMode;
            if (!Busy("lens")) LensPosition = s.LensPosition;

            if (!Busy("exposure"))
            {
                if (!string.IsNullOrEmpty(s.ExposureMode) && ExposureOptions.Any(o => o.Key == s.ExposureMode)) ExposureMode = s.ExposureMode;
                if (s.Iso > 0) _iso = Math.Clamp(s.Iso, _minIso, _maxIso);
                if (s.ExposureDurationSec > 0) _shutter = Math.Clamp(s.ExposureDurationSec, _minShutter, _maxShutter);
            }
            OnPropertyChanged(nameof(IsoPos));
            OnPropertyChanged(nameof(IsoLabel));
            OnPropertyChanged(nameof(ShutterPos));
            OnPropertyChanged(nameof(ShutterLabel));

            if (!Busy("bias")) ExposureBias = Math.Clamp(s.ExposureBias, _biasMin, _biasMax);

            if (!Busy("wb"))
            {
                if (!string.IsNullOrEmpty(s.WhiteBalanceMode) && WbOptions.Any(o => o.Key == s.WhiteBalanceMode)) WhiteBalanceMode = s.WhiteBalanceMode;
                if (s.Temperature > 0) Temperature = Math.Clamp(s.Temperature, 2500, 8000);
                Tint = Math.Clamp(s.Tint, -150, 150);
            }

            if (!Busy("torch")) Torch = s.Torch;
            if (!Busy("stab") && !string.IsNullOrEmpty(s.Stabilization) && StabilizationOptions.Any(o => o.Key == s.Stabilization)) Stabilization = s.Stabilization;
            if (!Busy("hdr")) Hdr = s.Hdr;
            if (!Busy("lowlight")) LowLightBoost = s.LowLightBoost;
            if (!Busy("center")) CenterStage = s.CenterStage;
            if (!Busy("dim")) DimScreen = s.DimScreen;

            OnPropertyChanged(nameof(HdrEnabled));
            OnPropertyChanged(nameof(ZoomEnabled));
            OnPropertyChanged(nameof(TorchEnabled));
            OnPropertyChanged(nameof(LensEnabled));
            OnPropertyChanged(nameof(ManualExposureEnabled));
            OnPropertyChanged(nameof(ManualWbEnabled));
        }
        finally
        {
            _syncing = false;
        }
        RefreshStats();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statsTimer.Stop();
        foreach (var t in _debouncers.Values) t.Stop();
        _conn.Dispose();
        _video.Dispose();
        IosApp.Dispose();
    }
}
