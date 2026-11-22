using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CaboCam.Native;

/// <summary>P/Invoke surface of CaboCam.Native.dll (windows/native/common/cabocam_native.h).</summary>
internal static unsafe partial class NativeMethods
{
    public const string Lib = "CaboCam.Native.dll";
    public const int CodecH264 = 1;
    public const int CodecHevc = 2;

    // ---- Virtual camera lifecycle
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_vcam_register();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_vcam_unregister();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial void cc_set_log_callback(delegate* unmanaged[Cdecl]<char*, void> callback);

    /// <summary>Routes native CcLog lines into the app log (%LocalAppData%\CaboCam\logs).</summary>
    public static void EnableNativeLog()
    {
        try { cc_set_log_callback(&OnNativeLog); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { /* older/missing DLL */ }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeLog(char* message)
    {
        try { CaboCam.Services.Log.Info("native: " + new string(message)); } catch { /* never throw into native */ }
    }

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_vcam_is_registered();

    // ---- Frame sink
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_sink_open();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_sink_write_nv12(byte* data, int width, int height, int stride, long timestampHns);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_sink_consumer_active();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial void cc_sink_close();

    // ---- Decoder (cc_decoder* is opaque)
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial nint cc_decoder_create(int codec);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_decoder_push(nint dec, byte* annexb, int length, long ptsHns);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_decoder_pull(nint dec, byte* nv12Out, int capacity, int* width, int* height, long* ptsHns);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial void cc_decoder_destroy(nint dec);

    // ---- Utility
    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static partial int cc_nv12_to_bgra(byte* nv12, int width, int height, byte* bgraOut, int outWidth, int outHeight);
}
