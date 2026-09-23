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
- `DeterministicRng` - seeded xorshift128+-derived recurrence (splitmix64 init), reproducible across .NET
  versions and platforms. `State` get/set for save/resume, `CreateDerived("combat")` for decorrelated
  per-subsystem streams, `StableHash` for a platform-stable string hash. Derived, not canonical: the state
  update is Vigna's xorshift128+ word for word, but the returned sum is taken after that update rather than
  before it, so the stream is not the one the studied generator emits. The output stream is a shipped
  contract (persisted `State`, seed-generated content), so the variant is pinned by known vectors and stays.
- `XorRng` - tiny xorshift32 value-type PRNG for allocation-free hot paths (particles, audio noise).
  Copy the struct to snapshot. Use `DeterministicRng` when you need resume or derived streams.
- `IRandomSource` / `SeededRandomSource` / `CryptographicRandomSource` - the engine's one gameplay-randomness
  seam, for a loot draw, a craft roll, a spawn choice or a decoder fuzzer. Five members
  (`NextInt(minInclusive, maxExclusive)`, `NextULong`, `NextRollPosition` over 0 to 65535, `NextBytes`,
  `Skip`), no
  float, and deliberately NO `Seed`, `State` or `CreateDerived`: a source whose seed is readable is a source
  a crafting system can leak, and a durable record carries the resolved outcome rather than a seed or a draw
  index. `NextInt` throws `ArgumentOutOfRangeException` on an empty range, which is a caller bug rather than
  a draw, and a one-wide range answers without consuming a draw. `Skip` advances the stream by exactly one
  draw, for a caller that must consume a draw it will not use: it is what a position-stable generator
  discards with, because `NextInt(0, 1)` consumes nothing and so leaves the stream where a skipped call
  would. It is a default interface method (`NextInt(0, 2)` discarded), so a foreign implementation gets the
  advance for free. `SeededRandomSource` overrides it to take exactly one draw off the wrapped generator and
  `CryptographicRandomSource` overrides it to do nothing, because a cryptographic stream has no position to
  advance. `CryptographicRandomSource` draws from the
  OS through `System.Security.Cryptography.RandomNumberGenerator` and is what a hosted server runs.
  `SeededRandomSource(ulong seed)` wraps `DeterministicRng`, so a seeded replay and a test reuse the engine's
  one seeded stream definition, and the seed is not readable back off the instance. Both bound a draw by
  rejection sampling rather than modulo, because modulo bias on a crafting roll is an edge a player can farm.
  A consumer takes one as a constructor parameter: there is no ambient instance and no default, because a
  default is how a production server ends up on the test source.
- `RandomDraw` - a bounded draw off an `IRandomSource` that always costs exactly one draw, so a stream stays
  position stable when a tunable bound collapses. `Below(rng, exclusiveMax)` answers `[0, exclusiveMax)`, and a
  bound of 0 or 1 answers 0 through `Skip` rather than a one-wide `NextInt` that would consume nothing.
  `UpTo(rng, inclusiveMax)` answers `[0, inclusiveMax]`, the form a roll ladder is quoted in, and refuses
  `int.MaxValue`. A negative bound is refused with `ArgumentOutOfRangeException` before any draw is spent.
  Holds no state and decides no probability: the distribution is `NextInt`'s.
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
- `SafeAreaInsets` - finite, non-negative top/bottom/left/right insets in design units, supplied by the
  platform host. `Apply(viewport.DesignBounds)` produces the usable layout rect. Opposing insets that
  consume an axis yield an empty rect on that axis. `Zero` leaves bounds unchanged.
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
