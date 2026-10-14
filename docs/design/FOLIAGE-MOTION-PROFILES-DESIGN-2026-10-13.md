# Foliage motion profiles, movement-driven shiver and animated foliage shadows

Date 2026-10-13. Issue [#1377](https://github.com/APKiwiOrg/KhaozEngine/issues/1377). Status: approved by the
owner on 2026-10-13. Target: rides staged 20.34.0. First consumer: Grimhollow's four bush archetypes, after the owner chose
an engine feature over a game-only opt-in (Grimhollow ruling B1, 2026-10-13).

## Problem

Animated foliage has one motion model, built for grass blades. Evidence is at `origin/main` ec20a2b61.

1. **One lean per instance.** `FoliageVert` computes one bend vector per mesh instance from its root against the
   wind and up to four interactors, caps the combined horizontal bend at `0.65 * height`, weights it by the squared
   normalised height above the mesh's lowest point and lowers the tip to keep the arc length
   (`KhaozEngine.Render3D/Internal/ShaderSources.Foliage.cs`, displacement block after `rootWeight`). On a blade that
   reads as grass parting. On a 1.3 m shrub the whole crown shears up to 0.85 m and sags, which reads as rubber.
2. **No shadows.** `GroundCoverGpuCache.CanRetain` requires `!options.CastsShadows`
   (`KhaozEngine.Terrain.Render3D/GroundCoverGpuCache.cs`), and `TileWorldView`'s `_animatedFoliageOptions` never
   sets casting (`KhaozEngine.TileWorld.Render3D/TileWorldView.AnimatedFoliage.cs`). The CPU fallback can cast but
   performs no wind or interactor deformation (`KhaozEngine.Terrain.Render3D/GroundCoverRenderer.cs`). A tall
   animated prop therefore loses its shadow.
3. **No sense of movement.** The bend depends only on the interactor's position. A player standing still inside a
   shrub holds it bent forever, and nothing reads as leaves rustling while something pushes through.

## Goal

A shrub leans a little away from whatever moves through it, shivers its leaves while that thing moves, settles
when it stops, sways less in the wind than grass and casts a shadow that moves with it. Grass, and every existing
consumer that does not opt in, behaves and renders exactly as today.

## Non-goals

- Sound. No collision, physics or authoritative state.
- Per-vertex authoring weights. Motion stays derived from height above the mesh's lowest point, so existing meshes
  need no new attributes and stems at the base stay put by construction.
- Short ground-cover generation and density rules.
- Displaced point-light shadows (ruling S5).
- A tunable shiver frequency (ruling S6).

## Design

### 1. `FoliageMotionProfile` (KhaozEngine.Render3D)

An immutable value of four numbers, packed into one per-instance `vec4`:

| Field | Meaning | Blade (legacy) | Valid range |
|---|---|---|---|
| `BendScale` | Multiplier on the interactor lean | 1 | 0 to 4 |
| `MaxLean` | Cap on the combined horizontal bend, as a fraction of the instance height | 0.65 | 0 to 1 |
| `WindScale` | Multiplier on wind sway | 1 | 0 to 4 |
| `Shiver` | Leaf shiver amplitude at full agitation, as a fraction of the instance height | 0 | 0 to 0.25 |

`FoliageMotionProfile.Blade` is today's grass motion and the default wherever no profile is given. `MaxLean` stays at
or below 1 because the tip-lowering term takes `sqrt(height^2 - bend^2)`. Validation rejects non-finite values and
values outside the ranges with an exception naming the field.

The zero value must never silently freeze foliage. The public surface resolves an absent profile to `Blade`, so
`default` is unreachable from consumer code. The plan picks the mechanism (a nullable carrier or storage relative
to `Blade`) under that constraint.

### 2. Where the profile enters

The profile travels **per instance**, as a fifth instance `vec4` (80 to 96 bytes per retained foliage instance). It
is assigned before multipart expansion, so every part of a model moves together. Patches already group by mesh,
so per-instance data needs no extra draws, no per-profile uniform slots and no grouping change, and it serves the
native world path unchanged. The cost is 16 bytes more per retained foliage instance, about 20 percent of foliage
instance memory, accepted to keep one shader and one batch path (ruling S1).

The public seams, from lowest to highest:

- **Render3D.** `FoliageInstance` carries an optional profile, resolved to `Blade` when absent.
- **Terrain.Render3D.** `GroundCoverRenderOptions` gains a model-keyed profile map. A ground-cover model id with no
  entry is `Blade`.
- **TileWorld.** `TileWorldViewOptions` gains `AnimatedFoliageStyles`, a map from archetype id to
  `AnimatedFoliageStyle(FoliageMotionProfile Motion, bool CastsShadows)`. `AnimatedFoliageArchetypes` remains the
  only selection. A style for an id outside the set is refused with an `ArgumentException` naming the id. Two
  selected archetypes that share one mesh must share one style, and a conflict is refused the same way. A selected
  archetype with no style behaves exactly as today. Changing styles invalidates the animated split the way changing
  the selection does, limited to splits that draw a changed archetype.
- **Native world.** Native foliage (world-authoring R7, then the shared native rendering path in R8) reaches
  retained foliage through the same Render3D and Terrain seams, so it inherits profiles and casting without a
  second mechanism. The R7 plan consumes these seams instead of adding its own motion model.

### 3. One displacement function

Displacement is factored into a single GLSL function shared by `FoliageVert`, the temporal motion variant and the
new foliage depth shader (ruling S4). Colour, motion vectors and shadows can then never disagree.

- **Wind.** Today's wind bend, multiplied by `WindScale`.
- **Lean.** Today's per-interactor push, multiplied by `BendScale`. The combined wind and lean bend is capped at
  `MaxLean * height` instead of the constant `0.65 * height`. The tip-lowering term is unchanged.
- **Shiver.** The shiver intensity is the strongest single interactor's `falloff * Strength * Agitation`, where
  `falloff` is the existing root-distance falloff, and the amplitude is `Shiver * height * intensity` (ruling S7). The offset is a horizontal oscillation at a fixed angular frequency of
  about 14 rad/s (about 2.2 Hz, ruling S6), whose phase mixes the instance root position and the vertex's local
  position, so different parts of the crown move out of step and read as leaves rather than a rigid wobble. It is
  weighted by the same squared height weight, so the base stays put, and it is added after the lean cap, so a fully
  leaning shrub still shivers.

With `Blade` (1, 0.65, 1, 0) the arithmetic reduces to today's. Acceptance is that every existing foliage golden
passes unchanged on all three backend legs. If a backend's compiler contracts the new terms into different
rounding, the implementation keeps the legacy expression on a uniform branch for `Blade` rather than re-baking
goldens.

### 4. Agitation: rustle only while moving

- `FoliageInteractor` gains `Agitation`, from 0 through 1, validated like `Strength`. It defaults to 0, meaning at
  rest, so an existing interactor still leans grass exactly as before and shivers nothing (ruling S3).
- A new allocation-free CPU helper, `FoliageAgitation`, turns a tracked position into agitation. Each frame it takes
  the interactor position and the frame time. Agitation rises with horizontal speed (full at a configurable
  reference speed, default 2 m/s) and decays to rest over a configurable settle time (default 0.6 s) once movement
  stops. A jump beyond a configurable distance in one frame (a teleport or respawn) resets it to rest instead of
  spiking. Ruinborne and any other consumer get the same behaviour without writing their own.
- The foliage uniform slot is full today (160 current bytes plus 96 temporal history bytes). It grows to carry
  current and previous agitation for the four interactor slots, one `vec4` each. The temporal variant evaluates the
  shiver for the previous frame with the previous time, interactors, strengths and agitation. History reset (first
  frame, or temporal rendering switching on) behaves as today: previous equals current.

### 5. Shadows

- Retained animated foliage may cast **cascade** shadows. Casting is per instance (from `AnimatedFoliageStyle` in
  TileWorld, or the instance carrier at the Render3D and Terrain seams), and patches group by casting as well as
  mesh, so the depth pass draws only casting patches. `CanRetain` stops refusing casters. The CPU fallback is
  unchanged.
- A foliage depth vertex shader calls the shared displacement. It uses the **camera's** wind-fade pixel scale, not the
  light's, so the shadow moves exactly as the visible mesh does. Alpha cutout is honoured.
- Shadow candidates are gathered independently of camera-frustum rejection, so a shrub just off screen still shades
  the ground in view. Patch bounds are padded by the maximum displacement,
  `(MaxLean + Shiver + WindScale * WindStrength) * height`.
- Animated casters never enter static or cached shadow content. They draw in the per-frame caster set.
- **Point lights (ruling S5).** Casting animated foliage draws into point-light shadows at its rest pose, through the
  existing rigid caster path with its instance transforms, so static point-shadow caching keeps working. Shrub
  motion is a few centimetres of shiver and at most about 20 cm of lean, so the mismatch is small. Displaced point
  shadows become a follow-up issue if a consumer shows that it matters.
- `Blade` grass keeps casting off by default, so no existing consumer pays shadow cost it did not ask for.

### 6. Compatibility

Additive, so a minor change riding 20.34.0. `AnimatedFoliageArchetypes`, positional `FoliageInteractor` construction
and `GroundCoverRenderOptions` stay source compatible, and every new parameter is optional. Consumers rebuild on a
pin change, so binary layout changes are not a concern. Ruinborne uses none of these APIs today (it uses `PropLayer`
casting), so it is unaffected.

## Tests

Headless unless marked GPU.

- Profile validation, `Blade` defaults, packing into the instance `vec4`, instance stride and vertex layout.
- Uniform layout for the grown slot, current and previous agitation, and previous-state propagation only while
  temporal rendering is on.
- TileWorld styles: refusal outside the selection, the shared-mesh conflict, unchanged behaviour for selected
  archetypes without a style, split invalidation on a style change, and casting routing.
- `FoliageAgitation`: rise with speed, decay over the settle time, teleport reset and zero allocation.
- The CPU displacement oracle (`FoliageWindMirror`) extended with the lean cap, wind scale and shiver, plus the
  shader parity and corpus checks (`MotionShaderTextTests`, `ShippedShaderPrograms`).
- GPU: existing goldens unchanged (`Blade` identity). A shrub profile's lean never exceeds `MaxLean`. Shiver moves
  upper vertices and leaves the root fixed. Shiver is zero at zero agitation. Motion vectors for a shivering shrub
  match the oracle. A casting animated shrub's shadow follows its displacement. Non-casting grass adds no depth
  draws. Goldens are baked by the backend CI legs per [`CROSS-PLATFORM.md`](../CROSS-PLATFORM.md).

## Grimhollow adoption (consumer lane, after the engine release)

- Add `bush`, `bush_small`, `bush_copse` and `bush_copse_2` to `AnimatedArchetypes` with a shrub style. Starting
  values `BendScale` 0.3, `MaxLean` 0.15, `WindScale` 0.4, `Shiver` 0.035, casting on, tuned through the owner's
  look review.
- Feed the avatar interactor's `Agitation` from `FoliageAgitation`.
- This needs a Grimhollow pin that includes 20.34.0. That pin's adoption lane also sweeps the rest of 20.34.0,
  including the #438 complete-footprint contact changes, and rebakes navigation as the game's engine bump ritual
  requires.

## Rulings

Orchestrator rulings. The owner may overrule any of them.

- **S1.** The profile is per instance, not a per-batch uniform, to keep batching and one shader path.
- **S2.** Motion stays derived from height above the mesh's lowest point. No new mesh attributes.
- **S3.** Agitation is consumer-fed through `FoliageAgitation`, not inferred in the shader from interactor history,
  which the base shader does not have and the CPU only keeps while temporal rendering is on.
- **S4.** One displacement function serves colour, motion vectors and depth.
- **S5.** Point-light shadows draw animated casters at rest pose to keep static point-shadow caching.
- **S6.** The shiver frequency is a documented constant. A profile field waits for a consumer that needs it.
- **S7.** Shiver takes the strongest single interactor instead of summing them, so the total never exceeds
  `Shiver * height`, the bounds allowance in section 5 holds, and two players in one shrub do not make it shake
  harder than one. Added during plan review on 2026-10-13.

## Risks

- Concurrent world-authoring shadow work (`fix/wa-shadow-control-cpu`, `fix/wa-shadow-diagnostics`,
  `fix/wa-shadow-proof`) touches shadow caster code. The foliage caster path lives in its own type and this branch
  reconciles with `main` late.
- The larger instance stride touches GPU memory accounting tests and upload budgets.
- Shader compiler differences may break `Blade` byte identity on one backend. The uniform-branch fallback in
  section 3 covers it.

## Delivery

After owner approval: an implementation plan in `docs/superpowers/plans/`, implementation on
`feature/foliage-motion-profiles`, full Release verification plus the GPU legs, merge into engine `main` riding
20.34.0, then an owner-approved tag from an engine-rooted thread, then the Grimhollow adoption lane.
