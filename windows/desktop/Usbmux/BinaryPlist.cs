using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CaboCam.Usbmux;

/// <summary>
/// Minimal reader for Apple binary property lists ("bplist00"). Supports the types found in
/// usbmuxd pairing records: dict, array, string (ASCII/UTF-16), data, integer, real, bool, date.
/// </summary>
public static class BinaryPlist
{
    public static Dictionary<string, object?> ParseDict(byte[] data)
    {
        var root = Parse(data);
        return root as Dictionary<string, object?> ?? throw new InvalidDataException("bplist raiz não é um dict");
    }

    public static object? Parse(byte[] data)
    {
        if (data.Length < 40) throw new InvalidDataException("bplist muito curto");
        var trailer = data.AsSpan(data.Length - 32);
        int offsetSize = trailer[6];
        int refSize = trailer[7];
        long numObjects = BinaryPrimitives.ReadInt64BigEndian(trailer[8..]);
        long topObject = BinaryPrimitives.ReadInt64BigEndian(trailer[16..]);
        long tableOffset = BinaryPrimitives.ReadInt64BigEndian(trailer[24..]);

        var offsets = new long[numObjects];
        for (long i = 0; i < numObjects; i++)
            offsets[i] = ReadUInt(data, tableOffset + i * offsetSize, offsetSize);

        return new Reader(data, offsets, refSize).Read(topObject, 0);
    }

    private static long ReadUInt(byte[] data, long pos, int size)
    {
        long v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | data[pos + i];
        return v;
    }

    private sealed class Reader
    {
        private readonly byte[] _d;
        private readonly long[] _offsets;
        private readonly int _refSize;

        public Reader(byte[] d, long[] offsets, int refSize) { _d = d; _offsets = offsets; _refSize = refSize; }

        public object? Read(long index, int depth)
        {
            if (depth > 64) throw new InvalidDataException("bplist aninhado demais");
            long pos = _offsets[index];
            byte marker = _d[pos];
            int type = marker >> 4, info = marker & 0x0F;
            switch (type)
            {
                case 0x0:
                    return info switch { 0x8 => false, 0x9 => true, _ => null };
                case 0x1:
                {
                    int n = 1 << info;
                    long v = ReadUInt(_d, pos + 1, Math.Min(n, 8));
                    return v;
                }
                case 0x2:
                    return info == 3
                        ? BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(_d.AsSpan((int)pos + 1)))
                        : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(_d.AsSpan((int)pos + 1)));
                case 0x3:
                {
                    var secs = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(_d.AsSpan((int)pos + 1)));
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs).ToString("o");
                }
                case 0x4:
                {
                    var (len, start) = Length(pos, info);
                    return _d.AsSpan((int)start, (int)len).ToArray();
                }
                case 0x5:
                {
                    var (len, start) = Length(pos, info);
                    return Encoding.ASCII.GetString(_d, (int)start, (int)len);
                }
                case 0x6:
                {
                    var (len, start) = Length(pos, info);
                    return Encoding.BigEndianUnicode.GetString(_d, (int)start, (int)len * 2);
                }
                case 0x8:
                    return ReadUInt(_d, pos + 1, info + 1);
                case 0xA:
                {
                    var (len, start) = Length(pos, info);
                    var list = new List<object?>((int)len);
                    for (long i = 0; i < len; i++)
                        list.Add(Read(ReadUInt(_d, start + i * _refSize, _refSize), depth + 1));
                    return list;
                }
                case 0xD:
                {
                    var (len, start) = Length(pos, info);
                    var dict = new Dictionary<string, object?>((int)len);
                    for (long i = 0; i < len; i++)
                    {
                        var key = Read(ReadUInt(_d, start + i * _refSize, _refSize), depth + 1) as string ?? "";
                        dict[key] = Read(ReadUInt(_d, start + (len + i) * _refSize, _refSize), depth + 1);
                    }
                    return dict;
                }
                default:
                    return null;
            }
        }

        /// <summary>Object length (count) and where its payload starts.</summary>
        private (long Length, long Start) Length(long pos, int info)
        {
            if (info != 0xF) return (info, pos + 1);
            byte intMarker = _d[pos + 1];
            int n = 1 << (intMarker & 0x0F);
            return (ReadUInt(_d, pos + 2, n), pos + 2 + n);
        }
    }
}
