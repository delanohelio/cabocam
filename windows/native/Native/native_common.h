#pragma once
#include <windows.h>
#include <mfapi.h>
#include <mutex>
#include "cabocam_log.h"

// MFStartup exactly once per process (never shut down: the DLL lives as long as the app).
inline HRESULT CcEnsureMfStartup()
{
    static std::once_flag once;
    static HRESULT hr = E_FAIL;
    std::call_once(once, [] { hr = MFStartup(MF_VERSION, MFSTARTUP_FULL); });
    return hr;
}

// Best-effort COM init for the calling thread (MTA). A thread that is already STA
// returns RPC_E_CHANGED_MODE, which is fine for our free/both-threaded objects.
inline void CcEnsureComInit()
{
    (void)CoInitializeEx(nullptr, COINIT_MULTITHREADED);
}
