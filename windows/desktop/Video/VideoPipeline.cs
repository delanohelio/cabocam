using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using CaboCam.Native;
using CaboCam.Protocol;
using CaboCam.Services;

namespace CaboCam.Video;

/// <summary>
/// Decodes VIDEO packets on a dedicated thread, feeds the virtual-camera sink and produces
/// throttled BGRA preview frames.
/// </summary>
public sealed unsafe class VideoPipeline : IDisposable
{
    private const int QueueCapacity = 90;
    /// <summary>~200 ms at 30 fps (~100 ms at 60 fps): more than this queued means we are behind.</summary>
    private const int MaxQueuedFrames = 6;

    private long _latencyDrops;
    private long _lastDropLog;
    private double _decodeMsSum;
    private int _decodeMsCount;
    private long _decodeWindowStart = Environment.TickCount64;
    private bool _dumpedFailure;
    private VideoPacket? _lastKeyframe;
    private const int PreviewMaxWidth = 960;
    private const long PreviewIntervalMs = 33;       // ~30 fps
    private const long SinkRetryMs = 500;
    private const long KeyframeRequestIntervalMs = 1000;

    private readonly BlockingCollection<VideoPacket> _queue = new(new ConcurrentQueue<VideoPacket>(), QueueCapacity);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    private nint _decoder;
    private int _decoderCodec;
    private volatile bool _waitKeyframe = true;
    private volatile bool _resetRequested;
    private bool _nativeBroken;

    private byte[] _nv12 = new byte[1920 * 1080 * 3 / 2];
    private byte[] _bgra = Array.Empty<byte>();

    private bool _sinkOpen;
    private long _lastSinkAttempt;
    private long _lastKeyframeRequest;
    private long _lastPreview;
    private int _previewPending;

    private long _fpsWindowStart = Stopwatch.GetTimestamp();
    private int _fpsFrames;
    private long _framesDecoded;

    public VideoPipeline()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "CaboCam.Video", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Raised (decoder thread) when a keyframe should be requested from the phone. Rate-limited.</summary>
    public event Action? KeyframeNeeded;
    /// <summary>Raised (decoder thread) with a human-readable error. Not rate-limited by the pipeline.</summary>
    public event Action<string>? Error;
    /// <summary>Raised (decoder thread) with a BGRA frame. The consumer must call <see cref="AckPreview"/> when done with the buffer.</summary>
    public event Action<byte[], int, int>? PreviewReady;

    public volatile int FrameWidth;
    public volatile int FrameHeight;
    public bool SinkOpen => _sinkOpen;
    public double DecodedFps { get; private set; }
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);

    public void AckPreview() => Volatile.Write(ref _previewPending, 0);

    /// <summary>
    /// Queues a packet; never blocks. Live video must not build up delay: when decoding falls
    /// behind by more than <see cref="MaxQueuedFrames"/> the backlog is dropped and decoding
    /// resumes at the next keyframe (which is requested from the phone).
    /// </summary>
    public void Enqueue(VideoPacket packet)
    {
        if (_queue.IsAddingCompleted) return;

        if (_queue.Count >= MaxQueuedFrames)
        {
            while (_queue.TryTake(out _)) { }
            _waitKeyframe = true;
            Interlocked.Increment(ref _latencyDrops);
            long now = Environment.TickCount64;
            if (now - _lastDropLog > 5000)
            {
                _lastDropLog = now;
                Log.Warn($"Vídeo atrasado: fila descartada (decodificação {AvgDecodeMs:0.0} ms/quadro, {Interlocked.Read(ref _latencyDrops)} descartes)");
            }
            RequestKeyframe();
            if (!packet.IsKeyframe) return;
        }

        if (!_queue.TryAdd(packet))
        {
            while (_queue.TryTake(out _)) { }
            _waitKeyframe = true;
            RequestKeyframe();
        }
    }

    /// <summary>Times the latency guard dropped queued video.</summary>
    public long LatencyDrops => Interlocked.Read(ref _latencyDrops);
    /// <summary>Average push+pull time per access unit (ms), last ~second.</summary>
    public double AvgDecodeMs { get; private set; }

    /// <summary>Drops queued data and forces decoder re-creation at the next keyframe (new connection).</summary>
    public void Reset()
    {
        while (_queue.TryTake(out _)) { }
        _resetRequested = true;
        _waitKeyframe = true;
    }

    private void RequestKeyframe()
    {
        long now = Environment.TickCount64;
        if (now - _lastKeyframeRequest < KeyframeRequestIntervalMs) return;
        _lastKeyframeRequest = now;
        KeyframeNeeded?.Invoke();
    }

    private void Run()
    {
        try
        {
            foreach (var packet in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    Process(packet);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    if (!_nativeBroken)
                    {
                        _nativeBroken = true;
                        Log.Error("Biblioteca nativa indisponível", ex);
                        Error?.Invoke("CaboCam.Native.dll não encontrada ou incompatível");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Erro no pipeline de vídeo", ex);
                    DestroyDecoder();
                    _waitKeyframe = true;
                    RequestKeyframe();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            DestroyDecoder();
        }
    }

    private void Process(VideoPacket packet)
    {
        if (_nativeBroken) return;

        if (_resetRequested)
        {
            _resetRequested = false;
            DestroyDecoder();
        }

        int codec = packet.IsHevc ? NativeMethods.CodecHevc : NativeMethods.CodecH264;
        if (_decoder != 0 && codec != _decoderCodec)
        {
            DestroyDecoder();
            _waitKeyframe = true;
        }

        if (_waitKeyframe)
        {
            if (!packet.IsKeyframe)
            {
                RequestKeyframe();
                return;
            }
        }

        if (_decoder == 0)
        {
            _decoder = NativeMethods.cc_decoder_create(codec);
            if (_decoder == 0)
            {
                Error?.Invoke(codec == NativeMethods.CodecHevc
                    ? "Não foi possível criar o decodificador HEVC (instale a extensão 'Extensões de Vídeo HEVC' da Microsoft Store)"
                    : "Não foi possível criar o decodificador H.264");
                _waitKeyframe = true;
                return;
            }
            _decoderCodec = codec;
        }
        _waitKeyframe = false;
        if (packet.IsKeyframe) _lastKeyframe = packet;

        long t0 = Stopwatch.GetTimestamp();
        int rc;
        fixed (byte* p = packet.Payload)
        {
            rc = NativeMethods.cc_decoder_push(_decoder, p + packet.DataOffset, packet.DataLength, packet.PtsUs * 10);
        }
        if (rc < 0)
        {
            Log.Warn($"cc_decoder_push falhou: 0x{rc:X8} ({FrameWidth}x{FrameHeight}, {packet.DataLength} bytes, key={packet.IsKeyframe}, hevc={packet.IsHevc})");
            DumpFailure(packet);
            DestroyDecoder();
            _waitKeyframe = true;
            RequestKeyframe();
            return;
        }

        PullFrames();
        TrackDecodeTime(t0);
    }

    private void TrackDecodeTime(long startTimestamp)
    {
        _decodeMsSum += (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        _decodeMsCount++;
        long now = Environment.TickCount64;
        if (now - _decodeWindowStart >= 1000 && _decodeMsCount > 0)
        {
            AvgDecodeMs = _decodeMsSum / _decodeMsCount;
            _decodeMsSum = 0;
            _decodeMsCount = 0;
            _decodeWindowStart = now;
        }
    }

    /// <summary>
    /// Saves the last keyframe and the failing access unit (Annex-B) once per run, so a decoder
    /// failure can be reproduced offline: %LocalAppData%\CaboCam\logs\decoder-failure-*.h264
    /// </summary>
    private void DumpFailure(VideoPacket failing)
    {
        if (_dumpedFailure) return;
        _dumpedFailure = true;
        try
        {
            var path = System.IO.Path.Combine(Log.Directory, $"decoder-failure-{DateTime.Now:yyyyMMdd-HHmmss}.{(failing.IsHevc ? "h265" : "h264")}");
            using var fs = System.IO.File.Create(path);
            if (_lastKeyframe is { } key && !ReferenceEquals(key, failing))
                fs.Write(key.Payload, key.DataOffset, key.DataLength);
            fs.Write(failing.Payload, failing.DataOffset, failing.DataLength);
            Log.Info($"Amostra do vídeo que falhou salva em {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar amostra do decodificador", ex);
        }
    }

    private void PullFrames()
    {
        while (_decoder != 0)
        {
            int w, h;
            long pts;
            int r;
            fixed (byte* buf = _nv12)
            {
                r = NativeMethods.cc_decoder_pull(_decoder, buf, _nv12.Length, &w, &h, &pts);
            }

            if (r == -2)
            {
                if (w <= 0 || h <= 0 || (long)w * h * 3 / 2 > 3840L * 2160 * 3 / 2 * 2)
                {
                    DestroyDecoder();
                    _waitKeyframe = true;
                    RequestKeyframe();
                    return;
                }
                _nv12 = new byte[w * h * 3 / 2];
                continue;
            }
            if (r == 0) return;
            if (r < 0)
            {
                Log.Warn($"cc_decoder_pull falhou: 0x{r:X8}");
                DestroyDecoder();
                _waitKeyframe = true;
                RequestKeyframe();
                return;
            }

            HandleFrame(w, h, pts);
        }
    }

    /// <summary>Phone clock minus PC clock (ms), or null when unknown. Set by the owner.</summary>
    public Func<long?>? PhoneClockOffsetMs { get; set; }

    /// <summary>Camera capture → decoded on the PC, averaged over ~1 s (ms); -1 when unknown.</summary>
    public int CaptureToDecodeMs { get; private set; } = -1;

    private long _e2eSum;
    private int _e2eCount;
    private long _e2eWindowStart = Environment.TickCount64;
    private long _lastE2eLog;

    private void TrackEndToEnd(long ptsHns)
    {
        long now = Environment.TickCount64;
        if (PhoneClockOffsetMs?.Invoke() is { } offset)
        {
            long captureOnPcClock = ptsHns / 10_000 - offset;
            long lat = now - captureOnPcClock;
            if (lat > -1000 && lat < 60_000) { _e2eSum += Math.Max(0, lat); _e2eCount++; }
        }
        if (now - _e2eWindowStart >= 1000)
        {
            CaptureToDecodeMs = _e2eCount > 0 ? (int)(_e2eSum / _e2eCount) : -1;
            if (now - _lastE2eLog >= 10_000)
            {
                _lastE2eLog = now;
                Log.Info($"Vídeo: {FrameWidth}x{FrameHeight} {DecodedFps:0.0} fps, decodificação {AvgDecodeMs:0.0} ms, " +
                         $"atraso câmera→PC {(CaptureToDecodeMs >= 0 ? CaptureToDecodeMs + " ms" : "desconhecido")}, fila {_queue.Count}");
            }
            _e2eSum = 0;
            _e2eCount = 0;
            _e2eWindowStart = now;
        }
    }

    private void HandleFrame(int w, int h, long ptsHns)
    {
        FrameWidth = w;
        FrameHeight = h;
        Interlocked.Increment(ref _framesDecoded);
        UpdateFps();
        TrackEndToEnd(ptsHns);

        // 1) virtual camera
        long now = Environment.TickCount64;
        if (!_sinkOpen && now - _lastSinkAttempt >= SinkRetryMs)
        {
            _lastSinkAttempt = now;
            _sinkOpen = NativeMethods.cc_sink_open() == 1;
        }
        if (_sinkOpen)
        {
            int wr;
            fixed (byte* buf = _nv12)
            {
                wr = NativeMethods.cc_sink_write_nv12(buf, w, h, w, ptsHns);
            }
            if (wr <= 0) _sinkOpen = false; // 0: sink gone; <0: bad args — retry open later
        }

        // 2) preview (<= 30 fps, drop if the UI has not consumed the previous one)
        if (now - _lastPreview >= PreviewIntervalMs && Volatile.Read(ref _previewPending) == 0 && PreviewReady is { } handler)
        {
            _lastPreview = now;
            int outW = Math.Min(PreviewMaxWidth, w) & ~1;
            int outH = Math.Max(2, (int)Math.Round(outW * (double)h / w));
            int need = outW * outH * 4;
            if (_bgra.Length != need) _bgra = new byte[need];

            int cv;
            fixed (byte* src = _nv12)
            fixed (byte* dst = _bgra)
            {
                cv = NativeMethods.cc_nv12_to_bgra(src, w, h, dst, outW, outH);
            }
            if (cv == 0)
            {
                Volatile.Write(ref _previewPending, 1);
                try { handler(_bgra, outW, outH); }
                catch { Volatile.Write(ref _previewPending, 0); throw; }
            }
        }
    }

    private void UpdateFps()
    {
        _fpsFrames++;
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _fpsWindowStart) / (double)Stopwatch.Frequency;
        if (elapsed >= 1.0)
        {
            DecodedFps = _fpsFrames / elapsed;
            _fpsFrames = 0;
            _fpsWindowStart = now;
        }
    }

    private void DestroyDecoder()
    {
        if (_decoder != 0)
        {
            try { NativeMethods.cc_decoder_destroy(_decoder); } catch (Exception ex) { Log.Error("cc_decoder_destroy", ex); }
            _decoder = 0;
        }
        DecodedFps = 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        if (!_thread.Join(2000)) Log.Warn("Thread de vídeo não encerrou a tempo");
        if (_sinkOpen)
        {
            try { NativeMethods.cc_sink_close(); } catch { /* ignore */ }
            _sinkOpen = false;
        }
        _cts.Dispose();
    }
}
