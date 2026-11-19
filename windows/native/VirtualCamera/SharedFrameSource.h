#pragma once
#include "vcam_common.h"
#include "cabocam_shm.h"
#include "Nv12Scaler.h"
#include <mutex>

// Reader side of the shared-memory hand-off (see cabocam_shared.h).
// Render() is intended to be called from a single worker thread; SetStreaming() and
// Heartbeat() may be called from any thread.
class SharedFrameSource
{
public:
    SharedFrameSource();
    ~SharedFrameSource();

    // Creates (preferred) or opens Global\CaboCamFrames_v1 + event. Failure is non-fatal.
    void Open();

    void SetStreaming(bool on);
    void Heartbeat();

    // Writes an NV12 frame of dstW x dstH into the destination planes. Uses the latest
    // shared frame (crop-scaled if its size differs) or dark gray if none is fresh.
    // Returns true when a real frame was used.
    bool Render(uint8_t* dstY, int pitchY, uint8_t* dstUV, int pitchUV, int dstW, int dstH);

private:
    bool CopyLatestLocked();   // m_mtx held; returns true if m_local holds a fresh frame

    std::mutex m_mtx;
    CcShm m_shm;
    ULONGLONG m_lastOpenTry = 0;

    std::vector<uint8_t> m_local;
    int m_lw = 0;
    int m_lh = 0;
    int64_t m_lastFrameCounter = -1;
    LARGE_INTEGER m_freq{};
    Nv12Scaler m_scaler;
};
