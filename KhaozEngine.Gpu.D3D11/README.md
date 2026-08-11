# KhaozEngine.Gpu.D3D11

The engine's own native Direct3D 11 backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in
NO umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or
`WorldStore.Sqlite`, and nothing that does not want the Direct3D interop ever carries it.

> **Status: DEVICE CREATION IS REAL.** `CreateForWindow` and `CreateHeadless` build a device on Windows: the
> adapter choice, the `ID3D11Device` and its versioned context, one device state and one emitter context, the
> fence subsystem, the constant-buffer ring, the resource factory, the swapchain on the windowed path, the
> capability read, the device-loss latch and the debug-layer pump. Everything the sixteen rows before it built is
> joined up and reachable.
>
> **Nothing selects it by default.** It is reached by naming it: `KE_GRAPHICS_BACKEND=direct3d11-native`, or an
> explicit `GpuBackendKind.Direct3D11Native`. The `direct3d11-native` CI leg exists now and is blocking: it
> verifies the 36 shared `direct3d11` goldens on a pinned WARP adapter and runs the WARP parity `[GpuFact]` on
> its full-suite triggers. Its first recorded evidence, and the five rollout gates that decide the default flip,
> are https://github.com/APKiwiOrg/KhaozEngine/issues/460. What was checked before that leg existed still holds
> and is the whole of the device-free story: this compiles under CA1416, the construction and teardown ORDER are
> what they claim, and every subsystem below is green against its own device-free tests.
> `GpuBackendKind.Direct3D11` remains the working Direct3D 11 backend and stays selectable indefinitely.

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

## What creating a device does

`D3D11GpuDevice` is the whole backend joined up, and it builds in dependency order. Read it as the order below
rather than as the order the sections of this file happen to come in.

1. **The adapter.** `KE_D3D11_ADAPTER` is parsed, the DXGI enumeration is described, and the choice is logged as
   an INFO line naming which adapter ran and why. `warp` resolves through `DriverType.Warp` rather than through
   the enumeration, so the one value CI pins is the one value that cannot fail to resolve. A request that cannot
   be honoured WARNs and falls back to letting DXGI pick.
2. **The device.** `D3D11CreateDevice` at feature level 11_0, with `GpuD3D11DeviceFlags` OR'd against the
   `KE_D3D11_DEBUG` layer flag. A machine with no Graphics Tools answers `DXGI_ERROR_SDK_COMPONENT_MISSING`, and
   that one HRESULT retries WITHOUT the debug flag and WARNs naming the feature to install. Then
   `ID3D11DeviceContext1` is queried, because decision R7 needs the versioned context, and a runtime that cannot
   answer is refused here with a message rather than by a cast failing on the first draw.
3. **One device state and one emitter context**, handed to every emitter value the device ever makes
   (https://github.com/APKiwiOrg/KhaozEngine/issues/476). The state's bind flush is built here, so this is where
   decision R7's unset-before-set workaround is switched on for a runtime the threading probe says is EMULATING
   command lists, and for a probe that gave no answer at all.
4. **The fence subsystem**, over whichever timeline the runtime offers, with `KE_D3D11_REAL_DRAIN` resolved. The
   mechanism is logged, and a drain that is switched OFF is WARNed, because a no-op drain that nobody noticed is
   how a measurement of nothing gets published.
5. **The constant-buffer ring**, sized by `KE_D3D11_FRAMES_IN_FLIGHT` and gated on that fence subsystem.
6. **The resource factory**, with the creation gate the driver-threading probe earned. Whether creation is
   serialized is logged either way, and a probe that gave no answer serializes, the same reading of its silence
   step 3 takes.
7. **The swapchain**, on the windowed path only, and **the staging map path**, both carrying the device's
   liveness token and its device-loss latch.
8. **The debug-layer pump**, and only when the layer is genuinely active on the created device.

**Teardown is the reverse and the order is load-bearing.** The drain runs first, with no lock held, because it
refuses a caller holding the submit lock by name. Then the pump, the swapchain, the shared samplers and the fence
subsystem are released, all while the liveness token still says the device is alive, because every one of those
releases reads that token and no-ops when it says dead. The token is flipped LAST, and the `ID3D11Device` and its
context are released after that. Flipping it first (which is what the Veldrid wrapper does, correctly, since
destroying a Veldrid device frees its children) would silently skip every release and leave the device alive
holding a swapchain nobody can reach.

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
check. The budget itself is taken one level down, at `ID3D11BindSink`, which is where the countable sink went
(see "The bind flush" below).

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
what the tests pin is the shipped decision rather than a copy of it. A resource-set BIND still lands in the trace
as `ResourceSetPending`, which holds its place in the order and is excluded from the total, because a bind
genuinely issues nothing.

## The bind flush

**A resource-set bind records only, and the draw pays for it (decision R5).** `SetGraphicsResourceSet` and
`SetComputeResourceSet` issue no native call at all. They compare what they were handed against what the slot
already holds and leave it marked `Clean`, `DynamicOffsetsOnly` or `Full`, and `Draw`, `DrawIndexed` and
`Dispatch` flush every dirty slot through a pre-command hook before issuing. That is the 4.9.101 schedule ported
intact, and it is what produced the 40x shadow-encode collapse: the incumbent activated a set at the bind, so a
pass that rebinds one set thousands of times a frame paid a full activation each time.

The rest of the schedule, since each clause carries its own weight:

- The flush walks slots in SLOT order.
- `SetPipeline` DRAINS the pending sets under the OUTGOING pipeline's layouts and then FORGETS the records, before
  adopting the incoming ones, because the layout array is what numbers the registers. Flushing after the switch
  would bind the same set at different registers, which compiles, draws and renders the wrong resources. The wipe
  is the other half of the same rule: the comparison is against what the slot already holds, so a record that
  survived would mark a rebind of the SAME set at the SAME slot clean and issue nothing, leaving it at the
  outgoing pipeline's registers while the incoming one reads the new ones. **A pipeline switch therefore leaves
  every slot owing a full activation, so rebind your resource sets after one.**
- A re-bind of the pipeline already current drains nothing and forgets nothing, guarded on the layout array by
  reference, so binding a pipeline defensively between two draws costs nothing.
- A slot whose recorded set has gone null is skipped rather than unbound, and a slot goes clean only once its
  activation has landed, so a refused bind throws again on the next draw rather than once and then silently.
- Repeated marks on one slot between two draws collapse to one flush, and the flush owes the greater of them: an
  offsets-only rebind arriving over a pending full one is still a full activation.
- The bound record is KEYED by slot, one struct in an array indexed by slot, replaced in place. The hot path is
  thousands of offsets-only rebinds of ONE set, so a record that appended per rebind would make the frame
  quadratic in the rebind count.

**One native call per register file per stage (decision R6).** A full activation of the model set is FOUR native
calls from seven elements (the UBO to each of the two stages that read it, one shader-resource array covering all
four textures, one sampler array covering both samplers). The worst case in the engine is the WATER set at SIX,
also seven elements, because `WaterRenderer` declares its bathymetry texture, its ocean map, their samplers and
its dynamic UBO at `Vertex | Fragment`, so the vertex stage needs arrays of its own. Six is the bound to quote.
An offsets-only rebind pushes ONLY the dynamic constant buffers and skips textures and samplers entirely, so it
is exactly one `*SetConstantBuffers1` per visible stage. A span covers a contiguous register range with a null in
any hole, which is what keeps "one call per file per stage" true rather than nearly true.

**Every constant-buffer bind goes through `*SetConstantBuffers1` with an explicit first constant and count
(decision R7), including a full-range one.** Sending a full range through the plain `*SetConstantBuffers` is
wrong the moment the buffer is ring-backed, because the ring's per-frame base is an addend on every bind, so a
full-range bind of a ring-backed buffer still starts at a non-zero constant. The `!DriverCommandLists` workaround
stays: when the driver reports that the runtime is EMULATING command lists, the same span is unbound immediately
before the bind, because on that path a re-bind of the same buffer at a different first constant is dropped and
every draw after the first reads the first draw's constants. It doubles the constant-buffer call count there, and
both arms are asserted.

**BACKEND-DIVERGENT CREATION FAILURE: a layout element declared DYNAMIC on either structured-buffer kind throws
here.** A dynamic offset is a per-draw byte rebase, and the only bind that can carry one is the constant-buffer
bind, which takes a first constant and a count. A structured buffer binds through a view created once over the
whole buffer, and neither `*SetShaderResources` nor `*SetUnorderedAccessViews` has a per-bind window to put an
offset in, so the offset would be dropped in both directions: a full activation writes the pre-resolved view with
nothing added, and the offsets-only path skips the element entirely for not being a constant buffer. Every draw
would read the window the view was created with while the caller believed it had moved. The combination is vacuous
in the engine today (all six dynamic elements shipped are uniform buffers) and is refused anyway, because nothing
further down the path would ever say so. Declare the element as a uniform buffer, or build one set per window.

**The ring is unmapped at the flush point on the immediate driver.** `KE_D3D11_RECORD=immediate` issues draws as
the seam is called, so a ring mapped by a record-time uniform write is still mapped when the next draw binds it.
The flush unmaps every mapped ring before every DRAW and every DISPATCH, UNCONDITIONALLY rather than only when a
bind is pending: a draw with no dirty slot still draws against the constant buffers an earlier flush bound. It
does NOT unmap at a pipeline switch, because what Direct3D 11 refuses is a draw against a mapped resource and the
switch's drain issues bind calls alone, which are legal while the ring is mapped: the next draw releases it ahead
of the one command that cannot tolerate it. That is the per-FLUSH mapping the spec names as that driver's
degradation, and it is why both drivers now keep the mapping across the record phase. The deferred driver wires no
allocator into the flush at all, since its `Submit` already unmaps inside the lock it replays under.

**Where the native-call budget is taken, and what it gates.** The countable sink is `ID3D11BindSink`: the
schedule and the fan-out live in device-owned, device-free types (`D3D11BindFlush`, `D3D11SetActivation`) that
decide which calls to make, and the real emitter and `D3D11NativeTraceEmitter` supply the two ends of that one
seam. So a device-free budget measures the shipped dirty tracking, slot order, drain, register arithmetic and
batching, and can drift only in the naming translation (`D3D11NativeCallName`, which both emitters share).
Decision T3's WARP `[GpuFact]` is a belt-and-braces check on that mapping rather than the only guard.

The budget itself (decision T2) is a plain `[Fact]` suite that runs on every `dotnet test` including the cheap
Linux leg, and it is deliberately NOT named "Golden", because `cross-platform-gpu.yml` selects with
`--filter FullyQualifiedName~Golden`. What it GATES is the four structural invariants (zero `Create*`, which is a
compile error anyway, zero `Map` or `Unmap` during a replay, exactly one `ClearState` per submit, one viewport
plus one scissor per framebuffer change), the MARGINAL deltas (five distinct meshes against one, eighteen draws
against six, and an offsets-only rebind at one call per visible stage), the binding trace being identical for
eight instances of one mesh and for one, and upper bounds on the fan-out. The absolute totals are DOCUMENTATION
and may be updated freely: a test routinely edited to match reality stops being a gate, and the per-draw delta
jumping from two to eight is the fan-out defect returning.

The frame it is taken over is built to make three specific breaks visible, since a gate that cannot see a mutation
is not gating it. The per-draw set carries a texture and a sampler, so tracking that collapses to always-Full
re-pushes them per draw and moves the per-draw marginal. The per-draw window is rebound twice between two draws,
so a flush moved from the draw to the bind double-counts and moves the same marginal. And the frame ends with a
mid-frame pipeline switch carrying a pending set across it, which is the only thing that puts the drain in a
measured scenario at all: its registers are pinned by an invariant, and the incoming pipeline declares one layout,
so a drain taken under the incoming layouts throws instead of binding at the wrong register.

## The draw path, and the real emitter

**`D3D11NativeEmitter` is the type a frame renders through, and every `ID3D11DeviceContext` call in this package
is made from it.** It implements `ID3D11Emitter` and `ID3D11BindSink` over a live `ID3D11DeviceContext1`
(versioned, because decision R7 routes every constant-buffer bind through `*SetConstantBuffers1`). The device
constructs exactly ONE of them, over its one state and its one emitter context, and every command list copies
that value.

**It is deliberately the thinner of the two emitters.** Every decision it makes was already taken in a
device-free type it uses unchanged (`D3D11DeviceState`, `D3D11BindFlush`, `D3D11VertexStreams`,
`D3D11SetActivation`), which is what `D3D11NativeTraceEmitter` proves by writing down the calls it would have
made. What the real emitter carries alone is the translation into a Vortice call, and even the stage half of
that is shared: every switch is over what `D3D11NativeCallName` resolved, the same function the trace emitter
uses, so the residue is "does the arm for `PSSetSamplers` call `PSSetSamplers`" rather than "which stage's
method". Decision T3's WARP `[GpuFact]` closes that residue directly, and it has landed as
`D3D11NativeCallParityGpuTests`: it drives this emitter on a live device and reads the answer back out of the
`ID3D11DeviceContext` with the `Get*` counterparts, which is what settles the two questions no device-free test
can ask (what the Vortice array overloads do with a count smaller than the scratch they are handed, and
`OMSetRenderTargets` at a count of zero). It runs on the Windows full-suite triggers, and the 36 goldens on the
`direct3d11-native` leg run on every push, both against WARP.

**A draw flushes the resource sets first, then the vertex streams, then issues.** That order is decision R5's
rule 2 plus the batching below, and it is the same in both emitters.

**A vertex bind RECORDS and the draw issues the batch.** Two streams bound before one draw are one
`IASetVertexBuffers(0, 2, ...)`, which cannot be done once two per-stream calls have been made. The deferral is
also what makes the call possible at all, because `IASetVertexBuffers` takes the per-slot STRIDE and the stride
comes from the pipeline rather than from the bind. So a pipeline switch is part of the rule: a switch to a
different stride ARRAY marks every bound stream dirty, or the second pass draws the same buffer at the first
pass's stride and the frame is geometry noise with nothing thrown. Two pipelines sharing one stride array
invalidate nothing, and a pipeline with no vertex inputs (the fullscreen passes) declares none and re-issues
none. A flush covers the contiguous span of dirty slots, so a clean slot between two dirty ones is swept in and
rebound to what it already holds, which is the same trade the fan-out makes for a hole in a register span. The
index buffer is not deferred: there is no array form of `IASetIndexBuffer`, so it carries a redundancy cache
over the pair (buffer, format) and issues at the bind, resolving the buffer BEFORE that cache so a refused bind
leaves nothing recorded and the next identical one is refused again rather than passing as redundant.

**A disposal writes the record over its span, and that is the same rule.** R8's scrub answers with the span
between the outermost slots that named the disposed buffer, which can straddle a slot holding something else.
The emitter writes the RECORD across it, so the scrubbed slots go null and the straddled one is rebound to what
it already holds. Nulling the whole span would unbind a live stream while the record still called that slot
bound and clean, and the next draw would issue nothing at all.

**The pipeline cache key is the state object AND the argument that rides it.** `OMSetBlendState` takes a blend
factor and `OMSetDepthStencilState` takes a stencil reference, and the blend factor rides the pipeline rather
than being separately tracked state. Keyed on the object alone, two pipelines sharing one blend state and
differing only in factor would take the redundant path and the second would draw with the first one's factor,
golden-visible and silent. The key is the pair on both. Re-emitting the two calls on every pipeline bind was
the alternative and is rejected, because it makes a redundant pipeline bind cost two native calls. The sample
mask is NOT in the key: the GPU seam has no knob for one, so it cannot differ. The stencil reference is 0 on
every pipeline today for the same reason and is keyed anyway, so the day the seam grows a stencil pass the
cache is already right.

**Both framebuffer types bind through one seam.** `D3D11Framebuffer` and `D3D11SwapchainFramebuffer` have
opposite lifetimes and only `IGpuFramebuffer` in common, so an emitter casting to one of them would work for
every offscreen pass and throw on the first frame that presents. Both answer `ID3D11RenderTargetSurface`
instead, object-typed like the rest of this package's internal seams, and a framebuffer from another backend is
refused by name.

**What a resource offers a register file is the resource's own answer.** `ID3D11BindableViews` is the seam for
that, and `D3D11BindResolve` is the device-free half of the bind: it unwraps a `GpuBufferRange` to its buffer,
refuses a resource whose declared usage never earned it the view its layout asks for (naming the HLSL register
letter), passes a null through as the HOLE an array bind legitimately has, and transposes a span of binds into
the parallel arrays `*SetConstantBuffers1` takes. It is also where a REFUSAL both emitters owe lives: the
scissor index, a buffer from another backend, and the framebuffer a clear names an attachment of (none bound, no
colour attachment at that index, no depth attachment at all). A refusal kept in the real emitter alone means the
trace accepts a stream the device throws on, and neither side of that is reachable by a test here. All of it
runs under a plain `dotnet test` on macOS, which is the point: the emitter is left with a cast and a call. The
staging map asks the resource the same way, through `ID3D11MappableResource` (the native handle a `Map` names,
and whether the declared usage gave it CPU access at all), so its two refusals are device-free for the same
reason this one's are.

**The emitter is a readonly struct over two class references it RECEIVES.** The state is the device's one cache
(issue #476) and `D3D11EmitterContext` carries the device context plus the scratch arrays, grown geometrically
and reused so the path from a resource set to a native call allocates nothing once warm. The scratch lives
there rather than on the struct for a reason worth knowing before moving it back: the seam's shape scans read
the emitter's field and constructor types through reflection, and reading either resolves the type, so a
Vortice type on that surface would load the Direct3D interop into a macOS test run and take every load-path
assertion with it. Every value-typed call argument is a local or a `stackalloc`.

**A pixel-shader unordered-access bind stays refused by name.** Direct3D 11 has no per-stage setter for it
outside compute, and `OMSetRenderTargetsAndUnorderedAccessViews` is deliberately not implemented here. The
emitter inherits that refusal from `D3D11NativeCallName` rather than re-deciding it.

**So is a non-zero scissor rectangle index, and that one IS a difference from the incumbent.**
`RSSetScissorRects` takes a count and always starts at rectangle 0, so honouring an index means tracking the
whole array and re-issuing every rectangle below it. Veldrid keeps that array and this backend does not. Every
shipped call site passes zero and no shipped shader writes `SV_ViewportArrayIndex`, so the path is refused
loudly rather than scissoring the wrong output silently.

## Compute, the two ordering rules, and staging readback

**A compute pipeline is one compiled module plus its layout array, and it creates nothing native.** Direct3D 11
has no fixed-function stage behind a dispatch, so there are no state objects and no input layout to build:
`CSSetShader` takes the module the shader path already created and the register numbering is CPU-side arithmetic
over the layouts. The module's lifetime belongs to the `IGpuComputeShader` the caller created, exactly as a
graphics pipeline never disposes its shader set. The compute shader is bound UNGUARDED by any redundancy cache,
deliberately: a frame binds a graphics pipeline hundreds of times and a compute pipeline a handful, so a cache
slot for it would cost a reference compare per dispatch to save a call no profile shows.

**Compute runs the same record-then-flush-at-dispatch schedule on a SEPARATE dirty array.** A
`SetComputeResourceSet` records and issues nothing, a `Dispatch` flushes every dirty compute slot in slot order
and then dispatches, and a compute pipeline switch drains the pending sets under the OUTGOING layouts for the
same reason the graphics one does. Nothing about the schedule is special-cased for compute.

**The SRV-versus-UAV auto-unbind runs in BOTH directions, where the bind arrays are assembled.** Direct3D 11
will not let one resource be readable through a `t` register and writable through a `u` register at once, and
the GPU seam's ordering contract names this backend's mechanism in as many words: rule 1's compute-writes-then-
graphics-samples handoff works here because the backend unbinds the UAV as the SRV is bound. `D3D11ViewConflicts`
tracks every `t` and `u` register an activation issues, and a bind that conflicts with a tracked register on the
opposite file nulls it FIRST, in an array call of the same shape, inside the same activation. Three properties
are worth stating because each is a way to get it wrong:

- **One call per (file, stage), never one per register.** The unbind obeys the same O(kinds x stages) law as the
  bind, so two conflicting registers are one array call over the span. A per-register unbind would be the #418
  fan-out defect arriving on the compute side.
- **A live register swept in by the span is REBOUND to what it already holds, never nulled.** The span runs from
  the lowest conflicting register to the highest, so something else's resource can sit between two conflicts.
  Writing a null across it would unbind a live view while its owner's record still called that slot clean.
- **The owning slot is raised back to fully dirty, on whichever arm it belongs to.** That is the half a
  same-batch unbind cannot do for itself: the draw that nulls a compute set's register is not the flush that can
  put it back, so `D3D11BindFlush.Raise` marks the compute slot and the next dispatch pays it. It has to be the
  FULL state, because the offsets-only path skips both files that can conflict entirely.

Identity is the resource UNDERNEATH a `GpuBufferRange` rather than the value the caller bound, because that type
is a readonly struct implementing `IGpuBindableResource`, so a set stores it boxed and two boxes of one window
are two references. A set that binds one resource both ways at once resolves deterministically rather than being
refused: the activation issues `t` before `u`, so the UAV wins. The tracker is reset by the one `ClearState` at
the head of each replay, along with the records it raises.

**A self-conflicting set costs four array calls per flush, for ever, and that is deliberate.** The unbind raises
the slot that OWNED the register it nulled, and when a set binds one resource both ways the owner is the slot
being drained at that moment: the register is put back and re-nulled inside the same activation, so the slot
reads fully dirty again the instant the flush leaves it, and the next flush repeats the sequence (null the `u`
file, bind the `t` file, null the `t` file, bind the `u` file). It never settles. Settling it would mean silently
dropping one of the two bindings the caller declared, and Direct3D 11 cannot honour both at once whatever the
backend does, so a steady cost on a set no renderer writes is the better of the two failures. The ordinary
cross-arm case is unaffected: the other arm's next flush puts the register back and the slot goes clean. A
device-free test pins the repeating trace, so a change here has to be a decision rather than an accident.

**Rule 2 is honoured as written and adds no barrier member.** A dispatch that reads an earlier dispatch's writes
is separated by `End` plus `Submit` plus `WaitForIdle`, which is the cross-backend contract stated on
`IGpuCommandList` itself, and the ocean's `PrimeRowPass` is the shipped consumer of it. Worth naming so it is not
rediscovered: Direct3D 11 tracks hazards itself and inserts that synchronisation between dependent dispatches on
one context, so rule 2 is a Vulkan-shaped requirement being paid on a backend that does not need it. The right
resolution is a seam capability letting a consumer skip the drain where hazards are tracked, which is a seam
change plus a renderer change and is therefore outside this backend's "zero renderer changes by construction"
scope. A device-free test asserts that neither seam grew a barrier member, so the decision cannot drift quietly.

**Structured buffers keep the RAW byte-address treatment**, created by the resource path and consumed unchanged
here. SPIRV-Cross emits a GLSL storage block as a `ByteAddressBuffer` or an `RWByteAddressBuffer`, never a
`StructuredBuffer<T>`, so `StructureByteStride` stays advisory and both views are raw. Keeping this identical to
the incumbent is why the ocean's existing kernels work.

**A resolve is one `ResolveSubresource` at subresource 0 on both sides**, which is the whole of what the seam can
express: `ResolveTexture` takes two bare textures with no mip, no layer and no region. `GenerateMipmaps` goes
through the full-chain shader resource view the declared usage earned the texture at creation, and refuses by
name a texture created without `Sampled` or `GenerateMipmaps`, because `GenerateMips` is defined as reading and
writing THROUGH such a view. The copies are the region forms the seam asks for: a whole-texture copy is
`CopyResource`, a buffer copy is `CopySubresourceRegion` with a box built from both offsets, and the shorter
`CopyTextureSubresource` overload arrives at the emitter with a destination mip and layer of zero.

**Staging `Map` and `Unmap` take the submit lock for the duration of the map call and nothing longer.** The lock
is NOT held across the caller's read: a readback that held it from `Map` to `Unmap` would block every submit for
as long as a consumer walked the pixels, which is the frame-long hold this design exists to delete. Everything
that can be wrong without a GPU is device-free in `D3D11StagingMaps`: a second map of an already-mapped resource
is refused by name (Direct3D 11 answers it with a failed HRESULT and a debug-layer message, both silent in a
release build), an unmap of something never mapped is refused too (Direct3D 11 ignores it entirely), and the row
pitch is the runtime's padded stride rather than the packed row width, with the mapped size following the pitch.
Teardown and a device loss FORGET the open mappings rather than unmapping them, because after the device is gone
the mappings do not exist.

**The four native calls sit behind `ID3D11StagingMemory`, which is what makes that lock clause an assertion
rather than a promise.** It is the same shape the ring's two calls take behind `ID3D11RingMemory` and the fence's
behind `ID3D11FenceTimeline`: `D3D11ContextStagingMemory` is the Windows implementation over the immediate
context, `D3D11StagingAccess` consumes the seam, and a fake recording `Monitor.IsEntered` per call pins BOTH
halves of decision W4's staging clause off Windows (every native call under the lock, and the caller's read
between `Map` and `Unmap` not under it). A map answers its `HRESULT` across the seam untouched, so the G3 site
below is driven through the real path with a fake result rather than only against the static. Which resource a
map names, and whether its declared usage allows one at all, are answered by the resource itself through
`ID3D11MappableResource`, so both refusals stay device-free too: a cast straight to `D3D11Buffer` would be a cast
to a Windows-only type, and nothing off Windows could reach past it. The Windows residue is the four `Map` and
`Unmap` calls and nothing else. The device constructs
`new D3D11StagingAccess(new D3D11ContextStagingMemory(context), submitLock, latch)`.

**A failed map throws rather than handing back the null pointer it left behind**, and that is decision G3's
second check site. Vortice's `Map` returns its result rather than throwing, so a caller that ignored it would
read through null and report an empty readback with nothing logged. `D3D11StagingMaps.RequireMapped` is the one
place that result is interpreted: the device-loss latch is asked FIRST, before anything else at all, because
`DXGI_ERROR_DEVICE_REMOVED` is sticky and the reason is only meaningful at the first site that notices. The same
method reads the constant-buffer ring's own `MAP_WRITE_NO_OVERWRITE` result, under its own site name
(https://github.com/APKiwiOrg/KhaozEngine/issues/500), so both maps on this backend are checked. A device always
wires the latch, and a null one (every device-free test) still throws.

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
every device-free driver test, and a real device always passes both. Placing the signal after the replay is what
makes it name a point the GPU reaches only when the submission is finished, on both drivers, and a fenceless
submit signals too, because a later fence's value covers earlier work only if the earlier work took a value of
its own. A submit the drivers reject signals nothing, and a fence handed to a submit with no sink is refused
rather than left unarmed for something to wait on forever.

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

## What a telemetry session carries, and why it is cumulative

This is the only backend that answers `IGpuDevice.Counters` with anything but the default, because it is the only
one with a fence drain and a uniform ring to count. The seam value is a `GpuDeviceCounters` and it carries frames
begun, the drain count and duration (M2), the backpressure stall count and duration (M3), and the off-timeline
deferral pair, all accumulated since the device was created.

**The counters that cross are CUMULATIVE, and the per-frame rolls stay here.** `LastFrameDrain`,
`LastFrameBackpressure` and their rolls describe the frame that has ended, which is what a debug overlay wants
and what the tests assert. Neither survives being SAMPLED: a telemetry session writes a row on its own cadence,
so a per-frame value read a few times a second reports the frames it happened to land on and says nothing about
the hundreds it skipped. M3's exit criterion is a stall count of ZERO across a whole capture window, which that
reading cannot establish and a cumulative one settles by subtracting the first sampled row from the last. M2's
per-frame drain figure is the same subtraction over the frames between them. `WaitTotals` is the shared
count-and-duration pair behind `D3D11FenceSubsystem.TotalDrain` and `D3D11RingAllocator.TotalBackpressure`, and
it keeps `Stopwatch` ticks rather than a running double, because a week-long soak sums tens of millions of waits.
It lives in `KhaozEngine.Gpu` as of #531's extraction, shared with the other two native backends, and what did
NOT move with it is the counting rule: this backend counts every drain where the other two skip one that was
already caught up, and that difference is why the sites stayed per backend.

**Both backpressure readings cross, on separately named members**
(https://github.com/APKiwiOrg/KhaozEngine/issues/499). `BackpressureStallCount` is the frame boundary that
blocked on a segment the GPU was still reading. `OffTimelineDeferred` is a device-level `UpdateBuffer` that met
an in-flight segment and queued its bytes, which blocks nobody and usually happens at load time. A non-zero
off-timeline count beside a zero stall count is the specific reading you want when a load-time write is at fault:
the segment count is fine and a caller is writing off-timeline against work still in flight. Folding them would
destroy that diagnosis and make M3's criterion unreachable for a reason unrelated to pipeline depth.

**The kill switch counts nothing, in the totals exactly as in the rolls.** A `KE_D3D11_REAL_DRAIN=0` run that
reported hundreds of drains for zero milliseconds would read as a drain that costs nothing rather than as one
that never ran.

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

**Disposal after device death is a no-op.** `DeviceLiveness` is a volatile token the device flips inside its
lifecycle lock before the real device is released, and every wrapper's `Dispose` reads it. Destroying the device
already freed every child object, so a wrapper disposed afterwards must do nothing rather than release twice. It
implements `IDeviceLiveness`, which is the READ half the fence subsystem was built against: a fence asks whether
the device is dead so it can answer signalled, and it has no business flipping the token, so `MarkDead` stays off
that interface and the device's teardown remains its only caller. Both types live in `KhaozEngine.Gpu` as of
#531's extraction, shared with the other two native backends and with the Veldrid wrappers. WHERE in teardown
the flip happens is still this backend's own decision and is unchanged: it flips LAST, because every release
above it reads the token.

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
`Submit` unmaps before anything is replayed, inside the same acquisition of the submit lock that covers the replay
(an unmap that released the lock would let an off-timeline write re-map the ring before the replay bound it). That
is legal only because Direct3D 11 has no persistent mapping and does not permit a draw against a mapped resource,
so a mapping may live only across a span in which no draw happens. Under `KE_D3D11_RECORD=immediate` draws happen
during record, so that span is the run of writes between two commands, and the BIND FLUSH is what closes it: it
calls `D3D11RingAllocator.UnmapMappedRings` before every draw and every dispatch, and not at a pipeline switch,
whose drain only binds constant buffers and is legal against a mapped ring. That is the
per-FLUSH degradation the spec names for that driver, so both drivers now keep the mapping across the record phase
and `MapScopeFor` answers the same for both. Per-WRITE mapping (`D3D11RingMapScope.PerWrite`) was the interim
shape before a flush point existed, and it stays constructible and tested because it is the only scope that holds
the map, the copy and the unmap atomically. Getting the immediate driver off it matters for the measurement rather
than for the call count: milestone M1 A/Bs the two drivers on a real frame and deletes the loser, and a ring that
maps twice per uniform write on one arm and once per submit on the other measures a handicap rather than the
recording model.

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

**A DEVICE-LEVEL `UpdateBuffer` WRITES EVERY SEGMENT, A RECORD-TIME ONE WRITES THE CURRENT SEGMENT.** That split
is the resolution of https://github.com/APKiwiOrg/KhaozEngine/issues/484 and it is the one thing to know about
uniform writes here.

The off-timeline (device-level) write reaches all `FramesInFlight` segments, so a value written ONCE persists for
the buffer's life exactly as the same call persists it on the Veldrid leg, where the buffer has one copy. Reaching
the current segment alone was the shipped shape for one release, and it was a defect rather than a documentation
problem: a load-time write held only until the frame index wrapped back round, so two frames out of every three
bound memory nothing had ever written, intermittently, with nothing thrown and nothing logged. The splat-params
tail of `ModelRenderer`'s uniform buffer is the shipped consumer that writes that way, and it works here now with
no renderer change.

The record-time write is unchanged and still lands in the current segment alone, meaning the one the next `Submit`
will bind and the one any open recording is already writing, deliberately not the one executing on the GPU. The
split is the CALL rather than a usage hint on the buffer, because the call is what knows whether it happens once:
every shipped record-time uniform write is unconditional per frame, so replicating those would be `FramesInFlight`
memcpys for a value the next frame overwrites, on the one path the ring exists to make cheap.

**THE WRITE NEVER BLOCKS, AND A SEGMENT THE GPU IS STILL READING IS PATCHED INSTEAD.** Writing every segment
means writing segments an earlier frame may still be executing, which is the silent corruption the ring's fence
gate exists to prevent, so a segment whose owner fence has not completed does not receive the copy. The byte range
and a private copy of the data are queued on the ring, and the `BeginFrame` that next opens that segment applies
them, right after the gate has proved the GPU is done with it. One bounded hold of the submit lock, at most one
completion poll per call, no spin and no retry, on any thread and at any pipeline depth. At load time nothing has
been submitted, so every segment takes the copy and no completion read happens at all.

Waiting for those segments was the first shape and it STARVES, which is worth knowing before anyone proposes it
again. In the GPU-bound steady state the frame thread submits again for every frame the GPU retires, so at least
one non-current segment is always in flight and "wait until they are all free" is never satisfiable: the writer
chases the pipeline forever, burning a core. A deferral waits on a frame boundary that is going to happen anyway.

**What you get is EVENTUAL CONSISTENCY, which is exactly the persistence the Veldrid leg gives.** When the call
returns, every segment either already holds the write or holds a pending patch its next acquire applies, so any
segment BOUND after the call carries the value. The window in which an in-flight segment still holds the old bytes
is unobservable through the seam, because that segment is not bound again until it has been acquired, and
acquiring it applies the patch first. Patches for one ring are replayed in arrival order, so overlapping ranges
resolve last-write-wins exactly as direct copies do, and a segment already carrying a patch takes every later
write as a patch too rather than letting an older queued write land on top of a newer copy. Storage is bounded by
coalescing (a new patch drops every earlier one whose range it fully covers) on top of the shape of the caller:
off-timeline uniform writes are load-time or settings-change writes by construction, since the per-frame ones go
through the record-time path.

**Two off-timeline writers to one buffer are unordered per segment**, and that is outside the v1 contract in the
same decision W5 family as the record-time overlap below. Each call is atomic in the sense that all of its copies
and deferrals happen in one hold of the lock, but two overlapping calls may land in different orders in different
segments, because one segment can take a copy while another takes a patch replayed later. A single off-timeline
writer, which is what every shipped caller is, sees strict last-write-wins everywhere. A caller that already holds
the submit lock is legal, since with no wait inside there is nothing to deadlock on.

**The CURRENT segment is always copied**, deliberately and unchanged by the fix. Gating it would change the
documented semantic that the write lands when it is called and the next submitted list reads it, and deferring it
would be worse still, because it is bound by the very next submit while its own next acquire is a whole wrap away.
It never carries a pending patch of its own: patches are recorded for non-current segments only, and the frame
boundary that makes a segment current drains it in the same lock hold that publishes it. The exposure that leaves
is the exposure this call already had and is the forbidden case restated below. Against a concurrent RECORD-TIME
write to the same range the current segment is last-write-wins and unordered, which W5 already places outside the
v1 contract. The other segments are not contended at all, because a record-time write never touches them.

The write still maps idempotently when it finds the ring unmapped between two frames, leaving the mapping for the
next record phase to reuse. Under `D3D11RingMapScope.PerWrite` the map, every segment's copy and the unmap are
one critical section, which is the same atomicity that scope holds for a record-time write, and a patch replay
repeats it in its own hold. The deferrals are reported on their own cumulative `OffTimelinePatches` stat
(`Deferred`, `Applied`, `Coalesced`, the `Dropped` a disposed ring takes with it, and the `Outstanding`
difference) rather than on the per-frame backpressure, because they are not frame-boundary stalls and folding
them in would make M3's "zero stalls across the window" criterion unreachable for a reason unrelated to pipeline
depth. Every deferral leaves the queue exactly once, so `Outstanding` settles rather than climbing, and a number
that does keep climbing means patches are piling up for a segment nothing acquires.

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

## The shader path: GLSL to HLSL to our own FXC call

**GLSL 450 stays the single source and the backend calls FXC itself.** `CreateShadersFromSpirv` and
`CreateComputeShaderFromSpirv` cross-compile through the internal, Veldrid-free SPIRV-Cross helper in
`KhaozEngine.Gpu`, then compile the emitted HLSL to `vs_5_0` / `ps_5_0` / `cs_5_0` with
`Vortice.D3DCompiler` at optimization level 3. DXC is not an alternative and not a preference: DXC emits DXIL,
Direct3D 11 consumes DXBC, and there is no supported DXC path to DXBC, so Shader Model 6.x is unreachable from
this backend at all. `SpirvLocalSize` still hand-parses the workgroup size out of the module, because D3D11
takes it from the module while the seam's `IGpuComputeShader.ThreadGroupSize*` has to report it.

Owning the FXC call is what buys the three things below.

**The cross-compile options are pinned, and the emitted HLSL is hashed.** `HlslCrossCompilePin` states the exact
option set every HLSL emission in the engine runs under, with the citation from the Veldrid fork behind it, and a
device-free test hashes the emitted HLSL of all 34 shipped graphics programs and 8 compute kernels against a
checked-in table. The table is baked from THIS path, so what it pins is drift away from that bake rather than
agreement with the incumbent Veldrid path. Agreement was measured once, at review time on 2026-08-03: all 34
graphics programs emitted byte-identical HLSL under both, which is what lets the committed Direct3D 11 goldens
carry over without a rebake. The table is what keeps that true afterwards. One program's hashes moving is a
shader edit. Thirty moving at once is an option drift, which is exactly what the table exists to catch. `KE_UPDATE_HLSL_HASHES=1` re-bakes the table, which is a test-maintenance knob for a deliberate shader
or option change and is never set on CI, where the whole point is that the table does not move on its own.

**Compiled modules are cached on disk.** Keyed on the whole program's GLSL sources, the FXC profile, the FXC
flags, the pinned cross-compile options, the `Veldrid.SPIRV` version that emitted the HLSL
([#610](https://github.com/APKiwiOrg/KhaozEngine/issues/610), read off the assembly the process loaded rather
than out of the props file) and the engine version, under
`<local-app-data>/KhaozEngine/d3d11-dxbc/<engine version>/`. The key covers the WHOLE program rather than one
stage, because a pair is cross-compiled together and the emitted vertex HLSL is a function of the fragment source
too. Every failure is a miss: a cache that cannot be read or written is a slower start and nothing else. Set
`KE_D3D11_SHADER_CACHE` to a directory to relocate it, or to any of `off`, `0`, `false`, `no` or `none`
(case-insensitive, whitespace trimmed) to compile fresh every time. The disable words are a set rather than `off`
alone because any OTHER value is taken as a directory path verbatim, so a narrower vocabulary would quietly turn
`KE_D3D11_SHADER_CACHE=0` into a cache directory named `0` beside whatever the process's working directory is.

**A holed vertex input signature fails loudly instead of corrupting a frame.** SPIRV-Cross drops a vertex input
the vertex stage does not read, and names each survivor `TEXCOORD<location>`, so dropping the middle of a
declared range holes the emitted signature, and FXC plus WARP miscompile a holed signature silently. It has cost
this engine two production incidents: the shadow depth pass corrupted WARP so the main model and splat passes
rendered no colour, and the terrain blew to flat white. The workarounds in the GLSL stay, and are now asserted
from three directions. The shader path checks every module it compiles, pipeline creation re-checks the vertex
bytecode an input layout is about to be validated against (so a module served from the disk cache is checked
too), and `KhaozEngineD3D11.ValidateShaderPair` / `ValidateComputeShader` run the same path with no device at
all, which is what the Windows CI leg calls over every shipped program on every push.

Those two validation entry points are the half `KhaozEngine.Gpu`'s own `ShaderValidation` cannot cover: it
cross-compiles to all four shading languages and stops, so it has never compiled the HLSL it produced, which is
precisely how both incidents got past it. They are Windows-only (FXC is `d3dcompiler`), so gate the call on
`KhaozEngineD3D11.IsPlatformSupported` and run both.

**`KE_D3D11_DEBUG` compiles shaders with debug information and no optimization**, so a RenderDoc or PIX capture's
disassembly maps back to the emitted HLSL. The flags are part of the cache key, so a debug session and an
ordinary one never see each other's compiled bytes. The same variable ALSO switches on the Direct3D debug layer
and its info-queue pump (decision G4, below). One variable, two effects, deliberately.
## The swapchain: present, resize and framebuffer identity

**v1 keeps the LEGACY BLIT swapchain, reproduced from the incumbent field for field (W1).** Unversioned
`IDXGIFactory` off the adapter, `BufferCount = 2`, `Windowed = true`, `SwapEffect.Discard`,
`SampleDescription(1, 0)`, `B8G8R8A8_UNorm` non-sRGB, `Usage.RenderTargetOutput`, the
`MakeWindowAssociation(IgnoreAltEnter)` that stops DXGI toggling fullscreen behind the windowing layer, and a
present at sync interval 1 or 0 with no other throttling. Flipping vsync live reconfigures nothing, because on
Direct3D 11 the interval is an argument of `Present`, so there is no swapchain to recreate and none to leak.

There is deliberately no flip model, no `ALLOW_TEARING`, no waitable frame-latency object and no pacing. The
swapchain is the ONE area of this backend that no automated test anywhere can see: the goldens are headless, the
shape tests are device-free, and the WARP leg never presents. A flip model is therefore validated only by a human
looking at a window, which is exactly the evidence class that produced the Windows black screen and the fork's
resize hazard, so all of it is one sequenced follow-up with its own manual validation. Do not modernise the
description in place.

**That costs one measurement, and this is where the caveat is recorded so a soak capture is not misread.**
Because v1 carries the incumbent's blit present path unchanged, it CANNOT discriminate whether the blit model is
the mechanism behind the frame-pacing defect on issue #380. A native soak that reproduces #380 unchanged is
consistent with "blit causes it" and with "blit does not", so it proves nothing either way. The discriminating
measurement is the same scene on the flip-model prototype, A and B against this path on the same machine and the
same build.

**The swapchain framebuffer's identity NEVER changes, and a resize swaps its views underneath it (W2).** It is
`D3D11SwapchainFramebuffer`, a different type from the offscreen `D3D11Framebuffer`: the offscreen one aggregates
views that already live on engine textures and never changes, this one wraps a backbuffer the runtime hands back
and takes away again. The incumbent disposes the depth texture and the whole framebuffer and builds a new object
on every resize, which is why `VeldridGpuDevice.ResizeSwapchain` re-wraps only on a reference change, a workaround
whose comment names the Windows black screen after going fullscreen, maximising or drag-resizing. Owning the
wrapper deletes that workaround's reason to exist, and it makes Direct3D 11 behave the way Metal already does,
which is the behaviour the rest of the engine was written against. `Outputs` is fixed at construction and a resize
never touches it, so every pipeline built against the swapchain survives every resize.

Stable identity has one consequence worth knowing: W6's guard compares framebuffer REFERENCES, so what makes a
resize visible to the context is the single `ClearState` at the head of the next submit, whose reset clears the
bound framebuffer and forces the next `SetFramebuffer` to re-issue the render targets and the full viewport at the
new size. A resize only ever lands at a present boundary, so that always happens before anything binds again.

**`ResizeSwapchain` queues the size and returns, and the submit thread applies it at the next present boundary
(W3).** It takes no lock and touches nothing native, so a window callback on any thread returns immediately even
while the submit thread is mid-replay, and a foreign-thread resize during recording becomes structurally
impossible instead of contractually forbidden. Sizes are coalesced to the LAST requested, so a drag-resize burst
costs one `ResizeBuffers` per frame rather than one per event. The cost is one frame of resize latency. The apply
lands AFTER the present, because `ResizeBuffers` discards the backbuffer contents and resizing first would throw
away the frame that had just been rendered.

**The present's `HRESULT` is returned rather than discarded**, which is the seam the device-loss latch needs to
check at the fault site: the incumbent discards it, and a discarded device removal surfaces frames later as an
unrelated crash. A failed present also skips the queued resize, so the caller receives that `HRESULT` instead of a
throw out of `ResizeBuffers` against a device that has just gone. Naming the removal, calling
`GetDeviceRemovedReason` and surfacing it in the session header are the latch's, below.

The four native calls sit behind `ID3D11SwapchainSurface`, the same shape the ring memory and the fence timeline
have, so the queue, the coalescing, the boundary, the apply order, the sync interval and the framebuffer identity
are all device-free `[Fact]`s. The resize is three members rather than one on purpose: `ResizeBuffers` fails while
any outstanding reference to a backbuffer survives, so releasing the views first is a correctness rule the
incumbent depends on silently, and splitting it puts that order where a test can assert it.

The release does one thing the incumbent does not: it unbinds the output-merger before it disposes the views.
`ResizeBuffers` fails on INDIRECT references too, and the immediate context holds one, since `OMSetRenderTargets`
takes its own reference on the render target view and keeps it after the wrapper is disposed. The incumbent is
immune by accident, because it resets the context state at the end of every submit, while R3 puts this backend's
one `ClearState` at the head of a replay instead, so the last frame's targets are still bound when a resize
applies at the present boundary. That half is not device-free testable, because a fake surface has no context to
inspect, and its evidence is a real window resize on the WARP leg.

## Capabilities, adapter selection, the debug layer and device loss

Decisions G1 to G4. Everything in this section is engine logic over four interop calls, so nearly all of it is
device-free `[Fact]`s that run on macOS and Linux.

**Capability parity with the incumbent, except one member (G1).** `GpuCapabilities` reads the same here as on
`GpuBackendKind.Direct3D11` field for field, with `SupportsCompletionFences` true rather than false (decision
C5). Five of the nine members are CONSTANTS of the feature levels this backend requires rather than device
answers: `ClipSpaceYInverted` false, `DepthRangeZeroToOne` true, `SamplerAnisotropy` true, `SamplerLodBias` true
and `SupportsCompute` true. The four a device answers are `DeviceName` (the DXGI adapter description, cut at the
first NUL because it arrives out of a fixed 128-wide-char buffer, and otherwise raw: the incumbent does not trim
the vendor's padding either, and the two strings are compared character for character), `MaxMsaaSampleCount` (the
MIN over `R8G8B8A8_UNORM`, `R32_FLOAT` and `R32G8X24_TYPELESS` via `CheckMultisampleQualityLevels`, walked
DOWNWARD from 32 because the supported counts are not required to be contiguous, and any query failure yields 1),
`SupportsShadowMaps` (`CheckFormatSupport(R32_FLOAT)` for `RenderTarget | ShaderSample`), and
`SupportsCompletionFences` (from the fence subsystem, so the capability and the fence path cannot disagree).
The depth format is the TYPELESS sibling on purpose: the incumbent's depth-flagged `D32_Float_S8_UInt` becomes
`R32G8X24_Typeless` in `D3D11Formats.ToDxgiFormat` before it queries, so both backends ask the driver about the
same DXGI format and the parity assertion is satisfiable by construction.

`MaxMsaaSampleCount` is the member the parity assertion exists for. A different answer changes what
`AntiAliasing.ResolveFor` picks, which changes the field look and the golden output, and it would neither throw
nor log.

**An out-of-range MSAA request THROWS at texture creation (C4)** rather than rounding down. The engine already
has the one place a request is meant to be clamped, so a count arriving at `CreateTexture` above
`MaxMsaaSampleCount` came from a caller that skipped it, and honouring it by rounding down would hide that behind
a framebuffer that is quietly not multisampled.

**The sampler's four hardcodes are reproduced and its two degradations are dropped (G1).** No comparison
function, minimum LOD 0, maximum LOD `uint.MaxValue`, transparent-black border colour: those four are hardcoded
because the incumbent hardcodes them and the committed goldens were baked through them, and the seam exposes none
of them so a caller cannot ask for anything else. The incumbent's anisotropic-to-trilinear fallback and its
forcing of `MipLodBias` to 0 are NOT reproduced, because both read capabilities that are constants here, so both
branches are unreachable and carrying them would mean shipping a fallback nothing can enter.

**And the device's shared sampler pair is WRAP on all three axes, which is a fifth hardcode (G1).** It comes
from `D3D11SharedSamplers`, not from the engine's `GpuSamplerDescription.Point` / `.Linear` statics, because
those statics default every axis to CLAMP while the incumbent's built-in pair (Veldrid's
`SamplerDescription.Point` / `.Linear`, which `GraphicsDevice.PointSampler` and `LinearSampler` are built from)
wraps. Same names, opposite behaviour, and renderers assume wrap: `ModelRenderer` says so in writing. Building
the pair from the statics is what moved `scene3d_texbillboard` (worst 0.393) and `scene3d_particles_flipbook`
(worst 0.359) on CI run 30963173087, both scenes whose sampling leaves [0,1] by design.
`NativeVsVeldridCapabilityParityTests` now compares the pair against the incumbent's built-ins field by field,
device-free, so a drift fails on every OS rather than only on a Windows golden.

**`KE_D3D11_ADAPTER=warp|hardware|<index>|<name substring>` pins the adapter (G2).** Unset leaves DXGI to pick.
A request that cannot be honoured WARNs and falls back to the default enumeration, never fails, and the warning
lists the adapters that WERE enumerated, because a name substring is machine-specific and "nothing matched"
without the list sends the reader to check their spelling when the machine is usually what changed. There is no
unrecognized VALUE: anything that is not `warp` or `hardware` and does not parse as an integer is a name
substring. `warp` is resolved through `DriverType.Warp` rather than through the enumeration, so the one value CI
pins cannot fail to resolve on a machine whose factory enumerates no software adapter. The selection policy
decides over a list of descriptions and flags, so it is device-free tested, and only the enumeration itself is
Windows-only.

The reason it exists is CI integrity. The Windows golden leg runs on WARP only because `windows-latest` carries
no hardware adapter and DXGI falls back, so a runner image that grew a paravirtual adapter would silently change
the rasterizer the 36 committed goldens are compared on, and the failure would arrive as a diff on unrelated
goldens with nothing naming the cause. `DXGI_ADAPTER_FLAG_SOFTWARE` is recorded in the telemetry session header
as `softwareAdapter`, read off the CREATED device rather than off the choice, so it is right on the default path
where nothing in the engine picked the adapter at all.

**`KE_D3D11_DEBUG=1` adds `D3D11_CREATE_DEVICE_DEBUG` and pumps `ID3D11InfoQueue` into the engine log (G4).**
Corruption and error severities are raised to WARN, which is a deliberate ceiling rather than an oversight: ERROR
is for something the engine could not do, and a debug-layer message is a diagnostic about something that already
happened, so logging it at ERROR would make a debug session look like a broken engine and would put a row in a
consumer's error-rate telemetry for every diagnostic run. The pump is rate limited by three caps, per frame, per
repeated message and per session, and a cap that suppresses says so exactly once, because a limiter that silently
drops is worse than none at all in a crash investigation. The per-repeat cap is the one that does the real work,
since the layer's characteristic failure is one mistake reported once per draw call. A machine without the
Windows Graphics Tools feature answers `DXGI_ERROR_SDK_COMPONENT_MISSING`, which is retried without the flag plus
a WARN naming what to install, rather than refusing to start on someone who is by definition mid-diagnosis.

`KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS` keeps its exact semantics, including its value parsing and its
unrecognised-value warning. It lives in `KhaozEngine.Gpu`'s `GpuD3D11DeviceFlags` and is untouched. There is no
frame-capture integration: RenderDoc and PIX attach externally and need nothing from the engine.

**A device loss reports why, at the site that noticed (G3).** `DXGI_ERROR_DEVICE_REMOVED` is sticky, so the
reason is only meaningful at the FIRST site that sees it. The latch is handed the `HRESULT` after `Present`,
after every staging `Map` and after replay, and on `DXGI_ERROR_DEVICE_REMOVED` or `DXGI_ERROR_DEVICE_RESET` it
calls `GetDeviceRemovedReason` immediately, records it with the site, flips the liveness token so every later
release is a no-op, logs an ERROR line saying what the reason means, and hands the session header a stable token
plus the site as `deviceLossReason`. It latches exactly once: later sites answer "the device is gone" and change
nothing, because the first site is the only one near the cause.

An ordinary failure is deliberately NOT a device loss. A latch that fired on every failing `HRESULT` would kill
the device on a plain `DXGI_ERROR_INVALID_CALL`, after which every release is a no-op and nothing anywhere says
why.

There is a FOURTH site, which arrives as a throw rather than as an `HRESULT`: the swapchain's resize apply calls
`ResizeBuffers`, `GetBuffer` and `CreateRenderTargetView`, all of which end in `CheckError`
(https://github.com/APKiwiOrg/KhaozEngine/issues/489). `CheckAfterFault` covers it by asking the device for its
removal reason directly, and a false answer means the throw was something else and the caller must go on treating
it as its own fault. The residual fact that issue records is structural and unchanged: a throw between the view
release and the recreate leaves the framebuffer pointing at released views, and there is no rollback available,
because holding the old views across the resize is precisely what `ResizeBuffers` forbids. The latch IS the
repair, since once the device is known dead nothing binds again.

**Who calls all of this.** The device: the adapter enumeration and the choice run before `D3D11CreateDevice`,
the capability read takes the created device and the adapter it landed on, the debug-layer flag is OR'd into the
creation flags (with the retry arm for a machine that has no Graphics Tools installed), the latch sits at its
four check sites, and the pump is built only when the layer is actually active and drained at the submit
boundary. The Windows `ID3D11InfoQueue` reader is `D3D11InfoQueueMessages`, which reads through Vortice's own
`Message GetMessage(ulong)`, the managed form of the two-pass `GetMessageW` call, so the marshalling is the
binding's rather than this package's.

## Threading: the shipped contract

**This section is the contract. It is the authoritative statement of what this backend guarantees about threads,
and every XML doc in the package that mentions a lock points here rather than restating it.** Decision W4, and
its boundary W5.

**What is guaranteed.**

- **Recording appends with no lock, and the one exception is the map.** `Begin` truncates a per-list array and
  every seam call appends to that same array, so the command path itself takes no lock and makes no native call.
  The exception is a record-time `UpdateBuffer` on a ring-backed uniform buffer: the FIRST such write since the
  last unmap has to acquire the ring's mapping, which takes the submit lock for the duration of the `Map` call
  and nothing longer, once per ring per record phase. Every write after that one is a copy into already-mapped
  memory with no lock and no native call at all. Two recorders are two arrays, so a nested `Begin` cannot corrupt
  another recording.
- **One `_submitLock`, held for microseconds, covers exactly three things: the replay, the present, and the
  resize apply.** That is the whole of it. There is no frame-long monitor, so there is nothing to exit from a
  foreign thread, no `Map` queued behind a whole frame, and no lock-recursion leak to fix. The lock belongs to
  the device and is passed to the pieces that need it, so there is one of it rather than one per subsystem.
- **`Submit` order is the observable order.** The lock is taken ONCE for the unmap, the replay, the end-of-replay
  signal and the segment bookkeeping, so two submits cannot interleave, and the timeline value a submission
  signals orders the same way its commands reached the device.
- **Device-level `UpdateBuffer` is callable from any thread**, behind that same lock scoped to one pass over the
  ring's segments and never to a frame, so an off-timeline write cannot land in the middle of a replay. It reaches
  every segment and it never waits for any of them: a segment an earlier frame is still reading gets a pending
  patch that the segment's next acquire applies. A caller who already holds the lock re-enters for free. See the
  ring section above.
- **Resource creation is free-threaded, serialized behind a short creation lock when the driver reports
  `DriverConcurrentCreates` false.** That is `D3D11CreationGate`, the second and last lock in the package, taken
  around one native creation call and nothing longer. A driver that creates concurrently gets no lock at all, and
  an UNKNOWN answer (the threading probe did not run, or could not answer) serializes, because the probe degrades
  to unknown on every failure and its silence is not a licence. Five factory members are ungated by design, in two
  groups: the four that create no native object (framebuffer, resource layout, resource set, command list), and
  `CreateFence`, which creates none either, because the device's one timeline object was created with the device
  and a fence here is an engine-side target against it. `CreateComputePipeline` creates no native object either
  and IS gated anyway, to keep the two pipeline members symmetric and because a pipeline is the member most
  likely to grow a native call later.
- **The two locks nest in one direction only.** The submit lock is OUTER, the creation gate is INNER, and the
  gate is a strict leaf: nothing is acquired while it is held. A creation path that one day needs the immediate
  context takes the submit lock BEFORE entering the gate, never inside it.
- **Staging `Map` and `Unmap` take the lock for the duration of that one call and nothing longer.** Two calls
  take it twice, and between them the mapped pointer is the caller's alone, so a readback never holds it across
  a consumer's walk over the pixels. This clause is TESTED rather than asserted in prose: the four native calls
  sit behind `ID3D11StagingMemory`, and a fake recording `Monitor.IsEntered` per call pins both halves of it off
  Windows. See the compute section for the seam.
- **Nothing waits under the submit lock, with ONE knowingly paid exception**, and exactly two members CAN block
  unboundedly, both of which refuse a caller who holds it, by name: `WaitForIdle` (which signals and flushes
  under the lock and then releases it to wait, so the submission it is waiting for can still be made) and the
  ring allocator's `BeginFrame` (which waits for the GPU to finish with the segment it opens, up to a frame).
  Both throw rather than stall, because a frame-long hold of this lock is invisible from the outside and is the
  exact defect the design deletes. The ring allocator's `UpdateBuffer` is deliberately NOT a third one: it has
  no boundary of its own to be moved outside the lock, so rather than waiting for a busy segment it defers the
  write to a patch the next frame boundary applies, which is what makes a caller holding the lock legal there
  instead of refused. The exception is the staging map above: `Map(READ)` on the immediate context is DEFINED
  to wait until the GPU is done with that resource, which is exactly what makes a readback correct without an
  explicit drain, and the wait is bounded by the work already submitted against that one resource rather than
  by a frame. The alternative, mapping with `DO_NOT_WAIT` and spinning outside the lock, trades a bounded wait
  for a spin that can starve.
- The process-wide `GpuDeviceContext._lifecycleGate` is unchanged, and still serializes device creation and
  disposal across every backend.

**What is NOT in the contract (W5): concurrent RECORDING.** The design structurally permits it, because `Begin`
touches no device state and each recorder owns its own arrays, so two threads recording two lists will probably
work. It is not supported in v1 and it is outside this contract: nothing in the engine asks for it, no test
exercises it, and the redundancy caches and the constant-buffer ring have not been reviewed for concurrent
record. Concretely, under the deferred driver's mapping scope the map acquisition named in the recording clause
above is the only serialized step and the COPY runs with no lock held, so a record-time write racing a submit's
unmap, or racing a device-level write, or two record-time writes to one ring, are all outside the contract rather
than serialized. Do not read "it will probably work" as a guarantee: that is the shape that produces a bug report
nobody can triage. Shipping it properly is https://github.com/APKiwiOrg/KhaozEngine/issues/463.

**The immediate driver records ON the device, so the recording clause is the deferred driver's.** Under
`KE_D3D11_RECORD=immediate` (the M1 fallback lever) a seam call issues its native call as it is made, so
recording touches device state by construction and one thread records. That arm narrows the contract rather than
extending it.

**No open ends left in this contract.** Device-level `UpdateTexture` takes the same short lock as
`UpdateBuffer`, scoped to the write and never to a frame, and the staging clause is `D3D11StagingAccess`'s. Both
are stated in the contract above rather than promised.

## Design

`docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md` in the engine repo, section 3 (package and layering),
section 4.1 (getting the assembly loaded), section 5.1 (the stream and the emitter), section 5.3 (emission),
section 2.1 (the recording model), section 2.3 (nested `Begin` during coexistence), section 9.4 (the implicit
viewport), section 7 (resources, views and state objects), section 8 (the shader path) with 8.1 (register
numbering), 8.2 (pinning the cross-compile options) and 8.3 (the holed-signature hazards), sections 10.1 and
10.2 (compute, the two ordering rules and MSAA), sections 10.3 and
10.4 (fences and the empty `WaitForIdle`), section 6 (per-frame memory), sections 9.1 and 9.2 (the swapchain,
the present path and the resize), section 9.3 (threading), section 11 (capabilities, diagnostics, WARP and
device loss), and decisions P1, P2, P4, I2, R1, R2, R3, R4, R6, R8, W1, W2, W3, W4, W5, W6, T2, U1, U2, U3,
U4, U5, X1, X2, X3, S1, S2, S3, S4, S5, C1, C2, C3, C4, C5, C6, G1, G2, G3, G4 and T4. The recorded non-measurement
M5 in section
13 is the swapchain's, and its wording is reproduced above rather than referenced, because a reader of a soak
capture will not have the design doc open.
