// Shared-memory + event creation/opening used by BOTH DLLs.
//  - CaboCam.VirtualCamera.dll (LocalService, has SeCreateGlobalPrivilege): creates.
//  - CaboCam.Native.dll (user process): opens; creates only if elevated.
#pragma once
#include <windows.h>
#include <sddl.h>
#include "cabocam_shared.h"
#include "cabocam_log.h"

// SYSTEM, LocalService, Administrators: all access. Authenticated Users: read/write
// (mapping) and SYNCHRONIZE|READ_CONTROL|EVENT_MODIFY_STATE|EVENT_QUERY_STATE (event).
#define CABOCAM_MAP_SDDL L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGW;;;AU)"
#define CABOCAM_EVT_SDDL L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;0x00120003;;;AU)"

struct CcShm
{
    HANDLE map = nullptr;
    HANDLE evt = nullptr;
    CaboCamShmHeader* hdr = nullptr;
    bool created = false;

    void Close()
    {
        if (hdr) { UnmapViewOfFile(hdr); hdr = nullptr; }
        if (evt) { CloseHandle(evt); evt = nullptr; }
        if (map) { CloseHandle(map); map = nullptr; }
        created = false;
    }
};

namespace ccdetail
{
    struct SecAttr
    {
        SECURITY_ATTRIBUTES sa{};
        PSECURITY_DESCRIPTOR sd = nullptr;
        bool Init(const wchar_t* sddl)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &sd, nullptr))
                return false;
            sa.nLength = sizeof(sa);
            sa.lpSecurityDescriptor = sd;
            sa.bInheritHandle = FALSE;
            return true;
        }
        ~SecAttr() { if (sd) LocalFree(sd); }
    };
}

inline bool CcIsElevated()
{
    HANDLE tok = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) return false;
    TOKEN_ELEVATION el{};
    DWORD cb = 0;
    const BOOL ok = GetTokenInformation(tok, TokenElevation, &el, sizeof(el), &cb);
    CloseHandle(tok);
    return ok && el.TokenIsElevated != 0;
}

// Creates (or opens) the named event if it is not held yet. Safe to call repeatedly.
inline void CcShmEnsureEvent(CcShm& s, bool allowCreate)
{
    if (s.evt) return;
    HANDLE e = nullptr;
    if (allowCreate)
    {
        ccdetail::SecAttr sa;
        if (sa.Init(CABOCAM_EVT_SDDL))
            e = CreateEventW(&sa.sa, FALSE, FALSE, CABOCAM_EVENT_NAME);   // auto-reset, non-signaled
    }
    if (!e)
        e = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, CABOCAM_EVENT_NAME);
    s.evt = e;
}

// allowCreate: try CreateFileMapping with our DACL first (falls back to open on failure,
// e.g. ERROR_ACCESS_DENIED when another process already created it).
// allowCreate == false: only OpenFileMapping.
inline bool CcShmOpenOrCreate(CcShm& s, bool allowCreate)
{
    if (s.hdr) { CcShmEnsureEvent(s, allowCreate); return true; }

    HANDLE map = nullptr;
    bool created = false;
    if (allowCreate)
    {
        ccdetail::SecAttr sa;
        if (sa.Init(CABOCAM_MAP_SDDL))
        {
            SetLastError(0);
            map = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa.sa, PAGE_READWRITE, 0,
                                     CABOCAM_SHM_TOTAL_SIZE, CABOCAM_SHM_NAME);
            const DWORD err = GetLastError();
            if (map) created = (err != ERROR_ALREADY_EXISTS);
            else CcLog(L"CreateFileMapping failed, error %lu (will try OpenFileMapping)", err);
        }
    }
    if (!map)
        map = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, CABOCAM_SHM_NAME);
    if (!map) return false;

    void* view = MapViewOfFile(map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, CABOCAM_SHM_TOTAL_SIZE);
    if (!view) { CloseHandle(map); return false; }

    s.map = map;
    s.hdr = static_cast<CaboCamShmHeader*>(view);
    s.created = created;
    if (created)
    {
        // Fresh section is zero-filled. Publish magic last.
        s.hdr->version = CABOCAM_SHM_VERSION;
        s.hdr->dataCapacity = CABOCAM_SHM_DATA_CAPACITY;
        s.hdr->format = CABOCAM_FORMAT_NV12;
        MemoryBarrier();
        s.hdr->magic = CABOCAM_SHM_MAGIC;
    }
    CcShmEnsureEvent(s, allowCreate);
    return true;
}
