#pragma once
#include <cstdint>
#include <vector>

// Center-crop (to the destination aspect ratio) + bilinear scale of an NV12 frame.
// Fixed-point (8-bit weights), row-pointer based. Vertical blend is done first into a
// scratch row (sequential reads), then horizontal gather -> cheap for 4K -> 1080p.
// Not thread-safe (owns scratch buffers); use one instance per thread.
class Nv12Scaler
{
public:
    // dstW/dstH must be even and > 0; srcW/srcH even and > 0.
    // dstUV points to the interleaved UV plane (dstH/2 rows).
    void Scale(const uint8_t* srcY, int srcStrideY, const uint8_t* srcUV, int srcStrideUV,
               int srcW, int srcH,
               uint8_t* dstY, int dstStrideY, uint8_t* dstUV, int dstStrideUV,
               int dstW, int dstH);

private:
    // `unitsW`/`cropH` describe the cropped source region; one unit = `ch` bytes.
    void ScalePlane(const uint8_t* src, int srcStride, int cropX, int cropY, int unitsW, int cropH,
                    int ch, uint8_t* dst, int dstStride, int dstUnitsW, int dstH);
    template <int CH>
    void ScalePlaneT(const uint8_t* src, int srcStride, int cropX, int cropY, int unitsW, int cropH,
                     uint8_t* dst, int dstStride, int dstUnitsW, int dstH);

    std::vector<uint32_t> m_x0;
    std::vector<uint32_t> m_x1;
    std::vector<uint16_t> m_fx;
    std::vector<uint8_t>  m_row;
};
