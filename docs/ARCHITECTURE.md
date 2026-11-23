# CaboCam — Architecture

Use an iPhone camera (back wide / ultra-wide / telephoto / front) as a webcam on a
Windows 11 PC, over the Lightning/USB-C cable.

```
┌──────────────────────── iPhone (ios/, Swift, iOS 17+) ───────────────────────┐
│ AVCaptureSession ─► VTCompressionSession (H.264 / HEVC, realtime, no B-frames)│
│        ▲                     │ Annex-B access units                          │
│  control (JSON)              ▼                                               │
│        └──────── NWListener TCP :7878  (one client at a time)                │
└──────────────────────────────────┬───────────────────────────────────────────┘
                                   │ USB cable — usbmuxd tunnel
                                   │ (Windows: "Apple Mobile Device Service",
                                   │  shipped with the Apple Devices app / iTunes,
                                   │  listening on 127.0.0.1:27015)
┌──────────────────────────────────▼──── Windows 11 PC (windows/) ─────────────┐
│ CaboCam.Desktop.exe  (C# .NET 8 WPF)                                          │
│   UsbmuxClient ─► Connect(device, 7878) ─► ProtocolClient                     │
│   video packets ─► cc_decoder_* (CaboCam.Native.dll, MF H.264/HEVC MFT)       │
│                    NV12 ─► cc_sink_write_nv12 ─► shared memory ───────┐       │
│                    NV12 ─► cc_nv12_to_bgra ─► WPF preview             │       │
│   cc_vcam_register() ─► MFCreateVirtualCamera("CaboCam", CLSID below) │       │
│                                                                       │       │
│ Windows Camera Frame Server (svchost, LocalService)                   │       │
│   loads CaboCam.VirtualCamera.dll (COM media source) ◄────────────────┘       │
│   ─► Teams / Zoom / Chrome / OBS / Camera app see a webcam named "CaboCam"    │
└───────────────────────────────────────────────────────────────────────────────┘
```

## Components and ownership

| Path | Tech | Output |
|---|---|---|
| `ios/` | Swift 5.9+, SwiftUI, AVFoundation, VideoToolbox, Network.framework, XcodeGen `project.yml` | `CaboCam.app` (iOS 17+) |
| `windows/native/` | C++20, CMake, Media Foundation, MSVC x64 | `CaboCam.VirtualCamera.dll` (COM media source), `CaboCam.Native.dll` (flat C API) |
| `windows/desktop/` | C# .NET 8 WPF (`net8.0-windows10.0.22621.0`), P/Invoke via `LibraryImport` | `CaboCam.exe` |
| `windows/installer/` | PowerShell | `install.ps1` / `uninstall.ps1` (admin, one time) |
| `.github/workflows/windows.yml` | GitHub Actions `windows-latest` | zip artifact with everything |

## Fixed identifiers (do not change)

- Media source COM CLSID: `{B66867C7-2F79-49DF-983E-13FAB663FD84}`
- Virtual camera friendly name: `CaboCam`
- iPhone TCP port: `7878`
- Shared memory: see `windows/native/common/cabocam_shared.h`
- Native C API: see `windows/native/common/cabocam_native.h`
- Wire protocol: see `docs/PROTOCOL.md`

## Key decisions

1. **Transport = usbmux.** iOS has no USB-device API for apps; the only cable path is
   usbmuxd, which tunnels a TCP connection to a port the app listens on. On Windows,
   usbmuxd is the Apple Mobile Device Service (Apple Devices app from Microsoft Store,
   or iTunes). The phone must be unlocked once and "Trust This Computer" accepted.
   Desktop also allows a manual IP:port connection as a Wi-Fi fallback.
2. **Compressed video.** USB 2 (Lightning) cannot carry raw 1080p; we encode on the
   iPhone hardware encoder (H.264 default, HEVC optional) at 4–50 Mbps and decode with
   the Media Foundation decoder MFT on Windows (hardware when available).
3. **Windows 11 MF virtual camera** (`MFCreateVirtualCamera`, mfsensorgroup) — visible to
   every app including UWP/Camera. Lifetime `Session`, access `CurrentUser`: the camera
   exists while CaboCam.exe runs; no admin needed at runtime. The COM DLL must be
   registered once in HKLM (admin) from `C:\Program Files\CaboCam\` (frame server runs
   as LocalService and needs read access).
4. **Frame hand-off = Global shared memory created by the media source.** Non-admin
   desktop processes lack `SeCreateGlobalPrivilege`; the frame server (LocalService)
   has it. So the media source creates `Global\CaboCamFrames_v1` with a DACL granting
   Authenticated Users read/write, and the desktop opens it (retrying) — its presence
   also means "some app is consuming the camera". If creation fails, the source tries to
   open an existing one (desktop running elevated may create it).
5. **Media source scales.** It advertises NV12 1920×1080@30 (first/default), 1280×720@30,
   1920×1080@60, 1280×720@60, 2560×1440@30 (2K), 3200×1800@30 (3K), 3840×2160@30, 640×480@30.
   The frame buffer accepts any orientation up to 3840 per side and the 4K pixel count
   (`CABOCAM_FRAME_FITS`). Incoming frames of other sizes are
   center-crop-scaled (bilinear) to the negotiated size. With no recent frame (>1 s) it
   emits a dark gray frame so consumers never stall.
6. **iOS must stay in foreground** (iOS suspends camera in background). The app disables
   the idle timer and offers a "dim screen" black overlay.
