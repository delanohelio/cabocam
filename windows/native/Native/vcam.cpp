#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <wrl/client.h>
#include <mutex>

#include "cabocam_shared.h"
#include "cabocam_native.h"
#include "native_common.h"

using Microsoft::WRL::ComPtr;

namespace
{
    std::mutex g_mutex;
    ComPtr<IMFVirtualCamera> g_vcam;
}

CC_API int32_t cc_vcam_register(void)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    if (g_vcam) return 0;   // already registered in this process

    CcEnsureComInit();
    HRESULT hr = CcEnsureMfStartup();
    if (FAILED(hr))
    {
        CcLog(L"MFStartup failed 0x%08X", static_cast<unsigned>(hr));
        return static_cast<int32_t>(hr);
    }

    ComPtr<IMFVirtualCamera> vcam;
    hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
                               MFVirtualCameraLifetime_Session,
                               MFVirtualCameraAccess_CurrentUser,
                               CABOCAM_FRIENDLY_NAME,
                               CABOCAM_SOURCE_CLSID_STR,
                               nullptr, 0, &vcam);
    if (FAILED(hr))
    {
        CcLog(L"MFCreateVirtualCamera failed 0x%08X", static_cast<unsigned>(hr));
        return static_cast<int32_t>(hr);
    }

    hr = vcam->Start(nullptr);
    if (FAILED(hr))
    {
        CcLog(L"IMFVirtualCamera::Start failed 0x%08X", static_cast<unsigned>(hr));
        vcam->Remove();
        return static_cast<int32_t>(hr);
    }

    g_vcam = vcam;
    CcLog(L"virtual camera started");
    return 0;
}

CC_API int32_t cc_vcam_unregister(void)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    if (!g_vcam) return 0;
    HRESULT hr = g_vcam->Remove();
    if (FAILED(hr)) CcLog(L"IMFVirtualCamera::Remove failed 0x%08X", static_cast<unsigned>(hr));
    const HRESULT sh = g_vcam->Shutdown();
    if (FAILED(sh)) CcLog(L"IMFVirtualCamera::Shutdown failed 0x%08X", static_cast<unsigned>(sh));
    g_vcam.Reset();
    CcLog(L"virtual camera removed");
    return static_cast<int32_t>(hr);
}

CC_API void cc_set_log_callback(void (__cdecl *callback)(const wchar_t* message))
{
    CcLogSink() = callback;
}

CC_API int32_t cc_vcam_is_registered(void)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    return g_vcam ? 1 : 0;
}
