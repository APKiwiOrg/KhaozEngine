# Attack Telegraph / Danger-Zone Indicator System

Status: design approved (2026-06-23). Target engine version: 7.34.0 (additive minor).

## Goal

A reusable, data-driven telegraph renderer for KhaozEngine that draws animated "danger zone"
indicators showing where an attack will land, animated over a telegraph window so players can
dodge. Generic across all games (Hardpoint 3D, Nullwake 2D, SpaceGame 2.5D, future games).
First real consumer is SpaceGame's cunnuth boss tentacle slams (wired later, in the SpaceGame chat).

## Core principle

The engine owns the VISUAL primitive only. Gameplay (when/where an attack lands, telegraph timing)
stays in each game's sim, which is often deterministic/lockstep (SpaceGame). Each frame the game
feeds the renderer shape + position + progress(0..1) + style from its own state; the engine draws.
Telegraphs NEVER enter any game's determinism hash. They are presentation-only, exactly like the
existing skinned-mesh animation layers. The engine stores no telegraph sim state across frames
(immediate-mode).

## Shape kit (v1)

Each shape carries world position + rotation, size params, a telegraph progress 0..1, and a style.

- Circle: filled AoE disc (center, radius).
- Ring / annulus: expanding shockwave (center, inner radius, outer radius).
- Beam / line: oriented rect (origin, direction, length, width) for lasers and line-slams.
- Cone / sector: (origin, direction, half-angle, range) for breath/sweep attacks.
- Arc: swept band (center, radius, band width, start angle, sweep angle) for circular sweeps.

## Architecture and packaging

Three dependency tiers. The "two new packages" split (mirroring the 7.33.0
Snapshot / Snapshot.Render3D split) holds, with one deliberate adjustment: the GPU decal pass
lands in Render3D because it needs Render3D internals, and the two new packages are the
presentation-semantic layer on top.

```
Primitives (Color, Easing, MathUtil)  -- leaf, unchanged
        ^
KhaozEngine.Telegraphs        NEW. dep: Render2D + Primitives.  -> Game2D umbrella
  - shape param structs (Circle/Ring/Beam/Cone/Arc specs)
  - TelegraphStyle + presets (Generic / Fire / Poison)
  - TelegraphResolve   pure (progress, shape, style) -> resolved visual; headless-tested
  - TelegraphRenderer2D  tessellates via SpriteBatch / PrimitiveRenderer

Render2D    gains generic filled-sector / filled-arc-band helpers on PrimitiveRenderer
            (only if missing; engine-first generic primitives)

Render3D    gains a generic low-level "shaped ground decal" primitive:
            new GLSL decal shader + new render pass (between beams and post) + per-backend golden.
            Public seam: scene.DrawGroundDecal(shape, params, color, style).
            Reusable beyond telegraphs (scorch marks, selection rings, generic AoE).
        ^
KhaozEngine.Telegraphs.Render3D   NEW. dep: Telegraphs + Render3D.  -> Game3D umbrella
  - GroundCircle/Ring/Beam/Cone/Arc  maps TelegraphStyle + progress onto Render3D's DrawGroundDecal
  - no GPU code of its own; pure param mapping, headless-testable
```

### Why the GPU primitive goes in Render3D

The depth-sampling decal pass touches `RenderResources.DepthColorTex`, the GPU device/factory, and
the private render-pass ordering in `Scene3D.RenderInternal()`, all of which are `internal` to
Render3D. A separate package cannot reach those over the public API (unlike Snapshot.Render3D, which
only needed public capture). Putting a generic `DrawGroundDecal` in Render3D, alongside the existing
low-level primitives (`DebugFilledQuad`, `DrawBeam`, `DrawBillboard`), keeps internals encapsulated,
puts the golden where the pipeline lives, and gives every game a reusable ground-decal primitive.
Telegraphs.Render3D stays a thin, headless-testable wrapper.

## Public API (immediate-mode, fed each frame)

`progress` is 0..1, clamped by the renderer. The game computes it from its own sim. The engine
stores nothing across frames.

### 2D (KhaozEngine.Telegraphs)

```csharp
var tg = new TelegraphRenderer2D();
tg.Begin(batch);                                   // wraps an active SpriteBatch
tg.Circle(center, radius, progress, style);
tg.Ring(center, inner, outer, progress, style);
tg.Beam(origin, dir, length, width, progress, style);
tg.Cone(origin, dir, halfAngleRad, range, progress, style);
tg.Arc(center, radius, bandWidth, startAngle, sweepAngle, progress, style);
tg.End();
```

### 3D ground plane (KhaozEngine.Telegraphs.Render3D, extension methods on Scene3D)

```csharp
scene.GroundCircle(centerWorld, radius, progress, style);
scene.GroundRing(centerWorld, inner, outer, progress, style);
scene.GroundBeam(originWorld, dirXZ, length, width, progress, style);
scene.GroundCone(originWorld, dirXZ, halfAngleRad, range, progress, style);
scene.GroundArc(centerWorld, radius, bandWidth, startAngle, sweepAngle, progress, style);
```

## Style model

`TelegraphStyle` is a struct with presets:

```csharp
fillColor, outlineColor    // color ramp lerps fill safe->danger over progress
edgeThickness              // outline / ring-band / feathered-rim width
opacity                    // master alpha
fillMode   = Outline | Fill | OutlineAndFill
animation  = OutlinePulse | FillSweep | ColorRamp | ImpactFlash   // [Flags], composable
blend      = Alpha | Additive
zoneSense  = Danger | Safe   // RESERVED: Safe parses but renders as Danger in v1
```

Presets: `TelegraphStyle.Generic`, `.Fire` (warm ramp, additive), `.Poison` (green ramp).

`TelegraphResolve(shape, progress, style)` is the pure function both renderers consume. It maps
progress to the concrete per-frame visual:

- OutlinePulse: outline alpha oscillates over progress.
- FillSweep: the dangerous area fills (effective fill fraction grows) as impact nears.
- ColorRamp: fill color lerps fillColor (safe) -> a danger color as progress -> 1.
- ImpactFlash: an additive brightness boost spiking near progress -> 1.

Animation flags are composable. This resolve function is where the bulk of the headless tests sit.

## 3D ground decal (GPU)

Deferred ground decal. One new GLSL shader. One draw per decal with a dynamic-offset UBO (sidesteps
the known Veldrid/Metal per-instance-attribute drop bug; decal counts are tiny so per-draw is free).

- Pass slots between `DrawBeams()` and `_post.Run()` in `Scene3D.RenderInternal()`, drawing into the
  model MRT with depth test on / no write, so meshes standing on the zone occlude it and the decal
  still flows through the engine's post-process (quantize / outline).
- Vertex: a footprint quad on the ground plane at `center.Y`, sized to the shape's max extent plus
  feather, projected by `ViewProj`.
- Fragment: sample `DepthColorTex` (linear depth, R32Float, already `Sampled`) at the pixel ->
  reconstruct the surface world position via `inverse(ViewProj)` (orthographic IsoCamera3D, so this
  is clean) -> take world XZ -> evaluate the shape's analytic SDF (circle / ring / sector /
  oriented-rect / arc-band) in shape-local space -> `fwidth`-based AA edge -> apply the resolved
  style (fill sweep, outline, color ramp, impact flash) -> alpha or additive blend.
- Y-band gate: reconstructed world Y within `[center.Y - tol, center.Y + maxStep]` keeps the zone
  painting onto terrain and slopes but not climbing vertical mesh faces. This is what delivers
  "samples depth, conforms to uneven terrain."
- Default plane is XZ with +Y normal. The API reserves an optional plane normal for a later version.

## 2D path

No new shader. `TelegraphRenderer2D` tessellates each shape (fan / ring / oriented-rect / sector /
arc-band) and gives soft AA edges via a feathered alpha rim (an outer ring of triangles fading
alpha -> 0), drawn through `SpriteBatch` / `PrimitiveRenderer` with the style's blend mode.
Filled-sector and filled-arc-band helpers are added to `PrimitiveRenderer` if not already present
(generic, engine-first). Translucent danger glows read correctly tessellated. SDF parity for the 2D
path is a noted future option, not v1.

## Determinism neutrality

The engine holds zero telegraph sim state: immediate-mode, presentation-only, like the skinned-mesh
animation layers. Nothing here can enter any game's determinism hash. A headless test asserts the
renderers carry no sim-affecting cross-frame state and that `TelegraphResolve` is pure (same inputs
-> same output, no hidden state).

## Testing

Headless (mandatory, every behavior):

- `TelegraphResolve` mappings at progress 0 / 0.5 / 1 for each animation flag; color-ramp lerp; each
  preset; clamping of out-of-range progress.
- 2D tessellation: vertex counts and positions per shape (beam-rect corners from origin/dir/len/width;
  cone sector from half-angle/range; arc band from radius/band/start/sweep; circle fan; ring band),
  feathered rim present (outer ring alpha 0).
- 3D decal param packing: footprint quad corners for a given center/size; the per-decal uniform
  packing matches the values produced by `TelegraphResolve`.

GPU golden (the accepted cost):

- Render each ground-decal shape at a fixed progress, capture, compare per-backend grids.
- Bake Metal locally, then D3D11 + Vulkan via `cross-platform-gpu.yml` `workflow_dispatch bake=true`,
  download artifacts, commit. Otherwise main goes red on the cross-platform GPU job.

## Release

Single bump 7.33.0 -> 7.34.0 (additive = minor). In order, per the engine release ritual:

1. Bump `<KhaozEngineVersion>` in `Directory.Build.props`.
2. CHANGELOG.md newest-first detailed entry + CHANGENOTES.md one-line digest (same commit).
3. Update the 3 doc version declarations check-doc-versions.sh enforces (docs/CONSUMERS.md
   "Engine current version", docs/ROADMAP.md "Current released version", README.md
   `<PackageReference>` example).
4. Update docs/CONSUMERS.md package matrix (two new packages + Game2D/Game3D umbrella membership).
5. `dotnet pack -c Release -o ./local-feed`.
6. Commit; `git tag v7.34.0`; push main + tag (CI publishes to GitHub Packages on v*).

Two new packable projects (`KhaozEngine.Telegraphs`, `KhaozEngine.Telegraphs.Render3D`), each with
`<Version>$(KhaozEngineVersion)</Version>`, added to the Game2D / Game3D umbrella metapackages
respectively.

## First consumer (for reference; wired later in the SpaceGame chat)

During each `TentacleSlamState` emitter's telegraph window:

```csharp
float progress = 1f - emitter.TelegraphSeconds / window;
scene.GroundCircle(emitter.Tgt_i, emitter.FireRadius, progress, TelegraphStyle.Fire);
scene.GroundRing(emitter.Tgt_i, 0f, emitter.ShockwaveRadius, progress, TelegraphStyle.Generic);
```

Validated live on the ground plane in Hardpoint (the standing 3D testbed) before SpaceGame wires it.

## Out of scope for v1

- Safe-zone inverse rendering (API reserved via `zoneSense`, not implemented).
- Arbitrary decal plane orientation (API reserves a normal; v1 is XZ / +Y).
- SDF-based 2D path (v1 2D is tessellated + feathered rim).
- Retained / handle-based telegraph lifetimes (immediate-mode only; the game owns lifetime).
```
