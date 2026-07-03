# Bounded play area (`RimFeature` + `WorldBounds` + slope-gate wiring) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the engine's missing bounded-play-area mechanism: a `RimFeature` enclosing terrain wall with passes, a `WorldBounds` authoritative movement clamp, and the slope-gate wiring that makes the rim un-climbable.

**Architecture:** Two complementary pieces plus the wiring that activates the existing-but-dormant slope gate. `RimFeature : ITerrainFeature` (KhaozEngine.Terrain) raises a circular wall (smoothstep ramp + coordinate-hash jagged crest) with `RimPass` corridors cut through it; the diegetic/visual border. `WorldBounds` (KhaozEngine.NetWorld, abstract + `CircleBounds`/`RectBounds`) is the hard clamp applied after `CharacterMovement.Step` in the movement step (`PlayerMoveSimulator` + `PlayerMovementSystem`), clamp-and-slide, nullable (null = today's behaviour). `TerrainCollision.GroundNormal` is exposed and passed as the `groundNormal` slope-gate delegate everywhere movement runs, so the rim can't be walked up (authoritative).

**Tech Stack:** C# net10.0, xUnit headless tests, System.Numerics. No GPU in tests.

## Global Constraints

- TDD: every new behaviour ships with a headless test in `KhaozEngine.Tests` (no GPU, no real device). Construct state directly and assert.
- No em-dashes anywhere (code comments, commit messages, docs). Plain hyphens only.
- `ITerrainFeature.Apply` must be pure in `(x, z, h)` (load-order independent). Plain `float` math (authoritative server + visual client agree; replication corrects tiny FP drift).
- Additive changes only -> **one minor version bump** of `<KhaozEngineVersion>` in `Directory.Build.props` (from `7.50.0` to `7.51.0`, unless a concurrent release already took it; re-check `git fetch` + `git tag` before bumping and go past the highest).
- New public ctor params are OPTIONAL with defaults so existing demos/tests compile unchanged (`WorldBounds? bounds = null`).
- Match surrounding code style: terrain features use `private readonly` fields + a ctor (not public mutable fields); XML doc comments on public types.
- Release ritual (CLAUDE.md): bump version -> CHANGELOG.md + CHANGENOTES.md -> 3 guard declarations (docs/CONSUMERS.md, docs/ROADMAP.md, README.md PackageReference) -> CLAUDE.md package-map note + docs/USING-KHAOZENGINE.md usage section -> `scripts/check-doc-versions.sh` -> `dotnet test` -> `dotnet pack -c Release -o ./local-feed` -> merge to main -> tag `vX.Y.Z` -> push main + tag.

---

### Task 1: `WorldBounds` + `CircleBounds` + `RectBounds` (KhaozEngine.NetWorld)

**Files:**
- Create: `KhaozEngine.NetWorld/WorldBounds.cs`
- Test: `KhaozEngine.Tests/NetWorld/WorldBoundsTests.cs`

**Interfaces:**
- Produces: `abstract class WorldBounds { bool Contains(float x, float z); Vector2 Clamp(float x, float z); }`; `sealed class CircleBounds : WorldBounds` ctor `(Vector2 center, float radius)`, props `Center`, `Radius`; `sealed class RectBounds : WorldBounds` ctor `(float minX, float minZ, float maxX, float maxZ)`, props `MinX/MinZ/MaxX/MaxZ`.
- `Clamp` returns the nearest in-bounds point (a no-op for inside points = idempotent; for outside points it projects onto the boundary, which yields clamp-and-slide when called each tick).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldBoundsTests
{
    [Fact]
    public void Circle_contains_inside_not_outside_boundary_inclusive()
    {
        var b = new CircleBounds(new Vector2(0f, 0f), 10f);
        Assert.True(b.Contains(0f, 0f));
        Assert.True(b.Contains(10f, 0f));     // on the boundary counts as inside
        Assert.False(b.Contains(11f, 0f));
    }

    [Fact]
    public void Circle_clamp_projects_outside_onto_boundary_and_is_idempotent()
    {
        var b = new CircleBounds(new Vector2(0f, 0f), 10f);
        Vector2 inside = b.Clamp(3f, 4f);
        Assert.Equal(new Vector2(3f, 4f), inside);                       // inside unchanged
        Vector2 onEdge = b.Clamp(30f, 40f);                             // dist 50 -> onto r=10
        Assert.Equal(10f, onEdge.Length(), 3);
        Assert.Equal(6f, onEdge.X, 3);                                  // 30/50*10
        Assert.Equal(8f, onEdge.Y, 3);
        Assert.Equal(onEdge, b.Clamp(onEdge.X, onEdge.Y));             // idempotent on the boundary
    }

    [Fact]
    public void Circle_clamp_at_centre_is_safe()
    {
        var b = new CircleBounds(new Vector2(5f, 5f), 10f);
        Assert.Equal(new Vector2(5f, 5f), b.Clamp(5f, 5f));
    }

    [Fact]
    public void Rect_contains_and_clamp_per_axis()
    {
        var b = new RectBounds(-10f, -5f, 10f, 5f);
        Assert.True(b.Contains(0f, 0f));
        Assert.False(b.Contains(20f, 0f));
        Assert.Equal(new Vector2(10f, 0f), b.Clamp(20f, 0f));          // x clamped, z kept
        Assert.Equal(new Vector2(3f, -5f), b.Clamp(3f, -50f));         // z clamped, x kept (slide)
        Assert.Equal(new Vector2(3f, 2f), b.Clamp(3f, 2f));           // inside unchanged
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldBoundsTests"`
Expected: build FAIL (WorldBounds/CircleBounds/RectBounds not defined).

- [ ] **Step 3: Write minimal implementation**

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.NetWorld;

/// <summary>
/// An authoritative play-area shape the movement step clamps to each tick, so a player cannot be pushed
/// (or glitch) outside the bounded region even where the diegetic <c>RimFeature</c> wall could be climbed.
/// <see cref="Clamp"/> returns the nearest in-bounds point: a no-op inside (idempotent) and a projection
/// onto the boundary outside, which produces clamp-and-slide when applied every tick (the tangential part
/// of a blocked move survives). Nullable at the call site - no bounds means unbounded movement.
/// </summary>
public abstract class WorldBounds
{
    /// <summary>True when (x, z) is inside or on the boundary.</summary>
    public abstract bool Contains(float x, float z);

    /// <summary>The nearest point inside-or-on the bounds; (x, z) itself when already inside.</summary>
    public abstract Vector2 Clamp(float x, float z);
}

/// <summary>A circular play area centred at <see cref="Center"/> with radius <see cref="Radius"/>.</summary>
public sealed class CircleBounds : WorldBounds
{
    public CircleBounds(Vector2 center, float radius)
    {
        Center = center;
        Radius = MathF.Max(0f, radius);
    }

    public Vector2 Center { get; }
    public float Radius { get; }

    public override bool Contains(float x, float z)
    {
        float dx = x - Center.X, dz = z - Center.Y;
        return dx * dx + dz * dz <= Radius * Radius;
    }

    public override Vector2 Clamp(float x, float z)
    {
        float dx = x - Center.X, dz = z - Center.Y;
        float d2 = dx * dx + dz * dz;
        if (d2 <= Radius * Radius) return new Vector2(x, z);
        float d = MathF.Sqrt(d2);
        if (d < 1e-6f) return Center;
        float s = Radius / d;
        return new Vector2(Center.X + dx * s, Center.Y + dz * s);
    }
}

/// <summary>An axis-aligned rectangular play area (XZ).</summary>
public sealed class RectBounds : WorldBounds
{
    public RectBounds(float minX, float minZ, float maxX, float maxZ)
    {
        MinX = MathF.Min(minX, maxX);
        MaxX = MathF.Max(minX, maxX);
        MinZ = MathF.Min(minZ, maxZ);
        MaxZ = MathF.Max(minZ, maxZ);
    }

    public float MinX { get; }
    public float MinZ { get; }
    public float MaxX { get; }
    public float MaxZ { get; }

    public override bool Contains(float x, float z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

    public override Vector2 Clamp(float x, float z) =>
        new(Math.Clamp(x, MinX, MaxX), Math.Clamp(z, MinZ, MaxZ));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldBoundsTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/WorldBounds.cs KhaozEngine.Tests/NetWorld/WorldBoundsTests.cs
git commit -m "networld: WorldBounds (CircleBounds + RectBounds) authoritative play-area clamp"
```

---

### Task 2: `RimFeature` + `RimPass` (KhaozEngine.Terrain)

**Files:**
- Create: `KhaozEngine.Terrain/RimFeature.cs`
- Test: `KhaozEngine.Tests/Terrain/RimFeatureTests.cs`

**Interfaces:**
- Produces: `readonly struct RimPass` ctor `(float angleRadians, float halfWidth, float falloff)` with readonly fields `AngleRadians/HalfWidth/Falloff`; `sealed class RimFeature : ITerrainFeature` ctor `(Vector2 center, float innerRadius, float outerRadius, float wallHeight, float ruggedness = 0.25f, RimPass[]? passes = null, int seed = 1, float crestFrequency = 0.05f)`, method `float Apply(float x, float z, float h)`.
- Consumes: `TerrainNoise.SmoothStep`, `TerrainNoise.Fbm` (KhaozEngine.Terrain), `ITerrainFeature` (KhaozEngine.Terrain).

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class RimFeatureTests
    {
        static RimFeature Ring(float ruggedness = 0f, RimPass[]? passes = null) =>
            new RimFeature(Vector2.Zero, innerRadius: 40f, outerRadius: 60f, wallHeight: 30f,
                ruggedness: ruggedness, passes: passes, seed: 7);

        [Fact]
        public void Inside_inner_radius_is_unchanged()
        {
            var rim = Ring();
            Assert.Equal(5f, rim.Apply(0f, 0f, 5f), 4);
            Assert.Equal(5f, rim.Apply(30f, 0f, 5f), 4);   // still inside inner=40
        }

        [Fact]
        public void Ramps_to_wall_height_by_outer_radius_smooth_crest()
        {
            var rim = Ring();                               // ruggedness 0 -> exact
            float onWall = rim.Apply(60f, 0f, 0f);         // at outer radius
            float beyond = rim.Apply(80f, 0f, 0f);         // past outer -> plateau, still WallHeight
            Assert.Equal(30f, onWall, 2);
            Assert.Equal(30f, beyond, 2);
            float mid = rim.Apply(50f, 0f, 0f);            // halfway up the ramp
            Assert.True(mid > 5f && mid < 30f);
        }

        [Fact]
        public void Jagged_crest_stays_within_band_and_varies_by_position()
        {
            var rim = Ring(ruggedness: 0.3f);
            float a = rim.Apply(60f, 0f, 0f);
            float b = rim.Apply(0f, 60f, 0f);
            Assert.InRange(a, 30f * 0.7f - 0.01f, 30f * 1.3f + 0.01f);
            Assert.InRange(b, 30f * 0.7f - 0.01f, 30f * 1.3f + 0.01f);
            Assert.NotEqual(a, b, 3);                       // crest is jagged, not a uniform berm
        }

        [Fact]
        public void Pass_corridor_stays_low()
        {
            // one pass heading +X (angle 0): the +X wall is cut open.
            var rim = Ring(passes: new[] { new RimPass(angleRadians: 0f, halfWidth: 8f, falloff: 6f) });
            float atPass = rim.Apply(60f, 0f, 0f);         // along +X at outer radius -> open
            float offPass = rim.Apply(0f, 60f, 0f);        // +Z wall, far from the pass -> full wall
            Assert.True(atPass < 3f, $"pass not open: {atPass}");
            Assert.True(offPass > 25f, $"wall too low: {offPass}");
        }

        [Fact]
        public void Pass_only_opens_along_its_heading_not_the_opposite_wall()
        {
            var rim = Ring(passes: new[] { new RimPass(angleRadians: 0f, halfWidth: 8f, falloff: 6f) });
            float opposite = rim.Apply(-60f, 0f, 0f);      // -X wall: behind the heading -> still a wall
            Assert.True(opposite > 25f, $"opposite wall opened: {opposite}");
        }

        [Fact]
        public void Deterministic_in_position_and_seed()
        {
            var rim = Ring(ruggedness: 0.3f);
            Assert.Equal(rim.Apply(55f, 7f, 0f), rim.Apply(55f, 7f, 0f), 6);   // pure in (x,z)
            var other = new RimFeature(Vector2.Zero, 40f, 60f, 30f, ruggedness: 0.3f, seed: 99);
            Assert.NotEqual(rim.Apply(55f, 7f, 0f), other.Apply(55f, 7f, 0f), 4);   // seed changes the crest
        }

        [Fact]
        public void Composes_with_lake_and_flatten_in_a_field()
        {
            var cfg = new TerrainConfig
            {
                Seed = 3,
                Biomes = new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f } },
                GentleAmplitude = 0f,
                Features = new ITerrainFeature[]
                {
                    new LakeFeature(centerX: -12f, centerZ: 0f, radius: 8f, depth: 4f),
                    new FlattenFeature(centerX: 10f, centerZ: 0f, radius: 6f, targetHeight: 1f),
                    new RimFeature(Vector2.Zero, 40f, 60f, 30f, ruggedness: 0f, seed: 3),
                },
            };
            var field = new TerrainField(cfg);
            Assert.True(field.SampleHeight(-12f, 0f) < -1f, "lake not carved under the rim");
            Assert.Equal(1f, field.SampleHeight(10f, 0f), 1);            // flattened pad
            Assert.True(field.SampleHeight(0f, 60f) > 25f, "rim wall not raised in the field");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RimFeatureTests"`
Expected: build FAIL (RimFeature/RimPass not defined).

- [ ] **Step 3: Write minimal implementation**

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>A gap cut through the rim wall along a heading (the road out): the wall is lowered to ~0 within
    /// <see cref="HalfWidth"/> of the ray from the rim centre at <see cref="AngleRadians"/>, ramping back to the
    /// full wall by <see cref="HalfWidth"/> + <see cref="Falloff"/> (perpendicular distance). The pass only opens
    /// the wall on the heading side, not the opposite wall.</summary>
    public readonly struct RimPass
    {
        public RimPass(float angleRadians, float halfWidth, float falloff)
        {
            AngleRadians = angleRadians;
            HalfWidth = MathF.Max(0f, halfWidth);
            Falloff = MathF.Max(1e-3f, falloff);
        }

        /// <summary>Heading of the corridor from the rim centre (radians; dir = (cos, sin) in (x, z)).</summary>
        public readonly float AngleRadians;
        /// <summary>Half the open corridor width (world units, perpendicular to the heading).</summary>
        public readonly float HalfWidth;
        /// <summary>Perpendicular distance over which the wall ramps from open back to full.</summary>
        public readonly float Falloff;
    }

    /// <summary>
    /// Raises terrain into an enclosing wall around a bounded region: unchanged inside <c>InnerRadius</c>, a
    /// smoothstep ramp up to <c>WallHeight</c> by <c>OuterRadius</c> and held at <c>WallHeight</c> beyond (a
    /// plateau, so you cannot see/walk past it), modulated by a coordinate-hash jagged crest (<c>Ruggedness</c>,
    /// reusing <see cref="TerrainNoise"/>) so it reads as mountains not a smooth berm. <c>Passes</c> cut corridors
    /// through the wall so a road can leave. The visual/diegetic border; the authoritative hard stop is
    /// <c>KhaozEngine.NetWorld.WorldBounds</c>, and the rim is kept un-climbable by the movement slope gate
    /// (pass <c>TerrainCollision.GroundNormal</c> as the <c>groundNormal</c> delegate).
    /// MVP is circular: <see cref="Apply"/> is shaped around a "distance to the play-area boundary" (here the
    /// distance from <c>Center</c>) so a rect/polygon variant can swap the distance metric and reuse the ramp.
    /// Pure in (x, z) like every <see cref="ITerrainFeature"/>.
    /// </summary>
    public sealed class RimFeature : ITerrainFeature
    {
        readonly Vector2 _center;
        readonly float _inner, _outer, _wallHeight, _ruggedness, _crestFreq;
        readonly int _seed;
        readonly RimPass[] _passes;

        public RimFeature(Vector2 center, float innerRadius, float outerRadius, float wallHeight,
            float ruggedness = 0.25f, RimPass[]? passes = null, int seed = 1, float crestFrequency = 0.05f)
        {
            _center = center;
            _inner = innerRadius;
            _outer = MathF.Max(innerRadius + 1e-3f, outerRadius);
            _wallHeight = wallHeight;
            _ruggedness = Math.Clamp(ruggedness, 0f, 1f);
            _seed = seed;
            _crestFreq = crestFrequency;
            _passes = passes ?? Array.Empty<RimPass>();
        }

        public float Apply(float x, float z, float h)
        {
            // "distance to the play-area boundary": circular MVP = distance from the centre. A rect/polygon
            // variant replaces only this metric and the inner/outer interpretation; the ramp below is shared.
            float dx = x - _center.X, dz = z - _center.Y;
            float d = MathF.Sqrt(dx * dx + dz * dz);

            float t = TerrainNoise.SmoothStep(_inner, _outer, d);   // 0 inside inner, 1 by outer (and beyond)
            if (t <= 0f) return h;                                  // unchanged inside the play area

            // Jagged crest: symmetric coordinate-hash noise scales the wall height within +/- ruggedness.
            float crest = 1f;
            if (_ruggedness > 0f)
                crest = 1f + _ruggedness * TerrainNoise.Fbm(x * _crestFreq, z * _crestFreq, _seed);

            float gate = PassGate(dx, dz);                          // 1 = full wall, 0 = fully open at a pass
            return h + _wallHeight * t * crest * gate;
        }

        // 1 away from every pass; drops to 0 along an open corridor (perpendicular distance), heading-side only.
        float PassGate(float dx, float dz)
        {
            float gate = 1f;
            for (int i = 0; i < _passes.Length; i++)
            {
                RimPass p = _passes[i];
                Vector2 dir = new(MathF.Cos(p.AngleRadians), MathF.Sin(p.AngleRadians));
                Vector2 rel = new(dx, dz);
                float along = Vector2.Dot(rel, dir);
                if (along <= 0f) continue;                          // pass opens outward along its heading only
                float perp = (rel - dir * along).Length();
                float g = TerrainNoise.SmoothStep(p.HalfWidth, p.HalfWidth + p.Falloff, perp);
                if (g < gate) gate = g;                             // most-open pass wins
            }
            return gate;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RimFeatureTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Terrain/RimFeature.cs KhaozEngine.Tests/Terrain/RimFeatureTests.cs
git commit -m "terrain: RimFeature + RimPass enclosing wall with road-out corridors"
```

---

### Task 3: `TerrainCollision.GroundNormal` + rim-is-unwalkable test (slope-gate premise)

**Files:**
- Modify: `KhaozEngine.Terrain/TerrainCollision.cs` (add `GroundNormal`)
- Test: `KhaozEngine.Tests/Terrain/RimFeatureTests.cs` (add a slope-gate test using TerrainCollision)

**Interfaces:**
- Produces: `Vector3 TerrainCollision.GroundNormal(float x, float z)` (= `TerrainField.SampleNormal`), usable directly as the `Func<float,float,Vector3>` slope-gate delegate.

- [ ] **Step 1: Write the failing test** (append to RimFeatureTests.cs; add `using KhaozEngine.Locomotion;` if needed - not needed here)

```csharp
        [Fact]
        public void Rim_wall_is_unwalkable_but_the_pass_is_walkable()
        {
            var cfg = new TerrainConfig
            {
                Seed = 3,
                Biomes = new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f } },
                GentleAmplitude = 0f,
                Features = new ITerrainFeature[]
                {
                    new RimFeature(Vector2.Zero, 40f, 56f, 30f, ruggedness: 0f, seed: 3,
                        passes: new[] { new RimPass(angleRadians: 0f, halfWidth: 8f, falloff: 6f) }),
                },
            };
            var col = new TerrainCollision(new TerrainField(cfg));
            float maxSlope = MathF.PI * 50f / 180f;
            Assert.True(col.GroundNormal(0f, 0f).Y > 0.99f);                      // flat inside
            Assert.False(col.IsWalkable(0f, 48f, maxSlope), "rim wall mid-band should be too steep");
            Assert.True(col.IsWalkable(48f, 0f, maxSlope), "the pass corridor should stay walkable");
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RimFeatureTests.Rim_wall_is_unwalkable"`
Expected: build FAIL (`GroundNormal` not defined).

- [ ] **Step 3: Write minimal implementation** (add to TerrainCollision.cs after `GroundHeight`; class already `using System.Numerics;`)

```csharp
        /// <summary>Surface normal at the world point (= TerrainField.SampleNormal). Pass this as the
        /// <c>groundNormal</c> slope-gate delegate to CharacterMovement.Step so steep terrain (the rim wall)
        /// cannot be walked up.</summary>
        public Vector3 GroundNormal(float x, float z) => _field.SampleNormal(x, z);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RimFeatureTests"`
Expected: PASS (8 tests). If `Rim_wall_is_unwalkable` fails on a slope threshold, adjust the sampled z (e.g. 46-50) so it lands in the steep mid-band; the band for inner=40/outer=56/h=30 is steeper than 50deg between ~d=42 and ~d=54.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Terrain/TerrainCollision.cs KhaozEngine.Tests/Terrain/RimFeatureTests.cs
git commit -m "terrain: TerrainCollision.GroundNormal (slope-gate delegate) + rim-unwalkable test"
```

---

### Task 4: Wire `WorldBounds` + `groundNormal` slope gate into the movement step

**Files:**
- Modify: `KhaozEngine.NetWorld/PlayerMoveSimulator.cs` (add `WorldBounds? bounds` ctor param; clamp after Step)
- Modify: `KhaozEngine.NetWorld/PlayerMovementSystem.cs` (add `WorldBounds? bounds` ctor param; clamp in ForEach)
- Modify: `KhaozEngine.NetWorld/WorldServer.cs` (add `WorldBounds? bounds = null` ctor param; pass to simulator)
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs` (add `WorldBounds? bounds = null` ctor param; pass to movement + spawnClamp)
- Modify: `KhaozEngine.NetWorld/WorldClient.cs` (add `WorldBounds? bounds = null` ctor param; pass to prediction simulator so it matches the server)
- Test: `KhaozEngine.Tests/NetWorld/MovementBoundsTests.cs`

**Interfaces:**
- Consumes: `WorldBounds`/`CircleBounds`/`RectBounds` (Task 1), `CharacterMovement.Step`, `MoveTuning`, `PlayerMoveState`.
- Produces: `PlayerMoveSimulator(Func<float,float,float> groundHeight, MoveTuning tuning, Func<float,float,Vector3>? groundNormal = null, WorldBounds? bounds = null)`; `PlayerMovementSystem(..., WorldBounds? bounds = null)`; `WorldServer(..., Func<float,float,Vector3>? groundNormal = null, WorldBounds? bounds = null)`; `ShardedWorldServer(..., Func<float,float,Vector3>? groundNormal = null, WorldBounds? bounds = null)`; `WorldClient(..., Func<float,float,Vector3>? groundNormal = null, WorldBounds? bounds = null)`. Bounds is applied AFTER CharacterMovement.Step, re-deriving Y from the ground at the clamped XZ.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class MovementBoundsTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand East = new(new Vector2(1f, 0f), run: true, cameraYaw: 0f);

    [Fact]
    public void Simulator_clamps_player_inside_circle_bounds()
    {
        var bounds = new CircleBounds(new Vector2(0f, 0f), 5f);
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        for (int i = 0; i < 200; i++) s = sim.Step(s, East, 1f / 30f);   // drive east forever
        Assert.True(bounds.Contains(s.Position.X, s.Position.Z));
        Assert.True(s.Position.X <= 5f + 1e-3f);
        Assert.Equal(5f, s.Position.X, 2);                                // pinned on the edge
    }

    [Fact]
    public void Simulator_slides_along_a_rect_edge_keeping_tangential_progress()
    {
        // wall at x=5; drive north-east -> x pins at 5, z keeps advancing (slide).
        var bounds = new RectBounds(-100f, -100f, 5f, 100f);
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var ne = new MoveCommand(Vector2.Normalize(new Vector2(1f, 1f)), run: true, cameraYaw: 0f);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        for (int i = 0; i < 120; i++) s = sim.Step(s, ne, 1f / 30f);
        Assert.Equal(5f, s.Position.X, 2);                                // clamped to the wall
        Assert.True(s.Position.Z > 5f, $"no tangential slide: z={s.Position.Z}");
    }

    [Fact]
    public void Slope_gate_blocks_a_step_onto_too_steep_ground()
    {
        // a near-vertical wall for x>2 (normal.Y ~ 0) -> stepping east past x=2 is blocked.
        Func<float, float, Vector3> normal = (x, z) => x > 2f ? new Vector3(1f, 0.05f, 0f) : new Vector3(0f, 1f, 0f);
        var sim = new PlayerMoveSimulator((x, z) => 0f, MoveTuning.Default, groundNormal: normal);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        for (int i = 0; i < 200; i++) s = sim.Step(s, East, 1f / 30f);
        Assert.True(s.Position.X <= 2f + 1e-3f, $"climbed the steep wall to x={s.Position.X}");
    }

    [Fact]
    public void Bounded_prediction_reconciles_against_a_bounded_server_with_no_persistent_error()
    {
        var bounds = new CircleBounds(new Vector2(0f, 0f), 5f);
        var clientSim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var serverSim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var settings = PredictionSettings.Default with { TickSeconds = 1f / 30f };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(clientSim, settings);

        pred.Reset(new PlayerMoveState { Position = Vector3.Zero });
        var serverState = new PlayerMoveState { Position = Vector3.Zero };
        int seq = 0;
        for (int i = 0; i < 200; i++)
        {
            pred.Predict(East);                                   // client predicts into the wall (bounded)
            serverState = serverSim.Step(serverState, East, settings.TickSeconds);   // server steps the same (bounded)
            ReconciliationResult r = pred.Reconcile(authoritativeTick: i, serverState, lastAcknowledgedSeq: seq++);
            // both clamp identically -> reconciliation error stays tiny (prediction not broken at the wall).
            Assert.True(r.PositionError < 0.5f, $"tick {i}: error {r.PositionError}");
        }
        Assert.Equal(serverState.Position.X, pred.PredictedState.Position.X, 2);
    }

    [Fact]
    public void WorldServer_holds_a_player_inside_bounds()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var bounds = new CircleBounds(new Vector2(0f, 0f), 6f);
        var cfg = new WorldServerConfig { TickSeconds = 1f / 30f, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var client = new NetClient(ct);
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        for (int i = 0; i < 300; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, East), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState s));
        Assert.True(bounds.Contains(s.Position.X, s.Position.Z), $"escaped to {s.Position}");
    }

    [Fact]
    public void ShardedWorldServer_holds_a_player_inside_bounds()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var bounds = new CircleBounds(new Vector2(0f, 0f), 8f);
        var cfg = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f, CellSize = 10f, OverlapMargin = 4f, InterestRadius = 4f,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var client = new NetClient(ct);
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        for (int i = 0; i < 300; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, East), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState s));
        Assert.True(bounds.Contains(s.Position.X, s.Position.Z), $"escaped to {s.Position}");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~MovementBoundsTests"`
Expected: build FAIL (the new ctor params do not exist yet).

- [ ] **Step 3: Write minimal implementation**

`PlayerMoveSimulator.cs` - replace the class body fields/ctor/Step:

```csharp
public sealed class PlayerMoveSimulator : ITickSimulator<PlayerMoveState, MoveCommand>
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;
    private readonly WorldBounds? bounds;

    public PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.bounds = bounds;
    }

    /// <summary>Advances one player by one command over <paramref name="dt"/> seconds, ground-clamped and
    /// (when a <see cref="WorldBounds"/> is set) clamped into the play area (clamp-and-slide).</summary>
    public PlayerMoveState Step(in PlayerMoveState state, in MoveCommand command, float dt)
    {
        Vector3 p = CharacterMovement.Step(state.Position, command, dt, groundHeight, tuning, groundNormal);
        if (bounds is not null)
        {
            Vector2 c = bounds.Clamp(p.X, p.Z);
            p = new Vector3(c.X, groundHeight(c.X, c.Y) + tuning.CapsuleHalfHeight, c.Y);
        }
        return new() { Position = p };
    }
}
```

`PlayerMovementSystem.cs` - add the field/ctor param and clamp in the ForEach body:

```csharp
public sealed class PlayerMovementSystem : ISystem
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;
    private readonly WorldBounds? bounds;

    public PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.bounds = bounds;
    }

    public void Update(World world, float dt)
    {
        world.ForEach<NetId, ReplicatedPosition, PendingMove>((Entity e, ref NetId _, ref ReplicatedPosition pos, ref PendingMove move) =>
        {
            if (world.Has<Ghost>(e) || world.Has<Migrating>(e)) return;   // owner is the only simulator
            Vector3 p = CharacterMovement.Step(pos.Value, move.Command, dt, groundHeight, tuning, groundNormal);
            if (bounds is not null)
            {
                Vector2 c = bounds.Clamp(p.X, p.Z);
                p = new Vector3(c.X, groundHeight(c.X, c.Y) + tuning.CapsuleHalfHeight, c.Y);
            }
            pos.Value = p;
        });
    }
}
```

`WorldServer.cs` - extend the ctor and pass bounds to the simulator:

```csharp
    public WorldServer(INetTransport transport, WorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
        interest = new InterestGrid(MathF.Max(1f, config.InterestRadius));
    }
```

`ShardedWorldServer.cs` - extend the ctor and pass bounds to movement + spawnClamp:

```csharp
    public ShardedWorldServer(INetTransport transport, ShardedWorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        if (config.InterestRadius > config.OverlapMargin)
            throw new ArgumentException(
                $"InterestRadius {config.InterestRadius} must be <= OverlapMargin {config.OverlapMargin} so the home cell can hold the full AoI as ghosts.",
                nameof(config));

        movement = new PlayerMovementSystem(groundHeight, tuning, groundNormal, bounds);
        spawnClamp = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds);
        host = new ShardHost(
            cellSize: config.CellSize,
            tickSeconds: config.TickSeconds,
            registry: registry,
            interestCellSize: config.CellSize,
            overlapMargin: config.OverlapMargin,
            positionAccessor: PositionAccessor);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
    }
```

`WorldClient.cs` - extend the ctor and pass bounds to the prediction simulator (so client prediction clamps identically to the server, keeping reconciliation clean at the wall):

```csharp
    public WorldClient(INetTransport transport, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        config ??= new WorldClientConfig();
        net = new NetClient(transport, token);
        view = new ClientReplicationView(registry);
        var simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds);
        PredictionSettings settings = config.Prediction ?? (PredictionSettings.Default with { TickSeconds = config.TickSeconds });
        prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator, settings);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~MovementBoundsTests"`
Expected: PASS (6 tests). Then run the full NetWorld suite to confirm no regression: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetWorld"`.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/PlayerMoveSimulator.cs KhaozEngine.NetWorld/PlayerMovementSystem.cs KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.NetWorld/ShardedWorldServer.cs KhaozEngine.NetWorld/WorldClient.cs KhaozEngine.Tests/NetWorld/MovementBoundsTests.cs
git commit -m "networld: clamp movement to nullable WorldBounds + wire authoritative slope gate"
```

---

### Task 5: `TerrainPresets.BoundedClearing()`

**Files:**
- Modify: `KhaozEngine.Terrain/TerrainPresets.cs` (add `BoundedClearing`)
- Test: `KhaozEngine.Tests/Terrain/RimFeatureTests.cs` (add a preset smoke test)

**Interfaces:**
- Produces: `static TerrainConfig TerrainPresets.BoundedClearing(int seed = 5)` - a single gentle meadow ringed by a `RimFeature` (mountains) with one +Z pass, plus a carved lake; the rim is the border, the meadow is the play area.

- [ ] **Step 1: Write the failing test** (append to RimFeatureTests.cs)

```csharp
        [Fact]
        public void BoundedClearing_is_flat_inside_and_walled_around()
        {
            var field = new TerrainField(TerrainPresets.BoundedClearing());
            float maxSlope = MathF.PI * 50f / 180f;
            var col = new TerrainCollision(field);
            Assert.True(MathF.Abs(field.SampleHeight(0f, 0f)) < 4f, "centre should be roughly flat meadow");
            Assert.True(field.SampleHeight(0f, 55f) > 20f, "north should be walled by the rim");
            Assert.True(col.IsWalkable(0f, 0f, maxSlope), "the clearing floor should be walkable");
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BoundedClearing"`
Expected: build FAIL (`BoundedClearing` not defined).

- [ ] **Step 3: Write minimal implementation** (add to TerrainPresets.cs; add `using System.Numerics;` at the top)

```csharp
        /// <summary>A bounded forest clearing: a single gentle meadow ringed by a RimFeature mountain wall with
        /// one pass to the north (+Z, the road out) and a carved lake. The rim is the diegetic border (un-climbable
        /// once the movement slope gate is wired with TerrainCollision.GroundNormal); pair with a
        /// KhaozEngine.NetWorld.WorldBounds for an authoritative hard stop. The first ready-made bounded zone -
        /// games compose their own (town pads, buildings) on top.</summary>
        public static TerrainConfig BoundedClearing(int seed = 5) => new TerrainConfig
        {
            Seed = seed,
            WaterLevel = -1.2f,
            GentleFrequency = 0.02f,
            GentleAmplitude = 1.0f,
            DetailFrequency = 0.03f,
            DetailOctaves = 4,
            Biomes = new[]
            {
                // one gentle meadow everywhere; the rim provides the mountains (no +Z mountains band here).
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 1.0f },
            },
            Features = new ITerrainFeature[]
            {
                new LakeFeature(centerX: -12f, centerZ: -4f, radius: 8f, depth: 3.6f),
                new RimFeature(
                    center: Vector2.Zero, innerRadius: 38f, outerRadius: 56f, wallHeight: 30f,
                    ruggedness: 0.3f, seed: seed,
                    passes: new[] { new RimPass(angleRadians: MathF.PI / 2f /* +Z */, halfWidth: 10f, falloff: 6f) }),
            },
        };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RimFeatureTests"`
Expected: PASS (all RimFeatureTests including BoundedClearing).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Terrain/TerrainPresets.cs KhaozEngine.Tests/Terrain/RimFeatureTests.cs
git commit -m "terrain: TerrainPresets.BoundedClearing (meadow ringed by a rim wall with a +Z pass)"
```

---

### Task 6: `TerrainWalkSample` bounded mode (windowed demo)

**Files:**
- Modify: `TerrainWalkSample/Program.cs` (a `bounded` CLI arg: use `BoundedClearing` + wire the slope gate via `GroundNormal`)

**Interfaces:**
- Consumes: `TerrainPresets.BoundedClearing` (Task 5), `TerrainCollision.GroundNormal` (Task 3), `CharacterController3D.Update(..., groundNormal)` (existing).
- No test (windowed sample; KE_MAX_FRAMES headless smoke covers it via the existing GameApp3D path).

- [ ] **Step 1: Edit Program.cs** - detect the arg, switch the preset, and pass the slope-gate delegate.

At the top of the file, after the console banner, read the flag from the process args:

```csharp
bool bounded = Array.Exists(args, a => a is "bounded" or "--bounded");
Console.WriteLine(bounded
    ? "Bounded play area: mountains ring the clearing; one pass to the NORTH (+Z) is the way out. You can't climb the walls."
    : "TerrainWalkSample - WASD move | mouse-drag orbit | scroll zoom | shift run | Esc quit");
using (var app = new TerrainWalkApp(bounded))
    app.Run();
return 0;
```

Add a field + ctor param to `TerrainWalkApp`:

```csharp
    readonly bool _bounded;

    public TerrainWalkApp(bool bounded = false)
        : base(new GameAppOptions
        {
            Title = bounded ? "KhaozEngine - Bounded clearing" : "KhaozEngine - Terrain walk",
            Width = 1280,
            Height = 720,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Color(0.45f, 0.62f, 0.85f, 1f),   // sky
        })
    { _bounded = bounded; }
```

In `OnLoad`, choose the preset:

```csharp
        _field = new TerrainField(_bounded ? TerrainPresets.BoundedClearing() : TerrainPresets.Clearing());
        _terrain = new TerrainCollision(_field);
```

Wire the slope gate at the settle call (so the spawn settle uses it too) and store nothing else - the `OnUpdate` call below also passes it. The settle line becomes:

```csharp
        _character.Update(InputState.Empty, 0f, 0f, _terrain.GroundHeight, _bounded ? _terrain.GroundNormal : null);   // settle Y onto the ground
```

In `OnUpdate`, pass the slope-gate delegate when bounded so the rim cannot be climbed:

```csharp
        _character.Update(Input, dt, _camera.Yaw, _terrain.GroundHeight, _bounded ? _terrain.GroundNormal : null);
```

- [ ] **Step 2: Build the sample**

Run: `dotnet build TerrainWalkSample/TerrainWalkSample.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 3: Headless smoke run**

Run: `KE_MAX_FRAMES=3 dotnet run --project TerrainWalkSample/TerrainWalkSample.csproj -c Debug -- bounded`
Expected: exits 0, prints the bounded banner + "Streaming the world: primed N chunks".

- [ ] **Step 4: Commit**

```bash
git add TerrainWalkSample/Program.cs
git commit -m "sample(terrainwalk): bounded mode (BoundedClearing + slope-gated rim) via 'bounded' arg"
```

---

### Task 7: Release ritual (version bump, docs, pack, merge, tag, push)

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>`), `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `CLAUDE.md`, `docs/USING-KHAOZENGINE.md`

- [ ] **Step 1: Re-check the version is free**

```bash
git fetch origin && git tag | sort -V | tail -5 && git show origin/main:Directory.Build.props | grep KhaozEngineVersion
```
Pick the next free minor above the highest of {local 7.50.0, origin, tags}. Target `7.51.0`; if `v7.51.0` exists, go to the next free minor.

- [ ] **Step 2: Bump `Directory.Build.props`** - set `<KhaozEngineVersion>7.51.0</KhaozEngineVersion>`.

- [ ] **Step 3: CHANGELOG.md** - newest-first detailed entry under a `## 7.51.0` heading: RimFeature + RimPass (Terrain), WorldBounds/CircleBounds/RectBounds (NetWorld) clamped in PlayerMoveSimulator/PlayerMovementSystem/WorldServer/ShardedWorldServer/WorldClient (nullable, off = unchanged), TerrainCollision.GroundNormal + authoritative slope-gate wiring, TerrainPresets.BoundedClearing, TerrainWalkSample bounded mode.

- [ ] **Step 4: CHANGENOTES.md** - one newest-first digest line for 7.51.0.

- [ ] **Step 5: Guard declarations** - update the three the guard checks to 7.51.0: `docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` example(s).

- [ ] **Step 6: Doc sweep** - add the new types to `CLAUDE.md` (Terrain package note: RimFeature/RimPass; NetWorld note: WorldBounds/CircleBounds/RectBounds + slope-gate wiring; TerrainCollision.GroundNormal) and a "Bounded zones" usage section to `docs/USING-KHAOZENGINE.md`. Grep the new names across `*.md` + `CLAUDE.md` to confirm coverage.

- [ ] **Step 7: Run the guard + full tests**

```bash
./scripts/check-doc-versions.sh
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
```
Expected: guard prints "all engine-version declarations match 7.51.0"; tests all green.

- [ ] **Step 8: Commit the release bump**

```bash
git add -A && git commit -m "bounded(7.51.0): RimFeature + WorldBounds + slope-gate wiring + BoundedClearing"
```

- [ ] **Step 9: Merge to main, pack from main root, tag, push**

```bash
cd /Users/antonio/KhaozEngine
git merge --no-ff worktree-feature+bounded-play-area -m "Merge bounded play area (7.51.0): RimFeature + WorldBounds + slope-gate wiring"
mkdir -p local-feed && dotnet pack -c Release -o ./local-feed
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj   # green on the merged result
git tag v7.51.0
git push origin main && git push origin v7.51.0
```

- [ ] **Step 10: Clean up the worktree + branch**

```bash
git worktree remove .claude/worktrees/feature+bounded-play-area
git branch -d worktree-feature+bounded-play-area
```

---

## Self-Review

**Spec coverage:**
- RimFeature (+ RimPass), circular MVP, Apply shaped for rect/polygon -> Task 2 (Apply built around "distance to boundary").
- Slope-gate wiring (TerrainCollision.GroundNormal, passed through controller + sim, authoritative, MaxSlope) -> Tasks 3, 4, 6.
- WorldBounds (CircleBounds + RectBounds), clamped in WorldServer + ShardedWorldServer, nullable -> Tasks 1, 4.
- Bounded demo preset + sample -> Tasks 5, 6.
- Testing list: RimFeature inside/ramp/pass/deterministic/composes -> Task 2; rim unwalkable + pass walkable (slope gate) -> Task 3; WorldBounds Contains/Clamp circle+rect, outside-onto-boundary, idempotent -> Task 1; slope gate blocks steep + gentle walkable + authoritative consistency -> Task 4; movement integration held-inside + slide + prediction-not-broken -> Task 4.
- Out of scope (town/building content, RuinborneStartZone, rect/polygon rim, water/PBR, navmesh/gates, prop/building collision, gravity/jump/step-height) -> not built.

**Placeholder scan:** none - every code step shows complete code.

**Type consistency:** ctor signatures for PlayerMoveSimulator/PlayerMovementSystem/WorldServer/ShardedWorldServer/WorldClient all add `WorldBounds? bounds` after `groundNormal`; RimFeature/RimPass/WorldBounds/CircleBounds/RectBounds names consistent across tasks.
