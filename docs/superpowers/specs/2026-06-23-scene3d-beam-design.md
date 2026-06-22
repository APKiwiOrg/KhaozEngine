# Scene3D 3D beam primitive — design

Status: approved (brainstorm). Target release: `7.26.0` (minor, additive API).

## Problem

`Scene3D` can draw camera-facing billboards (soft-disc + textured, Alpha/Additive), dynamic point
lights (`AddLight`), and thin debug lines (`DebugLine`, no glow), but it has no glowing beam between
two world points. Games want lasers, thrusters, and tethers: a bright animated core with a soft halo,
drawn in world space, occluded by geometry. The 2D engine has `KhaozEngine.Render2D/Vfx/EnergyBeam.cs`
as a reference look (animated core + soft glow band) but it is screen-space only.

## Goal

Add `Scene3D.DrawBeam(Vector3 a, Vector3 b, float width, Color color, BeamStyle? style = null)`: a
camera-facing quad stretched along a->b, additive-blended, with a soft core + halo computed in the
fragment shader. Depth-interleaved like the textured billboard so geometry occludes the beam.
Time-driven pulse/scroll and tapered ends are supported. A dedicated beam pipeline (not the disc shader).

## Decisions (locked in brainstorm)

- **Animated, with a scene clock.** The host sets `Scene3D.EffectTimeSeconds` once per frame; beams whose
  style enables pulse/scroll animate off it, others stay static. A generic name (not `BeamTimeSeconds`) so
  future time-driven 3D effects can share the clock; beams are its only consumer today.
- **Split core + glow colours.** `BeamStyle` carries optional `CoreColor`/`GlowColor`; the `color`
  parameter seeds the core when `CoreColor` is null, and a dimmed/wider halo when `GlowColor` is null.
- **Dedicated beam pipeline**, additive into the model MRT, mirroring the textured-billboard depth path.
- **One batched draw for all beams** (style baked per-vertex) rather than per-beam UBO updates. Beams are
  few (lasers/thrusters/tethers), and batching sidesteps the per-draw-uniform Metal/Veldrid hazard the
  skinned-bone path documents (mid-list uniform rebinding mis-fetches past the first draw on Metal).

## Public API (KhaozEngine.Render3D)

### `Scene3D.DrawBeam`

```csharp
public void DrawBeam(Vector3 a, Vector3 b, float width, Color color, BeamStyle? style = null)
```

Queues one beam for this frame. Cleared in `Begin()` like the other immediate-mode queues. `color` seeds
the core colour. `width` is the full world-space cross-beam width (the quad spans `width` across, i.e.
`±width/2` from the axis). A degenerate beam (`a≈b`, or `width <= 0`) is a silent no-op (no throw),
matching `DebugRay`'s degenerate-direction guard and the invalid-handle billboard no-op. Presentation
only — never feed sim/RNG/netcode from beam state.

### `Scene3D.EffectTimeSeconds`

```csharp
public float EffectTimeSeconds { get; set; }   // default 0
```

Host-set per-frame clock (seconds) driving beam pulse/scroll. The host sets it in `OnDraw3D` (which runs
after `Begin()`), e.g. `scene.EffectTimeSeconds = totalSeconds`. **Not** cleared by `Begin()` — the host
owns the value and sets it each frame. Presentation only. Zero (never set) => no animation, so a static
beam renders identically regardless of the clock.

### `BeamStyle`

```csharp
public readonly record struct BeamStyle
{
    public Color? CoreColor { get; init; }   // null => the DrawBeam `color` param
    public Color? GlowColor { get; init; }   // null => a dimmed, wider halo derived from the core
    public float CoreFraction { get; init; } // bright-core share of the half-width [0..1], default 0.35
    public float GlowSoftness { get; init; } // halo falloff exponent (higher = tighter), default 2.0
    public float Taper { get; init; }        // end-fade fraction [0..0.5], default 0 (square ends)
    public float PulseSpeed { get; init; }   // brightness pulse speed (rad/s), default 0 (no pulse)
    public float PulseAmount { get; init; }  // pulse amplitude [0..1], default 0
    public float ScrollSpeed { get; init; }  // along-beam flow speed (cycles/s), default 0 (no flow)

    public static BeamStyle Default { get; }  // a cyan-white core with a soft blue halo, square ends, static
}
```

Immutable record struct; derive variants with `with`. Vocabulary mirrors the 2D `BeamParams`
(`CoreColor`/`GlowColor`/`PulseSpeed`/`PulseAmount`) so the two beam APIs read the same. `default(BeamStyle)`
(all nulls/zeros) is valid: it renders a static, square-ended single-colour beam (core = `color` param,
glow = derived). `DrawBeam(..., style: null)` uses `BeamStyle.Default`.

**Colour resolution** (in `DrawBeam`, before enqueue): the resolved core colour is `CoreColor ?? color`.
The resolved glow colour is `GlowColor ?? coreColour` with its alpha scaled to `0.4×` — the halo reads
*wider and softer* than the core purely from the falloff profile (`pow(1-d, GlowSoftness)` across the full
quad width vs the core's inner `CoreFraction`), not from a separate width. A caller wanting a distinct halo
hue sets `GlowColor` explicitly.

### `BeamGeometry` (new pure helper, sibling to `BillboardGeometry`)

GPU-free, headless-testable. Builds the camera-facing strip along the beam axis:

```csharp
public static class BeamGeometry
{
    // side = normalize(cross(viewDir, axis)); a-end corners ±side*halfWidth, b-end corners ±side*halfWidth.
    // Returns false (no geometry written) when the beam is degenerate (a≈b). When axis ∥ viewDir (beam points
    // at/away from the camera) `side` degenerates: fall back to a stable perpendicular so output stays finite.
    public static bool Corners(Vector3 a, Vector3 b, Vector3 viewDir, float width,
        out Vector3 aLeft, out Vector3 aRight, out Vector3 bLeft, out Vector3 bRight);

    // 6 triangle-list verts (two triangles) + UVs: u = across [0,1] (0 = aLeft/bLeft side, 1 = right side),
    // v = along [0,1] (0 at a, 1 at b). Returns the count written (6), or 0 for a degenerate beam.
    public static int Triangles(Vector3 a, Vector3 b, Vector3 viewDir, float width,
        Span<Vector3> positions, Span<Vector2> uvs);
}
```

`viewDir` is the camera forward (constant across a frame, matching `BillboardGeometry.CameraBasis`'s use of
`Camera.Forward`). UV convention: **u** is the across-axis coordinate the fragment shader uses for the
core/halo profile; **v** is the along-axis coordinate for taper and scroll.

## Rendering (internal)

### `BeamVertex`

```
Position : Float3   (world)
Uv       : Float2   (u across, v along)
CoreColor: Float4
GlowColor: Float4
Shape    : Float4   (x = CoreFraction, y = GlowSoftness, z = Taper, w unused)
Anim     : Float4   (x = PulseSpeed, y = PulseAmount, z = ScrollSpeed, w unused)
```

All style is baked per-vertex so the whole frame's beams render in one draw with a single shared
`{ ViewProj, Time }` UBO — no per-draw uniform rebinding.

### `BeamRenderer` (Rendering/, mirrors `TexturedBillboardRenderer`)

- Draws INTO the model MRT framebuffer (`_res.ModelFB`) after the meshes and textured billboards, with the
  depth test **less-equal, no write** (`GpuDepthStencilState.DepthTestLessEqualNoWrite`). The depth buffer
  holds the meshes' depth, so a nearer mesh occludes the beam and a beam in front draws over a farther mesh.
- Blend: attachment 0 = **Additive** (`SourceAlpha / One`); attachments 1 (normal) and 2 (depth) =
  **PreserveDestination**, so the beam never disturbs the normal/depth targets and the edge-outline post-pass
  ignores it (no outline traced around the quad — same trick the textured billboard uses).
- UBO: `{ mat4 ViewProj; vec4 Time; }` (80 bytes). `ViewProj` is `GpuClip.Correct`-ed for the live backend
  (same as the textured billboard); `Time.x = EffectTimeSeconds`.
- One pipeline (additive only — a beam is inherently a glow). One draw per frame for all queued beams.

### Shaders (`ShaderSources`, GLSL 450, cross-compiled)

`BeamVert` — transforms `Position` by `ViewProj`, passes `Uv`, `CoreColor`, `GlowColor`, `Shape`, `Anim`.

`BeamFrag` — additive core + halo profile from the across coordinate, with end taper and time animation:

```glsl
float d = abs(vUv.x * 2.0 - 1.0);                       // 0 at axis, 1 at edge
float core = 1.0 - smoothstep(coreFrac * 0.6, coreFrac, d);
float glow = pow(max(1.0 - d, 0.0), glowSoftness);
// End taper (v along the beam): smooth fade in/out over `taper` at each end; taper 0 => 1 (square ends).
float taperFade = (taper > 0.0)
    ? smoothstep(0.0, taper, vUv.y) * smoothstep(0.0, taper, 1.0 - vUv.y)
    : 1.0;
// Pulse: brightness oscillation. Scroll: along-beam flow ripple on the core.
float pulse = 1.0 + pulseAmount * sin(Time.x * pulseSpeed);
float flow  = (scrollSpeed != 0.0)
    ? 0.85 + 0.15 * sin((vUv.y - Time.x * scrollSpeed) * 6.2831853)
    : 1.0;
float master = taperFade * pulse;
vec3 rgb = vCoreColor.rgb * vCoreColor.a * core * flow
         + vGlowColor.rgb * vGlowColor.a * glow;
oColor  = vec4(rgb, master);   // Additive = src.rgb*src.a + dst  => adds rgb*master
oNormal = vec4(0.0);           // discarded (PreserveDestination on attachment 1)
oDepth  = vec4(0.0);           // discarded (PreserveDestination on attachment 2)
```

The per-band colour alpha (`CoreColor.a`/`GlowColor.a`) is that band's intensity weight; `master`
(taper × pulse) is the additive blend's source-alpha master multiplier.

### `Scene3D` wiring

- `readonly List<BeamItem> _beamItems` (resolved a/b/width/core+glow colour/shape/anim), cleared in `Begin()`.
- `DrawBeam` resolves the style (null => `Default`; null colours => derived from `color`) and enqueues a
  `BeamItem`.
- `internal int BeamCount => _beamItems.Count;` so headless tests can assert enqueue + `Begin` clear.
- `RenderInternal` flushes beams in a `DrawBeams(cl)` call placed immediately after `DrawTexturedBillboards(cl)`
  (still the model FB, before the post chain). For each item: `BeamGeometry.Triangles(...)` with `Camera.Forward`,
  append `BeamVertex`es, then one `BeamRenderer.Draw`. `BeamRenderer.SetFrameUniforms(cl, Camera.ViewProjection,
  EffectTimeSeconds)` runs once before the draw.
- Disposed in `Dispose()` alongside the other renderers.

## Tests

### Headless `BeamGeometryTests` (KhaozEngine.Tests/Render3D)

- **Faces the camera:** quad normal `cross(axis, side)` is parallel to `viewDir` (|dot| ≈ 1); `side ⟂ axis`
  and `side ⟂ viewDir`.
- **Spans a->b:** the a-end corner midpoint ≈ `a`, the b-end corner midpoint ≈ `b`; along-axis extent ≈ `|b-a|`.
- **Respects width:** across extent (left corner to right corner) ≈ `width`; each corner is `±width/2` off the axis.
- **UVs:** u spans [0,1] across, v spans [0,1] along; u/v corners match the position corners (a-end = v0,
  b-end = v1).
- **Degenerate `a≈b`:** `Corners` returns false / `Triangles` returns 0; no NaN.
- **Degenerate `axis ∥ viewDir`:** output is finite (no NaN), `side` is unit length (stable fallback).
- **Span-too-small** throws `ArgumentException` (matches `BillboardGeometry.Triangles`).

### Headless `Scene3DBeamQueueTests` (KhaozEngine.Tests/Render3D)

Constructed via the headless scene harness used by the other queue tests (no GPU): `DrawBeam` enqueues
(`BeamCount` rises); `Begin()` clears the queue; a degenerate beam is a no-op (count unchanged);
`EffectTimeSeconds` round-trips and is **not** cleared by `Begin()`. `BeamStyle.Default` and `default`
both produce a valid item; null colours resolve from the `color` param.

### GPU golden `scene3d_beam` (KhaozEngine.Tests/Gpu/GoldenSnapshotTests)

A fixed asymmetric scene: a beam drawn across the frame between two world points, with an opaque box
straddling the midpoint so the box **occludes the beam's centre** while the ends stay visible — this locks
the depth-interleave and the additive glow in one grid. Bake the **Metal** golden locally
(`KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1`); the **D3D11** and **Vulkan** goldens are baked on their own backends
per the established per-backend process (the cross-backend consistency test only compares backends that have
a committed golden, so a Metal-only first landing does not fail it, but all three should follow).

## Docs

`docs/USING-KHAOZENGINE.md` — a `DrawBeam` subsection near the billboard/light overlay docs:
- the signature, `BeamStyle` fields, and the `EffectTimeSeconds` clock note;
- the **recommended combo**: `DrawBeam(a, b, ...)` + `AddLight` at both endpoints (so the beam lights nearby
  geometry) + a `ParticleSystem` spark burst at the impact point.

## Out of scope (YAGNI for 7.26.0)

- Sideways jitter/wobble (the 2D `JitterAmount`), dashed beams, and explicit round end-caps as separate
  geometry — the taper covers soft ends; the rest can be added later if a game needs them.
- An alpha (non-additive) beam pipeline — a beam is a glow; additive only.
- Per-beam independent clocks — one shared `EffectTimeSeconds`.

## Release (per CLAUDE.md)

Minor bump `7.25.0 -> 7.26.0`: `Directory.Build.props`, `CHANGELOG.md` (detailed, newest-first),
`CHANGENOTES.md` (one-line digest), the three doc-version declarations the guard checks
(`docs/CONSUMERS.md` engine-current-version, `docs/ROADMAP.md` current-released-version, `README.md`
`<PackageReference>` example), `dotnet pack -c Release -o ./local-feed`, commit, `git tag v7.26.0`,
push `main` + tag.
