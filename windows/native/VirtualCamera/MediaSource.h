#pragma once
#include "vcam_common.h"
#include "SharedFrameSource.h"

// Event-generator boilerplate shared by source and stream. Requires members:
//   CcCs m_cs; bool m_shutdown; ComPtr<IMFMediaEventQueue> m_events;
#define CC_IMPL_EVENT_GENERATOR()                                                                  \
    STDMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override                         \
    {                                                                                              \
        ComPtr<IMFMediaEventQueue> q;                                                              \
        { CcAutoLock l(m_cs); if (m_shutdown) return MF_E_SHUTDOWN; q = m_events; }                \
        return q->GetEvent(dwFlags, ppEvent); /* may block; lock must not be held */               \
    }                                                                                              \
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* pState) override             \
    {                                                                                              \
        CcAutoLock l(m_cs);                                                                        \
        if (m_shutdown) return MF_E_SHUTDOWN;                                                      \
        return m_events->BeginGetEvent(pCallback, pState);                                         \
    }                                                                                              \
    STDMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override            \
    {                                                                                              \
        CcAutoLock l(m_cs);                                                                        \
        if (m_shutdown) return MF_E_SHUTDOWN;                                                      \
        return m_events->EndGetEvent(pResult, ppEvent);                                            \
    }                                                                                              \
    STDMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus,        \
                            const PROPVARIANT* pvValue) override                                   \
    {                                                                                              \
        CcAutoLock l(m_cs);                                                                        \
        if (m_shutdown) return MF_E_SHUTDOWN;                                                      \
        return m_events->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue);             \
    }

class CCMediaStream final : public IMFMediaStream2, public IKsControl
{
public:
    static HRESULT Create(IMFMediaSource* parent, IMFStreamDescriptor* sd,
                          std::shared_ptr<SharedFrameSource> shm, CCMediaStream** out);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&m_ref)); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }

    // IMFMediaEventGenerator
    CC_IMPL_EVENT_GENERATOR()

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** ppMediaSource) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor) override;
    STDMETHODIMP RequestSample(IUnknown* pToken) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE value) override;
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* value) override;

    // IKsControl
    STDMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) override
    { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) override
    { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) override
    { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }

    // Called by the source.
    HRESULT Start(IMFMediaType* type, const PROPVARIANT* startPos);
    HRESULT Stop();
    void Shutdown();

private:
    CCMediaStream();
    ~CCMediaStream();

    HRESULT Init(IMFMediaSource* parent, IMFStreamDescriptor* sd, std::shared_ptr<SharedFrameSource> shm);
    void WorkerLoop();
    HRESULT BuildSample(UINT32 w, UINT32 h, const ComPtr<IMFVideoSampleAllocatorEx>& alloc, IMFSample** out);
    void InitAllocatorLocked(IMFMediaType* type);

    volatile LONG m_ref = 1;
    CcCs m_cs;
    bool m_shutdown = false;
    bool m_quit = false;
    ComPtr<IMFMediaEventQueue> m_events;
    ComPtr<IMFMediaSource> m_parent;
    ComPtr<IMFStreamDescriptor> m_sd;
    std::shared_ptr<SharedFrameSource> m_shm;
    ComPtr<IMFMediaType> m_type;
    ComPtr<IMFVideoSampleAllocatorEx> m_alloc;

    MF_STREAM_STATE m_state = MF_STREAM_STATE_STOPPED;
    UINT32 m_w = 1920, m_h = 1080, m_fpsNum = 30, m_fpsDen = 1;
    LONGLONG m_frameDur = 333333;
    LONGLONG m_start = 0;
    LONGLONG m_nextDue = 0;
    std::deque<ComPtr<IUnknown>> m_pending;

    HANDLE m_wake = nullptr;    // auto-reset
    HANDLE m_timer = nullptr;   // waitable timer (high resolution when available)
    std::thread m_thread;
};

class CCMediaSource final : public IMFMediaSourceEx,
                            public IMFGetService,
                            public IKsControl,
                            public IMFSampleAllocatorControl
{
public:
    static HRESULT Create(IMFMediaSourceEx** out);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&m_ref)); }
    STDMETHODIMP_(ULONG) Release() override
    {
        const LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }

    // IMFMediaEventGenerator
    CC_IMPL_EVENT_GENERATOR()

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* pdwCharacteristics) override;
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppPD) override;
    STDMETHODIMP Start(IMFPresentationDescriptor* pPD, const GUID* pguidTimeFormat,
                       const PROPVARIANT* pvarStartPosition) override;
    STDMETHODIMP Stop() override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** ppAttributes) override;
    STDMETHODIMP GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes) override;
    STDMETHODIMP SetD3DManager(IUnknown* pManager) override;

    // IMFGetService
    STDMETHODIMP GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) override;

    // IKsControl
    STDMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) override
    { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) override
    { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) override
    { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }

    // IMFSampleAllocatorControl
    STDMETHODIMP SetDefaultAllocator(DWORD dwOutputStreamID, IUnknown* pAllocator) override;
    STDMETHODIMP GetAllocatorUsage(DWORD dwOutputStreamID, DWORD* pdwInputStreamID,
                                   MFSampleAllocatorUsage* peUsage) override;

private:
    CCMediaSource();
    ~CCMediaSource();
    HRESULT Init();

    volatile LONG m_ref = 1;
    CcCs m_cs;
    bool m_shutdown = false;
    bool m_streamAnnounced = false;
    ComPtr<IMFMediaEventQueue> m_events;
    ComPtr<IMFAttributes> m_attrs;
    ComPtr<IMFStreamDescriptor> m_sd;
    ComPtr<IMFPresentationDescriptor> m_pd;
    ComPtr<IMFMediaStream2> m_streamIface;   // owning reference
    CCMediaStream* m_stream = nullptr;       // same object as m_streamIface
    std::shared_ptr<SharedFrameSource> m_shm;
};
