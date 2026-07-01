# KhaozEngine.Telegraphs

Presentation-only attack telegraphs / danger-zone indicators. The game's sim supplies a 0..1
progress (elapsed / windup) and a `TelegraphStyle`, this package turns that into an animated
danger shape. Holds no simulation state, so feeding it from a deterministic/lockstep sim never
touches the hash. This is the render-free style + resolve core plus the 2D path. For danger
zones painted flat on the ground in a 3D scene, add `KhaozEngine.Telegraphs.Render3D`.

- `TelegraphStyle` - plain value type: fill/outline/danger colors, edge thickness, opacity,
  `FillMode` (Outline/Fill/OutlineAndFill), `TelegraphBlend` (Alpha/Additive), and composable
  `TelegraphAnim` flags (`OutlinePulse`, `FillSweep`, `ColorRamp`, `ImpactFlash`). Presets:
  `Generic` (red-orange), `Fire` (additive, warm), `Poison` (green, pulsing). Copy a preset
  and tweak fields.
- `TelegraphResolve.Resolve(progress, style)` - the pure progress-to-visual mapping. No state,
  no allocation, no randomness, same inputs give the same output. Returns a `ResolvedTelegraph`:
  final fill/outline colors (opacity + pulse already applied), swept fill fraction, impact-flash
  term, edge thickness, fill mode, blend.
- `TelegraphRenderer2D` - immediate-mode 2D renderer over a caller-owned `SpriteBatch` +
  `PrimitiveRenderer`: `Begin(batch, primitives)`, then `Circle` / `Ring` / `Beam` / `Cone` /
  `Arc`, then `End()`.
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
