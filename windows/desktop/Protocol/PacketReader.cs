using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CaboCam.Protocol;

public static class PacketType
{
    public const byte Hello = 0x01;
    public const byte Message = 0x02;
    public const byte Video = 0x10;
}

public readonly record struct Packet(byte Type, byte[] Payload);

/// <summary>Reads framed packets: u8 type, u32 LE length, payload.</summary>
public sealed class PacketReader
{
    public const int MaxPayload = 32 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[5];

    public PacketReader(Stream stream) => _stream = stream;

    /// <summary>Returns null on clean end-of-stream at a packet boundary.</summary>
    public async Task<Packet?> ReadAsync(CancellationToken ct)
    {
        int got = await ReadFullAsync(_header, 5, ct, allowEofAtStart: true).ConfigureAwait(false);
        if (got == 0) return null;

        byte type = _header[0];
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(1));
        if (length > MaxPayload) throw new InvalidDataException($"Pacote grande demais ({length} bytes)");

        var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0) await ReadFullAsync(payload, (int)length, ct, allowEofAtStart: false).ConfigureAwait(false);
        return new Packet(type, payload);
    }

    private async Task<int> ReadFullAsync(byte[] buffer, int count, CancellationToken ct, bool allowEofAtStart)
    {
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0 && allowEofAtStart) return 0;
                throw new EndOfStreamException("Conexão encerrada no meio de um pacote");
            }
            read += n;
        }
        return read;
    }
}
