# Private synchronous Metal frame-pool probe

NEVER MERGE THIS ARCHIVE. This is an owner-approved diagnostic spike, not a production API proposal.
It changes no shipping default, shader, quality setting, resource retain or recording contract.

## Provenance and question

The production source base is `465e16b27e0be1e52428d69b2c7b41ce992330d5`. The parent prepared
`probe/mac-frame-pools` at `a6db43e2d880f5101591bba747e84c1c8cfec1b1`, including the buffered recorder
through `3e170ad54` and `a6db43e2d`. This archive extends that recorder to schema 2.
The earlier [late-output probe record](mac-frame-late-output-probe.md) describes schema 1 and its original evidence.
This document owns the pool-probe schema and mode.

A direct same-quality pair was 137.9666 to 139.2714 FPS, below the 144 target. A later CPU trace
attributed 12.7142% of sampled render-thread residence to `ObjCAutoreleasePool.Enter`. Sampled residence
is not pure CPU time or a forecast of a cadence gain. Profiled FPS is not evidence for an unprofiled FPS
benefit. The owner reported no visible foliage in that baseline view. Preserve the view.

## Opt in and scope

At `AppWindow.Run` startup, `KE_FRAME_DIAGNOSTICS_NATIVE_POOL_BATCHING` is read once. Missing or exact
`0` disables batching. Exact `1` enables it. Every other supplied string, including empty or whitespace,
fails before recording. Enabled mode requires native Metal on macOS and fails clearly on other devices.
Keep `KE_FRAME_DIAGNOSTICS_RENDERER_INITIALIZES_SWAPCHAIN=1` in both comparison modes.

The parent prepares identical private binaries for both runs. Set the following environment values
on the parent's human-launched client command, with a fresh absolute result path in an existing directory.
These are environment examples only, not client launch commands.

```sh
# Off
export KE_FRAME_DIAGNOSTICS_NATIVE_POOL_BATCHING=0
export KE_FRAME_DIAGNOSTICS_RENDERER_INITIALIZES_SWAPCHAIN=1
export KE_FRAME_DIAGNOSTICS_PATH=/tmp/khaozengine-mac-frame-performance/pool-probe/off.json
# On, in the separate comparison run
export KE_FRAME_DIAGNOSTICS_NATIVE_POOL_BATCHING=1
export KE_FRAME_DIAGNOSTICS_PATH=/tmp/khaozengine-mac-frame-performance/pool-probe/on.json
```

After the client is foregrounded, settled, and showing the unchanged baseline view, the parent triggers
that run by creating its result path plus `.start`.

```sh
touch /tmp/khaozengine-mac-frame-performance/pool-probe/off.json.start
# For the on run instead
touch /tmp/khaozengine-mac-frame-performance/pool-probe/on.json.start
```

The lexical `MetalFramePoolProbe.Scope` wraps only the synchronous `FramePhases.Run` call on rendered
frames, including prepare, recording, capture, submit and present. It closes before pacing and before the
render callback returns, including exceptional exits. It never spans an await, stored recording lease,
queue-batch lifetime or later frame. Nested explicit scopes share the native root and enforce same-thread
LIFO disposal. Default and disabled scopes do nothing.

Inside that scope, local `ObjCAutoreleasePool.Enter` calls return empty scopes. Outside it, including other
threads and worker tasks, nested per-call native push/pop behavior is preserved. All state is non-flowing
thread-local state. The root is published only after successful acquisition and cleared before native pop,
so native teardown reentry uses standalone pools. Explicit retains and releases remain unchanged.
Off macOS the native path is inert and does not load libobjc. The internal native-call seam exists only
for deterministic failure and reentry tests in this archive.

## Schema 2

The same binary counts in both modes. Every baseline and end boundary contains `timestamp`, `window`,
GPU `counters`, and `nativePools`. Every sample contains the original timestamps, unclamped loop delta,
monotonic interval, preceding callback's `completedFrameWindow`, next `boundaryWindow`, GPU `counters`,
and `nativePools`. `window.nativePoolBatching` is the actual startup-resolved mode. A suppressed frame
is still marked suppressed, so it cannot be accepted as a rendered comparison interval.

The `nativePools` cumulative fields belong to the render thread and include work through the preceding
completed callback, at the same boundary as GPU counters. They cover this backend's wrapped pool calls,
not unrelated Cocoa or third-party pools.

| Field | Meaning |
|---|---|
| `nativePushCount` | Returned native pool-push calls, including a failed nil result |
| `nativePopCount` | Returned native pool-pop calls |
| `bypassedLocalScopeCount` | Local pool entries bypassed while an explicit frame scope was active |
| `explicitFrameScopeCount` | Successfully entered enabled explicit scopes, including nested scopes |

`summary.nativePoolDelta` subtracts baseline from end for all four fields. These are call counts, not GPU
execution timings. Existing GPU drain, backpressure and acquire durations remain CPU waiting durations.
The stable-window check also requires unchanged actual pool mode at every boundary, including a temporary
change that returns to baseline.

The recorder retains the 10-second duration, 4096-interval cap, monotonic calculations, nearest-rank
percentiles, trigger polling before sampling, discarded initial partial interval, and prior-callback
counter pairing. Warmed sampling allocates nothing. It reads no environment, does no file IO, reads no
native timestamps and adds no GPU work. Final serialization runs asynchronously after the final boundary.
Atomic publication refuses to overwrite an existing result and disposal joins an already started writer.

## Evidence boundary

Focused CPU tests cover native call order, nesting, unwind, reentrant drain, failed acquisition,
following-frame recovery, deterministic thread isolation, task context non-flow, and warmed allocations.
Native macOS tests use a real `NSObject` subclass whose `dealloc` callback is the lifetime witness.
They also check standalone drains and an explicitly retained object surviving the outer drain.

Off/on render tests reuse the existing full-scene fixture with geometry, HDR, opaque and transparent
backgrounds, AA off and TAA Native. Both frame outputs and the pre-present same-frame capture must match
every RGBA byte. Readback after the frame pool drains verifies retained output ownership. The real
frame-history fixture additionally exercises a layer-backed device, clip ring, fences, worker encoding,
screenshot, freeze and detach, comparing the screenshot and first clip-frame encoded bytes.
No renderer seam or production renderer change is needed.
The cross-assembly public probe bridge is archive-only. Shipping public-surface assertions are not
relaxed for it, and this branch is not a candidate for the production package or API gates.

The scratch implementation report records exact commands, exits, counts, limitations and the tested DLL
SHA256 values. The parent owns independent lifetime/capture review and runtime measurement. No FPS
benefit is claimed by this archive. No live client, profile, benchmark, stress run or full suite is part
of the implementation proof.
