#include "Activate.h"
#include "MediaSource.h"

CCActivate::CCActivate() { InterlockedIncrement(&g_dllRefCount); }
CCActivate::~CCActivate() { InterlockedDecrement(&g_dllRefCount); }

HRESULT CCActivate::Create(IMFActivate** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    CCActivate* a = new (std::nothrow) CCActivate();
    if (!a) return E_OUTOFMEMORY;
    const HRESULT hr = MFCreateAttributes(&a->m_attrs, 8);
    if (FAILED(hr)) { a->Release(); return hr; }
    *out = a;
    return S_OK;
}

STDMETHODIMP CCActivate::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IMFAttributes)) ||
        IsEqualIID(riid, __uuidof(IMFActivate)))
    {
        *ppv = static_cast<IMFActivate*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP CCActivate::ActivateObject(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    CcAutoLock l(m_cs);
    if (!m_source)
    {
        const HRESULT hr = CCMediaSource::Create(&m_source);
        if (FAILED(hr)) return hr;
    }
    return m_source->QueryInterface(riid, ppv);
}

STDMETHODIMP CCActivate::ShutdownObject()
{
    ComPtr<IMFMediaSourceEx> src;
    {
        CcAutoLock l(m_cs);
        src = std::move(m_source);
    }
    if (src) src->Shutdown();
    return S_OK;
}

STDMETHODIMP CCActivate::DetachObject()
{
    return E_NOTIMPL;
}

STDMETHODIMP CCClassFactory::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IClassFactory)))
    {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP CCClassFactory::CreateInstance(IUnknown* pOuter, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (pOuter) return CLASS_E_NOAGGREGATION;
    ComPtr<IMFActivate> act;
    HRESULT hr = CCActivate::Create(&act);
    if (FAILED(hr)) return hr;
    return act->QueryInterface(riid, ppv);
}

STDMETHODIMP CCClassFactory::LockServer(BOOL fLock)
{
    if (fLock) InterlockedIncrement(&g_dllRefCount);
    else InterlockedDecrement(&g_dllRefCount);
    return S_OK;
}
