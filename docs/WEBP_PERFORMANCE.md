# WebP performance evaluation — 18 November 2026

## Decision

Remove the WebP feature from the application. Keep JPEG, GPU desktop capture and the bounded streaming/decoding improvements. The tested WebP implementation saves bandwidth in some content types, but makes encoding 9.1–15.9 times slower across all 27 tested configurations. It also decodes more slowly in the tested desktop browser.

This conclusion concerns this implementation and host, not every possible WebP encoder or device. A limited bandwidth benefit exists: two of nine bandwidth-model comparisons improve at 10 Mbps (slide: 9.61→12.31 FPS; code: 3.00→3.58 FPS). Neither codec reaches the 30 FPS target there. In the other seven comparisons WebP lowers throughput, including every 30/100 Mbps case. For this application’s Full HD framerate goal, that trade-off does not justify shipping the option and its native dependency.

## Method

- Actual production StreamBroadcaster and WebPFrameEncoder, Release .NET 9.0.23, Microsoft Windows 10.0.26300, 16 logical processors. No captured personal content was used.
- 1280×720, 1920×1080 and 3840×2160; synthetic slide, code/text and gradient/noise photographic-like content. The photo-like case is procedural, not a real photograph or video corpus.
- Numeric quality 60/75/90; WebP method 0, lossy, one pass, threading enabled, sharp YUV disabled, pixel-cache disk spilling disabled. JPEG uses the current GDI+ encoder.
- Three rounds with alternating codec order, ten measured frames per codec per round. Five warmups before the first round and two before subsequent rounds: 30 measured frames per codec/configuration, 1,620 measured encodes total.
- Median, nearest-rank p95/p99, packet size, process CPU time and managed allocations are recorded. Native allocations are not included in managed-byte counts; the process CPU counter has limited timer resolution.
- Browser test uses six actual encoded Full HD quality-75 fixtures, 30 measured decodes each (180 total), Chrome 157 on Windows. Measures createImageBitmap plus Canvas submission, not physical display latency.
- Bandwidth test uses the production broadcaster and an asynchronous sink that delays each send by payload bits / configured Mbps, at a 30 FPS target for three seconds per case. It is an optimistic model: no capture cost, radio, TCP congestion or phone decode. Task.Delay scheduling and short windows affect the exact figures. It is not a measured Wi-Fi result.
- A mixed audience (one WebP-capable, one legacy JPEG viewer) is measured separately, after warmup.
- Equal numeric quality does not establish equal visual quality. No PSNR/SSIM, text-legibility or perceptual equivalence claim is made. Measurements describe the user-selectable feature as implemented.

## Encoding results

WebP is slower in **27/27** pairs. Median slowdown across configurations: **11.91×**. WebP is not smaller in **9/27** pairs.

Packet delta below is relative to JPEG; positive means WebP sends more data. Full raw timing samples, CPU times and managed allocations are in the JSON evidence.

| Resolution | Content | Quality | JPEG p50 ms | WebP p50 ms | JPEG p95 ms | WebP p95 ms | WebP packet delta |
|---|---|---:|---:|---:|---:|---:|---:|
| 1280×720 | slide | 60 | 3.62 | 39.89 | 3.91 | 52.33 | -6.8% |
| 1280×720 | slide | 75 | 3.62 | 39.35 | 5.16 | 41.28 | -14.5% |
| 1280×720 | slide | 90 | 3.74 | 49.45 | 4.69 | 50.55 | -36.0% |
| 1280×720 | code | 60 | 5.29 | 72.29 | 5.50 | 75.19 | -1.0% |
| 1280×720 | code | 75 | 5.70 | 76.81 | 6.14 | 79.86 | -18.9% |
| 1280×720 | code | 90 | 6.54 | 91.70 | 6.73 | 92.73 | -25.7% |
| 1280×720 | photo-like | 60 | 4.52 | 53.80 | 4.98 | 57.81 | +13.7% |
| 1280×720 | photo-like | 75 | 5.09 | 61.67 | 5.35 | 65.21 | +0.6% |
| 1280×720 | photo-like | 90 | 6.87 | 109.07 | 7.85 | 111.40 | +17.6% |
| 1920×1080 | slide | 60 | 8.61 | 82.10 | 9.53 | 89.94 | -34.0% |
| 1920×1080 | slide | 75 | 8.42 | 80.36 | 9.61 | 85.57 | -31.9% |
| 1920×1080 | slide | 90 | 8.28 | 100.99 | 8.77 | 103.21 | -44.2% |
| 1920×1080 | code | 60 | 10.33 | 126.94 | 11.16 | 138.66 | -6.8% |
| 1920×1080 | code | 75 | 10.87 | 131.96 | 11.11 | 140.32 | -23.1% |
| 1920×1080 | code | 90 | 12.68 | 164.18 | 13.42 | 170.33 | -28.1% |
| 1920×1080 | photo-like | 60 | 10.34 | 113.67 | 10.88 | 120.08 | +15.0% |
| 1920×1080 | photo-like | 75 | 11.60 | 132.06 | 12.28 | 135.85 | +1.7% |
| 1920×1080 | photo-like | 90 | 15.45 | 239.24 | 16.76 | 246.67 | +17.9% |
| 3840×2160 | slide | 60 | 32.00 | 300.85 | 34.03 | 314.81 | -52.0% |
| 3840×2160 | slide | 75 | 32.53 | 297.03 | 33.33 | 309.88 | -54.9% |
| 3840×2160 | slide | 90 | 32.29 | 382.98 | 32.61 | 395.26 | -51.2% |
| 3840×2160 | code | 60 | 35.82 | 381.76 | 37.19 | 404.34 | -17.4% |
| 3840×2160 | code | 75 | 37.44 | 399.16 | 45.24 | 411.32 | -30.5% |
| 3840×2160 | code | 90 | 40.46 | 504.42 | 41.58 | 521.62 | -30.9% |
| 3840×2160 | photo-like | 60 | 41.00 | 449.65 | 43.59 | 460.63 | +13.9% |
| 3840×2160 | photo-like | 75 | 46.44 | 543.62 | 47.48 | 557.26 | +0.9% |
| 3840×2160 | photo-like | 90 | 60.52 | 952.55 | 61.98 | 963.16 | +18.0% |

At Full HD quality 75, JPEG encoding medians are 8.42–11.60 ms, versus 80.36–132.06 ms for WebP. Even before capture and network time, these WebP medians exceed the 33.3 ms/frame budget for 30 FPS.

## Browser decode results

| Full HD content | JPEG decode p50 / p95 ms | WebP decode p50 / p95 ms |
|---|---:|---:|
| slide | 13.2 / 16.5 | 17.4 / 20.0 |
| code | 17.8 / 19.8 | 48.7 / 50.2 |
| photo-like | 19.1 / 21.3 | 57.5 / 59.8 |

## Bandwidth-limited model

Completed frame rate excludes unfinished sends at the three-second observation boundary.

| Content | Mbps | JPEG FPS | WebP FPS |
|---|---:|---:|---:|
| slide | 10 | 9.61 | 12.31 |
| slide | 30 | 22.18 | 12.36 |
| slide | 100 | 28.28 | 12.48 |
| code | 10 | 3.00 | 3.58 |
| code | 30 | 7.61 | 7.08 |
| code | 100 | 16.53 | 7.08 |
| photo-like | 10 | 3.32 | 2.58 |
| photo-like | 30 | 8.94 | 7.11 |
| photo-like | 100 | 19.00 | 7.10 |

## Mixed audience

At Full HD, code/text, quality 75:
- Jpeg: encode p50 11.52 ms, p95 16.39 ms.
- WebP: encode p50 143.24 ms, p95 150.58 ms.

WebP requires encoding both formats for mixed audiences, further increasing host cost.

## Removal and compatibility

The application no longer includes the codec selector, WebP encoder/packet handling, capability probe or Magick.NET runtime dependency. The old `<VideoFormat>WebP</VideoFormat>` setting is ignored by the XML serializer without losing other settings, and is not written again. Cached clients that still append `webp=1` receive normal JPEG packets. GPU capture, fullscreen placement, PIN, JPEG snapshots and the bounded queues remain.

## Evidence and reproduction

- [Encoding matrix and raw samples](../.ecc/benchmarks/webp-encoding-2026-11-18.json)
- [Throughput model and mixed audience](../.ecc/benchmarks/webp-throughput-2026-11-18.json)
- [Desktop browser summary](../.ecc/benchmarks/webp-browser-2026-11-18.json)
- [Isolated pre-removal reference source](../.ecc/benchmarks/webp-reference-2026-11-18.zip)

The reference ZIP contains the exact pre-removal streaming sources and benchmark harness with its own project. It is evidence only and is not compiled or distributed with ScreenTask. Extract to a temporary workspace folder and run `dotnet run --project <extracted-folder>/Audit.csproj -c Release` from the repository root. It restores Magick.NET solely for this archived benchmark and writes its results under `.ecc/benchmarks/`. Existing result files with the same names are overwritten by a rerun.

The browser summary was transcribed from the browser tool output; per-sample browser values remain in that tool log. The reference harness generates the browser fixtures at `build/webp-comparison/frames.json`; serve them on localhost and repeat createImageBitmap/Canvas decodes. Reproduction requires Windows and .NET 9; exact timings depend on hardware and system load.
