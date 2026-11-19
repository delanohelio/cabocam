import Foundation
import Network

/// Thread-safe counters for the streaming statistics.
final class StreamStats: @unchecked Sendable {
    private let lock = NSLock()
    private var sentFrames = 0
    private var sentBytes = 0
    private var droppedTotal = 0
    private var lastSampleTime = DispatchTime.now()
    private var lastFrames = 0
    private var lastBytes = 0
    private var lastFps = 0.0
    private var lastKbps = 0.0

    func recordSent(bytes: Int) {
        lock.lock(); defer { lock.unlock() }
        sentFrames += 1
        sentBytes += bytes
    }

    func recordDropped() {
        lock.lock(); defer { lock.unlock() }
        droppedTotal += 1
    }

    func reset() {
        lock.lock(); defer { lock.unlock() }
        sentFrames = 0; sentBytes = 0; droppedTotal = 0
        lastFrames = 0; lastBytes = 0; lastFps = 0; lastKbps = 0
        lastSampleTime = DispatchTime.now()
    }

    /// Rates since the previous call to `sample()`.
    func sample() -> (fps: Double, kbps: Double, dropped: Int) {
        lock.lock(); defer { lock.unlock() }
        let now = DispatchTime.now()
        let dt = Double(now.uptimeNanoseconds &- lastSampleTime.uptimeNanoseconds) / 1e9
        if dt >= 0.5 {
            lastFps = Double(sentFrames - lastFrames) / dt
            lastKbps = Double(sentBytes - lastBytes) * 8.0 / 1000.0 / dt
            lastFrames = sentFrames
            lastBytes = sentBytes
            lastSampleTime = now
        }
        return (lastFps, lastKbps, droppedTotal)
    }
}

/// TCP server on port 7878. Accepts a single client at a time (a new client replaces the old one).
/// All state is confined to `queue`; callbacks are invoked on `queue` and must hop to the main
/// thread themselves if they need UI state.
final class StreamServer: @unchecked Sendable {

    // Callbacks: set them before calling `start()`.
    var onClientConnected: (@Sendable (_ endpoint: String) -> Void)?
    var onClientDisconnected: (@Sendable () -> Void)?
    var onHello: (@Sendable (HelloMessage) -> Void)?
    var onMessage: (@Sendable (Data) -> Void)?
    /// The server dropped video frames and now needs an IDR to resume a decodable stream.
    var onNeedKeyframe: (@Sendable () -> Void)?
    /// nil = listening OK, otherwise a human readable problem.
    var onListenerStatus: (@Sendable (String?) -> Void)?

    let stats = StreamStats()

    // Backpressure thresholds (bytes accepted by `send` but not yet processed by the network stack).
    private static let softLimit = 2 * 1024 * 1024      // above: drop non-key frames
    private static let hardLimit = 8 * 1024 * 1024      // above: drop everything incl. keyframes
    private static let lowWatermark = 256 * 1024         // below: ask for a new keyframe after drops

    private let queue = DispatchQueue(label: "com.cabocam.network")
    private let helloPayload: Data

    // Queue-confined state.
    private var listener: NWListener?
    private var listenerReady = false
    private var connection: NWConnection?
    private var connectionReady = false
    private var generation = 0
    private var reader = PacketReader()
    private var pendingBytes = 0
    private var awaitingKeyframe = false
    private var keyframeRequested = false
    private var keyframeRequestTime = DispatchTime.now()
    private var stopped = false

    init(helloPayload: Data) {
        self.helloPayload = helloPayload
    }

    // MARK: Lifecycle

    func start() {
        queue.async {
            self.stopped = false
            self.startListener()
        }
    }

    /// Re-creates the listener if it is not healthy (e.g. after returning from background).
    func ensureRunning() {
        queue.async {
            guard !self.stopped else { return }
            if self.listener == nil || !self.listenerReady {
                self.listener?.cancel()
                self.listener = nil
                self.listenerReady = false
                self.startListener()
            }
        }
    }

    func stop() {
        queue.async {
            self.stopped = true
            self.listener?.cancel()
            self.listener = nil
            self.listenerReady = false
            if let c = self.connection { self.drop(c, notify: true) }
        }
    }

    private func startListener() {
        guard listener == nil else { return }
        do {
            let tcp = NWProtocolTCP.Options()
            tcp.noDelay = true
            tcp.enableKeepalive = true
            tcp.keepaliveIdle = 5
            tcp.keepaliveInterval = 2
            tcp.keepaliveCount = 3
            let params = NWParameters(tls: nil, tcp: tcp)
            params.allowLocalEndpointReuse = true
            guard let port = NWEndpoint.Port(rawValue: Wire.port) else { return }
            let l = try NWListener(using: params, on: port)
            l.stateUpdateHandler = { [weak self, weak l] state in
                guard let self, let l else { return }
                self.handleListenerState(state, l)
            }
            l.newConnectionHandler = { [weak self] c in
                self?.accept(c)
            }
            listener = l
            l.start(queue: queue)
        } catch {
            onListenerStatus?("Não foi possível abrir a porta \(Wire.port): \(error.localizedDescription)")
            scheduleListenerRetry()
        }
    }

    private func handleListenerState(_ state: NWListener.State, _ l: NWListener) {
        guard l === listener else { return }
        switch state {
        case .ready:
            listenerReady = true
            onListenerStatus?(nil)
        case .failed(let error):
            listenerReady = false
            l.cancel()
            listener = nil
            onListenerStatus?("Servidor de rede falhou: \(error.localizedDescription)")
            scheduleListenerRetry()
        case .waiting(let error):
            listenerReady = false
            onListenerStatus?("Rede indisponível: \(error.localizedDescription)")
        case .cancelled:
            listenerReady = false
        default:
            break
        }
    }

    private func scheduleListenerRetry() {
        queue.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            guard let self, !self.stopped, self.listener == nil else { return }
            self.startListener()
        }
    }

    // MARK: Connections

    private func accept(_ new: NWConnection) {
        // A new client replaces the old one.
        if let old = connection { drop(old, notify: true) }
        generation += 1
        let gen = generation
        connection = new
        connectionReady = false
        reader = PacketReader()
        pendingBytes = 0
        awaitingKeyframe = false
        keyframeRequested = false
        new.stateUpdateHandler = { [weak self, weak new] state in
            guard let self, let new else { return }
            self.handleConnectionState(state, new, gen)
        }
        new.start(queue: queue)
    }

    private func handleConnectionState(_ state: NWConnection.State, _ c: NWConnection, _ gen: Int) {
        guard gen == generation, c === connection else { return }
        switch state {
        case .ready:
            connectionReady = true
            write(PacketCodec.encode(.hello, payload: helloPayload))
            onClientConnected?(String(describing: c.endpoint))
            receiveLoop(c, gen)
        case .failed, .cancelled:
            drop(c, notify: true)
        case .waiting:
            // The path to the peer is unusable; do not keep a zombie client around.
            drop(c, notify: true)
        default:
            break
        }
    }

    private func drop(_ c: NWConnection, notify: Bool) {
        let wasCurrent = (c === connection)
        c.stateUpdateHandler = nil
        c.cancel()
        guard wasCurrent else { return }
        connection = nil
        connectionReady = false
        pendingBytes = 0
        awaitingKeyframe = false
        keyframeRequested = false
        reader = PacketReader()
        if notify { onClientDisconnected?() }
    }

    // MARK: Receiving

    private func receiveLoop(_ c: NWConnection, _ gen: Int) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self, gen == self.generation, c === self.connection else { return }
            if let data, !data.isEmpty {
                self.reader.append(data)
                if !self.drain(c) { return }
            }
            if error != nil || isComplete {
                self.drop(c, notify: true)
                return
            }
            self.receiveLoop(c, gen)
        }
    }

    /// Returns false if the connection was dropped because of a protocol violation.
    private func drain(_ c: NWConnection) -> Bool {
        do {
            while let packet = try reader.next() {
                switch PacketType(rawValue: packet.type) {
                case .hello:
                    let hello = (try? JSONCoding.decoder.decode(HelloMessage.self, from: packet.payload))
                        ?? HelloMessage(protocolVersion: nil, app: nil, version: nil, deviceName: nil, model: nil, systemVersion: nil)
                    onHello?(hello)
                case .message:
                    handleMessagePayload(packet.payload)
                default:
                    break // unknown / unexpected packet types are ignored
                }
            }
            return true
        } catch {
            drop(c, notify: true)
            return false
        }
    }

    private func handleMessagePayload(_ payload: Data) {
        // Answer pings right here to keep RTT measurements honest.
        if let object = try? JSONSerialization.jsonObject(with: payload) as? [String: Any],
           object["type"] as? String == "ping" {
            var reply: [String: Any] = ["type": "pong"]
            if let t = object["t"] { reply["t"] = t }
            if let data = try? JSONSerialization.data(withJSONObject: reply) {
                write(PacketCodec.encode(.message, payload: data))
            }
            return
        }
        onMessage?(payload)
    }

    // MARK: Sending

    /// Sends a JSON object as a MESSAGE packet.
    func sendMessage(_ json: Data) {
        let packet = PacketCodec.encode(.message, payload: json)
        queue.async { self.write(packet) }
    }

    func sendMessage<T: Encodable>(_ value: T) {
        if let data = JSONCoding.encode(value) { sendMessage(data) }
    }

    /// Sends one encoded access unit. Applies backpressure: when the send queue is backed up, non-key
    /// frames are dropped (and so are all following P-frames until a new keyframe has been sent).
    func sendVideo(_ frame: EncodedFrame) {
        // Packet is built on the caller's thread (encoder callback) to keep the network queue light.
        let packet = PacketCodec.encodeVideo(isKeyframe: frame.isKeyframe, isHEVC: frame.isHEVC,
                                             ptsMicros: frame.ptsMicros, accessUnit: frame.data)
        queue.async {
            guard self.connection != nil, self.connectionReady else { return }

            if frame.isKeyframe {
                if self.pendingBytes > Self.hardLimit {
                    self.noteDrop()
                    self.keyframeRequested = false
                    return
                }
                self.awaitingKeyframe = false
                self.keyframeRequested = false
            } else {
                if self.awaitingKeyframe || self.pendingBytes > Self.softLimit {
                    self.awaitingKeyframe = true
                    self.noteDrop()
                    self.requestKeyframeIfDrained()
                    return
                }
            }
            self.write(packet)
            self.stats.recordSent(bytes: packet.count)
        }
    }

    private func noteDrop() {
        stats.recordDropped()
    }

    /// After drops, ask for a keyframe once the pipe has drained (a keyframe is big; asking while the
    /// queue is still full would only make congestion worse). Re-ask after 2 s in case it got lost.
    private func requestKeyframeIfDrained() {
        let now = DispatchTime.now()
        let elapsed = Double(now.uptimeNanoseconds &- keyframeRequestTime.uptimeNanoseconds) / 1e9
        if keyframeRequested && elapsed > 2.0 { keyframeRequested = false }
        guard !keyframeRequested, pendingBytes < Self.lowWatermark else { return }
        keyframeRequested = true
        keyframeRequestTime = now
        onNeedKeyframe?()
    }

    private func write(_ packet: Data) {
        guard let c = connection, connectionReady else { return }
        let gen = generation
        pendingBytes += packet.count
        let size = packet.count
        c.send(content: packet, contentContext: .defaultStream, isComplete: false,
               completion: .contentProcessed({ [weak self] error in
            // Runs on `queue`.
            guard let self, gen == self.generation else { return }
            self.pendingBytes = max(0, self.pendingBytes - size)
            if error != nil, let current = self.connection {
                self.drop(current, notify: true)
            }
        }))
    }
}
