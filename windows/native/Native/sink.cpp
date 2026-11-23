#include <windows.h>
#include <cstring>
#include <mutex>

#include "cabocam_shared.h"
#include "cabocam_native.h"
#include "cabocam_shm.h"

namespace
{
    std::mutex g_mutex;
    CcShm g_shm;

    // Caller holds g_mutex.
    bool OpenLocked()
    {
        if (g_shm.hdr)
        {
            CcShmEnsureEvent(g_shm, CcIsElevated());
            return true;
        }
        // Prefer opening what the Frame Server created; create ourselves only if elevated
        // (SeCreateGlobalPrivilege) and nobody has created it yet.
        if (CcShmOpenOrCreate(g_shm, /*allowCreate*/ false)) return true;
        if (CcIsElevated() && CcShmOpenOrCreate(g_shm, /*allowCreate*/ true))
        {
            CcLog(L"sink created shared memory (elevated)");
            return true;
        }
        return false;
    }
}

CC_API int32_t cc_sink_open(void)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    return OpenLocked() ? 1 : 0;
}

CC_API int32_t cc_sink_write_nv12(const uint8_t* data, int32_t width, int32_t height,
                                  int32_t stride, int64_t timestampHns)
{
    if (!data || width <= 0 || height <= 0 || (width & 1) || (height & 1) ||
        !CABOCAM_FRAME_FITS(static_cast<uint32_t>(width), static_cast<uint32_t>(height)) ||
        stride < width)
        return E_INVALIDARG;

    std::lock_guard<std::mutex> lk(g_mutex);
    if (!g_shm.hdr && !OpenLocked()) return 0;
    CaboCamShmHeader* h = g_shm.hdr;
    if (h->magic != CABOCAM_SHM_MAGIC)
    {
        // Creator has not published the header yet (or foreign data): treat as not ready.
        return 0;
    }

    uint8_t* dst = reinterpret_cast<uint8_t*>(h) + CABOCAM_SHM_HEADER_SIZE;
    const size_t w = static_cast<size_t>(width);
    const size_t ht = static_cast<size_t>(height);
    const uint8_t* srcY = data;
    const uint8_t* srcUV = data + static_cast<size_t>(stride) * ht;

    // Seqlock: make sure the counter is even before starting (a crashed writer may have
    // left it odd), then odd while writing, even when done.
    if (h->seq & 1) InterlockedIncrement64(&h->seq);
    InterlockedIncrement64(&h->seq);          // odd; full barrier
    MemoryBarrier();

    h->width = static_cast<uint32_t>(width);
    h->height = static_cast<uint32_t>(height);
    h->dataSize = static_cast<uint32_t>(w * ht * 3 / 2);
    h->timestampHns = timestampHns;
    if (static_cast<size_t>(stride) == w)
    {
        std::memcpy(dst, srcY, w * ht);
        std::memcpy(dst + w * ht, srcUV, w * ht / 2);
    }
    else
    {
        for (size_t y = 0; y < ht; ++y)
            std::memcpy(dst + y * w, srcY + y * static_cast<size_t>(stride), w);
        uint8_t* dstUV = dst + w * ht;
        for (size_t y = 0; y < ht / 2; ++y)
            std::memcpy(dstUV + y * w, srcUV + y * static_cast<size_t>(stride), w);
    }

    MemoryBarrier();
    InterlockedIncrement64(&h->frameCounter);
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    InterlockedExchange64(&h->writerHeartbeatQpc, now.QuadPart);
    InterlockedIncrement64(&h->seq);          // even; readers may accept the frame now
    if (g_shm.evt) SetEvent(g_shm.evt);
    return 1;
}

CC_API int32_t cc_sink_consumer_active(void)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    if (!g_shm.hdr && !OpenLocked()) return 0;
    const CaboCamShmHeader* h = g_shm.hdr;
    if (h->readerStreaming != 1) return 0;
    LARGE_INTEGER now, freq;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    const int64_t hb = h->readerHeartbeatQpc;
    if (hb == 0) return 0;
    return (now.QuadPart - hb) <= 2 * freq.QuadPart ? 1 : 0;
}

CC_API void cc_sink_close(void)
{
    std::lock_guard<std::mutex> lk(g_mutex);
    g_shm.Close();
}
