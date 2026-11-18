# Optional WebRTC video

Stop the broadcast, open **Settings & Logs → Video transport**, select
**WebRTC / H.264**, then start broadcasting. The choice is saved. JPEG/WebSocket
remains the default. Existing viewers and browsers without WebRTC keep receiving
JPEG; negotiation, playback and encoder failures return that viewer to JPEG.
The viewer toolbar identifies the transport actually in use.

## Operation and compatibility

- Windows x64 host, Windows Media Foundation H.264 encoder: hardware first,
  software when hardware initialization fails. Missing Media Foundation (for
  example an N edition without its Media Feature Pack) falls back to JPEG.
- One shared H.264 baseline encoder, target 8 Mbps, up to 16 WebRTC peers.
  The JPEG Image Quality slider is disabled in this mode; its value is retained
  for JPEG fallback. Target Framerate still applies. Encoding is bounded by the
  existing capture/encoding pipeline; 60 FPS is a target, not a guarantee.
- Signaling and PIN checks use the existing video WebSocket. Media uses
  DTLS-SRTP over UDP on the **same port number** as the TCP web server, bound to
  the selected local address. There are no public ICE/STUN/TURN servers or cloud
  services. Networks with client isolation or blocked UDP can require JPEG.
- Audio remains on the existing PCM/Web Audio channel. This is WebRTC **video**,
  not Opus audio or a new audio synchronization implementation.
- Zoom, fullscreen, snapshots, announcements, telemetry, source switching,
  freezing and session kicks continue using the existing viewer controls.
  `/ScreenTask.jpg` stays JPEG, refreshed at most once per second for RTC-only
  audiences. Mixed audiences encode both formats and cost more CPU.
- Modern Android Chrome, desktop Chrome and Safari implement H.264 WebRTC;
  receive-only operation is attempted without requesting camera/microphone
  permissions. Local HTTP receive-only playback was verified in desktop Chrome.
  Physical Android/iOS devices were not available for this validation; JPEG
  remains the compatibility path. PIN/signaling/audio still use local HTTP/WS.

## Local comparison, 2026-11-18

Windows 11, Intel Core i7-13620H / Intel UHD hardware encoder, Release build,
one Chrome receiver on the same PC, target **60 FPS**, synthetic animated
1920×1080 bitmap, JPEG quality 80 and H.264 target 8 Mbps. Each observation
covered 15 seconds after warm-up. This isolates transport/encoding; it does not
measure monitor capture, Wi-Fi, Android decoding or end-to-end latency.

| Mode | Browser frames/s | Video payload | Observation |
| --- | ---: | ---: | --- |
| JPEG | 54.5 decoded | 20.39 Mbps | Host counter 60 FPS at end |
| WebRTC H.264 | 41.9 received; 40.6 excluding browser drops | 5.57 Mbps | Host counter 46 FPS at end; 19 browser drops |

For this simple, mostly solid-color animation H.264 reduced payload by **72.7%**,
but produced fewer frames than JPEG locally. It may help a bandwidth-limited
phone connection; this measurement does **not** establish an FPS improvement
on the user's Android phone. At target 30, a separate 1280×720 smoke test
maintained 30 FPS in the viewer. Results depend on scene, GPU, driver and device.

Reproduce with the synthetic-only harness (never captures the desktop or edits
settings/firewall). Close any other process on port 18780 first:

```powershell
dotnet run --project tools/StreamingProbe -c Release -- webrtc 60 127.0.0.1
# Open http://127.0.0.1:18780 in Chrome. Warm up before measuring.
# Type quit, then repeat with JPEG:
dotnet run --project tools/StreamingProbe -c Release -- jpeg 60 127.0.0.1
```

Use `/api/status` byte-counter deltas over the measured interval for video
payload Mbps (audio disabled). In Chrome use `video.getVideoPlaybackQuality()`
counter deltas, subtracting droppedVideoFrames; for JPEG count completed
createImageBitmap decodes. Neither payload counter includes RTP/IP headers or
retransmissions. Test both orders and multiple runs before drawing conclusions
about another machine. The harness accepts `small`, `full`, `freeze`, `resume`,
`pin` (2468), `announce` and `quit` for lifecycle checks.

Validation also covered two concurrent native peers, one peer returning to JPEG
while the other stayed in WebRTC, and JPEG snapshots during RTC playback.
Both hardware and software Media Foundation encoders produced H.264 output in
isolated encoder smoke tests. The automated suite passed 105 .NET tests and 11
JavaScript tests, including signaling, PIN denial, fragmented controls, NV12
colors/padding, settings persistence, fallback, stale callbacks and video gestures.

## Portable runtime and source

Before building distributable x64 binaries run:

```powershell
./scripts/Prepare-WebRtcRuntime.ps1
```

This downloads a pinned, hash-checked Microsoft VC++ runtime package and extracts
three app-local DLLs into ignored `build/native-webrtc`. No installer runs.
The native WebRTC DLL and prepared CRT libraries are embedded into the EXE.
The loader prefers installed system runtime libraries; if missing, it extracts
the bundled fallback. Extracted DLLs are SHA-256 checked under
`%LOCALAPPDATA%\ScreenTask\Native`. Media frames remain in memory.

CI/release workflows perform this preparation. Ordinary developer builds can
use an already installed VC++ runtime. Keep `WebRTC-NOTICE.txt` and
`DirectX-NOTICE.txt` with distributed binaries. The native package uses
libdatachannel/MPL-2.0 and its dependencies; notices include license texts and
upstream source references. Microsoft runtime redistribution follows Microsoft's
license terms; those runtime components are not relicensed as ScreenTask code.

Primary API references:

- https://github.com/paullouisageneau/libdatachannel/tree/v0.24.6
- https://learn.microsoft.com/windows/win32/medfound/h-264-video-encoder
- https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files
