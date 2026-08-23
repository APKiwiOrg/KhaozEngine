# Cross-platform desktop GPU

KhaozEngine's custom stack (Render2D / Render3D) runs on the engine's own graphics backends. Each desktop OS
gets one, and the GPU golden-snapshot net verifies rendering on each one through a CI matrix. The Veldrid
incumbent that used to sit behind all three was deleted in `18.0.0`
([#687](https://github.com/APKiwiOrg/KhaozEngine/issues/687)), and the shader toolchain it left behind was
swapped out in the same release ([#691](https://github.com/APKiwiOrg/KhaozEngine/issues/691)). `Veldrid.SPIRV`
is gone. The toolchain is `Silk.NET.Shaderc` (glslang, the GLSL to SPIR-V front end) plus
`Silk.NET.SPIRV.Cross` (the MSL and HLSL back end), still confined to `KhaozEngine.Gpu` and still the only
place in the engine that compiles a shader.

## Platform → backend (desktop scope)

| OS      | Backend the OS probe picks            | `GpuBackendKind`   | golden file suffix       | retired member (18.0.0)                            | software rasterizer (CI) |
| ------- | ------------------------------------- | ------------------ | ------------------------ | -------------------------------------------------- | ------------------------ |
| macOS   | Metal (`KhaozEngine.Gpu.Metal`)       | `MetalNative`      | `.metal-native.txt`      | `Metal`, named by `KE_GRAPHICS_BACKEND=metal`      | none, a real Apple GPU   |
| Windows | Direct3D 11 (`KhaozEngine.Gpu.D3D11`) | `Direct3D11Native` | `.direct3d11-native.txt` | `Direct3D11`, named by `KE_GRAPHICS_BACKEND=d3d11` | WARP (auto fallback)     |
| Linux   | Vulkan (`KhaozEngine.Gpu.Vulkan`)     | `VulkanNative`     | `.vulkan-native.txt`     | `Vulkan`, named by `KE_GRAPHICS_BACKEND=vulkan`    | Mesa lavapipe            |

**There is ONE implementation per API since `18.0.0`.** Every row used to carry two, the engine's own and a
Veldrid one, and the columns swapped at `17.40.0` when the OS probe started naming the engine's. The
right-hand column is what is LEFT of the incumbent: four `GpuBackendKind` members (`Metal`, `Vulkan`,
`Direct3D11`, `OpenGL`) kept because the enum is append-only and a consuming game has persisted them as a
player's saved choice. They are RETIRED, not repointed: nothing builds a device for them, an env token naming
one runs that API's native backend and warns, and a stored preference for one reports `FallbackAfterFailure`
so the game clears it. See "Retired backend members" below.

**The three native backends stopped being opt-in in `18.0.0` too.** They ship in the `Game2D` and `Game3D`
umbrellas now, all three of them whatever the build machine is, because `KhaozEngine.Gpu` builds no device of
its own any more: an umbrella carrying `Gpu` without a backend would carry a stack that cannot create a device.
A foreign backend is inert (platform-guarded, its interop behind `NoInlining` bodies the JIT never compiles off
its platform), and only the running platform's is REGISTERED.

**Each backend has OWNED its own golden family since `17.41.0`**: it was a guest in the incumbent's family
until then, which made its CI leg a verification of the incumbent's committed references on the incumbent's own
rasterizer, and row 2 of the Veldrid removal
([#683](https://github.com/APKiwiOrg/KhaozEngine/issues/683)) promoted all three ahead of the delete. The three
families were seeded as byte-identical COPIES of the incumbent ones rather than baked, so the guest-era
agreement between two implementations survives as committed bytes. The macOS row is the one whose rasterizer
column reads "none": those are the only families baked on real hardware, and the only legs in this matrix where
a golden disagreement is about a GPU rather than about a software rasterizer.

Backend selection is centralized in `KhaozEngine.Gpu.GpuBackendSelector`:

- `Select()` reads the `KE_GRAPHICS_BACKEND` env override (`metal-native` / `vulkan-native` / `d3d11-native`,
  case-insensitive, with `mtl-native`, `vk-native` and `direct3d11-native` as aliases), otherwise probes the OS
  (macOS → `MetalNative`, Windows → `Direct3D11Native`, Linux/other → `VulkanNative`). The four retired tokens
  (`metal` / `vulkan` / `d3d11` / `direct3d11` / `gl` / `opengl`) still PARSE, and run the native successor.
  **The OS probe names the three ENGINE-OWNED backends since 17.40.0.** That flip was taken by decision on
  2026-08-22, ahead of the field-evidence gates each rollout still had open, and the dated addendum in each
  design's rollout record says which of them remain open as issues. There is exactly ONE default per platform
  since `18.0.0`, which is what makes the fallback guard in `GpuDeviceContext` a complete statement: the second
  map that used to sit beside the probe, `GpuBackendSelector.IncumbentFor(os)`, was deleted with the backend it
  named, so a failed creation, a stored preference for a retired member and an unrecognized override all land
  on `ProbeOS`.
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
- `CreateHeadless` builds the matching offscreen device through the platform's registered provider. No window,
  so the golden tests need no SDL2. It takes no preference, and it falls back to the platform default when a
  provider-backed default fails (see below).

## Retired backend members (18.0.0)

`Metal`, `Vulkan`, `Direct3D11` and `OpenGL` are retired tokens. `GpuBackendSelector.IsRetired` is the
predicate, `NativeReplacementFor(kind, os)` is the map (`Metal` → `MetalNative`, `Vulkan` → `VulkanNative`,
`Direct3D11` → `Direct3D11Native`, and `OpenGL` → the platform default, because the engine never had an OpenGL
implementation and is not gaining one). They are NOT repointed at the native implementations, which is the
tidy-looking move the removal design rules out by name: repointing would silently move every Windows tester's
stored `Direct3D11` onto a different implementation with no rebuild signal and no player notice.

Three paths reach a retired member and each answers differently, on purpose:

- **`KE_GRAPHICS_BACKEND=metal`** (or `vulkan`, `d3d11`, `direct3d11`, `gl`, `opengl`) runs the native
  successor and WARNs, recording the retired member on `RequestedBackend`. Refusing the boot would turn every
  soak script, CI leg and shell alias in the fleet that still names one into a crash, for a variable whose
  whole purpose is to get a run going.
- **A stored `UserPreference`** for a retired member self-heals to that API's own native backend, the same
  `NativeReplacementFor` map the token takes, and reports `FallbackAfterFailure` with the retired member on
  `RequestedBackend`. That is the signal a consuming game already acts on, and acting on it clears the setting,
  which is the only thing that gets the player off a dead choice permanently. It is rejected AHEAD of
  `GpuBackendProviders.Require`, because `Require` throws by contract and a saved settings file must never be
  able to make the engine throw at boot. Where the replacement has no package on this OS (a stored
  `Direct3D11` on a Mac), creation falls back to the platform default and warns rather than throwing.
- **Naming one in code**, through `GpuDeviceContext.CreateForWindow` / `CreateHeadless` with an explicit
  selection, throws `GpuBackendRetiredException`, naming the retirement and the native to use. A caller that
  named one in source is a caller that has to be edited. `GpuBackendProviders.Require` throws the same
  exception for the same reason, so a consumer reaching the registry directly is told about the retirement
  rather than about a package that no longer exists.

`GpuBackendSelector.SupportedBackends()` never offers a retired member, so a settings dropdown built from it
cannot hand a player a new dead preference. `IsBackendSupported` answers false for all four.

### When the chosen backend cannot start (17.23.0)

Letting a player pick a backend means letting them pick one their machine cannot run, and the setting that
caused it lives inside the client that then will not start. Two mechanisms, and both are needed:

- **`GpuBackendSelector.IsBackendSupported` / `SupportedBackends()`** are a FUNCTIONAL probe, not a guess: the
  question is routed to the backend's own registered provider, which loads its library, creates an instance,
  enumerates physical devices, and for Vulkan checks the required surface extensions. A game's settings UI must
  offer only what `SupportedBackends()` returns. Results are cached for the process lifetime. No RETIRED member
  is ever reported supported, `OpenGL` included, so a dropdown built from this list cannot hand a player a new
  dead preference.
- **Creation fallback.** A probe pass is necessary but not sufficient: a broken or partial driver can report
  support and still fail at device creation. So `GpuDeviceContext.CreateForWindow` also catches a failed
  creation and falls back to the platform's own default (`GpuBackendSelector.ProbeOS`), WARNing with the
  requested backend, the failure, and what it fell back to. The retry reuses the same `GpuWindowHandle` (a
  readonly struct of native pointers, no device state), so no second window is created. There is exactly one
  default per platform since `18.0.0`, so "nothing to fall back TO when the request already IS the default" is
  the only case the fallback skips.
- **A STORED PREFERENCE with no registered provider** takes that same fallback, and it is the only provenance
  that may. A settings file outlives the build that wrote it and the machine it was written on (a profile synced
  across machines, or a game that dropped its explicit registrations after the player had picked a backend), and
  refusing the boot leaves the setting that caused it unreachable from inside the game.
  `GpuBackendSelection.CameFromStoredPreference` draws that line and covers a preference already redirected off
  a retired member. A DEFAULT with no provider throws `GpuBackendProviderMissingException`, and so does a
  backend pinned in `KE_GRAPHICS_BACKEND`, which is the half that stops a soak session measuring one backend and
  filing the number under another. An ordinary windowed game meets none of it: `AppWindow` calls
  `GpuBackends.RegisterResolvedIfUnregistered(preference)` at boot, registering both the kind that is about to
  be asked for and this platform's own as the fallback target, and `Render2DSnapshot` / `Render3DSnapshot` do
  the same for a headless host.
- **`CreateHeadless()` falls back as well since 17.40.0**, in both of the ways a request can fail: the
  unregistered provider above, and a REGISTERED provider that refuses this machine. A pinned backend still
  propagates everything there, which is what keeps each of the three legs below capturing goldens under the name
  it pinned.

The fallback is REPORTED, never repaired: `Source` becomes `FallbackAfterFailure`, `Backend` is what actually
runs, and `RequestedBackend` is what failed. **A game storing a backend preference must clear it on seeing that
source**, or the player retries the same broken choice every launch. The engine cannot, since writing a setting
is file IO and `KhaozEngine.Gpu` does none. `GpuBackendSource.DefaultProviderMissing` (ordinal 5), which
17.40.0 appended for a DEFAULT that fell back to the incumbent, is RETIRED at 18.0.0 with NO PRODUCER: there is
no incumbent to create instead, so that case throws. The member keeps its number because the enum is
append-only and a 17.40.0 capture that recorded a 5 still has to read back as what it meant. And when the
fallback fails too there is no device at all, which `GpuNoUsableBackendException` reports naming both backends
and both reasons.

A RETIRED member never reaches any of this. It is answered before the provider registry: as an env token by
the redirect, as a stored preference by the self-heal, and in code by `GpuBackendRetiredException`.

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

**THREE FAMILIES SINCE `18.0.0`.** The token is a mapping rather than the enum name, and it has been through
two moves. Until `17.41.0` it mapped seven kinds onto four families: `GpuBackendKind.Direct3D11Native` resolved
to `direct3d11`, `VulkanNative` to `vulkan` and `MetalNative` to `metal`, so each native backend was held to
the incumbent's already-committed references, unmodified, on the same rasterizer at the same tolerance. That
was the strongest free proof a native port had. Row 2 of the Veldrid removal
([#683](https://github.com/APKiwiOrg/KhaozEngine/issues/683)) ended it, because the incumbents that owned those
families were being deleted and a family whose owner is gone is a set of references nothing may ever re-bake.
Row 4 then deleted the four incumbent families outright with the backend
([#687](https://github.com/APKiwiOrg/KhaozEngine/issues/687)). What is left is one family per live backend,
named after it and spelled the way `KE_GRAPHICS_BACKEND` accepts it, hyphen included: `metal-native`,
`vulkan-native`, `direct3d11-native`. A RETIRED member has no family, and cannot acquire one, because it
resolves to its native successor before any golden path is built.

**What was lost is what happens NEXT, not what is committed.** The three native families were seeded as
byte-identical COPIES of the incumbent ones rather than baked, and `GoldenFamilyCopyGoldenTests` asserted the
copy cell for cell over all 120 grids until row 4 deleted it with the families it read, so every guest-era
green run is still exactly what the committed bytes record. From `17.41.0` on, though, a native family is a
reference only its own producer has ever agreed with, and its value is regression detection rather than
correctness evidence.

`KE_UPDATE_GOLDENS` still REFUSES to write when the running backend does not OWN its family, unless
`KE_GOLDEN_FAMILY_OVERRIDE=1` says the shared family is being moved on purpose. **No live backend trips that
refusal today**, because no backend is a guest any more. What it guards is the RULE, for the next append that
decides to share rather than own: that decision was taken three times in a row and the whole failure mode is
that a shared bake is undetectable after the fact, since the file it writes is exactly the file it would then
have compared against.

**The two Metal paths that used to move together are one path now.** They were held to the same grids until
`17.41.0`, so a behavioural change to either was a change to the other's reference, and then to two families
holding identical bytes. `17.39.0` is the worked example from that era: `GpuRasterizerState.DepthClipEnabled`
was read by Direct3D 11 and Vulkan and by neither Metal path, both of which derived the clip mode from the
depth test instead ([#598](https://github.com/APKiwiOrg/KhaozEngine/issues/598)), and the repair shipped as the
vendored Veldrid fork's `4.9.104` plus the matching `KhaozEngine.Gpu.Metal` change in one release. Fixing only
the native one would have reddened the native leg against grids the incumbent baked.
`DepthClipModeGpuTests` is the row that holds the contract, and it is backend-agnostic on purpose so every leg
asserts it rather than one.

The Metal goldens (`scene2d.metal-native.txt`, `scene3d.metal-native.txt`, baked on a real Apple GPU), the
Direct3D11 goldens (`scene2d.direct3d11-native.txt`, `scene3d.direct3d11-native.txt`, baked on WARP) and the
Vulkan goldens (`scene2d.vulkan-native.txt`, `scene3d.vulkan-native.txt`, baked on lavapipe) are all committed
and verified on every macOS / Windows / Linux run respectively.

The 2D golden loads a libre font bundled in the test project (`KhaozEngine.Render.Tests/Assets/Roboto-Regular.ttf`,
Apache-2.0) rather than an OS system-font path, so its glyph input is identical on every runner.

`KhaozEngine.Render.Tests/Gpu/goldens/*.txt` is meant to be pinned `text eol=lf` in `.gitattributes`, though the
pin still names the pre-split `KhaozEngine.Tests/Gpu/goldens/*.txt` path today, tracked as
[#213](https://github.com/APKiwiOrg/KhaozEngine/issues/213). The goldens are machine-generated
LF text, and a Windows checkout with `autocrlf` on would otherwise convert them to CRLF, breaking the byte-identity
contract between the committed file and `GoldenGrid.Serialize`'s LF output. This exact failure shipped and was
fixed in 10.18.1 (Metal and Vulkan legs and every actual golden compare were green, only the endings differed).

## CI matrix (`.github/workflows/cross-platform-gpu.yml`)

**THREE LEGS SINCE `18.0.0`, not six.** The Veldrid incumbent backend was deleted
([#687](https://github.com/APKiwiOrg/KhaozEngine/issues/687)) and its three legs went with it
([#689](https://github.com/APKiwiOrg/KhaozEngine/issues/689),
[#540](https://github.com/APKiwiOrg/KhaozEngine/issues/540)), along with the `libdl` / `libvulkan` symlink step
that only Veldrid's Vulkan binding needed.

The suite each leg runs is split by trigger, from measured hosted-runner cost
(`KE_GPU_TESTS=1`, `fail-fast: false`).

**The tiers were re-decided at the delete, against measured FULL-suite durations, and they did not move.** Run
`32579723071` was a full-matrix `workflow_dispatch`, so every leg ran the whole suite:

| leg | runner | full suite |
| --- | --- | --- |
| `metal-native` | `macos-26` | 4m31s |
| `metal` (deleted) | `macos-26` | 3m56s |
| `direct3d11-native` | `windows-latest` | 25m43s |
| `direct3d11` (deleted) | `windows-latest` | 37m41s |
| `vulkan-native` | `ubuntu-latest` | 26m36s (serialized collections, strict validation) |
| `vulkan` (deleted) | `ubuntu-latest` | 21m45s |
| `vulkan-native` sync job | `ubuntu-latest` | 4m36s |

`metal-native` keeps `always`: the whole suite on a real GPU costs four and a half minutes, cheaper than the
other legs' golden subsets, and it is the strongest net in the program. The two software legs keep
`scheduled`: their full suites are about 26 minutes each, six times the Metal leg, and almost all of that is
pipeline creation on a software rasterizer rather than engine work. Promoting either to `always` would take the
push gate from roughly 8 minutes to roughly 26 for the one trigger whose job is fast feedback, and what it
would newly catch is a behavioural regression visible ONLY under WARP or lavapipe, the rarest failure this
matrix has produced. The delete also shortened the matrix's own wall clock (slowest leg 37m41s → 26m36s) and
dropped about 63 minutes of runner time from a full dispatch.

- **GitHub-hosted Metal NATIVE leg (`macos-26`)**: the engine's own `KhaozEngine.Gpu.Metal` backend
  (`KE_GRAPHICS_BACKEND=metal-native`) on the WHOLE test suite (`--filter "Category!=LiveSocket"`) on every
  trigger - every `[GpuFact]` test (golden and behavioral) plus the headless suite, matching `ci.yml`'s own
  `Category!=LiveSocket` exclusion (LiveSocket tests need a live network peer, not available here). Metal ran
  on the dev Mac until [#552](https://github.com/APKiwiOrg/KhaozEngine/issues/552) moved it to a hosted runner,
  and the runner is pinned to `macos-26` by number rather than to `macos-latest` so an image promotion cannot
  move the GPU under a golden gate. It OWNS the `metal-native` golden family since `17.41.0`, exactly as the
  other two legs own theirs, so it verifies the committed `.metal-native.txt` grids on the same real GPU and
  bakes them on a bake dispatch. **This is the strongest regression net in the program**: the other two legs
  run golden-only on push, on software rasterizers, and this one runs everything on a real GPU every time.
  Three things were this leg's rather than the incumbent Metal leg's and are simply this leg's now. It sets
  `KE_METAL_REQUIRED=1`, for the reason the Vulkan leg sets its own equivalent below. It arms `MTL_DEBUG_LAYER=1` on every run and `MTL_SHADER_VALIDATION=1` on the
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
  | `MTL_DEBUG_LAYER=1 MTL_SHADER_VALIDATION=1` | `The device class is MTLDebugDevice`, no warning. The debug layer wins the class when both are armed |
  | `MTL_SHADER_VALIDATION=1` alone | `The device class is MTLGPUDebugDevice`, no warning. Hosted `macos-26` answers `MTLLegacySVDevice` for the same environment, see below |
  | `MTL_DEBUG_LAYER=1 MTL_CAPTURE_ENABLED=1` | `The device class is CaptureMTLDevice`, plus the WARN that the run `is probably NOT validated` |

  **Shader validation alone has TWO class spellings, and that is what
  [#628](https://github.com/APKiwiOrg/KhaozEngine/issues/628) turned out to be about.** That issue was filed on
  run `31874140088`, where hosted `macos-26` under `MTL_SHADER_VALIDATION=1` alone reported `MTLLegacySVDevice`
  and the engine emitted 99 copies of a warning calling a validated run unvalidated. The same launch environment
  on the M2 Max above reports `MTLGPUDebugDevice`. Both are shader-validation wrappers, so the disambiguation
  asks whether ANY validation wrapper is holding the device rather than whether one named class came back:
  pinning it to either spelling would put the false warning back on the other machine. Only `CaptureMTLDevice`
  and the driver's own class read as unvalidated now, and the WARN names whichever of the two variables was
  actually armed.

  `MTLDebugDevice` is the class that performs the API validation, so the arrangement this replaces (capture on
  every trigger except the deep dispatch) left tier one INERT on every push, pull request and cron this leg has
  ever run. Every unattended trigger takes the debug-device shape now, and the capture's 2.5-second cost
  (`KhaozEngine.Render.Tests` at `Category!=LiveSocket` under `KE_GRAPHICS_BACKEND=metal-native` with
  `MTL_DEBUG_LAYER=1`, on an M2 Max: 22.4s without it against 25.0s with it) stopped being the deciding
  argument the moment the real cost turned out to be the instrument rather than the clock. What that trade
  costs, stated rather than glossed: `MetalFrameCaptureTests`' positive arm, the one that starts a capture from
  the native queue pointer and asserts the bundle, is attended-only coverage now, and its negative arm is what
  every unattended trigger runs.
  **Two guards keep that exclusion true, because the re-tier above is only a set of comments an editor is free
  to not read.** The first is a step at the top of the matrix job, before the checkout, on the macOS legs. It
  reads the same job variables the test step arms and fails the job when an UNATTENDED trigger (push, pull
  request, cron) would run with both `MTL_DEBUG_LAYER` and `MTL_CAPTURE_ENABLED` set, and it asserts the inverse
  as well, that the `metal-native` leg really does carry the debug layer with the capture empty there, because
  "not both" is also satisfied by arming neither. It is scoped to the unattended triggers on purpose: the
  `capture` tier arms both by design, and a universal assertion would red the one tier that chose the pair.
  The second guard reads the DEVICE rather than the workflow. `MetalCaptureDisplacementTripwire` in
  `KhaozEngine.Render.Tests` promotes the engine's "armed and nothing is validating" warning to a test
  failure on an unattended CI run, so a leg cannot report six thousand green rows under an instrument that was
  displaced. It asks the engine's own `MetalValidation.ClassifyDevice`, so there is one predicate rather than
  two: it passes `MTLDebugDevice`, `MTLLegacySVDevice` and `MTLGPUDebugDevice` (all three validate, the last two
  being the same shader-validation wrapper under the two spellings measured across machines) and fails
  `CaptureMTLDevice` and the driver's own class, naming the capture as the displacement when the capture is
  armed. Off CI, and on an attended dispatch, it stands down and the warning is the whole answer. The two layers catch different things:
  the workflow step catches a bad tier edit at authoring time on the first push, and the tripwire catches a
  displacement this repository did not cause, such as a runner image that starts injecting the variable.
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
  **The control that issue asks for used to be a dispatch input**, `incumbentShaderValidation`, which armed
  `MTL_SHADER_VALIDATION=1` on the INCUMBENT Metal leg and nothing else. That leg was deleted in `18.0.0`, and
  the input with it, so the control is now a `tier: push` dispatch of this leg against a `tier: deep` one. The
  green incumbent leg on run `31581572414` was never the control it looked like either way, because it armed
  neither Metal variable.
  **The same runner drops `setDepthClipMode` Clamp, and only under the API validation layer**
  ([#682](https://github.com/APKiwiOrg/KhaozEngine/issues/682)). This is #617's shape rather than #614's:
  deterministic, and it flips cleanly with the instrument.
  `DepthClipModeGpuTests.DepthClipDisabled_KeepsTheHalfInFrontOfTheNearPlane` fails on this leg with the debug
  device armed and passes on the same image and the same device with it off (run `32559601885`, 6595 passed, 0
  failed), while real Apple silicon passes it WITH the layer armed. The clamp is derived and sent correctly on
  the failing leg itself, and forking the draw to the Veldrid incumbent's own four-argument selector did not
  move it (run `32559296816`, taken before that backend was deleted), so the engine is not the variable. The layer stays armed here, because it is the only
  Metal API-validation gate the engine has, so that ONE row is skipped by name on a virtualised adapter while
  the layer holds the device (`GpuFactAttribute.RequiresRealGpuUnderMetalApiValidation`, which reads
  `MTL_CAPTURE_ENABLED` too, since a capture displaces the layer and leaves nothing validating). The clip row
  and the derivation row stay unconditional, so a real regression in either direction still reddens this leg.
  **Neither Metal tier sets an error MODE, and the default is assert.** A validation error there aborts the test
  host rather than failing a row, so the rows that provoke the layer on purpose stand down in-process instead
  ([#591](https://github.com/APKiwiOrg/KhaozEngine/issues/591)). `MTL_DEBUG_LAYER_ERROR_MODE=nslog` stops the
  abort and reports nothing at all to the captured stream, so it is rejected: a tier that can neither fail nor
  testify is worse than none.
  **A failed command buffer is NOT a failed row**, which is the blindness #617 ran into.
  `MetalDeviceLossLatch` logs the driver's own description and flips the device's liveness, and nothing in it
  fails a test, so a leg can go red for a pile of unrelated-looking golden reasons with the real fault sitting
  in a log line. Until that issue, that line went nowhere at all: the test host configures a sink for the
  `Metal` log categories only when a Metal rung is armed, and the leg forwards the host's stdout at detailed
  verbosity on exactly the runs that arm one, which is every run of this leg. Those are the two halves it takes
  for the line to reach the artifact at all, and they read the same predicate, so the artifact cannot exist
  with the engine's half of it missing.
- **GitHub-hosted D3D11 native leg (2x billing)**: the engine's own `KhaozEngine.Gpu.D3D11` backend
  (`KE_GRAPHICS_BACKEND=direct3d11-native`), golden tests only (`FullyQualifiedName~Golden`) on
  `push`/`pull_request` and the WHOLE suite on the weekly `schedule` (Sunday 18:00 UTC) and on
  `workflow_dispatch`. The tier was chosen on a measurement of the incumbent leg, where the full suite on
  hosted Windows was 17m14s against the golden-only 7m44s, about +19 billed 2x minutes per run, and it survived
  the `18.0.0` re-decision on feedback speed alone at 25m43s for this leg's own full suite. It OWNS the
  `direct3d11-native` golden family since `17.41.0`: it verifies the committed `.direct3d11-native.txt` grids
  on WARP and bakes them on a bake dispatch. It pins `KE_D3D11_ADAPTER=warp` instead of inheriting the implicit
  fallback, so a runner image that grows a paravirtual adapter cannot quietly change the rasterizer under the
  family. That variable is read only by the native backend's adapter selection. It is also the leg that runs
  the device-free FXC shader validation step, which is the only thing in this repo that puts a real HLSL
  compiler over the shipped programs.
- **GitHub-hosted Vulkan native leg (1x)**: the engine's own `KhaozEngine.Gpu.Vulkan` backend
  (`KE_GRAPHICS_BACKEND=vulkan-native`), golden tests only on `push`/`pull_request` and the WHOLE suite on the
  weekly `schedule` and on `workflow_dispatch`, with the full-suite runs serializing xUnit test collections
  (`-- xUnit.ParallelizeTestCollections=false`, one live device at a time). It OWNS the `vulkan-native` golden
  family since `17.41.0`, verifying the committed `.vulkan-native.txt` grids on lavapipe and baking them on a
  bake dispatch, and it pins `KE_VULKAN_DEVICE=llvmpipe` for the reason the Windows leg pins its adapter.
  **The lavapipe history belongs to this leg now**, inherited from the incumbent Vulkan leg that carried it
  until `18.0.0`. Chasing that leg's full-suite crashes (reproduced 4/4 in an amd64 container running the exact
  CI Mesa version, 25.2.8) surfaced four real engine defects, all fixed: concurrent device creation racing the
  Vulkan loader's dispatch setup (fixed by serializing device create/dispose process-wide in
  `KhaozEngine.Gpu.GpuDeviceContext`), mid-life GPU resource disposal racing queued async work (fixed by
  draining the device via `IGpuDevice.WaitForIdle` before every mid-life disposal, the drain rule is documented
  in `docs/USING-KHAOZENGINE.md`), and a teardown-order pair where a resource wrapper outliving its device
  drained or destroyed against the destroyed device (fixed by a shared liveness latch). The full PARALLEL suite
  on lavapipe still exhibits residual driver-side instability with delayed-corruption symptoms (a CoreCLR
  fatal, deliberately not chased), which is why the full suite runs serialized and why it measured 26m36s in
  run `32579723071`. Golden-only runs keep their months-green parallel configuration.
  Two more things are its own. It
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
turned on with an environment variable. The install is scoped to the runs that arm a tier rather than to the
Linux leg as a whole, so a golden-subset push does not add an apt package it has no use for, and the layer
manifest is then CHECKED: a missing layer only WARNs and creates the device anyway, which would leave a
validation gate passing while validating nothing.

**A dispatch can run the whole matrix with the GPU shader caches OFF** (`disableGpuDiskCache`, default
false). The three backends' DISK caches are one mechanism, `KhaozEngine.Gpu/Internal/GpuDiskCache`, reached through
`KE_METAL_MSL_CACHE`, `KE_D3D11_SHADER_CACHE` and `KE_VULKAN_PIPELINE_CACHE`. Each takes a directory path
verbatim, treats blank as the default location under local app data, and recognises five disable words
(`off`, `0`, `false`, `no`, `none`, trimmed and case-insensitive), which is why anything else, a typo included,
is read as a directory name and caches happily under it. The input sets all three to `off` on every leg and on
the sync job, so a cacheless run is uniform and nothing in it is left comparing a cacheless leg against a
cached one. Empty is the shipped default rather than a third state, so every push, the cron and every dispatch
that leaves the box unticked behave exactly as before the input existed.
Since [#640](https://github.com/APKiwiOrg/KhaozEngine/issues/640) the same value also sets `KE_SPIRV_CACHE`,
which is not a disk cache at all: it is the process-wide memo in front of glslang, so it holds nothing across
boots and cannot serve the stale entry #614 is looking for. It is on the switch because it CAN serve one bad
emission to every test in a process, which a cacheless A/B has to be able to rule out, and because a run
described as cacheless that still memoized would be describing itself falsely.
It exists for [#614](https://github.com/APKiwiOrg/KhaozEngine/issues/614), where the metal-native leg fails
roughly one varying GPU test per boot on the hosted paravirtual adapter, bit-identically wrong when it is
wrong, while the same commit passes on real Metal locally. A warm
cache entry read back on an unhealthy boot would be a stable wrong answer, and an adapter fault would not have
to be, so running the same commit cacheless is what tells those apart. Measured end to end on real Metal: cold
with the cache on writes 32 `.kemsl` entries and costs about 3 s of emission, warm costs 140 ms and writes
nothing, and warm with `off` set costs the full 3 s again and does not touch one of the 32 entries, which is
what "neither read nor written" looks like from outside the process.

Historically the test step filtered every leg to `FullyQualifiedName~Golden`, so any `[GpuFact]` class
without "Golden" in its name never ran on ANY backend (`Scene3DTextureUnloadTests`, `WaterQueueTests`,
`RenderServiceTests`, and dozens of other classes were never exercised on Metal/D3D11/Vulkan). Every GPU
test now runs on `metal-native` (every trigger), `direct3d11-native` and `vulkan-native` (weekly and
dispatch) regardless of what it is called. The name is not a CI contract, and neither is the directory: `[GpuFact]` classes live across
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

Two further declarations gate on the ADAPTER rather than on a capability.
`[GpuFact(RequiresRealGpu = true)]` skips on a virtualised adapter, for a row asserting that an effect is
visible at all rather than comparing a golden. `[GpuFact(RequiresRealGpuUnderMetalApiValidation = true)]`
skips only where a virtualised adapter and an armed Metal API validation layer MEET, which is the pairing
[#682](https://github.com/APKiwiOrg/KhaozEngine/issues/682) measured, so the row keeps running on real Metal
under the same layer and on the same adapter without it. Both name the adapter in the skip reason, and both
RUN when no device could be created, so a broken device stays an error.

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
| `push` / `pull_request` on main  | **verify**: `metal-native` runs the full suite. `direct3d11-native` and `vulkan-native` run the golden tests only. The only validation tier that runs is Metal's debug layer, which the Metal leg arms on every trigger, and it arms it ALONE: `MTL_CAPTURE_ENABLED` would displace the `MTLDebugDevice` that does the validating ([#614](https://github.com/APKiwiOrg/KhaozEngine/issues/614)) |
| `schedule` (weekly, Sun 18:00 UTC) | **full sweep**: all three legs run the full suite (`vulkan-native` serialized and under `strict` validation), plus the `sync` validation job. The Metal leg keeps its debug layer here, alone for the same #614 reason, and does NOT arm `MTL_SHADER_VALIDATION` ([#617](https://github.com/APKiwiOrg/KhaozEngine/issues/617)) |
| `workflow_dispatch` `bake=false` | same as `schedule` (all three legs full suite, `vulkan-native` serialized, plus the `sync` job), plus the one thing no other trigger can do: `tier` picks the Metal leg's shape, where `deep` (the default) adds `MTL_SHADER_VALIDATION=1`, `capture` adds `MTL_CAPTURE_ENABLED=1` instead, and `push` is the unattended debug-device shape. A `push` dispatch against a `deep` one is #617's control since `18.0.0`, the incumbent-leg input that used to be it having gone with that leg |
| `workflow_dispatch` `bake=true`  | **re-bake** (`KE_UPDATE_GOLDENS=1`) on all three legs, each writing its own family, uploaded as per-backend goldens. Every leg has been a bake leg since `17.41.0`, when the three native legs became owners. The `sync` job still does not run: it is a validation instrument over a subset rather than a producer of references, and the `vulkan-native` matrix leg bakes that family. The copy constraint that used to sit here died with row 4 of [#683](https://github.com/APKiwiOrg/KhaozEngine/issues/683): each leg's artifact is committed as it comes |

Software rasterizers on the runners (no real GPU):

- Linux Vulkan → Mesa **lavapipe** (`mesa-vulkan-drivers`). The lavapipe ICD manifest's name/path drifts across
  Ubuntu runner images (it is now `lvp_icd.json`, was `lvp_icd.x86_64.json`), so the workflow **discovers it at
  runtime** and points `VK_ICD_FILENAMES` + `VK_DRIVER_FILES` at it rather than hardcoding. A second step used
  to symlink `libdl.so` → `libdl.so.2` and `libvulkan.so` → `libvulkan.so.1`, because Veldrid 4.9.0's Vulkan
  binding P/Invoked the bare names that modern Ubuntu only ships versioned. Silk.NET resolves through its own
  native-context search and needs neither, so that step went with the Veldrid backend in `18.0.0`
  ([#540](https://github.com/APKiwiOrg/KhaozEngine/issues/540)).
  The leg additionally pins `KE_VULKAN_DEVICE=llvmpipe`, the device-level belt to the loader-level brace. The
  variable has exactly one reader, the native backend's physical-device selection.
- Windows D3D11 → **WARP** software adapter, which the runtime falls back to automatically when no hardware
  adapter is present. The leg does not ride that accident: it pins `KE_D3D11_ADAPTER=warp`, so the rasterizer
  under the Windows family is stated rather than inherited from the runner image.

Net result: **all three legs are blocking, none of them informational** - `metal-native` (macOS),
`direct3d11-native` (Windows/WARP) and `vulkan-native` (Linux/lavapipe). The RASTERIZERS are long validated,
with months of green runs behind them under the Veldrid incumbent that used to share each one. The three
BACKENDS block by design rather than by record: a native backend's CI leg is its continuous exercise, so it
gates from its first run, and their first recorded evidence is rollout gate 1 on
[#460](https://github.com/APKiwiOrg/KhaozEngine/issues/460),
[#529](https://github.com/APKiwiOrg/KhaozEngine/issues/529) and
[#566](https://github.com/APKiwiOrg/KhaozEngine/issues/566). The overall workflow is green only when all three
verify, and since `17.41.0` that holds on a `bake=true` dispatch too: no leg sits one out any more.

**There is no escape-hatch leg any more.** Each incumbent leg used to be deliberately uncoupled from its native
sibling's health, installing no validation layer, arming no Metal tier and setting neither `KE_VULKAN_REQUIRED`
nor `KE_METAL_REQUIRED`, because an escape hatch that goes red whenever the thing it escapes from goes red is
not one. #683 closed that question the other way: the incumbent is deleted, so a native backend going bad is
fixed rather than escaped from.

### Per-backend golden flow

1. **Push / PR = verify.** Each leg verifies the committed goldens of its own FAMILY (`.metal-native.txt`,
   `.direct3d11-native.txt`, `.vulkan-native.txt`). Since `17.41.0` every leg owns the family it verifies. A family with no committed goldens **fails with
   "golden ... missing ... bake it"**.
2. **Generate a new backend's goldens:** run the workflow manually with `bake = true`. Every leg renders
   with `KE_UPDATE_GOLDENS=1` and uploads artifacts named `goldens-<backend>`
   (`scene2d.<backend>.txt`, `scene3d.<backend>.txt`).
3. **Commit them:** download the artifacts, drop the files into `KhaozEngine.Render.Tests/Gpu/goldens/`, commit.
   Each leg's artifact is committed as it comes since row 4 of
   [#683](https://github.com/APKiwiOrg/KhaozEngine/issues/683) deleted the incumbent families and the copy
   invariant with them.
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
broken. The two `<backend>` slots are the same token since `17.41.0`: the ARTIFACT is named for the leg and the
FILE inside it is named for the golden family the leg verified, and every leg now owns the family named after
it, so `golden-deltas-direct3d11-native` contains `golden-deltas.direct3d11-native.txt`. **THAT WAS NOT TRUE
BEFORE, and old artifacts still carry the old shape**: while the native backends were guests, a native leg's
artifact carried the SHARED family's filename, so downloading both Windows artifacts of a pre-`17.41.0` run
gives two same-named `golden-deltas.direct3d11.txt` files that are two implementations measured against one set
of references. The macOS pair is the one where mixing those up costs the most, because `metal` is the fleet's
cross-backend reference family.

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
  is every unattended run, `shader-validation` is a deep dispatch (where `MTL_SHADER_VALIDATION=1` adds
  in-shader bounds checking on top), and `capture` is a `tier=capture` dispatch, where the device is a
  `CaptureMTLDevice` and the layer is therefore NOT the live instrument. That last case is why the name has
  three cases rather than one: while the capture rode every unattended trigger, this artifact was called
  `debug-layer` on runs where the layer held nothing. A fourth case, the #617 control on the incumbent Metal
  leg, went with that leg in `18.0.0`. **Neither tier is a synchronisation
  validator**, and Metal has none at all, which is the one place this matrix is weaker than the Vulkan side:
  a missing read-after-write hazard across encoders has no detector anywhere in this net.
  On EVERY run of that leg this artifact also carries the engine's OWN Metal lines, which no earlier run of it
  could contain ([#617](https://github.com/APKiwiOrg/KhaozEngine/issues/617)): the armed tier and the device's
  Objective-C class from `MetalGpuDevice`, and every failed command buffer from `MetalDeviceLossLatch`. A run
  with the sink configured ANNOUNCES ITSELF, one line under the category `MetalValidationLogHost`, and that
  announcement is what makes the artifact readable on its own. The line present with nothing after it is a
  clean run. NO Metal lines at all is a lost producer rather than a clean run, which is the state #617's
  artifact was in and could not report.
- **`device-evidence-<leg>`** is what the boot's GPU actually was, taken on the macOS leg after the test step:
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
  DIFFERS between a red boot and a green one. **That is the whole reason it is not `failure()`-gated**: a
  failure-only capture produces a red boot's facts with no baseline anywhere to difference them against. That
  matters more since `18.0.0` than it did before, because the incumbent Metal leg that used to run beside it as
  a same-boot control is deleted, and a green run of this leg is the only baseline left. The step is `continue-on-error` with `|| true` on every command,
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
- **One uniform buffer per pipeline.** The rule was written against the Veldrid Metal backend, where a STAGE
  referencing fewer buffers than the declared layout array puts before them made Veldrid's per-kind declaration
  count and SPIRV-Cross's emission disagree: a fragment function reading set 1 alone was emitted at `buffer(0)`
  and written at `buffer(1)`, so it read a slot nothing wrote. Measured 2026-08 on an M2 Max, two of the three
  multi-uniform-buffer shapes bound correctly on that backend and only the third failed, and the engine's own
  native Metal backend binds all three, because it binds at the index the emission chose rather than at a
  counted one. **That backend was deleted in `18.0.0`, so the mis-binding is gone with it**, and the rule is
  kept as a shipped-content constraint rather than a live hazard: the splat pipeline still appends its params
  after the light arrays in one combined UBO, and changing that shape is a golden-moving change rather than a
  free simplification. See `SplatVert` / `SplatFrag`, and `docs/DEPENDENCY-SEAMS.md`'s "ONE uniform buffer per
  pipeline" section for the mechanism and the exact scope.
- **A new render feature needs a pixel-READBACK assertion, not just "it did not throw".** Any `[GpuFact]` test
  runs on `metal-native` (every trigger) and on the other two legs (weekly and dispatch) regardless of its name,
  so this is no longer a naming trap, but the underlying lesson stands: the original splat tests asserted no
  throw with no pixel readback, so the D3D11 leg ran them and still let the white-terrain bug through.

When a backend-specific render bug reproduces ONLY on the CI rasterizer (WARP / lavapipe), dump the SPIRV-Cross
output locally to read what that backend's compiler receives:
```bash
KE_WRITE_SHADER_CORPUS=1 KE_SHADER_CORPUS_DUMP=/tmp/shaders dotnet test KhaozEngine.Render.Tests --filter ShaderCorpus
```

That drops the emitted HLSL and MSL for every shipped program as files under that directory, alongside the
SPIR-V each was cross-compiled from. It is a pure cross-compile with no device, so it runs on any OS and
produces byte-identical text to what FXC sees on Windows, which is what lets you diff a broken shader's
signature against a working sibling. Bisect with many cheap Windows-only `cross-platform-gpu`
`workflow_dispatch` runs, and restore the full three-backend matrix only to verify the final fix.

## Remaining productization gaps

This release delivers the **verification mechanism**, not a finished cross-platform product. Open items:

1. **Windowed-app native bundling (mostly resolved).** The headless golden tests use `CreateHeadless`
   (no window), so they need no windowing natives. The engine windows through Silk.NET.Windowing (GLFW), which
   bundles its natives per-RID across desktop, so a shipped windowed game needs no hand-bundled SDL2 (no
   `brew install sdl2` on macOS). The shader toolchain's natives ride along per-RID the same way, as
   `Silk.NET.Shaderc.Native` and `Silk.NET.SPIRV.Cross.Native`, and they cover eight RIDs including
   `linux-arm64`, which the outgoing `libveldrid-spirv` did not. No Veldrid package is left in the graph at
   all. The remaining work is run-verifying the windowed path on Windows/Linux hardware (the headless matrix
   above does not open a window).
2. **There is no OpenGL backend and there is not going to be one.** The engine never wrote one, the incumbent's
   was never verified, and `OpenGL` is a RETIRED `GpuBackendKind` member since `18.0.0`: the token still parses,
   so a script that sets `KE_GRAPHICS_BACKEND=gl` keeps working, and it runs the platform's own default and
   warns. No `.opengl` golden family exists and none ever will.
   (Clip-space-Y is not a baked Metal assumption: `GpuClip` derives the clip-Y sign
   from `GpuCapabilities`, identity on Metal / D3D11 and flipped on Vulkan. The Vulkan path is verified in
   CI against lavapipe (goldens committed) but not yet on real Vulkan hardware.)
3. **Deferred port-hardening.** Two items are scoped but not yet built: GPU device-lost / device-removed
   handling (recreate the device + resources on a lost swapchain) and a central `Platform` OS-info seam
   (one place that answers OS / RID / capability questions).
4. **Mobile (Android / iOS) is a separate project.** It needs native windowing/lifecycle, Native AOT, and
   build-time shader pre-compilation - not covered by this desktop matrix.
