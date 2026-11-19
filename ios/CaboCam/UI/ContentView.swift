import AVFoundation
import SwiftUI
import UIKit

// MARK: - Camera preview

/// Hosts an AVCaptureVideoPreviewLayer that shares the capture session.
final class PreviewView: UIView {
    override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }

    var previewLayer: AVCaptureVideoPreviewLayer {
        // layerClass guarantees the type.
        layer as! AVCaptureVideoPreviewLayer
    }

    /// Makes the preview show exactly the frame that is being streamed (same rotation / mirroring).
    func apply(angle: Double, mirrored: Bool) {
        guard let connection = previewLayer.connection else { return }
        if connection.isVideoRotationAngleSupported(angle) {
            connection.videoRotationAngle = angle
        }
        if connection.isVideoMirroringSupported {
            connection.automaticallyAdjustsVideoMirroring = false
            connection.isVideoMirrored = mirrored
        }
    }
}

struct CameraPreview: UIViewRepresentable {
    let session: AVCaptureSession
    let angle: Double
    let mirrored: Bool

    func makeUIView(context: Context) -> PreviewView {
        let view = PreviewView()
        view.backgroundColor = .black
        view.previewLayer.session = session
        view.previewLayer.videoGravity = .resizeAspect
        return view
    }

    func updateUIView(_ view: PreviewView, context: Context) {
        view.apply(angle: angle, mirrored: mirrored)
    }
}

// MARK: - Content

struct ContentView: View {
    @ObservedObject var model: AppModel
    @State private var zoomDragging = false
    @State private var zoomSliderValue = 0.0

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            GeometryReader { geo in
                if geo.size.width > geo.size.height {
                    HStack(spacing: 16) {
                        previewArea
                        ScrollView {
                            VStack(spacing: 14) { header; controls }
                                .padding(.vertical, 4)
                        }
                        .frame(width: min(geo.size.width * 0.42, 380))
                    }
                    .padding(16)
                } else {
                    VStack(spacing: 12) {
                        header
                        previewArea
                        controls
                    }
                    .padding(16)
                }
            }

            if model.dimScreen {
                dimOverlay
            }
        }
        .preferredColorScheme(.dark)
        .statusBarHidden(model.dimScreen)
        .persistentSystemOverlays(model.dimScreen ? .hidden : .automatic)
    }

    // MARK: Pieces

    private var header: some View {
        VStack(spacing: 6) {
            HStack(spacing: 8) {
                Circle()
                    .fill(statusColor)
                    .frame(width: 10, height: 10)
                Text(model.statusText)
                    .font(.headline)
                    .lineLimit(2)
                    .minimumScaleFactor(0.8)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            if let notice = model.notice ?? model.networkNotice {
                Text(notice)
                    .font(.footnote)
                    .foregroundStyle(.yellow)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            Text(statsLine)
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var statusColor: Color {
        if model.streaming { return .green }
        if model.isClientConnected { return .yellow }
        return .gray
    }

    private var statsLine: String {
        let fps = String(format: "%.0f fps", model.statsFps)
        let mbps = String(format: "%.1f Mbps", model.statsMbps).replacingOccurrences(of: ".", with: ",")
        var parts = [fps, mbps, "Temp.: \(model.thermalText)"]
        if let level = model.batteryLevel {
            parts.append("Bateria: \(Int((level * 100).rounded()))%" + (model.charging ? " ⚡︎" : ""))
        }
        return parts.joined(separator: " · ")
    }

    private var previewArea: some View {
        CameraPreview(session: model.camera.session,
                      angle: model.snapshot.rotationAngle,
                      mirrored: model.snapshot.mirror)
            .clipShape(RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.white.opacity(0.12), lineWidth: 1))
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .accessibilityLabel("Pré-visualização da câmera")
    }

    private var controls: some View {
        VStack(spacing: 14) {
            cameraPicker
            zoomControl
            HStack(spacing: 12) {
                torchButton
            }
            dimButton
        }
    }

    private var cameraPicker: some View {
        HStack(spacing: 8) {
            ForEach(model.lensPresets) { preset in
                lensButton(preset.label, selected: model.selectedPresetLabel == preset.label) {
                    model.selectLens(preset)
                }
            }
            if model.hasFrontCamera {
                lensButton("Frontal", selected: model.isFrontSelected) {
                    model.selectFront()
                }
            }
        }
        .frame(maxWidth: .infinity)
    }

    private func lensButton(_ title: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.subheadline.weight(.semibold))
                .frame(minWidth: 44, minHeight: 36)
                .padding(.horizontal, 8)
                .background(selected ? Color.yellow : Color.white.opacity(0.14), in: Capsule())
                .foregroundStyle(selected ? Color.black : Color.white)
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Câmera \(title)")
    }

    // Zoom slider on a logarithmic scale between the camera's min and a sane maximum.
    private var zoomRange: (min: Double, max: Double) {
        let info = model.currentCamera
        let mult = max(info?.zoomDisplayMultiplier ?? 1, 0.01)
        let lo = max(model.snapshot.minZoom, 0.1)
        let hi = max(min(model.snapshot.maxZoom, 15.0 / mult), lo * 1.01)
        return (lo, hi)
    }

    private func sliderPosition(forZoom zoom: Double) -> Double {
        let r = zoomRange
        let z = min(max(zoom, r.min), r.max)
        return log(z / r.min) / log(r.max / r.min)
    }

    private func zoom(forSliderPosition t: Double) -> Double {
        let r = zoomRange
        return r.min * pow(r.max / r.min, min(max(t, 0), 1))
    }

    private var zoomControl: some View {
        let mult = model.currentCamera?.zoomDisplayMultiplier ?? 1
        let shownZoom = zoomDragging ? zoom(forSliderPosition: zoomSliderValue) : model.snapshot.zoom
        return VStack(spacing: 4) {
            HStack {
                Text("Zoom")
                    .font(.subheadline)
                Spacer()
                Text(AppModel.formatMultiplier(shownZoom * mult))
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(.yellow)
            }
            Slider(
                value: Binding(
                    get: { zoomDragging ? zoomSliderValue : sliderPosition(forZoom: model.snapshot.zoom) },
                    set: { newValue in
                        zoomSliderValue = newValue
                        model.setZoom(zoom(forSliderPosition: newValue))
                    }),
                in: 0 ... 1,
                onEditingChanged: { editing in
                    if editing { zoomSliderValue = sliderPosition(forZoom: model.snapshot.zoom) }
                    zoomDragging = editing
                })
            .tint(.yellow)
            .accessibilityLabel("Zoom")
        }
    }

    private var torchButton: some View {
        let on = model.snapshot.torch > 0
        return Button {
            model.setTorch(!on)
        } label: {
            Label(on ? "Lanterna ligada" : "Lanterna", systemImage: on ? "flashlight.on.fill" : "flashlight.off.fill")
                .frame(maxWidth: .infinity, minHeight: 44)
        }
        .buttonStyle(.bordered)
        .tint(on ? .yellow : .gray)
        .disabled(!model.snapshot.hasTorch)
    }

    private var dimButton: some View {
        Button {
            model.setDim(true)
        } label: {
            Label("Escurecer tela", systemImage: "moon.fill")
                .font(.title3.weight(.bold))
                .frame(maxWidth: .infinity, minHeight: 60)
        }
        .buttonStyle(.borderedProminent)
        .tint(.indigo)
    }

    private var dimOverlay: some View {
        Color.black
            .ignoresSafeArea()
            .overlay(
                Text("Toque duas vezes para sair")
                    .font(.footnote)
                    .foregroundStyle(Color.white.opacity(0.12))
            )
            .contentShape(Rectangle())
            .onTapGesture(count: 2) { model.setDim(false) }
            .accessibilityLabel("Tela escurecida. Toque duas vezes para sair.")
    }
}

#Preview {
    ContentView(model: AppModel.preview())
}
