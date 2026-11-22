using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CaboCam.Native;

namespace CaboCam.Services;

public enum VcamStatus { Unknown, Active, NotInstalled, NoAccess, RequiresWindows11, Failed }

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
        if (Environment.OSVersion.Version.Build < 22000)
        {
            Set(VcamStatus.RequiresWindows11, "A câmera virtual requer o Windows 11.");
            return;
        }
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

            var registered = RegisteredDllPath();
            Log.Error($"cc_vcam_register falhou: 0x{hr:X8} (driver registrado: {registered ?? "nenhum"})");
            bool otherFolder = registered is not null &&
                               !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(DriverDllPath), StringComparison.OrdinalIgnoreCase);
            switch ((uint)hr)
            {
                case 0x80040154: // REGDB_E_CLASSNOTREG
                    Set(VcamStatus.NotInstalled, "Driver da câmera virtual não instalado.");
                    break;
                case 0x80070005: // E_ACCESSDENIED: the Frame Server (LOCAL SERVICE) cannot load the DLL
                    Set(VcamStatus.NoAccess, otherFolder
                        ? $"O driver está registrado em outra pasta ({Path.GetDirectoryName(registered)}) que o serviço de câmera do Windows não consegue ler. Reinstale o driver."
                        : "O serviço de câmera do Windows não tem permissão para ler o driver (pasta extraída de novo?). Reinstale o driver.");
                    break;
                default:
                    Set(otherFolder ? VcamStatus.NoAccess : VcamStatus.Failed, otherFolder
                        ? $"O driver está registrado em outra pasta ({Path.GetDirectoryName(registered)}). Reinstale o driver. (0x{hr:X8})"
                        : $"Falha ao criar a câmera virtual (0x{hr:X8})");
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

    public static string DriverDllPath => Path.Combine(AppContext.BaseDirectory, "CaboCam.VirtualCamera.dll");

    /// <summary>%ProgramData%\CaboCam\logs: vcam.log written by the camera inside the Frame Server.</summary>
    public static string VcamLogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CaboCam", "logs");

    /// <summary>DLL path registered for the media source CLSID (HKLM), or null.</summary>
    public static string? RegisteredDllPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Classes\CLSID\{B66867C7-2F79-49DF-983E-13FAB663FD84}\InprocServer32");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Registers the virtual camera COM DLL as administrator (UAC prompt), the same as the
    /// installer does, then retries creating the camera. Returns null on success or an error.
    /// </summary>
    public async Task<string?> InstallDriverAsync()
    {
        var dll = DriverDllPath;
        if (!File.Exists(dll)) return $"CaboCam.VirtualCamera.dll não encontrado em {AppContext.BaseDirectory}. Reinstale o CaboCam.";

        var dir = AppContext.BaseDirectory.TrimEnd('\\');
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // The Frame Server runs as LocalService: grant read/execute to Users and LOCAL SERVICE, then register.
        var logs = VcamLogDirectory;
        var script = $"\"{sys}\\icacls.exe\" \"{dir}\" /grant *S-1-5-32-545:(OI)(CI)RX *S-1-5-19:(OI)(CI)RX /T /C /Q >nul"
                   + $" & mkdir \"{logs}\" 2>nul"
                   + $" & \"{sys}\\icacls.exe\" \"{logs}\" /grant *S-1-5-6:(OI)(CI)M *S-1-5-19:(OI)(CI)M *S-1-5-32-545:(OI)(CI)M *S-1-15-2-1:(OI)(CI)M /C /Q >nul"
                   + $" & \"{sys}\\regsvr32.exe\" /s \"{dll}\"";
        var psi = new ProcessStartInfo(Path.Combine(sys, "cmd.exe"), $"/d /c \"{script}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("processo não iniciou");
            await p.WaitForExitAsync().ConfigureAwait(false);
            Log.Info($"Instalação do driver: regsvr32 saiu com {p.ExitCode}");
            if (p.ExitCode != 0) return $"regsvr32 falhou (código {p.ExitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return "Instalação cancelada: a permissão de administrador foi negada.";
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao instalar o driver", ex);
            return "Falha ao instalar o driver: " + ex.Message;
        }

        Register();
        return Status == VcamStatus.Active ? null : Message;
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
            try
            {
                int hr = NativeMethods.cc_vcam_unregister();
                Log.Info(hr == 0 ? "Câmera virtual removida" : $"Remoção da câmera virtual retornou 0x{hr:X8}");
            }
            catch (Exception ex) { Log.Error("cc_vcam_unregister", ex); }
            _registered = false;
            Set(VcamStatus.Unknown, "Câmera virtual removida");
        }
    }
}
