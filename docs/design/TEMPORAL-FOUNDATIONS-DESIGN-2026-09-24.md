# Temporal foundations

Status: design, awaiting owner review.
Date: 2026-09-24.
Issue: [#1149](https://github.com/APKiwiOrg/KhaozEngine/issues/1149), round 1 of 3.
Consumer: Grimhollow, through round 3 of the same program.

## Outcome

`Scene3D` gains the shared machinery every temporal technique needs, built once and to a standard later graphics
work can reuse. Each frame renders through one view snapshot that carries a sub-pixel jitter for the rasteriser and
an unjittered copy for everything else. The previous frame's view is kept and survives render origin steps. Moving
draws carry a stable key, the engine remembers their previous transforms and bone palettes, and every opaque pass
can write a screen-space motion vector target. History targets live in their own owner with explicit reset rules.

Nothing is visible yet. The temporal machinery switches on only when a consumer asks for it, and with it off every
frame renders byte-identically to today on all three backends. Round 2 builds temporal anti-aliasing and upscaling
on top. Motion blur, screen-space reflections and temporal ambient occlusion are later consumers of the same parts.

## Why now

Grimhollow's distant grass shimmered as it swayed. The wind fade in 20.5.0
([#1146](https://github.com/APKiwiOrg/KhaozEngine/issues/1146)) and MSAA 4x removed the still-camera shimmer, and
the owner reports a small remainder while the camera moves. Blades 30 m and more from the camera are 1.1 to 1.7 cm
across against 3.8 cm per internal pixel, so camera motion slides them across sample positions every frame. Only
temporal accumulation removes that. The measurements are in Grimhollow's shimmer probe,
[`summary.md`](https://github.com/APKiwiOrg/Grimhollow/blob/2f7a6063/tools/SnapshotTool/perf-results/grass-shimmer-fade/summary.md).

Earlier roadmap items kept TAA out of scope until a game needed it
([#45](https://github.com/APKiwiOrg/KhaozEngine/issues/45)). This is that pull. The engine also removed a temporal
shadow cross-fade in 14.0.0 ([#233](https://github.com/APKiwiOrg/KhaozEngine/issues/233)) because moving casters
ghosted. Ghosting is the failure this program has to design out, and per-object motion is how.

## Owner decisions

Taken on 2026-09-24.

1. TAA with temporal upscaling, not TAA alone.
2. Stability over crispness when they conflict, with a light sharpen to offset softness.
3. A player quality setting that scales the internal resolution against the window.
4. Moving draws report motion through an engine-tracked caller key (approach B below).
5. Three rounds: this foundations round, the resolve and upscaler, then Grimhollow adoption.
6. Foundations at a AAA standard, reusable by later graphics work, with no time pressure.

## Approaches considered for object motion

Instances are immediate mode. They are regrouped by mesh every frame (`Scene3D.InstanceGrouping.cs`) and carry no
identity across frames (`SceneInstances.Instance`), so the engine cannot know today how any object moved.

| Criterion | A. Camera-only | B. Engine tracks a caller key | C. Caller passes previous transforms |
| --- | --- | --- | --- |
| Quality (ghosting, stability) | 3 | 9 | 9 |
| Burden on consumers | 10 | 7 | 3 |
| Engine complexity | 9 | 5 | 7 |
| Performance | 9 | 7 | 8 |
| Reuse by later effects | 2 | 10 | 7 |
| Total | 33 | 38 | 34 |

A reprojects everything with the camera and relies on history rejection for anything that moved, so wind-swayed
grass and walking characters smear or fall back to shimmer. C is correct but makes every consumer duplicate motion
state. B keeps the motion state in the engine, costs a key only on draws that move, and is how production engines
track previous local-to-world transforms. The owner chose B.

## 1. Frame view snapshot and jitter

A new internal `FrameView` is latched once per rendered frame in `RenderInternal`, immediately after `EnsureSize`,
when the camera override and the internal size are final. It holds:

- unjittered `View`, `Projection` and `ViewProjection`, render-relative, plus the unjittered absolute view-projection,
- the jittered `Projection` and `ViewProjection` used to rasterise,
- the jitter offset in internal pixels and in clip space,
- the internal width and height, the latched render origin, whether the projection is orthographic, and the frame
  index.

Consumers split along one rule. Everything that rasterises scene geometry reads the jittered matrices: rigid models,
splat terrain, tile ground, foliage, GPU and CPU skinned meshes, ground decals, water, particles, textured
billboards, beams, trails, overlay meshes, silhouettes and the sky, which takes view and projection separately.
Everything that reasons about space on the CPU reads the unjittered matrices: frustum culling, the key-light cascade
fit and its dirty-skip, point-shadow and point-light cluster work, picking, the foliage metres-per-pixel scale and
`WorldToScreen`. Target outlines and the post-chain overlays draw after the temporal resolve at display resolution,
so they read the unjittered matrices too.

This replaces the roughly fifteen places that call `FrameViewProjection()` or read the camera directly. After this
round no renderer reads the camera for raster matrices. A test enumerates the raster consumers and fails when a new
one reads the camera instead of the snapshot.

Jitter follows the Halton (2, 3) sequence, offsets in the half-open range of minus half to plus half an internal
pixel. The sequence length is 8 at native resolution and `ceil(8 * r * r)` when the display is `r` times the
internal size on each axis, so upscaled output pixels are covered evenly. The offset is applied as a clip-space
translation, `P' = P * J` where `J` is the identity with `M41 = jx` and `M42 = jy`, `jx = 2 * px / width` and
`jy = -2 * py / height`. That moves clip x and y by the offset times w, so it is the same sub-pixel shift for
perspective and orthographic projections. A test projects a known point through both projection kinds and pins
the direction and size of the shift. `GpuClip.Correct` still applies at upload exactly as today.

Jitter is zero whenever no temporal consumer is active, so the jittered and unjittered matrices are identical and
every existing golden stays byte-identical.

## 2. Previous frame state and history lifetime

At the end of each rendered frame the snapshot becomes `PreviousFrameView`. Motion is always the difference between
this frame's unjittered projection of a point and last frame's unjittered projection of the same point, so jitter
never reads as motion.

The render origin steps in exact 128 m multiples on X and Z (`WorldFrame`). When it moved by `d` between frames,
the previous render-relative view-projection is rebased as `T(d) * PreviousViewProjection` before use. Float32
represents the step exactly, so a rebase adds no error. A static scene read across a step shows zero motion.

The frame index advances once per `Begin`. A second render in the same frame, such as an offscreen capture, reuses
the frame's snapshot and does not advance history.

A new internal `TemporalHistory` owns every cross-frame target: round 2's colour history now, and motion blur or
reflection history later. It lives outside `RenderResources`, so the rebuilds that already happen there, a
distortion toggle or a bloom change, no longer discard temporal state. History resets on:

- a change of internal size or render scale, or of the anti-aliasing mode,
- a device or backend reset,
- `Scene3D.CameraCut()`, for teleports, loading screens and cutscene cuts,
- an automatic cut when the camera moves further than `Post.Temporal.CutDistanceMetres` (default 16) or turns more
  than `Post.Temporal.CutAngleDegrees` (default 60) in one frame.

A reset marks the history invalid for one frame. Consumers then treat the frame as having no previous state, and the
motion target reports zero motion. A test proves the frame after a cut matches a render from scratch.

## 3. Motion keys and previous transforms

### Public shape

- `MotionKey`, a readonly value over a 64-bit id. `MotionKey.None` is the default. `MotionKey.Combine(key, part)`
  derives a stable key for each part of a multi-part body.
- `RigidInstanceDraw` and `SkinnedInstanceDraw`, readonly descriptor structs that carry every draw knob the
  overloads carry today (mesh, transform, tint, material, dissolve, edge, shadow casting and the shadow-only and
  complement phases) plus `Motion`. `Scene3D.Draw(in RigidInstanceDraw)` and
  `Scene3D.DrawSkinned(in SkinnedInstanceDraw, ReadOnlySpan<Matrix4x4> bones)` are the new entry points.
- Every existing `Draw` and `DrawSkinned` overload stays and forwards to the descriptor path with `MotionKey.None`.
  No caller changes. A future draw knob becomes a descriptor field rather than another overload.

### Engine-side history

A new internal `MotionHistory` keeps, per key, the previous world transform and, for skinned draws, the previous bone
palette. It double-buffers current and previous maps and swaps them in `Begin`. Storage is grow-only and reused, so a
steady frame allocates nothing, and the existing allocation tests extend to it. A key submitted twice in one frame
is a collision. The last submission wins and `TemporalDiagnostics.KeyCollisions` counts it. A key not seen this frame
is dropped at the swap. A key seen for the first time has no previous state and reports camera-only motion.

A draw without a key is treated as static and gets camera-only motion. That is correct for terrain, tile ground and
placed props, which is almost everything a world draws. A moving draw that forgets its key gets camera-only motion
too, and round 2's history clamp rejects the mismatch, so a missed key shows as a softer patch rather than a smear.
The debug view in section 5 makes a missing key obvious during development.

The engine's own helpers key themselves. `CharacterAvatar` (`KhaozEngine.Game.Render3D`) owns a key for its lifetime.
`Scene3DBinder` (`KhaozEngine.Render3D.Ecs`) derives one from the entity id and version.

### Previous positions per path

| Path | Previous position |
| --- | --- |
| Rigid instances | The previous transform per key, read in the vertex shader. Unkeyed instances use the current transform |
| GPU skinned | Skinned twice, with the current and the previous palette |
| CPU skinned | Last frame's skinned vertices, kept per key |
| Foliage | The same analytic wind and interactor bend at the previous frame's time, interactor positions, focus and pixel scale |
| Splat terrain and tile ground | Camera-only, from the world position they already carry |
| Water, particles, decals, billboards, beams, trails | No motion and no depth. Round 2 treats them through a reactive mask |

The rigid instance stream is 128 bytes and uses vertex locations up to 14. A previous transform does not fit in the
instance stream within Vulkan's guaranteed 16 vertex attributes, so previous transforms go in a structured buffer
indexed by the instance's slot, as bone palettes are. The plan's first task confirms the GPU seam binds a readable
buffer to the vertex stage on all three backends. If one cannot, the fallback is a 3x4 previous transform in a
second instance stream on backends whose attribute limit allows it, recorded as a plan amendment.

The foliage uniform slot grows to hold the previous time, focus, interactors and pixel scale. Its size and alignment
follow the dynamic-offset rules the slot already obeys.

## 4. Motion vector target

- RG16F at the internal resolution, holding screen-space motion in UV units from the previous frame to this one,
  with jitter removed so consumers never see it.
- Written as a fourth colour attachment of the model pass by every opaque path in section 3's table, through separate
  pipeline variants that exist only while temporal is active. Transparent pipelines that draw into the model target
  preserve the attachment. With temporal off the attachment, the variants and the previous-transform buffer are not
  created, so nothing is allocated and nothing changes.
- Cleared to a sentinel that marks background. Sky pixels carry it, and consumers reproject them from camera rotation
  alone. Today the depth target is cleared to the background colour's red channel
  (`ModelRenderer.cs`), which cannot tell sky from geometry, so the sentinel is the reliable background mask for any
  screen-space effect.
- Temporal rendering is single-sample. Anti-aliasing resolution refuses MSAA while temporal is active, and TAA in
  round 2 replaces it.

## 5. Diagnostics and debug view

- `Scene3D.LastTemporalDiagnostics` reports the frame index, jitter phase and offset, keyed rigid and skinned draws,
  key collisions, whether history is valid, and the reason for the last reset.
- `Scene3D.DebugView`, a new enum with `None` and `MotionVectors`. `MotionVectors` activates temporal rendering and
  replaces the final image with the motion target, hue for direction and brightness for magnitude, background black.
  A missing key shows as an object painted with the camera's motion instead of its own. Later effects add views here.

## 6. Settings and activation

- `Post.Temporal`, a new settings bag beside `Post.Water` and `Post.Bloom`, holds the cut thresholds. Round 2 adds
  the resolve's settings to the same bag.
- Temporal rendering is active when a consumer requests it for the frame. In this round the only public requester is
  `DebugView.MotionVectors`. Tests request it through the internal seam. Round 2's TAA mode becomes the main
  requester.

## Out of scope

- The temporal resolve, upscaling, reactive mask, sharpening, texture LOD bias and render-scale presets (round 2).
- Grimhollow's keys and settings (round 3).
- Water motion vectors. Gerstner water is analytic and could write them, but water writes no depth and round 2
  decides whether it joins the reactive mask or the motion target.
- Motion blur, screen-space reflections and temporal ambient occlusion. They reuse this round's parts later.

## Risks

1. A raster consumer missed in section 1 draws unjittered, which round 2 would show as a one-pass shimmer. The
   enumeration test in section 1 is the guard.
2. Jitter reaching a CPU path would re-render shadow cascades every frame. A test drives temporal on and off over the
   same still scene and requires identical cascade fits, identical culled sets and a skipped shadow pass.
3. New varyings on the model programs can hit the FXC signature-hole miscompile described in
   `docs/CROSS-PLATFORM.md`. The varying layout stays contiguous and the D3D11 leg covers every variant.
4. Every new program touches the three shader hash tables and six hand-maintained shader lists. The plan names each.
5. `Scene3D.cs` is frozen by the file-size ratchet, so the new code lands in new partials and types.

## Acceptance

1. With temporal off, every committed golden is byte-identical on Metal, D3D11 and Vulkan, and no new target,
   pipeline or buffer is created.
2. Readback tests match analytic motion within 0.05 internal pixels for a moving camera, a keyed rigid object, a
   keyed GPU-skinned object, a keyed CPU-skinned object, foliage wind, an unkeyed mover (camera-only) and a static
   scene across a render origin step (zero).
3. Jitter never reaches a CPU path, per risk 2.
4. A steady frame with temporal active allocates nothing.
5. The motion target and previous-transform work cost under 0.2 ms of GPU at 1600x900 on Apple silicon for the
   Grimhollow town path, measured with Grimhollow's offscreen probe and temporal requested through the debug view.
6. `docs/USING-KHAOZENGINE.md`, the Render3D README and the CHANGELOG describe keys, descriptors, the debug view,
   `Post.Temporal` and `CameraCut`.

## Plan amendments

None yet. The implementation plan records here every place its reading of the code changes a detail above.
