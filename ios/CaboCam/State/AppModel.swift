import AVFoundation
import Combine
import SwiftUI
import UIKit

/// Hops to the main thread (FIFO) and runs the block main-actor isolated.
func runOnMain(_ block: @escaping @MainActor @Sendable () -> Void) {
    DispatchQueue.main.async {
        MainActor.assumeIsolated { block() }
    }
}

/// A lens button like in the Camera app ("0,5×", "1×", "3×"…).
struct LensPreset: Identifiable, Equatable {
    let label: String
    let zoom: Double          // raw videoZoomFactor
    var id: String { label }
}

@MainActor
final class AppModel: ObservableObject {

    // MARK: Published UI state

    @Published private(set) var cameras: [CameraInfo] = []
    @Published private(set) var snapshot = CameraSnapshot()
    @Published private(set) var isClientConnected = false
    @Published private(set) var peerName: String?
    @Published private(set) var streaming = false
    @Published private(set) var dimScreen = false
    @Published private(set) var codec: VideoCodec = .h264
    @Published private(set) var bitrateKbps = 12000
    @Published private(set) var statsFps = 0.0
    @Published private(set) var statsMbps = 0.0
    @Published private(set) var thermalState: ProcessInfo.ThermalState = .nominal
    @Published private(set) var batteryLevel: Double?
    @Published private(set) var charging = false
    @Published private(set) var notice: String?
    @Published private(set) var networkNotice: String?

    // MARK: Components

    let camera: CameraManager
    private let server: StreamServer
    private let encoder: VideoEncoder

    // MARK: Internal state (main actor)

    private var desired = CaptureConfig()              // fps = what the desktop asked for
    private var encoderConfig = EncoderConfig()        // fps = effective fps
    private var thermalFpsCap: Int?
    private var pendingAnnounce = false
    private var broadcastScheduled = false
    private var lastStats: (fps: Double, kbps: Double, dropped: Int) = (0, 0, 0)
    private var tickTask: Task<Void, Never>?
    private var savedBrightness: CGFloat?
    private var started = false
    private var observers: [NSObjectProtocol] = []
    private var noticeClearTask: Task<Void, Never>?

    // MARK: Init

    init() {
        let camera = CameraManager()
        let server = StreamServer(helloPayload: AppModel.makeHelloPayload())
        let encoder = VideoEncoder(onFrame: { [server] frame in server.sendVideo(frame) })
        self.camera = camera
        self.server = server
        self.encoder = encoder
        self.cameras = camera.cameras
        wireCallbacks()
    }

    private static func makeHelloPayload() -> Data {
        let hello = HelloMessage(
            protocolVersion: Wire.protocolVersion,
            app: "CaboCam-iOS",
            version: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0",
            deviceName: UIDevice.current.name,   // iOS 16+ returns the generic model name without a special entitlement
            model: machineIdentifier(),
            systemVersion: UIDevice.current.systemVersion
        )
        return JSONCoding.encode(hello) ?? Data("{}".utf8)
    }

    private static func machineIdentifier() -> String {
        var info = utsname()
        uname(&info)
        return Mirror(reflecting: info.machine).children.reduce(into: "") { result, element in
            if let v = element.value as? Int8, v > 0 { result.append(Character(UnicodeScalar(UInt8(v)))) }
        }
    }

    private func wireCallbacks() {
        encoder.onError = { [weak self] message in
            runOnMain { self?.sendError(message) }
        }
        camera.onUpdate = { [weak self] snap in
            runOnMain { self?.cameraDidUpdate(snap) }
        }
        camera.onEvent = { [weak self] event in
            runOnMain { self?.cameraEvent(event) }
        }
        server.onClientConnected = { [weak self] endpoint in
            runOnMain { self?.clientConnected(endpoint) }
        }
        server.onClientDisconnected = { [weak self] in
            runOnMain { self?.clientDisconnected() }
        }
        server.onHello = { [weak self] hello in
            runOnMain { self?.receivedHello(hello) }
        }
        server.onMessage = { [weak self] data in
            runOnMain { self?.handleMessage(data) }
        }
        // Called on the network queue: forcing a keyframe is thread-safe, no main hop needed.
        server.onNeedKeyframe = { [encoder] in encoder.forceKeyframe() }
        server.onListenerStatus = { [weak self] problem in
            runOnMain { self?.networkNotice = problem }
        }
    }

    // MARK: App lifecycle

    func start() {
        guard !started else { return }
        started = true
        UIApplication.shared.isIdleTimerDisabled = true
        UIDevice.current.isBatteryMonitoringEnabled = true
        installObservers()
        refreshBattery()
        thermalChanged()
        server.start()
        startTick()

        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .notDetermined:
            Task { [weak self] in
                let granted = await AVCaptureDevice.requestAccess(for: .video)
                guard let self else { return }
                if granted { self.startCamera() } else { self.showNotice(AppModel.deniedText, autoClear: false) }
            }
        default:
            startCamera()
        }
    }

    private static let deniedText = "Sem permissão da câmera. Ative em Ajustes > CaboCam > Câmera."

    private func startCamera() {
        camera.start()
        pushCaptureConfig()
    }

    func scenePhaseChanged(_ phase: ScenePhase) {
        switch phase {
        case .active:
            UIApplication.shared.isIdleTimerDisabled = true
            if dimScreen { applyDimBrightness() }
            refreshBattery()
            thermalChanged()
            server.ensureRunning()
            if started {
                camera.start()
                if streaming { encoder.forceKeyframe() }
            }
        case .background:
            UIApplication.shared.isIdleTimerDisabled = false
            restoreBrightness()
            camera.stop()
        case .inactive:
            break
        @unknown default:
            break
        }
    }

    private func installObservers() {
        let nc = NotificationCenter.default
        let main = OperationQueue.main
        observers.append(nc.addObserver(forName: ProcessInfo.thermalStateDidChangeNotification, object: nil, queue: main) { [weak self] _ in
            MainActor.assumeIsolated { self?.thermalChanged() }
        })
        observers.append(nc.addObserver(forName: UIDevice.batteryLevelDidChangeNotification, object: nil, queue: main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshBattery() }
        })
        observers.append(nc.addObserver(forName: UIDevice.batteryStateDidChangeNotification, object: nil, queue: main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshBattery() }
        })
    }

    private func startTick() {
        tickTask?.cancel()
        tickTask = Task { [weak self] in
            var n = 0
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                guard let self else { return }
                n += 1
                self.tick(n)
            }
        }
    }

    private func tick(_ n: Int) {
        if streaming {
            lastStats = server.stats.sample()
            statsFps = lastStats.fps
            statsMbps = lastStats.kbps / 1000
        } else {
            lastStats = (0, 0, lastStats.dropped)
            statsFps = 0
            statsMbps = 0
        }
        if n % 2 == 0 {
            // Refresh live camera values (ISO, lens position…); the update also broadcasts `state`.
            camera.requestSnapshot()
        }
    }

    // MARK: Battery / thermal

    private func refreshBattery() {
        let device = UIDevice.current
        batteryLevel = device.batteryLevel >= 0 ? Double(device.batteryLevel) : nil
        charging = device.batteryState == .charging || device.batteryState == .full
        scheduleBroadcast()
    }

    private func thermalChanged() {
        let state = ProcessInfo.processInfo.thermalState
        thermalState = state
        let cap: Int? = (state == .serious || state == .critical) ? 30 : nil
        if cap != thermalFpsCap {
            thermalFpsCap = cap
            pushCaptureConfig()
        }
        scheduleBroadcast()
    }

    private var effectiveFps: Int {
        min(desired.fps, thermalFpsCap ?? Int.max)
    }

    // MARK: Capture / encoder configuration

    private func pushCaptureConfig() {
        var config = desired
        config.fps = effectiveFps
        camera.apply(config)
        encoderConfig.fps = effectiveFps
        encoder.update(encoderConfig)
    }

    // MARK: Network events (main actor)

    private func clientConnected(_ endpoint: String) {
        if streaming { stopStreaming(notify: false) }
        isClientConnected = true
        peerName = endpoint
    }

    private func clientDisconnected() {
        if streaming { stopStreaming(notify: false) }
        isClientConnected = false
        peerName = nil
    }

    private func receivedHello(_ hello: HelloMessage) {
        if let app = hello.app, !app.isEmpty {
            peerName = [app, hello.version].compactMap { $0 }.joined(separator: " ")
        }
        sendCapabilities()
        sendStateNow()
    }

    // MARK: Message dispatch

    private func handleMessage(_ data: Data) {
        guard let message = try? JSONCoding.decoder.decode(ClientMessage.self, from: data) else { return }
        switch message.type {
        case "getCapabilities":
            sendCapabilities()
        case "configure":
            configure(message)
        case "start":
            startStreaming()
        case "stop":
            if streaming { stopStreaming(notify: true) }
            sendStateNow()
        case "requestKeyframe":
            encoder.forceKeyframe()
        case "setControl":
            if let dim = message.dimScreen { setDim(dim) }
            camera.setControl(message)
        default:
            break // unknown message types are ignored (ping is answered by the server)
        }
    }

    private func configure(_ m: ClientMessage) {
        var captureChanged = false
        var encoderChanged = false

        if let id = m.cameraId {
            if camera.hasCamera(id: id) {
                if desired.cameraId != id { desired.cameraId = id; captureChanged = true }
            } else {
                sendError("Câmera não encontrada")
            }
        }
        if let w = safeInt(m.width), w > 0, w != desired.width { desired.width = w; captureChanged = true }
        if let h = safeInt(m.height), h > 0, h != desired.height { desired.height = h; captureChanged = true }
        if let f = safeInt(m.fps), f > 0, f != desired.fps { desired.fps = min(f, 240); captureChanged = true }
        if let o = m.orientation {
            let portrait = (o == "portrait")
            if (o == "portrait" || o == "landscape"), portrait != desired.portrait { desired.portrait = portrait; captureChanged = true }
        }
        if let mirror = m.mirror, mirror != desired.mirror { desired.mirror = mirror; captureChanged = true }

        if let c = m.codec, let newCodec = VideoCodec(rawValue: c), newCodec != encoderConfig.codec {
            encoderConfig.codec = newCodec
            codec = newCodec
            encoderChanged = true
        }
        if let b = safeInt(m.bitrateKbps) {
            let clamped = min(max(b, 500), 100_000)
            if clamped != encoderConfig.bitrateKbps {
                encoderConfig.bitrateKbps = clamped
                bitrateKbps = clamped
                encoderChanged = true
            }
        }
        if let k = m.keyframeIntervalSec, k.isFinite {
            let clamped = min(max(k, 0.5), 10)
            if clamped != encoderConfig.keyframeIntervalSec { encoderConfig.keyframeIntervalSec = clamped; encoderChanged = true }
        }

        if captureChanged { pushCaptureConfig() }   // also updates the encoder
        else if encoderChanged { encoder.update(encoderConfig) }

        if streaming {
            if captureChanged {
                pendingAnnounce = true          // announced when the camera reports the new format
            } else if encoderChanged {
                encoder.forceKeyframe()
                announceStream()
            }
        }
        if !captureChanged { scheduleBroadcast() }
    }

    // MARK: Streaming

    private func startStreaming() {
        guard isClientConnected else { return }
        guard !camera.cameras.isEmpty else {
            sendError("Nenhuma câmera disponível")
            return
        }
        streaming = true
        server.stats.reset()
        lastStats = (0, 0, 0)
        encoder.update(encoderConfig)
        encoder.forceKeyframe()
        camera.setFrameHandler { [encoder] pixelBuffer, pts in
            encoder.encode(pixelBuffer, pts: pts)
        }
        camera.start()
        if snapshot.outputWidth > 0 {
            announceStream()
        } else {
            pendingAnnounce = true
            camera.requestSnapshot()
        }
        sendStateNow()
    }

    private func stopStreaming(notify: Bool) {
        streaming = false
        pendingAnnounce = false
        camera.setFrameHandler(nil)
        encoder.invalidate()
        statsFps = 0
        statsMbps = 0
        if notify && isClientConnected {
            server.sendMessage(StreamStoppedMessage())
        }
    }

    private func announceStream() {
        pendingAnnounce = false
        guard streaming, isClientConnected, snapshot.outputWidth > 0 else { return }
        server.sendMessage(StreamStartedMessage(
            codec: encoderConfig.codec.rawValue,
            width: snapshot.outputWidth,
            height: snapshot.outputHeight,
            fps: snapshot.fps))
    }

    // MARK: Camera events (main actor)

    private func cameraDidUpdate(_ new: CameraSnapshot) {
        let old = snapshot
        snapshot = new
        if streaming, new.outputWidth > 0 {
            let formatChanged = old.outputWidth != new.outputWidth || old.outputHeight != new.outputHeight
                || old.fps != new.fps || old.cameraId != new.cameraId
            if pendingAnnounce || formatChanged {
                encoder.forceKeyframe()
                announceStream()
            }
        }
        scheduleBroadcast()
    }

    private func cameraEvent(_ event: CameraEvent) {
        switch event {
        case .interrupted(let reason):
            showNotice("Câmera interrompida (\(reason))", autoClear: false)
        case .resumed:
            notice = nil
            if streaming { encoder.forceKeyframe() }
        case .error(let message):
            showNotice(message, autoClear: true)
            sendError(message)
        case .authorizationDenied:
            showNotice(AppModel.deniedText, autoClear: false)
        }
    }

    private func showNotice(_ text: String, autoClear: Bool) {
        notice = text
        noticeClearTask?.cancel()
        guard autoClear else { return }
        noticeClearTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 6_000_000_000)
            guard !Task.isCancelled else { return }
            self?.notice = nil
        }
    }

    // MARK: Outgoing messages

    private func sendError(_ message: String) {
        guard isClientConnected else { return }
        server.sendMessage(ErrorMessage(message: message))
    }

    private func sendCapabilities() {
        guard isClientConnected else { return }
        server.sendMessage(CapabilitiesMessage(cameras: camera.cameras))
    }

    private func sendStateNow() {
        guard isClientConnected else { return }
        server.sendMessage(buildState())
    }

    /// Coalesces bursts of changes (sliders…) into one `state` every ~60 ms.
    private func scheduleBroadcast() {
        guard isClientConnected, !broadcastScheduled else { return }
        broadcastScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.06) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.broadcastScheduled = false
                self.sendStateNow()
            }
        }
    }

    private static func thermalName(_ s: ProcessInfo.ThermalState) -> String {
        switch s {
        case .nominal: return "nominal"
        case .fair: return "fair"
        case .serious: return "serious"
        case .critical: return "critical"
        @unknown default: return "nominal"
        }
    }

    private func buildState() -> StateMessage {
        let s = snapshot
        let portrait = s.hasDevice ? s.portrait : desired.portrait
        return StateMessage(
            streaming: streaming,
            cameraId: s.cameraId,
            // Landscape sensor format (same convention as `configure`); `orientation` gives the output shape.
            width: s.sensorWidth,
            height: s.sensorHeight,
            fps: s.fps,
            codec: encoderConfig.codec.rawValue,
            bitrateKbps: encoderConfig.bitrateKbps,
            orientation: portrait ? "portrait" : "landscape",
            mirror: s.hasDevice ? s.mirror : desired.mirror,
            zoom: finite(s.zoom, 1),
            focusMode: s.focusMode,
            lensPosition: finite(s.lensPosition),
            exposureMode: s.exposureMode,
            iso: finite(s.iso),
            exposureDurationSec: finite(s.exposureDurationSec),
            minISO: finite(s.minISO),
            maxISO: finite(s.maxISO),
            minExposureDurationSec: finite(s.minExposureDurationSec),
            maxExposureDurationSec: finite(s.maxExposureDurationSec),
            exposureBias: finite(s.exposureBias),
            whiteBalanceMode: s.whiteBalanceMode,
            temperature: finite(s.temperature, 5000),
            tint: finite(s.tint),
            torch: finite(s.torch),
            stabilization: s.stabilization,
            activeStabilization: s.activeStabilization,
            hdr: s.hdr,
            hdrSupported: s.hdrSupported,
            lowLightBoost: s.lowLightBoost,
            centerStage: s.centerStage,
            dimScreen: dimScreen,
            thermalState: AppModel.thermalName(thermalState),
            batteryLevel: batteryLevel,
            charging: charging,
            stats: StatsInfo(fps: finite(lastStats.fps), bitrateKbps: finite(lastStats.kbps), droppedFrames: lastStats.dropped)
        )
    }

    // MARK: Screen dimming

    func setDim(_ on: Bool) {
        guard on != dimScreen else { return }
        dimScreen = on
        if on { applyDimBrightness() } else { restoreBrightness() }
        scheduleBroadcast()
    }

    private func applyDimBrightness() {
        if savedBrightness == nil { savedBrightness = UIScreen.main.brightness }
        UIScreen.main.brightness = 0
    }

    private func restoreBrightness() {
        if let b = savedBrightness {
            UIScreen.main.brightness = b
            savedBrightness = nil
        }
    }

    // MARK: Phone-side controls (broadcast to the desktop through `state`)

    var currentCamera: CameraInfo? {
        cameras.first { $0.id == snapshot.cameraId }
    }

    var isFrontSelected: Bool { currentCamera?.position == "front" }

    var hasFrontCamera: Bool { camera.primaryFrontId != nil }

    /// Lens buttons for the primary back camera.
    var lensPresets: [LensPreset] {
        guard let id = camera.primaryBackId, let info = cameras.first(where: { $0.id == id }) else { return [] }
        return AppModel.lensPresets(for: info)
    }

    /// Index of the preset matching the current zoom (nil when the front camera / another lens is active).
    var selectedPresetLabel: String? {
        guard let id = camera.primaryBackId, snapshot.cameraId == id else { return nil }
        let presets = lensPresets
        return presets.last(where: { $0.zoom <= snapshot.zoom + 0.02 })?.label ?? presets.first?.label
    }

    static func formatMultiplier(_ value: Double) -> String {
        let rounded = (value * 10).rounded() / 10
        if abs(rounded - rounded.rounded()) < 0.001 { return "\(Int(rounded.rounded()))×" }
        return String(format: "%.1f", rounded).replacingOccurrences(of: ".", with: ",") + "×"
    }

    static func lensPresets(for info: CameraInfo) -> [LensPreset] {
        let mult = info.zoomDisplayMultiplier > 0 ? info.zoomDisplayMultiplier : 1
        var zooms = [info.minZoom] + info.zoomSwitchOver.filter { $0 > info.minZoom }
        // Camera-app style extra "2×" (crop of the main lens) when the next lens is far away.
        let displays = zooms.map { $0 * mult }
        if displays.contains(where: { abs($0 - 1) < 0.05 }) {
            let twoX = 2.0 / mult
            let nextDisplay = displays.first(where: { $0 > 1.05 })
            if twoX <= info.maxZoom, !displays.contains(where: { abs($0 - 2) < 0.15 }),
               nextDisplay == nil || nextDisplay! > 2.5 {
                zooms.append(twoX)
            }
        }
        zooms.sort()
        var seen = Set<String>()
        var presets: [LensPreset] = []
        for z in zooms {
            let label = formatMultiplier(z * mult)
            if seen.insert(label).inserted { presets.append(LensPreset(label: label, zoom: z)) }
        }
        return presets
    }

    func selectLens(_ preset: LensPreset) {
        guard let id = camera.primaryBackId else { return }
        let sameCamera = snapshot.cameraId == id
        if !sameCamera {
            desired.cameraId = id
            pushCaptureConfig()
        }
        var m = ClientMessage(type: "setControl")
        m.zoom = preset.zoom
        m.rampZoom = sameCamera
        camera.setControl(m)
    }

    func selectFront() {
        guard let id = camera.primaryFrontId, snapshot.cameraId != id else { return }
        desired.cameraId = id
        pushCaptureConfig()
    }

    func setZoom(_ zoom: Double) {
        var m = ClientMessage(type: "setControl")
        m.zoom = zoom
        camera.setControl(m)
    }

    func setTorch(_ on: Bool) {
        var m = ClientMessage(type: "setControl")
        m.torch = on ? 1 : 0
        camera.setControl(m)
    }

    // MARK: Status text

    var statusText: String {
        if streaming {
            let w = snapshot.outputWidth > 0 ? snapshot.outputWidth : desired.width
            let h = snapshot.outputHeight > 0 ? snapshot.outputHeight : desired.height
            let fps = snapshot.fps > 0 ? snapshot.fps : effectiveFps
            return "Transmitindo \(w)×\(h) @\(fps) \(encoderConfig.codec.displayName)"
        }
        if isClientConnected {
            return "Conectado: \(peerName ?? "computador")"
        }
        return "Aguardando computador via cabo…"
    }

    var thermalText: String {
        switch thermalState {
        case .nominal: return "Normal"
        case .fair: return "Morno"
        case .serious: return "Quente"
        case .critical: return "Crítico"
        @unknown default: return "Normal"
        }
    }

    // MARK: Preview support

    static func preview() -> AppModel {
        let model = AppModel()
        model.batteryLevel = 0.83
        model.charging = true
        model.statsFps = 29.9
        model.statsMbps = 11.8
        return model
    }
}
