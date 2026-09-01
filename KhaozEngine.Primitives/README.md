# KhaozEngine.Primitives

The zero-dependency leaf at the bottom of the KhaozEngine dependency graph (`System.Numerics` only,
no renderer, no window). Shared value types and pure helpers every other package builds on. An RGBA
color anywhere in the engine's public API is the `Color` from here.

**Frameworks: `net8.0` and `net10.0`.** This leaf multi-targets `net8.0` alongside the engine-wide
`net10.0` because `KhaozEngine.ServerStatus` depends on it and must run on an Azure Functions app on the
Linux Consumption plan, which has no .NET 10. A `net10.0` consumer resolves the `net10.0` asset
automatically, so this is transparent to every other package.

- `Color` - RGBA float struct, a typed wrapper over `Vector4` (implicit to `Vector4`, explicit back).
  `FromBytes`, `FromHex`/`ToHex`, `WithAlpha`, `ScaleRgb` (scale RGB, keep alpha - dim a color without
  making it translucent, unlike `* float`), `ScaleRgbClamped` (`ScaleRgb`, but each scaled channel clamped
  to 0..1 - a brighten factor that would otherwise overshoot 1.0), `* float`, unclamped `Lerp`.
- `DeterministicRng` - seeded xorshift128+ (splitmix64 init), reproducible across .NET versions and
  platforms. `State` get/set for save/resume, `CreateDerived("combat")` for decorrelated per-subsystem
  streams, `StableHash` for a platform-stable string hash.
- `XorRng` - tiny xorshift32 value-type PRNG for allocation-free hot paths (particles, audio noise).
  Copy the struct to snapshot. Use `DeterministicRng` when you need resume or derived streams.
- `StableHash` (14.9.0) - stateless, allocation-free integer hashing: `Mix(uint)`, `Mix(uint, uint)`,
  `Mix(uint, uint, uint)` fixed-arity hashes (FNV-1a accumulate + a Murmur3-style avalanche finalizer) and
  `ToUnitFloat(uint)` folding bits to a float in [0, 1). A pure key-to-value map (same inputs, same hash on every
  machine), distinct from `XorRng`'s stateful stream, for deterministic procedural content keyed off ids/coordinates
  with no shared RNG. `XorRng.NextFloat` shares its `ToUnitFloat` fold, so a hashed value and a stream draw land in
  [0, 1) off the same bits identically. The uint-keyed 32-bit sibling of `DeterministicRng.StableHash(string) -> ulong`.
- `MathUtil` - `Clamp01`, `Lerp`, `InverseLerp`, `SmoothStep(a, b, x)` (clamped Hermite), plus the angle
  helpers `WrapAngle` (radians to the half-open `(-pi, pi]`, so `-pi` comes back as `+pi`), `DeltaAngle`
  (shortest signed rotation), `MoveTowardsAngle(current, target, maxDelta)` (bounded shortest-arc step, no
  overshoot, a non-positive `maxDelta` holds still) and `LerpAngle` (shortest-arc interpolation, `t`
  unclamped like `Lerp`).
- `Easing` - `Linear`/`SmoothStep`/`EaseIn`/`EaseOut`/`EaseInOut`, all clamped to [0,1].
- `ViewportMath` - `Fit` (letterbox) and `Cover` (crop) uniform-scale factors for aspect-preserving fits, plus
  `CoverAnchored` (the rect form of `Cover`: cover a viewport at a uniform scale with the image's normalized
  anchor pinned to a screen point, enlarged to reach every edge from an off-centre anchor - camera-tracked
  backgrounds) and device-pixel snapping (`SnapToDevicePixel` / `SnapRectToDevice` / `SnapLengthToDevice`).
- `Rect` - axis-aligned pixel rect (top-left origin) with `Contains`, the input hit-testing rect.
- `RayMath` - allocation-free 3D ray-intersection helpers (`System.Numerics`), zero-dependency leaf math for
  editor picking and future spatial queries. `IntersectAabb(origin, direction, min, max, out tNear)` is a slab
  test against an axis-aligned box, true at `t >= 0` with `tNear` the entry distance (0 when the origin starts
  inside). Directions need not be normalized, `tNear` is in units of the direction's length, and an
  axis-parallel ray hits only when the origin already lies within that axis's slab (no division, no NaN risk).
  A degenerate zero-length ray (zero direction) hits, at `tNear` 0, only when the origin already lies inside
  the box on every axis. A NaN component in either the origin or the direction always misses (without the
  explicit check, the all-false NaN comparisons would fall through the slab test as an always-pass hit).
  `IntersectObbY(origin, direction, center, yaw, min, max, out tNear)` is the same test against a box that is
  axis-aligned in its own frame and yawed about world Y, the shape a placed prop, actor or clickbox has in a
  Y-up world. `center` is the box's world anchor and `min`/`max` are its extents in the box's local frame, so
  they are relative to that anchor. It untranslates and unrotates the ray and defers to `IntersectAabb`, so
  every edge case above holds unchanged and a `yaw` of 0 gives the same answer as `IntersectAabb` with the
  anchor subtracted out.
- `WorldFrame` - a quantized planar frame for large worlds, the floating-origin primitive: a `(short X, short Z)`
  index onto a 128 m grid whose `Anchor` is `(X, 0, Z) * Grid` metres, always exactly representable in float32.
  `Nearest(world)` rounds (never floors) so a freshly anchored local lies inside half a grid, which is what makes
  a re-anchor an EXACT translation that introduces no error at all. `ToLocal`/`ToWorld`/`ToLocalXz`/`ToWorldXz`
  convert, `DeltaTo(target)` is the translation that carries a local into another frame, and
  `ShouldReanchor(local)` is the hysteresis policy (past `ReanchorRadius` = 96 m, guaranteeing at least 64 m of
  travel between consecutive re-anchors). `Grid` is a CONSTANT, not a knob: two peers on different grids decode
  different world positions from the same bytes. `MaxLocalRadius` is the sizing ceiling the measured divergence
  budget gives (`Divergence20sUlps` ULPs of the coordinate per 20 s window against `DivergenceBudgetMetres`).
  `default` is the world origin, so a game that never leaves it is byte-identical to the pre-frame engine. Y is
  NEVER framed. `Scene3D.RenderOrigin` uses `Nearest(...).Anchor` for camera-relative rendering, and on the
  simulation side it is the stamp `ReplicatedPosition.Frame` carries and the frame a `WorldServer` island
  anchors to (`WorldServerConfig.FrameAnchoring`).
- `IDesignViewport` - the fakeable design-viewport seam (design size, scale + letterbox offset,
  screen to design mapping, and the `DesignBounds`/`ContentBounds`/`WindowBounds` rects) that rendering,
  layout, and headless tests target. Moved here from Windowing in 9.0.0, which carries the concrete
  `DesignViewport`. `WindowBounds` (10.38.0) is the whole window mapped into design space - `DesignBounds`
  plus the letterbox bars - so a full-window fill covers the bars; it is a default-interface-member derived
  from the scale + offset and reduces to `DesignBounds` when unletterboxed.
- `ObjectPool<T>` / `IPoolable` / `PoolRental<T>` - fixed-capacity free-list pool (absorbed from the
  retired `KhaozEngine.Pooling` package in 9.0.0). Items are prewarmed via a factory, `Return`/`Clear`
  call `Reset`, and the active set stays compacted so `GetActive(0..ActiveCount-1)` visits every live
  item with no gaps. Rent through `TryRent(out PoolRental<T>)`, which is `false` when the pool is
  exhausted and otherwise hands out a handle naming THAT RENTAL rather than the slot. `Return(in
  rental)` refuses a rental that is already over (returned once, or its slot rented out again since)
  with a `StalePoolReturnException`, and `TryReturn(in rental)` is the non-throwing half for an
  idempotent dispose or a `finally` block. `PoolRental<T>` is a `readonly struct` passed by `in`, so
  the pool still allocates nothing per rent or return.
  The older `Rent()` / `Return(item)` pair still works and is unchanged, but it identifies rentals by
  the item reference alone, and successive rentals of a slot are the same object, so a stale return
  frees the current renter's item out from under it. New code takes the handle.
- `TrailSampler` / `TrailPoint` - a pure, render-free ring of timed motion-trail samples bounded by a max
  age and a max count. `Add(position, nowSeconds)` appends and evicts aged/overflow from the oldest end,
  `Prune(nowSeconds)` decays the tail while the emitter idles, and `Samples` returns the live tail
  oldest-first to hand straight to `Scene3D.DrawTrail` (Render3D). No GPU dependency; headless-testable.
  Feed it the moving emitter's world position each frame (a sword tip, a thruster nozzle, a projectile).
- `NumberFormatter` / `NumberNotation` - large-number display formatting for idle/incremental values in one
  place: `Simple` short suffixes (1.23K, 45.6M ... up to 1e33 `Dc`, then scientific), `Scientific`, and
  `Engineering` (exponent a multiple of 3). A settable process-wide `Notation` default a game binds to its
  setting once, plus per-call notation overloads; `Format` / `FormatInt`. NaN -> "0", infinity -> "Inf",
  culture-invariant output. Magnitudes below 1 automatically gain enough decimal places to stay truthful
  (0.05 -> "0.05", never rounds up to "0.1") unless the call explicitly asks for zero small-value decimals
  (`FormatInt`'s integer-count contract is unaffected). A non-localizable value token: format here, compose
  into a localized string.
- `TimeFormatter` / `DurationStyle` - duration formatting in two shapes: `Clock` (the ticking colon clock
  `1:02:34`, rounds up to the next whole second) and `Coarse` (the two-unit summary `2h 15m`, with a
  `coarseUnits` knob). Non-finite -> "---", non-positive -> "0s", culture-invariant.
- `VersionComparer` - numeric, dot-separated `x.y.z` version comparison. Each segment compares
  numerically (`0.7.10` orders after `0.7.9`, unlike a string compare), a missing or non-numeric segment
  counts as 0 (`1.2` equals `1.2.0`), and a null or blank string is the empty all-zero version (never
  throws). The one shared rule behind `KhaozEngine.Updates.UpdateVersion.IsNewer` and
  `KhaozEngine.ServerStatus.VersionOrder.Compare`/`IsBelow`, both thin wrappers over `Compare` here.
- `RenderFrameStats` - per-frame render cost counters (draw calls, instances, triangles, buffer-update bytes,
  and the 2D quads / flushes / texture switches). A plain value type summed with `+`, so a host aggregates
  several surfaces' tallies into one frame total. Populated always-on by `Render2D.SpriteBatch.FrameStats` and
  `Render3D.Scene3D.LastFrameStats`, shown by the `Gui.DiagnosticsHud` / `DiagnosticsOverlay.DrawStatsSection`.

```csharp
var rng = new DeterministicRng(seed: 12345);
var combat = rng.CreateDerived("combat");   // isolated, reproducible stream
int roll = combat.Next(1, 21);

Color tint = Color.FromHex("#FF8800").WithAlpha(0.5f);
```

```csharp
var pool = new ObjectPool<Bullet>(() => new Bullet(), prewarmCount: 64);
if (pool.TryRent(out PoolRental<Bullet> rental))   // false when exhausted
{
    Bullet b = rental.Item!;
    for (int i = 0; i < pool.ActiveCount; i++)
        pool.GetActive(i).Update(dt);
    pool.Return(in rental);                 // calls b.Reset(); refuses a rental that is already over
}
```

```csharp
bool hit = RayMath.IntersectAabb(ray.Origin, ray.Direction, box.Min, box.Max, out float tNear);
Vector3 hitPoint = ray.Origin + ray.Direction * tNear;   // tNear is in units of Direction's length

// A placed prop: local extents around its world anchor, yawed about Y.
bool onProp = RayMath.IntersectObbY(
    ray.Origin, ray.Direction, prop.Position, prop.YawRadians, prop.LocalMin, prop.LocalMax, out float t);
```
