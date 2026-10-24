# Kitty Graphics review and local benchmarks

## Scope and disposition

Reviewed the 2.1.0 implementation against `4cfaf27` (the preceding `main`): parser
configuration, owned snapshots, PNG allocation/decoding, pane geometry/layers,
D3D12 upload/cache lifetime, ConPTY replies, bounds, tests and release metadata.
No remaining release-blocking issue was found within the documented direct-image
scope. This is a point-in-time review, not a claim of complete Kitty compatibility.

### Findings addressed before release

| Finding | Resolution / evidence |
| --- | --- |
| Large images could exhaust the glyph/geometry upload pages and fail subsequent text drawing. | Image transfers now have separate fence-protected upload buffers and a 64 MiB per-frame transfer budget. The WARP regression uploads two 32 MiB images and then draws text geometry successfully. |
| Evicting a texture while another frame still references it could invalidate queued GPU work. | Each frame retains both texture and immutable descriptor heap until its fence completes. The pressure regression records a second frame that evicts a first-frame texture before draining the queue. |
| A full snapshot cache could copy an image repeatedly for placements that cannot be admitted. | Cache admission now happens before pixel copying; unused entries can be reclaimed, but a full active working set rejects additional images without decoding/copying each placement. |
| Already-RGBA snapshots did unnecessary per-pixel channel conversion. | RGBA uses a bulk copy; RGB/grayscale conversion remains explicit. Unchanged generations retain their existing pixel buffers. |
| ConPTY consumes raw APC input replies even though it forwards output-side graphics commands. | Kitty replies are encoded as literal Win32-input-mode character records in the reserved response queue. The child fixture verifies exact reply delivery, and the host parser verifies the matching success reply. |

Direct image data does not grant filesystem/shared-memory access. PNG output is
allocated through the supplied libghostty allocator; dimensions and decoded bytes
are bounded before output allocation. Borrowed libghostty image handles never
cross into rendering snapshots.

Remaining limitations are intentional and documented: direct transmission only;
no Unicode placeholders, animation or relative placements; fixed storage,
placement, cache and transfer budgets. Over-budget uploads wait for a later paint
attempt rather than consuming text buffers. Desktop compatibility across arbitrary
applications, WSL distributions, DPI settings and GPU drivers is not established.

## Environment and method

Local Windows x64 execution through WSL interoperability on 2026-10-24:

- Intel Core i5-6600, NVIDIA GeForce GTX 1060 3GB.
- NVIDIA driver `32.0.15.8278`; D3D12 hardware adapter, not WARP, for primary results.
- Pinned Zig 0.15.2, ReleaseSafe for Zig code; matching optimized C++ builds with
  exceptions/RTTI disabled. Runtime safety was not disabled.
- Baseline renderer and terminal sources were extracted from `4cfaf27`; candidate
  sources were the reviewed 2.1.0 working tree. Both used the same libghostty DLL,
  harness, toolchain and workload.
- Five alternating baseline/candidate runs after warmup, reversing order between
  runs. Tables report the median of five run summaries, not a pooled percentile.
- Benchmark harnesses were disposable local tools, not repository scripts or hosted
  CI coverage, in accordance with the project benchmark policy.

### Renderer benchmark

A 1280×720 offscreen target, a cached synthetic 160×40 glyph grid, 30 warmup frames
and 180 measured frames per case. The glyph painter fills small rectangles: this
exercises the actual atlas/geometry-cache/GPU path but does not measure font layout
or terminal parsing. Images cover the target; the 64-placement case tiles it.

`CPU submit` includes frame setup, recording and command submission. `Fence wall`
includes those CPU costs plus waiting for that frame's GPU fence. It is **not** a
GPU timestamp measurement. There is no swap chain, DWM presentation, vsync, PTY or
input handling in these timings, so they must not be converted into application
FPS or input-latency claims.

| Workload | CPU submit median | CPU submit p95 | Fence wall median | Fence wall p95 |
| --- | ---: | ---: | ---: | ---: |
| Text only — baseline | 0.0576 ms | 0.0817 ms | 0.1674 ms | 0.1996 ms |
| Text only — candidate | 0.0584 ms | 0.0831 ms | 0.1761 ms | 0.2333 ms |
| Cached 1024×1024 RGBA | 0.0597 ms | 0.0903 ms | 0.1989 ms | 0.2640 ms |
| Replace 1024×1024 every frame | 1.9828 ms | 2.5306 ms | 2.6041 ms | 3.1864 ms |
| Cached 1920×1080 RGBA | 0.0600 ms | 0.0877 ms | 0.2537 ms | 0.2886 ms |
| Replace 1920×1080 every frame | 3.6106 ms | 5.2139 ms | 4.7318 ms | 6.3369 ms |
| 64 placements of one cached image | 0.0755 ms | 0.1150 ms | 0.5358 ms | 0.5893 ms |

For each cached-image case there was exactly **one upload across all 210 frames**:
none during the measured post-warmup frames. Forced-replacement cases uploaded
210 times. The 64-placement case used 420 draw calls total: one cached text draw
and one instanced image draw per frame, not 64 separate image draws.

Text-only submission increased by 0.8 microseconds in the median summary. Fence
wall results varied with driver scheduling/clocks: baseline run medians ranged
0.1670–0.2455 ms, candidate 0.1680–0.1780 ms. Cached 1080p run medians ranged
0.1993–0.3755 ms. These spreads do not establish a statistically significant
application-wide regression or improvement. The strong result is avoiding repeat
uploads, not a claim that image rendering is free.

One separate software-renderer run identified `Microsoft Basic Render Driver`:
cached 1024×1024 fence wall median 16.20 ms versus 17.40 ms for replacement; cached
1080p 16.08 ms versus 18.09 ms for replacement. WARP is a fallback/correctness path,
not representative of the hardware results above.

### Parser, decoder and snapshot benchmark

An initialized STA COM apartment matches the application's window-thread state.
Clean snapshots use 100,000 reads per run; dirty text snapshots use 2,000 samples.
Image cases use 10 warmups and 40 measured retransmissions, including feed
filtering, libghostty parsing/base64 decoding, PNG decoding when applicable, and
owned snapshot refresh. They exclude ConPTY and GPU work.

| Workload | Baseline | Candidate |
| --- | ---: | ---: |
| Clean snapshot, median of run averages | 83.5 ns/read | 91.8 ns/read |
| Dirty 160×40 text snapshot, median | 174.3 µs | 178.4 µs |
| Dirty 160×40 text snapshot, p95 | 233.8 µs | 242.6 µs |
| Clean snapshot with one image, median of run averages | Not supported | 90.4 ns/read |
| 1024×1024 raw RGBA retransmission + snapshot, median | Not supported | 17.76 ms |
| 1024×1024 PNG retransmission + snapshot, median | Not supported | 6.47 ms |

The image is a synthetic solid RGBA color: 4 MiB decoded. The raw RGBA terminal
stream is 5,604,748 bytes; the highly compressible PNG is 7,145 bytes and its
terminal stream is 9,602 bytes. PNG measurements must not be generalized to photos,
noise or other compression ratios. Repeated transmissions deliberately change the
libghostty generation even though the source pixels are identical.

Every clean-image run confirmed the owned pixel-buffer pointer was unchanged.
The no-image clean-read cost increased about 8 ns. Dirty-frame run medians overlap
(baseline 174.1–179.7 µs; candidate 174.4–183.5 µs); the median summary is about 2.4%
higher, not evidence of zero overhead. Stable images bypass the millisecond-scale
transmit/decode path entirely.

Local harness SHA-256 identifiers:

- Renderer: `deb0f3eda261ddae83bf4ae5a0f5870d8eaa59f05ff3d4ac8a6d71219dfcd462`
- Terminal: `ca124bf511559f0c7916e99ab30b61eab8875f9ca9cdb1c2940778b8e1e6084e`
- DLL loader / COM setup: `89557f73eed6d370fa0371b0bac289dd1cb4b1c3c067e17635d4b2a52473885c`

## Validation

- Formatting, portable Zig tests, Python suite: **15 tests passed**.
- Debug and ReleaseSafe application/test cross-builds passed.
- Native Windows suites on a local Windows filesystem: **256 tests passed in each
  configuration**, with adjacent DLLs and fixture. Earlier UNC-backed attempts
  failed file-lock/shell-start checks; those are not counted as successful runs.
- Native WARP readback verifies alpha, texture generation invalidation, stable
  texture reuse, full transfer-budget handling and in-flight retirement.
- Two isolated-settings desktop captures from the real application/hardware
  renderer were inspected: chunked RGBA through ConPTY, then cropped semi-transparent
  PNG with text above it and a deeply negative-z image behind explicit cell
  backgrounds. These are local smoke checks, not a full desktop compatibility suite.
- ZIP staging, SHA-256 generation and payload verification passed. Stable MSI and
  publication remain gated by the repository release workflow's Windows checks.
