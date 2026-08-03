# KhaozEngine.Gpu.D3D11

The engine's own native Direct3D 11 backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in
NO umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or
`WorldStore.Sqlite`, and nothing that does not want the Direct3D interop ever carries it.

> **Status: registration, the platform guards, the machine-capability probe, the recording model, the replay
> contract, the resource model, the fence subsystem and the constant-buffer ring are live.** The recording model
> (see "Recording, and the two drivers" below) lands behind `KE_D3D11_RECORD`, the replay's own rules (see "What
> a replay does to the device") land on top of it, the resource model (see "Resources, views and state objects")
> lands beside them, the fence subsystem (see "Completion fences, and a `WaitForIdle` that drains") lands behind
> `KE_D3D11_REAL_DRAIN`, and the per-frame uniform ring (see "Per-frame memory: the constant-buffer ring") lands
> on top of the fences it recycles against. Those rows were built in parallel and are joined: the submit path
> raises the end-of-replay signal and brackets the ring around the replay, the fence subsystem reads the device's
> own liveness latch and answers the ring's segment gate, and the pipeline handle answers the redundancy caches.
> Device creation is still not built, so `CreateForWindow` and `CreateHeadless` throw a message saying so, and
> nothing shipped constructs any of them. `GpuBackendKind.Direct3D11` remains the working Direct3D 11 backend and
> stays selectable indefinitely.

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
answers with what. `D3D11GraphicsPipeline` implements it EXPLICITLY, since six of the seven members collide by
name with those typed properties, and every member hands back a stored field: a value built per access would
never compare equal, so every bind would report a change and the whole cache would be defeated with nothing
thrown and nothing logged.

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

**The signal is raised by the submit path, once per submit, after the last command and under the submit lock.**
`ID3D11SubmitSignal` is the one-member seam between the two: the driver submit takes it, and the submission's
fence, as an optional trailing pair, so a submit that names neither replays exactly as it always did. That is
every call site while nothing constructs a device. Placing the signal after the replay is what makes it name a
point the GPU reaches only when the submission is finished, on both drivers, and a fenceless submit signals too,
because a later fence's value covers earlier work only if the earlier work took a value of its own. A submit the
drivers reject signals nothing, and a fence handed to a submit with no sink is refused rather than left unarmed
for something to wait on forever.

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
twice. It is also the one implementation of `ID3D11DeviceLiveness`, which is the READ half the fence subsystem
was built against: a fence asks whether the device is dead so it can answer signalled, and it has no business
flipping the token, so `MarkDead` stays off that interface and the device's teardown remains its only caller.

## Per-frame memory: the constant-buffer ring

**Every `GpuBufferUsage.UniformBuffer` buffer is ring-backed, and its `IGpuBuffer` identity never changes.** The
native buffer is created `DYNAMIC` plus `CPU_ACCESS_WRITE` at its 256-aligned size times the frames in flight, so
it holds one segment per frame, and `SizeInBytes` stays the logical size the seam asked for. A record-time
`UpdateBuffer` is then a memcpy at `mapped + frameBase + offset`: no staging buffer, no copy command, no stall
and no whole-buffer requirement.

The problem it removes is measured rather than theoretical. Veldrid puts a partial write to a default-usage
constant buffer on a pooled staging path whose map blocks until the GPU is done with the buffer being recycled,
and only a whole-buffer write from offset 0 escapes it, because Direct3D 11 forbids a partial box on a constant
buffer. Zero renderer sites pass `GpuBufferUsage.Dynamic`, so every per-frame uniform buffer in the engine takes
that path by construction. A reporting client paid 22 of those blocking maps a frame at 12 to 17 ms a pass.

**The frame's base is applied AT BIND, never baked into a resource set.** That is what keeps
`CreateResourceSet`'s pinned `GpuBufferRange` valid across the 68 call sites that build one at load time: a set
still names the same handle and the same logical offset, and the bind computes
`firstConstant = (frameBase + rangeOffset + dynamicOffset) / 16` with `numConstants = roundUp(size) / 16`. A
segment stride is rounded up to 256 bytes because `*SetConstantBuffers1` wants its first constant on a
16-constant boundary, so every frame base is bindable by construction rather than by the callers happening to use
256-aligned sizes.

**The mapping belongs to the record phase, and two native calls per ring per submit is the floor.** The first
write of a record phase maps `MAP_WRITE_NO_OVERWRITE`, every later write reuses it, and the start of the next
`Submit` unmaps before anything is replayed. That is legal only because recording is deferred: Direct3D 11 has no
persistent mapping and forbids a mapped resource being bound to the pipeline, so under `KE_D3D11_RECORD=immediate`,
where draws happen during record, the mapping degrades to write-scoped (one map and one unmap per write). The
spec's phrasing for that degradation is per-FLUSH, which is coarser and strictly better and needs a flush point to
hang the unmap on. The flush point is the bind flush, which is not built yet, and
`D3D11RingAllocator.UnmapMappedRings` is the one call it needs to batch the immediate driver up to per-flush.

**A segment is recycled against a COMPLETION fence, never a submit receipt.** Frame N writes segment
`N % FramesInFlight`, and before handing that segment out the allocator reads the completion value the submission
that last used it was signalled under and blocks while the GPU has not reached it. `FramesInFlight` is 3, and
`KE_D3D11_FRAMES_IN_FLIGHT=<n>` moves it (1 to 16, an unparseable or out-of-range value warns and keeps 3). The
bet is that three segments mean this never blocks at all, so the per-frame backpressure stall count and the total
stall time are recorded: a non-zero count means the number is wrong for that machine, not that the design is. A
ring gated on a submit receipt instead would hand back a segment the moment the CPU finished asking for the work
rather than when the GPU finished doing it, and would overwrite uniforms a draw in flight is still reading, with
nothing thrown and nothing logged.

**BACKEND-DIVERGENT CREATION FAILURE: a uniform buffer combined with any other bindable usage throws here.**
`UniformBuffer | StructuredBufferReadOnly` (or either read-write structured bit, or the vertex, index or indirect
bits) is legal on the seam and is ACCEPTED by `GpuBackendKind.Direct3D11`. It is refused at creation on this
backend, because no bind other than the constant-buffer bind carries the ring's per-frame base: a structured
buffer's full-range RAW view, a vertex bind, an index bind and an indirect argument read all address byte zero, so
they would read the first segment while the uniform bind read the current one. Nothing about that is an error at
run time, it is one frame's data being read as another's. The combination is vacuous in the engine today, and the
divergence is written down here rather than left for a consumer to meet as a surprise. Create two buffers.

**Device-level `UpdateBuffer` writes the CURRENT segment**, meaning the one the next `Submit` will bind and the
one any open recording is already writing, deliberately not the one executing on the GPU. It is callable from any
thread behind a short lock scoped to the write itself and never to a frame (the submit lock, so an off-timeline
write cannot land in the middle of a replay), and it maps idempotently when it finds the ring unmapped between two
frames, leaving the mapping for the next record phase to reuse.

**What is still forbidden, restated because the ring makes it quieter rather than because it changed.** Writing
off-timeline to a range a recording has ALREADY recorded a bind for, and then expecting that recorded bind to see
the old value. It never worked, and the seam already documents that the CPU runs several frames ahead of the GPU.
For the same reason, a record-time uniform write lands the moment it is made, so two writes to the same range
inside one frame leave the second value for every draw of that frame, including draws recorded between them.
Per-draw uniforms are addressed by dynamic offset rather than by rewriting one range, which is what the renderers
already do and what makes the ring possible at all.

**Everything else is unchanged (U4).** Vertex, index and other bulk record-time payloads take the per-list
reusable CPU arena and replay as `UpdateSubresource`, since Direct3D 11 permits a partial box on a non-constant
buffer. Static and load-time `DEFAULT` buffers keep `UpdateSubresource`, and structured buffers keep `DEFAULT`
plus a full-range RAW view.

## Design

`docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md` in the engine repo, section 3 (package and layering),
section 4.1 (getting the assembly loaded), section 5.1 (the stream and the emitter), section 5.3 (emission),
section 2.1 (the recording model), section 2.3 (nested `Begin` during coexistence), section 9.4 (the implicit
viewport), section 7 (resources, views and state objects), section 8.1 (register numbering), sections 10.3 and
10.4 (fences and the empty `WaitForIdle`), section 6 (per-frame memory), and decisions P1, P2, P4, I2, R1, R2,
R3, R4, R6, R8, W6, T2, U1, U2, U3, U4, U5, X1, X2, X3, S2, C2, C5 and C6.
