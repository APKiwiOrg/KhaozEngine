# KhaozEngine.Telegraphs

Presentation-only attack telegraphs / danger-zone indicators. The game's sim supplies a 0..1
progress (elapsed / windup) and a `TelegraphStyle`, this package turns that into an animated
danger shape. Holds no simulation state, so feeding it from a deterministic/lockstep sim never
touches the hash. This is the render-free style + resolve core plus the 2D path. For danger
zones painted flat on the ground in a 3D scene, add `KhaozEngine.Telegraphs.Render3D`.

- `TelegraphStyle` - plain value type: fill/outline/danger colors, edge thickness, opacity,
  `FillMode` (Outline/Fill/OutlineAndFill), `TelegraphBlend` (Alpha/Additive), composable
  `TelegraphAnim` flags, and the modern style knobs below. It's a plain struct, so `with`-style
  copies work for tweaking a preset.
- `FillMode` is honored by both renderers: `TelegraphResolve` zeroes the unwanted alpha before
  either path draws. `Fill` zeroes the outline alpha plus the outline-band effects (`RimGlow`,
  `OutlineRunner`). `Outline` zeroes the fill alpha (which also silences the fill-driven pattern,
  `BaseFill`, and sweep glow). Behavior fix: the 3D ground-decal path used to draw the outline
  band unconditionally regardless of `FillMode`, and only the 2D renderer honored it. Both paths
  now agree, so `FillMode.Fill` + a nonzero `BaseFill` is the borderless-telegraph recipe.
- `TelegraphAnim` flags (composable, OR them together): `OutlinePulse`, `FillSweep`, `ColorRamp`,
  `ImpactFlash` (the original four), plus `RimGlow` (soft glow hugging the boundary), `SweepGlow`
  (bright leading edge on the `FillSweep` front, no-op without `FillSweep` also set, ramps in over
  the first fifth of the cast so an early sweep doesn't engulf the whole shape center),
  `EdgeSparkle` (sparse animated sparkle cells along the boundary), and `OutlineRunner` (rotating
  dash segments orbiting the outline band, a rune-ring feel).
- Modern style knobs on `TelegraphStyle` (consumed by the 3D ground-decal path only, see the
  callout below):
  - `FeatherWidth` - soft-edge band, as a fraction of the shape's characteristic size. 0 keeps
    the legacy hard anti-aliased edge.
  - `Pattern` (`TelegraphFillPattern`): `Solid` (legacy flat tint, default), `ScrollingNoise`
    (domain-warped value noise drifting across the shape into wispy filaments, not round
    scrolling blobs), `RadialNoise` (a Cartesian vortex swirl, spiral arms orbiting the shape
    center over time, no polar singularity at the center).
  - `PatternSpeed` - pattern animation rate, cycles per second of the scene effect clock.
  - `PatternScale` - noise cells across the shape's characteristic size. 0 falls back to 6.
  - `EdgeEnergy` - master strength multiplier for `RimGlow` / `SweepGlow` / `EdgeSparkle`. 0
    means the default full strength of 1 (not off). Set an explicit value to scale it.
  - `InteriorDim` - how much the deep fill interior dims relative to the boundary and sweep
    front (0 = legacy uniform fill, 1 = fully hollow), concentrating energy at the rim. Presets
    use roughly 0.35 to 0.6. All seven set a nonzero value now.
  - `BaseFill` - fraction of the fill alpha painted across the ENTIRE shape from progress 0,
    independent of the sweep (0 = legacy, nothing shows until the sweep reaches it). Lets a
    borderless (`FillMode.Fill`) telegraph's full danger extent read immediately, the sweep then
    brightens across it. Presets use 0.3.
  - `EdgeWidthWorld` - opt-in world-unit override for the outline / AA edge half-width on the 3D
    ground-decal path. 0 (default) keeps the derived auto-scaling edge (5% of the shape's
    characteristic size, clamped to 0.03..0.3 world units). A positive value pins the stroke at
    any shape size.
  - `FeatherWidthWorld` - opt-in world-unit override for the feather band on the 3D ground-decal
    path. 0 (default) keeps the shape-relative `FeatherWidth` fraction. A positive value pins the
    feather in world units.
  - `VoidFallback` (since 12.1.0) - opt-in: the 3D ground-decal path projects onto its own
    horizontal plane wherever its usual paint surface is missing, instead of truncating at the
    geometry's edge. The decal still conforms to any surface that is its ground. The plane covers
    background and off-ground surfaces, and paints there only where a depth comparison says it is
    genuinely visible (crosses in front of a cliff it overhangs, occluded by a wall standing in
    front of it). `false` (default) keeps the legacy depth-only behavior.
  - `VoidDim` (since 12.1.0) - alpha scale applied only to the plane-projected pixels of a
    `VoidFallback` decal, so they read as projected rather than as standing on ground. 0 (default)
    = no dim, 1 = fully transparent. Ignored unless `VoidFallback` is set.
- Presets, each a distinct character to reach for by name:

  | Preset | Character |
  |--------|-----------|
  | `Generic` | Neutral red-orange danger zone, alpha-blended, fill sweep + color ramp + impact flash, plus rim and sweep glow (no outline pulse). |
  | `Fire` | Additive warm glow, scrolling noise, edge sparkle. |
  | `Poison` | Toxic green, alpha-blended, pulsing outline, plus rim and sweep glow. |
  | `Steel` | Cool grey, crisp edge, fine brushed-grain noise, outline dash runner, no rim glow or sparkle. |
  | `Frost` | Pale ice blue, wide soft feather, slow vortex swirl, rim glow + edge sparkle, no sweep glow. |
  | `Nature` | Verdant green, soft organic drift, rim glow + sweep glow, no pulse or flash. |
  | `Arcane` | Violet additive energy, vortex swirl, every animation flag on. |

  Copy a preset and tweak fields.
- `TelegraphResolve.Resolve(progress, style)` - the pure progress-to-visual mapping. No state,
  no allocation, no randomness, same inputs give the same output. Returns a `ResolvedTelegraph`:
  final fill/outline colors (opacity + pulse already applied), swept fill fraction, impact-flash
  term, edge thickness, fill mode, blend, plus the resolved feather fraction, pattern +
  speed + scale, interior dim, and rim glow / sweep glow / sparkle / runner energies (each 0 when
  its flag is off).
- `ResolvedTelegraph` is built with an object initializer, naming each member
  (`new ResolvedTelegraph { FillColor = ..., RimGlow = ... }`). Every member is `init`-settable and defaults
  to its inert value (0, `Solid`, false), so an initializer that names only what it means is complete. The
  positional constructors are kept for source compatibility and are frozen at their current shapes: ten of
  the widest one's parameters are consecutive `float`s the compiler cannot order-check, so a transposed pair
  compiles, runs, and draws the wrong thing. New state lands as another `init` member, never as a wider
  constructor.
- `TelegraphRenderer2D` - immediate-mode 2D renderer over a caller-owned `SpriteBatch` +
  `PrimitiveRenderer`: `Begin(batch, primitives)`, then `Circle` / `Ring` / `Beam` / `Cone` /
  `Arc`, then `End()`. Draws the flat fill/outline/pulse/flash only, picking primitives by
  `FillMode` directly. **It reads none of the modern style knobs above** (FeatherWidth,
  Pattern/PatternSpeed/PatternScale, EdgeEnergy, InteriorDim, BaseFill, RimGlow, SweepGlow,
  EdgeSparkle, OutlineRunner, EdgeWidthWorld, FeatherWidthWorld, VoidFallback, VoidDim) - those are
  a `KhaozEngine.Telegraphs.Render3D` ground-decal feature.
- `ZoneSense.Safe` is reserved for a future version (v1 renders it exactly like `Danger`).

```csharp
var telegraphs = new TelegraphRenderer2D();

// each frame, inside an active SpriteBatch:
telegraphs.Begin(batch, primitives);
float progress = attack.Elapsed / attack.Windup; // 0..1 from the sim
telegraphs.Circle(bossPos, radius: 80f, progress, TelegraphStyle.Fire);
telegraphs.Cone(bossPos, aimDir, halfAngleRad: 0.5f, range: 220f, progress, TelegraphStyle.Generic);
telegraphs.End();
```

Depends on `KhaozEngine.Render2D` + `KhaozEngine.Primitives`. In the `Game2D` umbrella.
