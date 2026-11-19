#include "MediaSource.h"

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace
{
    const DWORD kFourccNV12 = 0x3231564E;   // 'N' 'V' '1' '2'

    struct Mode { UINT32 w, h, fps; };
    // Order matters: the first entry is the default mode.
    const Mode kModes[] = {
        { 1920, 1080, 30 }, { 1280, 720, 30 }, { 1920, 1080, 60 },
        { 1280, 720, 60 }, { 3840, 2160, 30 }, { 640, 480, 30 },
    };
    const DWORD kModeCount = static_cast<DWORD>(_countof(kModes));

    HRESULT CreateNv12Type(const Mode& m, IMFMediaType** out)
    {
        ComPtr<IMFMediaType> t;
        HRESULT hr = MFCreateMediaType(&t);
        if (FAILED(hr)) return hr;
        const UINT32 sampleSize = m.w * m.h * 3 / 2;
        const UINT64 bitrate = static_cast<UINT64>(sampleSize) * 8ull * m.fps;
        hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, m.w, m.h);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t.Get(), MF_MT_FRAME_RATE, m.fps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t.Get(), MF_MT_FRAME_RATE_RANGE_MIN, m.fps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t.Get(), MF_MT_FRAME_RATE_RANGE_MAX, m.fps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_DEFAULT_STRIDE, m.w);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_SAMPLE_SIZE, sampleSize);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AVG_BITRATE, static_cast<UINT32>(bitrate));
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
        if (FAILED(hr)) return hr;
        *out = t.Detach();
        return S_OK;
    }
}

// ===========================================================================
// CCMediaStream
// ===========================================================================

CCMediaStream::CCMediaStream() { InterlockedIncrement(&g_dllRefCount); }

CCMediaStream::~CCMediaStream()
{
    // Normally Shutdown() already ran; make sure the worker is gone before members die.
    Shutdown();
    if (m_wake) CloseHandle(m_wake);
    if (m_timer) CloseHandle(m_timer);
    InterlockedDecrement(&g_dllRefCount);
}

HRESULT CCMediaStream::Create(IMFMediaSource* parent, IMFStreamDescriptor* sd,
                              std::shared_ptr<SharedFrameSource> shm, CCMediaStream** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    CCMediaStream* s = new (std::nothrow) CCMediaStream();
    if (!s) return E_OUTOFMEMORY;
    const HRESULT hr = s->Init(parent, sd, std::move(shm));
    if (FAILED(hr)) { s->Release(); return hr; }
    *out = s;
    return S_OK;
}

HRESULT CCMediaStream::Init(IMFMediaSource* parent, IMFStreamDescriptor* sd,
                            std::shared_ptr<SharedFrameSource> shm)
{
    m_parent = parent;
    m_sd = sd;
    m_shm = std::move(shm);
    HRESULT hr = MFCreateEventQueue(&m_events);
    if (FAILED(hr)) return hr;
    m_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!m_wake) return HRESULT_FROM_WIN32(GetLastError());
    m_timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    if (!m_timer)
        m_timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
    return S_OK;   // m_timer may be null: WorkerLoop falls back to a timed wait on m_wake
}

STDMETHODIMP CCMediaStream::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IMFMediaEventGenerator)) ||
        IsEqualIID(riid, __uuidof(IMFMediaStream)) || IsEqualIID(riid, __uuidof(IMFMediaStream2)))
        *ppv = static_cast<IMFMediaStream2*>(this);
    else if (IsEqualIID(riid, __uuidof(IKsControl)))
        *ppv = static_cast<IKsControl*>(this);
    else
    {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    static_cast<IUnknown*>(*ppv)->AddRef();
    return S_OK;
}

STDMETHODIMP CCMediaStream::GetMediaSource(IMFMediaSource** ppMediaSource)
{
    if (!ppMediaSource) return E_POINTER;
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    *ppMediaSource = m_parent.Get();
    (*ppMediaSource)->AddRef();
    return S_OK;
}

STDMETHODIMP CCMediaStream::GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor)
{
    if (!ppStreamDescriptor) return E_POINTER;
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    *ppStreamDescriptor = m_sd.Get();
    (*ppStreamDescriptor)->AddRef();
    return S_OK;
}

STDMETHODIMP CCMediaStream::RequestSample(IUnknown* pToken)
{
    {
        CcAutoLock l(m_cs);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (m_state == MF_STREAM_STATE_STOPPED) return MF_E_INVALIDREQUEST;
        if (m_pending.size() >= 64) return MF_E_INVALIDREQUEST;   // runaway consumer
        m_pending.emplace_back(pToken);   // ComPtr<IUnknown>(IUnknown*) AddRefs; token may be null
    }
    SetEvent(m_wake);
    return S_OK;
}

STDMETHODIMP CCMediaStream::SetStreamState(MF_STREAM_STATE value)
{
    bool streaming = false;
    {
        CcAutoLock l(m_cs);
        if (m_shutdown) return MF_E_SHUTDOWN;
        switch (value)
        {
        case MF_STREAM_STATE_STOPPED:
            m_state = value;
            m_pending.clear();
            break;
        case MF_STREAM_STATE_PAUSED:
            m_state = value;
            streaming = true;
            break;
        case MF_STREAM_STATE_RUNNING:
            if (m_state != MF_STREAM_STATE_RUNNING)
            {
                m_nextDue = MFGetSystemTime();
                if (m_start == 0) m_start = m_nextDue;
            }
            m_state = value;
            streaming = true;
            break;
        default:
            return E_INVALIDARG;
        }
    }
    m_shm->SetStreaming(streaming);
    SetEvent(m_wake);
    return S_OK;
}

STDMETHODIMP CCMediaStream::GetStreamState(MF_STREAM_STATE* value)
{
    if (!value) return E_POINTER;
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    *value = m_state;
    return S_OK;
}

void CCMediaStream::InitAllocatorLocked(IMFMediaType* type)
{
    m_alloc.Reset();
    ComPtr<IMFVideoSampleAllocatorEx> a;
    if (FAILED(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&a))) || !a) return;
    ComPtr<IMFAttributes> at;
    if (FAILED(MFCreateAttributes(&at, 1))) return;
    at->SetUINT32(MF_SA_BUFFERS_PER_SAMPLE, 1);
    // No D3D manager supplied -> system-memory NV12 samples with IMF2DBuffer2 buffers.
    const HRESULT hr = a->InitializeSampleAllocatorEx(4, 16, at.Get(), type);
    if (SUCCEEDED(hr)) m_alloc = a;
    else CcLog(L"InitializeSampleAllocatorEx failed 0x%08X; using per-frame buffers", static_cast<unsigned>(hr));
}

HRESULT CCMediaStream::Start(IMFMediaType* type, const PROPVARIANT* startPos)
{
    UINT32 w = 0, h = 0, fn = 0, fd = 0;
    if (type)
    {
        MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &w, &h);
        MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &fn, &fd);
    }
    if (w == 0 || h == 0) { w = kModes[0].w; h = kModes[0].h; }
    if (fn == 0 || fd == 0) { fn = kModes[0].fps; fd = 1; }
    if ((w & 1) || (h & 1) || w > CABOCAM_MAX_WIDTH || h > CABOCAM_MAX_HEIGHT)
        return MF_E_INVALIDMEDIATYPE;

    {
        CcAutoLock l(m_cs);
        if (m_shutdown) return MF_E_SHUTDOWN;
        m_w = w; m_h = h; m_fpsNum = fn; m_fpsDen = fd;
        m_frameDur = static_cast<LONGLONG>(10000000ll * fd / fn);
        if (m_frameDur <= 0) m_frameDur = 333333;
        m_type = type;
        InitAllocatorLocked(type);
        m_pending.clear();
        m_state = MF_STREAM_STATE_RUNNING;
        m_start = MFGetSystemTime();
        m_nextDue = m_start;
        if (!m_thread.joinable())
        {
            m_quit = false;
            m_thread = std::thread([this] { WorkerLoop(); });
        }
        m_events->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, startPos);
    }
    m_shm->SetStreaming(true);
    CcLog(L"stream started %ux%u @ %u/%u", w, h, fn, fd);
    SetEvent(m_wake);
    return S_OK;
}

HRESULT CCMediaStream::Stop()
{
    {
        CcAutoLock l(m_cs);
        if (m_shutdown) return MF_E_SHUTDOWN;
        m_state = MF_STREAM_STATE_STOPPED;
        m_pending.clear();
        m_events->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }
    m_shm->SetStreaming(false);
    CcLog(L"stream stopped");
    SetEvent(m_wake);
    return S_OK;
}

void CCMediaStream::Shutdown()
{
    {
        CcAutoLock l(m_cs);
        if (m_shutdown) return;
        m_shutdown = true;
        m_quit = true;
        m_state = MF_STREAM_STATE_STOPPED;
        m_pending.clear();
    }
    if (m_wake) SetEvent(m_wake);
    if (m_thread.joinable()) m_thread.join();
    if (m_shm) m_shm->SetStreaming(false);
    if (m_events) m_events->Shutdown();
    {
        CcAutoLock l(m_cs);
        m_parent.Reset();    // breaks the source <-> stream reference cycle
        m_alloc.Reset();
        m_type.Reset();
    }
}

HRESULT CCMediaStream::BuildSample(UINT32 w, UINT32 h, const ComPtr<IMFVideoSampleAllocatorEx>& alloc,
                                   IMFSample** out)
{
    ComPtr<IMFSample> sample;
    if (alloc && FAILED(alloc->AllocateSample(&sample)))
        sample.Reset();   // pool exhausted (consumer holds many samples): allocate one-off below
    if (!sample)
    {
        ComPtr<IMFMediaBuffer> nb;
        HRESULT hr = MFCreate2DMediaBuffer(w, h, kFourccNV12, FALSE, &nb);
        if (FAILED(hr)) hr = MFCreateMemoryBuffer(w * h * 3 / 2, &nb);
        if (FAILED(hr)) return hr;
        hr = MFCreateSample(&sample);
        if (FAILED(hr)) return hr;
        hr = sample->AddBuffer(nb.Get());
        if (FAILED(hr)) return hr;
    }

    ComPtr<IMFMediaBuffer> buf;
    HRESULT hr = sample->GetBufferByIndex(0, &buf);
    if (FAILED(hr)) return hr;

    BYTE* scan0 = nullptr;
    LONG pitch = 0;
    bool locked2d = false;
    bool lockedPlain = false;
    DWORD capacity = 0;

    ComPtr<IMF2DBuffer2> b2;
    if (SUCCEEDED(buf.As(&b2)))
    {
        BYTE* start = nullptr;
        DWORD len = 0;
        if (SUCCEEDED(b2->Lock2DSize(MF2DBuffer_LockFlags_Write, &scan0, &pitch, &start, &len)))
        {
            locked2d = true;
            capacity = len;
        }
    }
    if (!locked2d)
    {
        BYTE* p = nullptr;
        DWORD maxLen = 0, curLen = 0;
        hr = buf->Lock(&p, &maxLen, &curLen);
        if (FAILED(hr)) return hr;
        lockedPlain = true;
        scan0 = p;
        pitch = static_cast<LONG>(w);
        capacity = maxLen;
    }

    HRESULT result = S_OK;
    const size_t needed = static_cast<size_t>(pitch > 0 ? pitch : 0) * h * 3 / 2;
    if (pitch <= 0 || needed > capacity)
    {
        CcLog(L"bad destination buffer: pitch=%ld capacity=%lu needed=%zu", pitch, capacity, needed);
        result = E_UNEXPECTED;
    }
    else
    {
        // NV12: Y plane (h rows) is followed by the interleaved UV plane (h/2 rows), same pitch.
        m_shm->Render(scan0, pitch, scan0 + static_cast<size_t>(pitch) * h, pitch,
                      static_cast<int>(w), static_cast<int>(h));
    }

    if (locked2d) b2->Unlock2D();
    if (lockedPlain) buf->Unlock();
    if (FAILED(result)) return result;

    buf->SetCurrentLength(static_cast<DWORD>(needed));
    *out = sample.Detach();
    return S_OK;
}

void CCMediaStream::WorkerLoop()
{
    const HRESULT coHr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    for (;;)
    {
        bool ready = false;
        LONGLONG due = 0;
        LONGLONG dur = 0;
        {
            CcAutoLock l(m_cs);
            if (m_quit) break;
            ready = (m_state == MF_STREAM_STATE_RUNNING && !m_pending.empty());
            due = m_nextDue;
            dur = m_frameDur;
        }
        if (!ready)
        {
            WaitForSingleObject(m_wake, INFINITE);
            continue;
        }

        LONGLONG now = MFGetSystemTime();
        if (now - due > 2 * dur) due = now;   // fell behind (idle consumer): resync, no burst
        if (due > now)
        {
            const LONGLONG rel = due - now;
            if (m_timer)
            {
                LARGE_INTEGER li;
                li.QuadPart = -rel;
                SetWaitableTimer(m_timer, &li, 0, nullptr, nullptr, FALSE);
                HANDLE hs[2] = { m_wake, m_timer };
                const DWORD r = WaitForMultipleObjects(2, hs, FALSE, INFINITE);
                if (r == WAIT_OBJECT_0) { CancelWaitableTimer(m_timer); continue; }
            }
            else
            {
                const DWORD ms = static_cast<DWORD>((rel + 9999) / 10000);
                if (WaitForSingleObject(m_wake, ms) == WAIT_OBJECT_0) continue;
            }
        }

        ComPtr<IUnknown> token;
        ComPtr<IMFVideoSampleAllocatorEx> alloc;
        UINT32 w = 0, h = 0;
        LONGLONG start = 0;
        {
            CcAutoLock l(m_cs);
            if (m_quit) break;
            if (m_state != MF_STREAM_STATE_RUNNING || m_pending.empty()) continue;
            token = std::move(m_pending.front());
            m_pending.pop_front();
            w = m_w; h = m_h; dur = m_frameDur; start = m_start;
            alloc = m_alloc;
            m_nextDue = due + dur;
        }

        m_shm->Heartbeat();

        ComPtr<IMFSample> sample;
        const HRESULT hr = BuildSample(w, h, alloc, &sample);
        if (FAILED(hr))
        {
            CcLog(L"BuildSample failed 0x%08X", static_cast<unsigned>(hr));
            continue;
        }
        LONGLONG t = MFGetSystemTime() - start;
        if (t < 0) t = 0;
        sample->SetSampleTime(t);
        sample->SetSampleDuration(dur);
        sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
        sample->DeleteItem(MFSampleExtension_Token);
        if (token) sample->SetUnknown(MFSampleExtension_Token, token.Get());

        // m_events is created in Init and never replaced, so it is safe to use unlocked;
        // after Shutdown the queue itself returns MF_E_SHUTDOWN.
        m_events->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
    }

    if (SUCCEEDED(coHr)) CoUninitialize();
}

// ===========================================================================
// CCMediaSource
// ===========================================================================

CCMediaSource::CCMediaSource() { InterlockedIncrement(&g_dllRefCount); }
CCMediaSource::~CCMediaSource() { InterlockedDecrement(&g_dllRefCount); }

HRESULT CCMediaSource::Create(IMFMediaSourceEx** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    CCMediaSource* s = new (std::nothrow) CCMediaSource();
    if (!s) return E_OUTOFMEMORY;
    const HRESULT hr = s->Init();
    if (FAILED(hr))
    {
        s->Shutdown();
        s->Release();
        return hr;
    }
    *out = static_cast<IMFMediaSourceEx*>(s);   // refcount 1 transferred
    return S_OK;
}

HRESULT CCMediaSource::Init()
{
    m_shm = std::make_shared<SharedFrameSource>();
    m_shm->Open();

    HRESULT hr = MFCreateEventQueue(&m_events);
    if (FAILED(hr)) return hr;

    hr = MFCreateAttributes(&m_attrs, 2);
    if (FAILED(hr)) return hr;
    m_attrs->SetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, CABOCAM_FRIENDLY_NAME);

    ComPtr<IMFMediaType> types[_countof(kModes)];
    IMFMediaType* raw[_countof(kModes)] = {};
    for (DWORD i = 0; i < kModeCount; ++i)
    {
        hr = CreateNv12Type(kModes[i], &types[i]);
        if (FAILED(hr)) return hr;
        raw[i] = types[i].Get();
    }

    hr = MFCreateStreamDescriptor(0, kModeCount, raw, &m_sd);
    if (FAILED(hr)) return hr;

    ComPtr<IMFMediaTypeHandler> handler;
    hr = m_sd->GetMediaTypeHandler(&handler);
    if (FAILED(hr)) return hr;
    hr = handler->SetCurrentMediaType(raw[0]);
    if (FAILED(hr)) return hr;

    hr = m_sd->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, kPinNameVideoCapture);
    if (SUCCEEDED(hr)) hr = m_sd->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
    if (SUCCEEDED(hr)) hr = m_sd->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
    if (SUCCEEDED(hr)) hr = m_sd->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);
    if (FAILED(hr)) return hr;

    IMFStreamDescriptor* sds[1] = { m_sd.Get() };
    hr = MFCreatePresentationDescriptor(1, sds, &m_pd);
    if (FAILED(hr)) return hr;
    hr = m_pd->SelectStream(0);
    if (FAILED(hr)) return hr;

    hr = CCMediaStream::Create(this, m_sd.Get(), m_shm, &m_stream);
    if (FAILED(hr)) return hr;
    m_streamIface.Attach(static_cast<IMFMediaStream2*>(m_stream));   // takes the creation ref
    CcLog(L"media source created");
    return S_OK;
}

STDMETHODIMP CCMediaSource::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IMFMediaEventGenerator)) ||
        IsEqualIID(riid, __uuidof(IMFMediaSource)) || IsEqualIID(riid, __uuidof(IMFMediaSourceEx)))
        *ppv = static_cast<IMFMediaSourceEx*>(this);
    else if (IsEqualIID(riid, __uuidof(IMFGetService)))
        *ppv = static_cast<IMFGetService*>(this);
    else if (IsEqualIID(riid, __uuidof(IKsControl)))
        *ppv = static_cast<IKsControl*>(this);
    else if (IsEqualIID(riid, __uuidof(IMFSampleAllocatorControl)))
        *ppv = static_cast<IMFSampleAllocatorControl*>(this);
    else
    {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    static_cast<IUnknown*>(*ppv)->AddRef();
    return S_OK;
}

STDMETHODIMP CCMediaSource::GetCharacteristics(DWORD* pdwCharacteristics)
{
    if (!pdwCharacteristics) return E_POINTER;
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    *pdwCharacteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

STDMETHODIMP CCMediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** ppPD)
{
    if (!ppPD) return E_POINTER;
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    return m_pd->Clone(ppPD);
}

STDMETHODIMP CCMediaSource::Start(IMFPresentationDescriptor* pPD, const GUID* pguidTimeFormat,
                                  const PROPVARIANT* pvarStartPosition)
{
    if (!pPD || !pvarStartPosition) return E_POINTER;
    if (pguidTimeFormat && !IsEqualGUID(*pguidTimeFormat, GUID_NULL))
        return MF_E_UNSUPPORTED_TIME_FORMAT;
    if (pvarStartPosition->vt != VT_EMPTY && pvarStartPosition->vt != VT_I8)
        return MF_E_UNSUPPORTED_TIME_FORMAT;

    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;

    DWORD count = 0;
    HRESULT hr = pPD->GetStreamDescriptorCount(&count);
    if (FAILED(hr)) return hr;
    if (count != 1) return MF_E_INVALIDREQUEST;

    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> sd;
    hr = pPD->GetStreamDescriptorByIndex(0, &selected, &sd);
    if (FAILED(hr)) return hr;
    if (!selected) return MF_E_INVALIDREQUEST;

    ComPtr<IMFMediaTypeHandler> handler;
    ComPtr<IMFMediaType> type;
    hr = sd->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->GetCurrentMediaType(&type);
    if (FAILED(hr)) return hr;

    // Reflect the negotiated type in our own descriptors (best effort).
    ComPtr<IMFMediaTypeHandler> ours;
    if (SUCCEEDED(m_sd->GetMediaTypeHandler(&ours))) ours->SetCurrentMediaType(type.Get());

    const MediaEventType ev = m_streamAnnounced ? MEUpdatedStream : MENewStream;
    m_streamAnnounced = true;
    hr = m_events->QueueEventParamUnk(ev, GUID_NULL, S_OK, m_streamIface.Get());
    if (FAILED(hr)) return hr;

    hr = m_stream->Start(type.Get(), pvarStartPosition);    // queues MEStreamStarted
    if (FAILED(hr)) return hr;

    return m_events->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, pvarStartPosition);
}

STDMETHODIMP CCMediaSource::Stop()
{
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    const HRESULT hr = m_stream->Stop();    // queues MEStreamStopped
    if (FAILED(hr)) return hr;
    return m_events->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

STDMETHODIMP CCMediaSource::Pause()
{
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    return MF_E_INVALID_STATE_TRANSITION;   // live source: pause is not supported
}

STDMETHODIMP CCMediaSource::Shutdown()
{
    ComPtr<IMFMediaStream2> keepAlive;
    CCMediaStream* stream = nullptr;
    {
        CcAutoLock l(m_cs);
        if (m_shutdown) return MF_E_SHUTDOWN;
        m_shutdown = true;
        stream = m_stream;
        keepAlive = std::move(m_streamIface);
        m_stream = nullptr;
    }
    // Outside the source lock: joins the stream's worker thread.
    if (stream) stream->Shutdown();
    if (m_events) m_events->Shutdown();
    {
        CcAutoLock l(m_cs);
        m_pd.Reset();
        m_sd.Reset();
        m_attrs.Reset();
    }
    CcLog(L"media source shut down");
    return S_OK;    // keepAlive released here
}

STDMETHODIMP CCMediaSource::GetSourceAttributes(IMFAttributes** ppAttributes)
{
    if (!ppAttributes) return E_POINTER;
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    *ppAttributes = m_attrs.Get();
    (*ppAttributes)->AddRef();
    return S_OK;
}

STDMETHODIMP CCMediaSource::GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes)
{
    if (!ppAttributes) return E_POINTER;
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    if (dwStreamIdentifier != 0) return MF_E_INVALIDSTREAMNUMBER;
    // A stream descriptor is itself an IMFAttributes holding the MF_DEVICESTREAM_* keys.
    return m_sd.CopyTo(ppAttributes);
}

STDMETHODIMP CCMediaSource::SetD3DManager(IUnknown*)
{
    CcAutoLock l(m_cs);
    if (m_shutdown) return MF_E_SHUTDOWN;
    return S_OK;    // we produce system-memory samples; the manager is ignored
}

STDMETHODIMP CCMediaSource::GetService(REFGUID, REFIID, LPVOID* ppvObject)
{
    if (ppvObject) *ppvObject = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

STDMETHODIMP CCMediaSource::SetDefaultAllocator(DWORD, IUnknown*)
{
    return S_OK;    // accepted and ignored: we allocate our own samples (see GetAllocatorUsage)
}

STDMETHODIMP CCMediaSource::GetAllocatorUsage(DWORD dwOutputStreamID, DWORD* pdwInputStreamID,
                                              MFSampleAllocatorUsage* peUsage)
{
    if (!pdwInputStreamID || !peUsage) return E_POINTER;
    *pdwInputStreamID = dwOutputStreamID;
    *peUsage = MFSampleAllocatorUsage_UsesCustomAllocator;
    return S_OK;
}
