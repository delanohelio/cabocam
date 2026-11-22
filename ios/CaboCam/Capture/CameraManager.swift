import AVFoundation
import CoreMedia
import CoreVideo
import Foundation

// MARK: - Public value types

/// What the desktop (or the phone UI) asks the capture pipeline to deliver.
struct CaptureConfig: Equatable, Sendable {
    var cameraId: String?
    /// Landscape sensor format size (a portrait output swaps them).
    var width = 1920
    var height = 1080
    var fps = 30
    var portrait = false
    var mirror = false
}

/// Everything the camera layer knows about itself at one instant (read on the session queue).
struct CameraSnapshot: Sendable, Equatable {
    var cameraId = ""
    var hasDevice = false
    var sensorWidth = 0, sensorHeight = 0        // active format, landscape
    var outputWidth = 0, outputHeight = 0        // after rotation
    var fps = 0
    var portrait = false                         // output frame shape
    var mirror = false
    var rotationAngle = 0.0                      // angle applied to the connection
    var zoom = 1.0, minZoom = 1.0, maxZoom = 1.0
    var focusMode = "continuous", lensPosition = 0.0
    var exposureMode = "continuous", iso = 0.0, exposureDurationSec = 0.0
    var minISO = 0.0, maxISO = 0.0, minExposureDurationSec = 0.0, maxExposureDurationSec = 0.0
    var exposureBias = 0.0
    var whiteBalanceMode = "continuous", temperature = 5000.0, tint = 0.0
    var torch = 0.0, hasTorch = false
    var stabilization = "auto", activeStabilization = "off"
    var hdr = false, hdrSupported = false
    var lowLightBoost = false, centerStage = false
    var sessionRunning = false
}

enum CameraEvent: Sendable {
    case interrupted(String)
    case resumed
    case error(String)
    case authorizationDenied
}

// MARK: - CameraManager

/// Owns the AVCaptureSession. All mutable state is confined to `sessionQueue`; frames are delivered
/// on `videoQueue`; snapshots / events are delivered on `sessionQueue` through the callbacks (the
/// receiver must hop to the main thread).
final class CameraManager: NSObject, @unchecked Sendable {

    let session = AVCaptureSession()
    /// Immutable list built once at init.
    let cameras: [CameraInfo]
    let primaryBackId: String?
    let primaryFrontId: String?

    var onUpdate: (@Sendable (CameraSnapshot) -> Void)?
    var onEvent: (@Sendable (CameraEvent) -> Void)?

    private let devicesById: [String: AVCaptureDevice]
    private let sessionQueue = DispatchQueue(label: "com.cabocam.capture.session")
    private let videoQueue = DispatchQueue(label: "com.cabocam.capture.video", qos: .userInteractive)
    private let videoOutput = AVCaptureVideoDataOutput()

    // Frame hand-off (read on videoQueue, written from anywhere).
    private let handlerLock = NSLock()
    private var frameHandler: (@Sendable (CVPixelBuffer, CMTime) -> Void)?

    // Session-queue confined state.
    private var configured = false
    private var wantRunning = false
    private var currentDevice: AVCaptureDevice?
    private var currentInput: AVCaptureDeviceInput?
    private var config = CaptureConfig()
    private var controls = Controls()
    private var appliedAngle: CGFloat = 0
    private var appliedMirror = false
    private var rotationCoordinator: AVCaptureDevice.RotationCoordinator?
    private var rotationObservation: NSKeyValueObservation?
    private var observers: [NSObjectProtocol] = []

    /// Persisted control values; re-applied after camera / format switches.
    private struct Controls {
        var zoom: Double?                 // raw videoZoomFactor; nil = camera default
        var focusMode = "continuous"
        var lensPosition: Double?
        var exposureMode = "continuous"
        var iso: Double?
        var exposureDuration: Double?
        var exposureBias = 0.0
        var whiteBalanceMode = "continuous"
        var temperature: Double?
        var tint: Double?
        var torch = 0.0
        var stabilization = "auto"
        var hdr = true
        var lowLightBoost = false
        var centerStage = false

    /// A message that re-applies the persisted controls (no POI, no zoom ramp).
    func asMessage(defaultZoom: Double? = nil) -> ClientMessage {
        var m = ClientMessage(type: "setControl")
        m.zoom = zoom ?? defaultZoom
        if let lensPosition {
            m.lensPosition = lensPosition
        } else {
            m.focusMode = focusMode
        }
        m.exposureMode = exposureMode
        if exposureMode == "custom" { m.iso = iso; m.exposureDurationSec = exposureDuration }
        m.exposureBias = exposureBias
        m.whiteBalanceMode = whiteBalanceMode
        if whiteBalanceMode == "custom" { m.temperature = temperature; m.tint = tint }
        m.torch = torch
        m.stabilization = stabilization
        m.hdr = hdr
        m.lowLightBoost = lowLightBoost
        m.centerStage = centerStage
        return m
    }
    }

    // MARK: Init / discovery

    override init() {
        let devices = Self.discoverDevices()
        var byId: [String: AVCaptureDevice] = [:]
        for d in devices { byId[d.uniqueID] = d }
        devicesById = byId
        cameras = devices.map { Self.makeInfo(for: $0) }

        func rank(_ d: AVCaptureDevice) -> Int {
            switch d.deviceType {
            case .builtInTripleCamera: return 0
            case .builtInDualWideCamera: return 1
            case .builtInDualCamera: return 2
            case .builtInWideAngleCamera: return 3
            default: return 9
            }
        }
        primaryBackId = devices.filter { $0.position == .back }.min { rank($0) < rank($1) }?.uniqueID
        primaryFrontId = (devices.first { $0.position == .front && $0.deviceType == .builtInWideAngleCamera }
            ?? devices.first { $0.position == .front })?.uniqueID
        super.init()
        registerNotifications()
    }

    deinit {
        for o in observers { NotificationCenter.default.removeObserver(o) }
    }

    private static func discoverDevices() -> [AVCaptureDevice] {
        let types: [AVCaptureDevice.DeviceType] = [
            .builtInTripleCamera, .builtInDualWideCamera, .builtInDualCamera,
            .builtInWideAngleCamera, .builtInUltraWideCamera, .builtInTelephotoCamera,
            .builtInTrueDepthCamera,
        ]
        let found = AVCaptureDevice.DiscoverySession(deviceTypes: types, mediaType: .video, position: .unspecified).devices
        func order(_ d: AVCaptureDevice) -> Int {
            let pos = d.position == .back ? 0 : 100
            switch d.deviceType {
            case .builtInTripleCamera: return pos + 0
            case .builtInDualWideCamera: return pos + 1
            case .builtInDualCamera: return pos + 2
            case .builtInWideAngleCamera: return pos + 3
            case .builtInUltraWideCamera: return pos + 4
            case .builtInTelephotoCamera: return pos + 5
            default: return pos + 6
            }
        }
        return found.sorted { order($0) < order($1) }
    }

    private static func typeName(_ t: AVCaptureDevice.DeviceType) -> String {
        switch t {
        case .builtInTripleCamera: return "triple"
        case .builtInDualWideCamera: return "dualWide"
        case .builtInDualCamera: return "dual"
        case .builtInUltraWideCamera: return "ultraWide"
        case .builtInTelephotoCamera: return "telephoto"
        case .builtInTrueDepthCamera: return "trueDepth"
        default: return "wide"
        }
    }

    static func is420v(_ f: AVCaptureDevice.Format) -> Bool {
        CMFormatDescriptionGetMediaSubType(f.formatDescription) == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
    }

    private static func maxFps(_ f: AVCaptureDevice.Format) -> Int {
        Int((f.videoSupportedFrameRateRanges.map { $0.maxFrameRate }.max() ?? 0).rounded(.down))
    }

    private static func makeInfo(for d: AVCaptureDevice) -> CameraInfo {
        // Formats: 420v only, deduped on (w, h, maxFps), hdr = any format of the group supports it.
        struct Key: Hashable { let w: Int; let h: Int; let fps: Int }
        var groups: [Key: Bool] = [:]
        var maxZoom = 1.0
        for f in d.formats where is420v(f) {
            let dim = CMVideoFormatDescriptionGetDimensions(f.formatDescription)
            let key = Key(w: Int(dim.width), h: Int(dim.height), fps: maxFps(f))
            groups[key] = (groups[key] ?? false) || f.isVideoHDRSupported
            maxZoom = max(maxZoom, Double(f.videoMaxZoomFactor))
        }
        let formats = groups
            .map { CameraFormatInfo(width: $0.key.w, height: $0.key.h, maxFps: $0.key.fps, hdr: $0.value) }
            .sorted {
                let a0 = $0.width * $0.height, a1 = $1.width * $1.height
                return a0 != a1 ? a0 > a1 : $0.maxFps > $1.maxFps
            }

        let switchOver = d.virtualDeviceSwitchOverVideoZoomFactors.map { $0.doubleValue }
        var multiplier = 1.0
        if #available(iOS 18.0, *) {
            multiplier = Double(d.displayVideoZoomFactorMultiplier)
        } else if d.deviceType == .builtInUltraWideCamera {
            multiplier = 0.5
        } else if let firstSwitch = switchOver.first,
                  d.constituentDevices.first?.deviceType == .builtInUltraWideCamera {
            // Virtual device that starts at the ultra-wide: the main lens sits at the first switch-over.
            multiplier = 1.0 / firstSwitch
        }
        if !(multiplier.isFinite && multiplier > 0) { multiplier = 1 }

        var focusModes: [String] = []
        if d.isFocusModeSupported(.continuousAutoFocus) { focusModes.append("continuous") }
        if d.isFocusModeSupported(.autoFocus) { focusModes.append("auto") }
        if d.isFocusModeSupported(.locked) { focusModes.append("locked") }

        return CameraInfo(
            id: d.uniqueID,
            name: d.localizedName,
            position: d.position == .front ? "front" : "back",
            deviceType: typeName(d.deviceType),
            formats: formats,
            minZoom: finite(Double(d.minAvailableVideoZoomFactor), 1),
            maxZoom: finite(maxZoom, 1),
            zoomSwitchOver: switchOver,
            zoomDisplayMultiplier: multiplier,
            hasTorch: d.hasTorch,
            focusModes: focusModes,
            supportsPointOfInterest: d.isFocusPointOfInterestSupported || d.isExposurePointOfInterestSupported,
            supportsCustomExposure: d.isExposureModeSupported(.custom),
            minExposureBias: finite(Double(d.minExposureTargetBias)),
            maxExposureBias: finite(Double(d.maxExposureTargetBias)),
            supportsCustomWhiteBalance: d.isLockingWhiteBalanceWithCustomDeviceGainsSupported,
            supportsLowLightBoost: d.isLowLightBoostSupported,
            centerStage: d.position == .front && d.formats.contains { $0.isCenterStageSupported }
        )
    }

    func hasCamera(id: String) -> Bool { devicesById[id] != nil }

    // MARK: Frame handler

    func setFrameHandler(_ handler: (@Sendable (CVPixelBuffer, CMTime) -> Void)?) {
        handlerLock.lock(); frameHandler = handler; handlerLock.unlock()
    }

    private func currentFrameHandler() -> (@Sendable (CVPixelBuffer, CMTime) -> Void)? {
        handlerLock.lock(); defer { handlerLock.unlock() }
        return frameHandler
    }

    // MARK: Lifecycle

    /// Starts (or resumes) capture. Safe to call repeatedly.
    func start() {
        sessionQueue.async {
            self.wantRunning = true
            guard self.isAuthorized() else { return }
            if self.currentDevice == nil {
                self.reconfigure()
            }
            if !self.session.isRunning, self.currentDevice != nil {
                self.session.startRunning()
                // After a stop (background) torch and other device state need to be re-applied.
                if let d = self.currentDevice { self.applyControlMessage(self.controls.asMessage(), to: d) }
            }
            self.emitSnapshot()
        }
    }

    func stop() {
        sessionQueue.async {
            self.wantRunning = false
            if let d = self.currentDevice, d.hasTorch, d.torchMode != .off {
                if (try? d.lockForConfiguration()) != nil {
                    d.torchMode = .off
                    d.unlockForConfiguration()
                }
            }
            if self.session.isRunning { self.session.stopRunning() }
            self.emitSnapshot()
        }
    }

    private func isAuthorized() -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized: return true
        case .notDetermined: return false      // the app is about to ask; it calls start() again
        default:
            onEvent?(.authorizationDenied)
            return false
        }
    }

    /// Applies a capture configuration (camera / format / fps / orientation / mirroring).
    func apply(_ newConfig: CaptureConfig) {
        sessionQueue.async {
            self.config = newConfig
            guard self.isAuthorized() else { return }
            self.reconfigure()
        }
    }

    func requestSnapshot() {
        sessionQueue.async { self.emitSnapshot() }
    }

    /// Applies the controls present in the message (everything else is left alone).
    func setControl(_ message: ClientMessage) {
        sessionQueue.async {
            if let device = self.currentDevice {
                self.applyControlMessage(message, to: device)
                if message.stabilization != nil { self.applyConnectionSettings() }
            }
            self.emitSnapshot()
            if message.rampZoom == true, message.zoom != nil {
                // Report the final zoom once the ramp has finished.
                self.sessionQueue.asyncAfter(deadline: .now() + 1.2) { self.emitSnapshot() }
            }
        }
    }

    // MARK: Session / input configuration (session queue)

    private func ensureSessionSetup() {
        guard !configured else { return }
        session.beginConfiguration()
        session.sessionPreset = .inputPriority
        session.automaticallyConfiguresCaptureDeviceForWideColor = false
        videoOutput.alwaysDiscardsLateVideoFrames = true
        videoOutput.videoSettings = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        ]
        videoOutput.setSampleBufferDelegate(self, queue: videoQueue)
        if session.canAddOutput(videoOutput) { session.addOutput(videoOutput) }
        session.commitConfiguration()
        configured = true
        if #available(iOS 14.5, *) {
            // Cooperative: the app (and therefore the desktop) controls Center Stage, not Control Center.
            AVCaptureDevice.centerStageControlMode = .cooperative
        }
    }

    private func resolveDevice() -> AVCaptureDevice? {
        if let id = config.cameraId, let d = devicesById[id] { return d }
        if let current = currentDevice { return current }
        if let id = primaryBackId ?? primaryFrontId ?? cameras.first?.id { return devicesById[id] }
        return nil
    }

    private func reconfigure() {
        ensureSessionSetup()
        guard let device = resolveDevice() else {
            onEvent?(.error("Nenhuma câmera disponível"))
            emitSnapshot()
            return
        }

        var needsControls = false
        if currentDevice !== device {
            guard swapInput(to: device) else { emitSnapshot(); return }
            controls.zoom = nil
            needsControls = true
        }
        if configureFormat(on: device) { needsControls = true }
        applyConnectionSettings()
        if needsControls { applyControlMessage(controls.asMessage(defaultZoom: defaultZoom(for: device)), to: device) }

        if wantRunning, !session.isRunning {
            session.startRunning()
        }
        emitSnapshot()
    }

    private func swapInput(to device: AVCaptureDevice) -> Bool {
        if let old = currentDevice, old.hasTorch, old.torchMode != .off, (try? old.lockForConfiguration()) != nil {
            old.torchMode = .off
            old.unlockForConfiguration()
        }
        let input: AVCaptureDeviceInput
        do {
            input = try AVCaptureDeviceInput(device: device)
        } catch {
            onEvent?(.error("Não foi possível abrir a câmera: \(error.localizedDescription)"))
            return false
        }
        session.beginConfiguration()
        defer { session.commitConfiguration() }
        let previous = currentInput
        if let previous { session.removeInput(previous) }
        guard session.canAddInput(input) else {
            if let previous, session.canAddInput(previous) { session.addInput(previous) }
            onEvent?(.error("Câmera não suportada nesta configuração"))
            return false
        }
        session.addInput(input)
        currentInput = input
        currentDevice = device
        rotationObservation = nil
        rotationCoordinator = AVCaptureDevice.RotationCoordinator(device: device, previewLayer: nil)
        rotationObservation = rotationCoordinator?.observe(\.videoRotationAngleForHorizonLevelCapture, options: [.new]) { [weak self] _, _ in
            self?.sessionQueue.async {
                guard let self else { return }
                self.applyConnectionSettings()
                self.emitSnapshot()
            }
        }
        return true
    }

    /// Raw zoom factor of the "1x" main lens.
    private func defaultZoom(for device: AVCaptureDevice) -> Double {
        guard let info = cameras.first(where: { $0.id == device.uniqueID }) else { return 1 }
        if info.zoomDisplayMultiplier < 1, info.zoomDisplayMultiplier > 0, device.isVirtualDevice {
            return max(info.minZoom, 1.0 / info.zoomDisplayMultiplier)
        }
        return info.minZoom
    }

    // MARK: Format selection

    /// PROTOCOL: pick the closest supported 420v format: same aspect first, then area >= requested
    /// (closest), then formats that can reach the requested fps (then HDR-capable).
    private func bestFormat(for device: AVCaptureDevice) -> AVCaptureDevice.Format? {
        let candidates = device.formats.filter(Self.is420v)
        guard !candidates.isEmpty else { return nil }
        let reqW = Double(max(config.width, 16)), reqH = Double(max(config.height, 16))
        let reqArea = reqW * reqH
        let reqAspect = reqW / reqH
        let wantFps = max(1, config.fps)
        let wantCenterStage = controls.centerStage && device.position == .front

        func score(_ f: AVCaptureDevice.Format) -> (Int, Int, Double, Int, Int, Int) {
            let dim = CMVideoFormatDescriptionGetDimensions(f.formatDescription)
            let w = Double(dim.width), h = Double(dim.height)
            let aspectPenalty = abs(w / h - reqAspect) < 0.02 ? 0 : 1
            let ratio = (w * h) / reqArea
            let smallerPenalty = ratio >= 1 ? 0 : 1
            let areaDistance = abs(log(ratio))
            let fpsPenalty = Self.maxFps(f) >= wantFps ? 0 : 1
            let hdrPenalty = f.isVideoHDRSupported ? 0 : 1
            let csPenalty = (wantCenterStage && !f.isCenterStageSupported) ? 1 : 0
            return (aspectPenalty, smallerPenalty, areaDistance, fpsPenalty, csPenalty, hdrPenalty)
        }
        return candidates.min { score($0) < score($1) }
    }

    private func clampedFps(for format: AVCaptureDevice.Format) -> Int {
        let want = Double(max(1, config.fps))
        let ranges = format.videoSupportedFrameRateRanges
        if ranges.contains(where: { $0.minFrameRate <= want && want <= $0.maxFrameRate }) { return Int(want) }
        let hi = ranges.map { $0.maxFrameRate }.max() ?? 30
        let lo = ranges.map { $0.minFrameRate }.min() ?? 1
        let clamped = want > hi ? hi.rounded(.down) : lo.rounded(.up)
        return max(1, Int(clamped))
    }

    /// Returns true when the active format or frame rate changed.
    private func configureFormat(on device: AVCaptureDevice) -> Bool {
        guard let format = bestFormat(for: device) else {
            onEvent?(.error("Formato não suportado pela câmera"))
            return false
        }
        let rate = clampedFps(for: format)
        let duration = CMTime(value: 1, timescale: CMTimeScale(rate))
        let formatChanged = device.activeFormat !== format
        let durationChanged = device.activeVideoMinFrameDuration != duration
            || device.activeVideoMaxFrameDuration != duration
        guard formatChanged || durationChanged else { return false }
        do {
            try device.lockForConfiguration()
        } catch {
            onEvent?(.error("Não foi possível configurar a câmera: \(error.localizedDescription)"))
            return false
        }
        defer { device.unlockForConfiguration() }
        if formatChanged { device.activeFormat = format }
        // Video-range BT.709 SDR output; sRGB is supported by every format.
        if device.activeColorSpace != .sRGB { device.activeColorSpace = .sRGB }
        // Setting activeFormat resets the frame durations, so they must be set afterwards.
        device.activeVideoMinFrameDuration = duration
        device.activeVideoMaxFrameDuration = duration
        return true
    }

    // MARK: Orientation, mirroring, stabilization

    /// 0 / 180 for landscape output, 90 / 270 for portrait. The base angle is 0 (sensor-native
    /// landscape) or 90 (portrait) -- identical for back and front cameras, because both sensors are
    /// mounted landscape and `videoRotationAngle` is relative to the sensor. We then pick the
    /// 180-degree-flipped variant when gravity (RotationCoordinator) says the phone is upside down.
    private func targetAngle() -> CGFloat {
        let base: CGFloat = config.portrait ? 90 : 0
        guard let rc = rotationCoordinator else { return base }
        let gravityAngle = rc.videoRotationAngleForHorizonLevelCapture
        let flipped = (base + 180).truncatingRemainder(dividingBy: 360)
        func distance(_ a: CGFloat, _ b: CGFloat) -> CGFloat {
            let d = abs((a - b).truncatingRemainder(dividingBy: 360))
            return min(d, 360 - d)
        }
        return distance(gravityAngle, flipped) < distance(gravityAngle, base) ? flipped : base
    }

    private func applyConnectionSettings() {
        guard let connection = videoOutput.connection(with: .video) else { return }
        let angle = targetAngle()
        if connection.isVideoRotationAngleSupported(angle) {
            connection.videoRotationAngle = angle
            appliedAngle = angle
        }
        if connection.isVideoMirroringSupported {
            connection.automaticallyAdjustsVideoMirroring = false
            connection.isVideoMirrored = config.mirror
            appliedMirror = config.mirror
        } else {
            appliedMirror = false
        }
        if connection.isVideoStabilizationSupported, let device = currentDevice {
            connection.preferredVideoStabilizationMode =
                Self.resolveStabilization(controls.stabilization, format: device.activeFormat)
        }
    }

    /// Live video must stay low-latency: AVFoundation's `.auto` picks cinematic stabilization on
    /// most 1080p/4K formats, which buffers ~1–1.5 s of frames. Our "auto" therefore only picks
    /// low-latency modes (preview-optimized, then standard, else off). Cinematic modes are used
    /// only when chosen explicitly.
    private static func resolveStabilization(_ name: String, format: AVCaptureDevice.Format) -> AVCaptureVideoStabilizationMode {
        func supported(_ m: AVCaptureVideoStabilizationMode) -> Bool { format.isVideoStabilizationModeSupported(m) }
        switch name {
        case "off":
            return .off
        case "standard":
            return supported(.standard) ? .standard : .off
        case "cinematic":
            return supported(.cinematic) ? .cinematic : (supported(.standard) ? .standard : .off)
        case "cinematicExtended":
            return supported(.cinematicExtended) ? .cinematicExtended
                : (supported(.cinematic) ? .cinematic : (supported(.standard) ? .standard : .off))
        default: // "auto" (and "previewOptimized")
            if #available(iOS 17.0, *), supported(.previewOptimized) { return .previewOptimized }
            return supported(.standard) ? .standard : .off
        }
    }

    private static func stabilizationName(_ mode: AVCaptureVideoStabilizationMode) -> String {
        switch mode {
        case .off: return "off"
        case .standard: return "standard"
        case .cinematic: return "cinematic"
        case .cinematicExtended: return "cinematicExtended"
        case .auto: return "auto"
        default:
            if #available(iOS 17.0, *), mode == .previewOptimized { return "previewOptimized" }
            return "standard"
        }
    }

    private var angleIndex: Int {
        ((Int((Double(appliedAngle) / 90).rounded()) % 4) + 4) % 4
    }

    // MARK: Point of interest

    /// Converts a normalized point in the OUTPUT frame (origin top-left, after rotation and mirroring)
    /// to AVCaptureDevice's normalized sensor space (origin top-left of the landscape-native image).
    ///
    /// Forward pipeline: output = mirror( rotateClockwise(native, angle) ). Inverse below:
    /// 1. undo the horizontal mirror, 2. undo the clockwise rotation.
    ///   rot 90  : native(x,y) -> output(1-y, x)    inverse: (x,y) = (yo, 1-xo)
    ///   rot 180 : native(x,y) -> output(1-x, 1-y)  inverse: (x,y) = (1-xo, 1-yo)
    ///   rot 270 : native(x,y) -> output(y, 1-x)    inverse: (x,y) = (1-yo, xo)
    func devicePoint(fromOutput p: CGPoint, angleIndex: Int, mirrored: Bool) -> CGPoint {
        var x = min(max(p.x, 0), 1)
        var y = min(max(p.y, 0), 1)
        if mirrored { x = 1 - x }
        switch angleIndex {
        case 1: (x, y) = (y, 1 - x)
        case 2: (x, y) = (1 - x, 1 - y)
        case 3: (x, y) = (1 - y, x)
        default: break
        }
        return CGPoint(x: x, y: y)
    }

    // MARK: Controls (session queue)

    private func clamp(_ v: Double, _ lo: Double, _ hi: Double) -> Double {
        guard hi >= lo else { return lo }
        return min(max(v, lo), hi)
    }

    private func applyControlMessage(_ m: ClientMessage, to device: AVCaptureDevice) {
        do {
            try device.lockForConfiguration()
        } catch {
            onEvent?(.error("Não foi possível ajustar a câmera: \(error.localizedDescription)"))
            return
        }
        defer { device.unlockForConfiguration() }

        // Zoom
        if let z = m.zoom, z.isFinite {
            let value = clamp(z, Double(device.minAvailableVideoZoomFactor), Double(device.maxAvailableVideoZoomFactor))
            if device.isRampingVideoZoom { device.cancelVideoZoomRamp() }
            if m.rampZoom == true {
                device.ramp(toVideoZoomFactor: CGFloat(value), withRate: 3.0)
            } else {
                device.videoZoomFactor = CGFloat(value)
            }
            controls.zoom = value
        }

        // Focus
        if let lp = m.lensPosition, lp.isFinite {
            if device.isLockingFocusWithCustomLensPositionSupported {
                device.setFocusModeLocked(lensPosition: Float(clamp(lp, 0, 1)), completionHandler: nil)
                controls.focusMode = "locked"
                controls.lensPosition = lp
            }
        } else if let mode = m.focusMode {
            switch mode {
            case "continuous":
                if device.isFocusModeSupported(.continuousAutoFocus) { device.focusMode = .continuousAutoFocus }
                controls.focusMode = mode; controls.lensPosition = nil
            case "auto":
                if device.isFocusModeSupported(.autoFocus) { device.focusMode = .autoFocus }
                controls.focusMode = mode; controls.lensPosition = nil
            case "locked":
                if device.isFocusModeSupported(.locked) { device.focusMode = .locked }
                controls.focusMode = mode; controls.lensPosition = nil
            default: break
            }
        }

        // Exposure
        if let mode = m.exposureMode {
            switch mode {
            case "continuous":
                if device.isExposureModeSupported(.continuousAutoExposure) { device.exposureMode = .continuousAutoExposure }
                controls.exposureMode = mode
            case "locked":
                if device.isExposureModeSupported(.locked) { device.exposureMode = .locked }
                controls.exposureMode = mode
            case "custom":
                if device.isExposureModeSupported(.custom) {
                    applyCustomExposure(device, iso: m.iso ?? controls.iso, duration: m.exposureDurationSec ?? controls.exposureDuration)
                    controls.exposureMode = mode
                }
            default: break
            }
        } else if controls.exposureMode == "custom", m.iso != nil || m.exposureDurationSec != nil {
            applyCustomExposure(device, iso: m.iso ?? controls.iso, duration: m.exposureDurationSec ?? controls.exposureDuration)
        }
        if let bias = m.exposureBias, bias.isFinite {
            let v = clamp(bias, Double(device.minExposureTargetBias), Double(device.maxExposureTargetBias))
            device.setExposureTargetBias(Float(v), completionHandler: nil)
            controls.exposureBias = v
        }

        // White balance
        if let mode = m.whiteBalanceMode {
            switch mode {
            case "continuous":
                if device.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) { device.whiteBalanceMode = .continuousAutoWhiteBalance }
                controls.whiteBalanceMode = mode
            case "locked":
                if device.isWhiteBalanceModeSupported(.locked) { device.whiteBalanceMode = .locked }
                controls.whiteBalanceMode = mode
            case "custom":
                if device.isLockingWhiteBalanceWithCustomDeviceGainsSupported {
                    applyCustomWhiteBalance(device, temperature: m.temperature ?? controls.temperature, tint: m.tint ?? controls.tint)
                    controls.whiteBalanceMode = mode
                }
            default: break
            }
        } else if controls.whiteBalanceMode == "custom", m.temperature != nil || m.tint != nil {
            applyCustomWhiteBalance(device, temperature: m.temperature ?? controls.temperature, tint: m.tint ?? controls.tint)
        }

        // Point of interest (focus + exposure), given in OUTPUT frame coordinates.
        if let poi = m.pointOfInterest, poi.x.isFinite, poi.y.isFinite {
            let p = devicePoint(fromOutput: CGPoint(x: poi.x, y: poi.y), angleIndex: angleIndex, mirrored: appliedMirror)
            if device.isFocusPointOfInterestSupported {
                device.focusPointOfInterest = p
                if controls.focusMode == "continuous" {
                    if device.isFocusModeSupported(.continuousAutoFocus) { device.focusMode = .continuousAutoFocus }
                } else {
                    if device.isFocusModeSupported(.autoFocus) { device.focusMode = .autoFocus }
                    controls.focusMode = "auto"; controls.lensPosition = nil
                }
            }
            if device.isExposurePointOfInterestSupported, controls.exposureMode != "custom" {
                device.exposurePointOfInterest = p
                if controls.exposureMode == "continuous" {
                    if device.isExposureModeSupported(.continuousAutoExposure) { device.exposureMode = .continuousAutoExposure }
                } else if device.isExposureModeSupported(.autoExpose) {
                    device.exposureMode = .autoExpose   // meter once, then lock
                }
            }
        }

        // Torch
        if let level = m.torch, level.isFinite {
            controls.torch = clamp(level, 0, 1)
            applyTorch(device)
        }

        // Stabilization is applied on the connection by the caller; just remember it.
        if let s = m.stabilization { controls.stabilization = s }

        // HDR
        if let hdr = m.hdr {
            controls.hdr = hdr
            applyHDR(device)
        }

        // Low light boost
        if let boost = m.lowLightBoost {
            controls.lowLightBoost = boost
            if device.isLowLightBoostSupported {
                device.automaticallyEnablesLowLightBoostWhenAvailable = boost
            }
        }

        // Center Stage (front camera, class-level setting)
        if let cs = m.centerStage {
            controls.centerStage = cs
            applyCenterStage(device)
        }
    }

    private func applyCustomExposure(_ device: AVCaptureDevice, iso: Double?, duration: Double?) {
        let format = device.activeFormat
        let minDuration = CMTimeGetSeconds(format.minExposureDuration)
        var maxDuration = CMTimeGetSeconds(format.maxExposureDuration)
        let activeMax = CMTimeGetSeconds(device.activeMaxExposureDuration)
        if activeMax.isFinite, activeMax > 0 { maxDuration = min(maxDuration, activeMax) }
        maxDuration = max(maxDuration, minDuration)
        let currentDuration = CMTimeGetSeconds(device.exposureDuration)
        let d = clamp(finite(duration ?? currentDuration, minDuration), minDuration, maxDuration)
        let i = clamp(finite(iso ?? Double(device.iso), Double(format.minISO)), Double(format.minISO), Double(format.maxISO))
        device.setExposureModeCustom(duration: CMTimeMakeWithSeconds(d, preferredTimescale: 1_000_000_000),
                                     iso: Float(i), completionHandler: nil)
        controls.iso = i
        controls.exposureDuration = d
    }

    private func validGains(_ g: AVCaptureDevice.WhiteBalanceGains, max maxGain: Float) -> Bool {
        func ok(_ v: Float) -> Bool { v.isFinite && v >= 1.0 && v <= maxGain }
        return ok(g.redGain) && ok(g.greenGain) && ok(g.blueGain)
    }

    private func clampGains(_ g: AVCaptureDevice.WhiteBalanceGains, max maxGain: Float) -> AVCaptureDevice.WhiteBalanceGains {
        func c(_ v: Float) -> Float { v.isFinite ? min(max(v, 1.0), maxGain) : 1.0 }
        return AVCaptureDevice.WhiteBalanceGains(redGain: c(g.redGain), greenGain: c(g.greenGain), blueGain: c(g.blueGain))
    }

    private func currentTemperatureAndTint(_ device: AVCaptureDevice) -> (Double, Double)? {
        let gains = device.deviceWhiteBalanceGains
        guard validGains(gains, max: device.maxWhiteBalanceGain) else { return nil }
        let tt = device.temperatureAndTintValues(for: gains)
        guard tt.temperature.isFinite, tt.tint.isFinite else { return nil }
        return (Double(tt.temperature), Double(tt.tint))
    }

    private func applyCustomWhiteBalance(_ device: AVCaptureDevice, temperature: Double?, tint: Double?) {
        let current = currentTemperatureAndTint(device)
        let t = clamp(finite(temperature ?? current?.0 ?? 5000, 5000), 2000, 10000)
        let ti = clamp(finite(tint ?? current?.1 ?? 0, 0), -150, 150)
        let tt = AVCaptureDevice.WhiteBalanceTemperatureAndTintValues(temperature: Float(t), tint: Float(ti))
        let gains = clampGains(device.deviceWhiteBalanceGains(for: tt), max: device.maxWhiteBalanceGain)
        device.setWhiteBalanceModeLocked(with: gains, completionHandler: nil)
        controls.temperature = t
        controls.tint = ti
    }

    private func applyTorch(_ device: AVCaptureDevice) {
        guard device.hasTorch else { return }
        if controls.torch <= 0 {
            if device.torchMode != .off { device.torchMode = .off }
        } else if device.isTorchAvailable {
            do {
                try device.setTorchModeOn(level: Float(clamp(controls.torch, 0.01, 1.0)))
            } catch {
                onEvent?(.error("Não foi possível ligar a lanterna"))
            }
        }
    }

    private func applyHDR(_ device: AVCaptureDevice) {
        guard device.activeFormat.isVideoHDRSupported else { return }
        if controls.hdr {
            device.automaticallyAdjustsVideoHDREnabled = true
        } else {
            // isVideoHDREnabled may only be set while automatic adjustment is off.
            device.automaticallyAdjustsVideoHDREnabled = false
            device.isVideoHDREnabled = false
        }
    }

    private func applyCenterStage(_ device: AVCaptureDevice) {
        guard device.position == .front, device.activeFormat.isCenterStageSupported else { return }
        if AVCaptureDevice.centerStageControlMode != .cooperative {
            AVCaptureDevice.centerStageControlMode = .cooperative
        }
        if AVCaptureDevice.isCenterStageEnabled != controls.centerStage {
            AVCaptureDevice.isCenterStageEnabled = controls.centerStage
        }
    }

    // MARK: Snapshot

    private func emitSnapshot() {
        onUpdate?(makeSnapshot())
    }

    private func makeSnapshot() -> CameraSnapshot {
        var s = CameraSnapshot()
        s.sessionRunning = session.isRunning
        guard let d = currentDevice else { return s }
        s.hasDevice = true
        s.cameraId = d.uniqueID

        let format = d.activeFormat
        let dim = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
        s.sensorWidth = Int(dim.width)
        s.sensorHeight = Int(dim.height)
        let rotated = angleIndex % 2 == 1
        s.portrait = rotated
        s.outputWidth = rotated ? s.sensorHeight : s.sensorWidth
        s.outputHeight = rotated ? s.sensorWidth : s.sensorHeight
        let frameSeconds = CMTimeGetSeconds(d.activeVideoMinFrameDuration)
        s.fps = frameSeconds > 0 ? Int((1.0 / frameSeconds).rounded()) : 0
        s.mirror = appliedMirror
        s.rotationAngle = Double(appliedAngle)

        s.zoom = finite(Double(d.videoZoomFactor), 1)
        s.minZoom = finite(Double(d.minAvailableVideoZoomFactor), 1)
        s.maxZoom = finite(Double(d.maxAvailableVideoZoomFactor), 1)

        s.focusMode = controls.focusMode
        s.lensPosition = finite(Double(d.lensPosition))

        s.exposureMode = controls.exposureMode
        s.iso = finite(Double(d.iso))
        s.exposureDurationSec = finite(CMTimeGetSeconds(d.exposureDuration))
        s.minISO = finite(Double(format.minISO))
        s.maxISO = finite(Double(format.maxISO))
        s.minExposureDurationSec = finite(CMTimeGetSeconds(format.minExposureDuration))
        var maxDuration = finite(CMTimeGetSeconds(format.maxExposureDuration))
        let activeMax = CMTimeGetSeconds(d.activeMaxExposureDuration)
        if activeMax.isFinite, activeMax > 0 { maxDuration = min(maxDuration, activeMax) }
        s.maxExposureDurationSec = max(maxDuration, s.minExposureDurationSec)
        s.exposureBias = controls.exposureBias

        s.whiteBalanceMode = controls.whiteBalanceMode
        if let tt = currentTemperatureAndTint(d) {
            s.temperature = tt.0
            s.tint = tt.1
        }

        s.hasTorch = d.hasTorch
        s.torch = (d.hasTorch && d.torchMode == .on) ? finite(Double(d.torchLevel)) : 0

        s.stabilization = controls.stabilization
        if let c = videoOutput.connection(with: .video) {
            s.activeStabilization = Self.stabilizationName(c.activeVideoStabilizationMode)
        }
        s.hdrSupported = format.isVideoHDRSupported
        s.hdr = format.isVideoHDRSupported && (d.automaticallyAdjustsVideoHDREnabled || d.isVideoHDREnabled)
        s.lowLightBoost = d.isLowLightBoostSupported && d.automaticallyEnablesLowLightBoostWhenAvailable
        s.centerStage = d.position == .front && format.isCenterStageSupported && AVCaptureDevice.isCenterStageEnabled
        return s
    }

    // MARK: Notifications

    private func registerNotifications() {
        let nc = NotificationCenter.default
        observers.append(nc.addObserver(forName: AVCaptureSession.runtimeErrorNotification, object: session, queue: nil) { [weak self] note in
            self?.sessionQueue.async { self?.handleRuntimeError(note) }
        })
        observers.append(nc.addObserver(forName: AVCaptureSession.wasInterruptedNotification, object: session, queue: nil) { [weak self] note in
            var reason = "motivo desconhecido"
            if let raw = note.userInfo?[AVCaptureSessionInterruptionReasonKey] as? Int,
               let r = AVCaptureSession.InterruptionReason(rawValue: raw) {
                switch r {
                case .videoDeviceNotAvailableInBackground: reason = "app em segundo plano"
                case .audioDeviceInUseByAnotherClient: reason = "áudio em uso por outro app"
                case .videoDeviceInUseByAnotherClient: reason = "câmera em uso por outro app"
                case .videoDeviceNotAvailableWithMultipleForegroundApps: reason = "vários apps em primeiro plano"
                case .videoDeviceNotAvailableDueToSystemPressure: reason = "temperatura/pressão do sistema"
                default: break
                }
            }
            self?.onEvent?(.interrupted(reason))
        })
        observers.append(nc.addObserver(forName: AVCaptureSession.interruptionEndedNotification, object: session, queue: nil) { [weak self] _ in
            self?.onEvent?(.resumed)
            self?.sessionQueue.async {
                guard let self, self.wantRunning, !self.session.isRunning else { return }
                self.session.startRunning()
            }
        })
    }

    private func handleRuntimeError(_ note: Notification) {
        let error = note.userInfo?[AVCaptureSessionErrorKey] as? AVError
        onEvent?(.error("Erro na câmera (\(error?.code.rawValue ?? 0)); reiniciando…"))
        guard wantRunning else { return }
        // Restart after a short delay (e.g. mediaServicesWereReset).
        sessionQueue.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            guard let self, self.wantRunning else { return }
            if !self.session.isRunning { self.session.startRunning() }
            self.emitSnapshot()
        }
    }
}

// MARK: - Sample buffers

extension CameraManager: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let handler = currentFrameHandler(),
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        handler(pixelBuffer, CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
    }
}
