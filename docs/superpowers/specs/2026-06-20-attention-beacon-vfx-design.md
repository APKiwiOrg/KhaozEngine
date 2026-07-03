# AttentionBeacon VFX - design

Status: approved design, pending spec review.
Target engine version: **7.5.0** (additive minor; current line is 7.4.0).
Module: `KhaozEngine.Render2D.Vfx` (ships inside the `KhaozEngine.Render2D` package).

## Goal

A reusable, game-agnostic "look at me" pulse drawn at a world/screen point: expanding sonar-ping
rings plus a configurable number of twinkling glints around the center. Generic VFX every game wants
(pickups, quest markers, objectives, drop pods). Lives next to `EnergyBeam` and follows the same
stateless, time-driven, texture-driven shape.

First consumer is Nullwake's timed-reward tappables (replacing a bespoke sine-pulsed
`DrawFilledCircle` aura), but nothing in this design is Nullwake-specific. Nullwake adoption is a
separate follow-up after this ships and Nullwake pins the new version.

## Public API

Two new files in `KhaozEngine.Render2D/Vfx/`, mirroring the `EnergyBeam` / `BeamParams` pair.

### `AttentionBeacon` (static, stateless)

```csharp
public static class AttentionBeacon
{
    public static void Draw(SpriteBatch batch, Texture2D? ring, Texture2D? glow,
        Vector2 center, in AttentionBeaconParams p, float timeSeconds);
}
```

- `timeSeconds` is caller-owned elapsed time (Nullwake feeds an unscaled real-time accumulator so the
  pulse keeps animating regardless of game `TimeScale`). Same time always renders the same frame - no
  hidden mutable state, mirroring `EnergyBeam.Draw`.
- `ring` is the soft annulus texture for the sonar rings (a `null` ring skips the rings). `glow` is the
  radial-glow texture for the glints (a `null` glow skips the glints). Same null-texture handling as
  `EnergyBeam`'s optional `glow`.
- Composited **additively** (set and restored around the draw), like `EnergyBeam`. Additive reads well
  for a bright pulse on a dark scene; documented on the type.
- `RingCount == 0` skips the rings; `GlintCount == 0` skips the glints; both zero draws nothing.
- No per-frame allocation: `in` param, no LINQ, no closures, no temporary collections.

### `VfxRenderer.DrawAttentionBeacon` (convenience)

```csharp
public void DrawAttentionBeacon(SpriteBatch batch, Vector2 center, float timeSeconds,
    in AttentionBeaconParams p);
```

Supplies `VfxRenderer`'s owned `RingTexture` + `GlowTexture`, mirroring `DrawBeam`. This is the bare
call site the consumer wants: `renderer.DrawAttentionBeacon(sb, center, time, p)`.

### `AttentionBeaconParams` + `GlintStyle`

```csharp
public enum GlintStyle { Disc, Star }

public readonly record struct AttentionBeaconParams
{
    public Color Color { get; init; }          // default white
    public float Intensity { get; init; }       // 0..1 master alpha multiplier, default 1

    public int   RingCount { get; init; }       // default 3
    public float RingPeriod { get; init; }      // seconds, default 2.4
    public float InnerRadius { get; init; }     // px, default 6
    public float MaxRadius { get; init; }       // px, default 48
    public float RingThickness { get; init; }   // relative band thickness, 1 = texture-native, default 1

    public int   GlintCount { get; init; }      // default 4
    public float GlintRadius { get; init; }     // spread around center (px), default 28
    public float GlintSize { get; init; }       // px, default 6
    public float TwinkleRate { get; init; }     // rad/s, default 6
    public GlintStyle GlintStyle { get; init; } // default Star

    public static AttentionBeaconParams Default => /* the preset above */;
}
```

A `readonly record struct` cannot carry non-zero field defaults through a bare `new()` (every field is
zero, so `new()` is a no-op that draws nothing). This follows the established `BeamParams.Default`
convention: `Default` is the sensible preset; derive variants with `with`.

## Geometry (pure, testable helpers)

All math lives in `internal static` pure helpers so it is unit-testable without a GPU (the same split
`EnergyBeam` uses: `Axis`, `DashAlpha`, `RoundCaps`).

### Rings

For ring `i` of `RingCount`, at `time` with period `RingPeriod`:

- `RingPhase(i, ringCount, time, period)` = `frac(time / period + i / ringCount)` in `[0, 1)`. The
  `i / ringCount` term evenly phase-staggers the rings.
- `RingRadius(phase, inner, max)` = `lerp(inner, max, phase)` - grows monotonically from `InnerRadius`
  to `MaxRadius` across the period, then resets.
- `RingAlpha(phase)` = `1 - phase` - ~1 at the inner radius, ~0 as it reaches `MaxRadius`. Multiplied
  by `Intensity` and the params `Color.A` for the final tint alpha.
- `RingDiameter(bandRadius, ringThickness, bandCenterFraction)` = `2 * bandRadius * ringThickness /
  bandCenterFraction`. The soft `ring` texture's bright band sits at a known fraction of its
  half-extent (`bandCenterFraction`, the `BakeRing` default ≈ 0.675); dividing places the band at
  `bandRadius`. `RingThickness` scales the quad: `>1` thicker/larger soft band, `<1` tighter. At the
  default `1.0` the band centers on `bandRadius`; off-default values trade a slight radial shift for
  thickness, which is the intuitive meaning of a thicker ring. One smooth draw per ring (no segment
  lumps).

Each ring is one additive `batch.Draw(ring, center, (d, d), (0.5, 0.5), 0, FullUV, tint)` with
`d = RingDiameter(...)` - the same centered-quad call `EnergyBeam.DrawDisc` uses.

### Glints

For glint `j` of `GlintCount`, deterministic placement from the index via a pure hash (no per-frame
RNG, no allocation):

- `GlintAngle(j, glintCount)` = `j * GoldenAngle` (≈ 2.39996 rad) - spreads glints over distinct
  angles without clumping; stable across calls for the same `j`.
- `GlintRadiusFactor(j)` = a stable per-index value in `[~0.6, 1.0]` from a small integer hash of `j`,
  so glints sit at varied radii within `GlintRadius` rather than a perfect circle.
- position = `center + (cos a, sin a) * (GlintRadius * factor)`.
- `GlintAlpha(j, time, twinkleRate)` = `0.5 + 0.5 * sin(time * twinkleRate + phase_j)`, clamped `>= 0`,
  with `phase_j` an index-derived offset so each glint twinkles independently. Multiplied by
  `Intensity` and `Color.A`.

Glint draw by `GlintStyle`:

- `Disc`: one soft glow dot - `batch.Draw(glow, pos, (GlintSize, GlintSize), center-origin, 0, FullUV,
  tint)`.
- `Star`: a tiny 4-point sparkle - two crossed additive quads stretched from the `glow` texture (a
  long thin horizontal quad + a long thin vertical quad, each `GlintSize` long and a fraction wide),
  so it reads as a twinkle. Uses only the `glow` texture (no white pixel needed in the signature).

## Testing

Unit tests (`KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs`), all on the pure helpers, no
GPU:

- Ring radius grows monotonically across a period and resets at the period boundary.
- Ring alpha ≈ 1 at `InnerRadius` (phase 0) and ≈ 0 at `MaxRadius` (phase → 1).
- Rings are evenly phase-staggered (`RingPhase(i)` offsets are uniform across `i`).
- `RingDiameter`: `RingThickness > 1` yields a larger diameter than `1`; at `1.0` the band centers on
  `bandRadius`.
- Glint angle/radius are stable across repeated calls for the same index, and angles for distinct
  indices are distinct / well spread.
- Glint twinkle alpha stays in range and is non-negative.
- Zero counts: `RingCount = 0` and `GlintCount = 0` produce no geometry (assert via the count guards /
  helper short-circuits).

GPU smoke test (`KhaozEngine.Tests/Gpu/VfxGpuTests.cs`, gated by `KE_GPU_TESTS=1`, matching the
existing beam smoke test): draw a beacon to a render target and assert it runs and writes pixels for
non-zero counts. Existing golden snapshots are unaffected (new draw, no change to existing paths).

## Release ritual (per CLAUDE.md)

1. Bump `<KhaozEngineVersion>` 7.4.0 → 7.5.0 in `Directory.Build.props`.
2. `CHANGELOG.md` newest-first detailed entry; `CHANGENOTES.md` one-line digest (same commit).
3. Update the three guard-checked declarations to 7.5.0 (`docs/CONSUMERS.md` engine version,
   `docs/ROADMAP.md` current released version, `README.md` `<PackageReference>` example) - run
   `scripts/check-doc-versions.sh`.
4. Document the new API in `docs/USING-KHAOZENGINE.md`.
5. `dotnet pack -c Release -o ./local-feed`; commit; `git tag v7.5.0`; push `main` + tag.
6. Report back the final API name(s) + shipped version so Nullwake can reconcile and pin.

Nullwake adoption (bump pin, swap `DrawTimedRewardNodes` aura, optionally the edge indicator, map
tunables onto `TimedRewardDefinition` / `timed_rewards.json` / schema) is out of scope here - a
separate follow-up after 7.5.0 ships.
