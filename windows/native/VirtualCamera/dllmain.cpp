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

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
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
