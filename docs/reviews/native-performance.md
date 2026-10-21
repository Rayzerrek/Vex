# Native performance review

## Changes retained

- ReleaseSafe uses Zig's thread-safe `smp_allocator`. Debug retains allocation
  diagnostics and leak checks; runtime safety remains enabled in both builds.
- Input copy-feedback snapshots reuse retained text when the owning terminal's
  frame generation and dimensions have not changed. Cancellation still clears
  attribution; a later input refreshes its timestamp. Changed frames still copy.
- The feed filter scans 16 bytes at a time to find possible ESC/`+q`/`$q`
  interception points. Ordinary dollar/plus signs do not force a slow scan. It
  preserves trailing `+`/`$` across pipe boundaries and leaves the existing
  control-sequence state machine unchanged. The vector preflight stays out of
  the state machine's hot instruction footprint.
- Startup logging now separates application, font and renderer initialization.

Experiments that vectorized every short ANSI run or bypassed the batching buffer
for long runs regressed dense ANSI throughput. Those changes were **not** kept.

## Measurements

Local Windows 10 x64 execution through WSL interoperability, pinned Zig 0.15.2,
ReleaseSafe. Baseline feed code was extracted from the pre-change Git revision;
both benchmark executables used the same harness and compiler options. Three
alternating baseline/candidate microbenchmark runs, medians:

| Feed workload | Before (MiB/s) | After (MiB/s) | Ratio |
| --- | ---: | ---: | ---: |
| Plain text, approximately 64 KiB chunks | 1,153 | 10,330 | 9.0× |
| Plain text with dollar signs and plus signs | 1,163 | 10,248 | 8.8× |
| Long text between ANSI sequences | 1,428 | 2,107 | 1.5× |
| Dense ANSI, dollar signs and plus signs | 541 | 566 | 1.0× |

These measure filtering only, with a counting sink, **not** terminal throughput.
A reused 160×60 copy-feedback snapshot avoids approximately 29–35 microseconds of
full-grid copying per unchanged input in these runs. Its cache-hit path is
constant-time and allocation-free; this is not an end-to-end input-latency claim.
Small 128-byte allocate/free pairs cost approximately 9–11 microseconds with the
diagnostic allocator versus 40–55 nanoseconds with the production allocator in
this synthetic workload. Allocation sizes, live sets and threading change that
ratio; it is not an application-wide speedup.

### Startup

Ten alternating warm baseline/candidate launches after one warmup pair, `cmd`,
isolated Lua settings, normal GPU selection, using `VEX_NATIVE_METRICS`:

| Milestone | Before median | After median |
| --- | ---: | ---: |
| First frame | 358.7 ms | 356.1 ms |
| First PTY output | 365.0 ms | 363.2 ms |

First-frame ranges were 347.7–371.8 ms before and 345.4–380.5 ms after. The
startup difference is **within run-to-run variation**, not a demonstrated
significant improvement. Candidate application-ready median was 5.4 ms,
fonts-ready 10.3 ms, renderer-ready 345.4 ms: native renderer/driver initialization
dominates this machine. Faster allocation alone does not solve that bottleneck.
The timings start inside `main`; they exclude process creation and DLL loading.
No cold-boot startup, WSL-shell startup, interactive-prompt or GPU-driver matrix
claim is made.

## Verification

- Pinned-toolchain formatting and Debug/ReleaseSafe cross-builds.
- Native Windows suites: 209 tests passed in each configuration, with DLLs and
  the ConPTY fixture adjacent on a local Windows filesystem.
- Portable tests cover every short SIMD slice/alignment against scalar detection,
  chunked OSC/CSI/DCS handling, report/reset ordering, and snapshot invalidation.
- Python packaging tests and ZIP generation passed.

Portable reproduction: `zig build bench` and `zig build test-portable` from
`app/` under `nix develop`. To run the harness on Windows after cross-compiling:

```sh
zig build-exe src/zig/performance.zig -target x86_64-windows-gnu -O ReleaseSafe \
  -femit-bin=zig-out/bin/vex-performance.exe
```

Run that executable on Windows; Linux benchmark results are not Windows results.
Desktop visual/clipboard checks, GPU recovery and cold-start profiling remain
separate manual validation. In particular, no risky renderer initialization or
persistent driver-cache change was introduced without supporting measurements.
