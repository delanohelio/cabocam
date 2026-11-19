// CaboCam — shared-memory frame hand-off between CaboCam.Native.dll (writer, in
// CaboCam.exe) and CaboCam.VirtualCamera.dll (reader, inside the Windows Camera
// Frame Server). See docs/ARCHITECTURE.md decision 4.
#pragma once
#include <stdint.h>

// {B66867C7-2F79-49DF-983E-13FAB663FD84}
#define CABOCAM_SOURCE_CLSID_STR L"{B66867C7-2F79-49DF-983E-13FAB663FD84}"
#define CABOCAM_FRIENDLY_NAME    L"CaboCam"

#define CABOCAM_SHM_NAME   L"Global\\CaboCamFrames_v1"
#define CABOCAM_EVENT_NAME L"Global\\CaboCamFrameEvent_v1"   // auto-reset, signaled by writer after each frame

#define CABOCAM_SHM_MAGIC   0x4D43424Fu  // 'OBCM'
#define CABOCAM_SHM_VERSION 1u
#define CABOCAM_FORMAT_NV12 1u

#define CABOCAM_MAX_WIDTH  3840u
#define CABOCAM_MAX_HEIGHT 2160u
#define CABOCAM_SHM_HEADER_SIZE 4096u
#define CABOCAM_SHM_DATA_CAPACITY (CABOCAM_MAX_WIDTH * CABOCAM_MAX_HEIGHT * 3u / 2u)
#define CABOCAM_SHM_TOTAL_SIZE (CABOCAM_SHM_HEADER_SIZE + CABOCAM_SHM_DATA_CAPACITY)

// Layout of the first CABOCAM_SHM_HEADER_SIZE bytes. Frame bytes start at offset
// CABOCAM_SHM_HEADER_SIZE: NV12, Y plane (stride = width) followed by interleaved UV
// plane (stride = width), height/2 rows. width and height are even.
//
// Concurrency: single writer, many readers, seqlock.
//   writer: seq++ (odd) ; MemoryBarrier ; write fields+data ; MemoryBarrier ; seq++ (even) ; SetEvent
//   reader: s1 = seq ; if odd -> retry/skip ; copy ; MemoryBarrier ; s2 = seq ; valid iff s1 == s2
#pragma pack(push, 8)
typedef struct CaboCamShmHeader {
    uint32_t magic;            // CABOCAM_SHM_MAGIC (set by creator)
    uint32_t version;          // CABOCAM_SHM_VERSION
    uint32_t dataCapacity;     // CABOCAM_SHM_DATA_CAPACITY
    uint32_t format;           // CABOCAM_FORMAT_NV12
    volatile int64_t seq;      // seqlock counter (even = stable)
    volatile int64_t frameCounter;     // incremented by writer per frame
    volatile int64_t writerHeartbeatQpc; // QueryPerformanceCounter at last write (writer)
    volatile int64_t readerHeartbeatQpc; // QueryPerformanceCounter, updated by source while streaming
    volatile int32_t readerStreaming;    // 1 while a consumer has the stream running
    uint32_t width;
    uint32_t height;
    uint32_t dataSize;         // width*height*3/2
    int64_t  timestampHns;     // capture timestamp, 100ns units
    uint32_t reserved[64];
} CaboCamShmHeader;
#pragma pack(pop)
