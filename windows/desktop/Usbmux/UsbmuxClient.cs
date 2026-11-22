using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CaboCam.Usbmux;

public sealed class UsbmuxException : Exception
{
    public int Code { get; }

    public UsbmuxException(int code, string message) : base(message) => Code = code;

    public static string Describe(int code) => code switch
    {
        2 => "Dispositivo não conectado",
        3 => "Porta recusada (abra o app CaboCam no iPhone)",
        5 => "Requisição malformada",
        6 => "Versão do protocolo usbmux incompatível",
        _ => $"Erro usbmux {code}",
    };
}

public sealed record UsbmuxDevice(int DeviceId, string ConnectionType, string SerialNumber, int ProductId)
{
    public bool IsUsb => string.Equals(ConnectionType, "USB", StringComparison.OrdinalIgnoreCase);
}

public enum UsbmuxEventKind { Attached, Detached, Paired }

public sealed record UsbmuxEvent(UsbmuxEventKind Kind, int DeviceId, UsbmuxDevice? Device);

/// <summary>
/// Client for usbmuxd as exposed by the Apple Mobile Device Service on Windows (127.0.0.1:27015).
/// Packet header (16 bytes LE): length (including header), version = 1, message = 8 (plist), tag.
/// </summary>
public static class UsbmuxClient
{
    public const int ServicePort = 27015;
    private const int MessagePlist = 8;
    private const int MaxPacket = 4 * 1024 * 1024;
    private const string ProgName = "CaboCam";

    private static int _tag;

    /// <summary>usbmux expects the port in network byte order stored in a host-order integer.</summary>
    public static int SwapPort(int port) => ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);

    private static async Task<Socket> OpenSocketAsync(CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ServicePort), timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>True if something is listening on the usbmuxd port (Apple Mobile Device Service running).</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var s = await OpenSocketAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?> BaseRequest(string messageType) => new()
    {
        ["MessageType"] = messageType,
        ["ClientVersionString"] = ProgName,
        ["ProgName"] = ProgName,
        ["kLibUSBMuxVersion"] = 3,
    };

    private static async Task SendAsync(Stream stream, Dictionary<string, object?> request, CancellationToken ct)
    {
        var payload = Plist.Write(request);
        var buf = new byte[16 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), buf.Length);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), MessagePlist);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), Interlocked.Increment(ref _tag));
        payload.CopyTo(buf, 16);
        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Conexão com o serviço Apple encerrada");
            read += n;
        }
    }

    private static async Task<Dictionary<string, object?>> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[16];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0));
        if (length < 16 || length > MaxPacket) throw new InvalidDataException($"Pacote usbmux inválido ({length})");
        var payload = new byte[length - 16];
        if (payload.Length > 0) await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        return Plist.ParseDict(payload);
    }

    private static void ThrowIfError(Dictionary<string, object?> reply)
    {
        var type = Plist.GetString(reply, "MessageType");
        if (type == "Result")
        {
            int number = (int)Plist.GetInt(reply, "Number");
            if (number != 0) throw new UsbmuxException(number, UsbmuxException.Describe(number));
        }
    }

    private static UsbmuxDevice? ParseDevice(Dictionary<string, object?> d)
    {
        if (!d.ContainsKey("DeviceID")) return null;
        var props = Plist.GetDict(d, "Properties");
        if (props is null) return null;
        return new UsbmuxDevice(
            (int)Plist.GetInt(d, "DeviceID"),
            Plist.GetString(props, "ConnectionType") ?? "",
            Plist.GetString(props, "SerialNumber") ?? "",
            (int)Plist.GetInt(props, "ProductID"));
    }

    public static async Task<List<UsbmuxDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        using var socket = await OpenSocketAsync(ct).ConfigureAwait(false);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        await SendAsync(stream, BaseRequest("ListDevices"), ct).ConfigureAwait(false);
        var reply = await ReadAsync(stream, ct).ConfigureAwait(false);
        ThrowIfError(reply);
        var result = new List<UsbmuxDevice>();
        if (Plist.GetArray(reply, "DeviceList") is { } list)
        {
            foreach (var item in list)
            {
                if (item is Dictionary<string, object?> d && ParseDevice(d) is { } dev) result.Add(dev);
            }
        }
        return result;
    }

    /// <summary>
    /// Long-lived connection yielding Attached / Detached / Paired events. usbmuxd first replays
    /// Attached for every device already present.
    /// </summary>
    public static async IAsyncEnumerable<UsbmuxEvent> ListenAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var socket = await OpenSocketAsync(ct).ConfigureAwait(false);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = BaseRequest("Listen");
        await SendAsync(stream, request, ct).ConfigureAwait(false);
        var reply = await ReadAsync(stream, ct).ConfigureAwait(false);
        ThrowIfError(reply);

        while (true)
        {
            var msg = await ReadAsync(stream, ct).ConfigureAwait(false);
            var type = Plist.GetString(msg, "MessageType");
            int id = (int)Plist.GetInt(msg, "DeviceID");
            switch (type)
            {
                case "Attached":
                    yield return new UsbmuxEvent(UsbmuxEventKind.Attached, id, ParseDevice(msg));
                    break;
                case "Detached":
                    yield return new UsbmuxEvent(UsbmuxEventKind.Detached, id, null);
                    break;
                case "Paired":
                    yield return new UsbmuxEvent(UsbmuxEventKind.Paired, id, null);
                    break;
            }
        }
    }

    /// <summary>
    /// Reads the host pairing record usbmuxd keeps for the device (created when the user tapped
    /// "Trust"). Contains HostID, SystemBUID, HostCertificate and HostPrivateKey (PEM).
    /// </summary>
    public static async Task<Dictionary<string, object?>> ReadPairRecordAsync(string udid, CancellationToken ct = default)
    {
        using var socket = await OpenSocketAsync(ct).ConfigureAwait(false);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = BaseRequest("ReadPairRecord");
        request["PairRecordID"] = udid;
        await SendAsync(stream, request, ct).ConfigureAwait(false);
        var reply = await ReadAsync(stream, ct).ConfigureAwait(false);
        ThrowIfError(reply);
        if (reply.TryGetValue("PairRecordData", out var v) && v is byte[] data && data.Length > 0)
            return Plist.ParseAny(data);
        throw new InvalidDataException("Registro de pareamento não encontrado. Desconecte o iPhone, reconecte e toque em \"Confiar\".");
    }

    /// <summary>
    /// Opens a tunnel to <paramref name="port"/> on the device. After a successful Result the same
    /// socket is the raw TCP stream to the device. The returned stream owns the socket.
    /// </summary>
    public static async Task<NetworkStream> ConnectAsync(int deviceId, int port, CancellationToken ct = default)
    {
        var socket = await OpenSocketAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = new NetworkStream(socket, ownsSocket: true);
            var request = BaseRequest("Connect");
            request["DeviceID"] = deviceId;
            request["PortNumber"] = SwapPort(port);
            await SendAsync(stream, request, ct).ConfigureAwait(false);
            var reply = await ReadAsync(stream, ct).ConfigureAwait(false);
            ThrowIfError(reply);
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
