# Particle system + 3D billboards design (5.21.0-experimental)

**Goal:** a MonoGame-free particle system for the 5.x stack — a pure, deterministic, headless-testable
simulation (`KhaozEngine.Particles`) plus camera-facing **billboard** rendering in Render3D
(`Scene3D.DrawBillboard`) to display the particles as soft round sprites. Enables juice: muzzle flashes, hit
sparks, death puffs, trails. The sim is render-agnostic (a 2D path can consume it later); this release ships
the 3D path that the Hardpoint testbed needs.

## Part A — `KhaozEngine.Particles` (NEW pure package: System.Numerics + BCL only, no Veldrid/MonoGame)

New project `KhaozEngine.Particles/KhaozEngine.Particles.csproj` (PackageId `KhaozEngine.Particles`,
`<Version>$(KhaozEngine5xVersion)</Version>`, `InternalsVisibleTo KhaozEngine.Tests`, a README, no project
refs). Add it to `KhaozEngine.slnx` and to `KhaozEngine.Tests.csproj` ProjectReferences.

### Types
```csharp
namespace KhaozEngine.Particles;

// Live particle state (current interpolated values). Public so a renderer can read it.
public struct Particle
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age;        // seconds since spawn
    public float Life;       // total lifetime seconds
    public float Size;       // current (interpolated) size
    public Vector4 Color;    // current (interpolated) RGBA
    public readonly bool Alive => Age < Life;
    public readonly float Norm => Life > 0f ? Age / Life : 1f;  // 0..1 over life
}

// Spawn parameters for a burst/emitter.
public struct EmitterConfig
{
    public float LifetimeMin, LifetimeMax;      // seconds
    public float SpeedMin, SpeedMax;            // initial speed along the spread cone
    public Vector3 Direction;                   // cone axis (normalized internally; zero => omni)
    public float SpreadDegrees;                 // cone half-angle (0 = straight, 180 = full sphere)
    public Vector3 Gravity;                     // world accel applied each second
    public float Drag;                          // velocity damping per second (0 = none)
    public float StartSize, EndSize;            // lerped over Norm
    public Vector4 StartColor, EndColor;        // lerped over Norm (alpha too — fade out via EndColor.W=0)
    public static EmitterConfig Spark { get; }  // a sensible default (short-lived, fast, fading)
    public static EmitterConfig Puff { get; }   // slow, growing, fading smoke-ish
}

public sealed class ParticleSystem
{
    public ParticleSystem(int capacity, uint seed = 1);
    public int Capacity { get; }
    public int ActiveCount { get; }
    public void Emit(in EmitterConfig cfg, Vector3 origin, int count);  // burst; clamps to remaining capacity
    public void Update(float dt);                                       // age, integrate, interpolate, recycle
    public void Clear();
    public ReadOnlySpan<Particle> Active { get; }   // the live particles, contiguous, for the renderer
}
```

### Behaviour
- **Pool**: fixed `capacity`; dead slots are reused. `Active` exposes the live prefix (keep alive particles
  compacted to the front via swap-remove on death, so `Active` is a contiguous span of `ActiveCount`).
- **Emit**: spawns up to `min(count, capacity - ActiveCount)`. Each particle: `Life` = rand(LifetimeMin..Max);
  initial direction = `Direction` perturbed within `SpreadDegrees` (uniform in the cone; if `Direction` is
  ~zero, emit omnidirectionally); `Velocity` = dir * rand(SpeedMin..Max); `Position` = origin; `Age`=0.
- **Update(dt)**: for each live particle: `Age += dt`; if `Age >= Life` recycle (swap-remove). Else
  `Velocity += Gravity*dt`; `Velocity *= max(0, 1 - Drag*dt)`; `Position += Velocity*dt`;
  `Size = lerp(StartSize, EndSize, Norm)`; `Color = lerp(StartColor, EndColor, Norm)`. Store the cfg per
  particle (or a compact subset) so interpolation works after the emitter is gone — keep a parallel array of
  the per-particle start/end size+color+gravity+drag, OR store the EmitterConfig index; simplest is to copy
  the needed lerp endpoints + gravity + drag into the Particle/private arrays at Emit time.
- **Determinism**: a small internal xorshift32 RNG seeded by `seed`. Two systems with the same seed + same
  Emit/Update calls produce identical particles. No `System.Random`, no `Math.Random`, nothing wall-clock.
- A tiny `RateAccumulator` helper (optional, public) for continuous emission: `Advance(dt, ratePerSec)`
  returns an int count to emit this frame (accumulates fractional remainder). Keep it a separate small struct;
  bursts are the primary use.

### Tests (headless, deterministic)
- Emit adds `count` (and clamps at capacity); `ActiveCount` tracks alive.
- `Update` ages particles; a particle with `Life=0.5` is gone after `Update(0.6)` (recycled, ActiveCount drops).
- Size/Color interpolate: right after Emit `Norm≈0` => ~StartSize/StartColor; near end => ~EndSize/EndColor.
- Gravity integrates (a downward gravity moves Position.Y down over steps); Drag reduces speed.
- Determinism: two `ParticleSystem(cap, seed:42)` with identical Emit+Update sequences have identical `Active`.
- Spread: with `SpreadDegrees=0` all velocities are parallel to `Direction`; with `180` they vary.
- `RateAccumulator`: 10/sec over 1.0s of dt steps emits 10 (±fractional carry correctness).

## Part B — Render3D billboards (`Scene3D.DrawBillboard`)

Add camera-facing soft-disc billboard rendering, hooked like the debug-line overlay (drawn after the post
chain into `target`, with `Camera.ViewProjection`).

### API (Scene3D, additive; per-frame, cleared in `Begin()`)
```csharp
public enum BillboardBlend { Alpha, Additive }
public void DrawBillboard(Vector3 worldPos, float size, Vector4 color, BillboardBlend blend = BillboardBlend.Alpha);
```
A convenience to splat a whole system:
```csharp
// (Render3D references KhaozEngine.Particles for this overload only — acceptable; both are 5.x.
//  If you'd rather avoid the dependency, OMIT this overload and let the game loop over Active itself.)
public void DrawParticles(ReadOnlySpan<Particle> particles, BillboardBlend blend = BillboardBlend.Additive);
```
Decision: AVOID the Render3D->Particles dependency. Do NOT add `DrawParticles`. The game iterates
`system.Active` and calls `DrawBillboard(p.Position, p.Size, p.Color, Additive)` per particle. Keeps Render3D
independent of the sim. (Note this choice in the changelog.)

### Rendering
- New internal `Rendering/BillboardRenderer.cs` (mirror `LineRenderer`): builds a camera-facing quad per
  billboard. Camera basis from `Camera.Forward`: `right = normalize(cross(Vector3.UnitY, forward))` (fallback
  to UnitX if forward∥UnitY), `up = normalize(cross(forward, right))`. Quad corners =
  `center ± right*size ± up*size`, with UVs (0,0)..(1,1) and the per-billboard colour. TriangleList, depth
  disabled (overlay), `FaceCullMode.None`. TWO blend modes: alpha (`BlendAttachmentDescription.AlphaBlend`)
  and additive (`SourceAlpha`/`One`, color add) — two pipelines sharing one vertex/UBO layout, or one pipeline
  rebuilt per blend group. UBO = one mat4 ViewProj (64 bytes). Growable vertex buffer.
- Shaders (new in `Internal/ShaderSources.cs`): `BillboardVert` (transform by ViewProj, pass uv + color),
  `BillboardFrag` (soft disc: `float d = length(vUv*2.0-1.0); float a = smoothstep(1.0, 0.55, d);
  oColor = vec4(vColor.rgb, vColor.a * a);`). Single colour target.
- `Scene3D`: hold a `BillboardRenderer` + two `List<BillboardVertex>` (alpha + additive); `Begin()` clears
  them; `DrawBillboard` appends a quad (6 verts) to the right list; in `RenderInternal` AFTER the debug-line
  pass, draw additive group then alpha group into `target`. Dispose the renderer + the layout (don't repeat
  the LineRenderer ResourceLayout leak — store + dispose it).

### Tests
- Headless: a pure `BillboardGeometry` helper that builds the 4 corners (or 6 verts) from center/size/right/up
  — assert the quad is centred on `center`, planar, and `size`-scaled; assert UVs. (The GPU draw + soft-disc
  shader are snapshot-verified, not unit-tested.)
- Visual (controller, Render3DSnapshot): emit a `Spark` burst + a `Puff`, `Update` a few frames, draw via
  `DrawBillboard` (additive for sparks) over a lit scene — confirm soft round glowing particles face the
  camera and blend. Asymmetric scene.

## Files
- Create `KhaozEngine.Particles/` (csproj + README + `Particle.cs`/`EmitterConfig.cs`/`ParticleSystem.cs`/
  `RateAccumulator.cs` + internal xorshift RNG); add to `KhaozEngine.slnx` + `KhaozEngine.Tests.csproj`.
- Create `KhaozEngine.Render3D/Rendering/BillboardRenderer.cs` + `BillboardGeometry.cs`; modify
  `Scene3D.cs` (+ `Internal/ShaderSources.cs`).
- Tests: `KhaozEngine.Tests/Particles/*Tests.cs`, `KhaozEngine.Tests/Render3D/BillboardGeometryTests.cs`.
- Release: bump `<KhaozEngine5xVersion>` 5.20.0 -> 5.21.0-experimental, CHANGELOG, pack the 5.x packages
  (now SIX: Windowing/Render2D/Render3D/Audio/Gui/**Particles**).
