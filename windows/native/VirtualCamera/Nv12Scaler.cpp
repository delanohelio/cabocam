#include "Nv12Scaler.h"
#include <algorithm>
#include <cstring>

void Nv12Scaler::Scale(const uint8_t* srcY, int srcStrideY, const uint8_t* srcUV, int srcStrideUV,
                       int srcW, int srcH,
                       uint8_t* dstY, int dstStrideY, uint8_t* dstUV, int dstStrideUV,
                       int dstW, int dstH)
{
    if (srcW < 2 || srcH < 2 || dstW < 2 || dstH < 2) return;

    int cropW = srcW;
    int cropH = srcH;
    const int64_t lhs = static_cast<int64_t>(srcW) * dstH;
    const int64_t rhs = static_cast<int64_t>(dstW) * srcH;
    if (lhs > rhs)        // source is wider than target: crop columns
        cropW = static_cast<int>((static_cast<int64_t>(srcH) * dstW / dstH) & ~1LL);
    else if (lhs < rhs)   // source is taller: crop rows
        cropH = static_cast<int>((static_cast<int64_t>(srcW) * dstH / dstW) & ~1LL);
    cropW = std::max(2, std::min(cropW, srcW));
    cropH = std::max(2, std::min(cropH, srcH));
    const int cropX = ((srcW - cropW) / 2) & ~1;
    const int cropY = ((srcH - cropH) / 2) & ~1;

    ScalePlane(srcY, srcStrideY, cropX, cropY, cropW, cropH, 1, dstY, dstStrideY, dstW, dstH);
    // Chroma: one unit = one interleaved U,V pair (2 bytes), half resolution.
    ScalePlane(srcUV, srcStrideUV, cropX / 2, cropY / 2, cropW / 2, cropH / 2, 2,
               dstUV, dstStrideUV, dstW / 2, dstH / 2);
}

void Nv12Scaler::ScalePlane(const uint8_t* src, int srcStride, int cropX, int cropY, int unitsW,
                            int cropH, int ch, uint8_t* dst, int dstStride, int dstUnitsW, int dstH)
{
    if (unitsW < 1 || cropH < 1 || dstUnitsW < 1 || dstH < 1) return;
    if (ch == 1)
        ScalePlaneT<1>(src, srcStride, cropX, cropY, unitsW, cropH, dst, dstStride, dstUnitsW, dstH);
    else
        ScalePlaneT<2>(src, srcStride, cropX, cropY, unitsW, cropH, dst, dstStride, dstUnitsW, dstH);
}

template <int CH>
void Nv12Scaler::ScalePlaneT(const uint8_t* src, int srcStride, int cropX, int cropY, int unitsW,
                             int cropH, uint8_t* dst, int dstStride, int dstUnitsW, int dstH)
{
    const size_t dstBytes = static_cast<size_t>(dstUnitsW) * CH;

    // Fast path: same size, plain row copy.
    if (unitsW == dstUnitsW && cropH == dstH)
    {
        for (int y = 0; y < dstH; ++y)
        {
            const uint8_t* s = src + static_cast<ptrdiff_t>(cropY + y) * srcStride + static_cast<ptrdiff_t>(cropX) * CH;
            std::memcpy(dst + static_cast<ptrdiff_t>(y) * dstStride, s, dstBytes);
        }
        return;
    }

    // Horizontal tables (16.16 positions, pixel-center aligned, 8-bit weights).
    m_x0.resize(dstUnitsW);
    m_x1.resize(dstUnitsW);
    m_fx.resize(dstUnitsW);
    const int64_t stepX = (static_cast<int64_t>(unitsW) << 16) / dstUnitsW;
    for (int x = 0; x < dstUnitsW; ++x)
    {
        int64_t pos = static_cast<int64_t>(x) * stepX + stepX / 2 - 32768;
        if (pos < 0) pos = 0;
        int64_t x0 = pos >> 16;
        uint32_t fx = static_cast<uint32_t>((pos & 0xFFFF) >> 8);
        int64_t x1 = x0 + 1;
        if (x0 >= unitsW - 1) { x0 = unitsW - 1; x1 = x0; fx = 0; }
        else if (x1 > unitsW - 1) { x1 = unitsW - 1; }
        m_x0[x] = static_cast<uint32_t>(x0) * CH;
        m_x1[x] = static_cast<uint32_t>(x1) * CH;
        m_fx[x] = static_cast<uint16_t>(fx);
    }

    const size_t rowBytes = static_cast<size_t>(unitsW) * CH;
    if (m_row.size() < rowBytes) m_row.resize(rowBytes);
    uint8_t* const rowBuf = m_row.data();

    const int64_t stepY = (static_cast<int64_t>(cropH) << 16) / dstH;
    const uint8_t* const base = src + static_cast<ptrdiff_t>(cropX) * CH;

    for (int y = 0; y < dstH; ++y)
    {
        int64_t pos = static_cast<int64_t>(y) * stepY + stepY / 2 - 32768;
        if (pos < 0) pos = 0;
        int64_t y0 = pos >> 16;
        uint32_t fy = static_cast<uint32_t>((pos & 0xFFFF) >> 8);
        int64_t y1 = y0 + 1;
        if (y0 >= cropH - 1) { y0 = cropH - 1; y1 = y0; fy = 0; }
        else if (y1 > cropH - 1) { y1 = cropH - 1; }

        const uint8_t* r0 = base + static_cast<ptrdiff_t>(cropY + y0) * srcStride;
        const uint8_t* r1 = base + static_cast<ptrdiff_t>(cropY + y1) * srcStride;

        const uint8_t* line;
        if (fy == 0)
        {
            line = r0;
        }
        else
        {
            const uint32_t w0 = 256 - fy;
            for (size_t i = 0; i < rowBytes; ++i)
                rowBuf[i] = static_cast<uint8_t>((r0[i] * w0 + r1[i] * fy + 128) >> 8);
            line = rowBuf;
        }

        uint8_t* d = dst + static_cast<ptrdiff_t>(y) * dstStride;
        for (int x = 0; x < dstUnitsW; ++x)
        {
            const uint32_t fx = m_fx[x];
            const uint32_t wx0 = 256 - fx;
            const uint8_t* p0 = line + m_x0[x];
            const uint8_t* p1 = line + m_x1[x];
            for (int c = 0; c < CH; ++c)
                d[x * CH + c] = static_cast<uint8_t>((p0[c] * wx0 + p1[c] * fx + 128) >> 8);
        }
    }
}
