using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CaboCam.Protocol;

/// <summary>Thread-safe packet writer.</summary>
public sealed class PacketWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PacketWriter(Stream stream) => _stream = stream;

    public async Task WriteAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > PacketReader.MaxPayload) throw new ArgumentException("Payload grande demais");
        var buf = new byte[5 + payload.Length];
        buf[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(buf.AsMemory(5));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buf, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task WriteJsonAsync(byte type, object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
        return WriteAsync(type, bytes, ct);
    }

    public void Dispose() => _lock.Dispose();
}
