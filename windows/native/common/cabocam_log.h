// Tiny OutputDebugString logger shared by both DLLs (view with Sysinternals DebugView,
// "Capture Global Win32"). No file logging: the Frame Server is sandboxed.
#pragma once
#include <windows.h>
#include <stdarg.h>
#include <stdlib.h>
#include <wchar.h>

// Optional sink (set by CaboCam.exe through cc_set_log_callback) so native diagnostics also land
// in the app's log file. Never set inside the Frame Server.
typedef void (__cdecl *CcLogSinkFn)(const wchar_t* message);
inline CcLogSinkFn& CcLogSink()
{
    static CcLogSinkFn sink = nullptr;
    return sink;
}

inline void CcLog(const wchar_t* fmt, ...)
{
    wchar_t buf[640];
    wcscpy_s(buf, L"[CaboCam] ");
    const size_t off = wcslen(buf);
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(buf + off, _countof(buf) - off - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    if (CcLogSinkFn sink = CcLogSink()) sink(buf + off);
    wcscat_s(buf, L"\n");
    OutputDebugStringW(buf);
}
