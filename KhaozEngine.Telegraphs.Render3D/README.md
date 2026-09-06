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
  and is deliberately ignored on this path. `TelegraphStyle.EdgeWidthWorld` is an opt-in world-unit
  override: 0 (default) keeps the derived auto-scaling edge, a positive value pins the stroke at
  any shape size instead (useful for a thin crisp static ring at a large radius).
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

- `FillMode` is honored here too. `TelegraphResolve` zeroes the outline alpha (plus `RimGlow` and
  `Runner`) for `FillMode.Fill`, and zeroes the fill alpha for `FillMode.Outline`, before this
  path ever queues a `GroundDecal`. Behavior fix: this path used to draw the outline band
  unconditionally regardless of `FillMode` (only the 2D renderer honored it), and now agrees with
  the 2D path. `FillMode.Fill` with a nonzero `BaseFill` is the borderless-telegraph recipe: the
  full shape extent reads immediately with no outline, and the sweep brightens across it.

- `FeatherWidth` maps to `GroundDecal.FeatherWidth` in WORLD UNITS: `Base()` clamps the style's
  0..1 feather fraction to 0..0.5 and multiplies it by the shape's characteristic size (the same
  size used for the world-space edge). 0 keeps the legacy hard fwidth-AA edge.
  `TelegraphStyle.FeatherWidthWorld` is an opt-in override on top of this: 0 (default) keeps the
  derived shape-relative feather above, a positive value feeds `GroundDecal.FeatherWidth` directly
  in world units instead, skipping the characteristic-size multiply.
- `TelegraphStyle.Pattern` (a `TelegraphFillPattern`) casts directly onto `GroundDecal.Pattern`
  (a `DecalFillPattern`, `GroundDecal`'s own enum in `KhaozEngine.Render3D`): the two enums share
  the same `Solid`/`ScrollingNoise`/`RadialNoise`/`MoltenCracks` values on purpose, one per fill
  style, so the cast never needs a lookup table. A test
  (`GroundTelegraphMappingTests.Every_decal_fill_pattern_has_a_telegraph_twin_at_the_same_value`)
  pins the two member lists against each other, because the cast is only sound while they agree
  and the telegraph side did fall a member behind once
  ([#229](https://github.com/APKiwiOrg/KhaozEngine/issues/229)). `ScrollingNoise` is domain-warped
  drift (wispy filaments, not round blobs). `RadialNoise` is a Cartesian vortex swirl (spiral arms
  orbiting the center, no polar singularity). `MoltenCracks` is an animated Voronoi crack web.
  `PatternSpeed` passes straight through. `PatternScale` gets one
  conversion: it is authored as "noise cells across the shape" and converted here to "noise cells
  per world unit" by dividing by the shape's characteristic size, so a bigger AoE gets
  proportionally coarser (not stretched) noise. Gated on `Pattern != Solid`: a fully legacy style
  (`Pattern == Solid`, `PatternScale == 0`) maps to a fully zero decal, so old callers that never
  touched these fields render byte-identical to before.
- `RimGlow`, `SweepGlow`, `Sparkle`, `Runner` on `ResolvedTelegraph` (already scaled by
  `EdgeEnergy` and gated by their `TelegraphAnim` flags) pass straight through to the matching
  `GroundDecal` fields. `SweepGlow` itself ramps in over the first fifth of the cast, so an
  early-cast sweep never reads as a bright ball at the shape center. `Runner` drives eight soft
  dash segments orbiting the outline band.
- `InteriorDim` on `ResolvedTelegraph` passes straight through to `GroundDecal.InteriorDim`: it
  eases the fill alpha down deep inside the swept region while staying full near the sweep front,
  so the energy reads at the rim and the moving edge instead of pooling into the shape center.
  0 (legacy styles that never set it) is inert, byte-identical to before.
- `BaseFill` on `ResolvedTelegraph` passes straight through to `GroundDecal.BaseFill`: a fraction
  of the fill alpha painted across the entire shape from progress 0, independent of the sweep, so
  a borderless (`FillMode.Fill`) telegraph's full danger extent reads immediately instead of only
  the swept fraction. 0 (legacy styles that never set it) is inert, byte-identical to before.
- `VoidFallback` / `VoidDim` on `ResolvedTelegraph` (since 12.1.0) pass straight through to the
  matching `GroundDecal` fields: an opt-in plane fallback that fills in where the decal's usual
  depth-reconstructed surface is missing (background, or geometry outside the decal's ground Y
  band), painted only where a depth comparison says the plane is genuinely visible. A ring
  overhanging a cliff paints across it, and a wall standing on the decal's ground still occludes
  it. `VoidDim` scales alpha on the plane-projected pixels only (0 default = no dim). Both are
  `false`/`0` on every preset, so no existing style opts in on its own. Set them explicitly on a
  style or a `TelegraphStyle.Generic with { VoidFallback = true, VoidDim = 0.15f }` copy. Every shape builder,
  including `BuildResidueCircle`, carries both fields.
- `AccentColor`, `PatternParam` and `EdgeErosion` on `ResolvedTelegraph` pass through VERBATIM to
  the matching `GroundDecal` fields, with no conversion at all. That is the point: `PatternParam`
  is in cell space and `EdgeErosion` is a fraction of the shape's own half-thickness, so both are
  dimensionless, unlike `FeatherWidth`, which this mapping either derives in world units from the
  characteristic size or takes from the `FeatherWidthWorld` override. Erosion therefore behaves
  identically whichever of those two feather paths a style is on, and the shader's own order
  (erode first, then feather the surviving boundary) is what relates them. `AccentColor` arrives
  with its alpha already scaled by the style `Opacity`, done in `TelegraphResolve` alongside the
  fill's. All three are zero on every preset, so no existing style opts in on its own.
- `Scene3D.DecalQuality` (`GroundDecalQuality.Full` / `.Reduced`) is a scene-wide tier read by
  the decal pass itself, not by this mapping: `Reduced` drops the second noise octave and the
  edge sparkle for weak GPUs. Set it once on the `Scene3D`, not per decal.
- The animated pattern/rim/sparkle/runner all read `Scene3D.EffectTimeSeconds`, the same host-set
  per-frame clock beams and water use. Never set it and every decal renders a static pattern.

## Residue marks

`GroundTelegraphs.BuildResidueCircle(center, radius, age01, style)` (and the `scene.GroundResidueCircle`
wrapper) build a one-shot fading, slightly expanding scorch/frost mark for the moment after a
telegraph resolves: fill alpha fades as `(1 - age01)^2` and the radius grows by up to 8%. The
builder stays pure and immediate-mode like every other telegraph call here, so the CONSUMER owns
and advances `age01` (0 = just resolved, 1 = gone) each frame and stops calling once it
reaches 1. It always uses `style.DangerColor` (dimmed) for the fill, never `style.FillColor`, and
defaults to a `ScrollingNoise` pattern even for a `Solid`-pattern style.

```csharp
residueAge += dt;
float age01 = Math.Clamp(residueAge / ResidueLifetime, 0f, 1f);
if (age01 < 1f)
    scene.GroundResidueCircle(impactPoint, radius, age01, TelegraphStyle.Fire);
```

Depends on `KhaozEngine.Telegraphs` + `KhaozEngine.Render3D`. In the `Game3D` umbrella.
