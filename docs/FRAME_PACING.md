# Frame pacing performance — 18 November 2026

## Outcome

A dedicated capture worker and a high-resolution waitable timer improve the host frame cadence without changing JPEG, pixel dimensions, quality settings, audio or network protocol. The Windows timer is local to this capture session; there is no busy-wait loop, global timer-resolution change or new package dependency. A cancellable standard wait is used when high-resolution timer creation or arming is unavailable.

Frame deadlines use Stopwatch ticks instead of rounded integer milliseconds. Missed deadlines are discarded, and changing the FPS target starts a fresh timing interval. Native waits are cancellable, allowing StopAsync to await actual worker completion.

## Measured comparison

Release, JPEG quality 80, three repetitions per scenario/target. Each run has one second of warmup and a three-second measurement window. Codec and capture remain the same; only pacing and worker scheduling change. The same preview bitmap clone is simulated every 160 ms. A local in-process WebSocket sink completes immediately: Wi-Fi, TCP congestion, phone decoding and display presentation are not measured.

The Full HD synthetic source clones an existing unchanged bitmap; it is a throughput/pacing test with almost no capture cost, not proof of 60 FPS live desktop sharing. The actual monitor is 2560×1080 and uses DXGI. Its visible content can change slightly between runs. The baseline phase ran before the optimized phase, so system-load/thermal drift is a limitation. Targets alternate between repetitions.

FPS is the number of completed sends during the observation window, divided by actual elapsed time. With a short window, one boundary frame contributes about 0.33 FPS; values around 30.3 or 60.3 do not imply a new target above 30/60. Percentile columns below are medians of the three per-run p95/p99 intervals, not pooled percentiles. CPU values are process CPU milliseconds per completed frame.

| Source | Target | Before median FPS | After median FPS | Before interval p95/p99 ms | After interval p95/p99 ms | CPU ms/frame before / after |
|---|---:|---:|---:|---:|---:|---:|
| synthetic-fullhd | 30 | 27.33 | 29.96 | 49.15 / 52.23 | 36.34 / 38.92 | 12.83 / 12.36 |
| synthetic-fullhd | 60 | 49.20 | 60.27 | 34.86 / 36.85 | 17.63 / 18.96 | 11.82 / 11.14 |
| monitor | 30 | 26.33 | 29.99 | 50.68 / 53.00 | 35.35 / 36.90 | 27.30 / 28.30 |
| monitor | 60 | 33.32 | 33.86 | 32.31 / 34.94 | 32.27 / 33.68 | 27.69 / 27.66 |

The real monitor at target 30 improves from a median 26.33 FPS to 29.99 FPS (about 14%), with lower frame-interval jitter. At target 60 the same real monitor stays near 34 FPS: its roughly 15 ms capture plus 13 ms encoding remain the main limit. The timer does not remove that work or improve a bandwidth-limited phone connection.

Process CPU per frame stays similar in the real-monitor test, while total CPU per second can rise because more frames are produced. Power/battery behavior on other hardware has not been measured.

## Rejected JPEG encoder experiment

A separate SkiaSharp 4.156.1 experiment encoded the same synthetic source images as JPEG, numeric quality 80 and 4:2:0 chroma subsampling. It was slower than the existing GDI+ JPEG encoder in all nine tested content/resolution pairs, so it was not added to ScreenTask. For Full HD:

| Content | GDI+ p50 / p95 ms | Skia p50 / p95 ms |
|---|---:|---:|
| slide | 10.29 / 13.72 | 38.02 / 43.49 |
| code | 12.52 / 13.63 | 55.76 / 66.82 |
| photographic-like noise | 19.55 / 19.99 | 110.69 / 113.93 |

This is an implementation-specific result, not a universal JPEG encoder ranking. The timing includes each implementation’s buffer handling; equal numeric quality does not establish equal visual quality. No image-quality equivalence was claimed. The isolated experiment remains under `build/jpeg-speed/`; no Skia dependency is included in the application.

## Checks

Tests exercise native and standard waits, cancellation of a pending wait, normal completion, missed deadlines and supported target clamping. Existing capture/freeze/shutdown, HTTP/WebSocket and browser decoding tests are also run. Hardware-specific timing is benchmarked rather than asserted in unit tests.

## Evidence

- [Baseline runs](../.ecc/benchmarks/frame-pacing-baseline-2026-11-18.json)
- [Optimized runs](../.ecc/benchmarks/frame-pacing-after-2026-11-18.json)

The local harness is `build/frame-pacing/Program.cs`. Its `before/after` measurements used the application binaries built in each phase. Rebuilding it against the current code reruns only the current implementation, regardless of the phase filename argument.

Native API references:

- https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw
- https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-setwaitabletimerex
