// Common includes and helpers for CaboCam.VirtualCamera.dll.
#pragma once

#include <windows.h>
#include <unknwn.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <mferror.h>
#include <evr.h>        // IMFVideoSampleAllocatorEx, MFCreateVideoSampleAllocatorEx
#include <ks.h>         // must precede ksproxy.h
#include <ksproxy.h>    // IKsControl

#include <wrl/client.h>

#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <new>
#include <thread>
#include <vector>

#include "cabocam_shared.h"
#include "cabocam_log.h"

using Microsoft::WRL::ComPtr;

extern HMODULE g_hModule;
extern volatile LONG g_dllRefCount;

// {B66867C7-2F79-49DF-983E-13FAB663FD84}
static const GUID CLSID_CaboCamSource =
    { 0xB66867C7, 0x2F79, 0x49DF, { 0x98, 0x3E, 0x13, 0xFA, 0xB6, 0x63, 0xFD, 0x84 } };

// PINNAME_VIDEO_CAPTURE {FB6C4281-0353-11D1-905F-0000C0CC16BA} (spelled out so we do not
// depend on ksmedia.h macro machinery).
static const GUID kPinNameVideoCapture =
    { 0xFB6C4281, 0x0353, 0x11D1, { 0x90, 0x5F, 0x00, 0x00, 0xC0, 0xCC, 0x16, 0xBA } };

class CcCs
{
public:
    CcCs() { InitializeCriticalSection(&m_cs); }
    ~CcCs() { DeleteCriticalSection(&m_cs); }
    void Lock() { EnterCriticalSection(&m_cs); }
    void Unlock() { LeaveCriticalSection(&m_cs); }
    CcCs(const CcCs&) = delete;
    CcCs& operator=(const CcCs&) = delete;
private:
    CRITICAL_SECTION m_cs;
};

class CcAutoLock
{
public:
    explicit CcAutoLock(CcCs& c) : m_c(c) { m_c.Lock(); }
    ~CcAutoLock() { m_c.Unlock(); }
    CcAutoLock(const CcAutoLock&) = delete;
    CcAutoLock& operator=(const CcAutoLock&) = delete;
private:
    CcCs& m_c;
};

// Forwards every IMFAttributes method to `target` (an IMFAttributes smart pointer).
#define CC_FORWARD_IMFATTRIBUTES(target)                                                          \
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return (target)->GetItem(k, v); }  \
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return (target)->GetItemType(k, t); } \
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return (target)->CompareItem(k, v, r); } \
    STDMETHODIMP Compare(IMFAttributes* o, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return (target)->Compare(o, m, r); } \
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return (target)->GetUINT32(k, v); }  \
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return (target)->GetUINT64(k, v); }  \
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return (target)->GetDouble(k, v); }  \
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return (target)->GetGUID(k, v); }        \
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* c) override { return (target)->GetStringLength(k, c); } \
    STDMETHODIMP GetString(REFGUID k, LPWSTR b, UINT32 n, UINT32* c) override { return (target)->GetString(k, b, n, c); } \
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* b, UINT32* c) override { return (target)->GetAllocatedString(k, b, c); } \
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* c) override { return (target)->GetBlobSize(k, c); } \
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 n, UINT32* c) override { return (target)->GetBlob(k, b, n, c); } \
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* c) override { return (target)->GetAllocatedBlob(k, b, c); } \
    STDMETHODIMP GetUnknown(REFGUID k, REFIID r, LPVOID* o) override { return (target)->GetUnknown(k, r, o); } \
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return (target)->SetItem(k, v); } \
    STDMETHODIMP DeleteItem(REFGUID k) override { return (target)->DeleteItem(k); }              \
    STDMETHODIMP DeleteAllItems() override { return (target)->DeleteAllItems(); }                \
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return (target)->SetUINT32(k, v); }   \
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return (target)->SetUINT64(k, v); }   \
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return (target)->SetDouble(k, v); }   \
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return (target)->SetGUID(k, v); }      \
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return (target)->SetString(k, v); }  \
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 n) override { return (target)->SetBlob(k, b, n); } \
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* u) override { return (target)->SetUnknown(k, u); } \
    STDMETHODIMP LockStore() override { return (target)->LockStore(); }                          \
    STDMETHODIMP UnlockStore() override { return (target)->UnlockStore(); }                      \
    STDMETHODIMP GetCount(UINT32* c) override { return (target)->GetCount(c); }                  \
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return (target)->GetItemByIndex(i, k, v); } \
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return (target)->CopyAllItems(d); }
