# KhaozEngine.Primitives

The zero-dependency leaf at the bottom of the KhaozEngine dependency graph (`System.Numerics` only,
no renderer, no window). Shared value types and pure helpers every other package builds on. An RGBA
color anywhere in the engine's public API is the `Color` from here.

- `Color` - RGBA float struct, a typed wrapper over `Vector4` (implicit to `Vector4`, explicit back).
  `FromBytes`, `FromHex`/`ToHex`, `WithAlpha`, `* float`, unclamped `Lerp`.
- `DeterministicRng` - seeded xorshift128+ (splitmix64 init), reproducible across .NET versions and
  platforms. `State` get/set for save/resume, `CreateDerived("combat")` for decorrelated per-subsystem
  streams, `StableHash` for a platform-stable string hash.
- `XorRng` - tiny xorshift32 value-type PRNG for allocation-free hot paths (particles, audio noise).
  Copy the struct to snapshot. Use `DeterministicRng` when you need resume or derived streams.
- `MathUtil` - `Clamp01`, `Lerp`, `InverseLerp`, `SmoothStep(a, b, x)` (clamped Hermite).
- `Easing` - `Linear`/`SmoothStep`/`EaseIn`/`EaseOut`/`EaseInOut`, all clamped to [0,1].
- `ViewportMath` - `Fit` (letterbox) and `Cover` (crop) uniform-scale factors for aspect-preserving fits.
- `Rect` - axis-aligned pixel rect (top-left origin) with `Contains`, the input hit-testing rect.
- `RayMath` - allocation-free 3D ray-intersection helpers (`System.Numerics`), zero-dependency leaf math for
  editor picking and future spatial queries. `IntersectAabb(origin, direction, min, max, out tNear)` is a slab
  test against an axis-aligned box, true at `t >= 0` with `tNear` the entry distance (0 when the origin starts
  inside). Directions need not be normalized, `tNear` is in units of the direction's length, and an
  axis-parallel ray hits only when the origin already lies within that axis's slab (no division, no NaN risk).
- `IDesignViewport` - the fakeable design-viewport seam (design size, scale + letterbox offset,
  screen to design mapping, and the `DesignBounds`/`ContentBounds`/`WindowBounds` rects) that rendering,
  layout, and headless tests target. Moved here from Windowing in 9.0.0, which carries the concrete
  `DesignViewport`. `WindowBounds` (10.38.0) is the whole window mapped into design space - `DesignBounds`
  plus the letterbox bars - so a full-window fill covers the bars; it is a default-interface-member derived
  from the scale + offset and reduces to `DesignBounds` when unletterboxed.
- `ObjectPool<T>` / `IPoolable` - fixed-capacity free-list pool (absorbed from the retired
  `KhaozEngine.Pooling` package in 9.0.0). Items are prewarmed via a factory, `Rent` returns null on
  exhaustion, `Return`/`Clear` call `Reset`, and the active set stays compacted so
  `GetActive(0..ActiveCount-1)` visits every live item with no gaps.
- `TrailSampler` / `TrailPoint` - a pure, render-free ring of timed motion-trail samples bounded by a max
  age and a max count. `Add(position, nowSeconds)` appends and evicts aged/overflow from the oldest end,
  `Prune(nowSeconds)` decays the tail while the emitter idles, and `Samples` returns the live tail
  oldest-first to hand straight to `Scene3D.DrawTrail` (Render3D). No GPU dependency; headless-testable.
  Feed it the moving emitter's world position each frame (a sword tip, a thruster nozzle, a projectile).
- `NumberFormatter` / `NumberNotation` - large-number display formatting for idle/incremental values in one
  place: `Simple` short suffixes (1.23K, 45.6M ... up to 1e33 `Dc`, then scientific), `Scientific`, and
  `Engineering` (exponent a multiple of 3). A settable process-wide `Notation` default a game binds to its
  setting once, plus per-call notation overloads; `Format` / `FormatInt`. NaN -> "0", infinity -> "Inf",
  culture-invariant output. A non-localizable value token: format here, compose into a localized string.
- `TimeFormatter` / `DurationStyle` - duration formatting in two shapes: `Clock` (the ticking colon clock
  `1:02:34`, rounds up to the next whole second) and `Coarse` (the two-unit summary `2h 15m`, with a
  `coarseUnits` knob). Non-finite -> "---", non-positive -> "0s", culture-invariant.

```csharp
var rng = new DeterministicRng(seed: 12345);
var combat = rng.CreateDerived("combat");   // isolated, reproducible stream
int roll = combat.Next(1, 21);

Color tint = Color.FromHex("#FF8800").WithAlpha(0.5f);
```

```csharp
var pool = new ObjectPool<Bullet>(() => new Bullet(), prewarmCount: 64);
Bullet? b = pool.Rent();                    // null when exhausted
for (int i = 0; i < pool.ActiveCount; i++)
    pool.GetActive(i).Update(dt);
pool.Return(b!);                            // calls b.Reset()
```

```csharp
bool hit = RayMath.IntersectAabb(ray.Origin, ray.Direction, box.Min, box.Max, out float tNear);
Vector3 hitPoint = ray.Origin + ray.Direction * tNear;   // tNear is in units of Direction's length
```
