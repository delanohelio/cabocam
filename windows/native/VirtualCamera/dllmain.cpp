// COM in-proc server entry points for CaboCam.VirtualCamera.dll.
#include "vcam_common.h"
#include "Activate.h"

HMODULE g_hModule = nullptr;
volatile LONG g_dllRefCount = 0;

namespace
{
    const wchar_t kClsidKey[] = L"Software\\Classes\\CLSID\\{B66867C7-2F79-49DF-983E-13FAB663FD84}";
    const wchar_t kInprocKey[] = L"Software\\Classes\\CLSID\\{B66867C7-2F79-49DF-983E-13FAB663FD84}\\InprocServer32";

    HRESULT SetStr(HKEY key, const wchar_t* name, const wchar_t* value)
    {
        const DWORD cb = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
        const LONG r = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), cb);
        return HRESULT_FROM_WIN32(r);
    }
}

namespace
{
    // File log for diagnosing the source inside the Windows Camera Frame Server, where
    // OutputDebugString is only visible to administrators. Written to
    // %ProgramData%\CaboCam\logs\vcam.log when that folder exists and is writable (the
    // installer creates it and grants write access to services and users).
    SRWLOCK g_logLock = SRWLOCK_INIT;

    void __cdecl FileLogSink(const wchar_t* message)
    {
        wchar_t dir[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"ProgramData", dir, MAX_PATH);
        if (n == 0 || n >= MAX_PATH - 40) wcscpy_s(dir, L"C:\\ProgramData");
        wchar_t path[MAX_PATH];
        swprintf_s(path, L"%s\\CaboCam\\logs\\vcam.log", dir);

        SYSTEMTIME st;
        GetLocalTime(&st);
        wchar_t line[800];
        swprintf_s(line, L"%02u:%02u:%02u.%03u [%lu] %s\r\n", st.wHour, st.wMinute, st.wSecond,
                   st.wMilliseconds, GetCurrentProcessId(), message);
        char utf8[1600];
        const int len = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), nullptr, nullptr);
        if (len <= 1) return;

        AcquireSRWLockExclusive(&g_logLock);
        HANDLE f = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                               nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f != INVALID_HANDLE_VALUE)
        {
            LARGE_INTEGER size{};
            if (GetFileSizeEx(f, &size) && size.QuadPart > 2 * 1024 * 1024)
            {
                // Keep it small: start over once it passes 2 MB.
                CloseHandle(f);
                f = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            }
            if (f != INVALID_HANDLE_VALUE)
            {
                DWORD written = 0;
                WriteFile(f, utf8, static_cast<DWORD>(len - 1), &written, nullptr);
                CloseHandle(f);
            }
        }
        ReleaseSRWLockExclusive(&g_logLock);
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
        CcLogSink() = &FileLogSink;
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_CaboCamSource)) return CLASS_E_CLASSNOTAVAILABLE;
    CCClassFactory* f = new (std::nothrow) CCClassFactory();
    if (!f) return E_OUTOFMEMORY;
    const HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return g_dllRefCount == 0 ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(g_hModule, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return HRESULT_FROM_WIN32(GetLastError());

    HKEY key = nullptr;
    LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kClsidKey, 0, nullptr, REG_OPTION_NON_VOLATILE,
                             KEY_WRITE | KEY_WOW64_64KEY, nullptr, &key, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    HRESULT hr = SetStr(key, nullptr, L"CaboCam Virtual Camera Source");
    RegCloseKey(key);
    if (FAILED(hr)) return hr;

    r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kInprocKey, 0, nullptr, REG_OPTION_NON_VOLATILE,
                        KEY_WRITE | KEY_WOW64_64KEY, nullptr, &key, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    hr = SetStr(key, nullptr, path);
    if (SUCCEEDED(hr)) hr = SetStr(key, L"ThreadingModel", L"Both");
    RegCloseKey(key);
    return hr;
}

STDAPI DllUnregisterServer()
{
    HKEY key = nullptr;
    LONG r = RegOpenKeyExW(HKEY_LOCAL_MACHINE, kClsidKey, 0, DELETE | KEY_WOW64_64KEY, &key);
    if (r == ERROR_FILE_NOT_FOUND) return S_OK;
    if (r == ERROR_SUCCESS) RegCloseKey(key);
    r = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey);
    if (r != ERROR_SUCCESS && r != ERROR_FILE_NOT_FOUND) return HRESULT_FROM_WIN32(r);
    return S_OK;
}
