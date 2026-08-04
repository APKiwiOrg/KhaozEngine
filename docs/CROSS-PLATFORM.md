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
- `Resolve()` (17.21.0) answers the same question but also reports WHERE the answer came from, as a
  `GpuBackendSelection` carrying `Source` (`OsProbe` / `EnvironmentOverride` / `UnrecognizedOverride` /
  `UserPreference` / `FallbackAfterFailure`) and the raw override value. `GpuDeviceContext` logs it once per device, and WARNs on an unrecognized override naming
  the bad value and the backend it fell back to. Check that line before concluding a backend comparison: a
  typo'd override silently uses the OS default and otherwise looks exactly like a successful run.
- A stored USER PREFERENCE (17.23.0) sits between the two, so the full precedence is `KE_GRAPHICS_BACKEND` >
  preference > OS probe. The env override stays on top deliberately: a developer must be able to force a backend
  for a repro regardless of what the player picked. The preference is passed in as a `GpuBackendKind?`
  (`GameAppOptions.GraphicsBackendPreference`), never read from disk by the engine, and `Source` reports
  `UserPreference` when it decided. With no preference the chain behaves exactly as it did before.
- `CreateHeadless` builds the matching offscreen device (`GraphicsDevice.CreateVulkan` / `CreateD3D11` /
  Metal). No window - so the golden tests need no SDL2. It takes no preference and does not fall back, so the
  golden path is unaffected by any of the above.

### When the chosen backend cannot start (17.23.0)

Letting a player pick a backend means letting them pick one their machine cannot run, and the setting that
caused it lives inside the client that then will not start. Two mechanisms, and both are needed:

- **`GpuBackendSelector.IsBackendSupported` / `SupportedBackends()`** are a FUNCTIONAL probe, not a guess:
  Veldrid loads the backend's library, creates an instance, enumerates physical devices, and for Vulkan checks
  the required surface extensions. A game's settings UI must offer only what `SupportedBackends()` returns.
  Results are cached for the process lifetime. `OpenGL` is never reported supported, because there is no
  windowed GL device path.
- **Creation fallback.** A probe pass is necessary but not sufficient: a broken or partial driver can report
  support and still fail at device creation. So `GpuDeviceContext.CreateForWindow` also catches a failed
  creation and falls back to the OS-probe backend, WARNing with the requested backend, the failure, and what it
  fell back to. The retry reuses the same `GpuWindowHandle` (a readonly struct of native pointers, no device
  state), so no second window is created.

The fallback is REPORTED, never repaired: `Source` becomes `FallbackAfterFailure`, `Backend` is what actually
runs, and `RequestedBackend` is what failed. **A game storing a backend preference must clear it on seeing that
source**, or the player retries the same broken choice every launch. The engine cannot, since writing a setting
is file IO and `KhaozEngine.Gpu` does none.

macOS and Linux default behaviour is unchanged. The fallback is skipped entirely when the requested backend
already IS the OS-probe default, which is every call with no override and no preference.

On Direct3D11 there is a second line worth reading before anything else, added in 17.22.0. `GpuDeviceContext`
logs the driver's `D3D11_FEATURE_DATA_THREADING` (`GpuThreadingCaps`, also on `GpuDeviceContext.ThreadingCaps` /
`AppWindow.ThreadingCaps`) and WARNs when `DriverCommandLists` is FALSE. That case means the driver cannot build
deferred-context command lists, so the D3D11 runtime emulates them in software and pays a fixed cost per recorded
command. It is a plausible explanation for a Windows box being many times slower than the same hardware on
Vulkan, and it is invisible without this line. Null on every other backend and off Windows, which is why the line
only appears on D3D11.

17.24.0 adds two more lines under the same backend line. `GPU adapter: <name>` is logged on EVERY backend (on
Direct3D11 it is exactly the DXGI adapter description), reachable as `GpuDeviceContext.AdapterDescription` /
`AppWindow.AdapterDescription`. On Windows the engine also scans the process's loaded modules against a list of
known overlay and capture injectors (`GpuInjectedModules`, on `GpuDeviceContext.InjectedModules` /
`AppWindow.InjectedModules`) and WARNs on any match, because that software hooks Direct3D and causes stutter,
corrupted frames, and driver crashes that read as engine bugs. That scan is gated on Windows rather than on the
backend, so a Windows Vulkan session logs it too. A null result means the scan never ran and is deliberately
distinct from an empty one, which means it ran and found nothing.

For chasing a Direct3D11 threading stall specifically, `KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS=1` adds
`D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS` at device creation and logs an INFO line proving
it was on. It is a probe, not a fix: it can cost performance, so it is off by default.

## Backend-aware goldens

`GoldenCompare.GoldenPath(name)` resolves `KhaozEngine.Render.Tests/Gpu/goldens/<name>.<backend>.txt` where
`<backend>` = `GoldenCompare.GoldenBackendToken(GpuBackendSelector.Select())`. Each rendering API has its own
reference grid because a software rasterizer (lavapipe, WARP) does not match Apple Metal pixel-for-pixel.
Per-backend goldens absorb that while still catching real shader / UBO / blend / winding / orientation
regressions (coarse 32×18 grid, per-channel tolerance).

The token is a mapping rather than the enum name, because two IMPLEMENTATIONS of one API share a family.
`GpuBackendKind.Direct3D11Native` resolves to `direct3d11`, so the native backend is held to the incumbent's
already-committed references, unmodified, on the same WARP rasterizer at the same tolerance. That sharing is the
strongest free proof the native port has, so it is guarded in the other direction too: `KE_UPDATE_GOLDENS`
REFUSES to write when the running backend does not OWN its family, unless `KE_GOLDEN_FAMILY_OVERRIDE=1` says the
shared family is being moved on purpose. Without that guard a bake on the native leg would overwrite both the
reference it is being checked against and the incumbent's, and the file it wrote would be exactly the file it
would then have compared against, so nothing downstream could notice.

The Metal goldens (`scene2d.metal.txt`, `scene3d.metal.txt`), the Direct3D11 goldens
(`scene2d.direct3d11.txt`, `scene3d.direct3d11.txt`, baked on WARP), and the Vulkan goldens
(`scene2d.vulkan.txt`, `scene3d.vulkan.txt`, baked on lavapipe) are all committed and verified on every macOS /
Windows / Linux run respectively.

The 2D golden loads a libre font bundled in the test project (`KhaozEngine.Render.Tests/Assets/Roboto-Regular.ttf`,
Apache-2.0) rather than an OS system-font path, so its glyph input is identical on every runner.

`KhaozEngine.Render.Tests/Gpu/goldens/*.txt` is meant to be pinned `text eol=lf` in `.gitattributes`, though the
pin still names the pre-split `KhaozEngine.Tests/Gpu/goldens/*.txt` path today, tracked as
[#213](https://github.com/APKiwiOrg/KhaozEngine/issues/213). The goldens are machine-generated
LF text, and a Windows checkout with `autocrlf` on would otherwise convert them to CRLF, breaking the byte-identity
contract between the committed file and `GoldenGrid.Serialize`'s LF output. This exact failure shipped and was
fixed in 10.18.1 (Metal and Vulkan legs and every actual golden compare were green, only the endings differed).

## CI matrix (`.github/workflows/cross-platform-gpu.yml`)

The suite each leg runs is split by trigger, from measured hosted-runner cost
(`KE_GPU_TESTS=1`, `fail-fast: false`):

- **Self-hosted Metal leg (free)**: the WHOLE test suite (`--filter "Category!=LiveSocket"`) on every
  trigger - every `[GpuFact]` test (golden and behavioral) plus the headless suite, matching `ci.yml`'s
  own `Category!=LiveSocket` exclusion (LiveSocket tests need a live network peer, not available here).
- **GitHub-hosted D3D11 leg (2x billing)**: golden tests only (`FullyQualifiedName~Golden`) on
  `push`/`pull_request`, and the WHOLE suite on the weekly `schedule` (Sunday 18:00 UTC) and on
  `workflow_dispatch`. The full suite on hosted Windows measured 17m14s vs the golden-only 7m44s, about
  +19 billed 2x minutes per run - too costly per-push, so full hosted coverage rides the weekly cron and
  any manual dispatch instead.
- **GitHub-hosted D3D11 native leg (2x billing)**: the engine's own `KhaozEngine.Gpu.D3D11` backend
  (`KE_GRAPHICS_BACKEND=direct3d11-native`) on exactly the incumbent leg's tier, golden tests only on
  `push`/`pull_request` and the WHOLE suite on the weekly `schedule` and on `workflow_dispatch`, for the same
  measured cost reason at the same 2x rate. It is a GUEST in the incumbent's golden family: it verifies the
  committed `.direct3d11.txt` grids on the same WARP rasterizer and never bakes them, so `KE_UPDATE_GOLDENS`
  stays empty on it for every trigger and it sits out bake dispatches entirely. It also pins
  `KE_D3D11_ADAPTER=warp` instead of inheriting the incumbent's implicit fallback, so a runner image that grows
  a paravirtual adapter cannot quietly change the rasterizer under the shared goldens.
- **GitHub-hosted Vulkan leg (1x)**: golden tests only (`FullyQualifiedName~Golden`) on
  `push`/`pull_request`, and the WHOLE suite on the weekly `schedule` and on `workflow_dispatch`, the
  same tier as D3D11, with the full-suite runs serializing xUnit test collections
  (`-- xUnit.ParallelizeTestCollections=false`, one live device at a time). Chasing this leg's
  full-suite crashes (reproduced 4/4 in an amd64 container running the exact CI Mesa version, 25.2.8)
  surfaced four real engine defects, all fixed: concurrent device creation racing the Vulkan loader's
  dispatch setup (fixed by serializing device create/dispose process-wide in
  `KhaozEngine.Gpu.GpuDeviceContext`), mid-life GPU resource disposal racing queued async work (fixed
  by draining the device via `IGpuDevice.WaitForIdle` before every mid-life disposal, the drain rule is
  documented in `docs/USING-KHAOZENGINE.md`), and a teardown-order pair where a resource wrapper
  outliving its device drained or destroyed against the destroyed device (fixed by a shared liveness
  latch). The full PARALLEL suite on lavapipe still exhibits residual driver-side instability with
  delayed-corruption symptoms (a CoreCLR fatal, deliberately not chased), so the weekly full suite runs
  serialized, measured ~22 min in the validation container versus minutes parallel. Golden-only runs
  keep their months-green parallel configuration.

Historically the test step filtered every leg to `FullyQualifiedName~Golden`, so any `[GpuFact]` class
without "Golden" in its name never ran on ANY backend (`Scene3DTextureUnloadTests`, `WaterQueueTests`,
`RenderServiceTests`, and dozens of other classes were never exercised on Metal/D3D11/Vulkan). Every GPU
test now runs on Metal (every trigger), D3D11, and Vulkan (both weekly + dispatch) regardless of what it
is called. The name is not a CI contract, and neither is the directory: `[GpuFact]` classes live across
many namespaces (Render3D, Terrain, MapEditor, MapEditTool, ParticlesRender3D, Snapshot, and more, some
25 files outside `KhaozEngine.Render.Tests/Gpu` alone), so a path or name filter is not a coverage contract
either. The first full-suite sweeps surfaced 13 real Windows portability bugs, a dispose-before-submit
contract violation in three test classes that only Vulkan enforces, and the lavapipe host-crash
instability above - all of which the golden-only filter had been hiding.

`KE_GPU_TESTS` accepts two values. `1` is strict (CI and the dev Mac): tests run, and a device-creation
failure is a test error, never a skip, so CI cannot go green with zero GPU coverage. `probe` is for
arbitrary machines: a one-per-process headless device probe runs the tests when a device exists and
skips them with the probe's failure reason when it does not.

A test may additionally declare a device CAPABILITY it needs: `[GpuFact(RequiresCompletionFences = true)]`
skips, naming the backend, on a device that reports no `GpuCapabilities.SupportsCompletionFences` (a
Vulkan and Metal capability today, so the D3D11 leg skips those two retire-fence tests instead of failing
an assertion it can never satisfy - issue #423). This is the only skip strict mode allows, and it is not a
hole in it: if no device can be created at all, the capability probe reports nothing and the test still
errors, so a leg with a broken device can never go quiet.

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
| `push` / `pull_request` on main  | **verify**: Metal runs the full suite. Both D3D11 legs and Vulkan run the golden tests only |
| `schedule` (weekly, Sun 18:00 UTC) | **full sweep**: Metal + both D3D11 legs + Vulkan all run the full suite (Vulkan serialized) |
| `workflow_dispatch` `bake=false` | same as `schedule` (all four legs full suite, Vulkan serialized)              |
| `workflow_dispatch` `bake=true`  | **re-bake** (`KE_UPDATE_GOLDENS=1`) on Metal, D3D11 and Vulkan, uploaded as per-backend goldens. The native leg skips its test step: it owns no family to bake, and verifying against references being replaced mid-run would only red it |

Software rasterizers on the runners (no real GPU):

- Linux Vulkan → Mesa **lavapipe** (`mesa-vulkan-drivers`). The lavapipe ICD manifest's name/path drifts across
  Ubuntu runner images (it is now `lvp_icd.json`, was `lvp_icd.x86_64.json`), so the workflow **discovers it at
  runtime** and points `VK_ICD_FILENAMES` + `VK_DRIVER_FILES` at it rather than hardcoding. Veldrid 4.9.0's Vulkan
  binding P/Invokes the bare names `libdl` / `libvulkan`, which modern Ubuntu only ships versioned, so the workflow
  also symlinks `libdl.so` → `libdl.so.2` and `libvulkan.so` → `libvulkan.so.1`.
- Windows D3D11 → **WARP** software adapter (automatic fallback when no hardware adapter is present) on the
  incumbent leg. Verified. The native leg does not ride that fallback: it pins `KE_D3D11_ADAPTER=warp`, so the
  rasterizer under the shared goldens is stated rather than inherited from the runner image.

Net result: **all four legs are blocking, none of them informational** - Metal (macOS), Direct3D11
(Windows/WARP), Direct3D11 native (Windows/WARP), and Vulkan (Linux/lavapipe). Three of them are long
validated. The native leg blocks by design rather than by record: it is the native backend's continuous
exercise, so it gates from its first run, and its first recorded evidence is rollout gate 1 on
[#460](https://github.com/APKiwiOrg/KhaozEngine/issues/460). The overall workflow is green only when all four
verify, with the one exception in the table above: on a `bake=true` dispatch the native leg skips its test step,
so that run is green on the three baking legs alone.

### Per-backend golden flow

1. **Push / PR = verify.** Each leg verifies the committed goldens of its FAMILY (`.metal.txt`,
   `.direct3d11.txt`, `.vulkan.txt`). Three legs own the family they verify. The native leg owns none: it is a
   guest in `direct3d11`, so it checks the incumbent's files on the same rasterizer and never writes them. A
   family with no committed goldens **fails with "golden ... missing ... bake it"**.
2. **Generate a new backend's goldens:** run the workflow manually with `bake = true`. The bake legs render
   with `KE_UPDATE_GOLDENS=1` and upload artifacts named `goldens-<backend>`
   (`scene2d.<backend>.txt`, `scene3d.<backend>.txt`).
3. **Commit them:** download the artifacts, drop the files into `KhaozEngine.Render.Tests/Gpu/goldens/`, commit.
   (Metal and D3D11 goldens are already committed this way; the D3D11 set was baked on the WARP runner.)
4. After that, the push/PR legs verify those backends instead of failing.

### Failure-evidence PNGs

A float-delta list tells you a cell moved but not what rendered. So on any non-trivial outcome the golden compare
also writes viewable PNGs (via the BCL-only `KhaozEngine.Imaging.PngWriter`) to `KhaozEngine.Render.Tests/Gpu/goldens-evidence/`
(gitignored; override the dir with `KE_GOLDEN_EVIDENCE_DIR`). Filenames are `<name>.<backend>.<kind>.png`:

- **compare failure** writes three, all at the captured `w`x`h`: `.got.png` (the frame as rendered), `.want.png`
  (the committed golden grid reconstructed as flat nearest-neighbour blocks, same dimensions), and `.diff.png` (a
  per-cell heat map: black = no diff, scaling to red toward 2x tolerance, with over-tolerance cells painted
  full-red with a black inner border so they are unmistakable). The three paths are appended to the failure text.
- **missing golden** writes `.got.png` so a brand-new scene can be eyeballed before its first bake.
- **bake** (`KE_UPDATE_GOLDENS=1`) writes `.bake.png` (the full-res capture) alongside each baked grid.

CI uploads these as artifacts on the `cross-platform-gpu` matrix: `golden-evidence-<backend>` on any failed leg
(`if: failure()`), and the bake evidence rides along in the `goldens-<backend>` bake artifact. Every leg also
uploads `golden-deltas-<backend>` on `always()`, a few kilobytes of text rather than pixels: each compare
appends its worst-cell delta (via `GoldenDeltaLog`) to `golden-deltas.<backend>.txt` in the same evidence dir on
a PASS as well as a fail, and a failure-only upload could only ever show that number after something had already
broken. The two `<backend>` slots are not the same token, which matters when you go looking for the native leg's
numbers: the ARTIFACT is named for the leg (`golden-deltas-direct3d11-native`) and the FILE inside it is named
for the golden family the leg verified (`golden-deltas.direct3d11.txt`). So the guest leg's deltas arrive under
its own artifact carrying the shared family's filename, and downloading both Windows artifacts gives you two
same-named files that are two implementations measured against one set of references.

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
  hazard note next to `ShadowDepthVert` in `KhaozEngine.Render3D/Internal/ShaderSources.Shadow.cs`. Its
  dissolve-aware sibling (`ShadowDepthDissolveVert`, 17.x) declares the model pass's full 0..13 input set and
  carries the same sink over everything it does not genuinely read, for the same reason.
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
