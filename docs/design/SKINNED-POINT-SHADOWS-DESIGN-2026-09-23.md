# Skinned point-light shadow casters

Status: implemented for staged 20.5.0. Approved by the owner on 2026-09-23. Issue [#1054](https://github.com/APKiwiOrg/KhaozEngine/issues/1054).
Date: 2026-09-23.

## Outcome

A skinned draw that casts shadows contributes to every shadowed point light whose sphere it touches. A dynamic
point light receives rigid and skinned casters in its existing row, which is already cleared and rendered every
frame. A static point light keeps its cached rigid row and receives a separately rendered skinned contribution
from a compact transient atlas. Animating a character therefore does not enter the rigid caster signature, dirty
the cached row, or spend `MaxStaticRebuildsPerFrame`.

Receivers combine the two maps at each sample location by taking the nearer stored distance. A light with no
transient row takes the existing base-only shader path before any transient texture fetch. A frame with no
eligible skinned point caster therefore preserves the existing point-shadow pixels byte for byte.

This is a correctness extension of `castsShadows: true`. It adds no quality or product setting.

## Existing contracts that remain in force

- The base point atlas stays six face columns by cached light rows. Each cell stores linear distance over the
  light radius in R32Float and uses D32FloatS8UInt while rendering.
- `LightShadow.Static` caches rigid geometry. Its signature continues to contain the light, its clearances, and
  rigid casters only.
- `LightShadow.Dynamic` redraws its base row every frame selected by `MaxDynamicLightsPerFrame`.
- `castsShadows: false` excludes a skinned draw from every shadow pass. A dissolving skinned draw uses the same
  threshold and world-anchored keep rule as the colour and key-light passes. The point pass scales its noise for
  its own face resolution. The existing skinned policy was fixed by
  [#387](https://github.com/APKiwiOrg/KhaozEngine/issues/387) and is reused rather than redefined here.
- Point-shadow allocation and receiver rebinding happen at `Scene3D.Begin`, before a command list is open.
- CPU spatial decisions use absolute coordinates. GPU matrices, light positions, exclusion boxes, and model
  transforms use render-relative coordinates.
- The point receiver reads the complete structured light list. The fixed 16-entry frame UBO arrays are a
  compatibility mirror only.

## Alternatives

Scores are 1 to 10 with equal weight. Correctness includes the issue's static-cache acceptance. Memory scores
reflect a scene with one affected light under the High profile.

| Approach | Correctness | Cache isolation | Memory | Simplicity | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Redraw each affected static base row | 8 | 2 | 10 | 8 | 28 |
| Full-size second atlas | 9 | 10 | 3 | 8 | 30 |
| Compact second atlas | 9 | 10 | 8 | 6 | 33 |

| Approach | Memory | Render work | Receiver work | Decision |
| --- | --- | --- | --- | --- |
| Redraw each affected static base row with rigid and skinned casters every frame | No second atlas | Repeats the rigid world across six faces for every animated character light and turns cached statics into dynamic rows | Unchanged | Rejected because it defeats the static cache and the issue acceptance requires one rigid rebuild while only the pose changes |
| Allocate a second atlas with the full base row count and reuse each base row number | Adds another complete point atlas whenever one static light sees a skinned caster | Draws only skinned casters | One extra fetch for affected lights and simple row mapping | Rejected because Default can add 27 MiB and High can add 91 MiB for a single affected light |
| Allocate a compact second atlas and publish an independent row per affected static light | Adds one atlas row per concurrent static light that sees a skinned caster, up to the observed high-water count | Draws only skinned casters | One extra fetch for affected lights and an independent row lookup | Chosen |

The chosen form spends binding and mapping complexity to preserve both valuable resources. It preserves the
rigid cache work and makes memory proportional to the observed high-water count of affected static lights within
the current base layout, rather than allocating the full base row count for the first affected light.

## Atlas ownership and row mapping

The existing atlas is the base atlas. A second `PointShadowAtlas` contains transient skinned contributions for
static lights only. It has the same face resolution, formats, face convention, and six-column width as the base
atlas. Its height is `transientRows * faceResolution`.

Transient packing uses the transient layout for every placement calculation. Its face matrix is
`PointShadowMath.FaceViewProjection(face, transientRow, transientRows, lightRender, radius)`. Row clear and face
scissor rectangles target `transientRow` in the transient framebuffer. Passing the base row count or base row to
any of those operations would write one atlas layout while the receiver samples another and is forbidden.

Transient rows have no identity across frames. For each frame:

1. Gather point-shadow requests before skinned draw compaction.
2. After base slot acquisition, find each sampleable static request that has at least one retained skinned caster
   intersecting its shadowing shell.
3. Select up to the live transient capacity, nearest to the eye first, with static key and uploaded light index as
   deterministic ties.
4. Assign the selected requests dense transient rows `0..N-1` and clear and redraw those rows.
5. Publish `-1` for every other light. Old pixels in unassigned rows are unreachable and need no clear.

The base and transient row numbers are deliberately independent. `PointLightRecord.ShadowParams` becomes
`(baseRow, bias, slopeBias, transientRow)`. The record remains 48 bytes. The first 16
`PointShadowParams` entries mirror all four values for layout compatibility, but receiver shaders continue to
read the structured record. Host packing tests must prove that a shadowed light beyond index 15 receives its
transient row in the structured buffer.

A static light receives a transient row only when its base row is sampleable. A new static row deferred before
its first rigid render receives neither base nor transient sampling. A dirty static row that has rendered before
may keep its existing rigid image under the current rebuild budget while its skinned overlay is current.

A dynamic light always publishes `transientRow = -1`. When its base row is selected for this frame, the pass
clears that row and draws its rigid and skinned casters into the same framebuffer. Hardware depth selection
therefore produces the nearer contribution directly. A dynamic request outside the existing per-frame budget
publishes no base row and records no skinned point-shadow work.

## Receiver sampling

Every receiver family binds `PointShadowTransientMap` beside `PointShadowMap`. A 1 by 1 white R32Float default
is bound when no transient atlas exists. A new frame-tail vector describes the transient atlas texel step and row
count. It is appended after the existing point-shadow tail so all earlier frame offsets stay fixed. Every stage
that declares the shared frame block, including the foliage vertex stage, declares the same appended tail.

`ShadowParams.w < 0` is checked before reading the transient atlas or its shape. That branch calls the existing
hard or soft function unchanged. This is the no-skinned compatibility path and also the path for dynamic lights,
whose combined rigid and skinned result is already in the base row.

When `ShadowParams.w >= 0`, each logical tap selects the cube face and local face UV once. It maps that same face
UV into the base row and the transient row, fetches both point-sampled depths, and uses
`min(baseDepth, transientDepth)` as the stored distance for the compare.

- Every one of the four hard compare taps uses the minimum at corresponding face UVs.
- Every soft blocker-search tap uses the minimum before deciding whether it found a blocker and before averaging
  blocker distance.
- Every soft nine-tap filter sample uses the minimum before its depth compare.

The transient atlas uses the base face resolution. The local tap pattern is therefore identical even though the
row counts and atlas-height UV scales differ. Soft taps still select a face per direction, so crossing a cube
edge remains seamless. Bias, slope bias, blocker math, dither rotation, and compare order do not change.

Receiver resource-set replacement is one transaction over the base texture and transient texture. A set never
observes a new base texture with a stale or disposed transient binding.

## Caster preparation and culling

Point-shadow request gathering moves ahead of skinned compaction and is reused later by point scheduling. It does
not allocate an atlas or acquire rows at that earlier point. It only supplies the shadow volumes needed to decide
whether an off-camera draw must survive.

The skinned visibility decision keeps a draw when any of these are true:

- it intersects the camera frustum
- it intersects an active key-light cascade
- it casts shadows and its inflated absolute rest-pose sphere intersects a point-shadow request that can be
  considered this frame

Static point requests are considered when a base atlas is live. Dynamic requests are considered nearest first up
to `MaxDynamicLightsPerFrame`. A pending base-atlas growth may retain a draw that cannot receive a row until the
next boundary. That conservative extra upload is preferable to dropping a caster whose row becomes available in
the same frame after slot acquisition.

The point test happens before the GPU and CPU draw lists receive compacted slots. Each retained draw carries its
absolute conservative caster sphere into the compacted record. Both skinning paths use the existing
`SkinnedCullSafetyFactor` on rest bounds. The same sphere drives per-light selection after compaction, so the
retention test and the pass cannot disagree about an off-camera caster.

The outer test is sphere against light sphere. A sphere wholly inside `NearRadius` is rejected. A sphere wholly
inside a valid `ExclusionMin` and `ExclusionMax` box is rejected. A partial intersection is retained and the
fragment shader applies the exact near-radius and exclusion-box discard. Conservative bounds may cause an extra
draw but cannot remove a fragment that should cast.

An opted-out draw is not retained solely for a point shadow. A dissolving draw is retained and routed through the
dissolve-aware point pipeline on both skinning paths. A fully discarded dissolve may still issue a conservative
draw, matching the current key-light policy.

## CPU and GPU skinning paths

The CPU path reuses the deformed vertex buffer and per-draw instance buffer already uploaded for the main and
key-light passes. It draws through the point caster pipeline with its base vertex and compacted instance index.
Opaque and dissolving variants follow `ShadowDepthSelection` exactly. The pass performs no second skinning or
vertex upload.

The GPU path reuses the shared per-caster bone palette. Point work therefore participates in the decision to
prepare and upload a palette even when key-light shadows are off and a draw is outside the camera. A point-skinned
vertex pipeline reads three independently indexed inputs:

- the current point face uniform with face projection, light position, radius, render origin, noise scale, and
  clearances
- the compacted caster header with render-relative model and dissolve data
- the existing shared bone-palette slot

The face slot is per rendered light face. The caster header and palette are per retained caster. The design does
not pack a separate matrix and palette copy for every light, face, and caster combination.

The point renderer owns one pipeline family that can target either atlas because both framebuffers have the same
formats. The transient atlas adds textures and a framebuffer, not a duplicate shader and pipeline set. Static
base rows draw rigid casters only. Dynamic base rows draw rigid and skinned casters. Transient rows draw skinned
casters only.

## Coordinate correctness

Request positions, skinned caster spheres, and the CPU near-radius and exclusion tests stay in absolute world
space. Immediately before packing a point face, the light position and both exclusion corners are rebased with
the same frame origin used by caster models.

CPU-skinned instance models and GPU-skinned caster headers are render-relative. Their point depth fragments
compare render-relative world position with the render-relative light and exclusion box. Distances and the near
radius are translation invariant.

Dissolve noise reconstructs absolute position by adding `RenderOrigin` to the render-relative position before
applying `ShadowDissolveNoise.ScaleForCascade(radius, faceResolution)`. Receiver soft-shadow rotation keeps its
existing absolute-position reconstruction. A render-origin rebase therefore changes neither the caster mask nor
the receiver tap rotation.

## Lazy allocation, growth, and failure

The transient atlas is not allocated until a frame contains at least one sampleable static light with a retained
skinned caster. That frame records the required row count and publishes `transientRow = -1`. Allocation and
receiver rebinding occur at the next `Scene3D.Begin`.

Capacity uses exact high-water growth rather than geometric growth. If demand rises from one row to three, the
pending layout asks for exactly three rows. The one-row atlas remains usable on the discovery frame, so the
nearest eligible light receives its current skinned contribution and the other two take the base-only path. At
the next boundary a successful replacement makes all three rows available. A compatible base layout means BOTH
face resolution and row count are unchanged. Capacity does not shrink within that layout because shrinking would
stall and rebuild receiver sets as characters cross light boundaries.

Transient capacity never exceeds the live base row count. When the base row count shrinks, the same frame-boundary
transaction replaces the transient atlas with at most the new base row count. It keeps the lesser of the old
capacity and new base row count when demand remains, or releases the transient atlas when demand is zero. A base
face-resolution change likewise rebuilds a compatible transient atlas when demand remains, or releases it when
demand is zero. Disabling point shadows, releasing the base atlas, and disposing the scene release it too.

Allocation follows the existing build, bind, commit order:

1. Build the candidate colour texture, depth texture, and framebuffer without touching the live atlas.
2. Rebuild every receiver set against the live base texture and candidate transient texture.
3. Commit and retire the old transient atlas only after every set binds successfully.

An allocation refusal leaves the previous transient capacity live. A bind refusal disposes the candidate and
leaves both live bindings intact. With no previous transient atlas, either failure keeps the white default bound
and static skinned contributions are omitted. The base atlas, its owners, rigid signatures, and rigid rebuild
counts remain intact in every transient failure. A refused `(faceResolution, transientRows)` layout is latched so
the device is not stalled once per frame. A different row demand or face resolution clears the latch and makes
one real attempt.

When a base atlas replacement changes its face resolution or row count, the candidate receiver pair uses the new
base texture and a compatible transient replacement when that allocation succeeds. A transient allocation refusal
degrades to the new base texture plus the default transient texture rather than refusing rigid point shadows. It
does not retain an oversized old transient atlas beside a smaller new base. A receiver bind refusal still keeps
the complete old pair.

`ResolvedPointShadows.Degraded` and `Reason` report a refused transient layout even when the base remains live.
The live transient row count tells callers how much of the request can be served.

## Memory accounting

`PointShadowSettings.AtlasBytes` keeps its existing meaning. It reports the configured base-atlas floor only and
does not silently start counting a skinned feature whose live row count depends on the scene.

`PointShadowResolution` gains live `BaseAtlasBytes`, `TransientAtlasRows`, `TransientAtlasBytes`, and
`TotalAtlasBytes` diagnostics. Each atlas byte count uses the current nine-byte-per-texel contract, four bytes of
R32Float colour plus five bytes of D32FloatS8UInt depth-stencil. A transient row therefore costs:

| Face resolution | Bytes per transient row | Full profile-sized transient atlas |
| --- | ---: | ---: |
| 256 | 3,538,944 bytes, 3.375 MiB | 28,311,552 bytes for 8 rows, 27 MiB |
| 384 | 7,962,624 bytes, 7.594 MiB | 95,551,488 bytes for 12 rows, 91.125 MiB |

The compact design can reach the full second-atlas cost when every static row sees a skinned caster. It avoids
paying that cost for the common one-light and two-light cases. Exact high-water growth keeps the diagnostic equal
to the most rows requested within the current base layout rather than a geometric buffer capacity. A base shrink
caps that high-water capacity to the new live base row count.

No new `PointShadowSettings` field is added. `MaxStaticRebuildsPerFrame` cannot budget transient animation because
deferral would reuse a previous pose. `MaxDynamicLightsPerFrame` continues to budget dynamic rows only. A future
transient budget would need an explicit visible degradation policy and measured evidence before becoming a
product setting.

## Public API and tile-world seam

The existing `Scene3D.DrawSkinned` overloads already carry `castsShadows`, and their default is true. No draw API
change is needed for E4.

`ITileWorldScene.DrawSkinned` and `DrawSkinnedDissolved` currently forward to overloads whose shadow policy
defaults true. That is sufficient for Grimhollow's tile-world skinned body and door path, which needs the default
caster behavior. Adding a tile-world opt-out overload would expand the compatibility interface and every adapter
without being required by #1054. It remains separate follow-up work if a tile-world consumer demonstrates a need
to suppress a skinned caster.

The additive resolved-memory diagnostics are the only public surface in this design.

## Diagnostics

Existing `PointStaticRebuilds` continues to count base static rigid rebuilds only. Add counters for transient rows
rendered and skinned point-shadow draw calls, split between dynamic base rows and static transient rows. This
keeps the acceptance claim observable without redefining the old counter.

The diagnostic state distinguishes these cases:

- no transient demand
- demand pending its first frame boundary
- live capacity serving all demand
- live capacity serving a deterministic subset while growth is pending or refused
- no live transient atlas because allocation or binding was refused

## Verification

### Headless

- Pin dense transient row assignment, nearest-first overflow selection, and `-1` for unassigned lights.
- Pack at least 20 point lights and prove a light beyond index 15 receives its base and transient rows through the
  structured record while the first 16 UBO entries remain a correct mirror.
- Prove that changing only a bone pose schedules a static transient redraw without changing the rigid caster
  signature or incrementing `PointStaticRebuilds` after the first base render.
- Prove that a dynamic request schedules skinned draws in the base row and never receives a transient row.
- Prove off-camera retention for a caster inside a point light with key-light shadows disabled. Prove culling
  outside camera, key-light, and point-light volumes.
- Pin outer sphere, near-radius, exclusion-box, opt-out, and partial-intersection decisions against the same
  conservative caster sphere for both skinning modes.
- Pin absolute-space culling and render-relative packing across a large render origin.
- Prove zero transient allocation in a fresh scene with no eligible skinned static caster. Prove first demand is
  pending for one frame, exact high-water growth occurs at the next boundary, and an existing smaller atlas
  serves a deterministic subset during growth. Grow the base to 60 rows, then shrink it to eight and prove
  transient capacity is at most eight, including the allocation-refusal fallback. A compatible unchanged base
  may retain its high-water transient capacity after demand falls.
- Inject texture, framebuffer, and receiver-set failures. Prove the previous transient atlas or white default
  stays bound, the base cache stays live, the failed layout latches, and a different layout retries.
- Keep `PointShadowSettings.AtlasBytes` unchanged and pin base, transient, and total live byte arithmetic for
  Default and High resolutions.
- Pin frame-block offsets, the appended transient shape vector, the unchanged 48 byte structured record, all
  receiver layouts, and default texture bindings.

### GPU

- Add a committed backend golden with one static point light, one rigid caster, and one skinned caster in a bent
  pose. Both shadows must be visible. The first frame records one static base rebuild and later pose-only frames
  record zero while transient rows continue to render.
- Add a pixel readback with two keyed static lights. Give the first a rigid caster only and the second a bent
  skinned caster, so the second light's base row is one while its dense transient row is zero. Assert the skinned
  shadow lands under the second light on both CPU and GPU paths. Bake this mapping case on each native backend.
- Run the same geometry through CPU skinning with pixel readback assertions that both caster shadows are present.
- Render a `LightShadow.Dynamic` light with rigid and bent skinned casters in its base row. Assert both shadows
  appear with CPU and GPU skinning, and that no transient row is sampled. Run this pixel proof on all three native
  backends.
- On both skinning paths, prove `castsShadows: false` removes only the skinned shadow and a half dissolve produces
  a partial shadow.
- Prove an off-camera skinned caster can shadow an on-camera receiver under a point light when key-light shadows
  are off.
- Prove near-radius and exclusion-box discard for skinned fragments, including a partial caster that remains.
- Compare a large-origin scene with its near-origin equivalent and require the same skinned shadow coverage and
  dissolve mask.
- Extend the byte-identity capture so a live but unmapped transient atlas and a frame with no skinned casters both
  equal the existing base-only pixels exactly.

Shader changes update the shipped shader corpus and the Metal MSL, Direct3D11 HLSL, and Vulkan SPIR-V hash tables.
D3D11 validation pins contiguous point-skinned vertex inputs. Vulkan binding-budget and shipped-layout tests cover
the added receiver texture. The committed golden is baked and verified separately on `metal-native`,
`direct3d11-native`, and `vulkan-native` according to `docs/CROSS-PLATFORM.md`. The GPU proof must read pixels and
must not rely on successful pipeline creation alone.

After Grimhollow adopts the released engine pin, its `shadowprobe-both-shadowed` shot must show both the rigid kit
avatar and the skinned tube casting lantern shadows. The live skinned avatar switch remains gated on that shot.

## Scope boundary

E4 does not add skinned alpha-cutout materials or point-light MASK alpha testing. Skinned colour and key-light
cutout are tracked by [#1097](https://github.com/APKiwiOrg/KhaozEngine/issues/1097), and point-light MASK
cutout by [#1098](https://github.com/APKiwiOrg/KhaozEngine/issues/1098). E4 also does not add posed bounds,
a transient row quality control, or tile-world shadow opt-out forwarding. It does not change static rigid cache
identity, base row ownership, point filter settings, or the key-light shadow policy.

## Owner decision

Approve the absence of a transient row budget for the first implementation. The recommendation is yes. Correct
static-light animation then has one rule, every affected sampleable light receives the current pose, and the
compact atlas plus live byte diagnostics make the cost explicit. The worst case still adds an atlas as large as
the live base atlas, never a larger historical high-water atlas. Requiring a lower cap would need this design
revised with a prioritization rule and an explicit promise about which nearby character shadows may disappear.
