# Desktop capture performance

Monitor sharing now tries DXGI Desktop Duplication automatically, using the GPU
adapter attached to the selected display. The existing JPEG transport and
source resolution are unchanged. Application-window sharing still uses PrintWindow.

The GPU session keeps one CPU-readable staging texture. Each capture returns an
independent bitmap, including when the desktop has not changed. Hardware cursors
are drawn separately when DXGI reports them as separate from the desktop image.

GDI remains the automatic fallback when duplication is unavailable, the session is
lost, or the monitor is rotated. GPU initialization retries after two seconds.
Display size changes recreate the session. Stale GPU images are not reused after
a session error; the GDI path retains the existing locked-desktop behavior.
Source-picker thumbnails use GDI so they do not retain extra GPU sessions.

Hover over the native app's FPS indicator to see the active capture backend and
the last capture/encoding times. `/api/status` also reports `captureBackend`,
`captureMilliseconds` and `encodeMilliseconds`. These are last-stage durations,
not end-to-end latency measurements.

## Local measurements

On the local 2560×1080 monitor, JPEG quality 80:

| Capture | Capture p50 | Encode p50 | Host pipeline FPS at target 30 |
|---|---:|---:|---:|
| GDI | 83–85 ms | 13 ms | 10.15 |
| DXGI | 16 ms | 14 ms | 26.82 |

Stage timings used 20 samples after five warmups; pipeline measurements used four
seconds per backend and an immediate in-process WebSocket sink. The benchmark did
not save captured media or expose a server. These results exclude Wi-Fi, phone
decoding and display latency and do not guarantee 27 FPS on another device.

For an Android/Wi-Fi comparison, start with JPEG and a 30 FPS target, reload the
viewer and compare the native app's FPS with the phone's FPS. WebP was removed
after the [performance evaluation](WEBP_PERFORMANCE.md).

Tests cover GPU buffer stride, colors, ownership after unmapping, invalid buffers,
initialization failure/backoff and GDI fallback. Full hardware validation of other
GPUs, display changes, lock/unlock, RDP and rotated monitors remains necessary.

Desktop Duplication background:
https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api

Subsequent frame-cadence improvements and the remaining host limit are documented
in [Frame pacing performance](FRAME_PACING.md).

Capture/JPEG worker overlap and the measured throughput/latency trade-off are
documented in [Parallel pipeline performance](PARALLEL_PIPELINE.md).
