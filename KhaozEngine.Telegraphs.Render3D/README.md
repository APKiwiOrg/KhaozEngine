# KhaozEngine.Telegraphs.Render3D

The ground-plane arm of the KhaozEngine telegraph system: `Scene3D` extension methods that paint
animated danger zones flat on the ground/terrain under the meshes, via Render3D's generic
depth-sampling `DrawGroundDecal` pass. Kept separate from `KhaozEngine.Telegraphs` so a 2D-only
game never drags in Render3D. Presentation only, holds no sim state.

- Extensions on `Scene3D`: `GroundCircle`, `GroundRing`, `GroundBeam`, `GroundCone`, `GroundArc`.
  Each takes world-space shape parameters plus a 0..1 progress and a `TelegraphStyle`, resolves
  the style at that progress, and queues a `GroundDecal`. Immediate-mode, call per frame.
- They live in the `KhaozEngine.Telegraphs` namespace on purpose, so the `using` you already
  have for `TelegraphStyle` and the presets brings the extensions into scope too.
- `GroundTelegraphs.BuildCircle/BuildRing/BuildBeam/BuildCone/BuildArc` statics are the pure
  style-to-decal mapping (headless-testable), the extensions are thin wrappers over
  `scene.DrawGroundDecal`.
- Edge/outline width is derived in world units as a small fraction of the shape's size, so a big
  AoE gets a proportionally bigger rim. `TelegraphStyle.EdgeThickness` is authored in 2D pixels
  and is deliberately ignored on this path.
- Beams and cones aim with an XZ direction vector. Decals gate on terrain height with sane
  defaults, so a zone hugs the ground instead of smearing up cliffs.

```csharp
// inside the 3D pass, progress 0..1 from the sim:
scene.GroundCircle(bossPos, radius: 6f, progress, TelegraphStyle.Fire);
scene.GroundCone(bossPos, aimDirXZ, halfAngleRad: 0.6f, range: 12f, progress, TelegraphStyle.Poison);
```

## Modern style knobs (feather, noise fills, edge energy)

This is the only path that renders `TelegraphStyle`'s modern knobs (`TelegraphRenderer2D` in
`KhaozEngine.Telegraphs` reads none of them):

- `FeatherWidth` maps to `GroundDecal.FeatherWidth` in WORLD UNITS: `Base()` clamps the style's
  0..1 feather fraction to 0..0.5 and multiplies it by the shape's characteristic size (the same
  size used for the world-space edge). 0 keeps the legacy hard fwidth-AA edge.
- `TelegraphStyle.Pattern` (a `TelegraphFillPattern`) casts directly onto `GroundDecal.Pattern`
  (a `DecalFillPattern`, `GroundDecal`'s own enum in `KhaozEngine.Render3D`): the two enums share
  the same `Solid`/`ScrollingNoise`/`RadialNoise` values on purpose, one per fill style, so the
  cast never needs a lookup table. `PatternSpeed` passes straight through. `PatternScale` gets one
  conversion: it is authored as "noise cells across the shape" and converted here to "noise cells
  per world unit" by dividing by the shape's characteristic size, so a bigger AoE gets
  proportionally coarser (not stretched) noise. Gated on `Pattern != Solid`: a fully legacy style
  (`Pattern == Solid`, `PatternScale == 0`) maps to a fully zero decal, so old callers that never
  touched these fields render byte-identical to before.
- `RimGlow`, `SweepGlow`, `Sparkle` on `ResolvedTelegraph` (already scaled by `EdgeEnergy` and
  gated by their `TelegraphAnim` flags) pass straight through to the matching `GroundDecal`
  fields.
- `Scene3D.DecalQuality` (`GroundDecalQuality.Full` / `.Reduced`) is a scene-wide tier read by
  the decal pass itself, not by this mapping: `Reduced` drops the second noise octave and the
  edge sparkle for weak GPUs. Set it once on the `Scene3D`, not per decal.
- The animated pattern/rim/sparkle all read `Scene3D.EffectTimeSeconds`, the same host-set
  per-frame clock beams and water use. Never set it and every decal renders a static pattern.

## Residue marks

`GroundTelegraphs.BuildResidueCircle(center, radius, age01, style)` (and the `scene.GroundResidueCircle`
wrapper) build a one-shot fading, slightly expanding scorch/frost mark for the moment after a
telegraph resolves: fill alpha fades as `(1 - age01)^2` and the radius grows by up to 8%. The
builder stays pure and immediate-mode like every other telegraph call here, so the CONSUMER owns
and advances `age01` (0 = just resolved, 1 = gone) each frame and simply stops calling once it
reaches 1. It always uses `style.DangerColor` (dimmed) for the fill, never `style.FillColor`, and
defaults to a `ScrollingNoise` pattern even for a `Solid`-pattern style.

```csharp
residueAge += dt;
float age01 = Math.Clamp(residueAge / ResidueLifetime, 0f, 1f);
if (age01 < 1f)
    scene.GroundResidueCircle(impactPoint, radius, age01, TelegraphStyle.Fire);
```

Depends on `KhaozEngine.Telegraphs` + `KhaozEngine.Render3D`. In the `Game3D` umbrella.
