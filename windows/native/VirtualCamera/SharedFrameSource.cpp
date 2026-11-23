#include "SharedFrameSource.h"

SharedFrameSource::SharedFrameSource()
{
    QueryPerformanceFrequency(&m_freq);
    if (m_freq.QuadPart <= 0) m_freq.QuadPart = 10000000;
    m_local.reserve(CABOCAM_SHM_DATA_CAPACITY);
}

SharedFrameSource::~SharedFrameSource()
{
    std::lock_guard<std::mutex> lk(m_mtx);
    if (m_shm.hdr)
    {
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_shm.hdr->readerStreaming), 0);
    }
    m_shm.Close();
}

void SharedFrameSource::Open()
{
    std::lock_guard<std::mutex> lk(m_mtx);
    m_lastOpenTry = GetTickCount64();
    if (CcShmOpenOrCreate(m_shm, /*allowCreate*/ true))
        CcLog(L"shared memory %s (%s)", m_shm.created ? L"created" : L"opened",
              m_shm.evt ? L"event ok" : L"no event");
    else
        CcLog(L"shared memory unavailable (error %lu); will retry", GetLastError());
}

void SharedFrameSource::SetStreaming(bool on)
{
    std::lock_guard<std::mutex> lk(m_mtx);
    if (!m_shm.hdr) return;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_shm.hdr->readerStreaming), on ? 1 : 0);
    if (on)
    {
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        InterlockedExchange64(&m_shm.hdr->readerHeartbeatQpc, now.QuadPart);
    }
}

void SharedFrameSource::Heartbeat()
{
    std::lock_guard<std::mutex> lk(m_mtx);
    if (!m_shm.hdr) return;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    InterlockedExchange64(&m_shm.hdr->readerHeartbeatQpc, now.QuadPart);
}

bool SharedFrameSource::CopyLatestLocked()
{
    if (!m_shm.hdr)
    {
        const ULONGLONG t = GetTickCount64();
        if (t - m_lastOpenTry < 1000) return false;
        m_lastOpenTry = t;
        if (!CcShmOpenOrCreate(m_shm, true)) return false;
        CcLog(L"shared memory now available");
    }
    CaboCamShmHeader* h = m_shm.hdr;
    if (h->magic != CABOCAM_SHM_MAGIC || h->version != CABOCAM_SHM_VERSION ||
        h->format != CABOCAM_FORMAT_NV12)
        return false;

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const int64_t hb = h->writerHeartbeatQpc;
    if (hb == 0 || now.QuadPart - hb > m_freq.QuadPart)   // no write within the last second
        return false;

    const uint8_t* data = reinterpret_cast<const uint8_t*>(h) + CABOCAM_SHM_HEADER_SIZE;
    for (int attempt = 0; attempt < 6; ++attempt)
    {
        const int64_t s1 = h->seq;
        if (s1 & 1) { YieldProcessor(); Sleep(0); continue; }   // writer in progress
        MemoryBarrier();

        const uint32_t w = h->width;
        const uint32_t ht = h->height;
        const uint32_t ds = h->dataSize;
        const int64_t fc = h->frameCounter;
        if ((w & 1) || (ht & 1) || !CABOCAM_FRAME_FITS(w, ht) ||
            ds != w * ht * 3u / 2u || ds > CABOCAM_SHM_DATA_CAPACITY)
        {
            MemoryBarrier();
            if (h->seq == s1) return false;   // stable but invalid
            continue;
        }

        if (fc == m_lastFrameCounter && m_lw == static_cast<int>(w) && m_lh == static_cast<int>(ht) &&
            m_local.size() == ds)
        {
            MemoryBarrier();
            if (h->seq == s1) return true;    // unchanged: reuse private copy
            continue;
        }

        if (m_local.size() != ds) m_local.resize(ds);
        std::memcpy(m_local.data(), data, ds);
        MemoryBarrier();
        if (h->seq != s1) continue;           // torn read, retry

        m_lw = static_cast<int>(w);
        m_lh = static_cast<int>(ht);
        m_lastFrameCounter = fc;
        return true;
    }
    // Writer kept racing us: fall back to the previous good copy if any.
    return m_lastFrameCounter >= 0 && !m_local.empty();
}

bool SharedFrameSource::Render(uint8_t* dstY, int pitchY, uint8_t* dstUV, int pitchUV, int dstW, int dstH)
{
    bool real;
    {
        std::lock_guard<std::mutex> lk(m_mtx);
        real = CopyLatestLocked();
    }
    if (real)
    {
        const uint8_t* sy = m_local.data();
        const uint8_t* suv = sy + static_cast<size_t>(m_lw) * m_lh;
        m_scaler.Scale(sy, m_lw, suv, m_lw, m_lw, m_lh, dstY, pitchY, dstUV, pitchUV, dstW, dstH);
        return true;
    }
    for (int y = 0; y < dstH; ++y)
        std::memset(dstY + static_cast<ptrdiff_t>(y) * pitchY, 32, static_cast<size_t>(dstW));
    for (int y = 0; y < dstH / 2; ++y)
        std::memset(dstUV + static_cast<ptrdiff_t>(y) * pitchUV, 128, static_cast<size_t>(dstW));
    return false;
}
