# KhaozEngine.Gpu

The GPU backend seam for the custom MonoGame-free stack: the graphics APIs contained behind an engine-owned
layer. Since 18.0.0 this package builds NO device of its own, and the engine's three backend packages are the
only implementations.

**"The incumbent" below always means the Veldrid backend deleted in 18.0.0**, whichever of its three graphics
APIs the sentence is about. It is cited in the past tense, because what it did is what several members here
were shaped to reproduce or to diverge from, and that reasoning is what makes the seam readable. Nothing
selects it any more, and no Veldrid package is left in the graph: the shader toolchain it left behind became
`Silk.NET.Shaderc` plus `Silk.NET.SPIRV.Cross` in the same release.

What it owns today:

- **`GpuBackendKind`** - Metal / Vulkan / Direct3D11 / OpenGL / Direct3D11Native / VulkanNative / MetalNative.
  Members are pinned to explicit values and are APPEND-ONLY: a game persists the player's backend choice as a
  stored preference and hands it back as a `GpuBackendKind`, so renumbering repoints every saved graphics
  setting. `Direct3D11Native` (17.30.0), `VulkanNative` (17.32.0) and `MetalNative` (17.35.0) are those three
  APIs through the engine's own backends (`KhaozEngine.Gpu.D3D11`, `KhaozEngine.Gpu.Vulkan`,
  `KhaozEngine.Gpu.Metal`), and each is a separate member so a session log, a telemetry header and a frame time
  each name the implementation that actually ran. **`Metal`, `Vulkan`, `Direct3D11` and `OpenGL` are RETIRED in
  18.0.0**: they named the deleted Veldrid backend, `GpuBackendSelector.IsRetired` answers for them,
  `NativeReplacementFor` maps each onto the live backend for its API, and naming one in code throws
  `GpuBackendRetiredException`. They are kept forever, because the enum is append-only and games have persisted
  them.
  `GpuBackendKinds.IsDirect3D11(kind)` (17.30.0), `GpuBackendKinds.IsVulkan(kind)` (17.32.0) and
  `GpuBackendKinds.IsMetal(kind)` (17.35.0) are the family predicates for anything that talks to that API or
  reports on its driver, and each answers true for both of its implementations.
- **`GpuBackendSelector`** - `Select()` reads the `KE_GRAPHICS_BACKEND` env override
  (`metal`/`metal-native`/`vulkan`/`vulkan-native`/`d3d11`/`d3d11-native`/`gl`, case-insensitive, plus the
  `mtl-native`, `vk-native`, `direct3d11`, `direct3d11-native` and `opengl` aliases) and otherwise probes the
  OS. **Since 17.40.0 every arm of that probe answers the ENGINE'S OWN backend**: macOS -> `MetalNative`,
  Windows -> `Direct3D11Native`, Linux and everything else -> `VulkanNative`, and since 18.0.0 those are the
  only backends. A `metal` / `d3d11` / `vulkan` / `gl` token names a retired member and RESOLVES to that API's
  native backend with a WARN. `IncumbentFor` is deleted, so `ProbeOS` is the single map and is what a failed
  device creation and an unrecognized override both fall back TO, along with a stored `OpenGL`. A stored
  `Metal` / `Vulkan` / `Direct3D11` takes `NativeReplacementFor` instead, the same map the token takes, so a
  Windows player's stored `Vulkan` runs `VulkanNative` rather than being quietly reversed onto the platform
  default. `Select(string?, OSPlatformKind)` is the pure, headless-testable overload.
- **`GpuBackendSelection` / `GpuBackendSource`** (17.21.0) - the same choice, reported with its provenance.
  `Resolve()` and the pure `Resolve(string?, OSPlatformKind)` return `GpuBackendSelection(Backend, Source,
  RequestedOverride)`, where `Source` is `OsProbe`, `EnvironmentOverride`, or `UnrecognizedOverride`, and
  `RequestedOverride` is the RAW env value as read (untrimmed, original case) or null when none was present.
  `Select` is implemented on top of `Resolve`, so there is one decision path and the two cannot drift. A blank
  or whitespace-only value counts as no override at all. Only a non-blank unparseable value is
  `UnrecognizedOverride`, and the OS probe still decides the backend in that case, so
  `WasPinnedByEnvironment` (17.40.0) is false for it: a value that decided nothing pinned nothing.
  `CameFromStoredPreference` (18.0.0) is the mirror: true when a player's saved choice put this backend here,
  honoured as `UserPreference` or already redirected off a retired member, and it is the one provenance for
  which a MISSING provider falls back rather than throwing. This exists because a typo'd
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
  a FUNCTIONAL probe run by the backend's own provider (it loads the library, creates an instance, enumerates
  devices, checks the required Vulkan surface extensions), cached for the process lifetime. **A settings UI must
  offer only what `SupportedBackends()` returns.** Since 18.0.0 that list is the three native kinds, and every
  retired member answers false, so a picker that maps only the three retired Veldrid kinds renders the default
  as an unknown row. A kind appears ONLY where its provider is registered, which the `Game2D` / `Game3D`
  umbrellas and `AppWindow` take care of for an ordinary game.
  Necessary but NOT sufficient, so it is paired with the creation fallback below rather than trusted alone.
- **`GpuBackendProviders` / `IGpuBackendProvider`** (17.30.0) - the registry for a backend that ships in its own opt-in
  package, which this package cannot reference without a cycle. The consuming app registers it with one explicit
  call at startup (`KhaozEngineD3D11.Register()` for `KhaozEngine.Gpu.D3D11`), and `GpuDeviceContext` then creates
  through the provider. No `[ModuleInitializer]` and no reflection: the CLR loads an assembly lazily on first type
  reference, so a package reference alone would not guarantee self-registration ever runs, and that failure is
  silent and machine-dependent. **A missing registration for a backend the caller NAMED throws
  `GpuBackendProviderMissingException` and never falls back**, because a run that quietly used a different
  backend would file its measurements under the wrong name. Since 18.0.0 a DEFAULTED backend with no
  registered provider throws the same exception, because there is no incumbent left to create instead, and
  `GpuBackendSource.DefaultProviderMissing` is retired with the fallback it reported and has no producer left.
  An ordinary game never meets either: the `Game2D` and `Game3D` umbrellas carry the three backend packages and `AppWindow` plus both
  snapshot hosts call `GpuBackends.RegisterResolvedIfUnregistered()` at boot. A stored `UserPreference`
  still falls back and reports `FallbackAfterFailure`, the signal a game clears the preference on, and a
  preference naming a RETIRED member takes the same report after being redirected onto that API's native
  backend. An incapable MACHINE is the other case entirely: the provider's own `IsSupported()` functional probe
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
- **`GpuBackendSource.DefaultProviderMissing`** (17.40.0, ordinal 5) - **RETIRED AT 18.0.0, WITH NO PRODUCER.**
  Nothing in the engine reports it any more and no code path can produce it. It named the case the 17.40.0 flip
  created, a default whose provider was not registered, which fell back to the platform's Veldrid incumbent.
  There is no incumbent, so that case throws `GpuBackendProviderMissingException` instead, and its pure builder
  `AfterMissingDefaultProvider` was deleted with the fallback. The MEMBER stays and keeps its number, because
  this enum is append-only and a 17.40.0 capture that recorded a 5 still has to read back as what it meant, and
  `GpuDeviceContext` keeps a spelled-out boot-line arm for it (`default, {RequestedBackend} has no registered
  provider`) so a replayed capture reads as itself. A game switching on `GpuBackendSource` keeps its arm for old
  captures and will never see it on a live 18.0.0 run.
- **`GpuNoUsableBackendException`** (17.40.0) - the DOUBLE FALL: the requested backend failed, the engine fell
  back, and the fallback failed too, so there is no device. It carries `RequestedBackend`, `FallbackBackend` and
  both failures, and its message names both backends and both reasons in the order they were tried. The FIRST
  failure is `InnerException`, because on a native backend and its deleted Veldrid twin the two usually shared
  one underlying cause and the one worth reading is the first, and the fallback's own exception is on
  `FallbackFailure` so neither stack has to be reconstructed from a log. A backend NAMED outright never reaches
  it: naming one turns fallback off, so its failure propagates alone.
- **`GpuThreadingCaps` / `GpuThreadingDiagnostics`** (17.22.0) - the graphics driver's multi-threading
  capabilities, on **Direct3D11 only**: `DriverCommandLists` and `DriverConcurrentCreates`, read straight off the
  live device with `ID3D11Device::CheckFeatureSupport` (`D3D11_FEATURE_THREADING`) and surfaced on
  `GpuDeviceContext.ThreadingCaps` (and `AppWindow.ThreadingCaps`). `GpuThreadingDiagnostics.Describe` renders the
  value, `ShouldWarn` says whether it is the bad case, and `EmulatedCommandListsWarning` is the wording the engine
  logs. All three are pure, so a game's debug overlay shows exactly what the log says.
  - **Why it exists.** A driver reporting `DriverCommandLists=FALSE` cannot build deferred-context command lists,
    so the D3D11 runtime emulates them by recording every call into a token stream and replaying it. That is a
    fixed cost on every recorded command, and the deleted Veldrid incumbent added an extra
    `VSUnsetConstantBuffer` before each partial constant-buffer bind on exactly this flag. The emulation alone
    reads as "this machine is just slow". `GpuDeviceContext` logs an INFO line per created device and a WARN when the flag is false.
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
  `SoftwareAdapter` (on Direct3D11, `DXGI_ADAPTER_FLAG_SOFTWARE`, and on the native Vulkan backend a physical
  device whose type is CPU or whose driver id is Mesa's llvmpipe) and `DeviceLossReason`
  (`GetDeviceRemovedReason`'s answer plus the call site that noticed). On `IGpuDevice.Diagnostics`,
  `GpuDeviceContext.Diagnostics` and `AppWindow.Diagnostics`, and read THROUGH to the device on every access
  rather than captured at
  creation, because a device loss happens at an arbitrary moment long afterwards and a captured value would
  always say the device was fine.
  - Both members are nullable and **null means "nobody answered" rather than "no"**. A backend that does not
    report the software-adapter flag is a different fact from one that reports false, and a capture that cannot
    tell those apart cannot say whether its performance numbers are comparable with another capture's.
  - The `IGpuDevice` member is DEFAULT-IMPLEMENTED, so it was appended without breaking any implementer, and the
    default is the honest one: no answers. The Metal and Vulkan backends take it, which is correct rather than a
    gap, since neither API answers either question. The native Direct3D 11 backend is what fills it.
- **`GpuDeviceCounters`** (17.32.0, `AcquireWaitCount` / `AcquireWaitMs` added 17.34.0) - the soak counters a
  device keeps about itself, cumulative since creation and read LIVE, on `IGpuDevice.Counters`,
  `GpuDeviceContext.Counters` and `AppWindow.Counters`. `FramesBegun`, `DrainCount` / `DrainMs`,
  `BackpressureStallCount` / `BackpressureStallMs`, `OffTimelineDeferred` / `OffTimelineOutstanding`, and
  `AcquireWaitCount` / `AcquireWaitMs`. All three native backends fill it, and they are the only backends since
  18.0.0.
  - **The acquire pair is the one reading that separates the two acquire models.** `AcquireWaitCount` is present
    boundaries that BLOCKED waiting for the presentation engine to hand back the next swapchain image, and it is
    a READING rather than a count of calls: the native Vulkan backend probes with a zero timeout first and records
    only the blocking call that follows. A backend that acquires with a semaphore and lets the GPU do the waiting
    reports zero, and one that blocks the CPU reports one per frame. Zero is a reading rather than a gap on a
    backend with no acquire at all and on a headless device with no swapchain, which is why `HasValue` below is
    what answers "did anybody look".
  - **`HasValue` is the whole point, because absent is not zero.** Zero stalls is the PASSING result of a field
    soak, so a backend that keeps no counters must not report the same numbers as one that counted and found
    nothing. The default value answers false, which no live backend gives any more. The
    `IGpuDevice` member is DEFAULT-IMPLEMENTED, so it was appended without breaking any implementer.
  - **Cumulative rather than per frame**, which is what makes it usable from a capture. A telemetry session writes
    a row on its own cadence, so a per-frame reading sampled a few times a second reports the frames it happened
    to land on. Subtract the first sampled row from the last and the window's stalls are exact, and the per-frame
    drain cost is that difference over `FramesBegun`'s. The backend keeps its own per-frame rolls for the overlay.
  - **The two backpressure readings are separate members and must stay separate.**
    `BackpressureStallCount` is a frame boundary that BLOCKED on a ring segment the GPU was still reading, which
    is a statement about pipeline depth. `OffTimelineDeferred` is a device-level `UpdateBuffer` that met an
    in-flight segment and queued its bytes for that segment's next reopen, which blocks nobody and usually
    happens at load time. Non-zero off-timeline beside zero stalls is a specific diagnosis, namely that the
    segment count is fine and a caller is writing off-timeline against work still in flight.
- **`GpuTelemetryChannels`** (17.32.0, two channels added 17.34.0) - the projection from `GpuDeviceCounters` onto
  the named numeric channels a `TelemetryRecorder` SAMPLE row carries, with the channel names as constants
  (`gpuFramesBegun`, `gpuDrainCount`, `gpuDrainMs`, `gpuBackpressureStalls`, `gpuBackpressureStallMs`,
  `gpuOffTimelineDeferred`, `gpuOffTimelineOutstanding`, `gpuAcquireWaits`, `gpuAcquireWaitMs`) so a reader and a
  test share one spelling. `AppendTo(channels, window.Counters)` joins a row the game already built, and
  `For(counters)` is the standalone form.
  - **Sample rows, not the header.** The header is written once at start and describes what was already true then,
    when every counter is still zero. The row is the session's channel for a number that moves.
  - **A device that counted nothing writes no columns at all.** Emitting zeros would put a clean-looking stall
    count in every Metal and Vulkan capture, which reads as a backend that never stalls rather than one that
    never looked.
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
- **The Direct3D11 nesting rule, kept from the incumbent (history).** The engine used to create every
  Direct3D11 device through a vendored Veldrid fork in `D3D11DeviceOptions.UseImmediateContext` mode, which
  recorded straight onto the immediate context. Opening a second command list while one was recording then ran
  `ClearState` on the live context and silently wiped the open recording's bindings, which is the corruption
  behind [#423](https://github.com/APKiwiOrg/KhaozEngine/issues/423), and the fork grew a guardrail that threw
  instead ([#428](https://github.com/APKiwiOrg/KhaozEngine/issues/428)). The fork and its backend were deleted in
  18.0.0 along with `vendor/veldrid`, and the RULE outlived them: do not open a command list while another is
  recording, and put work that needs its own list in `Scene3D.PrepareFrame` or the loop's pre-record phase. It is
  enforced on every backend now, by `GpuRecording` below, rather than by one leg's fork.
- **`GpuRecording` / `GpuRecordingScope` / `GpuNestedRecordingException`** - the seam's open-recording register,
  and where the portable one-open-recording-per-device contract on `IGpuCommandList.Begin` is enforced rather
  than described ([#424](https://github.com/APKiwiOrg/KhaozEngine/issues/424)). It STAYS after the incumbent's
  deletion, and its refusal message changed in 18.0.0 to say so
  ([#690](https://github.com/APKiwiOrg/KhaozEngine/issues/690)): the wording used to explain the rule by
  describing the vendored fork's immediate-context mode, and now names the portable rule, the damage and the fix
  with no backend in it, because a reader who meets a backend name in a portable refusal reasonably stops
  applying the rule once that backend is gone.
  `GpuRecording.Open(device, list, owner)` begins the list and claims the device, the returned scope ends it and
  releases, and a second `Open` on the same device throws `GpuNestedRecordingException` carrying `Owner` (who is
  already recording) and `Attempted` (who was refused). `OpenOwner(device)` and `CanOpen(device)` answer the same
  question without being refused. Every recording the engine opens goes through it (the windowed frame list, both
  snapshot hosts, the preview, the offscreen 2D captures, the ocean's priming pass, the retire barrier, the mipmap
  generates and every readback), so the refusal reads the same on every backend and is provable with no GPU.
  Calling `IGpuCommandList.Begin` directly stays legal and unwatched: a consumer's own list gets whatever its
  backend does. Keyed by device instance with no strong reference, and per device rather than per thread, because
  the contract is. Three properties are worth knowing before you build on it. A refusal frees whatever the
  refused call had already built, so catching it and moving the call leaks nothing even when it is retried every
  frame. `CanOpen` is advisory rather than a reservation, so a concurrent `Open` can win the race between the
  `true` it returned and the open it encouraged. And `GpuRecordingScope` is a struct whose release is matched to
  its own list, so a second `Dispose` and a stale copy both do nothing. Each device's entry carries its own lock and none
  is held while a backend is inside `Begin`, which blocks by design on the native Metal and Vulkan backends, so
  one device's GPU backpressure is never paid on another device's thread.
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
- **Texture arrays, including one-layer ones** - `GpuTextureDescription.Texture2DArray(w, h, format, usage,
  arrayLayers, mipLevels)` builds a 2D texture array and sets the new `GpuTextureDescription.IsArray`, so a set
  with exactly ONE layer is still created as an array and binds under a shader that declares `texture2DArray` /
  `Texture2DArray`. Array-ness was derived from `ArrayLayers > 1` alone before 17.39.0, which made a one-layer
  array indistinguishable from a plain 2D texture and bound the wrong type (Metal aborts the process under armed
  validation, lavapipe reads through it silently). That inference is kept as the default, so `IsArray` is true
  whenever `ArrayLayers > 1` and a caller that only passes a layer count is unchanged. The flag names the
  2D-array case only: a cubemap (`GpuTextureUsage.Cubemap`) keeps its own layer-count rule, and a MULTISAMPLED
  ARRAY is refused by the constructor (`ArgumentOutOfRangeException` on `IsArray && SampleCount > 1`), because no
  backend agrees on which type it takes. The three native backends create the array type directly, and they are the
  only backends since 18.0.0. The deleted Veldrid incumbent could not express a one-layer array at all and
  padded one with a second, never-addressed slice that it still COUNTED, so the two paths that name subresources
  handle padding and keep doing so: `CopyTexture` (and with it `GpuReadback.ToRgba`) narrows to the logical
  subresources when a side pads, and an `UpdateTexture` aimed at the phantom layer is refused rather than
  accepted silently. That refusal was made uniform in 17.40.0: native Direct3D 11 and
  native Vulkan took the call in silence until then, because neither API rejects a subresource index past the end
  of the resource (#695). A `GpuTextureUsage.Staging` texture is never padded, having no view to fix.
- **`GpuWindowHandle`** - a native window handle (kind + handle/display) the windowing layer hands over, so
  this package needs no reference to the windowing library.
- **`GpuDeviceContext`** - `CreateForWindow(in GpuWindowHandle, width, height, syncToVerticalBlank = true)` (device
  + swapchain for a Silk.NET/GLFW window; the vsync flag feeds both the device options and the swapchain, since
  9.23.0) and `CreateHeadless()` (offscreen device) on the selected backend. Two further `CreateForWindow`
  overloads since 17.23.0: a nullable `GpuBackendKind?` preference (resolved against the environment, WITH the
  fallback below) and a non-nullable `GpuBackendKind` (exactly that backend, no resolution and no fallback, the
  "retry as X" lever). `CreateHeadless(GpuBackendKind)` (17.32.0) is the headless twin of that last one: exactly
  that backend, no resolution and no fallback, throwing `GpuBackendProviderMissingException` for a
  provider-backed backend nobody registered. It is how a caller brings up TWO backends in one process (a parity
  comparison, or replacing one implementation with another under the same measurements) without reaching around
  this class to `GpuBackendProviders` and creating a device outside the process-wide creation gate below.
  **Creation falls back** to the platform's own default (`GpuBackendSelector.ProbeOS`, the single map since
  `IncumbentFor` was deleted with the incumbent) when the requested backend fails, rather than propagating, so a stored preference the machine cannot run cannot leave a player with a client that will
  not start. It WARNs, reports `GpuBackendSource.FallbackAfterFailure` with `Selection.RequestedBackend`, and
  **never clears the game's stored setting, which the game must do itself** (file IO is not this package's job).
  The retry reuses the same `GpuWindowHandle`, so no second window is created. Skipped entirely when the request
  already IS that default, which is a complete statement since 18.0.0 because there is one default per platform. **`CreateHeadless()` falls back too since 17.40.0**, on the same two guards and with
  the same WARN, because the probe answers a provider-backed kind everywhere now and a `Render2DSnapshot.Capture`
  that worked before a repin must not throw after it. The one thing that never falls back on either path is a
  backend `KE_GRAPHICS_BACKEND` PINNED, which is how every soak session and each cross-platform GPU leg selects:
  its provider is not even probed, and a creation failure comes out as the provider raised it (the windowed half
  of that was missing until #719). A fallback that fails as well throws `GpuNoUsableBackendException` naming both attempts. Exposes `Backend`, `Selection` (the
  full `GpuBackendSelection`, since 17.21.0), `ThreadingCaps` (the D3D11 driver threading caps, since 17.22.0),
  `AdapterDescription` (the adapter the device runs on, empty when the backend reports none, since 17.24.0 - the
  same value as `Capabilities.DeviceName`, which stays the single source, and on Direct3D11 it is exactly the
  DXGI adapter description), `InjectedModules` (the overlay scan result, since 17.24.0),
  `Capabilities`, and the engine-owned `IGpuDevice`. No backend object reaches the public surface (the
  transitional accessor that used to be here is gone, and `Capabilities` is read once so the context's copy and
  the device's cannot drift). Device creation and disposal are serialized process-wide behind a single static gate, on
  every backend: concurrent device creation races the Vulkan loader's dispatch setup (observed as
  `vkGetDeviceQueue` aborts on Mesa lavapipe under full test-suite parallelism), and creation/disposal are rare
  enough that the serialization costs nothing measurable. Callers see no API change, it only affects the
  interleaving of concurrent create/dispose calls across threads.
- **`IGpuDevice.SyncToVerticalBlank`** (settable, since 9.24.0) - flip vsync on a live windowed device: it
  reconfigures the main swapchain in place (no recreate, no leaked swapchain, size + depth preserved; on Metal it
  reaches `CAMetalLayer.displaySyncEnabled`). A no-op mirrored value on a headless device, which has no
  swapchain to reconfigure. `AppWindow.PresentMode` routes through it for runtime present-mode switching.
- **Completion fences** - `IGpuResourceFactory.CreateFence()` makes an unsignaled `IGpuFence`,
  `IGpuDevice.Submit(cl, fence)` signals it once the GPU has finished that submission, and `IGpuFence.Signaled`
  polls it without blocking (`Reset()` returns it for reuse). There is no blocking wait on the seam: the point of a
  fence here is to REPLACE a `WaitForIdle`, not to dress one up. A fence handed to a submission made after some
  earlier work signals only once the queue has drained through that work, which is what makes it a drop-in for the
  drain that guarded deferred GPU-resource destruction (`GpuRetireQueue`, below).
- **`GpuReadback`** - GPU-to-CPU reads. `ToRgba(gd, src, width, height)` and
  `ToRgbaMip(gd, src, mipLevel, arrayLayer, mipWidth, mipHeight)` return a tightly-packed RGBA8 buffer
  (`width * height * 4` bytes, row-major, top-left origin), and `ReadBuffer<T>(gd, src, elementCount,
  srcOffsetBytes)` is the buffer counterpart for a compute-written storage buffer. Each opens, submits and drains a
  command list of its own, so none of them may be called while a frame is recording (see `GpuRecording` above).
  - **`ToRgba`'s SOURCE must already be single-mip, single-sample `R8G8B8A8UNorm` at the size asked for.** It
    allocates a staging texture of exactly that shape and takes a WHOLE-texture copy into it, and a whole copy names
    every subresource on both sides, so anything else is not a copy a backend can narrow. A source that disagrees is
    refused with an `ArgumentException` naming the readback and what the texture actually is, before anything is
    allocated or submitted. Native Metal and Vulkan already refused it in their own `CopyTexture`, but Direct3D 11's
    `CopyResource` is silent about a mismatch, so the same call used to throw on two backends and hand back
    channel-swapped or garbage bytes on the third
    ([#83](https://github.com/APKiwiOrg/KhaozEngine/issues/83)). Resolve a multisampled target first, read one level
    of a mip chain with `ToRgbaMip`, and pass the source's own dimensions.
- **`GpuRetireQueue`** (since 17.37.0) - deferred disposal for anything a renderer frees MID-LIFE (a streamed mesh
  unloaded while the scene runs, a sprite atlas whose descriptor set fell out of the working set). `Retire(resource)`
  costs nothing at the call site, `BeginFrame()` seals the frame's retirements into one batch behind a fence and
  destroys every batch the GPU has provably finished with, and `Dispose()` flushes the tail behind one drain at
  teardown. It replaces the hand-rolled retire list each renderer used to carry
  ([#80](https://github.com/APKiwiOrg/KhaozEngine/issues/80)), so a new renderer gets the safe behaviour by
  construction rather than by remembering to copy another one.
  - **Two factories, and the choice is about WHERE your frame boundary is.** `Create(device)` is the default:
    fence-polled ripeness where the device can signal on GPU completion, the frame count plus one `WaitForIdle`
    where it cannot. Minting the fence opens a command list of its own, so it must be advanced from a point with
    nothing recording on the device (the frame's prepare phase), or the seam refuses it with
    `GpuNestedRecordingException`. `CreateFrameCounted(device, frameDelay)` never mints a fence and never drains on
    the frame path, for a renderer whose only per-frame hook is INSIDE the frame's recording. `SpriteBatch` is that
    renderer, and it passes 4. The delay has to beat the deepest the CPU runs ahead of the GPU, which on the
    engine's own backends is what `KE_METAL_FRAMES_IN_FLIGHT` / `KE_VULKAN_FRAMES_IN_FLIGHT` /
    `KE_D3D11_FRAMES_IN_FLIGHT` set (default 3, up to 16) rather than the swapchain's image count, so 4 holds at
    the default and at a depth of 4 and has to be raised with the knob past that. On this path the count is the
    whole safety argument, and too small a delay is a use-after-free rather than an artifact.
  - **The safety valve bounds the holding, and it is the only reason the fence path ever drains.** A batch on the
    fence path lives until its fence signals, so a CPU that outruns its GPU (a software rasterizer, a weak card, an
    offscreen loop with no swapchain throttling it) grew the pending list, the batch list and the barrier's fence
    pool with no limit at all ([#425](https://github.com/APKiwiOrg/KhaozEngine/issues/425)). Past
    `MaxSealedBatches` sealed batches the queue stops polling and pays ONE `WaitForIdle`, which proves every
    submitted batch complete, then frees the whole holding behind it. `Create(device, frameDelay,
    maxSealedBatches)` sets it and `DefaultMaxSealedBatches` is 8: comfortably above the deepest a PRESENTED loop
    reaches, since the present stops the CPU at `KE_*_FRAMES_IN_FLIGHT` frames ahead (default 3) and each of those
    frames has to have retired something to seal a batch. Raising that knob past 8 wants this raised with it or you
    buy a drain you did not need, and there is no way to do it from outside the engine today
    ([#661](https://github.com/APKiwiOrg/KhaozEngine/issues/661)). What actually decides whether it fires is how far
    ahead the LOOP gets rather than how fast the GPU is: an offscreen loop that submits without presenting has
    nothing throttling it and runs eight or nine frames ahead on an M2 Max, where the engine's own 400-frame churn
    test parks the peak holding exactly on the cap and fires the valve anywhere from once to a couple of dozen times
    a run, against the 396 drains the unfenced fallback pays. The count is not reproducible, since it tracks how far
    ahead that pass got. The peak on the cap is. `ValveDrains` counts the firings, and it is the honest signal that
    the CPU is running away from the GPU rather than a defect reading. `SealedBatchCount`
    is the batch-level view of the holding that the bound is written against, next to the resource-level
    `PendingCount`. The frame-counted policies need no valve and get none: a batch there dies on the frame count
    alone, which caps the holding at `FrameDelay` batches by construction, and `CreateFrameCounted` must not drain
    on the frame path at all.
  - **`FlushAll()` and `Dispose()` are TEARDOWN, and the seam enforces that.** Both drain the device and then
    destroy everything pending, so calling either with something pending while anything is recording on that
    device raises `GpuDrainDuringRecordingException` naming the open recording, and frees nothing. A drain only
    waits out work that was already SUBMITTED, so it says nothing about the draws in an open list, and the
    disposals behind it would be a use-after-free with a drain in front of it. Mid-frame, `Retire` plus
    `BeginFrame` is the pair that frees things, and neither drains. An EMPTY flush stays a no-op even
    mid-recording: there is no drain and no disposal to protect, and refusing it would break the teardown a
    capture does after the seam refuses it mid-frame (#424).
  - **Gate on `GpuCapabilities.SupportsCompletionFences`, which all three native backends answer true.** The
    native Direct3D 11 backend answers true on a real device-wide completion counter, which was its one permitted
    capability difference from the deleted incumbent: that incumbent handed out a fence on Direct3D11 and OpenGL
    that was a `ManualResetEvent` set on the CPU as the submit call returned, a submit receipt saying nothing
    about the GPU, so those members reported FALSE. Keep the gate: `CreateFence()` throws on a backend that
    answers false rather than return a fence that lies, and a caller that cannot get one keeps whatever it did
    before.
- **A shader source is compiled to SPIR-V once per process** (since 17.37.0,
  [#640](https://github.com/APKiwiOrg/KhaozEngine/issues/640)) - `CreateShadersFromSpirv` and
  `CreateComputeShaderFromSpirv` go through a process-wide memo in front of glslang, keyed on the source, the stage
  and the option set the caller compiles under. Nothing about the seam changed: the same call returns the same
  module it always did, and a caller still gets an array it owns. What changed is that a repeat is a dictionary
  lookup rather than a compile, which took `new Scene3D(...)` from 2560 ms to 21 ms on Metal, first scene in a
  process apart. It is PROCESS-wide rather than per device on purpose, because SPIR-V is device-free, so a headless
  capture that stands up a device per picture and a game that opens a second window both hit it. Bounded at 512
  distinct modules, past which it compiles without inserting (the engine ships 59 distinct modules and a `Scene3D`
  reaches 48 of them), and nothing evicts. `KE_SPIRV_CACHE` switches it off with the same five disable words the
  three backends' shader DISK caches take (`off`, `0`, `false`, `no`, `none`), for a session that needs to state
  that every module in the run came out of the compiler.
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
  - **Both `CopyBuffer` offsets, and `ReadBuffer<T>`'s `srcOffsetBytes`, must be multiples of four** (since
    17.40.0). macOS requires it of the Metal copy selector, so the seam requires it of every backend rather than
    letting the same call succeed on three and throw on the fourth
    ([#602](https://github.com/APKiwiOrg/KhaozEngine/issues/602)). An offset that is not is an
    `ArgumentOutOfRangeException` naming the side it came from. The size is unconstrained.
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
  stage/target. BOTH ALSO CHECK THE METAL BINDING ORDER (17.36.0, and `ValidateCompute` alone since 16.3.0),
  which is not a compile failure anywhere and is the one shader bug that renders a wrong picture instead of
  throwing. Metal has no binding decorations, so the cross-compiler assigns each resource an index of its own in
  first-reference order while the deleted Veldrid Metal backend bound a resource set by counting the layout in
  binding order, and a helper function that reads binding 1 before anything reads binding 0 silently swaps the two on
  Metal with Vulkan and Direct3D11 perfectly correct. One check runs over the emitted Metal, per stage:
  - **Index order, per stage, over buffers AND textures AND samplers.** Each emitted argument is joined back to
    the `(set, binding)` you declared through that stage's own SPIR-V decorations, so a swap between two
    resources OF THE SAME KIND is caught as well (two storage buffers are both `device T&` in Metal, which is
    why the 16.3.0 kind comparison could not see it). The message names both `layout(set=, binding=)` pairs and
    the slot they collided on. Since 18.0.0 the engine AUTHORS each Metal argument index by walking the
    reflected layout in binding order, and the native Metal backend binds against that same scheme, so this
    confirms the authored scheme reached the emission rather than constraining how the shader is written.

  **A second, pair-wide check lived here until #604.** It required every stage's resources to be a PREFIX of
  the layout's per index space, which is what the retired Veldrid Metal backend's one-counter-per-kind count
  over the whole layout needed. It served the engine's one-uniform-buffer-per-pipeline rule and retired with it
  ([#604](https://github.com/APKiwiOrg/KhaozEngine/issues/604)), so a pipeline whose two stages read disjoint
  uniform buffers validates clean now.

  It degrades rather than false-positives: an index space carrying an argument the join cannot resolve is
  dropped silently instead of guessed at. The engine's own shader-source tests use this to validate every
  embedded production shader, and games can validate their custom shaders the same way in their own fast test
  suites.

This package builds NO device since 18.0.0, and the containment is complete: the resource, command and device
surface is the engine-owned `IGpuDevice` / `IGpuResourceFactory` / `IGpuCommandList` interface set, the backend
implementations are the three sibling packages, and no third-party type reaches the public API (asserted by
reflection in `GpuPublicApiTests` here, and by `ArchitectureTests.ThirdPartyHomes`, which maps the five
shader-toolchain package ids (`Silk.NET.Shaderc`, `Silk.NET.SPIRV.Cross`, `Silk.NET.SPIRV` and the two `.Native`
blobs) to this package alone, and to its shader toolchain alone). Adding a backend is a new
`IGpuDevice` implementation behind an `IGpuBackendProvider`, not a consumer-visible change.
