# Parallel capture and JPEG encoding — 18 November 2026

## Result

The capture and encoding stages now run on independent dedicated workers. A single pending slot keeps only the newest captured frame; replaced and invalidated frames are disposed. JPEG, quality settings, source dimensions and the WebSocket protocol are unchanged. No new runtime dependency was added.

On the local 2560×1080 DXGI monitor at target 60 and JPEG quality 80, the median completed host rate increases from **29.60 to 52.46 FPS (+77.2%)**. This is a host-pipeline benchmark, not measured Android/Wi-Fi throughput. A preliminary run reached about 60 FPS; that result was not stable across the final repetitions and is not used as the claimed outcome.

## Benchmark design

- Serial baseline is the previous high-resolution-paced implementation, with a timing callback added after each encoded frame. Its executable and ScreenTask assembly were frozen before the parallel implementation.
- Same harness, JPEG quality 80, targets 30/60, one-second warmup and three-second observation window. Three repetitions for each source/target/implementation.
- Two sources: an unchanged synthetic 1920×1080 code bitmap already in memory, and the actual local 2560×1080 desktop via DXGI. The synthetic test has negligible capture cost; it cannot prove 60 FPS live sharing.
- Preview work simulates the native UI’s bitmap clone every 160 ms. The WebSocket sink completes immediately, so CPU/capture/encoding are exercised without network congestion or browser decoding.
- Group order: serial0, parallel0, parallel1, serial1, serial2, parallel2. This partially balances order effects; the first baseline was taken before implementation work.
- Desktop contents, background load and temperatures were not isolated. The final per-run monitor/60 ranges are reported below; they should not be interpreted as a controlled codec/image-quality comparison.
- Completed sends in each measured window determine FPS. A boundary frame contributes about 0.33 FPS, so a result slightly above a target is normal with this short window.
- Age is capture-call start to JPEG/broadcast-method completion. It includes capture, pending-slot wait, preview and encoding; it excludes TCP queues, Wi-Fi, phone decode and physical display. It does not use the GPU’s presentation timestamp.
- The p95 columns below are medians of per-run p95 values, not pooled percentiles. Queue-wait p95 and capture-discard counters in raw parallel records include the warmup.

## Comparison

| Source | Target | Serial median FPS | Parallel median FPS | Change | Serial / parallel frame age p95 ms | Serial / parallel interval p95 ms |
|---|---:|---:|---:|---:|---:|---:|
| synthetic-fullhd | 30 | 30.21 | 30.18 | -0.1% | 18.49 / 19.50 | 35.79 / 38.18 |
| synthetic-fullhd | 60 | 59.93 | 59.87 | -0.1% | 16.03 / 17.88 | 19.46 / 20.33 |
| monitor | 30 | 28.57 | 29.88 | +4.6% | 42.20 / 43.75 | 42.21 / 40.69 |
| monitor | 60 | 29.60 | 52.46 | +77.2% | 40.86 / 48.12 | 40.87 / 21.23 |

The final monitor/60 runs range from 28.64–33.33 FPS serially and 50.94–52.63 FPS in parallel. The synthetic source already meets its target serially, so no material rate gain is expected there.

## Cost and latency

The extra overlap is not free. In the monitor/60 comparison:

- Median process CPU per second grows from about 0.93 to 1.67 logical-core equivalents.
- Median process CPU per completed frame remains similar: 31.78 versus 31.74 ms.
- Frame age p95 rises from 40.86 to 48.12 ms. Thus higher throughput has a small measured latency cost under load.
- The p95 interval between completed frames falls from 40.87 to 21.23 ms.
- With higher FPS, network traffic rises proportionally for the same average JPEG size. A bandwidth-limited viewer may still receive fewer frames.
- The pending queue is structurally limited to one bitmap. At peak, capture, encoding, the pending slot and the retained freeze frame can each own a separate bitmap. Memory/power usage across long sessions and other GPUs has not been exhaustively profiled.

## Correctness and lifecycle

The producer transfers ownership of every bitmap. The encoding worker retains its own last successful frame for freeze; it alone replaces/disposes that retained bitmap. Stop cancels and joins both workers before disposing the queue and its wait handles. Capture completion after cancellation cannot enter encoding.

Freeze and source changes invalidate pending captures using a state epoch. Retained frames are tagged with the source version so freezing after a source change cannot resend a previous source’s retained image. A frame already being processed or sent can still complete during a source switch, as can existing network data.

Tests deliberately stall the encoding-side preview while capture continues. They verify that only one frame remains pending, superseded bitmaps are disposed, the newest ready frame is encoded, stop waits for both workers, old queued source frames are discarded, frozen frames are resent without new captures, and stop/restart creates fresh workers.

Diagnostics added to `/api/status`: `frameAgeMilliseconds`, `queueWaitMilliseconds`, `pendingFrames`, `droppedCapturedFrames`, and `frozen`. Frozen frame age intentionally grows because its image is retained. Capture-stage discards are separate from the broadcaster’s network/client drop counters.

## Evidence and reproduction

- [Combined data, summary and binary hashes](../.ecc/benchmarks/parallel-pipeline-2026-11-18.json)
- Raw per-group files: `.ecc/benchmarks/parallel-{serial|parallel}-repeat-{0|1|2}-2026-11-18.json`.
- Local harness: `build/parallel-pipeline/Program.cs`; archived serial executable: `build/parallel-pipeline/serial-bin/Audit.exe`; current parallel executable: `build/parallel-pipeline/bin/Release/net9.0-windows10.0.19041.0/Audit.exe`.
- Example commands from repository root: `build/parallel-pipeline/serial-bin/Audit.exe serial 0` and `build/parallel-pipeline/bin/Release/net9.0-windows10.0.19041.0/Audit.exe parallel 0`. These overwrite the corresponding data file.
- The current harness can be rebuilt with `dotnet build build/parallel-pipeline/Audit.csproj -c Release`; doing so does not recreate the previous serial implementation. Raw data and SHA-256 hashes identify which assemblies were used.

Windows/.NET 9 are required. No captured desktop images were saved or exposed through a server. The recorded benchmark contains timings and counters only.
