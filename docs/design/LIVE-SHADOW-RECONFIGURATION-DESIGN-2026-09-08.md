# Live shadow-map detail reconfiguration

Status: Implemented. Release pending in the shared HLOD engine batch. No engine version assigned yet. Program issue
[397](https://github.com/APKiwiOrg/KhaozEngine/issues/397). Grimhollow
[158](https://github.com/APKiwiOrg/Grimhollow/issues/158) is the active consumer.

## Problem

`ShadowSettings.ShadowMapResolution` and `ShadowCascadeCount` are committed when `Scene3D` constructs its
shadow atlas. A later write throws, which is correct because silently accepting an ignored setting was worse.
The engine still has no supported way to resize that atlas while a scene is alive.

Ruinborne can change shadow mode live, but its resolution tiers remain restart-only. Grimhollow exposes only
directional shadow-map tiers, so every quality change currently needs a restart. The requested contract is that
Low, Default, and High apply from the settings screen without rebuilding the game process.

## Decision

Add an explicit, coalescing scene request rather than relaxing committed property setters:

```csharp
public void RequestShadowMapDetail(ShadowMapDetail detail);
public void RequestShadowMapLayout(int resolution, int cascadeCount);
```

The detail overload resolves through `ShadowSettings.ForDetail` and forwards the atlas-shaping values. The raw
layout overload keeps custom consumers possible without making ordinary property assignment expensive or
surprising. Both validate with the construction-time limits. The latest request wins when several arrive before
the next scene frame.

The request applies at the next frame boundary, before any command list begins. It is never executed from a GUI
callback in the middle of rendering. An unchanged layout is a no-op.

`ShadowMapResolution` and `ShadowCascadeCount` remain guarded after commit. They continue to throw when assigned
directly. The explicit request is the only live reconfiguration door and keeps the cost visible in the API.

## Transactional GPU rebuild

The rebuild is one transaction:

1. Validate and coalesce the requested layout on the CPU.
2. At the next frame boundary, wait for submitted GPU work to finish. A settings click is rare and may pay one
   visible hitch. The ordinary frame path remains stall-free.
3. Allocate a complete replacement atlas, cascade targets, depth pipelines, and every resource set that samples
   the atlas while the old graph is still valid.
4. If any allocation or binding build fails, dispose only the incomplete replacement, retain the old graph, log
   once, and leave the committed layout unchanged.
5. Swap the complete graph, update the committed layout values, force every cascade dirty, and dispose the old
   graph after the idle boundary that already made it safe.

The temporary memory peak is accepted. Keeping the old atlas until the new graph is complete is the only way an
allocation failure can preserve a drawable scene. This is preferable to disposing first and turning a quality
change into a missing-shadow state.

## Resource ownership

`ShadowMapRenderer` keeps atlas and depth-pass ownership. Its current `EnsureLayout` work becomes an internal
replacement builder rather than an externally callable mutator.

`ModelRenderer` owns every sampling resource set and rebuilds them against the replacement atlas in one focused
method. The sweep includes ordinary rigid materials, CPU and GPU skinned materials, splat materials, tile-ground
materials, and any fallback material set. No material cache entry may retain the old atlas handle after the swap.

The scene-level method coordinates the renderer pair because neither owner can complete the transaction alone.
Atlas creation without sampling-set replacement leaves stale GPU references. Sampling-set replacement without a
complete atlas cannot build.

Cascade count changes travel through the same seam. Leaving them construction-frozen would preserve two atlas
rebuild paths and make a later cascade-quality tier repeat this work.

## State after a rebuild

- Shadow mode, strength, bias, normal offset, distance, blend, and filtering remain at their current live values.
- The committed resolution and cascade count report the applied replacement, not the queued request.
- A successful swap invalidates the shadow reuse state and records a full depth pass on the next draw.
- Blob and Off modes may resize the dormant atlas. A later switch to ShadowMap then uses the new layout.
- Scene disposal clears a pending request and disposes whichever graph is current exactly once.

## Failure and threading contract

Requests may originate on the scene thread only, matching the rest of `Scene3D`. The engine does not add locks
around arbitrary cross-thread scene mutation. A request after disposal throws `ObjectDisposedException`.

Unsupported enum values, invalid resolutions, and invalid cascade counts throw before being queued. GPU failures
surface through the scene log and retain the previous layout. The game setting may remain persisted even when a
particular device cannot allocate it, so the next boot can retry or the player can choose a lower tier.

## Tests

Headless tests:

- Direct writes to committed atlas-shaping properties still throw.
- Repeated requests coalesce to the newest layout.
- Same-layout requests allocate nothing.
- A successful request changes the committed layout only at the next frame boundary.
- A failed replacement retains the old layout and forces no dirty-state transition.
- Disposal before application drops the pending request safely.

GPU tests:

- Low to Default to High to Low produces the exact 1024, 2048, 3072, and 1024 atlas widths.
- Every transition records a fresh depth pass and keeps ordinary rigid, skinned, splat, and tile-ground receivers
  sampling a valid atlas.
- Shadow coverage remains visible across the transition.
- The test runs through the native Metal, Direct3D 11, and Vulkan gates.
- Resource lifetime validation reports no stale atlas binding or double disposal.

## Documentation and release

Update the Render3D package README and `docs/USING-KHAOZENGINE.md` with the explicit frame-boundary API and its
rare hitch contract. Close issue 397 when the verified release is merged and packed. Grimhollow remains paused on
its adoption until that release is tagged under the pinned-consumer exception.

## Non-goals

- Per-frame adaptive shadow resolution.
- Background atlas allocation on another thread.
- Hiding allocation failure by silently selecting a lower profile.
- Changing the numeric Low, Default, and High profile definitions in this round.
