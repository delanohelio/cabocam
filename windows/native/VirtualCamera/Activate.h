#pragma once
#include "vcam_common.h"

// IMFActivate handed out by our class factory. Frame Server (MFCreateVirtualCamera with a
// custom source CLSID) CoCreates this class and calls ActivateObject(IID_IMFMediaSource).
class CCActivate final : public IMFActivate
{
public:
    static HRESULT Create(IMFActivate** out);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&m_ref)); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }

    // IMFAttributes -> forwarded to a real attribute store
    CC_FORWARD_IMFATTRIBUTES(m_attrs)

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    STDMETHODIMP ShutdownObject() override;
    STDMETHODIMP DetachObject() override;

private:
    CCActivate();
    ~CCActivate();

    volatile LONG m_ref = 1;
    CcCs m_cs;
    ComPtr<IMFAttributes> m_attrs;
    ComPtr<IMFMediaSourceEx> m_source;
};

class CCClassFactory final : public IClassFactory
{
public:
    CCClassFactory() { InterlockedIncrement(&g_dllRefCount); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&m_ref)); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }
    STDMETHODIMP CreateInstance(IUnknown* pOuter, REFIID riid, void** ppv) override;
    STDMETHODIMP LockServer(BOOL fLock) override;

private:
    ~CCClassFactory() { InterlockedDecrement(&g_dllRefCount); }
    volatile LONG m_ref = 1;
};
