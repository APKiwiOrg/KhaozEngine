# KhaozEngine.Effects Particle System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote Nullwake's `HitParticleSystem` into a new game-agnostic `KhaozEngine.Effects` package as a data-driven, pooled particle system whose built-in `Spark`/`Ember` presets reproduce Nullwake's look exactly.

**Architecture:** A fixed-size, zero-allocation struct pool (`ParticleSystem`) emits bursts described by an immutable `ParticleEmitterConfig` record. One system mixes particles from different presets (each particle stores its own behavioral params). `Update(double)` integrates motion with real (unscaled) delta; `Draw` is a thin shim over `KhaozEngine.UI.PrimitiveRenderer.DrawFilledRect`; `ActiveParticles()` exposes read-only snapshots for headless tests and custom rendering.

**Tech Stack:** net10.0, C# records/`readonly record struct`, MonoGame.Framework.DesktopGL 3.8 (via `KhaozEngine.UI` ProjectReference), xUnit.

---

## Context for the implementer

- Work happens in the worktree `.claude/worktrees/effects-particles` on branch `worktree-effects-particles`. Do all work there.
- **Release discipline (hard rule):** do NOT edit `Directory.Build.props` `<Version>`, do NOT touch `CHANGELOG.md`, do NOT `dotnet pack`. A coordinating chat owns the batched 3.3.0 release. Version is inherited (3.2.0) and left alone.
- Repo conventions: one public type per file; inter-package deps via `ProjectReference`; every package ships a packed `README.md`; tests are flat `*Tests.cs` xUnit files in `KhaozEngine.Tests/`.
- Source of truth being ported: `~/Nullwake/Nullwake/Nullwake.Core/Rendering/HitParticleSystem.cs`. Design spec: `docs/superpowers/specs/2026-06-11-khaozengine-effects-particles-design.md`.
- Run all tests with: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. Baseline before this work: **268 passing**.

## File structure

- Create `KhaozEngine.Effects/KhaozEngine.Effects.csproj` — package project, refs MonoGame + `KhaozEngine.UI`.
- Create `KhaozEngine.Effects/README.md` — packed readme.
- Create `KhaozEngine.Effects/ParticleEmission.cs` — emission-pattern enum.
- Create `KhaozEngine.Effects/ParticleEmitterConfig.cs` — immutable preset/config record.
- Create `KhaozEngine.Effects/ParticlePresets.cs` — built-in `Spark`/`Ember` presets.
- Create `KhaozEngine.Effects/ParticleView.cs` — read-only live-particle snapshot.
- Create `KhaozEngine.Effects/ParticleSystem.cs` — the pool (Emit/Update/Draw/ActiveParticles).
- Create `KhaozEngine.Tests/ParticlePresetsTests.cs` — preset-value tests.
- Create `KhaozEngine.Tests/ParticleSystemTests.cs` — pool/update behavior tests.
- Modify `KhaozEngine.slnx` — add the project.
- Modify `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add a ProjectReference.

---

## Task 1: Scaffold the `KhaozEngine.Effects` project and wire it into the solution

**Files:**
- Create: `KhaozEngine.Effects/KhaozEngine.Effects.csproj`
- Create: `KhaozEngine.Effects/README.md`
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`

- [ ] **Step 1: Create the project file**

Create `KhaozEngine.Effects/KhaozEngine.Effects.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Effects</PackageId>
    <Description>Game-agnostic pooled particle system: data-driven emitter presets (sparks, embers, custom) drawn as filled rectangles.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.*" />
    <ProjectReference Include="../KhaozEngine.UI/KhaozEngine.UI.csproj" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the package README**

Create `KhaozEngine.Effects/README.md`:

```markdown
# KhaozEngine.Effects

Game-agnostic pooled particle system for MonoGame games.

- `ParticleSystem` — fixed-size, zero-allocation pool of rectangle particles.
- `ParticleEmitterConfig` — immutable, data-driven emitter preset (lifetime,
  speed, emission pattern, jitter, size curve, sway, acceleration, color).
- `ParticlePresets.Spark` / `.Ember` — built-in presets (outward sparks,
  upward-drifting embers).

```csharp
var particles = new ParticleSystem(rng);
particles.Emit(ParticlePresets.Spark, screenPos, oreColor, count: 4);
particles.Emit(ParticlePresets.Ember, screenPos, count: 3);

particles.Update(realDeltaSeconds);   // real (unscaled) delta
particles.Draw(spriteBatch, primitiveRenderer);
```

Derive custom presets with `with`:

```csharp
var fast = ParticlePresets.Spark with { MaxSpeed = 120f, Acceleration = new Vector2(0, 200) };
```
```

- [ ] **Step 3: Add the project to the solution**

In `KhaozEngine.slnx`, add this line immediately after the `KhaozEngine.Ecs` line (keep alphabetical order):

```xml
  <Project Path="KhaozEngine.Effects/KhaozEngine.Effects.csproj" />
```

- [ ] **Step 4: Reference the project from the test project**

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add this line immediately after the `KhaozEngine.Ecs` ProjectReference (keep alphabetical order):

```xml
    <ProjectReference Include="../KhaozEngine.Effects/KhaozEngine.Effects.csproj" />
```

- [ ] **Step 5: Build the solution to verify wiring**

Run: `dotnet build KhaozEngine.Effects/KhaozEngine.Effects.csproj`
Expected: Build succeeded (an empty assembly is fine — no types yet).

Then run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 268 passing (unchanged — nothing added yet).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Effects/KhaozEngine.Effects.csproj KhaozEngine.Effects/README.md KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "Scaffold KhaozEngine.Effects package"
```

---

## Task 2: Data types and built-in presets

**Files:**
- Create: `KhaozEngine.Effects/ParticleEmission.cs`
- Create: `KhaozEngine.Effects/ParticleEmitterConfig.cs`
- Create: `KhaozEngine.Effects/ParticleView.cs`
- Create: `KhaozEngine.Effects/ParticlePresets.cs`
- Test: `KhaozEngine.Tests/ParticlePresetsTests.cs`

- [ ] **Step 1: Write the failing preset-value tests**

Create `KhaozEngine.Tests/ParticlePresetsTests.cs`:

```csharp
using Microsoft.Xna.Framework;
using KhaozEngine.Effects;
using Xunit;

namespace KhaozEngine.Tests;

public class ParticlePresetsTests
{
    [Fact]
    public void Spark_matches_nullwake_values()
    {
        var s = ParticlePresets.Spark;
        Assert.Equal(40f, s.MinSpeed);
        Assert.Equal(80f, s.MaxSpeed);
        Assert.Equal(0.22f, s.MinLife);
        Assert.Equal(0.35f, s.MaxLife);
        Assert.Equal(2f, s.StartSize);
        Assert.Equal(1f, s.EndSizeFactor);
        Assert.Equal(ParticleEmission.Radial, s.Emission);
        Assert.Equal(3f, s.JitterX);
        Assert.Equal(3f, s.JitterY);
        Assert.Equal(Color.White, s.BlendTarget);
        Assert.Equal(0.5f, s.BlendAmount);
        Assert.Null(s.OverrideColor);
    }

    [Fact]
    public void Ember_matches_nullwake_values()
    {
        var e = ParticlePresets.Ember;
        Assert.Equal(15f, e.MinSpeed);
        Assert.Equal(25f, e.MaxSpeed);
        Assert.Equal(0.45f, e.MinLife);
        Assert.Equal(0.7f, e.MaxLife);
        Assert.Equal(3f, e.StartSize);
        Assert.Equal(0.3f, e.EndSizeFactor);
        Assert.Equal(ParticleEmission.Directional, e.Emission);
        Assert.Equal(new Vector2(0f, -1f), e.Direction);
        Assert.Equal(5f, e.JitterX);
        Assert.Equal(3f, e.JitterY);
        Assert.Equal(6f, e.SwayFrequency);
        Assert.Equal(8f, e.SwayAmplitude);
        Assert.Equal(new Color(255, 160, 40), e.OverrideColor);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: FAIL — build error, `ParticlePresets` / `ParticleEmission` / config members do not exist.

- [ ] **Step 3: Create the emission enum**

Create `KhaozEngine.Effects/ParticleEmission.cs`:

```csharp
namespace KhaozEngine.Effects;

/// <summary>How a particle's initial velocity direction is chosen at emit time.</summary>
public enum ParticleEmission
{
    /// <summary>Random direction over the full circle (outward burst).</summary>
    Radial,

    /// <summary>Along <see cref="ParticleEmitterConfig.Direction"/>, jittered by the spread cone.</summary>
    Directional,
}
```

- [ ] **Step 4: Create the config record**

Create `KhaozEngine.Effects/ParticleEmitterConfig.cs`:

```csharp
using Microsoft.Xna.Framework;

namespace KhaozEngine.Effects;

/// <summary>
/// Data-driven emitter preset: all tunables for a burst of particles. Immutable;
/// derive variants with <c>with</c> expressions, e.g.
/// <c>ParticlePresets.Spark with { MaxSpeed = 120f }</c>.
/// </summary>
public sealed record ParticleEmitterConfig
{
    /// <summary>Minimum particle lifetime in seconds.</summary>
    public float MinLife { get; init; }

    /// <summary>Maximum particle lifetime in seconds.</summary>
    public float MaxLife { get; init; }

    /// <summary>Minimum initial speed (units/second).</summary>
    public float MinSpeed { get; init; }

    /// <summary>Maximum initial speed (units/second).</summary>
    public float MaxSpeed { get; init; }

    /// <summary>Particle size at spawn (pixels).</summary>
    public float StartSize { get; init; } = 1f;

    /// <summary>End size as a fraction of <see cref="StartSize"/>. 1 = constant, &lt;1 shrinks over life.</summary>
    public float EndSizeFactor { get; init; } = 1f;

    /// <summary>How the initial direction is chosen.</summary>
    public ParticleEmission Emission { get; init; } = ParticleEmission.Radial;

    /// <summary>Base direction for <see cref="ParticleEmission.Directional"/> (need not be normalized).</summary>
    public Vector2 Direction { get; init; } = new(0f, -1f);

    /// <summary>Half-angle (radians) of the directional spread cone.</summary>
    public float SpreadRadians { get; init; }

    /// <summary>Half-extent of the random spawn offset on X (pixels).</summary>
    public float JitterX { get; init; }

    /// <summary>Half-extent of the random spawn offset on Y (pixels).</summary>
    public float JitterY { get; init; }

    /// <summary>Horizontal sway frequency (radians/second). 0 disables sway.</summary>
    public float SwayFrequency { get; init; }

    /// <summary>Horizontal sway amplitude (pixels/second of positional drift).</summary>
    public float SwayAmplitude { get; init; }

    /// <summary>Constant acceleration applied to velocity each frame (e.g. gravity).</summary>
    public Vector2 Acceleration { get; init; } = Vector2.Zero;

    /// <summary>If set, particles use this color and ignore the emit base color.</summary>
    public Color? OverrideColor { get; init; }

    /// <summary>Target the emit base color is lerped toward when <see cref="OverrideColor"/> is null.</summary>
    public Color BlendTarget { get; init; } = Color.White;

    /// <summary>Lerp amount [0,1] from the emit base color toward <see cref="BlendTarget"/>.</summary>
    public float BlendAmount { get; init; }
}
```

- [ ] **Step 5: Create the particle snapshot type**

Create `KhaozEngine.Effects/ParticleView.cs`:

```csharp
using Microsoft.Xna.Framework;

namespace KhaozEngine.Effects;

/// <summary>
/// Read-only snapshot of a live particle, for headless tests and custom rendering.
/// <see cref="Size"/> is the current draw size (after the size-over-life curve);
/// <see cref="Color"/> is the base color before alpha fade.
/// </summary>
public readonly record struct ParticleView(
    Vector2 Position, Vector2 Velocity, Color Color, float Size, float Life, float MaxLife);
```

- [ ] **Step 6: Create the presets**

Create `KhaozEngine.Effects/ParticlePresets.cs`:

```csharp
using Microsoft.Xna.Framework;

namespace KhaozEngine.Effects;

/// <summary>Built-in emitter presets. <see cref="Spark"/> and <see cref="Ember"/> reproduce Nullwake's hit effects.</summary>
public static class ParticlePresets
{
    /// <summary>Fast outward spark burst, lightened toward white. Nullwake mining-hit look.</summary>
    public static readonly ParticleEmitterConfig Spark = new()
    {
        MinSpeed = 40f,
        MaxSpeed = 80f,
        MinLife = 0.22f,
        MaxLife = 0.35f,
        StartSize = 2f,
        EndSizeFactor = 1f,
        Emission = ParticleEmission.Radial,
        JitterX = 3f,
        JitterY = 3f,
        BlendTarget = Color.White,
        BlendAmount = 0.5f,
    };

    /// <summary>Slow upward-drifting embers with horizontal sway. Nullwake damage-over-time look.</summary>
    public static readonly ParticleEmitterConfig Ember = new()
    {
        MinSpeed = 15f,
        MaxSpeed = 25f,
        MinLife = 0.45f,
        MaxLife = 0.7f,
        StartSize = 3f,
        EndSizeFactor = 0.3f,
        Emission = ParticleEmission.Directional,
        Direction = new Vector2(0f, -1f),
        SpreadRadians = 0f,
        JitterX = 5f,
        JitterY = 3f,
        SwayFrequency = 6f,
        SwayAmplitude = 8f,
        OverrideColor = new Color(255, 160, 40),
    };
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 270 passing (268 + 2 new).

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Effects/ParticleEmission.cs KhaozEngine.Effects/ParticleEmitterConfig.cs KhaozEngine.Effects/ParticleView.cs KhaozEngine.Effects/ParticlePresets.cs KhaozEngine.Tests/ParticlePresetsTests.cs
git commit -m "Add KhaozEngine.Effects config, presets, and snapshot types"
```

---

## Task 3: `ParticleSystem` pool — Emit and ActiveCount

**Files:**
- Create: `KhaozEngine.Effects/ParticleSystem.cs`
- Test: `KhaozEngine.Tests/ParticleSystemTests.cs`

- [ ] **Step 1: Write the failing pool tests**

Create `KhaozEngine.Tests/ParticleSystemTests.cs`:

```csharp
using System;
using System.Linq;
using Microsoft.Xna.Framework;
using KhaozEngine.Effects;
using Xunit;

namespace KhaozEngine.Tests;

public class ParticleSystemTests
{
    private static ParticleSystem NewSystem(int poolSize = 80)
        => new(new Random(12345), poolSize);

    [Fact]
    public void Emit_adds_active_particles()
    {
        var sys = NewSystem();
        sys.Emit(ParticlePresets.Spark, new Vector2(100, 100), Color.Gray, 5);
        Assert.Equal(5, sys.ActiveCount);
    }

    [Fact]
    public void Emit_beyond_capacity_caps_at_pool_size()
    {
        var sys = NewSystem(poolSize: 4);
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), Color.Gray, 10);
        Assert.Equal(4, sys.ActiveCount);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: FAIL — build error, `ParticleSystem` does not exist.

- [ ] **Step 3: Create `ParticleSystem` with the pool, Emit, and ActiveCount**

Create `KhaozEngine.Effects/ParticleSystem.cs`:

```csharp
using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Effects;

/// <summary>
/// Fixed-size, zero-allocation pool of rectangle particles. Emit bursts from
/// <see cref="ParticleEmitterConfig"/> presets; one system can mix particles from
/// different presets. Update with real (unscaled) delta so effects stay smooth
/// regardless of game speed.
/// </summary>
public sealed class ParticleSystem
{
    private struct Particle
    {
        public float X, Y;
        public float VelX, VelY;
        public float Life, MaxLife;
        public float StartSize, EndSizeFactor;
        public float AccelX, AccelY;
        public float SwayFrequency, SwayAmplitude, Phase;
        public Color Color;
    }

    private readonly Particle[] _particles;
    private readonly Random _rng;
    private int _cursor;

    /// <summary>Creates a system with a seeded RNG and pool capacity (default 80).</summary>
    public ParticleSystem(Random rng, int poolSize = 80)
    {
        if (poolSize <= 0) throw new ArgumentOutOfRangeException(nameof(poolSize));
        _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        _particles = new Particle[poolSize];
    }

    /// <summary>Number of currently live particles.</summary>
    public int ActiveCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _particles.Length; i++)
                if (_particles[i].Life > 0f) n++;
            return n;
        }
    }

    /// <summary>Emits <paramref name="count"/> particles at <paramref name="position"/> using White as the base color.</summary>
    public void Emit(ParticleEmitterConfig config, Vector2 position, int count)
        => Emit(config, position, Color.White, count);

    /// <summary>Emits <paramref name="count"/> particles at <paramref name="position"/>, blending from <paramref name="baseColor"/>.</summary>
    public void Emit(ParticleEmitterConfig config, Vector2 position, Color baseColor, int count)
    {
        Color color = config.OverrideColor
            ?? Color.Lerp(baseColor, config.BlendTarget, config.BlendAmount);

        for (int i = 0; i < count; i++)
        {
            float speed = config.MinSpeed + (float)(_rng.NextDouble() * (config.MaxSpeed - config.MinSpeed));
            float life = config.MinLife + (float)(_rng.NextDouble() * (config.MaxLife - config.MinLife));

            float vx, vy;
            if (config.Emission == ParticleEmission.Radial)
            {
                double angle = _rng.NextDouble() * Math.PI * 2.0;
                vx = (float)Math.Cos(angle) * speed;
                vy = (float)Math.Sin(angle) * speed;
            }
            else
            {
                float baseAngle = (float)Math.Atan2(config.Direction.Y, config.Direction.X);
                float spread = config.SpreadRadians <= 0f
                    ? 0f
                    : (float)((_rng.NextDouble() * 2.0 - 1.0) * config.SpreadRadians);
                float angle = baseAngle + spread;
                vx = (float)Math.Cos(angle) * speed;
                vy = (float)Math.Sin(angle) * speed;
            }

            float offsetX = (float)((_rng.NextDouble() * 2.0 - 1.0) * config.JitterX);
            float offsetY = (float)((_rng.NextDouble() * 2.0 - 1.0) * config.JitterY);

            ref Particle p = ref _particles[_cursor];
            p.X = position.X + offsetX;
            p.Y = position.Y + offsetY;
            p.VelX = vx;
            p.VelY = vy;
            p.Life = life;
            p.MaxLife = life;
            p.StartSize = config.StartSize;
            p.EndSizeFactor = config.EndSizeFactor;
            p.AccelX = config.Acceleration.X;
            p.AccelY = config.Acceleration.Y;
            p.SwayFrequency = config.SwayFrequency;
            p.SwayAmplitude = config.SwayAmplitude;
            p.Phase = config.SwayAmplitude > 0f ? (float)(_rng.NextDouble() * Math.PI * 2.0) : 0f;
            p.Color = color;

            _cursor = (_cursor + 1) % _particles.Length;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 272 passing.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Effects/ParticleSystem.cs KhaozEngine.Tests/ParticleSystemTests.cs
git commit -m "Add ParticleSystem pool with Emit and ActiveCount"
```

---

## Task 4: `ActiveParticles()` snapshots — color blend and radial spread

**Files:**
- Modify: `KhaozEngine.Effects/ParticleSystem.cs`
- Test: `KhaozEngine.Tests/ParticleSystemTests.cs:ParticleSystemTests` (add methods)

- [ ] **Step 1: Write the failing snapshot/color tests**

Add these methods inside the `ParticleSystemTests` class in `KhaozEngine.Tests/ParticleSystemTests.cs`:

```csharp
    [Fact]
    public void Spark_color_is_base_lerped_to_white()
    {
        var sys = NewSystem();
        var baseColor = new Color(100, 100, 100);
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), baseColor, 1);
        var expected = Color.Lerp(baseColor, Color.White, 0.5f);
        Assert.Equal(expected, sys.ActiveParticles().Single().Color);
    }

    [Fact]
    public void Ember_color_overrides_base()
    {
        var sys = NewSystem();
        sys.Emit(ParticlePresets.Ember, new Vector2(0, 0), Color.Gray, 1);
        Assert.Equal(new Color(255, 160, 40), sys.ActiveParticles().Single().Color);
    }

    [Fact]
    public void Radial_emission_produces_varied_directions()
    {
        var sys = NewSystem();
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), Color.Gray, 16);
        var velocities = sys.ActiveParticles().Select(p => p.Velocity).ToList();
        Assert.True(velocities.Distinct().Count() > 1);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: FAIL — build error, `ParticleSystem.ActiveParticles` does not exist.

- [ ] **Step 3: Add `CurrentSize` and `ActiveParticles` to `ParticleSystem`**

Add `using System.Collections.Generic;` to the top of `KhaozEngine.Effects/ParticleSystem.cs` (below `using System;`). Then add these members to the `ParticleSystem` class (e.g. after the `Emit(config, position, baseColor, count)` method):

```csharp
    /// <summary>Current draw size for a particle given its life fraction.</summary>
    private static float CurrentSize(in Particle p)
    {
        float t = p.Life / p.MaxLife;       // 1 at spawn, 0 at death
        return p.StartSize * (p.EndSizeFactor + (1f - p.EndSizeFactor) * t);
    }

    /// <summary>
    /// Enumerates live particles as snapshots. For tests and custom rendering;
    /// not used by the <see cref="Draw"/> hot path.
    /// </summary>
    public IEnumerable<ParticleView> ActiveParticles()
    {
        for (int i = 0; i < _particles.Length; i++)
        {
            Particle p = _particles[i];
            if (p.Life <= 0f) continue;
            yield return new ParticleView(
                new Vector2(p.X, p.Y), new Vector2(p.VelX, p.VelY),
                p.Color, CurrentSize(p), p.Life, p.MaxLife);
        }
    }
```

Note: `CurrentSize` references `Draw` in its sibling's doc comment; `Draw` is added in Task 6. The XML `<see cref="Draw"/>` will resolve once Task 6 lands. It does not block compilation (1591/cref warnings are not errors here), but if a doc-cref warning appears, it clears in Task 6.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 275 passing.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Effects/ParticleSystem.cs KhaozEngine.Tests/ParticleSystemTests.cs
git commit -m "Add ActiveParticles snapshots; cover color blend and radial spread"
```

---

## Task 5: `Update` — age-out, directional rise with sway, acceleration

**Files:**
- Modify: `KhaozEngine.Effects/ParticleSystem.cs`
- Test: `KhaozEngine.Tests/ParticleSystemTests.cs:ParticleSystemTests` (add methods)

- [ ] **Step 1: Write the failing update tests**

Add these methods inside the `ParticleSystemTests` class:

```csharp
    [Fact]
    public void Particles_age_out_after_their_lifetime()
    {
        var sys = NewSystem();
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), Color.Gray, 5);
        // Spark MaxLife is 0.35s; step well past it.
        for (int i = 0; i < 10; i++) sys.Update(0.1);
        Assert.Equal(0, sys.ActiveCount);
    }

    [Fact]
    public void Ember_rises_and_sways()
    {
        var sys = NewSystem(poolSize: 1);
        sys.Emit(ParticlePresets.Ember, new Vector2(50, 50), Color.Gray, 1);
        var start = sys.ActiveParticles().Single().Position;
        for (int i = 0; i < 5; i++) sys.Update(0.02);
        var now = sys.ActiveParticles().Single().Position;
        Assert.True(now.Y < start.Y, "ember should drift upward");
        Assert.NotEqual(start.X, now.X); // horizontal sway moved it
    }

    [Fact]
    public void Acceleration_changes_velocity_over_time()
    {
        var gravity = ParticlePresets.Spark with { Acceleration = new Vector2(0, 200), SwayAmplitude = 0 };
        var sys = NewSystem(poolSize: 1);
        sys.Emit(gravity, new Vector2(0, 0), Color.Gray, 1);
        float vy0 = sys.ActiveParticles().Single().Velocity.Y;
        sys.Update(0.1);
        float vy1 = sys.ActiveParticles().Single().Velocity.Y;
        Assert.True(vy1 > vy0, "downward gravity should increase Y velocity");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: FAIL — build error, `ParticleSystem.Update` does not exist.

- [ ] **Step 3: Add `Update` to `ParticleSystem`**

Add this method to the `ParticleSystem` class (e.g. after the `Emit` overloads, before `CurrentSize`):

```csharp
    /// <summary>Advances all live particles by <paramref name="realDeltaSeconds"/>.</summary>
    public void Update(double realDeltaSeconds)
    {
        float dt = (float)realDeltaSeconds;
        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];
            if (p.Life <= 0f) continue;

            p.Life -= dt;
            if (p.Life <= 0f) { p.Life = 0f; continue; }

            p.VelX += p.AccelX * dt;
            p.VelY += p.AccelY * dt;
            p.X += p.VelX * dt;
            p.Y += p.VelY * dt;

            if (p.SwayAmplitude > 0f)
            {
                float elapsed = p.MaxLife - p.Life;
                p.X += (float)Math.Sin(elapsed * p.SwayFrequency + p.Phase) * p.SwayAmplitude * dt;
            }
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 278 passing.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Effects/ParticleSystem.cs KhaozEngine.Tests/ParticleSystemTests.cs
git commit -m "Add ParticleSystem Update: motion, sway, acceleration, age-out"
```

---

## Task 6: Size-over-life curve and the `Draw` shim

**Files:**
- Modify: `KhaozEngine.Effects/ParticleSystem.cs`
- Test: `KhaozEngine.Tests/ParticleSystemTests.cs:ParticleSystemTests` (add methods)

- [ ] **Step 1: Write the failing size-curve tests**

Add these methods inside the `ParticleSystemTests` class:

```csharp
    [Fact]
    public void Spark_size_is_constant_over_life()
    {
        var sys = NewSystem(poolSize: 1);
        sys.Emit(ParticlePresets.Spark, new Vector2(0, 0), Color.Gray, 1);
        Assert.Equal(2f, sys.ActiveParticles().Single().Size, 3);
        sys.Update(0.1);
        Assert.Equal(2f, sys.ActiveParticles().Single().Size, 3);
    }

    [Fact]
    public void Ember_size_shrinks_toward_end_factor()
    {
        var sys = NewSystem(poolSize: 1);
        sys.Emit(ParticlePresets.Ember, new Vector2(0, 0), Color.Gray, 1);
        float sizeAtSpawn = sys.ActiveParticles().Single().Size;
        sys.Update(0.2);
        var p = sys.ActiveParticles().Single();
        float t = p.Life / p.MaxLife;
        float expected = 3f * (0.3f + 0.7f * t);
        Assert.Equal(expected, p.Size, 3);
        Assert.True(p.Size < sizeAtSpawn, "ember shrinks as it ages");
    }
```

- [ ] **Step 2: Run tests to verify they pass (size curve already implemented)**

`CurrentSize` was added in Task 4, so these size tests should pass immediately — they lock the curve behavior over life now that `Update` exists.

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 280 passing.

- [ ] **Step 3: Add the `Draw` shim**

Add `using Microsoft.Xna.Framework.Graphics;` and `using KhaozEngine.UI;` to the top of `KhaozEngine.Effects/ParticleSystem.cs`. Then add this method to the `ParticleSystem` class (e.g. after `ActiveParticles`):

```csharp
    /// <summary>Draws all live particles as small filled rectangles, fading out over life.</summary>
    public void Draw(SpriteBatch spriteBatch, PrimitiveRenderer renderer)
    {
        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];
            if (p.Life <= 0f) continue;

            float alpha = p.Life / p.MaxLife;
            int pixelSize = Math.Max(1, (int)(CurrentSize(p) + 0.5f));
            renderer.DrawFilledRect(spriteBatch,
                new Rectangle((int)p.X - pixelSize / 2, (int)p.Y - pixelSize / 2, pixelSize, pixelSize),
                p.Color * alpha);
        }
    }
```

`Draw` is a thin shim: its size/alpha math is the same `CurrentSize` curve covered by the size tests; the only untested part is the `SpriteBatch`/`PrimitiveRenderer` call, which needs a `GraphicsDevice` and is exercised in-game during Nullwake adoption. This matches the item brief (keep the SpriteBatch draw a thin shim).

- [ ] **Step 4: Build to verify the shim compiles**

Run: `dotnet build KhaozEngine.Effects/KhaozEngine.Effects.csproj`
Expected: Build succeeded (and the Task 4 `<see cref="Draw"/>` doc reference now resolves).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, 280 passing.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Effects/ParticleSystem.cs KhaozEngine.Tests/ParticleSystemTests.cs
git commit -m "Add Draw shim and lock size-over-life curve"
```

---

## Done criteria

- `KhaozEngine.Effects` builds and is referenced by `KhaozEngine.Tests`.
- Full suite green: **280 passing** (268 baseline + 12 new).
- No changes to `Directory.Build.props`, `CHANGELOG.md`, or the shared `local-feed`.
- Hand back to the coordinator: branch `worktree-effects-particles`, worktree path, package `KhaozEngine.Effects`, files added, +12 tests, and the open questions from the spec (API surface sign-off; SpaceGame `ParticleManager` overlap; Nullwake call-site migration is a separate adoption item).
