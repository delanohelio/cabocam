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

    /// <summary>Queues a packet; never blocks. On overflow the backlog is dropped and a keyframe requested.</summary>
    public void Enqueue(VideoPacket packet)
    {
        if (_queue.IsAddingCompleted) return;
        if (_queue.TryAdd(packet)) return;

        while (_queue.TryTake(out _)) { }
        _waitKeyframe = true;
        RequestKeyframe();
    }

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

        int rc;
        fixed (byte* p = packet.Payload)
        {
            rc = NativeMethods.cc_decoder_push(_decoder, p + packet.DataOffset, packet.DataLength, packet.PtsUs * 10);
        }
        if (rc < 0)
        {
            Log.Warn($"cc_decoder_push falhou: 0x{rc:X8}");
            DestroyDecoder();
            _waitKeyframe = true;
            RequestKeyframe();
            return;
        }

        PullFrames();
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

    private void HandleFrame(int w, int h, long ptsHns)
    {
        FrameWidth = w;
        FrameHeight = h;
        Interlocked.Increment(ref _framesDecoded);
        UpdateFps();

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
