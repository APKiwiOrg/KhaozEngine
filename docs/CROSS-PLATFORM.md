# Cross-platform desktop GPU

KhaozEngine's custom stack (Render2D / Render3D) runs on Veldrid. Each desktop OS gets a native graphics
backend; the GPU golden-snapshot net verifies rendering on each one through a CI matrix.

## Platform → backend (desktop scope)

| OS      | Backend     | `GpuBackendKind` | golden file suffix | software rasterizer (CI) |
| ------- | ----------- | ---------------- | ------------------ | ------------------------ |
| macOS   | Metal       | `Metal`          | `.metal.txt`       | native (Apple GPU)       |
| Windows | Direct3D11  | `Direct3D11`     | `.direct3d11.txt`  | WARP (auto fallback)     |
| Linux   | Vulkan      | `Vulkan`         | `.vulkan.txt`      | Mesa lavapipe            |

Backend selection is centralized in `KhaozEngine.Gpu.GpuBackendSelector`:

- `Select()` reads the `KE_GRAPHICS_BACKEND` env override (`metal` / `vulkan` / `d3d11` / `gl`,
  case-insensitive), otherwise probes the OS (macOS → Metal, Windows → Direct3D11, Linux/other → Vulkan).
- `CreateHeadless` builds the matching offscreen device (`GraphicsDevice.CreateVulkan` / `CreateD3D11` /
  Metal). No window - so the golden tests need no SDL2.

## Backend-aware goldens

`GoldenCompare.GoldenPath(name)` resolves `KhaozEngine.Tests/Gpu/goldens/<name>.<backend>.txt` where
`<backend>` = `GpuBackendSelector.Select().ToString().ToLowerInvariant()`. Each backend has its own reference
grid because a software rasterizer (lavapipe, WARP) does not match Apple Metal pixel-for-pixel; per-backend
goldens absorb that while still catching real shader / UBO / blend / winding / orientation regressions (coarse
32×18 grid, per-channel tolerance).

The Metal goldens (`scene2d.metal.txt`, `scene3d.metal.txt`), the Direct3D11 goldens
(`scene2d.direct3d11.txt`, `scene3d.direct3d11.txt`, baked on WARP), and the Vulkan goldens
(`scene2d.vulkan.txt`, `scene3d.vulkan.txt`, baked on lavapipe) are all committed and verified on every macOS /
Windows / Linux run respectively.

The 2D golden loads a libre font bundled in the test project (`KhaozEngine.Tests/Assets/Roboto-Regular.ttf`,
Apache-2.0) rather than an OS system-font path, so its glyph input is identical on every runner.

`KhaozEngine.Tests/Gpu/goldens/*.txt` is pinned `text eol=lf` in `.gitattributes`. The goldens are machine-generated
LF text, and a Windows checkout with `autocrlf` on would otherwise convert them to CRLF, breaking the byte-identity
contract between the committed file and `GoldenGrid.Serialize`'s LF output. This exact failure shipped and was
fixed in 10.18.1 (Metal and Vulkan legs and every actual golden compare were green, only the endings differed).

## CI matrix (`.github/workflows/cross-platform-gpu.yml`)

The suite each leg runs is split by trigger, from measured cost and lavapipe stability
(`KE_GPU_TESTS=1`, `fail-fast: false`):

- **Self-hosted Metal leg (free)**: the WHOLE test suite (`--filter "Category!=LiveSocket"`) on every
  trigger - every `[GpuFact]` test (golden and behavioral) plus the headless suite, matching `ci.yml`'s
  own `Category!=LiveSocket` exclusion (LiveSocket tests need a live network peer, not available here).
- **GitHub-hosted D3D11 leg (2x billing)**: golden tests only (`FullyQualifiedName~Golden`) on
  `push`/`pull_request`, and the WHOLE suite on the weekly `schedule` (Sunday 18:00 UTC) and on
  `workflow_dispatch`. The full suite on hosted Windows measured 17m14s vs the golden-only 7m44s, about
  +19 billed 2x minutes per run - too costly per-push, so full hosted coverage rides the weekly cron and
  any manual dispatch instead.
- **GitHub-hosted Vulkan leg (1x)**: golden tests only, on EVERY trigger. The full suite on Mesa
  lavapipe crashes the test host natively at a nondeterministic point (three consecutive dispatch runs
  died in three unrelated GpuFact classes, the last with zero managed failures. Silent segfault, Mesa
  25.2.8). WARP and Metal run the identical full suite green, so this is lavapipe/Veldrid native-layer
  instability under parallel headless-device churn, tracked as a follow-up (first lead: serialize xUnit
  collections on that leg). The golden subset is the configuration lavapipe has run green for months.

Historically the test step filtered every leg to `FullyQualifiedName~Golden`, so any `[GpuFact]` class
without "Golden" in its name never ran on ANY backend (`Scene3DTextureUnloadTests`, `WaterQueueTests`,
`RenderServiceTests`, and dozens of other classes were never exercised on Metal/D3D11/Vulkan). Every GPU
test now runs on Metal (every trigger) and D3D11 (weekly + dispatch) regardless of what it is called; the
name is not a CI contract. The first full-suite sweeps surfaced 13 real Windows portability bugs, a
dispose-before-submit contract violation in three test classes that only Vulkan enforces, and the
lavapipe host-crash instability above - all of which the golden-only filter had been hiding.

`KE_GPU_TESTS` accepts two values. `1` is strict (CI and the dev Mac): tests run, and a device-creation
failure is a test error, never a skip, so CI cannot go green with zero GPU coverage. `probe` is for
arbitrary machines: a one-per-process headless device probe runs the tests when a device exists and
skips them with the probe's failure reason when it does not.

### Golden test flavors

Two flavors of golden test exist, both conventionally named with `Golden` for discoverability (grep-ability,
not a CI filter contract):

- **committed-grid goldens** - render a scene, downsample, and diff against a committed per-backend reference grid
  via `GoldenCompare.AssertOrUpdate` (e.g. `GoldenSnapshotTests`, `CollisionOverlayGoldenTests`). A backend needs
  its own baked `.txt`.
- **property / invariant "goldens"** - assert thresholds or invariants on the rendered pixels instead of a
  committed grid (e.g. `SplatTerrainGoldenTests`, `SplatTerrainDistanceGoldenTests`). No committed grid.

| trigger                          | behaviour                                                                     |
| -------------------------------- | ----------------------------------------------------------------------------- |
| `push` / `pull_request` on main  | **verify**: Metal runs the full suite; D3D11 and Vulkan run the golden tests only |
| `schedule` (weekly, Sun 18:00 UTC) | **full sweep**: Metal + D3D11 run the full suite; Vulkan stays golden-only  |
| `workflow_dispatch` `bake=false` | same as `schedule` (Metal + D3D11 full suite, Vulkan golden-only)             |
| `workflow_dispatch` `bake=true`  | **re-bake** (`KE_UPDATE_GOLDENS=1`) and upload per-backend goldens as artifacts |

Software rasterizers on the runners (no real GPU):

- Linux Vulkan → Mesa **lavapipe** (`mesa-vulkan-drivers`). The lavapipe ICD manifest's name/path drifts across
  Ubuntu runner images (it is now `lvp_icd.json`, was `lvp_icd.x86_64.json`), so the workflow **discovers it at
  runtime** and points `VK_ICD_FILENAMES` + `VK_DRIVER_FILES` at it rather than hardcoding. Veldrid 4.9.0's Vulkan
  binding P/Invokes the bare names `libdl` / `libvulkan`, which modern Ubuntu only ships versioned, so the workflow
  also symlinks `libdl.so` → `libdl.so.2` and `libvulkan.so` → `libvulkan.so.1`.
- Windows D3D11 → **WARP** software adapter (automatic fallback when no hardware adapter is present). Verified.

Net result: **all three desktop backends are validated and blocking** - Metal (macOS), Direct3D11 (Windows/WARP),
and Vulkan (Linux/lavapipe). The overall workflow is green only when all three verify.

### Per-backend golden flow

1. **Push / PR = verify.** Each leg verifies its committed goldens (`.metal.txt`, `.direct3d11.txt`,
   `.vulkan.txt`). A backend with no committed goldens **fails with "golden ... missing ... bake it"**.
2. **Generate a new backend's goldens:** run the workflow manually with `bake = true`. The bake legs render
   with `KE_UPDATE_GOLDENS=1` and upload artifacts named `goldens-<backend>`
   (`scene2d.<backend>.txt`, `scene3d.<backend>.txt`).
3. **Commit them:** download the artifacts, drop the files into `KhaozEngine.Tests/Gpu/goldens/`, commit.
   (Metal and D3D11 goldens are already committed this way; the D3D11 set was baked on the WARP runner.)
4. After that, the push/PR legs verify those backends instead of failing.

### Failure-evidence PNGs

A float-delta list tells you a cell moved but not what rendered. So on any non-trivial outcome the golden compare
also writes viewable PNGs (via the BCL-only `KhaozEngine.Imaging.PngWriter`) to `KhaozEngine.Tests/Gpu/goldens-evidence/`
(gitignored; override the dir with `KE_GOLDEN_EVIDENCE_DIR`). Filenames are `<name>.<backend>.<kind>.png`:

- **compare failure** writes three, all at the captured `w`x`h`: `.got.png` (the frame as rendered), `.want.png`
  (the committed golden grid reconstructed as flat nearest-neighbour blocks, same dimensions), and `.diff.png` (a
  per-cell heat map: black = no diff, scaling to red toward 2x tolerance, with over-tolerance cells painted
  full-red with a black inner border so they are unmistakable). The three paths are appended to the failure text.
- **missing golden** writes `.got.png` so a brand-new scene can be eyeballed before its first bake.
- **bake** (`KE_UPDATE_GOLDENS=1`) writes `.bake.png` (the full-res capture) alongside each baked grid.

CI uploads these as artifacts on the `cross-platform-gpu` matrix: `golden-evidence-<backend>` on any failed leg
(`if: failure()`), and the bake evidence rides along in the `goldens-<backend>` bake artifact.

The fast inner-loop CI (`.github/workflows/ci.yml`: build/test/pack/publish, GPU tests skipped) is separate and
untouched.

## Authoring shaders that pass on all three backends

The GLSL shaders cross-compile through SPIRV-Cross to MSL (Metal), HLSL→FXC (Direct3D11), and SPIR-V (Vulkan).
The three backends do NOT fail the same way, so a shader that renders correctly on Metal can be silently wrong on
D3D11 or Vulkan. The golden matrix above is the net; these are the traps it has caught. (Per-shader specifics live
in the `ShaderSources.cs` source comments; this is the consolidated checklist.)

- **Keep a fragment shader's input interpolants CONTIGUOUS from location 0.** If a fragment declares an `in`
  interpolant it never reads, SPIRV-Cross drops it and leaves a HOLE in the pixel-input signature (e.g.
  `vWorldPos`@3 then `vTint`@5, with location 4 absent). On D3D11/WARP that gap misaligns the interpolant
  registers and the highest live interpolant reads garbage: `SplatFrag`'s declared-but-unused `vUv`@4 corrupted
  `vEmissive` and rendered the whole terrain flat white (since fixed), while Metal and Vulkan tolerated it.
  Declare only the interpolants the fragment uses, contiguous from 0; if the paired vertex shader emits extras (a
  shared interpolant layout), number those ABOVE the fragment's live block. (`ModelFrag` is unaffected because it
  reads all of its interpolants.) Note: this is NOT an FXC optimizer miscompile - disabling FXC optimization does
  not fix it; the gapped signature is the cause.
- **The same hole-in-signature landmine also hits VERTEX input signatures, not just fragment interpolants.** If a
  vertex shader only reads some of its declared `in` attributes, SPIRV-Cross drops the unread ones and leaves a
  HOLE in the HLSL vertex-input signature, and FXC/WARP miscompiles it exactly like the fragment case above. In
  10.19.0 the depth-only shadow pass (`ShadowDepthVert`) only read `Position` + the per-instance model matrix, so
  SPIRV-Cross dropped `Normal`/`Color`/`TexCoord`/`Tangent`/`ITint`/`IEmissive`/`ISpecParams` from the signature -
  and building that one pipeline at scene construction corrupted WARP so badly that the MAIN model and splat passes
  rendered no colour afterward (silhouette/normal/depth survived, only `oColor` went blank). Fix pattern: a
  numerically negligible LIVE sink that reads every declared input (summed with a `1e-30` scale, so the optimizer
  cannot fold it away) keeps the signature contiguous without changing the output to the bit. See the in-source
  hazard note next to `ShadowDepthVert` in `KhaozEngine.Render3D/Internal/ShaderSources.cs`.
- **Sample all textures up front, in binding order.** SPIRV-Cross assigns MSL texture indices in the order
  textures are first SAMPLED, not by `binding=`, so sampling a higher-binding texture first makes a lower one read
  the wrong texture on Metal (untextured meshes came out flat-normal coloured). See the `ModelFrag` / `EdgeFrag` /
  `SplatFrag` comments.
- **One uniform buffer per pipeline.** A second fragment UBO reads the first UBO's bytes on Metal; fold extra
  per-material data into the frame UBO (the splat pipeline appends its params after the light arrays in one
  combined UBO). See `SplatVert` / `SplatFrag`.
- **A new render feature needs a pixel-READBACK assertion, not just "it did not throw".** Any `[GpuFact]` test
  runs on Metal (every trigger) and D3D11 (weekly/dispatch) in `cross-platform-gpu.yml` regardless of its name,
  so this is no longer a naming trap, but the underlying lesson stands: the original splat tests asserted no
  throw with no pixel readback, so the D3D11 leg ran them and still let the white-terrain bug through.

When a backend-specific render bug reproduces ONLY on the CI rasterizer (WARP / lavapipe), dump the SPIRV-Cross
output locally to read what that backend's compiler receives:
`Veldrid.SPIRV.SpirvCompilation.CompileVertexFragment(vsGlslBytes, fsGlslBytes, CrossCompileTarget.HLSL, new CrossCompileOptions())`
is a pure cross-compile that runs on any OS and is byte-identical to what FXC sees on Windows - diff the broken
shader's signature against a working sibling. Bisect with many cheap Windows-only `cross-platform-gpu`
`workflow_dispatch` runs (each ~3 min); keep the macOS leg OUT of the bisect loop (it bills 10×) and restore the
full three-backend matrix only to verify the final fix.

## Remaining productization gaps

This release delivers the **verification mechanism**, not a finished cross-platform product. Open items:

1. **Windowed-app native bundling (mostly resolved).** The headless golden tests use `CreateHeadless`
   (no window), so they need no windowing natives. The engine windows through Silk.NET.Windowing (GLFW), which
   bundles its natives per-RID across desktop, so a shipped windowed game needs no hand-bundled SDL2 (no
   `brew install sdl2` on macOS). `libveldrid-spirv` still rides along per-RID via the Veldrid
   GPU packages. The remaining work is run-verifying the windowed path on Windows/Linux hardware (the headless
   matrix above does not open a window).
2. **OpenGL backend deferred.** D3D11 and Vulkan honor Veldrid's clip-space prefer-flags
   (clip-Y / 0..1 depth) like Metal, so they need no special handling. OpenGL's runtime clip-Y / depth
   derivation is the troublesome one and is out of scope here; the `gl` override parses but is unverified.
   (Clip-space-Y itself is not a baked Metal assumption: `GpuClip` derives the clip-Y sign
   from `GpuCapabilities` - identity on Metal / D3D11, flipped on Vulkan. The Vulkan path is verified in
   CI against lavapipe (goldens committed) but not yet on real Vulkan hardware.)
3. **Deferred port-hardening.** Two items are scoped but not yet built: GPU device-lost / device-removed
   handling (recreate the device + resources on a lost swapchain) and a central `Platform` OS-info seam
   (one place that answers OS / RID / capability questions).
4. **Mobile (Android / iOS) is a separate project.** It needs native windowing/lifecycle, Native AOT, and
   build-time shader pre-compilation - not covered by this desktop matrix.
