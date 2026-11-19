#include <windows.h>
#include <vector>
#include "cabocam_native.h"

namespace
{
    inline uint8_t Clamp8(int v)
    {
        return static_cast<uint8_t>(v < 0 ? 0 : (v > 255 ? 255 : v));
    }
}

// NV12 (limited range, BT.709) -> BGRA. Nearest-neighbour scaling with precomputed column
// map: this is only for the WPF preview, so speed beats quality.
CC_API int32_t cc_nv12_to_bgra(const uint8_t* nv12, int32_t width, int32_t height,
                               uint8_t* bgraOut, int32_t outWidth, int32_t outHeight)
{
    if (!nv12 || !bgraOut || width <= 0 || height <= 0 || (width & 1) || (height & 1) ||
        outWidth <= 0 || outHeight <= 0)
        return E_INVALIDARG;

    const uint8_t* yPlane = nv12;
    const uint8_t* uvPlane = nv12 + static_cast<size_t>(width) * height;

    std::vector<uint32_t> xMap(static_cast<size_t>(outWidth));
    for (int x = 0; x < outWidth; ++x)
        xMap[x] = static_cast<uint32_t>((static_cast<int64_t>(x) * width) / outWidth);

    for (int y = 0; y < outHeight; ++y)
    {
        const int sy = static_cast<int>((static_cast<int64_t>(y) * height) / outHeight);
        const uint8_t* yRow = yPlane + static_cast<size_t>(sy) * width;
        const uint8_t* uvRow = uvPlane + static_cast<size_t>(sy >> 1) * width;
        uint8_t* out = bgraOut + static_cast<size_t>(y) * outWidth * 4;

        for (int x = 0; x < outWidth; ++x)
        {
            const uint32_t sx = xMap[x];
            const int c = static_cast<int>(yRow[sx]) - 16;
            const int d = static_cast<int>(uvRow[sx & ~1u]) - 128;       // U
            const int e = static_cast<int>(uvRow[(sx & ~1u) + 1]) - 128; // V
            const int c298 = 298 * c + 128;
            out[0] = Clamp8((c298 + 541 * d) >> 8);             // B
            out[1] = Clamp8((c298 - 55 * d - 136 * e) >> 8);    // G
            out[2] = Clamp8((c298 + 459 * e) >> 8);             // R
            out[3] = 255;
            out += 4;
        }
    }
    return 0;
}
