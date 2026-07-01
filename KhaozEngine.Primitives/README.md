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
- `IDesignViewport` - the fakeable design-viewport seam (design size, scale + letterbox offset,
  screen to design mapping) that rendering, layout, and headless tests target. Moved here from
  Windowing in 9.0.0, which carries the concrete `DesignViewport`.
- `ObjectPool<T>` / `IPoolable` - fixed-capacity free-list pool (absorbed from the retired
  `KhaozEngine.Pooling` package in 9.0.0). Items are prewarmed via a factory, `Rent` returns null on
  exhaustion, `Return`/`Clear` call `Reset`, and the active set stays compacted so
  `GetActive(0..ActiveCount-1)` visits every live item with no gaps.

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
