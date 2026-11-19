import Foundation

// MARK: - Wire constants (see docs/PROTOCOL.md)

enum PacketType: UInt8 {
    case hello = 0x01
    case message = 0x02
    case video = 0x10
}

enum Wire {
    static let port: UInt16 = 7878
    static let protocolVersion = 1
    static let maxPayload = 32 * 1024 * 1024
    static let headerSize = 5
    static let videoHeaderSize = 9
}

// MARK: - Packet framing

enum PacketCodec {
    private static func appendLE32(_ value: UInt32, to data: inout Data) {
        data.append(UInt8(truncatingIfNeeded: value))
        data.append(UInt8(truncatingIfNeeded: value >> 8))
        data.append(UInt8(truncatingIfNeeded: value >> 16))
        data.append(UInt8(truncatingIfNeeded: value >> 24))
    }

    /// type(u8) | length(u32 LE) | payload
    static func encode(_ type: PacketType, payload: Data) -> Data {
        var packet = Data()
        packet.reserveCapacity(Wire.headerSize + payload.count)
        packet.append(type.rawValue)
        appendLE32(UInt32(payload.count), to: &packet)
        packet.append(payload)
        return packet
    }

    /// Builds a complete VIDEO packet: header + flags(u8) + pts(i64 LE, µs) + Annex-B access unit.
    static func encodeVideo(isKeyframe: Bool, isHEVC: Bool, ptsMicros: Int64, accessUnit: Data) -> Data {
        let payloadLength = Wire.videoHeaderSize + accessUnit.count
        var packet = Data()
        packet.reserveCapacity(Wire.headerSize + payloadLength)
        packet.append(PacketType.video.rawValue)
        appendLE32(UInt32(payloadLength), to: &packet)
        var flags: UInt8 = 0
        if isKeyframe { flags |= 0x01 }
        if isHEVC { flags |= 0x02 }
        packet.append(flags)
        let pts = UInt64(bitPattern: ptsMicros)
        for shift in stride(from: 0, to: 64, by: 8) {
            packet.append(UInt8(truncatingIfNeeded: pts >> UInt64(shift)))
        }
        packet.append(accessUnit)
        return packet
    }
}

enum PacketError: Error {
    case payloadTooLarge(Int)
}

/// Incremental packet parser. Feed arbitrary chunks with `append`, then call `next()` until it returns nil.
/// Handles partial headers and partial payloads.
struct PacketReader {
    private var buffer = Data()
    private var offset = 0

    mutating func append(_ data: Data) {
        buffer.append(data)
    }

    mutating func next() throws -> (type: UInt8, payload: Data)? {
        let available = buffer.count - offset
        guard available >= Wire.headerSize else { return nil }
        let type = buffer[offset]
        let length = Int(buffer[offset + 1])
            | (Int(buffer[offset + 2]) << 8)
            | (Int(buffer[offset + 3]) << 16)
            | (Int(buffer[offset + 4]) << 24)
        if length > Wire.maxPayload { throw PacketError.payloadTooLarge(length) }
        guard available >= Wire.headerSize + length else { return nil }
        let start = offset + Wire.headerSize
        let payload = buffer.subdata(in: start ..< (start + length))
        offset += Wire.headerSize + length
        if offset == buffer.count {
            buffer.removeAll(keepingCapacity: true)
            offset = 0
        } else if offset > 64 * 1024 {
            buffer = Data(buffer[offset...])
            offset = 0
        }
        return (type, payload)
    }
}

// MARK: - JSON helpers

enum JSONCoding {
    static let encoder = JSONEncoder()
    static let decoder = JSONDecoder()

    static func encode<T: Encodable>(_ value: T) -> Data? {
        try? encoder.encode(value)
    }
}

/// Replaces NaN / infinity (which JSON cannot represent) by a fallback.
func finite(_ x: Double, _ fallback: Double = 0) -> Double {
    x.isFinite ? x : fallback
}

/// Double -> Int without trapping on NaN / huge values.
func safeInt(_ d: Double?) -> Int? {
    guard let d, d.isFinite, abs(d) < 1_000_000_000 else { return nil }
    return Int(d.rounded())
}

// MARK: - HELLO

struct HelloMessage: Codable {
    var protocolVersion: Int?
    var app: String?
    var version: String?
    var deviceName: String?
    var model: String?
    var systemVersion: String?

    enum CodingKeys: String, CodingKey {
        case protocolVersion = "protocol"
        case app, version, deviceName, model, systemVersion
    }
}

// MARK: - Desktop -> phone messages

struct PointOfInterest: Codable {
    var x: Double
    var y: Double
}

/// One decoded desktop message. Every field except `type` is optional; numbers are decoded as
/// Double so that `1920` and `1920.0` are both accepted. Unknown fields are ignored.
struct ClientMessage: Decodable {
    var type: String

    // configure
    var cameraId: String?
    var width: Double?
    var height: Double?
    var fps: Double?
    var codec: String?
    var bitrateKbps: Double?
    var keyframeIntervalSec: Double?
    var orientation: String?
    var mirror: Bool?

    // setControl
    var zoom: Double?
    var rampZoom: Bool?
    var focusMode: String?
    var lensPosition: Double?
    var pointOfInterest: PointOfInterest?
    var exposureMode: String?
    var iso: Double?
    var exposureDurationSec: Double?
    var exposureBias: Double?
    var whiteBalanceMode: String?
    var temperature: Double?
    var tint: Double?
    var torch: Double?
    var stabilization: String?
    var hdr: Bool?
    var lowLightBoost: Bool?
    var centerStage: Bool?
    var dimScreen: Bool?
}

// MARK: - Phone -> desktop messages

struct CameraFormatInfo: Codable, Equatable {
    var width: Int
    var height: Int
    var maxFps: Int
    var hdr: Bool
}

struct CameraInfo: Codable, Equatable, Identifiable {
    var id: String
    var name: String
    var position: String          // "back" | "front"
    var deviceType: String        // wide|ultraWide|telephoto|dual|dualWide|triple|trueDepth
    var formats: [CameraFormatInfo]
    var minZoom: Double
    var maxZoom: Double
    var zoomSwitchOver: [Double]
    var zoomDisplayMultiplier: Double
    var hasTorch: Bool
    var focusModes: [String]
    var supportsPointOfInterest: Bool
    var supportsCustomExposure: Bool
    var minExposureBias: Double
    var maxExposureBias: Double
    var supportsCustomWhiteBalance: Bool
    var supportsLowLightBoost: Bool
    var centerStage: Bool
}

struct CapabilitiesMessage: Encodable {
    var type = "capabilities"
    var cameras: [CameraInfo]
}

struct StatsInfo: Encodable {
    var fps: Double
    var bitrateKbps: Double
    var droppedFrames: Int
}

struct StateMessage: Encodable {
    var type = "state"
    var streaming: Bool
    var cameraId: String
    var width: Int
    var height: Int
    var fps: Int
    var codec: String
    var bitrateKbps: Int
    var orientation: String
    var mirror: Bool
    var zoom: Double
    var focusMode: String
    var lensPosition: Double
    var exposureMode: String
    var iso: Double
    var exposureDurationSec: Double
    var minISO: Double
    var maxISO: Double
    var minExposureDurationSec: Double
    var maxExposureDurationSec: Double
    var exposureBias: Double
    var whiteBalanceMode: String
    var temperature: Double
    var tint: Double
    var torch: Double
    var stabilization: String
    var activeStabilization: String
    var hdr: Bool
    var hdrSupported: Bool
    var lowLightBoost: Bool
    var centerStage: Bool
    var dimScreen: Bool
    var thermalState: String
    var batteryLevel: Double?     // omitted when unknown
    var charging: Bool
    var stats: StatsInfo
}

struct StreamStartedMessage: Encodable {
    var type = "streamStarted"
    var codec: String
    var width: Int
    var height: Int
    var fps: Int
}

struct StreamStoppedMessage: Encodable {
    var type = "streamStopped"
}

struct ErrorMessage: Encodable {
    var type = "error"
    var message: String
}
