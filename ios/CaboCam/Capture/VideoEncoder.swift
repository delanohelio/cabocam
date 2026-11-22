import Foundation
import CoreMedia
import CoreVideo
import VideoToolbox

enum VideoCodec: String, Sendable {
    case h264
    case hevc

    var cmCodecType: CMVideoCodecType {
        self == .hevc ? kCMVideoCodecType_HEVC : kCMVideoCodecType_H264
    }

    var displayName: String { self == .hevc ? "HEVC" : "H.264" }
}

struct EncoderConfig: Equatable, Sendable {
    var codec: VideoCodec = .h264
    var fps: Int = 30
    var bitrateKbps: Int = 12000
    var keyframeIntervalSec: Double = 2
}

struct EncodedFrame: Sendable {
    let isKeyframe: Bool
    let isHEVC: Bool
    let ptsMicros: Int64
    /// Annex-B access unit (00 00 00 01 start codes); keyframes start with their parameter sets.
    let data: Data
}

/// Hardware H.264 / HEVC encoder (VTCompressionSession), real-time, no B-frames.
/// `encode` may be called from the capture queue; configuration from any thread.
final class VideoEncoder: @unchecked Sendable {
    private let onFrame: @Sendable (EncodedFrame) -> Void
    /// Set once before the first `encode`.
    var onError: (@Sendable (String) -> Void)?

    private let lock = NSLock()               // guards session + config + dimensions
    private var session: VTCompressionSession?
    private var sessionWidth = 0
    private var sessionHeight = 0
    private var config = EncoderConfig()
    private var lastErrorTime = DispatchTime(uptimeNanoseconds: 0)

    private let flagLock = NSLock()           // guards forceKey (separate so it never blocks behind `lock`)
    private var forceKey = true

    init(onFrame: @escaping @Sendable (EncodedFrame) -> Void) {
        self.onFrame = onFrame
    }

    deinit { invalidate() }

    // MARK: Public API

    /// Applies a new configuration. Bitrate / fps / keyframe interval are changed live; a codec change
    /// recreates the session on the next frame.
    func update(_ new: EncoderConfig) {
        lock.lock(); defer { lock.unlock() }
        let old = config
        config = new
        guard let s = session else { return }
        if new.codec != old.codec {
            destroySessionLocked()
            return
        }
        if new.bitrateKbps != old.bitrateKbps { applyBitrate(s, new) }
        if new.fps != old.fps || new.keyframeIntervalSec != old.keyframeIntervalSec { applyTiming(s, new) }
    }

    /// The next encoded frame will be an IDR (with parameter sets).
    func forceKeyframe() {
        flagLock.lock(); forceKey = true; flagLock.unlock()
    }

    private func takeForceKey() -> Bool {
        flagLock.lock(); defer { flagLock.unlock() }
        let v = forceKey
        forceKey = false
        return v
    }

    /// Tears the session down (stream stop). A new one is created lazily by the next `encode`.
    func invalidate() {
        lock.lock(); defer { lock.unlock() }
        destroySessionLocked()
        forceKeyframe()
    }

    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)

        lock.lock(); defer { lock.unlock() }

        // (Re)create the session when absent or when the frame size changed (camera / orientation switch).
        if session == nil || width != sessionWidth || height != sessionHeight {
            destroySessionLocked()
            guard createSessionLocked(width: width, height: height) else { return }
            forceKeyframe()
        }
        guard let session else { return }

        var frameProperties: CFDictionary?
        if takeForceKey() {
            frameProperties = [kVTEncodeFrameOptionKey_ForceKeyFrame as String: true] as CFDictionary
        }
        let hevc = config.codec == .hevc
        var infoFlags = VTEncodeInfoFlags()
        let status = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: pts,
            duration: .invalid,
            frameProperties: frameProperties,
            infoFlagsOut: &infoFlags
        ) { [weak self] status, _, sampleBuffer in
            self?.handleOutput(status: status, sampleBuffer: sampleBuffer, hevc: hevc)
        }
        if status != noErr {
            // Typically kVTInvalidSessionErr after the app was suspended: rebuild on the next frame.
            destroySessionLocked()
            forceKeyframe()
            reportError("Falha no codificador de vídeo (\(status))")
        }
    }

    // MARK: Session management (lock held)

    private func createSessionLocked(width: Int, height: Int) -> Bool {
        let cfg = config
        var created: VTCompressionSession?
        // iPhones always encode in hardware; the explicit key only exists on iOS 17.4+.
        var spec: [String: Any] = [:]
        if #available(iOS 17.4, *) {
            spec[kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder as String] = true
        }
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(width),
            height: Int32(height),
            codecType: cfg.codec.cmCodecType,
            encoderSpecification: spec as CFDictionary,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: nil,       // output is delivered through the per-frame output handler
            refcon: nil,
            compressionSessionOut: &created
        )
        guard status == noErr, let s = created else {
            reportError("Não foi possível criar o codificador \(cfg.codec.displayName) (\(status))")
            return false
        }

        set(s, kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        set(s, kVTCompressionPropertyKey_ProfileLevel,
            cfg.codec == .hevc ? kVTProfileLevel_HEVC_Main_AutoLevel : kVTProfileLevel_H264_High_AutoLevel)
        set(s, kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        set(s, kVTCompressionPropertyKey_PrioritizeEncodingSpeedOverQuality, kCFBooleanTrue)
        // Signal BT.709 limited range (what the camera's 420v buffers are) in the bitstream VUI.
        set(s, kVTCompressionPropertyKey_ColorPrimaries, kCMFormatDescriptionColorPrimaries_ITU_R_709_2)
        set(s, kVTCompressionPropertyKey_TransferFunction, kCMFormatDescriptionTransferFunction_ITU_R_709_2)
        set(s, kVTCompressionPropertyKey_YCbCrMatrix, kCMFormatDescriptionYCbCrMatrix_ITU_R_709_2)
        applyBitrate(s, cfg)
        applyTiming(s, cfg)
        VTCompressionSessionPrepareToEncodeFrames(s)

        session = s
        sessionWidth = width
        sessionHeight = height
        return true
    }

    private func destroySessionLocked() {
        if let s = session {
            VTCompressionSessionCompleteFrames(s, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(s)
        }
        session = nil
        sessionWidth = 0
        sessionHeight = 0
    }

    private func set(_ s: VTCompressionSession, _ key: CFString, _ value: CFTypeRef) {
        _ = VTSessionSetProperty(s, key: key, value: value)
    }

    private func applyBitrate(_ s: VTCompressionSession, _ cfg: EncoderConfig) {
        let bps = max(100_000, cfg.bitrateKbps * 1000)
        set(s, kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: bps))
        // Hard cap: at most 1.5x the average within any 1 second window.
        let bytesPerSecond = Int(Double(bps) * 1.5 / 8.0)
        set(s, kVTCompressionPropertyKey_DataRateLimits, [NSNumber(value: bytesPerSecond), NSNumber(value: 1)] as CFArray)
    }

    private func applyTiming(_ s: VTCompressionSession, _ cfg: EncoderConfig) {
        let fps = max(1, cfg.fps)
        let interval = max(0.5, cfg.keyframeIntervalSec)
        set(s, kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: fps))
        set(s, kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, NSNumber(value: interval))
        set(s, kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: Int((Double(fps) * interval).rounded())))
    }

    private func reportError(_ message: String) {
        // Throttle: at most one error report every 2 s.
        let now = DispatchTime.now()
        if Double(now.uptimeNanoseconds &- lastErrorTime.uptimeNanoseconds) / 1e9 < 2.0 { return }
        lastErrorTime = now
        onError?(message)
    }

    // MARK: Output (VideoToolbox thread; must NOT take `lock`)

    private func handleOutput(status: OSStatus, sampleBuffer: CMSampleBuffer?, hevc: Bool) {
        guard status == noErr, let sb = sampleBuffer, CMSampleBufferDataIsReady(sb) else { return }
        guard let (accessUnit, isKey) = Self.makeAccessUnit(from: sb, hevc: hevc) else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sb)
        let micros = CMTimeConvertScale(pts, timescale: 1_000_000, method: .roundHalfAwayFromZero).value
        onFrame(EncodedFrame(isKeyframe: isKey, isHEVC: hevc, ptsMicros: micros, data: accessUnit))
    }

    private static let startCode: [UInt8] = [0, 0, 0, 1]

    private static func isParameterSet(_ firstNalByte: UInt8, hevc: Bool) -> Bool {
        if hevc {
            let t = (firstNalByte >> 1) & 0x3F
            return t == 32 || t == 33 || t == 34
        }
        let t = firstNalByte & 0x1F
        return t == 7 || t == 8
    }

    /// AVCC/HVCC length-prefixed sample -> Annex-B access unit; on keyframes the parameter sets
    /// (H.264: SPS,PPS; HEVC: VPS,SPS,PPS) from the format description are prepended.
    static func makeAccessUnit(from sb: CMSampleBuffer, hevc: Bool) -> (Data, Bool)? {
        guard let block = CMSampleBufferGetDataBuffer(sb),
              let fmt = CMSampleBufferGetFormatDescription(sb) else { return nil }

        // Keyframe = sync sample.
        var isKey = true
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[CFString: Any]],
           let first = attachments.first,
           let notSync = first[kCMSampleAttachmentKey_NotSync] as? Bool, notSync {
            isKey = false
        }

        // Parameter set count + NAL length-prefix size.
        var paramCount = 0
        var nalHeaderLength: Int32 = 4
        var status: OSStatus
        if hevc {
            status = CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
                fmt, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil,
                parameterSetCountOut: &paramCount, nalUnitHeaderLengthOut: &nalHeaderLength)
        } else {
            status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                fmt, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil,
                parameterSetCountOut: &paramCount, nalUnitHeaderLengthOut: &nalHeaderLength)
        }
        guard status == noErr, nalHeaderLength >= 1, nalHeaderLength <= 4 else { return nil }

        let total = CMBlockBufferGetDataLength(block)
        guard total > 0 else { return nil }

        var out = Data()
        out.reserveCapacity(total + 256)

        if isKey {
            for i in 0 ..< paramCount {
                var pointer: UnsafePointer<UInt8>?
                var size = 0
                if hevc {
                    status = CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
                        fmt, parameterSetIndex: i, parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
                        parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
                } else {
                    status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                        fmt, parameterSetIndex: i, parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
                        parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
                }
                if status == noErr, let pointer, size > 0 {
                    out.append(contentsOf: startCode)
                    out.append(pointer, count: size)
                }
            }
        }

        // Copy the (possibly non-contiguous) block buffer, then convert NAL by NAL.
        var sample = Data(count: total)
        let copyStatus = sample.withUnsafeMutableBytes { raw -> OSStatus in
            guard let base = raw.baseAddress else { return -1 }
            return CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: total, destination: base)
        }
        guard copyStatus == noErr else { return nil }

        let lengthSize = Int(nalHeaderLength)
        sample.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            guard let base = raw.baseAddress else { return }
            let n = raw.count
            var i = 0
            while i + lengthSize <= n {
                var len = 0
                for k in 0 ..< lengthSize { len = (len << 8) | Int(raw[i + k]) }
                i += lengthSize
                guard len > 0, i + len <= n else { break }
                let skip = isKey && isParameterSet(raw[i], hevc: hevc)
                if !skip {
                    out.append(contentsOf: startCode)
                    out.append(base.advanced(by: i).assumingMemoryBound(to: UInt8.self), count: len)
                }
                i += len
            }
        }
        return (out, isKey)
    }
}
