# KhaozEngine.Gpu

The GPU backend seam for the custom MonoGame-free stack: Veldrid contained behind an engine-owned layer.

What it owns today:

- **`GpuBackendKind`** - Metal / Vulkan / Direct3D11 / OpenGL / Direct3D11Native. Members are pinned to explicit
  values and are APPEND-ONLY: a game persists the player's backend choice as a stored preference and hands it back
  as a `GpuBackendKind`, so renumbering repoints every saved graphics setting. `Direct3D11Native` (17.30.0) is
  Direct3D 11 through the engine's own backend (`KhaozEngine.Gpu.D3D11`) rather than through Veldrid, and it is a
  separate member so a session log, a telemetry header and a frame time each name the implementation that actually
  ran. `GpuBackendKinds.IsDirect3D11(kind)` (17.30.0) is the family predicate for anything that talks to the D3D11
  API or reports on the D3D11 driver, and it answers true for both.
- **`GpuBackendSelector`** - `Select()` reads the `KE_GRAPHICS_BACKEND` env override
  (`metal`/`vulkan`/`d3d11`/`d3d11-native`/`gl`, case-insensitive) and otherwise probes the OS (macOS -> Metal,
  Windows -> Direct3D11, Linux -> Vulkan). The Windows probe stays on the Veldrid `Direct3D11` until the native
  backend's rollout gates are green, so `d3d11-native` is reached by naming it. `Select(string?, OSPlatformKind)`
  is the pure, headless-testable overload.
- **`GpuBackendSelection` / `GpuBackendSource`** (17.21.0) - the same choice, reported with its provenance.
  `Resolve()` and the pure `Resolve(string?, OSPlatformKind)` return `GpuBackendSelection(Backend, Source,
  RequestedOverride)`, where `Source` is `OsProbe`, `EnvironmentOverride`, or `UnrecognizedOverride`, and
  `RequestedOverride` is the RAW env value as read (untrimmed, original case) or null when none was present.
  `Select` is implemented on top of `Resolve`, so there is one decision path and the two cannot drift. A blank
  or whitespace-only value counts as no override at all. Only a non-blank unparseable value is
  `UnrecognizedOverride`, and it still falls back to the OS probe for the backend. This exists because a typo'd
  override is otherwise indistinguishable from the OS default: the run silently uses the default and reads as
  "the requested backend did not help" when it never ran.
- **Stored user preference** (17.23.0) - `Resolve(string?, OSPlatformKind, GpuBackendKind?)` (and the matching
  `Select` / `Resolve(GpuBackendKind?)`) put the game's saved graphics setting between the env override and the
  OS probe, so the precedence is `KE_GRAPHICS_BACKEND` > preference > probe. The preference arrives as DATA and
  is never read from disk here: this package references only Diagnostics + Primitives, and a settings dependency
  would invert that. A game passes it via `GameAppOptions.GraphicsBackendPreference`. `GpuBackendSource` gains
  an appended `UserPreference`. An unrecognized env value now falls through to the preference (it is not an
  override if it does not parse) while still carrying its raw text for the warning. With a null preference the
  behaviour is identical to before.
- **`IsBackendSupported` / `SupportedBackends()`** (17.23.0) - which backends this machine can actually run, as
  a FUNCTIONAL probe (Veldrid loads the library, creates an instance, enumerates devices, checks the required
  Vulkan surface extensions), cached for the process lifetime. **A settings UI must offer only what
  `SupportedBackends()` returns.** `OpenGL` always reports unsupported: there is no windowed GL device path.
  Necessary but NOT sufficient, so it is paired with the creation fallback below rather than trusted alone.
- **`GpuBackendProviders` / `IGpuBackendProvider`** (17.30.0) - the registry for a backend that ships in its own opt-in
  package, which this package cannot reference without a cycle. The consuming app registers it with one explicit
  call at startup (`KhaozEngineD3D11.Register()` for `KhaozEngine.Gpu.D3D11`), and `GpuDeviceContext` then creates
  through the provider. No `[ModuleInitializer]` and no reflection: the CLR loads an assembly lazily on first type
  reference, so a package reference alone would not guarantee self-registration ever runs, and that failure is
  silent and machine-dependent. **A missing registration THROWS `GpuBackendProviderMissingException` and never
  falls back**, because a run that quietly used a different backend would file its measurements under the wrong
  name. An incapable MACHINE is the other case entirely: the provider's own `IsSupported()` functional probe
  answers `IsBackendSupported`, and it reports through the ordinary `FallbackAfterFailure` path. A provider that
  CREATES successfully and then hands back nothing, or hands back a device whose own `Backend` disagrees with the
  selection, **throws too and never falls back**: that is a bug in the provider, and the fallback shape says
  "this machine cannot run the backend", which would be the wrong answer told about the wrong thing. The rejected
  device is disposed on the way out, since no context exists to own it.
  `RequiresProvider(kind)` says which kinds go through the registry, stated as everything this package does not
  build itself, so an appended kind is provider-backed by default.
- **`AfterFallback(selection, fallbackBackend)`** (17.23.0) - the pure helper building the post-fallback report
  (backend becomes what ran, source becomes `FallbackAfterFailure`, `RequestedBackend` keeps what was asked
  for). Used by `GpuDeviceContext`, and by a consumer driving its own retry so both report identically.
- **`GpuThreadingCaps` / `GpuThreadingDiagnostics`** (17.22.0) - the graphics driver's multi-threading
  capabilities, on **Direct3D11 only**: `DriverCommandLists` and `DriverConcurrentCreates`, read straight off the
  live device with `ID3D11Device::CheckFeatureSupport` (`D3D11_FEATURE_THREADING`) and surfaced on
  `GpuDeviceContext.ThreadingCaps` (and `AppWindow.ThreadingCaps`). `GpuThreadingDiagnostics.Describe` renders the
  value, `ShouldWarn` says whether it is the bad case, and `EmulatedCommandListsWarning` is the wording the engine
  logs. All three are pure, so a game's debug overlay shows exactly what the log says.
  - **Why it exists.** A driver reporting `DriverCommandLists=FALSE` cannot build deferred-context command lists,
    so the D3D11 runtime emulates them by recording every call into a token stream and replaying it. That is a
    fixed cost on every recorded command, and Veldrid adds an extra `VSUnsetConstantBuffer` before each partial
    constant-buffer bind on exactly this flag. The two compound, and the result reads as "this machine is just
    slow". `GpuDeviceContext` logs an INFO line per created device and a WARN when the flag is false.
  - **Null means no answer**, and that covers three cases on purpose: not a Direct3D11 device, not Windows, or the
    query failed. None of them tells you anything about the driver, so none of them warns.
  - **A hard no-op off Windows and off D3D11.** The guard returns before touching the device and before naming any
    Vortice type, so that assembly never loads on macOS or Linux. Every failure path degrades to unknown instead
    of throwing: this is a diagnostic, and it must not be able to break device creation.
- **`GpuInjectedModules`** (17.24.0) - the known third-party overlay / capture injectors hooked into this
  process, surfaced on `GpuDeviceContext.InjectedModules` (and `AppWindow.InjectedModules`) and logged once per
  created device, with a WARN on any match. The list covers Nahimic, Sonic Studio, RivaTuner / MSI Afterburner,
  NVIDIA GeForce Experience, Discord, and OBS game-capture, in both their 32-bit and 64-bit spellings.
  `Match(IEnumerable<string?>)`, `Describe`, `ShouldWarn`, `Warning`, and `KnownModuleNames` are all pure and
  device-free, so a game overlay renders exactly what the log says and the wording is testable on any OS. Only
  the module enumeration itself is Windows-only, and it is internal.
  - **Why it exists.** This software injects itself into Direct3D to draw over the game and is a known cause of
    stutter, corrupted frames, and driver-level crashes that read as engine bugs. A run that does not record
    whether one was present cannot rule it out afterwards.
  - **Null and empty are opposite facts.** Null = the scan never ran (not Windows, or it failed) and renders as
    `UnknownDescription`. Empty = it ran and the process is clean, `NoneDescription`. `ShouldWarn` is false for
    null, since "we could not look" is not evidence of a hook. Test with `Describe`, not with `Count`.
  - Gated on the SCAN, not the backend: overlays inject on Windows whatever API is in use, so a Windows Vulkan
    session logs the line too. Scanned per created device rather than cached process-wide, so a late-attaching
    overlay still shows up.
- **`GpuTelemetry`** (17.25.0) - the one-call bridge that fills a `KhaozEngine.Diagnostics`
  `TelemetrySessionInfo`'s GPU fields, so a telemetry recording's session header names the backend, its
  provenance, what was asked for when that differs, the adapter, the injected overlays, and the Direct3D11
  threading caps without the game re-deriving any of it. `info.WithGpu(device)` for a live `GpuDeviceContext`, or
  `info.WithGpu(selection, adapterDescription, injectedModules, threadingCaps)` for a consumer holding an
  `AppWindow` (which surfaces the same four facts without handing out its device). Both return the instance so
  construction chains.
  - It lives HERE, not in `KhaozEngine.Diagnostics`, because this package references that one and not the
    reverse. The header's GPU fields are therefore plain strings and nullable bools, and the enum mapping sits in
    the package that owns the enums.
  - The enum NAMES are recorded, not the numbers. `GpuBackendKind` and `GpuBackendSource` members are append-only
    by contract, so the name is as stable as the number and says what it means to whoever reads the capture.
  - **All four `GpuBackendSelection` members are carried**, so a fallback capture is not less informative than
    the `GPU backend: <kind> (fallback, <requested> failed)` line logged beside it. `RequestedBackend` is the one
    record of a player's own in-game backend choice on a `UserPreference` fallback, and `RequestedOverride` goes
    in verbatim, since the untouched string is what makes a typo or stray quoting obvious.
  - The value overload is pure, so the whole mapping is testable with no device on any OS. Null and empty
    `injectedModules` stay apart, and `threadingCaps` null stays null.
  - **17.32.0 adds a five-value overload** taking a `GpuDeviceDiagnostics` as its last argument, which fills the
    header's `softwareAdapter` and `deviceLossReason` fields. `info.WithGpu(device)` uses it for you. The
    four-value overload is unchanged, so an already-compiled consumer keeps binding to what it was compiled
    against, and it leaves both new fields null.
- **`GpuDeviceDiagnostics`** (17.32.0) - the two facts a device can only report about ITSELF and only LIVE:
  `SoftwareAdapter` (on Direct3D11, `DXGI_ADAPTER_FLAG_SOFTWARE`) and `DeviceLossReason`
  (`GetDeviceRemovedReason`'s answer plus the call site that noticed). On `IGpuDevice.Diagnostics`,
  `GpuDeviceContext.Diagnostics` and `AppWindow.Diagnostics`, and read THROUGH to the device on every access
  rather than captured at
  creation, because a device loss happens at an arbitrary moment long afterwards and a captured value would
  always say the device was fine.
  - Both members are nullable and **null means "nobody answered" rather than "no"**. A backend that does not
    report the software-adapter flag is a different fact from one that reports false, and a capture that cannot
    tell those apart cannot say whether its performance numbers are comparable with another capture's.
  - The `IGpuDevice` member is DEFAULT-IMPLEMENTED, so it was appended without breaking any implementer, and the
    default is the honest one: no answers. Everything on the Veldrid path takes it, which is correct rather than
    a gap, since Veldrid exposes neither the DXGI adapter flag nor a device-removal reason. The native
    Direct3D 11 backend is what fills it.
- **`GpuD3D11DeviceFlags`** (17.24.0) - the opt-in Direct3D11 device-creation flags and the env gate for them.
  `KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS=1` (or `true`/`yes`/`on`) ORs
  `D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS` into both the windowed and the headless D3D11
  creation path and logs `ActiveDescription`, so a tester's log proves the lever was on. An unrecognized value
  WARNs (`UnrecognizedWarning`) instead of silently reading as off. `Resolve(string?, out string?)` is the pure
  parse, `FromEnvironment` the one impure member.
  **It is a DIAGNOSTIC lever, not a setting**: it stops the D3D11 runtime applying its internal threading
  optimizations, which can cost performance, so nothing turns it on by default. The flag value is taken from
  Vortice's enum as a compile-time constant, so it cannot drift and no Vortice type is named in the emitted code
  (the assembly stays unloaded off Windows, as the threading probe requires).
- **Direct3D11 immediate-context recording** (17.23.1) - KhaozEngine creates every Direct3D11 device through
  its Veldrid fork's `D3D11DeviceOptions.UseImmediateContext` mode. It records commands directly on the D3D11
  immediate context rather than creating and executing a deferred context per `IGpuCommandList`. This is
  deliberately D3D11-only: Metal, Vulkan, and OpenGL use their existing Veldrid factories unchanged. The fork
  serializes a list from `Begin` through `Submit`, which matches KhaozEngine's single render-thread command
  sequence. It addresses the large D3D11 encoding cost observed in the field even when
  `DriverCommandLists=TRUE`. The vendored fork is `4.9.103` since 17.27.0, which adds the second-recorder
  guardrail described below. `4.9.102` before it, since 17.26.0, fixed
  `SmallFixedOrDynamicArray` reading pool garbage for a resource set with more than five dynamic offsets (every
  backend, not just this mode). `4.9.101` before it, since 17.24.0, added immediate-context hazard fixes plus
  Direct3D11 bind batching (dirty tracking flushed at draw and dispatch time, an offsets-only rebind fast path,
  bound-record dedup, a pipeline-switch drain). The hazard fixes make a foreign-thread `Reset`, which both
  `Swapchain.Resize` and `CommandList.Dispose` issue, a silent no-op rather than a `SynchronizationLockException`
  that also clobbers the render thread's in-flight recording, and make the immediate-context lock outermost, which
  kills a reachable two-thread deadlock. Resizing during recording stays unsafe regardless, because `Resize` still
  disposes the framebuffer the render thread has bound, so resize between frames and never during one. No API
  change: see `vendor/veldrid/README.md`.
- **What the fork rejects, and the one residual.** Both nesting cases now throw, and they are different guards. A
  second `Begin` on the SAME `IGpuCommandList` has thrown since `4.9.101`. A second `Begin` on a DIFFERENT one,
  while the first still holds the immediate context, throws as of `4.9.103`
  ([#428](https://github.com/APKiwiOrg/KhaozEngine/issues/428)). Before that it ran `ClearState` on the live
  context and silently wiped the open recording's bindings, which is exactly the corruption behind
  [#423](https://github.com/APKiwiOrg/KhaozEngine/issues/423). The guardrail was held back for four fork releases
  because the windowed `GameApp3D` path forced that second list open (`AppWindow.Run` opened the frame's command
  list before calling back into the app), so adopting it would have converted a corrupted frame into a hard throw
  on Windows. The frame loop now has a pre-record phase and the ocean prime runs there
  ([#429](https://github.com/APKiwiOrg/KhaozEngine/issues/429)), so no engine-shipped windowed host opens a nested
  list any more, and the two ship together in 17.27.0 with the cause removed first. One residual remains by
  design: a host driving a `Render3DSurface` off a raw `AppWindow.Run(onFrame)` without passing `onPrepare` still
  nests, because the surface's safety-net `Scene3D.PrepareFrame` then runs inside the frame's recording. That
  residual is now a loud `VeldridException` naming the fix (pass the pre-record phase) instead of silent
  corruption, which is the intended trade. The rule for engine code is unchanged either way: do not open a command
  list while another is recording on Direct3D11, and put work that needs its own list in `Scene3D.PrepareFrame` or
  the loop's pre-record phase.
- **`GpuCapabilities`** - `ClipSpaceYInverted` / `DepthRangeZeroToOne` (so renderers derive clip-Y / depth
  handling from the active backend instead of a baked Metal assumption), plus diagnostics: `DeviceName` (the GPU
  adapter/driver), `SamplerAnisotropy`, `SamplerLodBias` (whether those sampler levers are supported),
  `MaxMsaaSampleCount` (the largest MSAA sample count the engine's MRT formats support, for building/clamping an AA
  menu), `SupportsShadowMaps` (whether the device can render + sample an R32_Float depth target, gating the
  `ShadowMode.ShadowMap` tier in Render3D), and `SupportsCompletionFences` (see **Completion fences** below).
  Read off the live device.
- **MSAA plumbing** - `GpuTextureDescription.SampleCount` (and `IGpuTexture.SampleCount`) make a multisampled render
  target; `GpuOutputDescription.SampleCount` / `WithSampleCount` carry the count so a pipeline matches its
  framebuffer (read a live multisampled framebuffer's count off `IGpuFramebuffer.Outputs`); and
  `IGpuCommandList.ResolveTexture(src, dst)` resolves a multisampled target into a single-sample texture. Default
  sample count 1 (single-sample) everywhere, so existing render paths are unchanged.
- **`GpuWindowHandle`** - a native window handle (kind + handle/display) the windowing layer hands over, so
  this package needs no reference to the windowing library.
- **`GpuDeviceContext`** - `CreateForWindow(in GpuWindowHandle, width, height, syncToVerticalBlank = true)` (device
  + swapchain for a Silk.NET/GLFW window; the vsync flag feeds both the device options and the swapchain, since
  9.23.0) and `CreateHeadless()` (offscreen device) on the selected backend. Two further `CreateForWindow`
  overloads since 17.23.0: a nullable `GpuBackendKind?` preference (resolved against the environment, WITH the
  fallback below) and a non-nullable `GpuBackendKind` (exactly that backend, no resolution and no fallback, the
  "retry as X" lever). **Creation falls back** to the OS-probe backend when the requested backend fails, rather
  than propagating, so a stored preference the machine cannot run cannot leave a player with a client that will
  not start. It WARNs, reports `GpuBackendSource.FallbackAfterFailure` with `Selection.RequestedBackend`, and
  **never clears the game's stored setting, which the game must do itself** (file IO is not this package's job).
  The retry reuses the same `GpuWindowHandle`, so no second window is created. Skipped entirely when the request
  already is the OS-probe default, which is every call with no override and no preference, so default macOS and
  Linux paths are unchanged, as is `CreateHeadless`. Exposes `Backend`, `Selection` (the
  full `GpuBackendSelection`, since 17.21.0), `ThreadingCaps` (the D3D11 driver threading caps, since 17.22.0),
  `AdapterDescription` (the adapter the device runs on, empty when the backend reports none, since 17.24.0 - the
  same value as `Capabilities.DeviceName`, which stays the single source, and on Direct3D11 it is exactly the
  DXGI adapter description), `InjectedModules` (the overlay scan result, since 17.24.0),
  `Capabilities`, and the engine-owned `IGpuDevice`. The raw Veldrid device is private to the context (the
  transitional accessor that used to be here is gone, and `Capabilities` is read once so the context's copy and
  the device's cannot drift). Device creation and disposal are serialized process-wide behind a single static gate, on
  every backend: concurrent device creation races the Vulkan loader's dispatch setup (observed as
  `vkGetDeviceQueue` aborts on Mesa lavapipe under full test-suite parallelism), and creation/disposal are rare
  enough that the serialization costs nothing measurable. Callers see no API change, it only affects the
  interleaving of concurrent create/dispose calls across threads.
- **`IGpuDevice.SyncToVerticalBlank`** (settable, since 9.24.0) - flip vsync on a live windowed device: it
  reconfigures the main swapchain in place (no recreate, no leaked swapchain, size + depth preserved; on Metal it
  reaches `CAMetalLayer.displaySyncEnabled`). A no-op mirrored value on a headless device (Veldrid throws setting
  it with no main swapchain). `AppWindow.PresentMode` routes through it for runtime present-mode switching.
- **Completion fences** - `IGpuResourceFactory.CreateFence()` makes an unsignaled `IGpuFence`,
  `IGpuDevice.Submit(cl, fence)` signals it once the GPU has finished that submission, and `IGpuFence.Signaled`
  polls it without blocking (`Reset()` returns it for reuse). There is no blocking wait on the seam: the point of a
  fence here is to REPLACE a `WaitForIdle`, not to dress one up. A fence handed to a submission made after some
  earlier work signals only once the queue has drained through that work, which is what makes it a drop-in for the
  drain that guarded deferred GPU-resource destruction (Render3D's `RetiredResourcePool`).
  - **Gate on `GpuCapabilities.SupportsCompletionFences`, and expect two backends to say no.** Metal and Vulkan
    report true. Direct3D11 and OpenGL report FALSE even though Veldrid hands out a `Fence` on them, because on
    those backends it is a `ManualResetEvent` set on the CPU as the submit call returns, which is a submit receipt
    and says nothing about the GPU. `CreateFence()` throws there rather than return a fence that lies. A caller
    that cannot get one keeps whatever it did before. The opt-in `KhaozEngine.Gpu.D3D11` native backend is the
    exception among the Direct3D 11 paths and reports TRUE, on a real device-wide completion counter, but its
    device is not creatable yet so nothing reaches that answer today.
- **Compute** (since 15.2.0) - `IGpuResourceFactory.CreateComputeShaderFromSpirv(computeGlsl)` compiles a GLSL 450
  compute source into an `IGpuComputeShader`, and `CreateComputePipeline(in GpuComputePipelineDescription)` builds an
  `IGpuComputePipeline` over it plus its resource layouts. Both handle types are separate from the graphics
  `IGpuShaderSet` / `IGpuPipeline`, so a compute pipeline bound for a draw is a compile error. `IGpuCommandList`
  gains `SetComputePipeline`, `SetComputeResourceSet` (plain and dynamic-offset), `Dispatch(x, y, z)`, and
  `CopyBuffer`. Storage resources: `GpuTextureUsage.Storage` for a read-write image (bound via
  `GpuResourceKind.TextureReadWrite`), and the existing `GpuBufferUsage.StructuredBufferReadOnly`/`ReadWrite` for
  storage buffers. Readback: `IGpuDevice.Map`/`Unmap` take a buffer as well as a texture, and
  `GpuReadback.ReadBuffer<T>` wraps the staging-copy-map-unmap sequence. Gate on `GpuCapabilities.SupportsCompute`;
  creating a compute shader or pipeline on a device without it throws.
  - **The workgroup size comes from the shader, not from you.** `IGpuComputeShader.ThreadGroupSizeX/Y/Z` is read
    out of the compiled SPIR-V module's `LocalSize` execution mode and is what the pipeline is built with, so there
    is no second copy of `layout(local_size_x = ...)` to drift (which would be invisible on Vulkan/D3D11 and
    silently wrong on Metal, the only backend that reads it). Do declare it: omitting the layout is legal GLSL and
    compiles in the 1x1x1 default, which dispatches one invocation per group rather than failing.
  - **Ordering rules, since there is no barrier call.** Compute writes a storage texture that a graphics pass then
    samples: record BOTH in the SAME command list and create the texture `Storage | Sampled`. A dispatch that reads
    what an earlier dispatch wrote: separate them with `End` + `Submit` + `WaitForIdle`. Full reasoning per backend
    is on `IGpuCommandList`, in `docs/USING-KHAOZENGINE.md`, and in `docs/design/GPU-COMPUTE-DESIGN-2026-07-26.md`.
  - **A compute-written texture that also needs a mip chain is TWO textures.** A storage-image binding must cover
    exactly one mip level, and resource sets bind whole textures rather than views, so the compute target stays
    single-mip and a second `Sampled | GenerateMipmaps` texture carries the chain.
    `IGpuCommandList.CopyTextureSubresource(src, srcMip, srcLayer, dst, dstMip, dstLayer, w, h)` seeds the second
    one's base level per array layer; `GenerateMipmaps` then fills the rest. Both go in the same list as the
    dispatch and cost no extra drain - the ordering rule above is about a dispatch reading a dispatch, and a
    transfer is where every backend synchronises anyway.
- **`ShaderValidation`** - `ValidatePair(vertexGlsl, fragmentGlsl, label?)` compiles a GLSL 450 vertex/fragment
  pair to SPIR-V and cross-compiles it to every backend target (HLSL, MSL, GLSL, ESSL) with NO `GraphicsDevice`,
  so a shader syntax error or a backend miscompile is caught in a fast GPU-free test loop instead of at first run
  on a real device of that backend. `ValidateCompute(computeGlsl, label?)` is the single-stage sibling for a
  compute shader. A compile failure throws `ShaderValidationException` naming the label and the failing
  stage/target. `ValidateCompute` ALSO rejects a source whose cross-compiled Metal entry point numbers its buffer
  arguments out of binding order (since 16.3.0): Metal has no binding decorations, so the cross-compiler assigns
  slots in first-reference order while the backend binds a resource set by counting the layout in binding order,
  and a helper function that reads binding 1 before anything reads binding 0 silently swaps the two on Metal with
  Vulkan and Direct3D11 perfectly correct. It catches a uniform/storage swap; two same-kind buffers swapping is
  not visible from the emitted Metal and still needs a readback test. The engine's own shader-source tests use this to validate every embedded production shader; games
  can validate their custom shaders the same way in their own fast test suites.

This is the ONLY package meant to reference Veldrid, and the containment is complete: the resource, command and
device surface is the engine-owned `IGpuDevice` / `IGpuResourceFactory` / `IGpuCommandList` interface set, the
backend implementation lives in `Internal/`, and no Veldrid type reaches the public API (asserted by reflection in
`GpuPublicApiTests` here and `VeldridLockdownTests` for the consumer packages). Swapping the backend is a new
`IGpuDevice` implementation, not a consumer-visible change.
