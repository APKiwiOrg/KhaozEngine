# AttentionBeacon VFX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable `AttentionBeacon` VFX to `KhaozEngine.Render2D.Vfx` that draws an attention pulse (expanding sonar rings + twinkling glints) at a point, mirroring the existing `EnergyBeam`.

**Architecture:** A stateless static `AttentionBeacon.Draw(batch, ring?, glow?, center, in params, time)` composites additively, with all geometry/animation in pure `internal static` helpers (unit-tested headlessly, exactly like `EnergyBeam.Axis`/`DashAlpha`). A `readonly record struct AttentionBeaconParams` (+ `GlintStyle` enum) carries the tunables with a `Default` preset. `VfxRenderer.DrawAttentionBeacon` is a thin convenience that supplies the owned `RingTexture`/`GlowTexture`, mirroring `DrawBeam`.

**Tech Stack:** C# / net10.0, `System.Numerics`, `KhaozEngine.Primitives.Color`, xUnit. Render via `SpriteBatch` (MonoGame-free). Ships in the `KhaozEngine.Render2D` package under the shared `<KhaozEngine5xVersion>` line.

**Working directory:** worktree `feature/attention-beacon` (branch `worktree-feature+attention-beacon`), branched off `origin/main` at 7.4.0. Target release: **7.5.0**.

**Reference files (read for patterns, do not change in Tasks 1-5):**
- `KhaozEngine.Render2D/Vfx/EnergyBeam.cs` - static Draw + pure helpers + additive blend set/restore + `DrawDisc` centered-quad.
- `KhaozEngine.Render2D/Vfx/BeamParams.cs` - `readonly record struct` + `Default` preset + `BeamCap` enum.
- `KhaozEngine.Render2D/Vfx/VfxRenderer.cs` - owns `RingTexture`/`GlowTexture`/`WhitePixel`; `DrawBeam` wrapper.
- `KhaozEngine.Render2D/Vfx/VfxTextures.cs` - `BakeRing` defaults (`innerRadius01 = 0.55`, `thickness01 = 0.25` → band center fraction `0.675`).
- `KhaozEngine.Tests/Render2D/Vfx/EnergyBeamTests.cs` - headless helper-test style (`const float Tol = 1e-4f;`).
- `KhaozEngine.Tests/Gpu/VfxGpuTests.cs` - `[GpuFact]`, `Render2DSnapshot.Capture`, `Lum` pixel asserts.

**Key API facts (verified):**
- `SpriteBatch.Draw(Texture2D tex, Vector2 position, Vector2 size, Vector2 originNormalized, float rotation, Vector4 srcUV, Color color)` - centered draw uses origin `(0.5f, 0.5f)`.
- `PrimitiveRenderer.FullUV` = `(0,0,1,1)`.
- `Color.White` exists; `Color * float` scales all channels (RGBA), the same fade idiom `EnergyBeam` uses (`color * alpha`).
- `BlendMode.Additive`; set `batch.BlendMode`, restore the previous value after.

---

## File Structure

- Create `KhaozEngine.Render2D/Vfx/AttentionBeaconParams.cs` - `GlintStyle` enum + `AttentionBeaconParams` record struct + `Default`.
- Create `KhaozEngine.Render2D/Vfx/AttentionBeacon.cs` - static `Draw` + pure helpers.
- Modify `KhaozEngine.Render2D/Vfx/VfxRenderer.cs` - add `DrawAttentionBeacon`.
- Create `KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs` - headless helper tests.
- Modify `KhaozEngine.Tests/Gpu/VfxGpuTests.cs` - add beacon GPU smoke tests.
- Modify (Task 7, release): `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

---

## Task 1: `AttentionBeaconParams` + `GlintStyle`

**Files:**
- Create: `KhaozEngine.Render2D/Vfx/AttentionBeaconParams.cs`
- Test: `KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the pure geometry/animation helpers behind the attention beacon renderer.</summary>
public class AttentionBeaconTests
{
    const float Tol = 1e-4f;

    [Fact]
    public void Default_HasSensiblePreset()
    {
        var p = AttentionBeaconParams.Default;
        Assert.Equal(Color.White, p.Color);
        Assert.Equal(1f, p.Intensity, Tol);
        Assert.Equal(3, p.RingCount);
        Assert.Equal(2.4f, p.RingPeriod, Tol);
        Assert.Equal(6f, p.InnerRadius, Tol);
        Assert.Equal(48f, p.MaxRadius, Tol);
        Assert.Equal(1f, p.RingThickness, Tol);
        Assert.Equal(4, p.GlintCount);
        Assert.Equal(28f, p.GlintRadius, Tol);
        Assert.Equal(6f, p.GlintSize, Tol);
        Assert.Equal(6f, p.TwinkleRate, Tol);
        Assert.Equal(GlintStyle.Star, p.GlintStyle);
    }

    [Fact]
    public void BareNew_DrawsNothing_ZeroCounts()
    {
        // A record struct's bare new() is all-zero; that means no rings and no glints (a no-op), by design.
        var p = new AttentionBeaconParams();
        Assert.Equal(0, p.RingCount);
        Assert.Equal(0, p.GlintCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AttentionBeaconTests"`
Expected: FAIL - compile error, `AttentionBeaconParams`/`GlintStyle` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Render2D/Vfx/AttentionBeaconParams.cs`:

```csharp
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>How an attention-beacon glint is drawn around the center.</summary>
    public enum GlintStyle
    {
        /// <summary>A small soft glow dot (the radial glow texture).</summary>
        Disc = 0,

        /// <summary>A tiny 4-point sparkle: two crossed soft quads stretched from the glow texture. The default.</summary>
        Star = 1,
    }

    /// <summary>
    /// Tunables for an additive "attention" pulse (see <see cref="AttentionBeacon.Draw"/>): expanding sonar-ping
    /// rings plus a ring of twinkling glints around a point. Immutable; derive variants with <c>with</c>. A bare
    /// <c>new()</c> is all-zero (no rings, no glints - a no-op); use <see cref="Default"/> for the sensible preset.
    /// </summary>
    public readonly record struct AttentionBeaconParams
    {
        /// <summary>Tint for the rings and glints.</summary>
        public Color Color { get; init; }

        /// <summary>Master alpha multiplier in [0,1] applied to every ring and glint.</summary>
        public float Intensity { get; init; }

        /// <summary>Number of expanding sonar rings. 0 disables the rings.</summary>
        public int RingCount { get; init; }

        /// <summary>Seconds for a ring to expand from <see cref="InnerRadius"/> to <see cref="MaxRadius"/> and reset.</summary>
        public float RingPeriod { get; init; }

        /// <summary>Radius (pixels) a ring starts at.</summary>
        public float InnerRadius { get; init; }

        /// <summary>Radius (pixels) a ring fades out at.</summary>
        public float MaxRadius { get; init; }

        /// <summary>Relative ring band thickness: 1 = the ring texture's native band, &lt;1 tighter, &gt;1 thicker.</summary>
        public float RingThickness { get; init; }

        /// <summary>Number of twinkling glints around the center. 0 disables the glints.</summary>
        public int GlintCount { get; init; }

        /// <summary>Spread (pixels) of the glints from the center.</summary>
        public float GlintRadius { get; init; }

        /// <summary>Size (pixels) of each glint.</summary>
        public float GlintSize { get; init; }

        /// <summary>Twinkle speed (radians/second) of the glints' alpha oscillation.</summary>
        public float TwinkleRate { get; init; }

        /// <summary>Glint shape: <see cref="GlintStyle.Disc"/> or <see cref="GlintStyle.Star"/> (default).</summary>
        public GlintStyle GlintStyle { get; init; }

        /// <summary>A white pulse with 3 sonar rings and 4 twinkling star glints (a sensible starting point).</summary>
        public static AttentionBeaconParams Default => new()
        {
            Color = Color.White,
            Intensity = 1f,
            RingCount = 3,
            RingPeriod = 2.4f,
            InnerRadius = 6f,
            MaxRadius = 48f,
            RingThickness = 1f,
            GlintCount = 4,
            GlintRadius = 28f,
            GlintSize = 6f,
            TwinkleRate = 6f,
            GlintStyle = GlintStyle.Star,
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AttentionBeaconTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/Vfx/AttentionBeaconParams.cs KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs
git commit -m "feat(render2d-vfx): AttentionBeaconParams + GlintStyle"
```

---

## Task 2: Ring geometry helpers

**Files:**
- Create: `KhaozEngine.Render2D/Vfx/AttentionBeacon.cs`
- Test: `KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs` (append)

- [ ] **Step 1: Write the failing test**

Append these methods inside the `AttentionBeaconTests` class:

```csharp
    [Fact]
    public void RingPhase_EvenlyStaggered()
    {
        // At time 0 the i-th of 4 rings is offset by i/4 of the period.
        Assert.Equal(0.00f, AttentionBeacon.RingPhase(0, 4, 0f, 2f), Tol);
        Assert.Equal(0.25f, AttentionBeacon.RingPhase(1, 4, 0f, 2f), Tol);
        Assert.Equal(0.50f, AttentionBeacon.RingPhase(2, 4, 0f, 2f), Tol);
        Assert.Equal(0.75f, AttentionBeacon.RingPhase(3, 4, 0f, 2f), Tol);
    }

    [Fact]
    public void RingPhase_WrapsWithinUnitInterval_AndResets()
    {
        // Period 2: phase advances time/period and wraps at the period boundary back to its start.
        float atStart = AttentionBeacon.RingPhase(0, 3, 0f, 2f);
        float atHalf = AttentionBeacon.RingPhase(0, 3, 1f, 2f);
        float atPeriod = AttentionBeacon.RingPhase(0, 3, 2f, 2f);
        Assert.Equal(0.0f, atStart, Tol);
        Assert.Equal(0.5f, atHalf, Tol);
        Assert.Equal(0.0f, atPeriod, Tol); // reset
        Assert.InRange(AttentionBeacon.RingPhase(0, 3, 5f, 2f), 0f, 1f); // always in [0,1)
    }

    [Fact]
    public void RingRadius_GrowsMonotonically_FromInnerToMax()
    {
        float r0 = AttentionBeacon.RingRadius(0f, 6f, 48f);
        float rMid = AttentionBeacon.RingRadius(0.5f, 6f, 48f);
        float r1 = AttentionBeacon.RingRadius(1f, 6f, 48f);
        Assert.Equal(6f, r0, Tol);   // inner at phase 0
        Assert.Equal(27f, rMid, Tol); // lerp midpoint
        Assert.Equal(48f, r1, Tol);  // max at phase 1
        Assert.True(rMid > r0 && r1 > rMid, "radius must grow monotonically with phase");
    }

    [Fact]
    public void RingAlpha_OneAtInner_ZeroAtMax()
    {
        Assert.Equal(1f, AttentionBeacon.RingAlpha(0f), Tol);   // bright at the inner radius
        Assert.Equal(0f, AttentionBeacon.RingAlpha(1f), Tol);   // faded out by the max radius
        Assert.Equal(0.25f, AttentionBeacon.RingAlpha(0.75f), Tol);
    }

    [Fact]
    public void RingDiameter_DefaultThickness_CentersBandOnRadius()
    {
        // bandCenterFraction 0.675: a band at radius 27 needs a quad of side 2*27/0.675 = 80.
        Assert.Equal(80f, AttentionBeacon.RingDiameter(27f, 1f, 0.675f), Tol);
    }

    [Fact]
    public void RingDiameter_ThickerMultiplier_YieldsLargerQuad()
    {
        float thin = AttentionBeacon.RingDiameter(27f, 0.5f, 0.675f);
        float native = AttentionBeacon.RingDiameter(27f, 1f, 0.675f);
        float thick = AttentionBeacon.RingDiameter(27f, 2f, 0.675f);
        Assert.True(thin < native && thick > native, "RingThickness scales the drawn quad");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AttentionBeaconTests"`
Expected: FAIL - compile error, `AttentionBeacon` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Render2D/Vfx/AttentionBeacon.cs` with the ring helpers only (Draw is added in Task 4):

```csharp
using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Draws an additive "attention" pulse at a point: expanding sonar-ping rings under a configurable number of
    /// twinkling glints. Time-driven and stateless - the caller passes the elapsed time in seconds, so the same
    /// time always renders the same frame (pass an unscaled real-time accumulator to keep it animating regardless
    /// of any game time-scale). Composited additively regardless of the batch's current <see cref="BlendMode"/>
    /// (set and restored around the draw), so it reads as a bright pulse on a dark scene.
    /// </summary>
    public static class AttentionBeacon
    {
        // The radial-glow texture's bright ring band sits at this fraction of its half-extent (the BakeRing default
        // innerRadius01 0.55 + half of thickness01 0.25 = 0.675); placing a band at radius r needs side 2r/0.675.
        internal const float BandCenterFraction = 0.675f;

        /// <summary>
        /// Phase in [0,1) of ring <paramref name="index"/> of <paramref name="ringCount"/> at
        /// <paramref name="time"/> seconds with period <paramref name="period"/> seconds. Advances
        /// <c>time/period</c> and is staggered by <c>index/ringCount</c> so the rings are evenly spaced. Pure.
        /// </summary>
        internal static float RingPhase(int index, int ringCount, float time, float period)
        {
            if (period <= 0f || ringCount <= 0) return 0f;
            float phase = time / period + (float)index / ringCount;
            phase -= MathF.Floor(phase);
            return phase;
        }

        /// <summary>Ring radius (pixels) at <paramref name="phase"/>: lerp from <paramref name="inner"/> to <paramref name="max"/>. Pure.</summary>
        internal static float RingRadius(float phase, float inner, float max) => inner + (max - inner) * phase;

        /// <summary>Ring alpha at <paramref name="phase"/>: 1 at the inner radius (phase 0), 0 at the max radius (phase 1). Pure.</summary>
        internal static float RingAlpha(float phase) => Math.Clamp(1f - phase, 0f, 1f);

        /// <summary>
        /// Side (pixels) of the centered square quad for a soft ring whose bright band should sit at
        /// <paramref name="bandRadius"/>: <c>2 * bandRadius * ringThickness / bandCenterFraction</c>.
        /// <paramref name="ringThickness"/> scales the quad (1 = the texture's native band). Pure.
        /// </summary>
        internal static float RingDiameter(float bandRadius, float ringThickness, float bandCenterFraction) =>
            2f * bandRadius * ringThickness / bandCenterFraction;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AttentionBeaconTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/Vfx/AttentionBeacon.cs KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs
git commit -m "feat(render2d-vfx): AttentionBeacon ring geometry helpers"
```

---

## Task 3: Glint geometry helpers

**Files:**
- Modify: `KhaozEngine.Render2D/Vfx/AttentionBeacon.cs`
- Test: `KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs` (append)

- [ ] **Step 1: Write the failing test**

Append these methods inside the `AttentionBeaconTests` class:

```csharp
    [Fact]
    public void GlintAngle_StableAcrossCalls_AndDistinctPerIndex()
    {
        Assert.Equal(AttentionBeacon.GlintAngle(2), AttentionBeacon.GlintAngle(2), Tol); // deterministic
        // Golden-angle spacing: consecutive indices are well separated, none coincide mod tau.
        float a0 = AttentionBeacon.GlintAngle(0);
        float a1 = AttentionBeacon.GlintAngle(1);
        float a2 = AttentionBeacon.GlintAngle(2);
        Assert.True(MathF.Abs(a1 - a0) > 0.1f);
        Assert.True(MathF.Abs(a2 - a1) > 0.1f);
    }

    [Fact]
    public void GlintRadiusFactor_StableAndWithinBand()
    {
        for (int j = 0; j < 8; j++)
        {
            float f = AttentionBeacon.GlintRadiusFactor(j);
            Assert.Equal(f, AttentionBeacon.GlintRadiusFactor(j), Tol); // deterministic
            Assert.InRange(f, 0.6f, 1.0f);
        }
    }

    [Fact]
    public void GlintAlpha_StaysInRange_AndIsNonNegative()
    {
        for (float t = 0f; t < 4f; t += 0.13f)
        {
            float a = AttentionBeacon.GlintAlpha(1, t, 6f);
            Assert.InRange(a, 0f, 1f);
        }
    }

    [Fact]
    public void GlintAlpha_DifferentIndices_TwinkleOutOfPhase()
    {
        // Distinct per-index phase: two glints are not identical at every instant.
        bool differ = false;
        for (float t = 0f; t < 2f; t += 0.1f)
        {
            if (MathF.Abs(AttentionBeacon.GlintAlpha(0, t, 6f) - AttentionBeacon.GlintAlpha(1, t, 6f)) > 1e-3f)
            {
                differ = true;
                break;
            }
        }
        Assert.True(differ, "glints should twinkle on independent phases");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AttentionBeaconTests"`
Expected: FAIL - compile error, `GlintAngle`/`GlintRadiusFactor`/`GlintAlpha` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add these members to `AttentionBeacon` (after `RingDiameter`):

```csharp
        // Golden angle / golden-ratio conjugate give stable, well-spread, RNG-free per-index offsets.
        internal const float GoldenAngle = 2.39996323f;       // radians
        const float GoldenRatioConj = 0.61803399f;

        /// <summary>Fractional part of <paramref name="v"/> in [0,1). Pure.</summary>
        static float Frac(float v) => v - MathF.Floor(v);

        /// <summary>Angle (radians) of glint <paramref name="index"/>: golden-angle spacing, stable and well spread. Pure.</summary>
        internal static float GlintAngle(int index) => index * GoldenAngle;

        /// <summary>Per-index radius factor in [0.6, 1.0] from a golden-ratio hash, so glints sit at varied radii. Pure.</summary>
        internal static float GlintRadiusFactor(int index) => 0.6f + 0.4f * Frac((index + 1) * GoldenRatioConj);

        /// <summary>
        /// Twinkle alpha in [0,1] of glint <paramref name="index"/> at <paramref name="time"/> seconds twinkling at
        /// <paramref name="twinkleRate"/> rad/s, on an index-derived phase so glints pulse out of step. Pure.
        /// </summary>
        internal static float GlintAlpha(int index, float time, float twinkleRate)
        {
            float phase = Frac(index * GoldenRatioConj) * MathF.Tau;
            return 0.5f + 0.5f * MathF.Sin(time * twinkleRate + phase);
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AttentionBeaconTests"`
Expected: PASS (12 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/Vfx/AttentionBeacon.cs KhaozEngine.Tests/Render2D/Vfx/AttentionBeaconTests.cs
git commit -m "feat(render2d-vfx): AttentionBeacon glint geometry helpers"
```

---

## Task 4: `AttentionBeacon.Draw` + `VfxRenderer.DrawAttentionBeacon`

**Files:**
- Modify: `KhaozEngine.Render2D/Vfx/AttentionBeacon.cs`
- Modify: `KhaozEngine.Render2D/Vfx/VfxRenderer.cs`
- Test: GPU smoke tests come in Task 5; this task is verified by a full build + existing suite staying green.

This task wires the verified pure helpers into the actual draw. There is no new headless unit test (drawing is a GPU concern - covered in Task 5); correctness of the math is already locked by Tasks 2-3.

- [ ] **Step 1: Add `Draw` to `AttentionBeacon`**

Add to `AttentionBeacon` (place the public `Draw` above the helpers, matching `EnergyBeam`'s layout - public API first). Reuse `PrimitiveRenderer.FullUV` and the centered-quad origin `(0.5, 0.5)`:

```csharp
        static readonly Vector2 Centered = new(0.5f, 0.5f);

        /// <summary>
        /// Draws the attention pulse centered at <paramref name="center"/> (screen-space) on <paramref name="batch"/>.
        /// <paramref name="ring"/> is the soft annulus texture for the sonar rings (a null ring skips the rings);
        /// <paramref name="glow"/> is the radial-glow texture for the glints (a null glow skips the glints).
        /// <paramref name="timeSeconds"/> drives the ring expansion and glint twinkle. Composited additively (the
        /// batch's blend mode is restored afterwards).
        /// </summary>
        public static void Draw(SpriteBatch batch, Texture2D? ring, Texture2D? glow,
            Vector2 center, in AttentionBeaconParams p, float timeSeconds)
        {
            ArgumentNullException.ThrowIfNull(batch);
            if (p.Intensity <= 0f) return;

            BlendMode prev = batch.BlendMode;
            batch.BlendMode = BlendMode.Additive;

            DrawRings(batch, ring, center, p, timeSeconds);
            DrawGlints(batch, glow, center, p, timeSeconds);

            batch.BlendMode = prev;
        }

        static void DrawRings(SpriteBatch batch, Texture2D? ring, Vector2 center, in AttentionBeaconParams p, float time)
        {
            if (ring == null || p.RingCount <= 0) return;
            for (int i = 0; i < p.RingCount; i++)
            {
                float phase = RingPhase(i, p.RingCount, time, p.RingPeriod);
                float radius = RingRadius(phase, p.InnerRadius, p.MaxRadius);
                float alpha = RingAlpha(phase) * p.Intensity;
                if (alpha <= 0f) continue;
                float d = RingDiameter(radius, p.RingThickness, BandCenterFraction);
                if (d <= 0f) continue;
                batch.Draw(ring, center, new Vector2(d, d), Centered, 0f, PrimitiveRenderer.FullUV, p.Color * alpha);
            }
        }

        static void DrawGlints(SpriteBatch batch, Texture2D? glow, Vector2 center, in AttentionBeaconParams p, float time)
        {
            if (glow == null || p.GlintCount <= 0 || p.GlintSize <= 0f) return;
            for (int j = 0; j < p.GlintCount; j++)
            {
                float alpha = GlintAlpha(j, time, p.TwinkleRate) * p.Intensity;
                if (alpha <= 0f) continue;

                float angle = GlintAngle(j);
                float dist = p.GlintRadius * GlintRadiusFactor(j);
                Vector2 pos = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;
                Color tint = p.Color * alpha;

                if (p.GlintStyle == GlintStyle.Star)
                {
                    // Two crossed soft quads stretched from the radial glow = a tiny 4-point sparkle.
                    float arm = p.GlintSize, thin = p.GlintSize * 0.28f;
                    batch.Draw(glow, pos, new Vector2(arm, thin), Centered, 0f, PrimitiveRenderer.FullUV, tint);
                    batch.Draw(glow, pos, new Vector2(thin, arm), Centered, 0f, PrimitiveRenderer.FullUV, tint);
                }
                else
                {
                    batch.Draw(glow, pos, new Vector2(p.GlintSize, p.GlintSize), Centered, 0f, PrimitiveRenderer.FullUV, tint);
                }
            }
        }
```

- [ ] **Step 2: Add `DrawAttentionBeacon` to `VfxRenderer`**

In `KhaozEngine.Render2D/Vfx/VfxRenderer.cs`, add after `DrawBeam` (before `Dispose`):

```csharp
        /// <summary>
        /// Draws an additive attention pulse (expanding sonar rings + twinkling glints) centered at
        /// <paramref name="center"/> using the owned ring (sonar rings) and glow (glints) textures. Forwards to
        /// <see cref="AttentionBeacon.Draw"/>. Pass an unscaled real-time accumulator as
        /// <paramref name="timeSeconds"/> so the pulse animates regardless of game time-scale.
        /// </summary>
        public void DrawAttentionBeacon(SpriteBatch batch, Vector2 center, float timeSeconds, in AttentionBeaconParams p)
            => AttentionBeacon.Draw(batch, RingTexture, GlowTexture, center, p, timeSeconds);
```

- [ ] **Step 3: Build and run the full headless suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS - all existing tests plus the 12 `AttentionBeaconTests` stay green; no compile errors. (GPU tests are skipped without `KE_GPU_TESTS=1`.)

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Render2D/Vfx/AttentionBeacon.cs KhaozEngine.Render2D/Vfx/VfxRenderer.cs
git commit -m "feat(render2d-vfx): AttentionBeacon.Draw + VfxRenderer.DrawAttentionBeacon"
```

---

## Task 5: GPU smoke tests

**Files:**
- Modify: `KhaozEngine.Tests/Gpu/VfxGpuTests.cs`

- [ ] **Step 1: Write the GPU tests**

Add these `[GpuFact]` methods inside the `VfxGpuTests` class (they use the existing `W`/`H`/`Lum` members):

```csharp
        [GpuFact]
        public void AttentionBeacon_LightsPixelsAroundCenter()
        {
            Vector2 c = new(W / 2f, H / 2f);
            // A frozen time where ring 0 is partway out; rings + glints should light pixels off-center.
            var p = AttentionBeaconParams.Default with { MaxRadius = 40f, GlintRadius = 24f };

            byte[] rgba = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx);
                ctx.Batch.Begin();
                vfx.DrawAttentionBeacon(ctx.Batch, c, timeSeconds: 0.6f, p);
                ctx.Batch.End();
            });

            // Somewhere on the ring band away from the exact center should be lit.
            bool anyLit = false;
            for (int x = W / 2; x < W / 2 + 40 && !anyLit; x++)
                if (Lum(rgba, x, H / 2) > 10) anyLit = true;
            Assert.True(anyLit, "beacon should light pixels out from the center");
            Assert.Equal(0, Lum(rgba, 2, 2)); // far corner stays background
        }

        [GpuFact]
        public void AttentionBeacon_ZeroCounts_DrawNothing()
        {
            Vector2 c = new(W / 2f, H / 2f);
            var p = AttentionBeaconParams.Default with { RingCount = 0, GlintCount = 0 };

            byte[] rgba = Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                using var vfx = new VfxRenderer(ctx);
                ctx.Batch.Begin();
                vfx.DrawAttentionBeacon(ctx.Batch, c, timeSeconds: 0.6f, p);
                ctx.Batch.End();
            });

            Assert.Equal(0, Lum(rgba, W / 2, H / 2)); // nothing drawn anywhere
            Assert.Equal(0, Lum(rgba, W / 2 + 20, H / 2));
        }
```

- [ ] **Step 2: Run the GPU smoke tests**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~VfxGpuTests"`
Expected: PASS - the two new beacon tests plus the existing beam/particle/glow GPU tests are green.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/Gpu/VfxGpuTests.cs
git commit -m "test(render2d-vfx): AttentionBeacon GPU smoke tests"
```

---

## Task 6: Document the API in `USING-KHAOZENGINE.md`

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md`

- [ ] **Step 1: Find the existing VFX / EnergyBeam section**

Run: `grep -n "EnergyBeam\|VfxRenderer\|Render2D.Vfx" docs/USING-KHAOZENGINE.md`
Expected: locate where the 2D VFX module is documented (the EnergyBeam/VfxRenderer area).

- [ ] **Step 2: Add an AttentionBeacon subsection**

Next to the EnergyBeam docs, add a short subsection (match the file's existing heading depth and prose style):

```markdown
#### AttentionBeacon

A reusable "look at me" pulse (pickups, quest markers, objectives): expanding sonar-ping rings plus a
configurable number of twinkling glints, drawn additively at a point. Stateless and time-driven like
`EnergyBeam` - pass an unscaled real-time accumulator so it animates regardless of game time-scale.

```csharp
// Owns the ring + glow textures; one call per frame.
using var vfx = new VfxRenderer(surface);
var beacon = AttentionBeaconParams.Default with { Color = new Color(1f, 0.85f, 0.3f, 1f) };

batch.Begin();
vfx.DrawAttentionBeacon(batch, worldOrScreenPoint, realTimeSeconds, beacon);
batch.End();
```

`AttentionBeaconParams` tunables: `RingCount`/`RingPeriod`/`InnerRadius`/`MaxRadius`/`RingThickness`
(relative band thickness, 1 = texture-native), `GlintCount`/`GlintRadius`/`GlintSize`/`TwinkleRate`/
`GlintStyle` (`Disc` or `Star`), plus `Color` and `Intensity`. `RingCount = 0` and `GlintCount = 0`
draw nothing. The low-level `AttentionBeacon.Draw(batch, ring, glow, center, in p, time)` takes the
two textures directly (null skips that sub-effect).
```

- [ ] **Step 3: Commit**

```bash
git add docs/USING-KHAOZENGINE.md
git commit -m "docs(render2d-vfx): document AttentionBeacon usage"
```

---

## Task 7: Release ritual - bump to 7.5.0, changelogs, doc-version guard, pack, tag

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

Follow the canonical order from `CLAUDE.md`. Do NOT bump per item - this is the single bump for the whole feature.

- [ ] **Step 1: Bump the shared version line**

In `Directory.Build.props`, change `<KhaozEngine5xVersion>7.4.0</KhaozEngine5xVersion>` to `7.5.0`.

- [ ] **Step 2: Add the `CHANGELOG.md` entry (newest-first, detailed)**

Add a `## 7.5.0` entry at the top describing the public API: new `AttentionBeacon` (static `Draw` + pure helpers), `AttentionBeaconParams` + `GlintStyle { Disc, Star }`, and `VfxRenderer.DrawAttentionBeacon`; additive sonar rings + twinkling glints; existing render paths and goldens unchanged.

- [ ] **Step 3: Add the `CHANGENOTES.md` entry (newest-first, one or two sentences)**

Add a `7.5.0` digest line, e.g.: "7.5.0: AttentionBeacon VFX in Render2D.Vfx - expanding additive sonar rings + deterministic twinkling glints (Disc/Star) at a point, stateless/time-driven like EnergyBeam; VfxRenderer.DrawAttentionBeacon convenience." No em-dashes (use a colon/parentheses).

- [ ] **Step 4: Update the three guard-checked version declarations to 7.5.0**

Update so `scripts/check-doc-versions.sh` passes:
- `docs/CONSUMERS.md` - the "Engine current version" line.
- `docs/ROADMAP.md` - the "Current released version" line.
- `README.md` - the `<PackageReference>` version in the example.

Run: `bash scripts/check-doc-versions.sh`
Expected: PASS (the three declarations match `<KhaozEngine5xVersion>` = 7.5.0).

- [ ] **Step 5: Full test run on the merged feature**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS - whole suite green.

- [ ] **Step 6: Pack to local-feed**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: `KhaozEngine.Render2D.7.5.0.nupkg` (and the rest of the line) written to `local-feed/`.

- [ ] **Step 7: Commit the version bump + changelogs + doc declarations together**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "render2d-vfx(7.5.0): AttentionBeacon VFX (sonar rings + twinkling glints)"
```

- [ ] **Step 8: Tag (push happens at branch-finish time, with user sign-off)**

```bash
git tag v7.5.0
```

Do not push here. Pushing `main` + the tag and merging the worktree is the branch-finish step (use the `finishing-a-development-branch` flow), per the engine "merge implies push" rule, with user confirmation.

---

## Notes for the implementer

- `docs/CONSUMERS.md` "which game pins which version" matrix is unchanged by this engine release (no consumer adopts here); only its engine-version line moves. Nullwake adoption is a separate follow-up after 7.5.0 ships.
- Keep `Draw` allocation-free: no LINQ, no closures, `in` params, integer loops only (already the case above).
- If `scripts/check-doc-versions.sh` reports an extra declaration, update that too - the script is the source of truth for which files it checks.
