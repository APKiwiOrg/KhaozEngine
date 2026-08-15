# Cross-platform desktop GPU

KhaozEngine's custom stack (Render2D / Render3D) runs on Veldrid. Each desktop OS gets a native graphics
backend; the GPU golden-snapshot net verifies rendering on each one through a CI matrix.

## Platform → backend (desktop scope)

| OS      | Backend the OS probe picks | `GpuBackendKind` | golden file suffix | engine-owned native backend (opt-in, same family) | software rasterizer (CI) |
| ------- | -------------------------- | ---------------- | ------------------ | ------------------------------------------------- | ------------------------ |
| macOS   | Metal                      | `Metal`          | `.metal.txt`       | `MetalNative` (`KhaozEngine.Gpu.Metal`)           | none, a real Apple GPU   |
| Windows | Direct3D11                 | `Direct3D11`     | `.direct3d11.txt`  | `Direct3D11Native` (`KhaozEngine.Gpu.D3D11`)      | WARP (auto fallback)     |
| Linux   | Vulkan                     | `Vulkan`         | `.vulkan.txt`      | `VulkanNative` (`KhaozEngine.Gpu.Vulkan`)         | Mesa lavapipe            |

Every API in that table has TWO implementations behind it now, and the right-hand column is the engine's own.
Each native backend is opt-in (its package is in no umbrella and registers explicitly), is selected by nothing
by default, and shares the incumbent's golden family rather than owning one, which is what makes its CI leg a
verification of the incumbent's committed references on the incumbent's own rasterizer. The macOS row is the
one whose rasterizer column reads "none": it is the only family baked on real hardware, and the only leg in
this matrix where a golden disagreement is about a GPU rather than about a software rasterizer.

Backend selection is centralized in `KhaozEngine.Gpu.GpuBackendSelector`:

- `Select()` reads the `KE_GRAPHICS_BACKEND` env override (`metal` / `metal-native` / `vulkan` /
  `vulkan-native` / `d3d11` / `d3d11-native` / `gl`, case-insensitive, with `mtl-native`, `vk-native`,
  `direct3d11` and `direct3d11-native` as aliases),
  otherwise probes the OS (macOS → Metal, Windows → Direct3D11, Linux/other → Vulkan). **The OS probe still
  names the three Veldrid backends**: no native backend is a default anywhere, and on Linux the flip to
  `VulkanNative` is the last step of that backend's rollout rather than a consequence of the token existing
  ([#529](https://github.com/APKiwiOrg/KhaozEngine/issues/529)). On macOS the flip to `MetalNative` is the same
  last step of the phase-4 rollout ([#566](https://github.com/APKiwiOrg/KhaozEngine/issues/566)), and it is the
  one with the largest blast radius of the three: macOS is the fleet's development platform, so it would move
  every windowed playtest, capture, editor session and local golden bake on the day it landed.
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
`GpuBackendKind.Direct3D11Native` resolves to `direct3d11`, `GpuBackendKind.VulkanNative` to `vulkan` and
`GpuBackendKind.MetalNative` to `metal`, so each native backend is held to the incumbent's already-committed
references, unmodified, on the same rasterizer at the same tolerance. The Metal pair is the one that is not
symmetric with the other two: `metal` is the only family baked on real hardware rather than on a software
rasterizer, and it is the fleet's cross-backend reference, so a guest disagreeing there is a fleet event
rather than a leg event. That sharing is the
strongest free proof a native port has, so it is guarded in the other direction too: `KE_UPDATE_GOLDENS`
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

- **GitHub-hosted Metal leg (`macos-26`)**: the WHOLE test suite (`--filter "Category!=LiveSocket"`) on every
  trigger - every `[GpuFact]` test (golden and behavioral) plus the headless suite, matching `ci.yml`'s
  own `Category!=LiveSocket` exclusion (LiveSocket tests need a live network peer, not available here). It ran
  on the dev Mac until [#552](https://github.com/APKiwiOrg/KhaozEngine/issues/552) moved it to a hosted runner,
  and the runner is pinned to `macos-26` by number rather than to `macos-latest` so an image promotion cannot
  move the GPU under a golden gate.
- **GitHub-hosted Metal NATIVE leg (`macos-26`)**: the engine's own `KhaozEngine.Gpu.Metal` backend
  (`KE_GRAPHICS_BACKEND=metal-native`) on exactly the incumbent Metal leg's tier, which is the whole suite on
  every trigger. It is a GUEST in the incumbent's `metal` golden family exactly as the other two native legs are
  guests in theirs, so `KE_UPDATE_GOLDENS` stays empty on it for every trigger and it sits a bake dispatch out
  entirely. **This is the strongest regression net in the program**: the other two native legs run golden-only
  on push, on software rasterizers, and this one runs everything on a real GPU every time. Three things are its
  own rather than the incumbent Metal leg's. It sets `KE_METAL_REQUIRED=1`, for the reason the Vulkan native leg
  sets its own equivalent below. It arms `MTL_DEBUG_LAYER=1` on every run and `MTL_SHADER_VALIDATION=1` on the
  deep tier, which are Metal's two validation tiers. And it arms `MTL_CAPTURE_ENABLED=1` on the `capture`
  dispatch tier alone, which is what lets the frame-capture suite really start a capture and write a
  `.gputrace` bundle rather than only assert that an unarmed process refuses to.
  **A dispatch therefore picks one of three shapes** (`tier`: `push`, `deep` or `capture`), because no two of
  this leg's three Metal variables can usefully share a process:
  a capture cannot share one with `MTL_SHADER_VALIDATION` (the manager
  reports the GPU-trace destination as supported and `startCapture` returns false anyway), and, measured for
  [#614](https://github.com/APKiwiOrg/KhaozEngine/issues/614), **a capture displaces the debug device too**. On
  real Metal (Mac14,6 / Apple M2 Max, macOS 26.6.1 build 25G76), one row under `KE_GPU_TESTS=1`
  `KE_GRAPHICS_BACKEND=metal-native` at detailed verbosity:

  | launch environment | what `MetalGpuDevice` reported |
  | --- | --- |
  | `MTL_DEBUG_LAYER=1` | `The device class is MTLDebugDevice`, no disambiguation warning |
  | `MTL_DEBUG_LAYER=1 MTL_CAPTURE_ENABLED=1` | `The device class is CaptureMTLDevice`, plus the WARN that the run `is probably NOT validated` |

  `MTLDebugDevice` is the class that performs the API validation, so the arrangement this replaces (capture on
  every trigger except the deep dispatch) left tier one INERT on every push, pull request and cron this leg has
  ever run. Every unattended trigger takes the debug-device shape now, and the capture's 2.5-second cost
  (`KhaozEngine.Render.Tests` at `Category!=LiveSocket` under `KE_GRAPHICS_BACKEND=metal-native` with
  `MTL_DEBUG_LAYER=1`, on an M2 Max: 22.4s without it against 25.0s with it) stopped being the deciding
  argument the moment the real cost turned out to be the instrument rather than the clock. What that trade
  costs, stated rather than glossed: `MetalFrameCaptureTests`' positive arm, the one that starts a capture from
  the native queue pointer and asserts the bundle, is attended-only coverage now, and its negative arm is what
  every unattended trigger runs.
  **The deep tier is a DISPATCH and nothing else, and the cron does not arm it**
  ([#617](https://github.com/APKiwiOrg/KhaozEngine/issues/617)). Its first ever armed run failed 186 of 6201
  rows on this leg (run `31581572414`), with every golden reading back as exactly the pass clear colour: the
  clears landed and no draw produced a fragment, across every unrelated subsystem at once. Three readings say
  the rung is what broke rendering on this runner, rather than the rung reporting an engine bug. The same
  commit, suite and rung are fully green on real Apple silicon (6202 passed, 0 failed, 0 skipped on an M2 Max
  under macOS 26.5, with the host's own log confirming tier `Shaders` and a device class of `MTLDebugDevice`).
  The rung's COST has the wrong sign on the runner: on that same M2 Max it multiplies this leg's own test
  assembly by 3.5x to 4x (`KhaozEngine.Render.Tests` at `Category!=LiveSocket` under
  `KE_GRAPHICS_BACKEND=metal-native`, measured on two sittings), where on the hosted paravirtual device the
  armed run was 29% FASTER than the same leg's debug-layer-only run (7m46s in run `31581572414` against 10m54s
  in run `31585988023`), which is work being dropped rather than instrumented. And the assert error mode below
  means an objection would have aborted the host, which did not happen, so nothing objected.
  What none of that establishes is that the ENGINE is clean. The paravirtual failure stays unresolved until
  the first armed artifact taken with the sink configured, because a `MetalDeviceLossLatch` command-buffer
  failure fits the observed shape exactly: it logs, it flips the device's liveness, it fails no row, and drains
  against a dead device return immediately, which is a run that is faster and clears to the pass colour without
  drawing. That is the same silent-loss mechanism
  [#614](https://github.com/APKiwiOrg/KhaozEngine/issues/614) carries as a live candidate for its own varying
  failures on this leg, so one armed artifact is likely to answer both or neither. So a human aims that rung
  now, and the cron keeps the debug layer alone.
  **The control that issue asks for is a dispatch input**, `incumbentShaderValidation`, default false: it arms
  `MTL_SHADER_VALIDATION=1` on the INCUMBENT Metal leg and nothing else, which this matrix had never done, so
  the green incumbent leg on run `31581572414` is not the control it looks like. Green under the rung means the
  rung is fine on the paravirtual runner and #617's 186 failures are native-leg-specific. Red with the same
  blank-render shape means the runner drops work under the rung for any backend. It arms shader validation
  ALONE and never the debug layer: the layer at its default assert error mode used to abort the incumbent host,
  attributed by [#621](https://github.com/APKiwiOrg/KhaozEngine/issues/621) to one row that stands down under
  the layer on the incumbent now, and arming it on a blocking golden leg is still the wrong place to discover
  the next provoking row.
  **Neither Metal tier sets an error MODE, and the default is assert.** A validation error there aborts the test
  host rather than failing a row, so the rows that provoke the layer on purpose stand down in-process instead
  ([#591](https://github.com/APKiwiOrg/KhaozEngine/issues/591)). `MTL_DEBUG_LAYER_ERROR_MODE=nslog` stops the
  abort and reports nothing at all to the captured stream, so it is rejected: a tier that can neither fail nor
  testify is worse than none.
  **A failed command buffer is NOT a failed row**, which is the blindness #617 ran into.
  `MetalDeviceLossLatch` logs the driver's own description and flips the device's liveness, and nothing in it
  fails a test, so a leg can go red for a pile of unrelated-looking golden reasons with the real fault sitting
  in a log line. Until that issue, that line went to a `NullLogger`: the test host configures a sink for the
  `Metal` log categories only when a Metal rung is armed, and the leg forwards the host's stdout at detailed
  verbosity on exactly the runs that arm one, which is every run of this leg. Those are the two halves it takes
  for the line to reach the artifact at all, and they read the same predicate, so the artifact cannot exist
  with the engine's half of it missing.
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
  stays empty on it for every trigger and it sits out bake dispatches entirely. Both Windows legs pin
  `KE_D3D11_ADAPTER=warp` instead of inheriting the implicit fallback, so a runner image that grows a
  paravirtual adapter cannot quietly change the rasterizer under the shared goldens. The incumbent leg needs
  the pin because it creates native devices too, through the parity tests, and the variable is read only by the
  native backend's adapter selection, so the Veldrid device that leg also creates is unaffected.
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
- **GitHub-hosted Vulkan native leg (1x)**: the engine's own `KhaozEngine.Gpu.Vulkan` backend
  (`KE_GRAPHICS_BACKEND=vulkan-native`) on exactly the incumbent Vulkan leg's tier, golden tests only on
  `push`/`pull_request` and the WHOLE suite on the weekly `schedule` and on `workflow_dispatch`, serialized
  the same way and for the same reason. It is a GUEST in the incumbent's golden family, verifying the
  committed `.vulkan.txt` grids on the same lavapipe rasterizer and never baking them, so `KE_UPDATE_GOLDENS`
  stays empty on it for every trigger and it sits out bake dispatches entirely. Two things are its own. It
  sets **`KE_VULKAN_REQUIRED=1`**, because the rows that need a real native device go DORMANT when the
  backend's functional probe refuses the machine, and a dormant row is not a skip, so on the one leg built to
  run them a loader regression would empty them into passes that assert nothing and the zero-skipped gate
  could not see it. With the variable set, that refusal throws and names what the probe objected to. And its
  full suite runs under **`KE_VULKAN_VALIDATION=strict`**, the first tier of the validation gate.
- **Vulkan sync-validation job (1x, `gpu-vulkan-sync`)**: the second tier. Core validation PLUS
  synchronisation validation over the golden subset and the compute suite
  (`FullyQualifiedName~Golden|FullyQualifiedName~Compute`) on the native backend, on the `schedule` and on a
  non-bake `workflow_dispatch`. It is a separate job rather than a matrix leg because it runs a different
  suite for a different reason: synchronisation validation tracks every access and is slow, so it buys the
  subset where the barrier machinery lives instead of the serialized full suite. **It is the only instrument
  in this net that can see a missing barrier or a wrong image layout.** A software rasterizer executes with
  far stronger implicit ordering than a real GPU, so that class of defect passes every golden here and
  corrupts on the field GPU, on the one machine that is not in CI, and core validation does not catch it
  either. **The job gates on error severity itself, through a scan of the log it tees**, and the engine is not
  what fails it. `KE_VULKAN_VALIDATION=sync` deliberately does not latch or throw the way `strict` does, since
  the two rungs want opposite things from an error: `strict` stops at the first one, and `sync` finishes the
  sweep so one run reports every hazard rather than costing a run per finding. That leaves a tier with nothing
  to fail it, which matters because rollout gate 3 and MV6's VMA decline are both read off this job being
  green, so the scan step supplies the teeth at the CI level and engine-side latching stays strict-only by
  design. The scan matches the engine pump's own `Vulkan validation [Error]` format and the Khronos layer's
  `Validation Error:` message prefix, and it prints the count of validation lines at any severity on every run,
  so a scan that has quietly stopped matching anything is visible instead of reading as a clean sweep. The
  engine arm of that scan could not fire until [#565](https://github.com/APKiwiOrg/KhaozEngine/issues/565): the
  pump logged through an ambient facade nothing in the test process had configured, so it wrote to a no-op
  logger on every leg. The artifacts section below has what each producer contributes now.

**The validation layer is an install, not a knob**, which is why the tiers arrived with a workflow step
rather than a variable. Before them, `VK_LAYER`, `VK_INSTANCE_LAYERS` and `vulkan-validationlayers` had zero
hits across every workflow, script and source file in this repo, and the Vulkan legs' own layer enumeration
listed three Mesa and Intel layers with no Khronos validation among them. `VK_EXT_debug_utils` is an instance
extension, so the messenger the engine pumps messages through needs no install, but the LAYER cannot be
turned on with an environment variable. The install is scoped to the legs that use it, so a package rename on
a future runner image cannot redden the incumbent leg, and the layer manifest is then CHECKED: a missing
layer only WARNs and creates the device anyway, which would leave a validation gate passing while validating
nothing.

**A dispatch can run the whole matrix with the GPU shader disk caches OFF** (`disableGpuDiskCache`, default
false). The three backends' caches are one mechanism, `KhaozEngine.Gpu/Internal/GpuDiskCache`, reached through
`KE_METAL_MSL_CACHE`, `KE_D3D11_SHADER_CACHE` and `KE_VULKAN_PIPELINE_CACHE`. Each takes a directory path
verbatim, treats blank as the default location under local app data, and recognises five disable words
(`off`, `0`, `false`, `no`, `none`, trimmed and case-insensitive), which is why anything else, a typo included,
is read as a directory name and caches happily under it. The input sets all three to `off` on every leg and on
the sync job, so a cacheless run is uniform and nothing in it is left comparing a cacheless leg against a
cached one. Empty is the shipped default rather than a third state, so every push, the cron and every dispatch
that leaves the box unticked behave exactly as before the input existed.
It exists for [#614](https://github.com/APKiwiOrg/KhaozEngine/issues/614), where the metal-native leg fails
roughly one varying GPU test per boot on the hosted paravirtual adapter, bit-identically wrong when it is
wrong, while the same commit passes on real Metal locally and the incumbent Metal leg passes beside it. A warm
cache entry read back on an unhealthy boot would be a stable wrong answer, and an adapter fault would not have
to be, so running the same commit cacheless is what tells those apart. Measured end to end on real Metal: cold
with the cache on writes 32 `.kemsl` entries and costs about 3 s of emission, warm costs 140 ms and writes
nothing, and warm with `off` set costs the full 3 s again and does not touch one of the 32 entries, which is
what "neither read nor written" looks like from outside the process.

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

**A committed grid sees the FINAL image and nothing else, which is a smaller claim than the family's name
suggests.** `MsaaResolveTargetGoldenTests` exists because of a measured case
([#603](https://github.com/APKiwiOrg/KhaozEngine/issues/603)): `Golden3D_HdrMsaa` drives the MSAA resolve path
six times per run, twice with a render encoder already open, and every one of the 91 goldens stayed green with
the first of `RenderResources.ResolveDepthNormal`'s back-to-back pair silently discarded. A 32x18 grid of
per-cell average RGB cannot see one intermediate target holding the previous frame. So that test reads the
resolve DESTINATIONS back after a real `Scene3D` frame and checks them against the same scene rendered on the
single-sample path, where those two textures ARE the MRT attachments and no resolve happens: one path checking
the other, in one session on one device, with nothing committed and nothing to bake. Its device-free sibling
`MsaaResolveWiringTests` asserts which resolves the pass records, from which source into which destination, in
what order, and needs no GPU at all. The general lesson is worth more than the pair: when a change lands on an
INTERMEDIATE target, ask what in the final image would have to move before assuming the golden family covers it.

| trigger                          | behaviour                                                                     |
| -------------------------------- | ----------------------------------------------------------------------------- |
| `push` / `pull_request` on main  | **verify**: both Metal legs run the full suite. Both D3D11 legs and both Vulkan legs run the golden tests only. The only validation tier that runs is Metal's debug layer, which the native Metal leg arms on every trigger, and it arms it ALONE: `MTL_CAPTURE_ENABLED` would displace the `MTLDebugDevice` that does the validating ([#614](https://github.com/APKiwiOrg/KhaozEngine/issues/614)) |
| `schedule` (weekly, Sun 18:00 UTC) | **full sweep**: both Metal legs + both D3D11 legs + both Vulkan legs all run the full suite (both Vulkan legs serialized, the native one under `strict` validation), plus the `sync` validation job. The native Metal leg keeps its debug layer here, alone for the same #614 reason, and does NOT arm `MTL_SHADER_VALIDATION` ([#617](https://github.com/APKiwiOrg/KhaozEngine/issues/617)) |
| `workflow_dispatch` `bake=false` | same as `schedule` (all six legs full suite, both Vulkan legs serialized, plus the `sync` job), plus the two things no other trigger can do. `tier` picks the native Metal leg's shape: `deep` (the default) adds `MTL_SHADER_VALIDATION=1`, `capture` adds `MTL_CAPTURE_ENABLED=1` instead, and `push` is the unattended debug-device shape. And `incumbentShaderValidation=true` arms `MTL_SHADER_VALIDATION=1` on the INCUMBENT Metal leg, which is #617's control |
| `workflow_dispatch` `bake=true`  | **re-bake** (`KE_UPDATE_GOLDENS=1`) on Metal, D3D11 and Vulkan, uploaded as per-backend goldens. All three guest legs skip their test step and the `sync` job does not run at all: a guest owns no family to bake, and verifying against references being replaced mid-run would only red it |

Software rasterizers on the runners (no real GPU):

- Linux Vulkan → Mesa **lavapipe** (`mesa-vulkan-drivers`). The lavapipe ICD manifest's name/path drifts across
  Ubuntu runner images (it is now `lvp_icd.json`, was `lvp_icd.x86_64.json`), so the workflow **discovers it at
  runtime** and points `VK_ICD_FILENAMES` + `VK_DRIVER_FILES` at it rather than hardcoding. Veldrid 4.9.0's Vulkan
  binding P/Invokes the bare names `libdl` / `libvulkan`, which modern Ubuntu only ships versioned, so the workflow
  also symlinks `libdl.so` → `libdl.so.2` and `libvulkan.so` → `libvulkan.so.1`. That symlink step runs on BOTH
  Linux legs, and it is not dead on the native one. Silk.NET resolves through its own native-context search and
  needs no symlink, which is what makes the step LOOK like the incumbent's alone, but the capability-parity test
  creates a Veldrid Vulkan device beside the native one on whichever leg it runs, so dropping the step from the
  native leg breaks that test at device creation with a `DllNotFoundException` that reads as a mystery rather
  than as a deleted workaround. The step retires with the Veldrid Vulkan leg itself
  ([#540](https://github.com/APKiwiOrg/KhaozEngine/issues/540)) and not before.
  Both Linux legs additionally pin `KE_VULKAN_DEVICE=llvmpipe`, the device-level belt to the loader-level brace,
  and the incumbent leg needs it because it creates native devices too, through the capability-parity test. The
  variable has exactly one reader, the native backend's physical-device selection, so no Veldrid device sees it.
- Windows D3D11 → **WARP** software adapter (automatic fallback when no hardware adapter is present) for the
  incumbent's Veldrid device. Verified. Neither Windows leg rides that fallback for the NATIVE devices it
  creates: both pin `KE_D3D11_ADAPTER=warp`, so the rasterizer under the shared goldens is stated rather than
  inherited from the runner image.

Net result: **all six legs are blocking, none of them informational** - Metal (macOS), Metal native (macOS),
Direct3D11 (Windows/WARP), Direct3D11 native (Windows/WARP), Vulkan (Linux/lavapipe) and Vulkan native
(Linux/lavapipe). Three of them are long validated. The three guest legs block by design rather than by record:
a native backend's CI leg is its continuous exercise, so it gates from its first run, and their first recorded
evidence is rollout gate 1 on [#460](https://github.com/APKiwiOrg/KhaozEngine/issues/460),
[#529](https://github.com/APKiwiOrg/KhaozEngine/issues/529) and
[#566](https://github.com/APKiwiOrg/KhaozEngine/issues/566). The overall workflow is green only when all six
verify, with the one exception in the table above: on a `bake=true` dispatch all three guest legs skip their
test step, so that run is green on the three baking legs alone.

**Neither incumbent leg is coupled to its native sibling's health, and that is the same decision made twice.**
The incumbent Vulkan leg installs no validation layer and sets no `KE_VULKAN_REQUIRED`. The incumbent Metal leg
arms neither Metal validation tier and sets no `KE_METAL_REQUIRED`. On both, the rows that touch a native device
stay dormant if the probe ever refuses the runner. Those legs are the escape hatches the rollouts keep
selectable indefinitely, and an escape hatch that goes red whenever the thing it escapes from goes red is not
one.

### Per-backend golden flow

1. **Push / PR = verify.** Each leg verifies the committed goldens of its FAMILY (`.metal.txt`,
   `.direct3d11.txt`, `.vulkan.txt`). Three legs own the family they verify. The three native legs own none:
   each is a guest in its incumbent's family, so it checks the incumbent's files on the same rasterizer (the
   same real GPU, on macOS) and never writes them. A family with no committed goldens **fails with
   "golden ... missing ... bake it"**.
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
same-named files that are two implementations measured against one set of references. The Linux pair works the
same way (`golden-deltas-vulkan-native` contains `golden-deltas.vulkan.txt`) and so does the macOS pair
(`golden-deltas-metal-native` contains `golden-deltas.metal.txt`). **The macOS pair is the one where that trap
costs the most**, because `metal` is the fleet's cross-backend reference family: two same-named files there are
two implementations measured against the references every other family is read against, so mixing them up
misattributes a fleet event to a leg or the reverse.

Five artifacts carry no pixels at all and upload on `always()`, because each of them is read off a run that
PASSED:

- **`vulkan-validation-strict-vulkan-native`** and **`vulkan-validation-sync`** are the two validation tiers'
  test output, teed to a file as the suite runs. Warning and performance severities never fail a run, so a
  failure-only upload would discard the entire non-fatal half of what the layer produces, and a hazard the
  layer reports as a warning is still the thing the tier was installed to find. The interleaving with test
  names is the diagnostic: a validation message is only actionable next to the test that provoked it. The sync
  artifact keeps uploading on `always()` for a second reason too, now that the sync job's own scan step fails
  the job on an error-severity line: the gate goes red and the log that explains it survives the red.
  **Two independent producers put validation text in these logs, and only one of them is the engine.** The
  engine-formatted lines (`Vulkan validation [<Severity>] <VUID>: <text>`, written by `VulkanValidationPump`)
  appear only when the run armed a rung, because the test host configures a console sink for the Vulkan
  backend's log categories exactly then and leaves the ambient logger unconfigured otherwise
  ([#565](https://github.com/APKiwiOrg/KhaozEngine/issues/565)). The Khronos layer's own output
  (`Validation Error:` / `Validation Warning:`) is written by the layer on its own account and is independent of
  that sink. One line under the category `VulkanValidationLogHost` is written per armed run, so a log with no
  validation messages in it can be read as a clean sweep rather than as a lost producer. Both armed test steps
  run at `--logger "console;verbosity=detailed"`, which is what forwards the test host's stdout at all and what
  supplies the `Passed <TestName>` lines the interleaving is measured against.
- **`vulkan-device-limits-<leg>`** is the `vulkaninfo` dump, which runs without `--summary` on purpose so the
  whole `VkPhysicalDeviceLimits` block lands rather than the driver and API version alone. No Vulkan device
  limit had ever been observable anywhere in this repo before that, so the native backend's descriptor model
  and allocator rest on spec minimums until the observed lavapipe values are recorded
  ([#541](https://github.com/APKiwiOrg/KhaozEngine/issues/541)). The artifact exists because those numbers have
  to be quotable verbatim into a design doc, and a job log is thousands of lines long and expires sooner.
- **`metal-validation-<tier>-<leg>`** is the Metal leg's suite output, teed the same way and
  uploaded for the same reason: the debug layer reports on stderr beside the test names, and the interleaving is
  the diagnostic. The tier in the name is read off the expression that arms it, so it cannot lie: `debug-layer`
  is every unattended run of the native leg, `shader-validation` is a deep dispatch (where
  `MTL_SHADER_VALIDATION=1` adds in-shader bounds checking on top) or the incumbent leg under #617's control
  input, and `capture` is a `tier=capture` dispatch, where the device is a `CaptureMTLDevice` and the layer is
  therefore NOT the live instrument. That last case is why the name has four cases rather than two: while the
  capture rode every unattended trigger, this artifact was called `debug-layer` on runs where the layer held
  nothing. The incumbent leg uploads this only when the control armed it. **Neither tier is a synchronisation
  validator**, and Metal has none at all, which is the one place this matrix is weaker than the Vulkan side:
  a missing read-after-write hazard across encoders has no detector anywhere in this net.
  On EVERY run of that leg this artifact also carries the engine's OWN Metal lines, which no earlier run of it
  could contain ([#617](https://github.com/APKiwiOrg/KhaozEngine/issues/617)): the armed tier and the device's
  Objective-C class from `MetalGpuDevice`, and every failed command buffer from `MetalDeviceLossLatch`. A run
  with the sink configured ANNOUNCES ITSELF, one line under the category `MetalValidationLogHost`, and that
  announcement is what makes the artifact readable on its own. The line present with nothing after it is a
  clean run. NO Metal lines at all is a lost producer rather than a clean run, which is the state #617's
  artifact was in and could not report.
- **`device-evidence-<leg>`** is what the boot's GPU actually was, taken on BOTH macOS legs after the test step:
  `system_profiler SPDisplaysDataType SPHardwareDataType` (the displays view alone is a zero-byte file on a
  headless runner, and the hardware one plus `sysctl hw.model` is what says which machine in the pool the boot
  landed on), the runner image version and OS, the engine's own device facts (the
  Metal probe's four reads, the reported capability set and the adapter description, from a filtered `--no-build`
  run of three device-reading rows at detailed verbosity, which is what forwards a PASSING row's output), the
  last 200 lines of the suite log, and `device-loss.txt`.
  **That last file is the named-line detector**, echoed into the job log as well so the reading needs no
  download. It answers the two questions every #614 boot had to be re-read by hand to answer. Was the
  instrument armed: the `MetalValidationLogHost` announcement proves the test host configured a Metal sink at
  all, and the device-class tally proves which Objective-C class actually held the device, which is a different
  question from which variables were set. And did the device go: `DEVICE-LOSS-OBSERVED` with the matching lines
  when `MetalDeviceLossLatch` fired, `DEVICE-LOSS: none` when it did not, plus buckets for
  `MetalCompletionHandler` errors, `MetalTimeline` dead-device drains and whatever the Metal runtime itself
  said. It is **read-only diagnosis and never a gate** (the leg's own test failure is already the gate), it
  names its known-benign exclusions rather than filtering them out, and it reports a missing suite log as
  UNREADABLE rather than as a clean run. It is the [#614](https://github.com/APKiwiOrg/KhaozEngine/issues/614)
  instrument: that leg's red runs name a test and say nothing about the machine, so the reading wanted is what
  DIFFERS between a red boot and a green one. **That is the whole reason it is not `failure()`-gated**, and the
  incumbent leg carries it for the same reason: a failure-only capture produces a red boot's facts with no
  baseline anywhere to difference them against. The step is `continue-on-error` with `|| true` on every command,
  because a diagnostic that can redden the leg it is diagnosing would be a second flake on top of the one being
  chased.

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
- **One uniform buffer per pipeline.** Still the rule for every new render path, but the measured scope is
  narrower than this bullet used to state it. What mis-binds on the Veldrid Metal backend is a STAGE that
  references fewer buffers than the declared layout array puts before them, which is the pattern that makes
  Veldrid's per-kind declaration count and SPIRV-Cross's emission disagree: a fragment function reading set 1
  alone is emitted at `buffer(0)` and written at `buffer(1)`, so it reads a slot nothing wrote. Measured 2026-08
  on an M2 Max, two of the three multi-uniform-buffer shapes bind CORRECTLY on the incumbent today and only that
  third one fails, and the engine's own native Metal backend binds all three, because it binds at the index the
  emission chose rather than at a counted one. The rule stands while the Veldrid Metal leg ships: fold extra
  per-material data into the frame UBO (the splat pipeline appends its params after the light arrays in one
  combined UBO). See `SplatVert` / `SplatFrag`, and `docs/DEPENDENCY-SEAMS.md`'s "ONE uniform buffer per
  pipeline" section for the mechanism and the exact scope.
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
