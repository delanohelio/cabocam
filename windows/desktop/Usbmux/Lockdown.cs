using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CaboCam.Usbmux;

/// <summary>
/// Minimal lockdownd client (TCP 62078 through usbmuxd): StartSession + TLS with the host pairing
/// certificate, then StartService. Used to reach com.apple.amfi.lockdown, which can reveal the
/// "Developer Mode" switch in Settings (same as `idevicedevmodectl reveal`).
/// </summary>
public sealed class Lockdown : IAsyncDisposable
{
    public const int Port = 62078;
    private const string Label = "CaboCam";
    private const int MaxMessage = 4 * 1024 * 1024;

    private readonly int _deviceId;
    private readonly X509Certificate2 _hostCert;
    private Stream _stream;

    private Lockdown(int deviceId, Stream stream, X509Certificate2 hostCert)
    {
        _deviceId = deviceId;
        _stream = stream;
        _hostCert = hostCert;
    }

    public static async Task<Lockdown> ConnectAsync(UsbmuxDevice device, CancellationToken ct)
    {
        var pair = await UsbmuxClient.ReadPairRecordAsync(device.SerialNumber, ct).ConfigureAwait(false);
        var hostId = Plist.GetString(pair, "HostID") ?? throw new InvalidDataException("Pareamento sem HostID");
        var buid = Plist.GetString(pair, "SystemBUID") ?? throw new InvalidDataException("Pareamento sem SystemBUID");
        var cert = LoadHostCertificate(pair);

        var stream = await UsbmuxClient.ConnectAsync(device.DeviceId, Port, ct).ConfigureAwait(false);
        var ld = new Lockdown(device.DeviceId, stream, cert);
        try
        {
            var reply = await ld.RequestAsync(new Dictionary<string, object?>
            {
                ["Label"] = Label,
                ["ProtocolVersion"] = "2",
                ["Request"] = "StartSession",
                ["HostID"] = hostId,
                ["SystemBUID"] = buid,
            }, ct).ConfigureAwait(false);
            ThrowIfError(reply, "StartSession");

            if (reply.TryGetValue("EnableSessionSSL", out var ssl) && ssl is true)
                ld._stream = await WrapTlsAsync(ld._stream, cert, ct).ConfigureAwait(false);
            return ld;
        }
        catch
        {
            await ld.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts a lockdown service and returns a stream to it (TLS-wrapped when required).</summary>
    public async Task<Stream> StartServiceAsync(string service, CancellationToken ct)
    {
        var reply = await RequestAsync(new Dictionary<string, object?>
        {
            ["Label"] = Label,
            ["Request"] = "StartService",
            ["Service"] = service,
        }, ct).ConfigureAwait(false);
        ThrowIfError(reply, service);

        int port = (int)Plist.GetInt(reply, "Port");
        if (port <= 0) throw new IOException($"Serviço {service} não retornou porta");

        Stream s = await UsbmuxClient.ConnectAsync(_deviceId, port, ct).ConfigureAwait(false);
        if (reply.TryGetValue("EnableServiceSSL", out var ssl) && ssl is true)
            s = await WrapTlsAsync(s, _hostCert, ct).ConfigureAwait(false);
        return s;
    }

    /// <summary>
    /// Asks AMFI to show the Developer Mode switch in Settings → Privacy &amp; Security.
    /// Returns null on success or the device's error message.
    /// </summary>
    public static async Task<string?> RevealDeveloperModeAsync(UsbmuxDevice device, CancellationToken ct)
    {
        await using var ld = await ConnectAsync(device, ct).ConfigureAwait(false);
        await using var amfi = await ld.StartServiceAsync("com.apple.amfi.lockdown", ct).ConfigureAwait(false);
        await WriteMessageAsync(amfi, new Dictionary<string, object?> { ["action"] = 0 }, ct).ConfigureAwait(false);
        var reply = await ReadMessageAsync(amfi, ct).ConfigureAwait(false);
        if (reply.TryGetValue("success", out var ok) && ok is true) return null;
        return Plist.GetString(reply, "Error") ?? "resposta inesperada do iPhone";
    }

    // =============================================================================
    // Plumbing
    // =============================================================================

    private async Task<Dictionary<string, object?>> RequestAsync(Dictionary<string, object?> request, CancellationToken ct)
    {
        await WriteMessageAsync(_stream, request, ct).ConfigureAwait(false);
        return await ReadMessageAsync(_stream, ct).ConfigureAwait(false);
    }

    private static void ThrowIfError(Dictionary<string, object?> reply, string what)
    {
        var err = Plist.GetString(reply, "Error");
        if (err is null) return;
        var hint = err switch
        {
            "InvalidHostID" or "SessionInactive" => " (refaça a confiança: desconecte, reconecte e toque em \"Confiar\")",
            "PasswordProtected" => " (desbloqueie o iPhone)",
            _ => "",
        };
        throw new IOException($"{what}: {err}{hint}");
    }

    private static async Task WriteMessageAsync(Stream s, Dictionary<string, object?> msg, CancellationToken ct)
    {
        var payload = Plist.Write(msg);
        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, payload.Length);
        payload.CopyTo(buf, 4);
        await s.WriteAsync(buf, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, object?>> ReadMessageAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        await s.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len <= 0 || len > MaxMessage) throw new InvalidDataException($"Mensagem lockdown inválida ({len})");
        var payload = new byte[len];
        await s.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return Plist.ParseAny(payload);
    }

    private static X509Certificate2 LoadHostCertificate(Dictionary<string, object?> pair)
    {
        static string Pem(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) switch
        {
            true when v is byte[] b => Encoding.ASCII.GetString(b),
            true when v is string s => s,
            _ => throw new InvalidDataException($"Pareamento sem {key}"),
        };

        using var pemCert = X509Certificate2.CreateFromPem(Pem(pair, "HostCertificate"), Pem(pair, "HostPrivateKey"));
        // Schannel needs the private key in a key container, not an ephemeral PEM key.
        return new X509Certificate2(pemCert.Export(X509ContentType.Pkcs12));
    }

    private static async Task<Stream> WrapTlsAsync(Stream inner, X509Certificate2 cert, CancellationToken ct)
    {
        // The device presents a certificate signed by the pairing root; it is not in any store.
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, (_, _, _, _) => true, (_, _, _, _, _) => cert);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "iPhone",
            ClientCertificates = new X509CertificateCollection { cert },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, ct).ConfigureAwait(false);
        return ssl;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        _hostCert.Dispose();
    }
}
