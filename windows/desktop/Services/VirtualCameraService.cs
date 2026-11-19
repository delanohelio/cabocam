using System;
using System.Threading;
using CaboCam.Native;

namespace CaboCam.Services;

public enum VcamStatus { Unknown, Active, NotInstalled, RequiresWindows11, Failed }

/// <summary>Registers the MF virtual camera and polls whether an app consumes it.</summary>
public sealed class VirtualCameraService : IDisposable
{
    private Timer? _poll;
    private bool _registered;
    private int _consumerActive;

    public VcamStatus Status { get; private set; } = VcamStatus.Unknown;
    public string Message { get; private set; } = "Câmera virtual não iniciada";
    public bool InUse => Volatile.Read(ref _consumerActive) != 0;

    public event Action? Changed;

    public void Register()
    {
        try
        {
            int hr = NativeMethods.cc_vcam_register();
            if (hr == 0)
            {
                _registered = true;
                Set(VcamStatus.Active, "Câmera virtual 'CaboCam' ativa");
                _poll = new Timer(_ => Poll(), null, 1000, 1000);
                return;
            }

            Log.Error($"cc_vcam_register falhou: 0x{hr:X8}");
            switch ((uint)hr)
            {
                case 0x80040154: // REGDB_E_CLASSNOTREG
                    Set(VcamStatus.NotInstalled, "Driver da câmera virtual não instalado — execute install.ps1 como administrador");
                    break;
                case 0x80070005: // E_ACCESSDENIED
                    Set(VcamStatus.RequiresWindows11, "Requer Windows 11");
                    break;
                default:
                    Set(VcamStatus.Failed, $"Falha ao criar a câmera virtual (0x{hr:X8})");
                    break;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Error("CaboCam.Native.dll ausente ou sem exportação", ex);
            Set(VcamStatus.RequiresWindows11, "Requer Windows 11");
        }
        catch (BadImageFormatException ex)
        {
            Log.Error("CaboCam.Native.dll com arquitetura incorreta", ex);
            Set(VcamStatus.Failed, "CaboCam.Native.dll incompatível (requer x64)");
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao registrar câmera virtual", ex);
            Set(VcamStatus.Failed, "Erro ao criar a câmera virtual: " + ex.Message);
        }
    }

    private void Poll()
    {
        try
        {
            int active = NativeMethods.cc_sink_consumer_active();
            if (Interlocked.Exchange(ref _consumerActive, active) != active) Changed?.Invoke();
        }
        catch
        {
            // native library unavailable; nothing to poll
        }
    }

    private void Set(VcamStatus status, string message)
    {
        Status = status;
        Message = message;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        if (_registered)
        {
            try { NativeMethods.cc_vcam_unregister(); } catch (Exception ex) { Log.Error("cc_vcam_unregister", ex); }
            _registered = false;
        }
    }
}
