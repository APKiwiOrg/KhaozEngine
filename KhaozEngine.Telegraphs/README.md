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
- `TelegraphAnim` flags (composable, OR them together): `OutlinePulse`, `FillSweep`, `ColorRamp`,
  `ImpactFlash` (the original four), plus `RimGlow` (soft glow hugging the boundary), `SweepGlow`
  (bright leading edge on the `FillSweep` front, no-op without `FillSweep` also set), and
  `EdgeSparkle` (sparse animated sparkle cells along the boundary).
- Modern style knobs on `TelegraphStyle` (consumed by the 3D ground-decal path only, see the
  callout below):
  - `FeatherWidth` - soft-edge band, as a fraction of the shape's characteristic size. 0 keeps
    the legacy hard anti-aliased edge.
  - `Pattern` (`TelegraphFillPattern`): `Solid` (legacy flat tint, default), `ScrollingNoise`
    (value noise scrolling across the shape), `RadialNoise` (value noise flowing radially
    outward from the shape center).
  - `PatternSpeed` - pattern animation rate, cycles per second of the scene effect clock.
  - `PatternScale` - noise cells across the shape's characteristic size. 0 falls back to 6.
  - `EdgeEnergy` - master strength multiplier for `RimGlow` / `SweepGlow` / `EdgeSparkle`. 0
    means the default full strength of 1 (not off). Set an explicit value to scale it.
- Presets, each a distinct character to reach for by name:

  | Preset | Character |
  |--------|-----------|
  | `Generic` | Neutral red-orange danger zone, alpha-blended, fill sweep + color ramp + impact flash, plus rim and sweep glow (no outline pulse). |
  | `Fire` | Additive warm glow, scrolling noise, edge sparkle. |
  | `Poison` | Toxic green, alpha-blended, pulsing outline. |
  | `Steel` | Cool grey, crisp edge, fine brushed-grain noise, no rim glow or sparkle. |
  | `Frost` | Pale ice blue, wide soft feather, slow radial noise flow, rim glow + edge sparkle, no sweep glow. |
  | `Nature` | Verdant green, soft organic drift, rim glow + sweep glow, no pulse or flash. |
  | `Arcane` | Violet additive energy, radial noise, every animation flag on. |

  Copy a preset and tweak fields.
- `TelegraphResolve.Resolve(progress, style)` - the pure progress-to-visual mapping. No state,
  no allocation, no randomness, same inputs give the same output. Returns a `ResolvedTelegraph`:
  final fill/outline colors (opacity + pulse already applied), swept fill fraction, impact-flash
  term, edge thickness, fill mode, blend, plus the resolved feather fraction, pattern +
  speed + scale, and rim glow / sweep glow / sparkle energies (each 0 when its flag is off).
- `TelegraphRenderer2D` - immediate-mode 2D renderer over a caller-owned `SpriteBatch` +
  `PrimitiveRenderer`: `Begin(batch, primitives)`, then `Circle` / `Ring` / `Beam` / `Cone` /
  `Arc`, then `End()`. Draws the flat fill/outline/pulse/flash only. **It reads none of the
  modern style knobs above** (FeatherWidth, Pattern/PatternSpeed/PatternScale, EdgeEnergy,
  RimGlow, SweepGlow, EdgeSparkle) - those are a `KhaozEngine.Telegraphs.Render3D` ground-decal
  feature.
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
