using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaboCam.Services;

namespace CaboCam.Protocol;

/// <summary>Speaks PROTOCOL.md over an established stream. Events are raised on the read-loop thread.</summary>
public sealed class ProtocolClient : IDisposable
{
    private readonly Stream _stream;
    private readonly PacketReader _reader;
    private readonly PacketWriter _writer;
    private long _lastRxTick = Environment.TickCount64;

    public ProtocolClient(Stream stream)
    {
        _stream = stream;
        _reader = new PacketReader(stream);
        _writer = new PacketWriter(stream);
    }

    public long LastReceiveTick => Interlocked.Read(ref _lastRxTick);

    public event Action<HelloMessage>? HelloReceived;
    public event Action<CapabilitiesMessage>? CapabilitiesReceived;
    public event Action<DeviceState>? StateReceived;
    public event Action<StreamStartedMessage>? StreamStarted;
    public event Action? StreamStopped;
    public event Action<string>? ErrorReceived;
    /// <summary>(t echoed back, phone capture-clock time in µs if the app sends it).</summary>
    public event Action<long, long?>? PongReceived;
    public event Action<VideoPacket>? VideoReceived;

    public Task SendHelloAsync(CancellationToken ct) =>
        _writer.WriteJsonAsync(PacketType.Hello, new { protocol = 1, app = "CaboCam-Windows", version = "1.0" }, ct);

    public Task SendAsync(object message, CancellationToken ct) =>
        _writer.WriteJsonAsync(PacketType.Message, message, ct);

    /// <summary>Reads until the peer closes the connection (returns) or an error occurs (throws).</summary>
    public async Task RunReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var packet = await _reader.ReadAsync(ct).ConfigureAwait(false);
            if (packet is null) return;
            Interlocked.Exchange(ref _lastRxTick, Environment.TickCount64);
            Dispatch(packet.Value);
        }
    }

    private void Dispatch(Packet packet)
    {
        try
        {
            switch (packet.Type)
            {
                case PacketType.Video:
                    if (packet.Payload.Length > VideoPacket.HeaderSize)
                        VideoReceived?.Invoke(new VideoPacket(packet.Payload));
                    break;
                case PacketType.Hello:
                {
                    var hello = JsonSerializer.Deserialize<HelloMessage>(packet.Payload, ProtocolJson.Options);
                    if (hello is not null) HelloReceived?.Invoke(hello);
                    break;
                }
                case PacketType.Message:
                    DispatchMessage(packet.Payload);
                    break;
                // unknown packet types are ignored (forward compatibility)
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"Mensagem JSON inválida ignorada: {ex.Message}");
        }
    }

    private void DispatchMessage(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return;

        switch (typeEl.GetString())
        {
            case MessageTypes.Capabilities:
                if (root.Deserialize<CapabilitiesMessage>(ProtocolJson.Options) is { } caps) CapabilitiesReceived?.Invoke(caps);
                break;
            case MessageTypes.State:
                if (root.Deserialize<DeviceState>(ProtocolJson.Options) is { } state) StateReceived?.Invoke(state);
                break;
            case MessageTypes.StreamStarted:
                if (root.Deserialize<StreamStartedMessage>(ProtocolJson.Options) is { } ss) StreamStarted?.Invoke(ss);
                break;
            case MessageTypes.StreamStopped:
                StreamStopped?.Invoke();
                break;
            case MessageTypes.Error:
                ErrorReceived?.Invoke(root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "");
                break;
            case MessageTypes.Pong:
                if (root.TryGetProperty("t", out var t) && t.TryGetInt64(out var tv))
                {
                    long? phoneUs = root.TryGetProperty("phoneUs", out var pu) && pu.TryGetInt64(out var puv) ? puv : null;
                    PongReceived?.Invoke(tv, phoneUs);
                }
                break;
            case "hello":
                if (root.Deserialize<HelloMessage>(ProtocolJson.Options) is { } h) HelloReceived?.Invoke(h);
                break;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { /* ignore */ }
        _writer.Dispose();
    }
}
