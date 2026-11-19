# CaboCam wire protocol v1

Transport: one TCP connection, iPhone is the server on port **7878**, desktop is the
client (through usbmux, or directly over Wi-Fi). All integers little-endian.

## Packet framing

```
offset size  field
0      1     type   (u8)
1      4     length (u32 LE) — number of payload bytes that follow (max 32 MiB)
5      n     payload
```

| type | name | direction | payload |
|---|---|---|---|
| 0x01 | HELLO | both | UTF-8 JSON |
| 0x02 | MESSAGE | both | UTF-8 JSON object with a `"type"` string field |
| 0x10 | VIDEO | phone → desktop | binary, see below |

Unknown packet types and unknown JSON `type`s must be ignored (forward compatibility).
Unknown JSON fields must be ignored.

### HELLO
Each side sends HELLO first, immediately after connect.
- Phone: `{"protocol":1,"app":"CaboCam-iOS","version":"1.0","deviceName":"iPhone de Ana","model":"iPhone16,1","systemVersion":"18.1"}`
- Desktop: `{"protocol":1,"app":"CaboCam-Windows","version":"1.0"}`

After the phone receives the desktop HELLO it sends `capabilities` then `state`.

### VIDEO payload
```
offset size field
0      1    flags  bit0 = keyframe (IDR), bit1 = codec HEVC (0 = H.264)
1      8    pts    (i64 LE, microseconds, monotonic from capture clock)
9      n    access unit, Annex-B (00 00 00 01 start codes)
```
Every keyframe is preceded **in the same access unit** by its parameter sets
(H.264: SPS, PPS; HEVC: VPS, SPS, PPS). No B-frames (decode order == display order).

## JSON messages (packet type 0x02)

### Desktop → phone

```jsonc
{"type":"getCapabilities"}
{"type":"configure",                 // all fields optional; phone keeps the others
  "cameraId":"com.apple.avfoundation.avcapturedevice.built-in_video:0",
  "width":1920,"height":1080,"fps":30,
  "codec":"h264",                    // "h264" | "hevc"
  "bitrateKbps":12000,
  "keyframeIntervalSec":2,
  "orientation":"landscape",         // "landscape" | "portrait" (output frame shape)
  "mirror":false}
{"type":"start"}                     // start streaming VIDEO packets
{"type":"stop"}
{"type":"requestKeyframe"}
{"type":"setControl",                // all fields optional, apply only those present
  "zoom":2.0,                        // raw AVCaptureDevice.videoZoomFactor
  "rampZoom":true,                   // smooth ramp instead of jump
  "focusMode":"continuous",          // "continuous" | "auto" (one-shot) | "locked"
  "lensPosition":0.5,                // 0..1, implies focusMode locked
  "pointOfInterest":{"x":0.5,"y":0.5}, // normalized in OUTPUT frame coords (0,0 top-left); sets focus+exposure POI
  "exposureMode":"continuous",       // "continuous" | "locked" | "custom"
  "iso":400, "exposureDurationSec":0.0166,  // with "custom"
  "exposureBias":0.0,                // EV
  "whiteBalanceMode":"continuous",   // "continuous" | "locked" | "custom"
  "temperature":5000, "tint":0,      // with "custom"
  "torch":0.0,                       // 0 = off, (0,1] = level
  "stabilization":"auto",            // "off"|"standard"|"cinematic"|"cinematicExtended"|"auto"
  "hdr":true,                        // video HDR if supported by format (false = off, true = on/auto)
  "lowLightBoost":true,
  "centerStage":false,               // front camera only (class-level setting, cooperative mode)
  "dimScreen":true}                  // toggles the phone's black overlay
{"type":"ping","t":123456}           // phone echoes {"type":"pong","t":123456}
```

### Phone → desktop

```jsonc
{"type":"capabilities","cameras":[
  {"id":"<AVCaptureDevice.uniqueID>",
   "name":"Câmera Traseira Tripla",         // localizedName
   "position":"back",                       // "back" | "front"
   "deviceType":"triple",                   // wide|ultraWide|telephoto|dual|dualWide|triple|trueDepth
   "formats":[{"width":3840,"height":2160,"maxFps":60,"hdr":true},
              {"width":1920,"height":1080,"maxFps":60,"hdr":true}],  // deduped, best per WxH×maxFps, 420v formats only, sorted desc
   "minZoom":1.0,"maxZoom":123.75,
   "zoomSwitchOver":[2.0,6.0],              // virtualDeviceSwitchOverVideoZoomFactors
   "zoomDisplayMultiplier":0.5,             // displayVideoZoomFactorMultiplier (iOS 18+, else 1); UI shows zoom*multiplier ("0.5x")
   "hasTorch":true,
   "focusModes":["continuous","auto","locked"],
   "supportsPointOfInterest":true,
   "supportsCustomExposure":true,
   "minExposureBias":-8.0,"maxExposureBias":8.0,
   "supportsCustomWhiteBalance":true,
   "supportsLowLightBoost":false,
   "centerStage":false}]}

{"type":"state",                             // sent after any change + every 2 s while connected
  "streaming":true,
  "cameraId":"...","width":1920,"height":1080,"fps":30,"codec":"h264",
  "bitrateKbps":12000,"orientation":"landscape","mirror":false,
  "zoom":2.0,"focusMode":"continuous","lensPosition":0.62,
  "exposureMode":"continuous","iso":125,"exposureDurationSec":0.0083,
  "minISO":32,"maxISO":3072,"minExposureDurationSec":0.00002,"maxExposureDurationSec":0.0333, // of active format
  "exposureBias":0.0,"whiteBalanceMode":"continuous","temperature":5100,"tint":2,
  "torch":0.0,"stabilization":"auto","activeStabilization":"standard",
  "hdr":true,"hdrSupported":true,"lowLightBoost":false,"centerStage":false,
  "dimScreen":false,
  "thermalState":"nominal",                  // nominal|fair|serious|critical
  "batteryLevel":0.83,"charging":true,
  "stats":{"fps":29.9,"bitrateKbps":11850,"droppedFrames":0}}

{"type":"streamStarted","codec":"h264","width":1920,"height":1080,"fps":30}
{"type":"streamStopped"}
{"type":"error","message":"Formato não suportado pela câmera"}
{"type":"pong","t":123456}
```

Rules:
- `width`/`height` in `configure` describe the landscape sensor format; when
  `orientation` is `portrait` the phone outputs height×width frames.
- If requested width/height/fps is unsupported by the camera, phone picks the closest
  supported format (prefer same aspect, then larger area, then fps) and reports it in `state`.
- Changing camera/format/codec while streaming: phone reconfigures, emits a new keyframe
  with parameter sets, and sends `streamStarted` again with the new values.
- When the phone hits `thermalState` serious it may reduce fps; it reports it in `state`.
- Desktop sends `requestKeyframe` whenever its decoder errors or after (re)connecting.
