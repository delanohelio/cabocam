// Tiny OutputDebugString logger shared by both DLLs (view with Sysinternals DebugView,
// "Capture Global Win32"). No file logging: the Frame Server is sandboxed.
#pragma once
#include <windows.h>
#include <stdarg.h>
#include <stdlib.h>
#include <wchar.h>

inline void CcLog(const wchar_t* fmt, ...)
{
    wchar_t buf[640];
    wcscpy_s(buf, L"[CaboCam] ");
    const size_t off = wcslen(buf);
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(buf + off, _countof(buf) - off - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    wcscat_s(buf, L"\n");
    OutputDebugStringW(buf);
}
