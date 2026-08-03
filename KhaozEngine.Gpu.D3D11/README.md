# KhaozEngine.Gpu.D3D11

The engine's own native Direct3D 11 backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in
NO umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or
`WorldStore.Sqlite`, and nothing that does not want the Direct3D interop ever carries it.

> **Status: registration, the platform guards, the machine-capability probe, the recording model, the replay
> contract, the resource model and the fence subsystem are live.** The recording model (see "Recording, and the
> two drivers" below) lands behind `KE_D3D11_RECORD`, the replay's own rules (see "What a replay does to the
> device") land on top of it, the resource model (see "Resources, views and state objects") lands beside them,
> and the fence subsystem (see "Completion fences, and a `WaitForIdle` that drains") lands behind
> `KE_D3D11_REAL_DRAIN`, but device creation is still not built, so `CreateForWindow` and `CreateHeadless` throw
> a message saying so, and nothing shipped constructs any of them. `GpuBackendKind.Direct3D11` remains the
> working Direct3D 11 backend and stays selectable indefinitely.

## Opting in

```csharp
using KhaozEngine.Gpu.D3D11;

KhaozEngineD3D11.Register();   // once, at startup, on every OS
```

That is the whole surface. Registration is a fact about the app's WIRING, so call it unconditionally: it is
safe on macOS and Linux, where the backend simply reports itself unsupported. After the call,
`GpuBackendKind.Direct3D11Native` is creatable through the ordinary `GpuDeviceContext` entry points, and
selectable by the `d3d11-native` / `direct3d11-native` tokens through `KE_GRAPHICS_BACKEND`.

Registration is explicit on purpose. A `[ModuleInitializer]` in this package would run only when the CLR
happens to load the assembly, which a package reference with no static type use does not guarantee, and that
failure is silent and machine-dependent. Reflection probing by assembly name is trim and AOT hostile and turns
a missing reference into a runtime string mismatch. A package reference plus one line is compile-time visible,
trim-safe and testable.

Forgetting the call is loud rather than quiet: asking for `Direct3D11Native` with nothing registered throws
`GpuBackendProviderMissingException` naming the line that fixes it, and never falls back to another backend. A
run that quietly used a backend other than the one it was asked for would report its frame times, its telemetry
session header and its golden images under the wrong name.

## What the machine probe checks

`GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native)` routes to this package's own
functional probe (Veldrid cannot answer for a backend it does not implement). The probe creates a throwaway
feature level 11_0 device on the default hardware adapter, falling back to WARP, and reads
`D3D11_FEATURE_D3D11_OPTIONS` off it. Two features are hard requirements:

- **`ConstantBufferOffsetting`** - every constant-buffer bind goes through `*SetConstantBuffers1` with an
  explicit first constant and constant count.
- **`MapNoOverwriteOnDynamicConstantBuffer`** - the per-frame uniform ring is mapped `MAP_WRITE_NO_OVERWRITE`
  for the whole record phase.

A machine missing either one cannot run the backend at all. Answering that here is what routes it through the
reported fallback instead of a crash on the first frame. The probe never throws: a probe that blows up and a
probe that answers no are the same answer to the settings screen that asked, so a failure is logged with its
reason and reported as unsupported. WARP counts as supported deliberately, since it is the rasterizer the
committed Direct3D 11 goldens are baked on and the one CI pins.

An incapable machine and a missing registration are kept strictly apart: the first falls back and reports
`FallbackAfterFailure`, the second throws. Collapsing them would let a soak session silently measure the
incumbent backend and file the numbers under this one.

## Platform boundary

The assembly targets `net10.0`, **not** `net10.0-windows`, so it compiles and its device-free tests run on the
Linux and macOS CI legs and `KhaozEngine.Render.Tests` can reference it unconditionally. The Windows boundary
is carried in code instead: `KhaozEngineD3D11.IsPlatformSupported` is a
`[SupportedOSPlatformGuard("windows")]` predicate, and every body that names a Vortice type is
`[MethodImpl(MethodImplOptions.NoInlining)]` plus `[SupportedOSPlatform("windows")]` behind it. That is the
pattern `KhaozEngine.Gpu`'s driver-threading probe already proves keeps the Vortice assembly off the load path
on macOS and Linux, and with warnings as errors CA1416 makes the compiler enforce it rather than a convention.

**And one rule the guards do not state: no type here may hold a Vortice VALUE-TYPE field.** Loading a type makes
the CLR compute its layout, which resolves every value-type field and loads the assembly declaring it. A
reference field costs nothing, because a pointer needs no layout, so an `ID3D11Device` field is free while one
`Format` or `PrimitiveTopology` field is not. The test suite reflects over this assembly's types (the
emitter-shape check calls `Assembly.GetTypes`), so a single such field pulls the interop into a macOS or Linux
process and turns every off-Windows load-path assertion in the run red, reported by whichever test looked
afterwards rather than by the type that caused it. The fix is always the same and costs nothing measurable:
keep the engine value in the field and expose the Direct3D reading as a COMPUTED property.
`D3D11Texture.DxgiFormat` and `D3D11GraphicsPipeline.Topology` are both written that way, and a test asserts the
rule directly so the next occurrence names its own cause.

## No Veldrid edge

This package references `KhaozEngine.Gpu`, `KhaozEngine.Diagnostics`, `Vortice.Direct3D11` and
`Vortice.D3DCompiler`, and nothing else. It carries no `Veldrid` package reference of its own and names no
Veldrid type, which is asserted two ways: the architecture tests reject a `Veldrid*` package reference on this
project, and a reflection test rejects a `Veldrid*` assembly reference in the built IL.

The shader path still needs SPIRV-Cross, which arrives as `Veldrid.SPIRV`. That edge stays in
`KhaozEngine.Gpu`, behind an internal, Veldrid-free cross-compile helper plus `InternalsVisibleTo`.
`KhaozEngine.Gpu` already owns `ShaderValidation`, which uses precisely that static API with no device in
existence, so the helper is at home there, and it becomes the single seat for the eventual SPIRV-Cross
replacement. Blessing a Veldrid package inside a backend whose premise is being Veldrid-free would be a bad
signal that no guard would ever catch.

## Recording, and the two drivers

Command recording sits behind one internal seam, `ID3D11Emitter`: one method per `IGpuCommandList` command,
written in engine-owned handle types and never in raw COM pointers, always consumed through a
`where TEmitter : struct, ID3D11Emitter` constraint so the JIT monomorphizes it. Both recording drivers are the
same recorder with a different type argument. The deferred driver (the default) encodes each command into a
32-byte op in an engine-owned CPU stream and replays the whole stream into the real emitter inside `Submit`.
`KE_D3D11_RECORD=immediate` selects the immediate driver, which hands the recorder the device's real emitter so
the calls happen as the seam is called. Both exist until milestone M1 A/Bs them on a renderable frame, and M1
is what removes the variable and deletes the loser.

**Writing an emitter: it is a readonly struct, and its mutable state lives behind a class reference.** The
recorder stores its emitter by value, one copy per list, so under the immediate driver N lists hold N copies
over one `ID3D11DeviceContext`. Inline mutable state would be per-list on one driver and per-device on the
other. The redundancy caches of R6 describe what is bound on the CONTEXT, so two of them over one context means
one list skips a rebind that another list already invalidated, and R8's precise unbind-and-scrub on disposal
would reach only one copy. A test enforces the shape, because the failure is silent and driver-specific
otherwise.

**A tally taken at this seam is not the native-call budget.** Decision T2 gates NATIVE calls, and one seam call
fans out inside the real emitter: a resource-set bind is up to six native calls, a redundant pipeline bind is
zero, and section 9.4's one viewport plus one scissor per framebuffer CHANGE (zero for a re-bind) turns on a
guard that lives in the real emitter. So the counting emitter here gives an upper-bound input and an ordering
check. Whether the countable sink for the whole budget goes BELOW the real emitter (tallying the shipped
fan-out, no second implementation to drift) or into a device-free harness guarded by T3's WARP `[GpuFact]` is
row 9's decision, written out on `D3D11CountingEmitter`. It is deliberately not taken yet.

## What a replay does to the device

**One `ClearState` per submit, at the head of the replay, and nowhere else. That is the DEFERRED driver's law,
not the package's.** On the default driver `Begin` resets the recording and touches no device state, `End`
seals, and `Submit` takes the device's lock and replays. So N lists may record concurrently, a nested `Begin`
cannot corrupt another recording (two recorders are two arrays), and the observable order is SUBMIT order rather
than record order.

**The immediate driver is the mirror of that, clause for clause.** `Begin` calls the real emitter's `Begin`, so
the `ClearState` lands at RECORD time and there is none at the submit head. Submit replays nothing at all, so
the same list submitted twice costs one recording's calls rather than two, a second concurrent `Begin` wipes the
state and the redundancy caches the first list already emitted, and RECORD order is the observable order. Both drivers ship until M1 deletes the loser, so
neither shape may be quoted as "the native backend's contract" without naming the driver it belongs to. The
portable `IGpuCommandList` contract is unchanged at one open recording per device, which is what a consumer may
rely on, and it is written on `IGpuCommandList.Begin` and in `docs/USING-KHAOZENGINE.md`.

**`D3D11DeviceState` is what is bound on the context, and the device owns exactly one.** It carries the
redundancy caches for the seven pipeline-level objects (vertex shader, pixel shader, blend, depth-stencil,
rasterizer, input layout, topology), so a rebind of what is already bound costs zero native calls and a switch
between two pipelines that share state costs only what changed. The caches are per OBJECT rather than per
pipeline, because two pipelines routinely share a blend state or an input layout. They are reset by the one
`ClearState`, and on disposal they are scrubbed PRECISELY: the resource is unbound from exactly the slots that
named it, never by reaching for a wholesale `ClearState` that would make the next draw rebind everything.

**A pipeline handle says what it is made of through `ID3D11PipelineState`**, which is the second internal seam
in this package and the contract the pipeline type implements alongside `IGpuPipeline`. Its seven members are
typed `object` on purpose: a redundancy cache asks only whether the same instance is already bound, so reference
identity answers it without a Direct3D type appearing in the signature of the one type the device-free tests
drive hardest. It costs the real emitter nothing, because that emitter already casts to its own concrete
pipeline type and reads TYPED fields to make the call. This interface answers what changed, the concrete type
answers with what.

**Every emitter value the device hands out points at that one state object, and an emitter RECEIVES it.** The
readonly-struct rule keeps mutable state behind a class reference, but a struct that allocates its own state in
its constructor satisfies that rule and still gives each command list its own caches, which is the defect the
rule exists to prevent. So the stronger rule is enforced too: an emitter carrying device state takes it as a
constructor parameter, checked by reflection, with behavioural tests that two lists from one emitter value share
one set of caches.

**There is no `SetViewport` on the seam, so `SetFramebuffer` carries the viewport (decision W6).** A framebuffer
CHANGE replays as `OMSetRenderTargets` plus `RSSetViewports(1, full)` plus `RSSetScissorRects(1, full)`, exactly
what Veldrid's base `SetFramebuffer` auto-applies. A redundant re-bind of the framebuffer already bound emits
NOTHING, and that is a correctness rule rather than a saved call: the shipped sequence `SetFramebuffer(fb)`,
`SetScissorRect(...)`, draw, `SetFramebuffer(fb)`, draw would otherwise have its live scissor silently replaced
by the full one and the second draw would render outside the intended rectangle. A later explicit
`SetScissorRect` overrides the scissor and nothing undoes it. Two of decision T2's four structural invariants
are exactly these tallies, and they are device-free `[Fact]`s.

`D3D11NativeTraceEmitter` is how all of that is asserted without a device: it applies the shipped guards through
`D3D11DeviceState` and writes the `ID3D11DeviceContext` calls it would have made into a `D3D11NativeCallLog`
instead of making them. The guards themselves live in the state object the real emitter will use unchanged, so
what the tests pin is the shipped decision rather than a copy of it. The bind flush of decision R5 is not
modelled: a resource-set bind lands in the trace as `ResourceSetPending`, which holds its place in the order and
is named for what it is.

## Completion fences, and a `WaitForIdle` that drains

**This backend reports `SupportsCompletionFences = true`, and it is the one capability where it differs from
`GpuBackendKind.Direct3D11`.** Veldrid's Direct3D 11 fence is a `ManualResetEvent` set the instant
`ExecuteCommandList` returns, which is a submit receipt rather than a completion signal, so the incumbent
reports false, `GpuRetireBarrier.TryCreate` hands back null there and the retire pool keeps a frame-count
fallback. Here the fence is a value on a device-wide monotonic counter that the GPU advances, so the flag is
honest and the fenced paths downstream become live.

One counter per device, on either of two mechanisms, chosen once at device creation:

- **`ID3D11Fence`** via `ID3D11Device5.CreateFence`, advanced with `ID3D11DeviceContext4.Signal` and read with
  `GetCompletedValue`. Windows 10 1703 and newer.
- **`ID3D11Query(Event)`**, one per signal, polled with `DO_NOT_FLUSH` and retired in submission order, for
  anything older. Queries are recycled, so the pool stays at the number of submissions in flight.

Both are real completion signals, so nothing above the timeline branches on which one it got, and which one is
live is reported for the session log and for nothing else. Where the two genuinely differ, they report the
capability rather than the name: the monotonic fence offers a blocking wait (which is what the drain uses) and a
lock-free poll, and the event-query fallback offers neither, so a fence poll there is serialised against
submission and can wait as long as a replay. On the primary path, which is every machine from Windows 10 1703
on, a fence poll waits for nothing.

An `IGpuFence` is a remembered value on that counter and holds no device object of its own. A fresh one is
unarmed and reads unsignalled, `Submit` arms it with the value that submission raised, and `Reset` unarms it so
the next submission arms it with a strictly higher one. Submitting a fence that is still armed throws rather
than overwriting its target.

**`WaitForIdle` is a real fence drain**, replacing the empty method body the Veldrid Direct3D 11 path has. It
signals a fresh point, flushes the context ONCE so the driver actually has that signal, and then waits for the
GPU to reach it. The submit lock is held for the signal and the flush and released before the wait, so a drain
never blocks the submission that would let it finish. The wait itself is `ID3D11Fence.SetEventOnCompletion` on
the primary mechanism and a yielding spin on the fallback, and neither ever sleeps a millisecond: one such sleep
is more than the whole per-frame drain budget the drain is measured against. `KE_D3D11_REAL_DRAIN=0` restores
the no-op for the measurement window, and the drain count plus the total drain duration of each frame are
recorded so the cost is a number rather than an argument. After device death the drain returns immediately and
every fence reads signalled, since a destroyed device has no outstanding work to finish.

The fence poll does NOT flush, deliberately. Only the drain has decided to wait, so only the drain pays to have
the work handed over, and `IGpuFence.Signaled` stays as cheap as the seam's contract expects.

## Resources, views and state objects

**Every view is created at resource creation, and there are at most four per texture.** From the declared usage
bits: a full-chain shader resource view if `Sampled` or `GenerateMipmaps`, a render target view at mip 0 layer 0
if `RenderTarget`, a depth-stencil view if `DepthStencil`, an unordered access view at mip 0 if `Storage`. That
bound is a fact about the seam rather than a hope, because nothing can ask for a fifth: there is no texture-view
type, `CreateFramebuffer` takes bare textures with no mip or layer parameter, `ResolveTexture` names two whole
textures, and per-face cubemap rendering is not expressible. `ID3D11Emitter` has no `Create*` member, so
creating a view during replay is a compile error. The reason is field evidence: all 25 `DEVICE_REMOVED` stacks
the incumbent produced surfaced inside a texture-view constructor reached from resource-set activation, which is
lazy creation putting an allocation on the draw path and on the exact path a corrupted context makes fail.

The POLICY (`D3D11ViewPolicy`) decides which views and bind flags follow from which usage bits in engine types
alone, so it is tested without a device on every platform. Creating the objects is the Windows half. A
framebuffer creates and owns NOTHING, since its views already live on its attachments. A `GpuBufferRange` in a
`CreateResourceSet` description resolves to a buffer plus an offset plus a size at SET creation, never at draw
time, and both structured buffer kinds keep a full-range RAW byte-address view over a `DEFAULT`-usage buffer
with `StructureByteStride` advisory, because SPIRV-Cross emits a GLSL storage block as a `ByteAddressBuffer`.

**Registers are assigned per kind, in declaration order, and flattened in pipeline-array order.**
`UniformBuffer` takes `bN`, `Sampler` takes `sN`, `TextureReadOnly` and `StructuredBufferReadOnly` SHARE `tN`,
and `TextureReadWrite` and `StructuredBufferReadWrite` SHARE `uN`. Across a pipeline's `ResourceLayouts` array,
each set's base for a file is the sum of the earlier sets' counts for that file. The GLSL `set=` number decides
nothing. A device-free table test covers every layout the renderers declare, because a numbering error compiles,
draws and renders every pixel wrong.

**Pipelines build their blend, depth-stencil, rasterizer and input-layout objects at creation** and store them.
The input layout needs the compiled vertex shader signature, which is in hand at exactly that moment. There is
no state cache: the Direct3D 11 runtime already returns an existing object for an identical state description,
so the incumbent's was dropped.

**Disposal after device death is a no-op.** `D3D11DeviceLiveness` is a volatile token the device flips inside
its lifecycle lock before the real device is released, and every wrapper's `Dispose` reads it. Destroying the
device already freed every child object, so a wrapper disposed afterwards must do nothing rather than release
twice.

## Design

`docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md` in the engine repo, section 3 (package and layering),
section 4.1 (getting the assembly loaded), section 5.1 (the stream and the emitter), section 5.3 (emission),
section 2.1 (the recording model), section 2.3 (nested `Begin` during coexistence), section 9.4 (the implicit
viewport), section 7 (resources, views and state objects), section 8.1 (register numbering), sections 10.3 and
10.4 (fences and the empty `WaitForIdle`), and decisions P1, P2, P4, I2, R1, R2, R3, R4, R6, R8, W6, T2, X1,
X2, X3, S2, C2, C5 and C6.
