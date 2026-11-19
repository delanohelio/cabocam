# CaboCam native components (Windows 11, MSVC x64, C++20)

Two DLLs, built with CMake and no dependencies beyond the Windows SDK (10.0.22621+):

| DLL | Loaded by | Purpose |
|---|---|---|
| `CaboCam.VirtualCamera.dll` | Windows Camera Frame Server (`svchost`, LocalService) | COM media source behind the "CaboCam" webcam. Reads NV12 frames from shared memory, scales them to the negotiated mode, hands them to the consuming app. |
| `CaboCam.Native.dll` | `CaboCam.exe` (C#, P/Invoke) | Flat C API (`common/cabocam_native.h`): registers the virtual camera, writes decoded frames into shared memory, decodes H.264/HEVC via a Media Foundation MFT, converts NV12 to BGRA for the preview. |

## Build

```
cmake -S windows/native -B build/native -A x64
cmake --build build/native --config Release
```

Both DLLs end up in `build/native/bin/Release/`. Needs Visual Studio 2022 and the
Windows 11 SDK (`mfvirtualcamera.h` first shipped in 10.0.22000).

## How the pieces fit

```
CaboCam.exe
  cc_vcam_register()  -> MFCreateVirtualCamera(SoftwareCameraSource, Session, CurrentUser,
                          "CaboCam", {B66867C7-...}) + Start()
  cc_decoder_*        -> NV12 frames
  cc_sink_write_nv12  -> Global\CaboCamFrames_v1 (seqlock) + Global\CaboCamFrameEvent_v1
                                   |
Frame Server -- CoCreateInstance({B66867C7-...}) --> CaboCam.VirtualCamera.dll
  class factory -> IMFActivate -> ActivateObject -> CCMediaSource (IMFMediaSourceEx)
  CCMediaStream: worker thread paces one sample per RequestSample at the negotiated fps
```

* The media source creates the shared memory with a DACL (SYSTEM/LocalService/Admins all,
  Authenticated Users read/write) because only LocalService has `SeCreateGlobalPrivilege`.
  The desktop process only opens it (it creates it only if elevated and it does not exist).
* Frame layout and seqlock protocol: `common/cabocam_shared.h`. If no frame was written in
  the last second the source emits dark gray NV12 (Y=32, UV=128).
* `readerStreaming` / `readerHeartbeatQpc` in the header tell the desktop app
  (`cc_sink_consumer_active`) whether some app currently has the camera open.

### Design choices

* **Sample allocation.** The source implements `IMFSampleAllocatorControl` and reports
  `UsesCustomAllocator`; it allocates system-memory NV12 samples itself via
  `MFCreateVideoSampleAllocatorEx` (pool of 4-16), falling back to one-off
  `MFCreate2DMediaBuffer` samples if the pool is exhausted. Reason: the frame server may
  hand out a GPU/DXGI allocator, and CPU-writing into a DXGI surface is slow and not
  guaranteed lockable; own system-memory samples are always lockable and copy-free for the CPU path.
  Destination pitch is always honored via `IMF2DBuffer2::Lock2DSize`.
* **Decoder.** Software (synchronous) decoder MFTs only: `CLSID_CMSH264DecoderMFT` for
  H.264, `MFTEnumEx(SYNCMFT)` for HEVC (needs the Microsoft HEVC Video Extension).
  Hardware decoders are async MFTs needing a DXGI device manager; this is deliberately not done.
  `MF_LOW_LATENCY` and `CODECAPI_AVLowLatencyMode` are set.
* **Registration.** `regsvr32 CaboCam.VirtualCamera.dll` (admin) writes
  `HKLM\Software\Classes\CLSID\{B66867C7-2F79-49DF-983E-13FAB663FD84}\InprocServer32`
  (default = full DLL path, `ThreadingModel=Both`). `regsvr32 /u` removes it. The DLL must live
  somewhere LocalService can read (e.g. `C:\Program Files\CaboCam\`), not in a user profile.

## Troubleshooting

* **`REGDB_E_CLASSNOTREG` (0x80040154) from `cc_vcam_register`**: the COM DLL is not
  registered, or the registered path is wrong / unreadable by LocalService. Re-run the installer,
  check the `InprocServer32` value, check the file ACL (Users/LocalService need read & execute).
* **`E_ACCESSDENIED` / `0x80070005`, or `MFCreateVirtualCamera` not found**: Windows 10 or
  older than Windows 11 21H2; virtual camera API unavailable.
* **Camera does not appear in apps**: it exists only while `CaboCam.exe` runs (Session
  lifetime). Make sure `cc_vcam_register` returned 0. Apps that already enumerated cameras may
  need a restart. Check Settings > Bluetooth & devices > Cameras. Make sure the DLL is x64.
* **Camera shows a gray frame**: no frames reach shared memory. Check `cc_sink_open` returns 1
  (the desktop app must be able to open `Global\CaboCamFrames_v1`; the source creates it only after
  an app opens the camera or after the source is activated) and that the writer heartbeat is recent.
* **Logs**: everything goes to `OutputDebugStringW` with a `[CaboCam]` prefix. Run Sysinternals
  DebugView as administrator with *Capture > Capture Global Win32* enabled to see the Frame Server side.
* **Frame Server holds the DLL after an update**: stop apps using the camera, then
  `net stop FrameServer` (admin) before replacing `CaboCam.VirtualCamera.dll`.
