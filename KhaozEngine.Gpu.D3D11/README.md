# KhaozEngine.Gpu.D3D11

The engine's own native Direct3D 11 backend for the [KhaozEngine.Gpu](../KhaozEngine.Gpu) seam. Opt-in and in
NO umbrella: a consumer adds this package explicitly, the same pattern as `Physics.Bepu` or
`WorldStore.Sqlite`, and nothing that does not want the Direct3D interop ever carries it.

> **Status: registration, the platform guards, the machine-capability probe, the recording model and the fence
> subsystem are live.** The recording model (see "Recording, and the two drivers" below) lands behind
> `KE_D3D11_RECORD` and the fence subsystem (see "Completion fences, and a `WaitForIdle` that drains") behind
> `KE_D3D11_REAL_DRAIN`, but device creation is still not built, so `CreateForWindow` and `CreateHeadless` throw
> a message saying so. `GpuBackendKind.Direct3D11` remains the working Direct3D 11 backend and stays selectable
> indefinitely.

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
check. Whether the countable sink goes BELOW the real emitter (tallying the shipped fan-out, no second
implementation to drift) or into a device-free harness guarded by T3's WARP `[GpuFact]` is row 9's decision,
written out on `D3D11CountingEmitter`. It is deliberately not built yet.

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

## Design

`docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md` in the engine repo, section 3 (package and layering),
section 4.1 (getting the assembly loaded), section 5.1 (the stream and the emitter), section 2.1 (the recording
model), sections 10.3 and 10.4 (fences and the empty `WaitForIdle`), and decisions P1, P2, P4, I2, R1, R2, T2,
C5, C6 and X3.
