# Attack Telegraph / Danger-Zone Indicator System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable, presentation-only telegraph renderer to KhaozEngine that draws animated danger-zone shapes (circle, ring, beam, cone, arc) in 2D and as terrain-conforming ground decals in 3D, fed immediate-mode from each game's own sim.

**Architecture:** A pure render-free core (`KhaozEngine.Telegraphs`) owns the style model + the progress->visual `TelegraphResolve` function + 2D drawing over Render2D. Render3D gains a generic depth-sampling `DrawGroundDecal` GPU primitive (new GLSL shader + pass). A thin `KhaozEngine.Telegraphs.Render3D` maps telegraph semantics onto that primitive. The engine stores no telegraph sim state (determinism-neutral).

**Tech Stack:** C# net10.0, System.Numerics, KhaozEngine.Gpu (Veldrid-backed) GLSL->SPIR-V shaders, xUnit headless tests + gated GPU golden tests.

**Spec:** `docs/superpowers/specs/2026-06-23-attack-telegraph-system-design.md`

**Conventions for every task:**
- Run all commands from the worktree root `/Users/antonio/KhaozEngine/.claude/worktrees/feature+telegraphs`.
- `mkdir -p local-feed` once before the first `dotnet restore`/`build` (it is gitignored but must exist).
- Build a single project: `dotnet build KhaozEngine.Telegraphs/KhaozEngine.Telegraphs.csproj`.
- Run headless tests for this feature: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Telegraph"`.
- Commit subjects: conventional `area(scope): summary`, no em-dashes. For non-release commits use `telegraphs` as the scope, e.g. `feat(telegraphs): TelegraphStyle + presets`. The single version-bump commit at the end uses the new version as scope.
- No version bump until the final release task (one bump per batch).

---

## Phase A: Core package (KhaozEngine.Telegraphs) - pure, headless

### Task 1: Scaffold the KhaozEngine.Telegraphs package

**Files:**
- Create: `KhaozEngine.Telegraphs/KhaozEngine.Telegraphs.csproj`
- Create: `KhaozEngine.Telegraphs/Placeholder.cs` (temporary, removed in Task 2)
- Modify: `KhaozEngine.slnx` (add the project)
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add a ProjectReference)
- Modify: `KhaozEngine.Game2D/KhaozEngine.Game2D.csproj` (add to umbrella)

- [ ] **Step 1: Create the csproj** (mirrors `KhaozEngine.Snapshot.csproj`)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Telegraphs</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <Description>Presentation-only attack-telegraph / danger-zone indicators for KhaozEngine. A data-driven, immediate-mode renderer for animated danger shapes (circle, ring, beam, cone, arc) driven by a 0..1 telegraph progress and a styleable TelegraphStyle (fill/outline, color ramp, fill sweep, outline pulse, impact flash, element presets). This package is the render-free style + resolve core plus the 2D path (TelegraphRenderer2D over Render2D); add KhaozEngine.Telegraphs.Render3D for the ground-plane 3D path. Holds no simulation state - safe to feed from a deterministic/lockstep game without touching its hash, exactly like the skinned-mesh animation layers.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Render2D/KhaozEngine.Render2D.csproj" />
    <ProjectReference Include="../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create a temporary placeholder so the project compiles**

`KhaozEngine.Telegraphs/Placeholder.cs`:
```csharp
namespace KhaozEngine.Telegraphs
{
    // Removed in Task 2 once real types exist. Keeps an otherwise-empty new project compiling.
    internal static class Placeholder { }
}
```

- [ ] **Step 3: Register in the solution and test/umbrella projects**

In `KhaozEngine.slnx`, add after the `KhaozEngine.Render3D` line:
```xml
  <Project Path="KhaozEngine.Telegraphs/KhaozEngine.Telegraphs.csproj" />
```

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add after the Render3D ProjectReference:
```xml
    <ProjectReference Include="../KhaozEngine.Telegraphs/KhaozEngine.Telegraphs.csproj" />
```

In `KhaozEngine.Game2D/KhaozEngine.Game2D.csproj`, add inside the 2D-runtime ItemGroup (after the Effects reference):
```xml
    <ProjectReference Include="../KhaozEngine.Telegraphs/KhaozEngine.Telegraphs.csproj" />
```

- [ ] **Step 4: Build to verify the project and wiring compile**

Run: `mkdir -p local-feed && dotnet build KhaozEngine.Telegraphs/KhaozEngine.Telegraphs.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Telegraphs KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Game2D/KhaozEngine.Game2D.csproj
git commit -m "feat(telegraphs): scaffold KhaozEngine.Telegraphs package"
```

---

### Task 2: TelegraphStyle, enums, and presets

**Files:**
- Create: `KhaozEngine.Telegraphs/TelegraphStyle.cs`
- Delete: `KhaozEngine.Telegraphs/Placeholder.cs`
- Test: `KhaozEngine.Tests/Telegraphs/TelegraphStyleTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Telegraphs/TelegraphStyleTests.cs`:
```csharp
using KhaozEngine.Primitives;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class TelegraphStyleTests
    {
        [Fact]
        public void Generic_preset_is_alpha_outline_and_fill_with_all_anims()
        {
            var s = TelegraphStyle.Generic;
            Assert.Equal(TelegraphBlend.Alpha, s.Blend);
            Assert.Equal(FillMode.OutlineAndFill, s.FillMode);
            Assert.Equal(ZoneSense.Danger, s.ZoneSense);
            Assert.True(s.Animation.HasFlag(TelegraphAnim.FillSweep));
            Assert.True(s.Animation.HasFlag(TelegraphAnim.ColorRamp));
            Assert.True(s.Animation.HasFlag(TelegraphAnim.ImpactFlash));
        }

        [Fact]
        public void Fire_preset_is_additive()
        {
            Assert.Equal(TelegraphBlend.Additive, TelegraphStyle.Fire.Blend);
        }

        [Fact]
        public void Poison_preset_fill_is_greenish()
        {
            var f = TelegraphStyle.Poison.FillColor;
            Assert.True(f.G > f.R && f.G > f.B);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TelegraphStyleTests"`
Expected: FAIL (TelegraphStyle / enums do not exist).

- [ ] **Step 3: Implement TelegraphStyle and the enums**

Delete `KhaozEngine.Telegraphs/Placeholder.cs`. Create `KhaozEngine.Telegraphs/TelegraphStyle.cs`:
```csharp
using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>How a telegraph shape is filled.</summary>
    public enum FillMode { Outline, Fill, OutlineAndFill }

    /// <summary>Compositing for a telegraph (matches the renderer blend states).</summary>
    public enum TelegraphBlend { Alpha, Additive }

    /// <summary>
    /// Whether the shape marks the DANGER area (default) or, RESERVED for a future version, the SAFE area
    /// (everything-dangerous-except-here). v1 renders <see cref="Safe"/> exactly like <see cref="Danger"/>.
    /// </summary>
    public enum ZoneSense { Danger, Safe }

    /// <summary>
    /// Progress-driven animation behaviours, composable. <see cref="OutlinePulse"/> oscillates the outline alpha;
    /// <see cref="FillSweep"/> grows the filled area as impact nears; <see cref="ColorRamp"/> lerps the fill toward
    /// a danger color; <see cref="ImpactFlash"/> spikes brightness near progress 1.
    /// </summary>
    [Flags]
    public enum TelegraphAnim
    {
        None = 0,
        OutlinePulse = 1 << 0,
        FillSweep = 1 << 1,
        ColorRamp = 1 << 2,
        ImpactFlash = 1 << 3,
    }

    /// <summary>
    /// Styling for a telegraph shape: colors, edge thickness, opacity, fill mode, animation flags, blend, and the
    /// reserved zone sense. A plain value type; use the presets (<see cref="Generic"/>, <see cref="Fire"/>,
    /// <see cref="Poison"/>) and `with`-style copies to tweak. Consumed (with a 0..1 progress) by
    /// <see cref="TelegraphResolve"/>.
    /// </summary>
    public struct TelegraphStyle
    {
        /// <summary>Base fill color (RGB). The "safe" end of the color ramp; alpha is the fill's base opacity.</summary>
        public Color FillColor;
        /// <summary>Outline color (RGBA). Alpha is the outline's base opacity.</summary>
        public Color OutlineColor;
        /// <summary>The "danger" end of the color ramp the fill lerps toward as progress -> 1.</summary>
        public Color DangerColor;
        /// <summary>Outline / ring-band / feathered-edge width, in the renderer's units (pixels for 2D, world for 3D).</summary>
        public float EdgeThickness;
        /// <summary>Master opacity multiplier applied on top of the per-color alphas (0..1).</summary>
        public float Opacity;
        public FillMode FillMode;
        public TelegraphAnim Animation;
        public TelegraphBlend Blend;
        public ZoneSense ZoneSense;

        /// <summary>Neutral red-orange danger zone: alpha-blended outline + fill, all animations on.</summary>
        public static TelegraphStyle Generic => new()
        {
            FillColor = new Color(0.95f, 0.30f, 0.15f, 0.35f),
            OutlineColor = new Color(1f, 0.55f, 0.25f, 0.9f),
            DangerColor = new Color(1f, 0.10f, 0.05f, 0.55f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.ImpactFlash,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
        };

        /// <summary>Fiery additive variant (warm ramp, glows over the scene).</summary>
        public static TelegraphStyle Fire => new()
        {
            FillColor = new Color(1f, 0.55f, 0.10f, 0.30f),
            OutlineColor = new Color(1f, 0.80f, 0.30f, 0.9f),
            DangerColor = new Color(1f, 0.20f, 0.02f, 0.6f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.ImpactFlash | TelegraphAnim.OutlinePulse,
            Blend = TelegraphBlend.Additive,
            ZoneSense = ZoneSense.Danger,
        };

        /// <summary>Toxic green variant (alpha-blended, pulsing outline).</summary>
        public static TelegraphStyle Poison => new()
        {
            FillColor = new Color(0.35f, 0.85f, 0.20f, 0.32f),
            OutlineColor = new Color(0.6f, 1f, 0.35f, 0.9f),
            DangerColor = new Color(0.30f, 1f, 0.10f, 0.55f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.FillSweep | TelegraphAnim.ColorRamp | TelegraphAnim.OutlinePulse,
            Blend = TelegraphBlend.Alpha,
            ZoneSense = ZoneSense.Danger,
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TelegraphStyleTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Telegraphs/TelegraphStyle.cs KhaozEngine.Tests/Telegraphs/TelegraphStyleTests.cs
git rm KhaozEngine.Telegraphs/Placeholder.cs
git commit -m "feat(telegraphs): TelegraphStyle, enums, and presets"
```

---

### Task 3: TelegraphResolve - the pure progress->visual function

**Files:**
- Create: `KhaozEngine.Telegraphs/ResolvedTelegraph.cs`
- Create: `KhaozEngine.Telegraphs/TelegraphResolve.cs`
- Test: `KhaozEngine.Tests/Telegraphs/TelegraphResolveTests.cs`

This is the heart of the system: a pure function mapping (progress, style) to a concrete per-frame visual. Both the 2D and 3D paths consume it, so its tests are the main behavioural net.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Telegraphs/TelegraphResolveTests.cs`:
```csharp
using KhaozEngine.Primitives;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class TelegraphResolveTests
    {
        [Fact]
        public void Progress_is_clamped_to_unit_range()
        {
            var lo = TelegraphResolve.Resolve(-5f, TelegraphStyle.Generic);
            var hi = TelegraphResolve.Resolve(5f, TelegraphStyle.Generic);
            Assert.Equal(TelegraphResolve.Resolve(0f, TelegraphStyle.Generic).FillFraction, lo.FillFraction, 5);
            Assert.Equal(TelegraphResolve.Resolve(1f, TelegraphStyle.Generic).FillFraction, hi.FillFraction, 5);
        }

        [Fact]
        public void FillSweep_grows_fill_fraction_with_progress()
        {
            var a = TelegraphResolve.Resolve(0.1f, TelegraphStyle.Generic);
            var b = TelegraphResolve.Resolve(0.9f, TelegraphStyle.Generic);
            Assert.True(b.FillFraction > a.FillFraction);
            Assert.InRange(a.FillFraction, 0f, 1f);
            Assert.InRange(b.FillFraction, 0f, 1f);
        }

        [Fact]
        public void Without_FillSweep_fill_fraction_is_full()
        {
            var style = TelegraphStyle.Generic;
            style.Animation = TelegraphAnim.None;
            Assert.Equal(1f, TelegraphResolve.Resolve(0f, style).FillFraction, 5);
        }

        [Fact]
        public void ColorRamp_lerps_fill_toward_danger_color()
        {
            var style = TelegraphStyle.Generic;
            var early = TelegraphResolve.Resolve(0f, style);
            var late = TelegraphResolve.Resolve(1f, style);
            // R channel: danger (1.0) vs base (0.95). Late should be at/above early and reach danger at progress 1.
            Assert.True(late.FillColor.R >= early.FillColor.R);
            Assert.Equal(style.DangerColor.R, late.FillColor.R, 3);
        }

        [Fact]
        public void ImpactFlash_spikes_near_one_and_is_zero_early()
        {
            var early = TelegraphResolve.Resolve(0.2f, TelegraphStyle.Generic);
            var late = TelegraphResolve.Resolve(1f, TelegraphStyle.Generic);
            Assert.Equal(0f, early.FlashAdd, 3);
            Assert.True(late.FlashAdd > 0.5f);
        }

        [Fact]
        public void OutlinePulse_oscillates_outline_alpha()
        {
            var style = TelegraphStyle.Poison; // has OutlinePulse, no ImpactFlash
            float a0 = TelegraphResolve.Resolve(0.0f, style).OutlineColor.A;
            float a1 = TelegraphResolve.Resolve(0.25f, style).OutlineColor.A;
            Assert.NotEqual(a0, a1, 3);
        }

        [Fact]
        public void Opacity_scales_all_alphas()
        {
            var full = TelegraphStyle.Generic;
            var half = TelegraphStyle.Generic; half.Opacity = 0.5f;
            var rf = TelegraphResolve.Resolve(0.5f, full);
            var rh = TelegraphResolve.Resolve(0.5f, half);
            Assert.Equal(rf.FillColor.A * 0.5f, rh.FillColor.A, 3);
        }

        [Fact]
        public void Resolve_is_pure()
        {
            var a = TelegraphResolve.Resolve(0.37f, TelegraphStyle.Fire);
            var b = TelegraphResolve.Resolve(0.37f, TelegraphStyle.Fire);
            Assert.Equal(a.FillColor, b.FillColor);
            Assert.Equal(a.OutlineColor, b.OutlineColor);
            Assert.Equal(a.FillFraction, b.FillFraction, 6);
            Assert.Equal(a.FlashAdd, b.FlashAdd, 6);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TelegraphResolveTests"`
Expected: FAIL (ResolvedTelegraph / TelegraphResolve do not exist).

- [ ] **Step 3: Implement ResolvedTelegraph and TelegraphResolve**

`KhaozEngine.Telegraphs/ResolvedTelegraph.cs`:
```csharp
using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// The concrete per-frame visual a renderer draws: final fill/outline colors (alphas already multiplied by
    /// opacity + pulse), the swept fill fraction (0..1 of the shape's extent that is filled this frame), an
    /// additive impact-flash term (0..1), and the blend/fill mode carried through from the style. Produced by
    /// <see cref="TelegraphResolve.Resolve"/>; holds no shape geometry (the renderer applies
    /// <see cref="FillFraction"/> to the shape).
    /// </summary>
    public readonly struct ResolvedTelegraph
    {
        public readonly Color FillColor;
        public readonly Color OutlineColor;
        public readonly float FillFraction;
        public readonly float FlashAdd;
        public readonly float EdgeThickness;
        public readonly FillMode FillMode;
        public readonly TelegraphBlend Blend;

        public ResolvedTelegraph(Color fillColor, Color outlineColor, float fillFraction, float flashAdd,
            float edgeThickness, FillMode fillMode, TelegraphBlend blend)
        {
            FillColor = fillColor;
            OutlineColor = outlineColor;
            FillFraction = fillFraction;
            FlashAdd = flashAdd;
            EdgeThickness = edgeThickness;
            FillMode = fillMode;
            Blend = blend;
        }
    }
}
```

`KhaozEngine.Telegraphs/TelegraphResolve.cs`:
```csharp
using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// Pure mapping from a 0..1 telegraph progress + a <see cref="TelegraphStyle"/> to a
    /// <see cref="ResolvedTelegraph"/>. No state, no allocation, no randomness - same inputs give the same output,
    /// so feeding it from a deterministic sim never perturbs the sim. The renderers apply the result; the shape
    /// geometry is theirs.
    /// </summary>
    public static class TelegraphResolve
    {
        public static ResolvedTelegraph Resolve(float progress, in TelegraphStyle style)
        {
            float p = MathUtil.Clamp01(progress);

            // Fill sweep: dangerous area grows from a small seed to full as impact nears (ease-out so it lingers
            // near full). Off => always full.
            float fillFraction = style.Animation.HasFlag(TelegraphAnim.FillSweep)
                ? MathUtil.Lerp(0.04f, 1f, Easing.EaseOut(p))
                : 1f;

            // Color ramp: lerp the fill RGB from base toward the danger color over progress. Off => base.
            Color fillRgb = style.Animation.HasFlag(TelegraphAnim.ColorRamp)
                ? Color.Lerp(style.FillColor, style.DangerColor, p)
                : style.FillColor;

            // Impact flash: 0 until late, then a sharp rise to ~1 at p=1 (quartic shoulder). Off => 0.
            float flash = style.Animation.HasFlag(TelegraphAnim.ImpactFlash)
                ? FlashCurve(p)
                : 0f;

            // Outline pulse: a 0.5..1 multiplier oscillating a few times across the window. Off => 1.
            float pulse = style.Animation.HasFlag(TelegraphAnim.OutlinePulse)
                ? 0.75f + 0.25f * MathF.Sin(p * MathF.Tau * 3f)
                : 1f;

            float op = MathUtil.Clamp01(style.Opacity);
            Color fill = fillRgb.WithAlpha(MathUtil.Clamp01(fillRgb.A * op));
            Color outline = style.OutlineColor.WithAlpha(MathUtil.Clamp01(style.OutlineColor.A * op * pulse));

            return new ResolvedTelegraph(fill, outline, fillFraction, flash, style.EdgeThickness, style.FillMode, style.Blend);
        }

        // 0 below ~0.6, rising steeply to 1 at p=1. Quartic for a snappy late spike.
        static float FlashCurve(float p)
        {
            if (p <= 0.6f) return 0f;
            float t = (p - 0.6f) / 0.4f; // 0..1 over the last 40%
            return t * t * t * t;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TelegraphResolveTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Telegraphs/ResolvedTelegraph.cs KhaozEngine.Telegraphs/TelegraphResolve.cs KhaozEngine.Tests/Telegraphs/TelegraphResolveTests.cs
git commit -m "feat(telegraphs): TelegraphResolve pure progress->visual mapping"
```

---

## Phase B: 2D path (Render2D primitives + TelegraphRenderer2D)

### Task 4: Add filled-sector and filled-arc-band primitives to Render2D

`PrimitiveRenderer` has no triangle path; it composes shapes from the 1x1 white quad (rotated quads / stacked rects), like `DrawFilledCircle` and `DrawRing`. The cone (sector) and arc-band telegraphs need filled angular regions, so add two generic primitives the same way (a fan of thin overlapping radial quads), with the pure spoke math extracted as a static for headless testing (the `RingSegments`/`ComputeProgressBarLayout` precedent).

**Files:**
- Modify: `KhaozEngine.Render2D/PrimitiveRenderer.cs`
- Test: `KhaozEngine.Tests/Render2D/PrimitiveSectorTests.cs`

- [ ] **Step 1: Write the failing test** (pure geometry only)

`KhaozEngine.Tests/Render2D/PrimitiveSectorTests.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    public class PrimitiveSectorTests
    {
        [Fact]
        public void SectorSegments_scales_with_arc_length_and_has_a_floor()
        {
            // Tiny sweep -> floored; large radius + full sweep -> more segments.
            Assert.True(PrimitiveRenderer.SectorSegments(2f, 0.05f) >= 2);
            Assert.True(PrimitiveRenderer.SectorSegments(300f, MathF.Tau) >
                        PrimitiveRenderer.SectorSegments(20f, 0.2f));
        }

        [Fact]
        public void SectorSpoke_endpoints_lie_on_the_arc_at_the_right_angle()
        {
            // A sector centered on +X (dir angle 0), half-angle 90deg, range 10, sampled at the leading edge.
            Vector2 center = new(5f, 5f);
            float dirAngle = 0f, halfAngle = MathF.PI / 2f, range = 10f;
            // t=0 is the start edge (dirAngle - halfAngle), t=1 the end edge (dirAngle + halfAngle).
            Vector2 start = PrimitiveRenderer.SectorRimPoint(center, dirAngle, halfAngle, range, 0f);
            Vector2 end = PrimitiveRenderer.SectorRimPoint(center, dirAngle, halfAngle, range, 1f);
            Assert.Equal(range, (start - center).Length(), 3);
            Assert.Equal(range, (end - center).Length(), 3);
            // start edge points down-ish (angle -90deg): y < center.y; end edge up-ish: y > center.y.
            Assert.True(start.Y < center.Y);
            Assert.True(end.Y > center.Y);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PrimitiveSectorTests"`
Expected: FAIL (SectorSegments / SectorRimPoint / sector draws do not exist).

- [ ] **Step 3: Implement the helpers** (add to `PrimitiveRenderer`, before `Dispose`)

```csharp
        /// <summary>
        /// Segment count for a sector/arc spanning <paramref name="sweep"/> radians at <paramref name="radius"/>:
        /// proportional to arc length, floored at 2 and clamped to 96 so a thin sweep stays cheap and a wide one
        /// stays smooth. Pure; extracted for headless tests.
        /// </summary>
        public static int SectorSegments(float radius, float sweep) =>
            Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweep) * MathF.Max(radius, 1f) * 0.25f), 2, 96);

        /// <summary>
        /// The rim point of a sector at normalized angle <paramref name="t"/> in [0,1] across the sweep:
        /// angle = <paramref name="dirAngle"/> - <paramref name="halfAngle"/> + t * (2 * halfAngle), at
        /// <paramref name="radius"/> from <paramref name="center"/>. Pure; extracted for headless tests.
        /// </summary>
        public static Vector2 SectorRimPoint(Vector2 center, float dirAngle, float halfAngle, float radius, float t)
        {
            float a = dirAngle - halfAngle + t * (2f * halfAngle);
            return center + new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius);
        }

        /// <summary>
        /// Draws a filled sector (pie wedge) centered at <paramref name="center"/>, facing
        /// <paramref name="dirAngle"/> radians, spanning +/- <paramref name="halfAngle"/>, out to
        /// <paramref name="radius"/>. Built as a fan of thin overlapping triangles, each drawn as a rotated quad
        /// (no triangle path in SpriteBatch). No-op when radius or sweep is non-positive.
        /// </summary>
        public void DrawFilledSector(SpriteBatch batch, Vector2 center, float dirAngle, float halfAngle, float radius, Color color)
        {
            if (radius <= 0f || halfAngle <= 0f) return;
            int segs = SectorSegments(radius, 2f * halfAngle);
            Vector2 prev = SectorRimPoint(center, dirAngle, halfAngle, radius, 0f);
            for (int i = 1; i <= segs; i++)
            {
                Vector2 cur = SectorRimPoint(center, dirAngle, halfAngle, radius, i / (float)segs);
                FillTriangleQuad(batch, center, prev, cur, color);
                prev = cur;
            }
        }

        /// <summary>
        /// Draws a filled arc band (annulus slice) between <paramref name="innerR"/> and <paramref name="outerR"/>,
        /// from <paramref name="startAngle"/> spanning <paramref name="sweep"/> radians, around
        /// <paramref name="center"/>. For a full ring pass sweep = MathF.Tau. No-op for non-positive sizes.
        /// </summary>
        public void DrawFilledArcBand(SpriteBatch batch, Vector2 center, float innerR, float outerR, float startAngle, float sweep, Color color)
        {
            if (outerR <= 0f || sweep == 0f) return;
            innerR = MathF.Max(0f, innerR);
            int segs = SectorSegments(outerR, sweep);
            float step = sweep / segs;
            void Pt(float a, float r, out Vector2 p) => p = center + new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
            Pt(startAngle, innerR, out var pi0);
            Pt(startAngle, outerR, out var po0);
            for (int i = 1; i <= segs; i++)
            {
                float a = startAngle + i * step;
                Pt(a, innerR, out var pi1);
                Pt(a, outerR, out var po1);
                // Two triangles per band segment, each as a rotated quad.
                FillTriangleQuad(batch, pi0, po0, po1, color);
                FillTriangleQuad(batch, pi0, po1, pi1, color);
                pi0 = pi1; po0 = po1;
            }
        }

        // Approximate a filled triangle (a,b,c) by a rotated quad spanning its longest edge with height to the
        // opposite vertex. Slight overdraw between adjacent fan triangles is harmless for translucent zones.
        void FillTriangleQuad(SpriteBatch batch, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            // Use edge a->c as the base; place a quad of width=|ac|, height=2*distance(b, line ac), centered so it
            // covers the triangle. For a fan this reduces to overlapping wedges that fill the sector.
            Vector2 baseEdge = c - a;
            float len = baseEdge.Length();
            if (len <= 1e-4f) return;
            float angle = MathF.Atan2(baseEdge.Y, baseEdge.X);
            // Height: perpendicular distance from b to line ac.
            Vector2 n = new(-baseEdge.Y / len, baseEdge.X / len);
            float h = MathF.Abs(Vector2.Dot(b - a, n));
            if (h <= 1e-4f) h = 1f;
            // Rotated quad origin at a, extending along the base and half the height each side of it.
            batch.Draw(_white, a - n * h, new Vector2(len, h * 2f), new Vector2(0f, 0.5f), angle, FullUV, color);
        }
```

Note: `FillTriangleQuad` is an intentional approximation (overlapping wedges) consistent with how `DrawFilledCircle` stacks rects. It keeps the 2D path shader-free. The exact look is validated visually in a 2D consumer, not pinned by a golden.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PrimitiveSectorTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Build Render2D to confirm no regressions**

Run: `dotnet build KhaozEngine.Render2D/KhaozEngine.Render2D.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render2D/PrimitiveRenderer.cs KhaozEngine.Tests/Render2D/PrimitiveSectorTests.cs
git commit -m "feat(render2d): filled-sector and filled-arc-band primitives"
```

---

### Task 5: TelegraphRenderer2D

A thin immediate-mode wrapper: `Begin(batch)` captures the active SpriteBatch + a `PrimitiveRenderer`; each shape method resolves the style and draws fill + outline. The fill applies `FillFraction` (sweep) to the shape extent. The outline pulse/flash come from the resolved colors. Blend is set on the batch per draw.

**Files:**
- Create: `KhaozEngine.Telegraphs/TelegraphRenderer2D.cs`
- Test: `KhaozEngine.Tests/Telegraphs/TelegraphRenderer2DTests.cs` (construction + argument guards; drawing is covered by the resolve tests + visual validation)

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Telegraphs/TelegraphRenderer2DTests.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class TelegraphRenderer2DTests
    {
        [Fact]
        public void Drawing_before_Begin_throws()
        {
            var tg = new TelegraphRenderer2D();
            Assert.Throws<InvalidOperationException>(() =>
                tg.Circle(Vector2.Zero, 10f, 0.5f, TelegraphStyle.Generic));
        }

        [Fact]
        public void End_without_Begin_throws()
        {
            var tg = new TelegraphRenderer2D();
            Assert.Throws<InvalidOperationException>(() => tg.End());
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TelegraphRenderer2DTests"`
Expected: FAIL (TelegraphRenderer2D does not exist).

- [ ] **Step 3: Implement TelegraphRenderer2D**

`KhaozEngine.Telegraphs/TelegraphRenderer2D.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// Immediate-mode 2D telegraph renderer. Call <see cref="Begin"/> with an already-<c>Begin</c>-ed
    /// <see cref="SpriteBatch"/>, issue shape draws (fed from the game's sim each frame), then <see cref="End"/>.
    /// Holds no per-frame state beyond the active batch; safe to feed from a deterministic sim. Owns a private
    /// 1x1-white <see cref="PrimitiveRenderer"/> created lazily from the first batch's device context is NOT
    /// possible (SpriteBatch exposes no device), so the caller supplies a PrimitiveRenderer to <see cref="Begin"/>.
    /// </summary>
    public sealed class TelegraphRenderer2D
    {
        SpriteBatch? _batch;
        PrimitiveRenderer? _prim;

        /// <summary>Begin a telegraph pass over an active <paramref name="batch"/> and a
        /// <paramref name="primitives"/> renderer (both owned by the caller).</summary>
        public void Begin(SpriteBatch batch, PrimitiveRenderer primitives)
        {
            _batch = batch ?? throw new ArgumentNullException(nameof(batch));
            _prim = primitives ?? throw new ArgumentNullException(nameof(primitives));
        }

        public void End()
        {
            if (_batch is null) throw new InvalidOperationException("TelegraphRenderer2D.End called before Begin.");
            _batch = null;
            _prim = null;
        }

        (SpriteBatch b, PrimitiveRenderer p) Active()
        {
            if (_batch is null || _prim is null)
                throw new InvalidOperationException("Call TelegraphRenderer2D.Begin before drawing.");
            return (_batch, _prim);
        }

        static BlendMode ToBlend(TelegraphBlend b) => b == TelegraphBlend.Additive ? BlendMode.Additive : BlendMode.Alpha;

        // Brighten a color toward white by the flash amount (additive impact pop).
        static Color WithFlash(Color c, float flash) =>
            flash <= 0f ? c : new Color(
                MathUtil.Clamp01(c.R + flash), MathUtil.Clamp01(c.G + flash), MathUtil.Clamp01(c.B + flash), c.A);

        public void Circle(Vector2 center, float radius, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            if (r.FillMode != FillMode.Outline)
                p.DrawFilledCircle(b, center, radius * r.FillFraction, WithFlash(r.FillColor, r.FlashAdd));
            if (r.FillMode != FillMode.Fill)
                p.DrawRing(b, center, radius, r.EdgeThickness, r.OutlineColor);
        }

        public void Ring(Vector2 center, float inner, float outer, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            if (r.FillMode != FillMode.Outline)
            {
                // Sweep grows the band outward from the inner edge.
                float bandOuter = inner + (outer - inner) * r.FillFraction;
                p.DrawFilledArcBand(b, center, inner, bandOuter, 0f, MathF.Tau, WithFlash(r.FillColor, r.FlashAdd));
            }
            if (r.FillMode != FillMode.Fill)
            {
                p.DrawRing(b, center, inner, r.EdgeThickness, r.OutlineColor);
                p.DrawRing(b, center, outer, r.EdgeThickness, r.OutlineColor);
            }
        }

        public void Beam(Vector2 origin, Vector2 direction, float length, float width, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            Vector2 dir = direction.LengthSquared() > 1e-6f ? Vector2.Normalize(direction) : Vector2.UnitX;
            if (r.FillMode != FillMode.Outline)
            {
                Vector2 end = origin + dir * (length * r.FillFraction);
                p.DrawLine(b, origin, end, WithFlash(r.FillColor, r.FlashAdd), width);
            }
            if (r.FillMode != FillMode.Fill)
            {
                // Outline = the two long edges of the rect.
                Vector2 n = new(-dir.Y, dir.X);
                Vector2 end = origin + dir * length;
                p.DrawLine(b, origin + n * (width * 0.5f), end + n * (width * 0.5f), r.OutlineColor, r.EdgeThickness);
                p.DrawLine(b, origin - n * (width * 0.5f), end - n * (width * 0.5f), r.OutlineColor, r.EdgeThickness);
            }
        }

        public void Cone(Vector2 origin, Vector2 direction, float halfAngleRad, float range, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            float dirAngle = MathF.Atan2(direction.Y, direction.X);
            if (r.FillMode != FillMode.Outline)
                p.DrawFilledSector(b, origin, dirAngle, halfAngleRad, range * r.FillFraction, WithFlash(r.FillColor, r.FlashAdd));
            if (r.FillMode != FillMode.Fill)
            {
                Vector2 a = PrimitiveRenderer.SectorRimPoint(origin, dirAngle, halfAngleRad, range, 0f);
                Vector2 c = PrimitiveRenderer.SectorRimPoint(origin, dirAngle, halfAngleRad, range, 1f);
                p.DrawLine(b, origin, a, r.OutlineColor, r.EdgeThickness);
                p.DrawLine(b, origin, c, r.OutlineColor, r.EdgeThickness);
            }
        }

        public void Arc(Vector2 center, float radius, float bandWidth, float startAngle, float sweepAngle, float progress, in TelegraphStyle style)
        {
            var (b, p) = Active();
            var r = TelegraphResolve.Resolve(progress, style);
            b.BlendMode = ToBlend(r.Blend);
            float inner = MathF.Max(0f, radius - bandWidth * 0.5f);
            float outer = radius + bandWidth * 0.5f;
            if (r.FillMode != FillMode.Outline)
                p.DrawFilledArcBand(b, center, inner, outer, startAngle, sweepAngle * r.FillFraction, WithFlash(r.FillColor, r.FlashAdd));
            if (r.FillMode != FillMode.Fill)
            {
                p.DrawRing(b, center, inner, r.EdgeThickness, r.OutlineColor);
                p.DrawRing(b, center, outer, r.EdgeThickness, r.OutlineColor);
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TelegraphRenderer2DTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Build the package**

Run: `dotnet build KhaozEngine.Telegraphs/KhaozEngine.Telegraphs.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Telegraphs/TelegraphRenderer2D.cs KhaozEngine.Tests/Telegraphs/TelegraphRenderer2DTests.cs
git commit -m "feat(telegraphs): TelegraphRenderer2D immediate-mode 2D path"
```

---

## Phase C: Render3D ground-decal primitive (the GPU work)

### Task 6: Public decal types + Scene3D queue (no GPU yet)

Define the generic ground-decal data types and the Scene3D immediate-mode queue + public `DrawGroundDecal`, cleared in `Begin`, mirroring the existing fill/line queues (`_fillVerts` + `FillVertexCount`). No rendering yet, so this is fully headless-testable.

**Files:**
- Create: `KhaozEngine.Render3D/GroundDecal.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.cs` (add the queue, `Begin` clear, `DrawGroundDecal`, internal `DecalCount`)
- Test: `KhaozEngine.Tests/Render3D/GroundDecalQueueTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Render3D/GroundDecalQueueTests.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class GroundDecalQueueTests
    {
        static GroundDecal SampleCircle() => new()
        {
            Shape = DecalShape.Circle,
            Center = new Vector3(1f, 0f, 2f),
            Rotation = 0f,
            Size = new Vector4(3f, 0, 0, 0),
            FillColor = new Color(1f, 0f, 0f, 0.5f),
            OutlineColor = new Color(1f, 1f, 0f, 1f),
            EdgeThickness = 0.1f,
            FillFraction = 1f,
            FlashAdd = 0f,
            Blend = DecalBlend.Alpha,
            YTolerance = 0.5f,
            MaxStep = 1f,
        };

        [Fact]
        public void DrawGroundDecal_enqueues_and_Begin_clears()
        {
            // Scene3D needs a device; this queue test uses the headless GPU scene factory only to construct it.
            using var scene = Render3DTestScene.CreateHeadless();
            Assert.Equal(0, scene.DecalCount);
            scene.DrawGroundDecal(SampleCircle());
            scene.DrawGroundDecal(SampleCircle());
            Assert.Equal(2, scene.DecalCount);
            scene.Begin();
            Assert.Equal(0, scene.DecalCount);
        }
    }
}
```

NOTE: if no headless `Scene3D` constructor helper exists for non-GPU tests, gate this test with `[GpuFact]` and construct via the same path the existing `FillVertexCount`/`LightCount` tests use. Check `KhaozEngine.Tests/Gpu` for how those Scene3D queue tests build a scene (e.g. a shared `Render3DTestScene` helper or an inline device). Use whichever the repo already uses; do not invent a new harness. If the existing queue tests are `[GpuFact]`, make this one `[GpuFact]` too and run it with `KE_GPU_TESTS=1`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GroundDecalQueueTests"`
Expected: FAIL (GroundDecal / DecalShape / DrawGroundDecal / DecalCount do not exist).

- [ ] **Step 3: Create the public decal types**

`KhaozEngine.Render3D/GroundDecal.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>Which analytic shape a <see cref="GroundDecal"/> paints. The SDF for each is in the decal shader.</summary>
    public enum DecalShape { Circle, Ring, Beam, Cone, Arc }

    /// <summary>Blend for a ground decal (matches the decal pipeline's two variants).</summary>
    public enum DecalBlend { Alpha, Additive }

    /// <summary>
    /// One generic shaped ground decal queued for this frame: a flat shape painted onto the ground/terrain by
    /// reconstructing the surface position from the depth buffer. Presentation only; cleared each
    /// <see cref="Scene3D.Begin"/>. The higher-level telegraph wrappers (KhaozEngine.Telegraphs.Render3D) build
    /// these from a TelegraphStyle + progress.
    /// </summary>
    /// <remarks>
    /// <see cref="Size"/> packs per-shape params: Circle (x=radius); Ring (x=innerR, y=outerR);
    /// Beam (x=halfLength, y=halfWidth, oriented by <see cref="Rotation"/> about +Y from +X);
    /// Cone (x=range, y=halfAngleRad, axis from <see cref="Rotation"/>); Arc (x=radius, y=halfBandWidth,
    /// z=startAngle, w=sweepAngle). <see cref="Center"/>.Y is the ground plane height; the decal paints surfaces
    /// whose reconstructed world Y is within [Center.Y - <see cref="YTolerance"/>, Center.Y + <see cref="MaxStep"/>].
    /// </remarks>
    public struct GroundDecal
    {
        public DecalShape Shape;
        public Vector3 Center;
        public float Rotation;
        public Vector4 Size;
        public Color FillColor;
        public Color OutlineColor;
        public float EdgeThickness;
        public float FillFraction;
        public float FlashAdd;
        public DecalBlend Blend;
        public float YTolerance;
        public float MaxStep;
    }
}
```

- [ ] **Step 4: Add the queue to Scene3D**

In `KhaozEngine.Render3D/Scene3D.cs`, near the other immediate-mode queues (e.g. by `_fillVerts`), add a field:
```csharp
        readonly System.Collections.Generic.List<GroundDecal> _decals = new();
```
In `Begin()`, add alongside `_fillVerts.Clear();`:
```csharp
            _decals.Clear();
```
Near `DebugFilledFan` / `FillVertexCount`, add the public API and the internal count:
```csharp
        /// <summary>Queue one generic shaped ground decal for this frame (painted onto the ground/terrain via the
        /// depth buffer, under the meshes, through the post chain). Presentation only; cleared in <see cref="Begin"/>.
        /// The telegraph wrappers build these from a style + progress.</summary>
        public void DrawGroundDecal(in GroundDecal decal) => _decals.Add(decal);

        /// <summary>Count of ground decals queued this frame. Internal: lets tests assert <see cref="Begin"/> clears
        /// the queue and <see cref="DrawGroundDecal"/> enqueues.</summary>
        internal int DecalCount => _decals.Count;
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GroundDecalQueueTests"` (add `KE_GPU_TESTS=1` if the test is `[GpuFact]` per the Step 1 note).
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/GroundDecal.cs KhaozEngine.Render3D/Scene3D.cs KhaozEngine.Tests/Render3D/GroundDecalQueueTests.cs
git commit -m "feat(render3d): GroundDecal types + Scene3D immediate-mode queue"
```

---

### Task 7: GroundDecalRenderer (shader + pipeline) and the ColorTex-only framebuffer

The decal pass samples `DepthColorTex` (a `ModelFB` attachment) so it cannot render into `ModelFB`. It renders into a framebuffer over `ColorTex` alone (like the existing `PingAFB = CreateFramebuffer(null, PingA)`), blending over the lit scene; the post chain then reads `ColorTex` with decals composited. One draw per decal (a fullscreen triangle + a per-decal UBO) - no per-instance vertex attributes, sidestepping the known Veldrid/Metal per-instance drop.

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/RenderResources.cs` (add `ColorOnlyFB`)
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.cs` (add `DecalFrag`)
- Create: `KhaozEngine.Render3D/Rendering/GroundDecalRenderer.cs`
- Test: `KhaozEngine.Tests/Render3D/DecalUboPackingTests.cs` (pure UBO packing only; GPU output is covered by the golden in Task 9)

- [ ] **Step 1: Add `ColorOnlyFB` to RenderResources**

In `RenderResources.cs`, add a field near `ModelFB`:
```csharp
        public IGpuFramebuffer ColorOnlyFB = null!;
```
In `Create`, after `ModelFB = ...`:
```csharp
            // Single-target view over the lit color attachment (no depth), so a pass can blend into ColorTex while
            // sampling DepthColorTex (a different texture) - used by the ground-decal pass before the post chain.
            ColorOnlyFB = _gd.Factory.CreateFramebuffer(null, ColorTex);
```
In `DisposeTargets`, add `ColorOnlyFB?.Dispose();` alongside `ModelFB?.Dispose();`.

- [ ] **Step 2: Write the failing test** (pure packing)

`KhaozEngine.Tests/Render3D/DecalUboPackingTests.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class DecalUboPackingTests
    {
        [Fact]
        public void Pack_carries_shape_index_size_and_colors()
        {
            var d = new GroundDecal
            {
                Shape = DecalShape.Cone,
                Center = new Vector3(2f, 0.5f, -3f),
                Rotation = 1.25f,
                Size = new Vector4(7f, 0.4f, 0f, 0f),
                FillColor = new Color(0.2f, 0.3f, 0.4f, 0.5f),
                OutlineColor = new Color(1f, 0.9f, 0.1f, 0.8f),
                EdgeThickness = 0.15f,
                FillFraction = 0.6f,
                FlashAdd = 0.25f,
                Blend = DecalBlend.Additive,
                YTolerance = 0.5f,
                MaxStep = 1.5f,
            };
            Matrix4x4.Invert(Matrix4x4.Identity, out var inv);
            var u = GroundDecalRenderer.PackUbo(d, inv);

            Assert.Equal((float)(int)DecalShape.Cone, u.Params.W, 3); // shape index in Params.w
            Assert.Equal(d.Size, u.Size);
            Assert.Equal(d.Center.X, u.Center.X, 3);
            Assert.Equal(d.Rotation, u.Center.W, 3);                  // rotation packed in Center.w
            Assert.Equal(d.FillColor.R, u.Fill.X, 3);
            Assert.Equal(d.OutlineColor.A, u.Outline.W, 3);
            Assert.Equal(d.EdgeThickness, u.Params.X, 3);
            Assert.Equal(d.FillFraction, u.Params.Y, 3);
            Assert.Equal(d.FlashAdd, u.Params.Z, 3);
            Assert.Equal(d.Center.Y, u.Gate.X, 3);                    // groundY
            Assert.Equal(d.YTolerance, u.Gate.Y, 3);
            Assert.Equal(d.MaxStep, u.Gate.Z, 3);
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DecalUboPackingTests"`
Expected: FAIL (GroundDecalRenderer / PackUbo / DecalUbo do not exist).

- [ ] **Step 4: Add the decal fragment shader to ShaderSources**

In `KhaozEngine.Render3D/Internal/ShaderSources.cs`, add (the layout mirrors `PaletteFrag`: one texture + sampler + UBO, the safe 3-binding shape):
```csharp
        // ---- Ground decal: paint an analytic danger-zone shape onto the surface under each pixel. Reconstructs the
        //      surface world position from the sampled linear depth (DepthTex) via InvViewProj, evaluates the shape
        //      SDF in shape-local space on the XZ plane, gates by a Y-band around the ground height (so it conforms
        //      to terrain but does not climb walls), and blends fill+outline with an fwidth AA edge. One draw per
        //      decal (per-decal UBO). Renders into ColorTex (ColorOnlyFB) before the post chain, with alpha or
        //      additive blend. ----
        public const string DecalFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D DepthTex;
layout(set=0, binding=1) uniform sampler Samp;
layout(set=0, binding=2) uniform Decal {
    mat4 InvViewProj;
    vec4 Center;    // xyz world center, w = rotation (radians about +Y)
    vec4 Size;      // per-shape params (see GroundDecal.Size)
    vec4 Fill;      // rgb, a = fill alpha (already opacity-scaled)
    vec4 Outline;   // rgb, a = outline alpha
    vec4 Params;    // x=edgeThickness, y=fillFraction, z=flashAdd, w=shapeIndex
    vec4 Gate;      // x=groundY, y=yTolerance, z=maxStep, w=unused
};
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;

// 2D SDFs in shape-local space (origin at decal center, +x along the decal's facing for oriented shapes).
float sdCircle(vec2 p, float r) { return length(p) - r; }
float sdRing(vec2 p, float ri, float ro) { float d = length(p); return max(ri - d, d - ro); }
float sdBox(vec2 p, vec2 b) { vec2 d = abs(p) - b; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0); }

void main() {
    float depth = texture(sampler2D(DepthTex, Samp), vUv).r;
    // Reconstruct world position from NDC (xy from screen UV, z from sampled depth).
    vec4 ndc = vec4(vUv * 2.0 - 1.0, depth, 1.0);
    vec4 wp = InvViewProj * ndc;
    vec3 world = wp.xyz / wp.w;

    // Y-band gate: only paint surfaces near the ground plane (conform to terrain, not walls).
    float gateLo = Gate.x - Gate.y;
    float gateHi = Gate.x + Gate.z;
    if (world.y < gateLo || world.y > gateHi) discard;

    // Into shape-local XZ (translate by center, rotate by -rotation so +x is the facing axis).
    vec2 q = world.xz - Center.xz;
    float c = cos(-Center.w), s = sin(-Center.w);
    vec2 local = vec2(q.x * c - q.y * s, q.x * s + q.y * c);

    int shape = int(Params.w + 0.5);
    float edge = max(Params.x, 1e-4);
    float fillFrac = clamp(Params.y, 0.0, 1.0);
    float sd;        // signed distance to the shape boundary (negative inside)
    float swept;     // signed distance to the swept (animated) fill boundary

    if (shape == 0) {              // Circle: Size.x = radius
        sd = sdCircle(local, Size.x);
        swept = sdCircle(local, Size.x * fillFrac);
    } else if (shape == 1) {       // Ring: Size.x=innerR, Size.y=outerR
        sd = sdRing(local, Size.x, Size.y);
        swept = sdRing(local, Size.x, Size.x + (Size.y - Size.x) * fillFrac);
    } else if (shape == 2) {       // Beam: Size.x=halfLength, Size.y=halfWidth (origin at one end -> shift by halfLength)
        vec2 b = vec2(Size.x, Size.y);
        vec2 p = local - vec2(Size.x, 0.0);
        sd = sdBox(p, b);
        swept = sdBox(p, vec2(Size.x * fillFrac, Size.y));
    } else if (shape == 3) {       // Cone: Size.x=range, Size.y=halfAngle. Sector via radius + angle test.
        float ang = atan(local.y, local.x);
        float inAng = abs(ang) - Size.y;             // <=0 inside the angular wedge
        float inRad = length(local) - Size.x;        // <=0 inside the range
        sd = max(inRad, inAng);
        swept = max(length(local) - Size.x * fillFrac, inAng);
    } else {                       // Arc: Size.x=radius, Size.y=halfBandWidth, Size.z=startAngle, Size.w=sweep
        float ang = atan(local.y, local.x) - Size.z;
        ang = mod(ang + 6.2831853, 6.2831853);       // 0..2pi from start
        float band = abs(length(local) - Size.x) - Size.y;
        float half = Size.w * 0.5;
        float inAng = abs(ang - half) - half;        // <=0 within [0, sweep]
        sd = max(band, inAng);
        float sweptHalf = (Size.w * fillFrac) * 0.5;
        swept = max(band, abs(ang - sweptHalf) - sweptHalf);
    }

    // Fill: inside the swept boundary, AA across one edge width.
    float fillA = (1.0 - smoothstep(0.0, edge, swept)) * Fill.a;
    // Outline: a band straddling the FULL shape boundary.
    float outlineA = (1.0 - smoothstep(edge, edge * 2.0, abs(sd))) * Outline.a;

    vec3 rgb = Fill.rgb;
    float a = fillA;
    // Composite the outline over the fill.
    rgb = mix(rgb, Outline.rgb, outlineA <= 0.0 ? 0.0 : outlineA / max(outlineA + fillA, 1e-4));
    a = max(a, outlineA);
    // Impact flash: brighten toward white.
    rgb = clamp(rgb + Params.z, 0.0, 1.0);

    if (a <= 0.001) discard;
    oColor = vec4(rgb, a);
}";
```

NOTE (on-device): the NDC z convention (0..1 vs -1..1) and any Vulkan Y flip in the reconstruction must be confirmed when baking the golden in Task 9. If the shape lands mirrored or offset, adjust `vUv.y` sign and the `ndc.z` mapping; the orthographic camera makes this a single consistent correction. Use `InvViewProj = inverse(GpuClip.Correct(Camera.ViewProjection, caps))` (Step 5) so it matches the matrix the depth was written with.

- [ ] **Step 5: Create GroundDecalRenderer**

`KhaozEngine.Render3D/Rendering/GroundDecalRenderer.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the queued <see cref="GroundDecal"/>s as a fullscreen pass per decal into the lit color attachment
    /// (ColorOnlyFB), sampling the linear depth to reconstruct each pixel's surface world position and painting the
    /// decal's analytic shape onto the ground/terrain. Runs after the model+beam passes and before the post chain,
    /// so decals are occluded by geometry (Y-band gate) and flow through quantize/blit. One draw per decal with a
    /// per-decal UBO (no per-instance vertex attributes). Two pipelines: alpha and additive.
    /// </summary>
    internal sealed class GroundDecalRenderer : IDisposable
    {
        /// <summary>176-byte UBO matching the Decal block in <see cref="ShaderSources.DecalFrag"/>.</summary>
        public struct DecalUbo
        {
            public Matrix4x4 InvViewProj; // 64
            public Vector4 Center;        // xyz center, w=rotation
            public Vector4 Size;
            public Vector4 Fill;
            public Vector4 Outline;
            public Vector4 Params;        // x=edge, y=fillFraction, z=flashAdd, w=shapeIndex
            public Vector4 Gate;          // x=groundY, y=yTol, z=maxStep, w=0
        }

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuBuffer _ubo;
        readonly IGpuPipeline _alphaPipe, _additivePipe;
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundW, _boundH;

        public GroundDecalRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.DecalFrag);
            _ubo = f.CreateBuffer(new GpuBufferDescription(176, GpuBufferUsage.UniformBuffer));
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Decal", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            _alphaPipe = Pipe(f, colorOutput, GpuBlendAttachment.AlphaBlend);
            _additivePipe = Pipe(f, colorOutput, GpuBlendAttachment.AdditiveBlend);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs, GpuBlendAttachment blend) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { blend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });

        void BindTargets(RenderResources res)
        {
            if (ReferenceEquals(_bound, res) && res.Width == _boundW && res.Height == _boundH) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, res.DepthColorTex, _gd.PointSampler, _ubo));
            _bound = res; _boundW = res.Width; _boundH = res.Height;
        }

        /// <summary>Pure: pack a decal + the (already clip-corrected, inverted) view-projection into the UBO.</summary>
        public static DecalUbo PackUbo(in GroundDecal d, Matrix4x4 invViewProj)
        {
            Vector4 fill = d.FillColor; Vector4 outline = d.OutlineColor;
            return new DecalUbo
            {
                InvViewProj = invViewProj,
                Center = new Vector4(d.Center, d.Rotation),
                Size = d.Size,
                Fill = fill,
                Outline = outline,
                Params = new Vector4(d.EdgeThickness, d.FillFraction, d.FlashAdd, (int)d.Shape),
                Gate = new Vector4(d.Center.Y, d.YTolerance, d.MaxStep, 0f),
            };
        }

        /// <summary>Draw all queued decals into ColorOnlyFB. Caller guarantees the model pass is complete (depth
        /// written) and the framebuffer is free to rebind. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 viewProj, ReadOnlySpan<GroundDecal> decals)
        {
            if (decals.Length == 0) return;
            BindTargets(res);
            Matrix4x4 clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);
            Matrix4x4.Invert(clipVp, out var inv);
            for (int i = 0; i < decals.Length; i++)
            {
                var u = PackUbo(decals[i], inv);
                cl.UpdateBuffer(_ubo, 0, in u);
                cl.SetFramebuffer(res.ColorOnlyFB);
                cl.SetPipeline(decals[i].Blend == DecalBlend.Additive ? _additivePipe : _alphaPipe);
                cl.SetGraphicsResourceSet(0, _set!);
                cl.Draw(3);
            }
        }

        public void Dispose()
        {
            _set?.Dispose();
            _alphaPipe.Dispose(); _additivePipe.Dispose();
            _layout.Dispose(); _shaders.Dispose(); _ubo.Dispose();
        }
    }
}
```

NOTE: confirm the exact names `GpuBlendAttachment.AdditiveBlend` and `GpuBlendAttachment.AlphaBlend` against `KhaozEngine.Gpu` (the overlay/beam renderers use them; grep `GpuBlendAttachment\.` in `KhaozEngine.Render3D/Rendering` and match). Also confirm `GpuClip.Correct(viewProj, _gd.Capabilities)` signature against `OverlayRenderer.Draw` (it uses exactly this call).

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DecalUboPackingTests"`
Expected: PASS (1 test).

- [ ] **Step 7: Build Render3D**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Render3D/Internal/RenderResources.cs KhaozEngine.Render3D/Internal/ShaderSources.cs KhaozEngine.Render3D/Rendering/GroundDecalRenderer.cs KhaozEngine.Tests/Render3D/DecalUboPackingTests.cs
git commit -m "feat(render3d): ground-decal shader + GroundDecalRenderer pipeline"
```

---

### Task 8: Wire the decal pass into Scene3D.RenderInternal

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.cs` (construct the renderer; insert the pass; dispose)

- [ ] **Step 1: Construct the renderer**

In `Scene3D.cs`, add a field near `_fills`:
```csharp
        readonly Rendering.GroundDecalRenderer _decalRenderer;
```
In the constructor (after `_beams = new BeamRenderer(...)`), add (the decal writes into ColorTex, so use the model FB's color output description; ColorTex shares the model FB's output format):
```csharp
            // Ground decals render into the lit color attachment (ColorOnlyFB) before the post chain, so they use
            // the model framebuffer's output description (same color format).
            _decalRenderer = new Rendering.GroundDecalRenderer(gd, _res.ColorOnlyFB.Outputs);
```

- [ ] **Step 2: Insert the pass between beams and post**

In `RenderInternal`, between `DrawBeams(cl);` (line ~779) and `_post.Run(cl, _res, target, Post);` (line ~781), add:
```csharp
            // Ground decals: after the model pass wrote depth (meshes + textured billboards + beams), paint the
            // queued decals onto the reconstructed surface into ColorTex, BEFORE post - so they conform to the
            // ground, are occluded by geometry (Y-band), and flow through the pixel post like the meshes.
            if (_decals.Count > 0)
                _decalRenderer.Draw(cl, _res, Camera.ViewProjection, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_decals));
```

- [ ] **Step 3: Dispose**

In `Scene3D`'s `Dispose` (find where `_fills.Dispose()` / `_beams.Dispose()` are called), add:
```csharp
            _decalRenderer.Dispose();
```

- [ ] **Step 4: Build Render3D**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Run the existing GPU goldens to confirm no regression** (decals queue is empty in those scenes, so they must be byte-stable)

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GoldenSnapshotTests"`
Expected: PASS (all existing goldens unchanged; the new pass is a no-op when no decals are queued).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Scene3D.cs
git commit -m "feat(render3d): insert ground-decal pass before post in Scene3D"
```

---

### Task 9: GPU golden for the ground decal + bake

**Files:**
- Modify: `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs` (add a `telegraph_ground` golden test)
- Create (baked output): `KhaozEngine.Tests/Gpu/goldens/telegraph_ground.metal.txt` (+ `.direct3d11.txt`, `.vulkan.txt` via CI)

- [ ] **Step 1: Add the golden test** (a fixed asymmetric scene with one decal of each shape)

Append to `GoldenSnapshotTests` (after the `scene3d_fill` test):
```csharp
        [GpuFact]
        public void Golden3D_GroundDecals()
        {
            MeshHandle floor = default, box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(8f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    scene.Camera.Frame(new Vector3(0.4f, 0.4f, -0.2f), new Vector3(6f, 4f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    // A box the decal must be occluded by where it overlaps (Y-band rejects the box's top face).
                    scene.Draw(box, Matrix4x4.CreateTranslation(1.3f, 0.45f, -1.1f),
                        new Color(0.15f, 0.75f, 0.2f, 1f));

                    // Red filled circle decal, partway through its sweep.
                    scene.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Circle, Center = new Vector3(-1.2f, 0.0f, 0.6f),
                        Size = new Vector4(1.4f, 0, 0, 0),
                        FillColor = new Color(0.95f, 0.15f, 0.1f, 0.55f),
                        OutlineColor = new Color(1f, 0.8f, 0.2f, 0.9f),
                        EdgeThickness = 0.08f, FillFraction = 0.7f, FlashAdd = 0f,
                        Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
                    });
                    // Cyan ring decal off to the other side.
                    scene.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Ring, Center = new Vector3(1.6f, 0.0f, 1.6f),
                        Size = new Vector4(0.7f, 1.3f, 0, 0),
                        FillColor = new Color(0.2f, 0.8f, 0.9f, 0.55f),
                        OutlineColor = new Color(0.7f, 1f, 1f, 0.9f),
                        EdgeThickness = 0.08f, FillFraction = 1f, FlashAdd = 0f,
                        Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
                    });
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("telegraph_ground", rgba, W, H);
        }
```

- [ ] **Step 2: Bake the Metal golden locally and eyeball it**

Run: `KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Golden3D_GroundDecals"`
Expected: writes `KhaozEngine.Tests/Gpu/goldens/telegraph_ground.metal.txt`.

To visually verify the decal actually rendered correctly (not a blank/garbled frame), dump the captured RGBA to a PNG via a throwaway snapshot (per the verify-visual-golden-via-png-dump memory): temporarily add `System.IO.File.WriteAllBytes` of a PNG (encode with `KhaozEngine.Imaging.PngWriter`) in the test, inspect, then remove. If the shapes are mirrored/offset, fix the NDC reconstruction in `DecalFrag` (Step 4 NOTE of Task 7) and re-bake.

- [ ] **Step 3: Verify the baked golden passes its own compare**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Golden3D_GroundDecals"`
Expected: PASS.

- [ ] **Step 4: Commit the test + Metal golden**

```bash
git add KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs KhaozEngine.Tests/Gpu/goldens/telegraph_ground.metal.txt
git commit -m "test(render3d): GPU golden for ground-decal shapes (Metal baked)"
```

- [ ] **Step 5: Bake D3D11 + Vulkan via CI** (REQUIRED before merge, or `cross-platform-gpu.yml` goes red)

A golden baked only on Metal makes main red on the cross-platform GPU job (see the `new-gpu-golden-needs-ci-bake` lesson). Trigger the bake on the other two backends:
```bash
gh workflow run cross-platform-gpu.yml -f bake=true
```
Wait for it, download the artifacts, and add the two files:
```bash
gh run watch
# download the run's artifacts, then:
git add KhaozEngine.Tests/Gpu/goldens/telegraph_ground.direct3d11.txt KhaozEngine.Tests/Gpu/goldens/telegraph_ground.vulkan.txt
git commit -m "test(render3d): bake ground-decal golden on D3D11 + Vulkan"
```
The `CrossBackendGoldenTests` auto-discovers the new `telegraph_ground.*` grids (it globs the goldens dir), so once all three exist they are cross-checked automatically.

---

## Phase D: 3D telegraph wrapper package (KhaozEngine.Telegraphs.Render3D)

### Task 10: Scaffold KhaozEngine.Telegraphs.Render3D

**Files:**
- Create: `KhaozEngine.Telegraphs.Render3D/KhaozEngine.Telegraphs.Render3D.csproj`
- Create: `KhaozEngine.Telegraphs.Render3D/Placeholder.cs` (temporary)
- Modify: `KhaozEngine.slnx`, `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, `KhaozEngine.Game3D/KhaozEngine.Game3D.csproj`

- [ ] **Step 1: Create the csproj** (mirrors `KhaozEngine.Snapshot.Render3D.csproj`)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Telegraphs.Render3D</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <Description>The 3D arm of the KhaozEngine telegraph system: Scene3D ground-plane extensions (GroundCircle/Ring/Beam/Cone/Arc) that map a TelegraphStyle + 0..1 progress onto Render3D's generic depth-sampling DrawGroundDecal primitive, so danger zones lie flat on the ground/terrain under the meshes and animate over the telegraph window. Kept separate from KhaozEngine.Telegraphs so a 2D-only game never drags in Render3D. Presentation only, holds no sim state. MonoGame-free.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Telegraphs/KhaozEngine.Telegraphs.csproj" />
    <ProjectReference Include="../KhaozEngine.Render3D/KhaozEngine.Render3D.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Placeholder**

`KhaozEngine.Telegraphs.Render3D/Placeholder.cs`:
```csharp
namespace KhaozEngine.Telegraphs.Render3DInternal
{
    internal static class Placeholder { }
}
```

- [ ] **Step 3: Register**

In `KhaozEngine.slnx`, after the `KhaozEngine.Telegraphs` line:
```xml
  <Project Path="KhaozEngine.Telegraphs.Render3D/KhaozEngine.Telegraphs.Render3D.csproj" />
```
In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, after the Telegraphs ProjectReference:
```xml
    <ProjectReference Include="../KhaozEngine.Telegraphs.Render3D/KhaozEngine.Telegraphs.Render3D.csproj" />
```
In `KhaozEngine.Game3D/KhaozEngine.Game3D.csproj`, after the `KhaozEngine.Game.Render3D` reference:
```xml
    <ProjectReference Include="../KhaozEngine.Telegraphs.Render3D/KhaozEngine.Telegraphs.Render3D.csproj" />
```

- [ ] **Step 4: Build**

Run: `dotnet build KhaozEngine.Telegraphs.Render3D/KhaozEngine.Telegraphs.Render3D.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Telegraphs.Render3D KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Game3D/KhaozEngine.Game3D.csproj
git commit -m "feat(telegraphs): scaffold KhaozEngine.Telegraphs.Render3D package"
```

---

### Task 11: Ground* Scene3D extensions (telegraph -> GroundDecal mapping)

**Files:**
- Create: `KhaozEngine.Telegraphs.Render3D/GroundTelegraphs.cs`
- Delete: `KhaozEngine.Telegraphs.Render3D/Placeholder.cs`
- Test: `KhaozEngine.Tests/Telegraphs/GroundTelegraphMappingTests.cs`

Keep the mapping (style + progress + shape args -> `GroundDecal`) in pure static methods so it is headless-testable; the extension methods are thin wrappers that call `scene.DrawGroundDecal`.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Telegraphs/GroundTelegraphMappingTests.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class GroundTelegraphMappingTests
    {
        [Fact]
        public void Circle_maps_radius_progress_and_style()
        {
            var d = GroundTelegraphs.BuildCircle(new Vector3(2f, 0.5f, -3f), 4f, 0.5f, TelegraphStyle.Generic);
            Assert.Equal(DecalShape.Circle, d.Shape);
            Assert.Equal(new Vector3(2f, 0.5f, -3f), d.Center);
            Assert.Equal(4f, d.Size.X, 3);                 // radius
            var r = TelegraphResolve.Resolve(0.5f, TelegraphStyle.Generic);
            Assert.Equal(r.FillFraction, d.FillFraction, 4);
            Assert.Equal(r.Blend == TelegraphBlend.Additive ? DecalBlend.Additive : DecalBlend.Alpha, d.Blend);
            Assert.Equal((Vector4)r.FillColor, (Vector4)d.FillColor);
        }

        [Fact]
        public void Cone_packs_range_halfangle_and_rotation_from_direction()
        {
            // dir = +Z (xz) -> rotation atan2(z=1, x=0) = pi/2.
            var d = GroundTelegraphs.BuildCone(Vector3.Zero, new Vector2(0f, 1f), 0.6f, 5f, 1f, TelegraphStyle.Fire);
            Assert.Equal(DecalShape.Cone, d.Shape);
            Assert.Equal(5f, d.Size.X, 3);                 // range
            Assert.Equal(0.6f, d.Size.Y, 3);               // halfAngle
            Assert.Equal(MathF.PI / 2f, d.Rotation, 3);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GroundTelegraphMappingTests"`
Expected: FAIL (GroundTelegraphs does not exist).

- [ ] **Step 3: Implement the mapping + extensions**

Delete `Placeholder.cs`. Create `KhaozEngine.Telegraphs.Render3D/GroundTelegraphs.cs`:
```csharp
using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// Ground-plane telegraph extensions on <see cref="Scene3D"/>. Each method resolves the style at the given
    /// 0..1 progress and queues a <see cref="GroundDecal"/> via the engine's generic depth-sampling decal pass.
    /// Immediate-mode; presentation only. The Build* statics are the pure mapping (headless-testable); the
    /// extension methods are thin wrappers.
    /// </summary>
    public static class GroundTelegraphs
    {
        // Default terrain gate: paint a little below the ground plane and up one small step; tweak per call if a
        // game has tall terrain features inside a zone.
        const float DefaultYTolerance = 0.3f;
        const float DefaultMaxStep = 0.5f;

        static DecalBlend Blend(TelegraphBlend b) => b == TelegraphBlend.Additive ? DecalBlend.Additive : DecalBlend.Alpha;

        static GroundDecal Base(DecalShape shape, Vector3 center, float rotation, Vector4 size, in ResolvedTelegraph r) => new()
        {
            Shape = shape,
            Center = center,
            Rotation = rotation,
            Size = size,
            FillColor = r.FillColor,
            OutlineColor = r.OutlineColor,
            EdgeThickness = r.EdgeThickness,
            FillFraction = r.FillFraction,
            FlashAdd = r.FlashAdd,
            Blend = Blend(r.Blend),
            YTolerance = DefaultYTolerance,
            MaxStep = DefaultMaxStep,
        };

        static float RotFromXZ(Vector2 dirXZ) =>
            dirXZ.LengthSquared() > 1e-6f ? MathF.Atan2(dirXZ.Y, dirXZ.X) : 0f;

        public static GroundDecal BuildCircle(Vector3 center, float radius, float progress, in TelegraphStyle style) =>
            Base(DecalShape.Circle, center, 0f, new Vector4(radius, 0, 0, 0), TelegraphResolve.Resolve(progress, style));

        public static GroundDecal BuildRing(Vector3 center, float inner, float outer, float progress, in TelegraphStyle style) =>
            Base(DecalShape.Ring, center, 0f, new Vector4(inner, outer, 0, 0), TelegraphResolve.Resolve(progress, style));

        public static GroundDecal BuildBeam(Vector3 origin, Vector2 dirXZ, float length, float width, float progress, in TelegraphStyle style)
        {
            // Decal center is the beam midpoint (the box SDF in the shader is origin-at-one-end, so the renderer
            // anchors at Center; place Center at the origin and let the SDF extend along +x by halfLength*2). To
            // keep the shader's "origin at one end" assumption, Center = origin and Size.x = halfLength = length/2,
            // Size.y = halfWidth.
            var r = TelegraphResolve.Resolve(progress, style);
            return Base(DecalShape.Beam, origin, RotFromXZ(dirXZ), new Vector4(length * 0.5f, width * 0.5f, 0, 0), r);
        }

        public static GroundDecal BuildCone(Vector3 origin, Vector2 dirXZ, float halfAngleRad, float range, float progress, in TelegraphStyle style) =>
            Base(DecalShape.Cone, origin, RotFromXZ(dirXZ), new Vector4(range, halfAngleRad, 0, 0), TelegraphResolve.Resolve(progress, style));

        public static GroundDecal BuildArc(Vector3 center, float radius, float bandWidth, float startAngle, float sweepAngle, float progress, in TelegraphStyle style) =>
            Base(DecalShape.Arc, center, 0f, new Vector4(radius, bandWidth * 0.5f, startAngle, sweepAngle), TelegraphResolve.Resolve(progress, style));

        // ---- Thin Scene3D extension wrappers ----
        public static void GroundCircle(this Scene3D scene, Vector3 center, float radius, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildCircle(center, radius, progress, style));

        public static void GroundRing(this Scene3D scene, Vector3 center, float inner, float outer, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildRing(center, inner, outer, progress, style));

        public static void GroundBeam(this Scene3D scene, Vector3 origin, Vector2 dirXZ, float length, float width, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildBeam(origin, dirXZ, length, width, progress, style));

        public static void GroundCone(this Scene3D scene, Vector3 origin, Vector2 dirXZ, float halfAngleRad, float range, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildCone(origin, dirXZ, halfAngleRad, range, progress, style));

        public static void GroundArc(this Scene3D scene, Vector3 center, float radius, float bandWidth, float startAngle, float sweepAngle, float progress, in TelegraphStyle style) =>
            scene.DrawGroundDecal(BuildArc(center, radius, bandWidth, startAngle, sweepAngle, progress, style));
    }
}
```

NOTE: the `Beam` decal's SDF in `DecalFrag` shifts the box by `+halfLength` along local +x (origin-at-one-end). `BuildBeam` passes `Center = origin` and `Size.x = halfLength`, so the painted beam runs from `origin` outward along `dirXZ` for `length`. Confirm this matches when baking; if the beam is centered instead of end-anchored, drop the `local - vec2(Size.x, 0.0)` shift in the shader.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GroundTelegraphMappingTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Build the package**

Run: `dotnet build KhaozEngine.Telegraphs.Render3D/KhaozEngine.Telegraphs.Render3D.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Telegraphs.Render3D/GroundTelegraphs.cs KhaozEngine.Tests/Telegraphs/GroundTelegraphMappingTests.cs
git rm KhaozEngine.Telegraphs.Render3D/Placeholder.cs
git commit -m "feat(telegraphs): Scene3D Ground* telegraph extensions over DrawGroundDecal"
```

---

## Phase E: Determinism, docs, release

### Task 12: Determinism-neutrality test

**Files:**
- Test: `KhaozEngine.Tests/Telegraphs/TelegraphDeterminismTests.cs`

- [ ] **Step 1: Write the test**

`KhaozEngine.Tests/Telegraphs/TelegraphDeterminismTests.cs`:
```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class TelegraphDeterminismTests
    {
        [Fact]
        public void Resolve_has_no_hidden_state_across_calls()
        {
            // Interleave different inputs; each output must depend ONLY on its own args (no accumulation).
            for (int i = 0; i < 100; i++)
            {
                float p = (i % 10) / 9f;
                var a = TelegraphResolve.Resolve(p, TelegraphStyle.Generic);
                _ = TelegraphResolve.Resolve(0.123f, TelegraphStyle.Fire); // perturb
                var b = TelegraphResolve.Resolve(p, TelegraphStyle.Generic);
                Assert.Equal(a.FillFraction, b.FillFraction, 6);
                Assert.Equal(a.FlashAdd, b.FlashAdd, 6);
                Assert.Equal((Vector4)a.FillColor, (Vector4)b.FillColor);
            }
        }

        [Fact]
        public void Build_mapping_is_pure()
        {
            var a = GroundTelegraphs.BuildArc(new Vector3(1, 0, 1), 3f, 0.5f, 0.2f, 1.1f, 0.4f, TelegraphStyle.Poison);
            var b = GroundTelegraphs.BuildArc(new Vector3(1, 0, 1), 3f, 0.5f, 0.2f, 1.1f, 0.4f, TelegraphStyle.Poison);
            Assert.Equal(a.Size, b.Size);
            Assert.Equal(a.FillFraction, b.FillFraction, 6);
            Assert.Equal((Vector4)a.FillColor, (Vector4)b.FillColor);
        }
    }
}
```

- [ ] **Step 2: Run**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TelegraphDeterminismTests"`
Expected: PASS (2 tests).

- [ ] **Step 3: Run the full telegraph + render2d suites headless** (no GPU) to confirm the whole feature is green

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Telegraph|FullyQualifiedName~PrimitiveSector"`
Expected: PASS (all telegraph + sector tests).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Tests/Telegraphs/TelegraphDeterminismTests.cs
git commit -m "test(telegraphs): determinism-neutrality (resolve + mapping purity)"
```

---

### Task 13: Docs - usage + consumer matrix + changelog/changenotes

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md` (add a Telegraphs usage section)
- Modify: `docs/CONSUMERS.md` (note the two new packages + umbrella membership)
- Modify: `CHANGELOG.md` (newest-first detailed entry)
- Modify: `CHANGENOTES.md` (newest-first one-line digest)

These doc edits are in the same commit as the version bump in Task 14 per the engine ritual, EXCEPT the usage/consumer prose which can land now. To keep the ritual clean, do CHANGELOG/CHANGENOTES in Task 14. Here, add only the usage + consumer-matrix prose.

- [ ] **Step 1: Add a Telegraphs usage section to `docs/USING-KHAOZENGINE.md`**

Add a section (match the file's existing heading style) covering:
```markdown
## Attack telegraphs / danger zones

`KhaozEngine.Telegraphs` (2D) + `KhaozEngine.Telegraphs.Render3D` (ground plane) draw animated
danger-zone indicators. Presentation only: feed shape + position + a 0..1 progress + a TelegraphStyle
from your own sim each frame; the engine holds no telegraph state (safe under lockstep).

3D (ground plane), `using KhaozEngine.Telegraphs;`:

    float progress = 1f - emitter.TelegraphSeconds / window;   // 0 at telegraph start, 1 at impact
    scene.GroundCircle(emitter.Target, emitter.Radius, progress, TelegraphStyle.Fire);
    scene.GroundRing(emitter.Target, 0f, emitter.ShockwaveRadius, progress, TelegraphStyle.Generic);

2D:

    tg.Begin(spriteBatch, primitiveRenderer);
    tg.Circle(center, radius, progress, TelegraphStyle.Generic);
    tg.End();

Shapes: Circle, Ring, Beam, Cone, Arc. Styles: Generic / Fire / Poison presets, or build a
TelegraphStyle (fill/outline color, edge thickness, opacity, FillMode, TelegraphAnim flags
[OutlinePulse | FillSweep | ColorRamp | ImpactFlash], blend). The 3D path paints onto the
ground/terrain via the depth buffer and is occluded by meshes.
```

- [ ] **Step 2: Note the new packages in `docs/CONSUMERS.md`** (in the prose near the 7.33.0 snapshot note, add a peer note; the version line itself is bumped in Task 14)

Add a sentence such as:
```markdown
The telegraph system ships as two libraries: `KhaozEngine.Telegraphs` (style + resolve + 2D path, in
the `Game2D` umbrella) and `KhaozEngine.Telegraphs.Render3D` (the Scene3D ground-plane extensions, in
the `Game3D` umbrella). Render3D also gains a generic `DrawGroundDecal` depth-sampling primitive.
```
If `docs/CONSUMERS.md` has a per-package umbrella table, add `KhaozEngine.Telegraphs` -> Game2D and
`KhaozEngine.Telegraphs.Render3D` -> Game3D rows.

- [ ] **Step 3: Commit**

```bash
git add docs/USING-KHAOZENGINE.md docs/CONSUMERS.md
git commit -m "docs(telegraphs): usage section + consumer package notes"
```

---

### Task 14: Release ritual (7.33.0 -> 7.34.0)

Single version bump for the whole batch, additive = minor. Follow the order in `KhaozEngine/CLAUDE.md`.

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>`)
- Modify: `CHANGELOG.md`, `CHANGENOTES.md`
- Modify: `docs/CONSUMERS.md` (Engine current version), `docs/ROADMAP.md` (Current released version), `README.md` (the four `<PackageReference>` example versions)

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`: `<KhaozEngineVersion>7.33.0</KhaozEngineVersion>` -> `7.34.0`.

- [ ] **Step 2: Update the three guarded doc declarations**

- `docs/CONSUMERS.md` line ~7: `**Engine current version:** \`7.33.0\`` -> `7.34.0`.
- `docs/ROADMAP.md` line ~3: `Current released version: **7.33.0**` -> `7.34.0`.
- `README.md` lines ~122-125: change all four `Version="7.33.0"` (Game2D/Game3D/Server/Foundation examples) to `Version="7.34.0"`.

- [ ] **Step 3: Add the CHANGELOG entry** (newest-first, at the top of the entries)

```markdown
## 7.34.0

Attack telegraph / danger-zone indicator system. Two new packages: `KhaozEngine.Telegraphs`
(presentation-only style model + the pure `TelegraphResolve` progress->visual mapping + the immediate-mode
`TelegraphRenderer2D` 2D path; in the `Game2D` umbrella) and `KhaozEngine.Telegraphs.Render3D` (the
`Scene3D` ground-plane extensions `GroundCircle/Ring/Beam/Cone/Arc`; in the `Game3D` umbrella). Shapes:
circle, ring, beam, cone, arc; styles: Generic/Fire/Poison presets plus a `TelegraphStyle`
(fill/outline color, edge thickness, opacity, fill mode, composable `OutlinePulse | FillSweep | ColorRamp
| ImpactFlash` animations, alpha/additive blend, reserved safe-zone sense). Render3D gains a generic
`DrawGroundDecal` primitive: a depth-sampling ground decal (new `DecalFrag` shader + pass between the
beam pass and post) that reconstructs each pixel's surface position from the linear-depth buffer, paints
an analytic shape SDF onto the ground/terrain within a Y-band, and is occluded by meshes. Render2D gains
generic `DrawFilledSector` / `DrawFilledArcBand` primitives. Telegraphs are presentation-only and never
enter a game's determinism hash. New GPU golden `telegraph_ground` (baked on Metal + D3D11 + Vulkan).
```

- [ ] **Step 4: Add the CHANGENOTES digest line** (newest-first, one or two sentences)

```markdown
- 7.34.0: Attack telegraph / danger-zone system (KhaozEngine.Telegraphs + .Render3D): animated circle/ring/beam/cone/arc indicators, 2D + a depth-sampling terrain-conforming ground decal in 3D, presentation-only. Render3D gains a generic DrawGroundDecal; Render2D gains filled-sector/arc-band primitives.
```

- [ ] **Step 5: Verify the doc-version guard passes**

Run: `./scripts/check-doc-versions.sh`
Expected: `all engine-version declarations match 7.34.0`.

- [ ] **Step 6: Full build + headless test sweep**

Run: `dotnet build KhaozEngine.slnx` then `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: Build succeeded; all non-GPU tests pass (GPU goldens are skipped without `KE_GPU_TESTS=1`).

- [ ] **Step 7: Pack to local-feed**

Run: `dotnet pack -c Release -o ./local-feed`
Expected: produces `KhaozEngine.Telegraphs.7.34.0.nupkg` and `KhaozEngine.Telegraphs.Render3D.7.34.0.nupkg` (plus the repacked rest) in `local-feed/`.

- [ ] **Step 8: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "feat(7.34.0): attack telegraph / danger-zone indicator system"
```

- [ ] **Step 9: Tag + push are the finishing step**

Tagging `v7.34.0` and pushing `main` + the tag (CI publishes on `v*`) happens in the finishing-a-development-branch step after the worktree merges to `main`. Do NOT tag mid-plan. The D3D11/Vulkan golden bake (Task 9 Step 5) must be committed before the final push or `cross-platform-gpu.yml` fails.

---

## Self-review notes (coverage)

- Spec shape kit (circle/ring/beam/cone/arc): Tasks 5 (2D), 7 (shader SDFs), 11 (3D wrappers). ✔
- Style model + presets + resolve: Tasks 2, 3. ✔
- 3D depth-sampling terrain-conforming decal: Tasks 6, 7, 8, 9. ✔
- 2D path: Tasks 4, 5. ✔
- Packaging (two new packages, Snapshot-style, correct umbrellas): Tasks 1, 10. ✔
- Determinism neutrality: Task 12 (+ immediate-mode design throughout). ✔
- Headless tests every behavior + GPU golden with cross-backend bake: Tasks 2-12 (headless), 9 (golden). ✔
- Safe-zone reserved, not implemented: `ZoneSense` enum in Task 2, no rendering. ✔
- Release ritual + doc guard + consumer matrix: Tasks 13, 14. ✔
- First consumer (cunnuth) is documented in the spec + usage doc; actual wiring is the SpaceGame chat, out of scope here. ✔

Type-consistency check: `GroundDecal` / `DecalShape` / `DecalBlend` (Task 6) are used identically in Tasks 7, 9, 11. `TelegraphStyle` / `TelegraphAnim` / `FillMode` / `TelegraphBlend` / `ZoneSense` (Task 2) and `ResolvedTelegraph` / `TelegraphResolve.Resolve` (Task 3) are used consistently in Tasks 5, 11, 12. `DecalUbo` / `PackUbo` (Task 7) match the test in Task 7 and the shader UBO block. `Size` packing is documented once on `GroundDecal` and consumed identically by the shader (Task 7) and the builders (Task 11).
