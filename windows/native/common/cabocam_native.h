// CaboCam.Native.dll — flat C API consumed by CaboCam.exe (C#) via P/Invoke.
// All functions are __cdecl, exported with extern "C". Return codes: >= 0 success
// (meaning documented per function), < 0 = HRESULT-style failure.
// Thread-safety: each cc_decoder is single-threaded; vcam/sink functions are
// internally synchronized.
#pragma once
#include <stdint.h>

#ifdef CABOCAM_NATIVE_EXPORTS
#define CC_API extern "C" __declspec(dllexport)
#else
#define CC_API extern "C" __declspec(dllimport)
#endif

// ---- Virtual camera lifecycle ------------------------------------------------
// Creates (MFCreateVirtualCamera: SoftwareCameraSource, Lifetime Session, Access
// CurrentUser, friendly name "CaboCam", source CLSID from cabocam_shared.h) and
// Start()s the virtual camera. Keeps the IMFVirtualCamera alive until
// cc_vcam_unregister or process exit. Calls MFStartup internally.
// Returns 0 on success, or the failing HRESULT (e.g. REGDB_E_CLASSNOTREG when the
// COM DLL was not registered by install.ps1; 0x80070005 on Windows < 11 / access).
CC_API int32_t cc_vcam_register(void);
CC_API int32_t cc_vcam_unregister(void);               // HRESULT of IMFVirtualCamera::Remove (0 if none)

// Diagnostics: receive native log lines (UTF-16, no prefix/newline). nullptr disables.
CC_API void    cc_set_log_callback(void (__cdecl *callback)(const wchar_t* message));
CC_API int32_t cc_vcam_is_registered(void);           // 1 / 0

// ---- Frame sink (writer side of the shared memory) ---------------------------
// Tries to open Global\CaboCamFrames_v1 (+ event). If it does not exist and the
// process is elevated, creates it. Cheap to call repeatedly (no-op when open).
// Returns 1 if the sink is open, 0 if not available yet.
CC_API int32_t cc_sink_open(void);
// Writes one NV12 frame (Y plane rows of `stride` bytes followed by UV plane).
// Width/height must be even and <= 3840x2160. Copies into the shared memory with
// stride = width. Returns 1 if written, 0 if the sink is not open (call
// cc_sink_open later), < 0 on bad arguments.
CC_API int32_t cc_sink_write_nv12(const uint8_t* data, int32_t width, int32_t height,
                                  int32_t stride, int64_t timestampHns);
// 1 if a consumer app currently has the CaboCam stream running
// (readerStreaming == 1 and readerHeartbeat within the last 2 s), else 0.
CC_API int32_t cc_sink_consumer_active(void);
CC_API void    cc_sink_close(void);

// ---- Decoder (Media Foundation H.264 / HEVC decoder MFT) ---------------------
#define CC_CODEC_H264 1
#define CC_CODEC_HEVC 2
typedef struct cc_decoder cc_decoder;
// Prefers a hardware decoder MFT (D3D11-free: system-memory output is fine), falls
// back to software. Sets low-latency mode. Output subtype NV12.
// Returns NULL on failure (e.g. HEVC extension not installed).
CC_API cc_decoder* cc_decoder_create(int32_t codec);
// Feeds one Annex-B access unit. Returns 0 on success, < 0 on failure (caller
// should then request a keyframe).
CC_API int32_t cc_decoder_push(cc_decoder* dec, const uint8_t* annexb, int32_t length,
                               int64_t ptsHns);
// Pulls one decoded frame if available. Output is tightly packed NV12 cropped to the
// display size (stride == width, no 1088 padding rows).
// Returns 1 if a frame was written, 0 if no frame is ready, < 0 on error.
// If `capacity` is too small returns -2 and sets *width/*height so the caller can
// grow the buffer (width*height*3/2 bytes needed).
CC_API int32_t cc_decoder_pull(cc_decoder* dec, uint8_t* nv12Out, int32_t capacity,
                               int32_t* width, int32_t* height, int64_t* ptsHns);
CC_API void    cc_decoder_destroy(cc_decoder* dec);

// ---- Utility -----------------------------------------------------------------
// Converts tightly packed NV12 (stride == width) to BGRA32 with bilinear-ish
// scaling to outWidth x outHeight (BT.709 limited range). bgraOut must hold
// outWidth*outHeight*4 bytes. Returns 0 on success.
CC_API int32_t cc_nv12_to_bgra(const uint8_t* nv12, int32_t width, int32_t height,
                               uint8_t* bgraOut, int32_t outWidth, int32_t outHeight);
