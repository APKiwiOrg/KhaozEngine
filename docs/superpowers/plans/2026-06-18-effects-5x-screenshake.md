# Graduate Effects to 5.x + ScreenShake Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Graduate `KhaozEngine.Effects` from the dead 4.x MonoGame particle package onto the 5.x line, and add `ScreenShake` — a trauma-based, deterministic screen-shake offset generator.

**Architecture:** Part 1 retires the superseded particle code, drops MonoGame, and moves Effects onto `<KhaozEngine5xVersion>`. Part 2 adds `ScreenShake`, a pure System.Numerics offset generator (no `Camera2D` dependency) the game composes onto its render camera. Shake magnitude is `trauma^2` with seeded smooth (sine-sum) noise; decays over time.

**Tech Stack:** C# / net10.0, `System.Numerics` (`Vector2`), xUnit. Headless.

**Spec:** `docs/superpowers/specs/2026-06-18-effects-5x-screenshake-design.md`

---

## File Structure

- `KhaozEngine.Effects/KhaozEngine.Effects.csproj` (rewrite) — graduate to 5.x, drop MonoGame/Graphics.
- Delete: `KhaozEngine.Effects/{ParticleSystem,ParticleEmission,ParticleEmitterConfig,ParticlePresets,ParticleView}.cs`; `KhaozEngine.Tests/{ParticleSystemTests,ParticlePresetsTests}.cs`.
- `KhaozEngine.Effects/README.md` (rewrite) — describe ScreenShake.
- `KhaozEngine.Effects/ScreenShake.cs` (new) — the shake generator.
- `KhaozEngine.Tests/ScreenShakeTests.cs` (new) — headless coverage.
- Release/doc (Task 3): `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `CLAUDE.md`.

Reference facts:
- Only `KhaozEngine.Effects.csproj` and `KhaozEngine.Tests.csproj` reference `KhaozEngine.Effects`; only the two deleted test files reference its particle types (confirmed by search). Deletion is clean.
- Baseline full suite on this branch: 1266 passed / 6 GPU-skipped. The two deleted test files hold 13 tests, so after Task 1 the suite is 1253 passed / 6 skipped; Task 2 adds ScreenShake tests.
- `KhaozEngine.Tests` keeps its `<ProjectReference>` to Effects (now the 5.x Effects).
- Run all `dotnet` commands from the worktree root.

---

## Task 1: Graduate `KhaozEngine.Effects` to the 5.x line

**Files:**
- Delete: 5 Effects particle sources + 2 particle test files (paths below).
- Modify: `KhaozEngine.Effects/KhaozEngine.Effects.csproj`, `KhaozEngine.Effects/README.md`.

This task is a retirement/graduation (not TDD): the verification is that the solution still builds and the suite is green with the particle tests gone.

- [ ] **Step 1: Delete the dead particle code and its tests**

```bash
git rm KhaozEngine.Effects/ParticleSystem.cs \
       KhaozEngine.Effects/ParticleEmission.cs \
       KhaozEngine.Effects/ParticleEmitterConfig.cs \
       KhaozEngine.Effects/ParticlePresets.cs \
       KhaozEngine.Effects/ParticleView.cs \
       KhaozEngine.Tests/ParticleSystemTests.cs \
       KhaozEngine.Tests/ParticlePresetsTests.cs
```

- [ ] **Step 2: Rewrite the Effects csproj**

Overwrite `KhaozEngine.Effects/KhaozEngine.Effects.csproj` with exactly:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Effects</PackageId>
    <!-- Shared 5.x line (see docs/ROADMAP.md "The post-MonoGame pivot"). Graduated off MonoGame at 5.56.0;
         the old 4.x rect-particle system was retired (superseded by KhaozEngine.Particles). -->
    <Version>$(KhaozEngine5xVersion)</Version>
    <Description>Game-feel visual effects for the custom MonoGame-free 5.x stack (System.Numerics + BCL only). ScreenShake: a trauma-based, deterministic screen-shake offset generator (trauma^2 falloff, seeded smooth noise, positional + rotational offset) the game composes onto its render camera. Particle simulation now lives in KhaozEngine.Particles.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Rewrite the Effects README**

Overwrite `KhaozEngine.Effects/README.md` with exactly:

````markdown
# KhaozEngine.Effects

Game-feel visual effects for the MonoGame-free 5.x stack (System.Numerics + BCL only).

## ScreenShake

A trauma-based, deterministic screen-shake offset generator. Add trauma on impacts; the shake magnitude
falls off as trauma squared and decays over time. Seeded smooth noise keeps it reproducible and
headless-testable (no `System.Random` / wall-clock). It produces a positional `Offset` and a rotational
`Angle` the game composes onto its render camera:

```csharp
var shake = new ScreenShake();
shake.Add(0.6f);              // on an explosion / hit
// each frame:
shake.Update(dt);
renderCamera.Position = camera.Position + shake.Offset;
renderCamera.Rotation = camera.Rotation + shake.Angle;
```

The old 4.x rect-particle system that used to live here was retired; particle simulation now lives in
`KhaozEngine.Particles`.
````

- [ ] **Step 4: Build and run the suite (verify graduation is clean)**

Run: `dotnet build KhaozEngine.Effects/KhaozEngine.Effects.csproj -c Debug`
Expected: builds with no MonoGame restore (the package is now reference-free). An assembly with no types yet is fine.
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all green, 1253 passed / 6 skipped (the 13 particle tests are gone; nothing else referenced the deleted types).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(effects): graduate KhaozEngine.Effects to 5.x — retire MonoGame particle system"
```

---

## Task 2: `ScreenShake`

**Files:**
- Create: `KhaozEngine.Effects/ScreenShake.cs`
- Test: `KhaozEngine.Tests/ScreenShakeTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/ScreenShakeTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Effects;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for the trauma-based ScreenShake offset generator.</summary>
public class ScreenShakeTests
{
    private const float Tol = 1e-4f;

    [Fact]
    public void Add_RaisesTraumaClampedToOne()
    {
        var s = new ScreenShake();
        s.Add(0.5f);
        Assert.Equal(0.5f, s.Trauma, Tol);
        s.Add(0.7f);
        Assert.Equal(1f, s.Trauma, Tol);   // 1.2 clamped to 1
    }

    [Fact]
    public void Add_AtOrAboveOneClampsToOne()
    {
        var s = new ScreenShake();
        s.Add(2f);
        Assert.Equal(1f, s.Trauma, Tol);
    }

    [Fact]
    public void Add_IgnoresNegativeAmount()
    {
        var s = new ScreenShake();
        s.Add(0.4f);
        s.Add(-0.2f);
        Assert.Equal(0.4f, s.Trauma, Tol);
    }

    [Fact]
    public void Update_DrainsTraumaFlooredAtZero()
    {
        var s = new ScreenShake { DecayPerSecond = 1f };
        s.Add(0.3f);
        s.Update(1f);   // 0.3 - 1 < 0 -> floored to 0
        Assert.Equal(0f, s.Trauma, Tol);
    }

    [Fact]
    public void ZeroTrauma_ProducesNoOffsetOrAngle()
    {
        var s = new ScreenShake();
        s.Update(0.1f);   // trauma still 0
        Assert.Equal(Vector2.Zero, s.Offset);
        Assert.Equal(0f, s.Angle, Tol);
    }

    [Fact]
    public void Offset_BoundedByTraumaSquaredTimesMaxOffset()
    {
        var s = new ScreenShake { DecayPerSecond = 0f, MaxOffset = 30f };
        s.Add(1f);
        for (int i = 0; i < 50; i++)
        {
            s.Update(0.01f);
            Assert.True(MathF.Abs(s.Offset.X) <= 30f + Tol, $"|offX| {s.Offset.X} exceeds 30");
            Assert.True(MathF.Abs(s.Offset.Y) <= 30f + Tol, $"|offY| {s.Offset.Y} exceeds 30");
        }
    }

    [Fact]
    public void Offset_ScalesWithTraumaSquared()
    {
        var full = new ScreenShake(seed: 7) { DecayPerSecond = 0f };
        var half = new ScreenShake(seed: 7) { DecayPerSecond = 0f };
        full.Add(1f);
        half.Add(0.5f);
        full.Update(0.1f);
        half.Update(0.1f);   // same seed + same elapsed -> same noise

        float ratio = half.Offset.Length() / full.Offset.Length();
        Assert.Equal(0.25f, ratio, 1e-3f);   // (0.5^2) / (1^2)
    }

    [Fact]
    public void Offset_IsDeterministicForSameSeedAndSequence()
    {
        var a = new ScreenShake(seed: 42) { DecayPerSecond = 0f };
        var b = new ScreenShake(seed: 42) { DecayPerSecond = 0f };
        a.Add(0.8f); b.Add(0.8f);
        for (int i = 0; i < 10; i++) { a.Update(0.02f); b.Update(0.02f); }
        Assert.Equal(a.Offset.X, b.Offset.X, Tol);
        Assert.Equal(a.Offset.Y, b.Offset.Y, Tol);
    }

    [Fact]
    public void Offset_OscillatesSignOverTime()
    {
        var s = new ScreenShake { DecayPerSecond = 0f, Frequency = 25f, MaxOffset = 30f };
        s.Add(1f);
        bool sawPositive = false, sawNegative = false;
        for (int i = 0; i < 200; i++)
        {
            s.Update(0.005f);
            if (s.Offset.X > 1f) sawPositive = true;
            if (s.Offset.X < -1f) sawNegative = true;
        }
        Assert.True(sawPositive && sawNegative, "offset X should swing both signs (it shakes, not pushes)");
    }

    [Fact]
    public void MaxAngleZero_ProducesNoAngle()
    {
        var s = new ScreenShake { DecayPerSecond = 0f, MaxAngle = 0f };
        s.Add(1f);
        for (int i = 0; i < 20; i++)
        {
            s.Update(0.02f);
            Assert.Equal(0f, s.Angle, Tol);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ScreenShakeTests"`
Expected: FAIL — `ScreenShake` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Effects/ScreenShake.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Effects
{
    /// <summary>
    /// Trauma-based screen shake. Add trauma on impacts; the shake magnitude falls off as
    /// <c>trauma^2</c> and decays over time. A pure offset generator - it does not touch a camera; the game
    /// composes <see cref="Offset"/>/<see cref="Angle"/> onto its render camera. Deterministic: the noise is
    /// seeded smooth (sine-sum) noise, no <see cref="System.Random"/> or wall-clock, so it is reproducible
    /// and headless-testable.
    /// </summary>
    public sealed class ScreenShake
    {
        private readonly float _phaseX;
        private readonly float _phaseY;
        private readonly float _phaseA;
        private float _time;
        private float _trauma;

        /// <summary>Creates a shake; <paramref name="seed"/> fixes the per-channel noise phases.</summary>
        public ScreenShake(uint seed = 1)
        {
            _phaseX = seed * 1.0f;
            _phaseY = seed * 2.0f + 1.3f;
            _phaseA = seed * 3.0f + 2.7f;
        }

        /// <summary>Current trauma, 0..1.</summary>
        public float Trauma => _trauma;

        /// <summary>Positional offset magnitude (world units) at trauma 1.</summary>
        public float MaxOffset { get; set; } = 30f;

        /// <summary>Rotational offset magnitude (radians) at trauma 1; set 0 for positional-only shake.</summary>
        public float MaxAngle { get; set; } = 0.1f;

        /// <summary>Trauma drained per second by <see cref="Update"/>.</summary>
        public float DecayPerSecond { get; set; } = 1f;

        /// <summary>Oscillation speed (higher = faster shaking).</summary>
        public float Frequency { get; set; } = 25f;

        /// <summary>Adds trauma (e.g. on an explosion/hit); the result is clamped to [0,1]. Non-positive
        /// amounts are ignored.</summary>
        public void Add(float amount)
        {
            if (amount <= 0f) return;
            _trauma = MathF.Min(1f, _trauma + amount);
        }

        /// <summary>Drains trauma by <see cref="DecayPerSecond"/>*<paramref name="dt"/> (floored at 0) and
        /// advances the internal noise time by <paramref name="dt"/>*<see cref="Frequency"/>.</summary>
        public void Update(float dt)
        {
            _trauma = MathF.Max(0f, _trauma - DecayPerSecond * dt);
            _time += dt * Frequency;
        }

        /// <summary>Positional offset this frame: <c>trauma^2 * MaxOffset * noise</c>, per axis.</summary>
        public Vector2 Offset
        {
            get
            {
                float m = _trauma * _trauma * MaxOffset;
                return new Vector2(m * Noise(_phaseX), m * Noise(_phaseY));
            }
        }

        /// <summary>Rotational offset this frame (radians): <c>trauma^2 * MaxAngle * noise</c>.</summary>
        public float Angle => _trauma * _trauma * MaxAngle * Noise(_phaseA);

        // Smooth deterministic noise in [-1,1] from the internal time at a per-channel phase.
        private float Noise(float phase)
            => 0.6f * MathF.Sin(_time + phase) + 0.4f * MathF.Sin(2.13f * _time + 1.7f * phase);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ScreenShakeTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all green, 1263 passed / 6 skipped (1253 + 10 ScreenShake).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Effects/ScreenShake.cs KhaozEngine.Tests/ScreenShakeTests.cs
git commit -m "feat(effects): ScreenShake — trauma-based deterministic shake offset generator"
```

---

## Task 3: Release ritual (5.56.0)

Additive (graduation + new feature) → minor bump. Follows `KhaozEngine/CLAUDE.md` release order.

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `CLAUDE.md`.

- [ ] **Step 1: Bump the 5.x version**

In `Directory.Build.props`, change `<KhaozEngine5xVersion>5.55.0</KhaozEngine5xVersion>` to `5.56.0`.

- [ ] **Step 2: Move `Effects` from the 4.x list to the 5.x list in `Directory.Build.props`**

In the same file's comments:
- The 4.x-line comment lists `(Effects/Graphics/Input/Screens/Sprites/Time/UI)` — change to `(Graphics/Input/Screens/Sprites/Time/UI)`.
- The 5.x-line comment lists the custom-stack packages ending `...Audio, Particles, Game)` — change to `...Audio, Particles, Effects, Game)`.

- [ ] **Step 3: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert directly above the `## 5.55.0 (custom 5.x line)` heading:

```markdown
## 5.56.0 (custom 5.x line)

Screen shake on the 5.x engine, and `KhaozEngine.Effects` graduates off MonoGame onto the 5.x line - the
last camera feel-layer slice (parallax aside).

- **`KhaozEngine.Effects` graduated to the 5.x line.** The old 4.x rect-particle system (MonoGame + Graphics)
  was retired - it was superseded by the MonoGame-free `KhaozEngine.Particles` and had no game consumer. The
  package now targets System.Numerics + BCL only and versions with `<KhaozEngine5xVersion>`. The 4.x line is
  down to `Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`.
- **`ScreenShake`** (`KhaozEngine.Effects`) - a trauma-based, deterministic shake offset generator. `Add(amount)`
  bumps trauma on impacts; the magnitude falls off as `trauma^2` and `Update(dt)` drains it. It exposes a
  positional `Offset` and rotational `Angle` (seeded smooth noise, no `System.Random`/wall-clock) that the
  game composes onto its render camera - it never mutates a camera itself. `MaxOffset`/`MaxAngle`/
  `DecayPerSecond`/`Frequency` tune the feel; `MaxAngle = 0` gives positional-only shake.
```

- [ ] **Step 4: Update the three guard-checked doc declarations**

In `docs/CONSUMERS.md`, change the `**Engine current version:** \`5.55.0\`` line to `5.56.0`.
In `docs/ROADMAP.md`, change the `Current released version: **5.55.0**` line (near the top) to `5.56.0`.
In `README.md`, change every `Version="5.55.0"` in the `<PackageReference>` example block to `5.56.0` (grep `grep -n "5.55.0" README.md` first to find all ~4 lines).

- [ ] **Step 5: Move `Effects` 4.x→5.x in the membership docs**

Make these edits (grep each target string first; if a string differs materially from what is quoted, report it rather than guessing):
- `docs/ROADMAP.md`: two occurrences of the 4.x list `` (`Effects`/`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`) `` (near line 6 and line 150) → drop the `` `Effects`/ `` so each reads `` (`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`) ``.
- `docs/CONSUMERS.md`: the same 4.x list near line 13 (`` `Effects`/`Graphics`/... ``) → drop `` `Effects`/ ``; and near line 207-208 the prose "the 7 genuinely-MonoGame packages (Effects/Graphics/Input/Screens/Sprites/Time/UI)" → "the 6 genuinely-MonoGame packages (Graphics/Input/Screens/Sprites/Time/UI)".
- `CLAUDE.md`: in the "Two shared version lines" section, the 5.x custom-stack list ending `` `Game`) `` — add `` `Effects`, `` before `` `Game`) `` (so it reads `...`Particles`, `Effects`, `Game`)` — match the surrounding backtick/comma style); and the 4.x list `` (`Effects`/`Graphics`/`Input`/`Screens`/`Sprites`/`Time`/`UI`) `` → drop `` `Effects`/ ``.

- [ ] **Step 6: Update the ROADMAP camera + screen-shake sections**

In `docs/ROADMAP.md`:
- In the "Camera: first-class follow / scroller camera" section, under `**Shipped:**` append:
```markdown
- 5.56.0: `ScreenShake` (`KhaozEngine.Effects`, graduated to 5.x) - **screen shake**: trauma-based,
  deterministic offset generator (`trauma^2` falloff, seeded smooth noise, positional + rotational), composed
  onto the render camera. Pairs with the follow / room cameras.
```
- In that section's `**Still open**` list, delete the bullet beginning `- Screen shake that perturbs the camera` (it references `KhaozEngine.Effects`). Leave the parallax bullet intact.
- In the standalone `## Screen shake (\`KhaozEngine.Effects\`)` section, prepend a `**Shipped (5.56.0):**` note (e.g. add a first line `**Shipped (5.56.0)** as the trauma-based `ScreenShake` offset generator; see the camera section.`) rather than deleting the section.

- [ ] **Step 7: Verify the doc-version guard passes**

Run: `bash scripts/check-doc-versions.sh`
Expected: exit 0 (the three version declarations match `<KhaozEngine5xVersion>` = 5.56.0). The membership-list edits are not guard-checked.

- [ ] **Step 8: Test and pack**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green, 1263 / 6 skipped).
Run: `dotnet pack -c Release -o ./local-feed`
Expected: builds `KhaozEngine.Effects.5.56.0.nupkg` (now a 5.x package) plus the other 5.x packages into `local-feed` (cumulative; do not delete old versions). Confirm with `ls local-feed/KhaozEngine.Effects.5.56.0.nupkg`.

- [ ] **Step 9: Commit and tag**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md CLAUDE.md
git commit -m "effects(5.56.0): graduate to 5.x + ScreenShake; move Effects to the 5.x version line"
git tag v5.56.0
```

Pushing `main` + the tag happens at branch-finish time, not here.

---

## Self-Review Notes

- **Spec coverage:** graduation — delete particle code + tests, rewrite csproj/README (Task 1); `ScreenShake` model + all test-matrix items (Task 2); version bump + changelog + three guard declarations + membership-doc moves + ROADMAP shipped/still-open + pack/tag (Task 3). All spec sections mapped.
- **Type consistency:** `ScreenShake(uint seed = 1)`, props `Trauma`(get)/`MaxOffset`/`MaxAngle`/`DecayPerSecond`/`Frequency`, `Add(float)`, `Update(float)`, `Offset`(Vector2)/`Angle`(float) used consistently between Task 2's tests and implementation. Namespace `KhaozEngine.Effects`.
- **No placeholders:** every code step shows complete file content; commands have expected output. Membership-doc edits enumerate each file/string and instruct the implementer to report mismatches rather than guess.
