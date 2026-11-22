// H.264 / HEVC decoding through a synchronous Media Foundation decoder MFT.
//
// Design: software (sync) decoder MFTs only. Hardware decoders are async MFTs that need an
// IMFDXGIDeviceManager and an event pump; the header allows system-memory output, and the
// Microsoft H.264 decoder is fast enough for 1080p60 in software with low-latency mode.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <strmif.h>      // ICodecAPI
#include <wrl/client.h>

#include <algorithm>
#include <cstring>
#include <deque>
#include <vector>

#include "cabocam_native.h"
#include "native_common.h"

using Microsoft::WRL::ComPtr;

namespace
{
    // CLSID_CMSH264DecoderMFT {32D186A7-218F-4C75-8876-DD77273A8999} (spelled out to avoid
    // linking wmcodecdspuuid.lib).
    const GUID kClsidMsH264Decoder =
        { 0x32D186A7, 0x218F, 0x4C75, { 0x88, 0x76, 0xDD, 0x77, 0x27, 0x3A, 0x89, 0x99 } };
    // CODECAPI_AVLowLatencyMode {9C27891A-ED7A-40E1-88E8-B22727A024EE}
    const GUID kCodecApiLowLatency =
        { 0x9C27891A, 0xED7A, 0x40E1, { 0x88, 0xE8, 0xB2, 0x27, 0x27, 0xA0, 0x24, 0xEE } };

    const size_t kMaxReadyFrames = 4;

    struct Frame
    {
        std::vector<uint8_t> data;   // tightly packed NV12
        int w = 0;
        int h = 0;
        int64_t pts = 0;
    };

    // Candidate decoders, best first. Registered MFTs come from MFTEnumEx; the direct
    // CoCreateInstance of CLSID_CMSH264DecoderMFT is only a last resort: on recent Windows 11
    // builds it returns an object that rejects every H.264 input type (MF_E_INVALIDMEDIATYPE),
    // while the same decoder activated through MFTEnumEx works.
    std::vector<ComPtr<IMFTransform>> CreateDecoderCandidates(int codec)
    {
        std::vector<ComPtr<IMFTransform>> list;

        MFT_REGISTER_TYPE_INFO in{};
        in.guidMajorType = MFMediaType_Video;
        in.guidSubtype = (codec == CC_CODEC_H264) ? MFVideoFormat_H264 : MFVideoFormat_HEVC;

        IMFActivate** acts = nullptr;
        UINT32 count = 0;
        const HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER,
                                     MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
                                     &in, nullptr, &acts, &count);
        if (SUCCEEDED(hr) && acts)
        {
            for (UINT32 i = 0; i < count; ++i)
            {
                ComPtr<IMFTransform> cand;
                if (SUCCEEDED(acts[i]->ActivateObject(IID_PPV_ARGS(&cand))) && cand)
                    list.push_back(cand);
                acts[i]->Release();
            }
            CoTaskMemFree(acts);
        }

        if (codec == CC_CODEC_H264)
        {
            ComPtr<IMFTransform> direct;
            if (SUCCEEDED(CoCreateInstance(kClsidMsH264Decoder, nullptr, CLSCTX_INPROC_SERVER,
                                           IID_PPV_ARGS(&direct))) && direct)
                list.push_back(direct);
        }
        return list;
    }

    // True when the decoder can already describe an output type for the current input type.
    bool HasOutputTypes(IMFTransform* mft)
    {
        ComPtr<IMFMediaType> t;
        return SUCCEEDED(mft->GetOutputAvailableType(0, 0, &t)) && t;
    }

    HRESULT ConfigureInput(IMFTransform* mft, int codec)
    {
        // Low-latency: both the MF attribute and the CODECAPI property (either may be unsupported).
        ComPtr<IMFAttributes> attrs;
        if (SUCCEEDED(mft->GetAttributes(&attrs)) && attrs)
            attrs->SetUINT32(MF_LOW_LATENCY, TRUE);
        ComPtr<ICodecAPI> api;
        if (SUCCEEDED(mft->QueryInterface(IID_PPV_ARGS(&api))))
        {
            VARIANT v;
            VariantInit(&v);
            v.vt = VT_BOOL;
            v.boolVal = VARIANT_TRUE;
            api->SetValue(&kCodecApiLowLatency, &v);
        }

        const GUID subtype = (codec == CC_CODEC_H264) ? MFVideoFormat_H264 : MFVideoFormat_HEVC;
        // Pass 0: no frame size (the H.264 decoder learns it from the SPS).
        // Pass 1: with a placeholder size. The HEVC decoder (HEVC Video Extensions) accepts an
        // input type without size but then offers no output type, so ProcessInput fails with
        // MF_E_TRANSFORM_TYPE_NOT_SET forever. With a size it offers NV12 and later signals a
        // stream change to the real resolution.
        // HEVC goes straight to the full description: it lists output types without a frame
        // size but then rejects them in SetOutputType (MF_E_ATTRIBUTENOTFOUND).
        HRESULT hr = E_FAIL;
        for (int pass = (codec == CC_CODEC_HEVC) ? 1 : 0; pass < 2; ++pass)
        {
            ComPtr<IMFMediaType> in;
            hr = MFCreateMediaType(&in);
            if (FAILED(hr)) return hr;
            in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            in->SetGUID(MF_MT_SUBTYPE, subtype);
            if (pass == 0)
            {
                in->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_MixedInterlaceOrProgressive);
            }
            else
            {
                // Full description: the HEVC decoder only lists output types with all of these.
                in->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                MFSetAttributeSize(in.Get(), MF_MT_FRAME_SIZE, 1920, 1080);
                MFSetAttributeRatio(in.Get(), MF_MT_FRAME_RATE, 30, 1);
                MFSetAttributeRatio(in.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            }
            hr = mft->SetInputType(0, in.Get(), 0);
            if (SUCCEEDED(hr) && HasOutputTypes(mft)) return hr;
        }
        return hr;
    }

    // Annex-B scan: does this access unit contain an IRAP/IDR (or parameter set)?
    bool IsKeyAccessUnit(int codec, const uint8_t* d, int32_t len)
    {
        for (int32_t i = 0; i + 3 < len; ++i)
        {
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                const uint8_t b = d[i + 3];
                if (codec == CC_CODEC_H264)
                {
                    const int t = b & 0x1F;
                    if (t == 5 || t == 7) return true;
                }
                else
                {
                    const int t = (b >> 1) & 0x3F;
                    if ((t >= 16 && t <= 21) || t == 32) return true;
                }
                i += 2;
            }
        }
        return false;
    }
}

struct cc_decoder
{
    int codec = 0;
    ComPtr<IMFTransform> mft;

    // Current output format
    UINT32 frameW = 0, frameH = 0;           // buffer dimensions (may be 1088 high)
    UINT32 cropX = 0, cropY = 0, cropW = 0, cropH = 0;   // display aperture (even values)
    LONG stride = 0;                         // fallback stride for non-2D buffers
    bool providesSamples = false;
    MFT_OUTPUT_STREAM_INFO outInfo{};
    ComPtr<IMFSample> outSample;             // caller-allocated output sample
    DWORD outSampleCap = 0;

    std::deque<Frame> ready;
    std::vector<uint8_t> spare;
    int64_t lastPts = 0;

    HRESULT NegotiateOutput();
    HRESULT EnsureOutSample();
    HRESULT ProduceOne(Frame& f, bool& got);
    HRESULT CopyFrame(IMFSample* s, Frame& f);
    void Drain();
};

HRESULT cc_decoder::NegotiateOutput()
{
    ComPtr<IMFMediaType> chosen;
    for (DWORD i = 0;; ++i)
    {
        ComPtr<IMFMediaType> t;
        const HRESULT hr = mft->GetOutputAvailableType(0, i, &t);
        if (hr == MF_E_NO_MORE_TYPES) break;
        if (FAILED(hr)) return hr;
        GUID st{};
        if (SUCCEEDED(t->GetGUID(MF_MT_SUBTYPE, &st)) && IsEqualGUID(st, MFVideoFormat_NV12))
        {
            chosen = t;
            break;
        }
    }
    if (!chosen) return MF_E_INVALIDMEDIATYPE;

    HRESULT hr = mft->SetOutputType(0, chosen.Get(), 0);
    if (FAILED(hr))
    {
        // The HEVC decoder offers NV12 types without frame size/rate before the first frame and
        // then rejects them (MF_E_ATTRIBUTENOTFOUND). Fill the gaps from the input type.
        ComPtr<IMFMediaType> filled;
        ComPtr<IMFMediaType> in;
        if (SUCCEEDED(MFCreateMediaType(&filled)) && SUCCEEDED(chosen->CopyAllItems(filled.Get())) &&
            SUCCEEDED(mft->GetInputCurrentType(0, &in)))
        {
            UINT32 w = 0, h = 0, n = 0, d = 0;
            if (FAILED(MFGetAttributeSize(filled.Get(), MF_MT_FRAME_SIZE, &w, &h)) || w == 0)
            {
                if (FAILED(MFGetAttributeSize(in.Get(), MF_MT_FRAME_SIZE, &w, &h)) || w == 0) { w = 1920; h = 1080; }
                MFSetAttributeSize(filled.Get(), MF_MT_FRAME_SIZE, w, h);
            }
            if (FAILED(MFGetAttributeRatio(filled.Get(), MF_MT_FRAME_RATE, &n, &d)) || n == 0)
            {
                if (FAILED(MFGetAttributeRatio(in.Get(), MF_MT_FRAME_RATE, &n, &d)) || n == 0 || d == 0) { n = 30; d = 1; }
                MFSetAttributeRatio(filled.Get(), MF_MT_FRAME_RATE, n, d);
            }
            if (FAILED(MFGetAttributeRatio(filled.Get(), MF_MT_PIXEL_ASPECT_RATIO, &n, &d)))
                MFSetAttributeRatio(filled.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            UINT32 il = 0;
            if (FAILED(filled->GetUINT32(MF_MT_INTERLACE_MODE, &il)))
                filled->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            const HRESULT h2 = mft->SetOutputType(0, filled.Get(), 0);
            if (SUCCEEDED(h2)) hr = h2;
            else CcLog(L"SetOutputType NV12 failed 0x%08X / filled 0x%08X", static_cast<unsigned>(hr), static_cast<unsigned>(h2));
        }
        if (FAILED(hr)) return hr;
    }

    ComPtr<IMFMediaType> cur;
    hr = mft->GetOutputCurrentType(0, &cur);
    if (FAILED(hr)) return hr;

    UINT32 w = 0, h = 0;
    MFGetAttributeSize(cur.Get(), MF_MT_FRAME_SIZE, &w, &h);
    frameW = w;
    frameH = h;

    // Display aperture -> crop rectangle (removes the 1080 -> 1088 padding rows).
    cropX = 0; cropY = 0; cropW = w; cropH = h;
    MFVideoArea area{};
    UINT32 got = 0;
    if (SUCCEEDED(cur->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area),
                               sizeof(area), &got)) && got == sizeof(area))
    {
        const UINT32 ax = static_cast<UINT32>(area.OffsetX.value);
        const UINT32 ay = static_cast<UINT32>(area.OffsetY.value);
        const UINT32 aw = static_cast<UINT32>(area.Area.cx);
        const UINT32 ah = static_cast<UINT32>(area.Area.cy);
        if (aw >= 2 && ah >= 2 && ax + aw <= w && ay + ah <= h)
        {
            cropX = ax; cropY = ay; cropW = aw; cropH = ah;
        }
    }
    cropX &= ~1u; cropY &= ~1u; cropW &= ~1u; cropH &= ~1u;

    // Stride (only used when the buffer does not implement IMF2DBuffer).
    UINT32 ds = 0;
    if (SUCCEEDED(cur->GetUINT32(MF_MT_DEFAULT_STRIDE, &ds)) && static_cast<LONG>(ds) > 0)
        stride = static_cast<LONG>(ds);
    else
    {
        LONG s = 0;
        if (w > 0 && SUCCEEDED(MFGetStrideForBitmapInfoHeader(MFVideoFormat_NV12.Data1, w, &s)) && s > 0)
            stride = s;
        else
            stride = static_cast<LONG>(w);
    }

    std::memset(&outInfo, 0, sizeof(outInfo));
    hr = mft->GetOutputStreamInfo(0, &outInfo);
    if (FAILED(hr)) return hr;
    providesSamples = (outInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
    outSample.Reset();     // re-created lazily with the new size
    outSampleCap = 0;
    CcLog(L"decoder output %ux%u crop %u,%u %ux%u stride %ld provides=%d", w, h, cropX, cropY,
          cropW, cropH, stride, providesSamples ? 1 : 0);
    return S_OK;
}

HRESULT cc_decoder::EnsureOutSample()
{
    if (providesSamples) return S_OK;
    DWORD need = outInfo.cbSize;
    const DWORD geom = static_cast<DWORD>(std::max<LONG>(stride, 0)) * frameH * 3 / 2;
    need = std::max(need, geom);
    if (need == 0) need = 1920u * 1088u * 3u / 2u;   // before the first SPS: generous default
    if (outSample && outSampleCap >= need) return S_OK;

    outSample.Reset();
    ComPtr<IMFMediaBuffer> buf;
    const DWORD align = outInfo.cbAlignment > 0 ? outInfo.cbAlignment - 1 : 0;
    HRESULT hr = MFCreateAlignedMemoryBuffer(need, align, &buf);
    if (FAILED(hr)) return hr;
    ComPtr<IMFSample> s;
    hr = MFCreateSample(&s);
    if (FAILED(hr)) return hr;
    hr = s->AddBuffer(buf.Get());
    if (FAILED(hr)) return hr;
    outSample = s;
    outSampleCap = need;
    return S_OK;
}

HRESULT cc_decoder::CopyFrame(IMFSample* s, Frame& f)
{
    if (cropW == 0 || cropH == 0 || frameW == 0 || frameH == 0) return MF_E_INVALIDMEDIATYPE;

    LONGLONG t = 0;
    if (SUCCEEDED(s->GetSampleTime(&t))) lastPts = t;
    f.pts = lastPts;

    ComPtr<IMFMediaBuffer> buf;
    HRESULT hr = s->ConvertToContiguousBuffer(&buf);
    if (FAILED(hr)) return hr;

    const uint8_t* scan0 = nullptr;
    LONG pitch = 0;
    bool locked2d = false;
    bool lockedPlain = false;
    ComPtr<IMF2DBuffer> b2;

    if (SUCCEEDED(buf.As(&b2)))
    {
        BYTE* p = nullptr;
        if (SUCCEEDED(b2->Lock2D(&p, &pitch)))
        {
            scan0 = p;
            locked2d = true;
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
        pitch = stride;
        (void)curLen;
        const size_t needed = pitch > 0 ? static_cast<size_t>(pitch) * frameH * 3 / 2 : 0;
        if (pitch <= 0 || maxLen < needed)
        {
            buf->Unlock();
            return MF_E_BUFFERTOOSMALL;
        }
    }

    const size_t outW = cropW, outH = cropH;
    f.w = static_cast<int>(outW);
    f.h = static_cast<int>(outH);
    f.data = std::move(spare);
    f.data.resize(outW * outH * 3 / 2);

    uint8_t* dst = f.data.data();
    // Y plane
    for (size_t r = 0; r < outH; ++r)
    {
        const uint8_t* src = scan0 + static_cast<ptrdiff_t>(cropY + r) * pitch + cropX;
        std::memcpy(dst + r * outW, src, outW);
    }
    // UV plane starts after the *buffer* height (frameH, possibly 1088), not the crop height.
    const uint8_t* uvBase = scan0 + static_cast<ptrdiff_t>(frameH) * pitch;
    uint8_t* dstUV = dst + outW * outH;
    for (size_t r = 0; r < outH / 2; ++r)
    {
        const uint8_t* src = uvBase + static_cast<ptrdiff_t>(cropY / 2 + r) * pitch + cropX;
        std::memcpy(dstUV + r * outW, src, outW);
    }

    if (locked2d) b2->Unlock2D();
    if (lockedPlain) buf->Unlock();
    return S_OK;
}

HRESULT cc_decoder::ProduceOne(Frame& f, bool& got)
{
    got = false;
    for (int attempt = 0; attempt < 8; ++attempt)
    {
        if (!providesSamples)
        {
            HRESULT hr = EnsureOutSample();
            if (FAILED(hr)) return hr;
            ComPtr<IMFMediaBuffer> b;
            if (SUCCEEDED(outSample->GetBufferByIndex(0, &b))) b->SetCurrentLength(0);
        }

        MFT_OUTPUT_DATA_BUFFER odb{};
        odb.dwStreamID = 0;
        odb.pSample = providesSamples ? nullptr : outSample.Get();
        DWORD status = 0;
        const HRESULT hr = mft->ProcessOutput(0, 1, &odb, &status);
        if (odb.pEvents) { odb.pEvents->Release(); odb.pEvents = nullptr; }
        ComPtr<IMFSample> provided;
        if (providesSamples && odb.pSample) provided.Attach(odb.pSample);   // we own this ref

        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE || hr == MF_E_TRANSFORM_TYPE_NOT_SET)
        {
            const HRESULT nh = NegotiateOutput();
            if (FAILED(nh)) return nh;
            continue;
        }
        if (FAILED(hr)) return hr;

        IMFSample* s = providesSamples ? provided.Get() : outSample.Get();
        if (!s) return E_UNEXPECTED;
        const HRESULT ch = CopyFrame(s, f);
        if (FAILED(ch)) return ch;
        got = true;
        return S_OK;
    }
    return E_FAIL;
}

void cc_decoder::Drain()
{
    for (int i = 0; i < 16; ++i)
    {
        Frame f;
        bool got = false;
        if (FAILED(ProduceOne(f, got)) || !got) break;
        if (ready.size() >= kMaxReadyFrames)
        {
            spare = std::move(ready.front().data);
            ready.pop_front();
        }
        ready.push_back(std::move(f));
    }
}

// ---------------------------------------------------------------------------------------

CC_API cc_decoder* cc_decoder_create(int32_t codec)
{
    if (codec != CC_CODEC_H264 && codec != CC_CODEC_HEVC) return nullptr;
    CcEnsureComInit();
    if (FAILED(CcEnsureMfStartup())) return nullptr;

    ComPtr<IMFTransform> mft;
    const auto candidates = CreateDecoderCandidates(codec);
    for (size_t i = 0; i < candidates.size(); ++i)
    {
        const HRESULT hr = ConfigureInput(candidates[i].Get(), codec);
        if (SUCCEEDED(hr))
        {
            mft = candidates[i];
            break;
        }
        CcLog(L"decoder candidate %u rejected input type 0x%08X", static_cast<unsigned>(i), static_cast<unsigned>(hr));
    }
    if (!mft)
    {
        CcLog(L"no usable decoder MFT for codec %d (%u candidates)", codec, static_cast<unsigned>(candidates.size()));
        return nullptr;
    }

    HRESULT hr = S_OK;

    cc_decoder* dec = new (std::nothrow) cc_decoder();
    if (!dec) return nullptr;
    dec->codec = codec;
    dec->mft = mft;

    hr = dec->NegotiateOutput();   // may legitimately fail until the first SPS arrives
    if (FAILED(hr))
        CcLog(L"initial output negotiation deferred (0x%08X)", static_cast<unsigned>(hr));

    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    return dec;
}

CC_API int32_t cc_decoder_push(cc_decoder* dec, const uint8_t* annexb, int32_t length, int64_t ptsHns)
{
    if (!dec || !annexb || length <= 0) return E_INVALIDARG;

    ComPtr<IMFMediaBuffer> buf;
    HRESULT hr = MFCreateMemoryBuffer(static_cast<DWORD>(length), &buf);
    if (FAILED(hr)) return static_cast<int32_t>(hr);
    BYTE* p = nullptr;
    hr = buf->Lock(&p, nullptr, nullptr);
    if (FAILED(hr)) return static_cast<int32_t>(hr);
    std::memcpy(p, annexb, static_cast<size_t>(length));
    buf->Unlock();
    buf->SetCurrentLength(static_cast<DWORD>(length));

    ComPtr<IMFSample> sample;
    hr = MFCreateSample(&sample);
    if (FAILED(hr)) return static_cast<int32_t>(hr);
    sample->AddBuffer(buf.Get());
    sample->SetSampleTime(ptsHns);
    if (IsKeyAccessUnit(dec->codec, annexb, length))
        sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);

    hr = dec->mft->ProcessInput(0, sample.Get(), 0);
    if (hr == MF_E_NOTACCEPTING)
    {
        dec->Drain();
        hr = dec->mft->ProcessInput(0, sample.Get(), 0);
    }
    if (hr == MF_E_TRANSFORM_STREAM_CHANGE || hr == MF_E_TRANSFORM_TYPE_NOT_SET)
    {
        const HRESULT nh = dec->NegotiateOutput();
        if (SUCCEEDED(nh))
            hr = dec->mft->ProcessInput(0, sample.Get(), 0);
        else
            CcLog(L"push: output renegotiation failed 0x%08X", static_cast<unsigned>(nh));
    }
    if (FAILED(hr))
        CcLog(L"push: ProcessInput 0x%08X (len %d, key %d)", static_cast<unsigned>(hr), length,
              IsKeyAccessUnit(dec->codec, annexb, length) ? 1 : 0);
    return FAILED(hr) ? static_cast<int32_t>(hr) : 0;
}

CC_API int32_t cc_decoder_pull(cc_decoder* dec, uint8_t* nv12Out, int32_t capacity,
                               int32_t* width, int32_t* height, int64_t* ptsHns)
{
    if (!dec || !nv12Out || !width || !height || !ptsHns) return E_INVALIDARG;

    if (dec->ready.empty())
    {
        Frame f;
        bool got = false;
        const HRESULT hr = dec->ProduceOne(f, got);
        if (FAILED(hr)) return static_cast<int32_t>(hr);
        if (!got) return 0;
        dec->ready.push_back(std::move(f));
    }

    Frame& f = dec->ready.front();
    *width = f.w;
    *height = f.h;
    if (capacity < 0 || static_cast<size_t>(capacity) < f.data.size()) return -2;   // frame is kept

    std::memcpy(nv12Out, f.data.data(), f.data.size());
    *ptsHns = f.pts;
    dec->spare = std::move(f.data);
    dec->ready.pop_front();
    return 1;
}

CC_API void cc_decoder_destroy(cc_decoder* dec)
{
    if (!dec) return;
    if (dec->mft)
    {
        dec->mft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        dec->mft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
    }
    delete dec;
}
