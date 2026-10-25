# Kitty Graphics

Vex supports direct Kitty Graphics image transmission (`t=d`) using the bundled
libghostty parser/storage and the native D3D12 renderer. RGB, RGBA and PNG images
are supported, including chunked transmission, image replacement/deletion,
multiple placements, source cropping, pixel offsets, z-order and scrolling.
PNG decoding uses Windows Imaging Component, not an additional bundled runtime.

Remote-session integration is out of scope. File, temporary-file and shared-memory
transfers are deliberately disabled: terminal output must not grant a sender
access to arbitrary local files. Applications should use direct transmission.
Unicode virtual placeholders, image animation and relative placement support are
not claimed by this integration; virtual placements are not rendered.

## Performance and lifetime

- libghostty owns parsing, base64 decoding, decompression and protocol replies.
  Vex does not maintain a competing escape-sequence parser for graphics.
- Snapshots own their pixels. No borrowed libghostty pointers escape into drawing
  code, and unchanged image generations reuse the same owned RGBA buffers.
- Storage generations skip snapshot rebuilding on clean frames. Placement geometry
  is refreshed for dirty viewports even when image content is unchanged.
- GPU textures are cached by process-wide image generation, not ID or dimensions.
  Same-sized replacements cannot reuse stale pixels. Unchanged images are neither
  decoded nor uploaded again on each frame.
- Consecutive placements of the same texture share an instanced draw. Positive-z
  images keep the ordinary cached terminal text/background path. Negative-z images
  use separately cached background/text layers to preserve Kitty's ordering.
- Image uploads use separate bounded buffers in the fence-protected frame slots;
  large transfers cannot consume the glyph/geometry upload pages. Each frame retains
  its image uploads, textures and immutable descriptors until its GPU fence completes,
  including resources evicted from the cache during command recording.
- Image clipping follows the pane scissor; previews reuse textures and transforms
  rather than resizing the PTY or decoding another copy. Scaling uses bilinear
  sampling; glyphs retain point sampling.

## Bounds

The initial limits are fixed, not configuration options:

- 32 MiB libghostty image storage per initialized screen.
- 64 KiB maximum buffered Kitty APC command; larger images must be chunked.
- 8192 pixels per dimension and 32 MiB decoded RGBA per image.
- At most 4096 placement visits and 128 cached visible images per snapshot.
- At most 64 MiB of owned snapshot pixels (replacement copies may temporarily
  increase peak allocation); unused snapshot images are released on refresh.
- 64 MiB logical RGBA texture cache shared across panes, 128 entries. Least-recently
  used entries are evicted under pressure and idle entries expire after two seconds
  when frames are running. Actual GPU allocations include resource alignment.
- A frame retains at most 128 unique image textures across all panes; additional
  textures are skipped rather than causing a fatal frame error.
- Image upload buffers have a separate 64 MiB per-frame budget, including row-pitch
  padding. Transfers beyond this budget are skipped for that paint and can be retried
  on a later paint; cached textures need no transfer budget.
- Evicted textures/descriptors and upload buffers can remain alive in the two
  in-flight frame slots; the texture cache budget is not a cap on total resident
  GPU memory. Glyph/geometry upload pages retain their independent limits.

## ConPTY replies

The bundled ConPTY forwards output-side APC graphics commands, but consumes raw
APC replies written to its input pipe. `vex_pty_write_response` therefore serializes
Kitty replies into Win32-input-mode character records. The child receives the
literal reply bytes without ConPTY interpreting them as another APC command.
Other terminal replies and ordinary user input retain their existing paths.
The encoded reply is bounded and queued atomically using the protocol reply
reserve. Tests cover delivery to a child using raw VT console input.

## Verification

`zig build test-build` compiles native tests. Run `vex-native-tests.exe` with its
adjacent DLLs and fixture on Windows. The Kitty tests cover:

- chunk transmission at every feed boundary and image deletion;
- owned-pixel reuse, same-sized replacement and primary/alternate screen isolation;
- allocation failure cleanup;
- WIC PNG decoding, straight alpha and malformed PNG rejection;
- ConPTY command forwarding, literal reply delivery and resulting image snapshots;
- headless D3D12 WARP pixel readback, alpha blending and generation-based texture
  reuse across frames, maximal-image transfer pressure and eviction while another
  frame retains its textures. This is offscreen rendering, not interactive desktop coverage.

Interactive Windows/WSL application compatibility still needs separate desktop
validation. Local protocol probes, capture tooling and hardware measurements are
maintained outside this repository. A successful protocol reply does not prove
correct rendering or PNG support; offscreen measurements are not application FPS
or input-latency benchmarks.

Protocol: <https://sw.kovidgoyal.net/kitty/graphics-protocol/>.
Inspiration: <https://x.com/mitchellh/status/2113761797308391526>.
