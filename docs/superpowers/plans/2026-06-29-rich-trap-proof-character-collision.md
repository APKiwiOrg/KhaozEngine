# Rich + Trap-Proof Character Collision Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a character collide against full-detail one-sided static meshes (buildings, trees) richly and without ever tunneling/trapping, and bake tree-trunk collision that follows the real leaning trunk.

**Architecture:** Part A replaces the teleport-then-depenetrate horizontal/vertical resolution in `CharacterMovement.Step(in MoveState, …)` with a substepped swept collide-and-slide over `IPhysicsWorld.SweepCapsule` (Bepu `Sweep`, deterministic), plus a step-up probe that finally wires `MoveTuning.StepHeight`; the depenetration loop survives as a residual-overlap settle pass. Part B adds `PropCollisionBake.BakeTrunkHull` (height-cap + running-centreline radial-core filter) and decouples `ke-propbake` so every prop emits a `.coll` while only walkable solids emit a `.surf`.

**Tech Stack:** net10.0, C#, BepuPhysics v2 behind `KhaozEngine.Physics` seam, xUnit headless tests, `System.Numerics`.

## Global Constraints

- Engine is MonoGame-free; one shared version line `<KhaozEngineVersion>` in `Directory.Build.props`. Target bump: **8.3.0**.
- No em-dashes anywhere (code comments, docs, commit messages). Use periods/commas/parentheses.
- Every new behavior ships with a headless test in `KhaozEngine.Tests`.
- `AppWindow` is the only class touching raw input; not relevant here but do not add input deps.
- Determinism is mandatory: the same binary must resolve an identical path to a byte-identical pose (server authority == client prediction). `world == null` paths must stay byte-identical to today.
- Conventional commit subjects `area(scope): summary`; on the version-bump commit the scope is the new version, e.g. `locomotion(8.3.0): ...`.
- Public API of `CharacterMovement.Step` must not change signature (internal behavior only).
- Run tests with: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. A single test: append `--filter "FullyQualifiedName~TestName"`.

---

## Part B — trunk-hull baker + tool emits tree `.coll`

(Done first: additive, self-contained, no behavior change to existing movement tests.)

### Task 1: Extract `HullFromPoints` shared helper

**Files:**
- Modify: `KhaozEngine.Render3D/Models/PropCollisionBake.cs` (refactor `BakeConvexHull`, lines ~152-184)
- Test: `KhaozEngine.Tests/Physics/PropCollisionBakeTests.cs` (existing `SolidProp_BakesAConvexHull_RoundTrips` is the regression guard)

**Interfaces:**
- Produces: `static ConvexHullShape HullFromPoints(IReadOnlyList<Vector3> points)` — dedups (5 mm bucket), deterministic lexicographic sort, caps at `MaxHullPoints` via `KeepExtremePoints`, returns `ConvexHullShape`. Used by `BakeConvexHull` (Task 1) and `BakeTrunkHull` (Task 2).

- [ ] **Step 1: Confirm the existing hull regression test passes before refactor**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SolidProp_BakesAConvexHull_RoundTrips"`
Expected: PASS (baseline before refactor; the octahedron bakes to a 6-point hull).

- [ ] **Step 2: Refactor — extract `HullFromPoints`, make `BakeConvexHull` delegate**

Replace the body of `BakeConvexHull` (the dedup + sort + cap + `new ConvexHullShape`) so it gathers the mesh vertex positions and calls the new helper. Add the helper directly below it:

```csharp
static ConvexHullShape BakeConvexHull(GltfMesh mesh)
{
    var positions = new List<Vector3>(mesh.Vertices.Length);
    foreach (ModelVertex v in mesh.Vertices) positions.Add(v.Position);
    return HullFromPoints(positions);
}

/// <summary>Build a TRUE convex hull from arbitrary local-space points: deduplicate (5 mm spatial bucket),
/// sort deterministically (streaming consistency: server and client must bake the identical shape), then hand
/// the FULL deduplicated set to <see cref="ConvexHullShape"/> (Bepu's hull helper discards interior points).
/// Only when the count exceeds <see cref="MaxHullPoints"/> (a guard for pathologically high-poly meshes) does it
/// cap, and then by keeping the spatially-EXTREME points (never striding). Shared by <see cref="BakeConvexHull"/>
/// (whole mesh) and <see cref="BakeTrunkHull"/> (filtered trunk verts).</summary>
static ConvexHullShape HullFromPoints(IReadOnlyList<Vector3> pts)
{
    var unique = new Dictionary<(int, int, int), Vector3>();
    foreach (Vector3 p in pts)
    {
        int bx = (int)MathF.Round(p.X * 200f);
        int by = (int)MathF.Round(p.Y * 200f);
        int bz = (int)MathF.Round(p.Z * 200f);
        unique.TryAdd((bx, by, bz), p);
    }

    var sortedKeys = new List<(int, int, int)>(unique.Keys);
    sortedKeys.Sort((a, b) =>
    {
        int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
            c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
        return a.Item3.CompareTo(b.Item3);
    });

    Vector3[] points = new Vector3[sortedKeys.Count];
    for (int i = 0; i < sortedKeys.Count; i++) points[i] = unique[sortedKeys[i]];

    if (points.Length > MaxHullPoints)
        points = KeepExtremePoints(points, MaxHullPoints);

    return new ConvexHullShape(points);
}
```

Leave `KeepExtremePoints` unchanged. Add `using System.Collections.Generic;` if not already present (it is).

- [ ] **Step 3: Run the regression test to verify it still passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SolidProp_BakesAConvexHull_RoundTrips"`
Expected: PASS (identical hull — the refactor is behavior-preserving; the octahedron still bakes to 6 points).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Render3D/Models/PropCollisionBake.cs
git commit -m "render3d: extract HullFromPoints shared hull builder"
```

---

### Task 2: `BakeTrunkHull` — leaning-trunk hull

**Files:**
- Modify: `KhaozEngine.Render3D/Models/PropCollisionBake.cs` (add constants + `BakeTrunkHull`)
- Test: `KhaozEngine.Tests/Physics/PropCollisionBakeTests.cs` (add fixtures + tests)

**Interfaces:**
- Consumes: `HullFromPoints` (Task 1), existing `BakeTrunkCylinder`, `TrunkRadiusPercentile`, `TrunkRadiusFloor`.
- Produces: `static PhysicsShape BakeTrunkHull(GltfMesh mesh)` — `ConvexHullShape` of the lower trunk band tracking the lean; falls back to `BakeTrunkCylinder` on degenerate input.

- [ ] **Step 1: Write the failing tests**

Add to `PropCollisionBakeTests.cs`:

```csharp
[Fact]
public void TrunkHull_TracksLean_AndExcludesCanopy()
{
    GltfMesh tree = TestMeshes.LeaningTree();
    Assert.True(PropCollisionBake.IsTree(tree), "leaning-tree fixture must classify as a tree");
    PhysicsShape shape = PropCollisionBake.BakeTrunkHull(tree);
    var hull = Assert.IsType<ConvexHullShape>(shape);

    // Canopy (y >= 3.5) is excluded: no hull point above the trunk-band cap (~2.6 m with a 0.1 m margin).
    foreach (Vector3 p in hull.Points)
        Assert.True(p.Y <= 2.6f, $"hull point above the trunk cap (canopy not excluded): y={p.Y}");

    // The trunk leans toward +X with height: the highest hull point sits clearly off the vertical axis,
    // i.e. the hull follows the leaning trunk rather than a base-pinned vertical cylinder.
    Vector3 highest = hull.Points[0];
    foreach (Vector3 p in hull.Points) if (p.Y > highest.Y) highest = p;
    Assert.True(highest.X > 0.3f, $"hull does not track the lean; highest point X={highest.X}");
}

[Fact]
public void TrunkHull_ExcludesWideLowBranches()
{
    GltfMesh tree = TestMeshes.StraightTrunkWithLowBranches();
    PhysicsShape shape = PropCollisionBake.BakeTrunkHull(tree);
    var hull = Assert.IsType<ConvexHullShape>(shape);

    // Low branches spread to |X|~1.5 at y~1; the radial-core filter drops them, so the hull stays near the
    // ~0.2 m trunk core (well under 0.6 m) for any point at branch height.
    foreach (Vector3 p in hull.Points)
        if (p.Y is > 0.8f and < 1.2f)
            Assert.True(MathF.Abs(p.X) < 0.6f && MathF.Abs(p.Z) < 0.6f,
                $"wide low branch survived the core filter: {p}");
}

[Fact]
public void TrunkHull_IsSolid_PushesACapsuleOut()
{
    GltfMesh tree = TestMeshes.LeaningTree();
    PhysicsShape shape = PropCollisionBake.BakeTrunkHull(tree);
    using KhaozEngine.Physics.IPhysicsWorld world = new KhaozEngine.Physics.Bepu.BepuPhysicsWorld();
    world.AddStatic(shape, KhaozEngine.Physics.Pose.At(Vector3.Zero));
    world.Step(1f / 60f);

    // A capsule overlapping the trunk near the base is pushed out (non-trapping solid).
    var capsule = new KhaozEngine.Physics.CapsuleShape(0.4f, 1.0f);
    bool overlap = world.ComputePenetration(capsule, KhaozEngine.Physics.Pose.At(new Vector3(0.1f, 0.6f, 0f)), out Vector3 mtv);
    Assert.True(overlap, "capsule overlapping the trunk hull should report penetration");
    Assert.True(mtv.Length() > 0f, "push-out MTV should be non-zero");
}

[Fact]
public void TrunkHull_DegenerateTrunk_FallsBackToCylinder()
{
    GltfMesh tree = TestMeshes.CollinearTrunkTree();
    PhysicsShape shape = PropCollisionBake.BakeTrunkHull(tree);
    Assert.IsType<CylinderShape>(shape);
}
```

Add the fixtures to `TestMeshes`:

```csharp
/// <summary>A tall tree whose trunk centreline LEANS toward +X with height (centre X = 0.2*y), a thin
/// trunk core (ring radius ~0.18) from y=0..3, and a wide canopy (|x|,|z| up to 2) from y=3.5..5. Tall
/// (height 5 > 2.5) with a canopy spread > 1.6x the base, so IsWalkableSolid is false (a tree).</summary>
public static GltfMesh LeaningTree()
{
    var verts = new List<ModelVertex>();
    var idx = new List<uint>();
    // Trunk rings: centre leans +X with height; small radius so it is clearly a thin trunk.
    for (float y = 0f; y <= 3f + 1e-3f; y += 0.5f)
    {
        float cx = 0.2f * y;            // lean
        const float r = 0.18f;
        AddRing(verts, idx, new Vector3(cx, y, 0f), r);
    }
    // Canopy: wide spread well above the trunk band.
    for (float y = 3.5f; y <= 5f + 1e-3f; y += 0.5f)
        AddRing(verts, idx, new Vector3(0.6f, y, 0f), 2.0f);
    return new GltfMesh(verts.ToArray(), idx.ToArray());
}

/// <summary>A straight (un-leaning) tall tree with a thin trunk plus a few WIDE low branch verts at y~1
/// (|x| up to 1.5), and a wide canopy above. Used to prove the radial-core filter drops the branches.</summary>
public static GltfMesh StraightTrunkWithLowBranches()
{
    var verts = new List<ModelVertex>();
    var idx = new List<uint>();
    for (float y = 0f; y <= 3f + 1e-3f; y += 0.5f) AddRing(verts, idx, new Vector3(0f, y, 0f), 0.18f);
    // Wide low branches at y~1.
    foreach (float ang in new[] { 0f, 1.57f, 3.14f, 4.71f })
        AddRing(verts, idx, new Vector3(1.5f * MathF.Cos(ang), 1f, 1.5f * MathF.Sin(ang)), 0.1f);
    for (float y = 3.5f; y <= 5f + 1e-3f; y += 0.5f) AddRing(verts, idx, new Vector3(0f, y, 0f), 2.0f);
    return new GltfMesh(verts.ToArray(), idx.ToArray());
}

/// <summary>A degenerate tree: the trunk band is a single COLLINEAR column of verts on the Y axis (no
/// volume), with a wide canopy above so it still classifies as a tree. The trunk hull is degenerate so
/// BakeTrunkHull must fall back to a cylinder.</summary>
public static GltfMesh CollinearTrunkTree()
{
    var verts = new List<ModelVertex>();
    var idx = new List<uint>();
    for (float y = 0f; y <= 3f + 1e-3f; y += 0.5f) { uint b = (uint)verts.Count; verts.Add(V(0, y, 0)); idx.Add(b); idx.Add(b); idx.Add(b); }
    for (float y = 3.5f; y <= 5f + 1e-3f; y += 0.5f) AddRing(verts, idx, new Vector3(0f, y, 0f), 2.0f);
    return new GltfMesh(verts.ToArray(), idx.ToArray());
}

/// <summary>Add an 8-vertex ring (octagon) of radius r centred at c, as 8 degenerate triangles (verts only;
/// the bake reads positions, not winding).</summary>
static void AddRing(List<ModelVertex> verts, List<uint> idx, Vector3 c, float r)
{
    for (int k = 0; k < 8; k++)
    {
        float a = k * MathF.PI / 4f;
        uint b = (uint)verts.Count;
        verts.Add(V(c.X + r * MathF.Cos(a), c.Y, c.Z + r * MathF.Sin(a)));
        idx.Add(b); idx.Add(b); idx.Add(b);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TrunkHull"`
Expected: FAIL — `BakeTrunkHull` does not exist yet (compile error / missing method).

- [ ] **Step 3: Implement `BakeTrunkHull` + constants**

Add the constants near the other trunk constants in `PropCollisionBake`:

```csharp
/// <summary>Hard cap (metres) on the trunk-hull band height. The hull uses the player-reachable lower trunk;
/// the canopy is excluded above min(this, <see cref="FoliageBaseFraction"/> * height).</summary>
public const float TrunkHullMaxMeters = 3.0f;

/// <summary>Fraction of the tree height treated as the trunk band (foliage starts above it). The trunk-hull
/// cap is min(<see cref="TrunkHullMaxMeters"/>, this * height).</summary>
public const float FoliageBaseFraction = 0.5f;

/// <summary>Keep trunk-band verts within this multiple of the percentile core radius of the running centreline;
/// drops spreading low branches while keeping the trunk core.</summary>
public const float TrunkCoreRadiusFactor = 1.6f;

/// <summary>Height (metres) of each centreline bin used to track a leaning trunk's drift with height.</summary>
public const float TrunkCentrelineBinMeters = 0.25f;
```

Change the `Bake` dispatch line for trees:

```csharp
if (IsTree(normalizedMesh))     return BakeTrunkHull(normalizedMesh);
```

Add the method (place it next to `BakeTrunkCylinder`):

```csharp
/// <summary>Bake a convex hull of the player-reachable lower trunk that FOLLOWS a leaning trunk (real
/// Quaternius trees lean 0.3-0.9 m over their height, so a base-pinned vertical cylinder is not where the
/// trunk is by mid-height). Keep verts below the trunk-band cap (min(<see cref="TrunkHullMaxMeters"/>,
/// <see cref="FoliageBaseFraction"/> * height)), build a per-height-bin centreline so the kept set tracks the
/// lean, and drop verts beyond <see cref="TrunkCoreRadiusFactor"/> * the percentile core radius of that
/// centreline (rejects spreading low branches). Degenerate trunk (&lt; 4 surviving verts or coplanar) falls
/// back to <see cref="BakeTrunkCylinder"/>. A trunk is roughly convex, so the hull loses no useful detail and
/// can never trap the capsule.</summary>
static PhysicsShape BakeTrunkHull(GltfMesh mesh)
{
    float minY = float.MaxValue, maxY = float.MinValue;
    foreach (ModelVertex v in mesh.Vertices)
    {
        if (v.Position.Y < minY) minY = v.Position.Y;
        if (v.Position.Y > maxY) maxY = v.Position.Y;
    }
    float height = maxY - minY;
    float cap = minY + MathF.Min(TrunkHullMaxMeters, FoliageBaseFraction * height);

    // Trunk-band verts (drop the canopy).
    var band = new List<Vector3>();
    foreach (ModelVertex v in mesh.Vertices)
        if (v.Position.Y <= cap) band.Add(v.Position);
    if (band.Count < 4) return BakeTrunkCylinder(mesh);

    // Running centreline: bin by height, centroid XZ per bin (tracks the lean). Vertical bin index off minY.
    var binSum = new Dictionary<int, (Vector3 sum, int n)>();
    foreach (Vector3 p in band)
    {
        int bin = (int)MathF.Floor((p.Y - minY) / TrunkCentrelineBinMeters);
        if (binSum.TryGetValue(bin, out var acc)) binSum[bin] = (acc.sum + p, acc.n + 1);
        else binSum[bin] = (p, 1);
    }
    Vector3 Centreline(Vector3 p)
    {
        int bin = (int)MathF.Floor((p.Y - minY) / TrunkCentrelineBinMeters);
        var acc = binSum[bin];
        Vector3 c = acc.sum / acc.n;
        return new Vector3(c.X, p.Y, c.Z);   // XZ centroid at this height
    }

    // Percentile core radius (XZ distance from each vert to its bin centreline).
    var radii = new List<float>(band.Count);
    foreach (Vector3 p in band)
    {
        Vector3 c = Centreline(p);
        radii.Add(MathF.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Z - c.Z) * (p.Z - c.Z)));
    }
    radii.Sort();
    float coreRadius = MathF.Max(TrunkRadiusFloor, radii[(int)(TrunkRadiusPercentile * (radii.Count - 1))]);
    float keepRadius = TrunkCoreRadiusFactor * coreRadius;

    var kept = new List<Vector3>(band.Count);
    foreach (Vector3 p in band)
    {
        Vector3 c = Centreline(p);
        float dx = p.X - c.X, dz = p.Z - c.Z;
        if (dx * dx + dz * dz <= keepRadius * keepRadius) kept.Add(p);
    }
    if (kept.Count < 4 || IsCoplanar(kept)) return BakeTrunkCylinder(mesh);
    return HullFromPoints(kept);
}

/// <summary>True when all points lie (within a tolerance) on a single plane, so a convex hull would be
/// degenerate. Picks the plane from the first non-collinear triple and checks every point's distance to it.</summary>
static bool IsCoplanar(IReadOnlyList<Vector3> pts)
{
    const float Eps = 1e-4f;
    Vector3 p0 = pts[0];
    // Find an edge a = p_i - p0 with length > eps.
    Vector3 a = Vector3.Zero;
    foreach (Vector3 p in pts) { Vector3 d = p - p0; if (d.LengthSquared() > Eps) { a = d; break; } }
    if (a.LengthSquared() <= Eps) return true; // all coincident
    // Find a normal n = a x (p_j - p0) that is non-degenerate (a non-collinear point).
    Vector3 n = Vector3.Zero;
    foreach (Vector3 p in pts) { Vector3 c = Vector3.Cross(a, p - p0); if (c.LengthSquared() > Eps) { n = Vector3.Normalize(c); break; } }
    if (n.LengthSquared() <= Eps) return true; // all collinear
    foreach (Vector3 p in pts) if (MathF.Abs(Vector3.Dot(p - p0, n)) > 1e-3f) return false;
    return true;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~TrunkHull"`
Expected: PASS (all four). If a threshold is off (lean/branch fixtures are synthetic), adjust the fixture magnitudes or `TrunkCoreRadiusFactor`, not the invariant being asserted.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/PropCollisionBake.cs KhaozEngine.Tests/Physics/PropCollisionBakeTests.cs
git commit -m "render3d: BakeTrunkHull tracks the leaning trunk and excludes canopy/branches"
```

---

### Task 3: `PropBakePlan.For` (testable tool decision)

**Files:**
- Create: `KhaozEngine.Render3D/Models/PropBakePlan.cs`
- Test: `KhaozEngine.Tests/Physics/PropBakePlanTests.cs`

**Interfaces:**
- Consumes: `PropCollisionBake.Bake`, `PropSurfaceBake.IsWalkableSolid`, `PropSurfaceBake.Bake`, `KhaozEngine.Collision.PropSurface`.
- Produces: `readonly record struct PropBakePlan(PhysicsShape Coll, PropSurface? Surface)` with `static PropBakePlan For(GltfMesh mesh)`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PropBakePlanTests
{
    [Fact]
    public void Tree_PlansCollOnly_NoSurface()
    {
        GltfMesh tree = TestMeshes.LeaningTree();
        PropBakePlan plan = PropBakePlan.For(tree);
        Assert.NotNull(plan.Coll);
        Assert.IsType<ConvexHullShape>(plan.Coll);   // trunk hull
        Assert.Null(plan.Surface);                   // thin blocker: no walkable top
    }

    [Fact]
    public void WalkableSolid_PlansCollAndSurface()
    {
        GltfMesh rock = TestMeshes.UnitIcosphere();
        PropBakePlan plan = PropBakePlan.For(rock);
        Assert.NotNull(plan.Coll);
        Assert.NotNull(plan.Surface);                // walkable solid: surface baked
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropBakePlan"`
Expected: FAIL — `PropBakePlan` does not exist.

- [ ] **Step 3: Implement `PropBakePlan`**

```csharp
using KhaozEngine.Collision;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    /// <summary>What <c>ke-propbake</c> emits for one prop: a collision shape (always) and an optional walkable
    /// top-surface heightmap (only for walkable-solid props). Pure decision (no file IO) so the tool stays thin
    /// and the tree-gets-coll-not-surf rule is unit-testable without a glTF fixture.</summary>
    public readonly record struct PropBakePlan(PhysicsShape Coll, PropSurface? Surface)
    {
        /// <summary>Plan the bakes for a <see cref="PropLoader"/>-normalized mesh: every prop gets a
        /// <see cref="PropCollisionBake"/> collision shape; only an <see cref="PropSurfaceBake.IsWalkableSolid"/>
        /// prop also gets a <see cref="PropSurfaceBake.Bake"/> surface (a tree has no walkable top).</summary>
        public static PropBakePlan For(GltfMesh mesh) => new(
            PropCollisionBake.Bake(mesh),
            PropSurfaceBake.IsWalkableSolid(mesh) ? PropSurfaceBake.Bake(mesh) : null);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropBakePlan"`
Expected: PASS (both).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/PropBakePlan.cs KhaozEngine.Tests/Physics/PropBakePlanTests.cs
git commit -m "render3d: PropBakePlan.For single-sources the ke-propbake per-prop decision"
```

---

### Task 4: `ke-propbake` always emits a `.coll`

**Files:**
- Modify: `KhaozEngine.PropSurface.Tool/Program.cs` (the per-prop loop, lines ~37-72)

**Interfaces:**
- Consumes: `PropBakePlan.For` (Task 3), `PropCollisionBake.Write`, `PropSurface.Write`, shape types for the kind label.

- [ ] **Step 1: Rewrite the per-prop loop**

Replace the loop body and the trailing summary (lines ~37-72) with:

```csharp
int baked = 0, blockers = 0;
foreach (AssetEntry entry in manifest.Props)
{
    GltfMesh mesh;
    try { mesh = PropLoader.LoadProp(entry); }
    catch (Exception ex) { Console.Error.WriteLine($"  ! {entry.Id}: {ex.Message}"); continue; }

    PropBakePlan plan = PropBakePlan.For(mesh);
    JsonObject node = props.OfType<JsonObject>().First(p => (string?)p["id"] == entry.Id);

    // Always bake the collision shape (.coll) and stamp collisionShape.
    string collName = entry.Id + ".coll";
    using (FileStream cfs = File.Create(Path.Combine(dir, collName))) PropCollisionBake.Write(plan.Coll, cfs);
    node["collisionShape"] = collName;
    string collKind = plan.Coll switch
    {
        TriangleMeshShape => "triangle-mesh",
        CylinderShape     => "cylinder",
        ConvexHullShape   => "convex-hull",
        _                 => "shape",
    };

    // Only walkable solids also get a top-surface heightmap (.surf).
    if (plan.Surface is { } surface)
    {
        string surfName = entry.Id + ".surf";
        using (FileStream fs = File.Create(Path.Combine(dir, surfName))) surface.Write(fs);
        node["surface"] = true;
        node["heightmap"] = surfName;
        Console.WriteLine($"  + {entry.Id}: baked {surfName} ({surface.Width}x{surface.Height}, top {surface.MaxHeight:0.00} m) + {collName} ({collKind})");
        baked++;
    }
    else
    {
        Console.WriteLine($"  + {entry.Id}: baked {collName} ({collKind}) [thin blocker, no surface]");
        blockers++;
    }
}

File.WriteAllText(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"ke-propbake: {baked} surface(s) + {baked + blockers} collision shape(s) baked, {blockers} blocker(s); manifest stamped.");
return 0;
```

Update the header comment block at the top of `Program.cs` to state it now bakes a `.coll` for every prop and a `.surf` only for walkable solids.

- [ ] **Step 2: Build the tool to verify it compiles**

Run: `dotnet build KhaozEngine.PropSurface.Tool/KhaozEngine.PropSurface.Tool.csproj -c Debug`
Expected: Build succeeded. (The bake decision itself is covered by `PropBakePlanTests`; this confirms the tool wiring compiles.)

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.PropSurface.Tool/Program.cs
git commit -m "propbake: always emit a .coll per prop (trees get a trunk-hull collider)"
```

---

## Part A — swept collide-and-slide resolver

### Task 5: Swept collide-and-slide (anti-tunnel, no step-up yet)

**Files:**
- Modify: `KhaozEngine.Locomotion/CharacterMovement.cs` (replace the candidate-position/depenetrate block, lines ~89-118; add `SweptMove`/`SlideSubstep`)
- Test: `KhaozEngine.Tests/Physics/SweptCollisionTests.cs` (new), and update `ControllerOnPhysicsTests.cs` thresholds.

**Interfaces:**
- Consumes: `IPhysicsWorld.SweepCapsule`, `SweepHit`, `CapsuleFor`, `Pose.At`.
- Produces: internal `SweptMove(IPhysicsWorld, CapsuleShape, Vector3 start, Vector3 target, in MoveTuning, bool grounded)` returning the resolved position (no step-up in this task).

- [ ] **Step 1: Write the failing anti-tunnel + slide tests**

Create `KhaozEngine.Tests/Physics/SweptCollisionTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class SweptCollisionTests
{
    private static readonly MoveTuning Tuning = new(
        WalkSpeed: 3f, RunSpeed: 6f, CapsuleHalfHeight: 0.9f, MaxSlopeRadians: 0.9f);
    private static float Flat(float x, float z) => 0f;

    // One-sided thin quad wall in the XY plane at z=2 (front face normal -Z, toward the approaching capsule),
    // spanning x[-10,10] (wide enough to block for a full sliding test), y[0,3]. A single quad => two triangles,
    // ~0.0 m thick: the classic tunnel trap.
    private static TriangleMeshShape ThinWallAtZ2()
    {
        var v = new[]
        {
            new Vector3(-10f, 0f, 2f), new Vector3(10f, 0f, 2f),
            new Vector3(10f, 3f, 2f), new Vector3(-10f, 3f, 2f),
        };
        // Wound so the front face normal points -Z (toward the capsule coming from z<2).
        var idx = new[] { 0, 2, 1, 0, 3, 2 };
        return new TriangleMeshShape(v, idx);
    }

    [Fact]
    public void FastMove_DoesNotTunnelThroughThinOneSidedWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2());
        world.Step(1f / 60f);

        // Drive straight toward +Z (Move.Y=-1 at yaw=0 => +Z). A LARGE dt (0.1 s) at run speed makes one tick's
        // displacement ~0.6 m, well over the 0.4 m capsule radius - the regime where the old teleport-then-
        // depenetrate resolver tunnels through the one-sided quad (low-frame-rate clients hit exactly this).
        const float BigDt = 0.1f;
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var run = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 60; i++)
            state = CharacterMovement.Step(state, run, BigDt, Flat, Tuning, groundNormal: null, world: world);

        // The capsule centre must stay on the near side of the wall (z < 2 - radius + skin), never past it.
        Assert.True(state.Position.Z < 1.65f, $"tunneled through the thin wall, z={state.Position.Z}");
    }

    [Fact]
    public void Diagonal_SlidesAlongWall_NoPenetration()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2());
        world.Step(1f / 60f);

        // Move diagonally into the wall (toward +Z and +X): expect blocked in Z, sliding in +X.
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 120; i++)
            state = CharacterMovement.Step(state, diag, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        Assert.True(state.Position.Z < 1.65f, $"penetrated/over-advanced into wall, z={state.Position.Z}");
        Assert.True(state.Position.X > 1.0f, $"did not slide along the wall, x={state.Position.X}");
    }

    [Fact]
    public void InnerCorner_StopsWithoutPenetration()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(ThinWallAtZ2());                                   // wall at z=2 (faces -Z)
        // Side wall at x=2 facing -X: quad in the ZY plane.
        var sv = new[]
        {
            new Vector3(2f, 0f, -3f), new Vector3(2f, 0f, 3f),
            new Vector3(2f, 3f, 3f), new Vector3(2f, 3f, -3f),
        };
        world.AddStatic(new TriangleMeshShape(sv, new[] { 0, 1, 2, 0, 2, 3 }));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, -1f)), run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < 240; i++)
            state = CharacterMovement.Step(state, diag, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

        // Wedged into the corner: stopped short of both faces (centre within radius+skin of each).
        Assert.True(state.Position.Z < 1.65f && state.Position.X < 1.65f,
            $"corner not respected: pos={state.Position}");
        // And stable (no NaN / fling).
        Assert.True(float.IsFinite(state.Position.X) && float.IsFinite(state.Position.Z));
    }

    [Fact]
    public void FastPath_IsDeterministic_AcrossTwoWorlds()
    {
        static MoveState RunOnce()
        {
            IPhysicsWorld world = new BepuPhysicsWorld();
            world.AddStatic(ThinWallAtZ2());
            world.Step(1f / 60f);
            var s = new MoveState { Position = new Vector3(0.13f, 0.9f, 0f), Grounded = true };
            var cmd = new MoveCommand(new Vector2(0.3f, -1f), run: true, cameraYaw: 0.2f, jump: false);
            for (int i = 0; i < 200; i++)
                s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);
            world.Dispose();
            return s;
        }

        MoveState a = RunOnce(), b = RunOnce();
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.X), BitConverter.SingleToInt32Bits(b.Position.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.Y), BitConverter.SingleToInt32Bits(b.Position.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.Z), BitConverter.SingleToInt32Bits(b.Position.Z));
    }
}
```

- [ ] **Step 2: Run to verify the tunnel test fails on the current resolver**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SweptCollisionTests"`
Expected: `FastMove_DoesNotTunnelThroughThinOneSidedWall` FAILS (the teleport-then-depenetrate resolver tunnels at run speed through the one-sided quad). Others may fail too (slide/corner depend on the swept resolver).

- [ ] **Step 3: Implement the swept collide-and-slide (no step-up)**

In `CharacterMovement.cs`, replace the candidate-position + depenetration block (the `Vector3 pos = new(dx, desiredY, dz);` through the closing of the `if (world is not null) { ... }` that ends with the post-depenetration `clampXz` re-clamp — current lines ~89-118) with:

```csharp
// 3. Candidate position: SWEEP from the current pose to the target (collide-and-slide), then settle.
// The swept move can never cross a face (substepped to a fraction of the capsule radius), so the capsule
// never begins a tick inside a wall - enforcing the depenetration contract for real. A one-sided building
// mesh is now both rich (every triangle blocks) and trap-proof (no entry => no inner-face suck-through).
Vector3 start = s.Position;
Vector3 target = new(dx, desiredY, dz);
Vector3 pos;
bool propGrounded = false;
if (world is null)
{
    pos = target;
}
else
{
    pos = SweptMove(world, capsule, start, target, t, s.Grounded);

    // Settle pass: residual-overlap depenetration (rarely fires now; the swept move starts known-outside).
    const int ResolveIterations = 6;
    const float ResolveSlop = 0.01f;
    const float MaxCorrection = 0.5f;
    for (int i = 0; i < ResolveIterations; i++)
    {
        if (!world.ComputePenetration(capsule, Pose.At(pos), out Vector3 mtv)) break;
        float len = mtv.Length();
        if (len <= 1e-6f) break;
        if (mtv.Y > 0.5f * len) propGrounded = true;
        if (len <= ResolveSlop) break;
        float push = MathF.Min(len - ResolveSlop, MaxCorrection);
        pos += mtv / len * push;
    }
    if (clampXz is not null) { Vector2 c = clampXz(pos.X, pos.Z); pos.X = c.X; pos.Z = c.Y; }
}
```

Add the helpers in the private region (next to `CapsuleFor`/`DesiredHorizontal`), including the tuning constants:

```csharp
// Swept collide-and-slide tuning. SubstepFraction keeps each swept query <= a fraction of the capsule radius
// so a fast move (jump/run/terminal fall) can never advance past a thin wall in one sweep (anti-tunnel).
private const float SubstepFraction = 0.5f;
private const int   SlideIterations = 4;
private const float SkinWidth       = 0.01f;

/// <summary>Move the capsule from <paramref name="start"/> toward <paramref name="target"/> by a substepped
/// swept collide-and-slide over <see cref="IPhysicsWorld.SweepCapsule"/>. The displacement is split into
/// substeps no longer than <see cref="SubstepFraction"/> * the capsule radius, so even a near-terminal fall or
/// fast jump never crosses a face. Deterministic (Bepu Sweep is deterministic single-threaded; the substep
/// count is a deterministic length).</summary>
private static Vector3 SweptMove(IPhysicsWorld world, CapsuleShape capsule, Vector3 start, Vector3 target,
    in MoveTuning t, bool grounded)
{
    Vector3 full = target - start;
    float fullLen = full.Length();
    if (fullLen <= 1e-6f) return target;

    float maxStep = MathF.Max(0.01f, t.CapsuleRadius * SubstepFraction);
    int substeps = (int)MathF.Ceiling(fullLen / maxStep);
    if (substeps < 1) substeps = 1;
    Vector3 stepDelta = full / substeps;

    Vector3 pos = start;
    for (int i = 0; i < substeps; i++)
        pos = SlideSubstep(world, capsule, pos, stepDelta, t, grounded);
    return pos;
}

/// <summary>Collide-and-slide one substep's displacement: sweep, advance to the contact minus a skin, project
/// the remainder onto the contact plane, iterate (resolves inner corners). No step-up in this overload.</summary>
private static Vector3 SlideSubstep(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector3 delta,
    in MoveTuning t, bool grounded)
{
    for (int iter = 0; iter < SlideIterations; iter++)
    {
        float dist = delta.Length();
        if (dist <= 1e-6f) break;
        Vector3 dir = delta / dist;
        if (!world.SweepCapsule(capsule, Pose.At(pos), dir, dist, out SweepHit hit))
        {
            pos += delta;     // clear path for the remainder of this substep
            break;
        }
        pos += dir * MathF.Max(0f, hit.Distance - SkinWidth);
        Vector3 remaining = delta - dir * hit.Distance;
        Vector3 n = hit.Normal;
        if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
        else break;           // degenerate contact (deep/zero-distance): let the settle pass handle it
        delta = remaining - Vector3.Dot(remaining, n) * n;   // slide along the contact plane
    }
    return pos;
}
```

Note: `propGrounded` is still declared/used by the floor block below (step 4) exactly as before; only its assignment source moved into the settle pass. Leave steps 4 and 5 of `Step` unchanged in this task.

- [ ] **Step 4: Run the swept tests + the full controller suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SweptCollisionTests|FullyQualifiedName~ControllerOnPhysicsTests"`
Expected: All `SweptCollisionTests` PASS. In `ControllerOnPhysicsTests`, `Capsule_SlidesAlongObliqueWall_CorrectlyAdvances` likely FAILS (its thresholds were measured from the old resolver). The dome/doorway/wall/null tests should PASS.

- [ ] **Step 5: Re-derive the oblique-slide thresholds (behavior change)**

The swap to swept collide-and-slide is a deliberate behavior change; re-measure the settled position from the new run and update only the numeric thresholds in `Capsule_SlidesAlongObliqueWall_CorrectlyAdvances`, preserving the intent (meaningful slide in -X, advance in +Z, no penetration). Read the actual settled `x,z` from the assertion failure message, then set the thresholds just inside them (e.g. `Z > <measured_z - 0.01>`, `X > <measured_x - 0.01>`) and update the explanatory comment to say the numbers are from the swept resolver. Do NOT loosen the no-penetration intent.

If any dome/mount test shifted slightly, apply the same intent-preserving threshold re-derivation (keep the no-penetration and grounded-mount assertions; only adjust measured constants).

- [ ] **Step 6: Run the full physics test suite green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Physics"`
Expected: PASS (all physics/controller tests).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Locomotion/CharacterMovement.cs KhaozEngine.Tests/Physics/SweptCollisionTests.cs KhaozEngine.Tests/Physics/ControllerOnPhysicsTests.cs
git commit -m "locomotion: swept collide-and-slide so one-sided meshes are trap-proof (anti-tunnel)"
```

---

### Task 6: Step-up probe (wire `MoveTuning.StepHeight`, stairs walkable)

**Files:**
- Modify: `KhaozEngine.Locomotion/CharacterMovement.cs` (add `TryStepUp`, thread a stepped flag through `SweptMove`/`SlideSubstep`, integrate with the floor block)
- Test: `KhaozEngine.Tests/Physics/SweptCollisionTests.cs` (add the stairs test)

**Interfaces:**
- Consumes: `IPhysicsWorld.SweepCapsule`, `MoveTuning.StepHeight`, `MoveTuning.MaxSlopeRadians`.
- Produces: internal `TryStepUp(...)`; `SweptMove` gains `out bool steppedUp, out float steppedFloorY`.

- [ ] **Step 1: Write the failing stairs test**

Add to `SweptCollisionTests.cs`:

```csharp
[Fact]
public void Stairs_WithRisersUnderStepHeight_AreWalkable()
{
    using IPhysicsWorld world = new BepuPhysicsWorld();
    // Three solid box steps, each riser 0.25 m (< StepHeight 0.4). Step s spans z[2+0.4s, 2+0.4s+0.4] and
    // y[0, 0.25*(s+1)]. One-sided-mesh richness/trap behavior is covered by the sibling thin-wall / closed-
    // shell tests; this fixture isolates the step-up probe (shape-agnostic: it sweeps).
    for (int s = 0; s < 3; s++)
    {
        float topY = 0.25f * (s + 1);
        float zCentre = 2f + 0.4f * s + 0.2f;
        world.AddStatic(new BoxShape(new Vector3(3f, topY * 0.5f, 0.2f)),
            Pose.At(new Vector3(0f, topY * 0.5f, zCentre)));
    }
    world.Step(1f / 60f);

    var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
    var fwd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);   // toward +Z
    for (int i = 0; i < 300; i++)
        state = CharacterMovement.Step(state, fwd, 1f / 60f, Flat, Tuning, groundNormal: null, world: world);

    // Climbed at least the first step: capsule centre rose from 0.9 (terrain rest) onto a tread
    // (>= 0.25 + halfHeight), and advanced onto the stairs.
    Assert.True(state.Position.Y > 0.9f + 0.2f, $"did not climb the stairs, y={state.Position.Y}");
    Assert.True(state.Position.Z > 2.2f, $"did not advance onto the stairs, z={state.Position.Z}");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Stairs_WithRisersUnderStepHeight_AreWalkable"`
Expected: FAIL — the riser face blocks the swept move; the capsule stops at the first riser (Y stays ~0.9).

- [ ] **Step 3: Add `TryStepUp` and thread the stepped result**

Change `SweptMove`/`SlideSubstep` signatures to surface a step-up, and add `TryStepUp`:

```csharp
// Contact counts as a wall/riser (step-up candidate) rather than a floor/ceiling when |normal.Y| is small.
private const float StepUpNormalY = 0.5f;

private static Vector3 SweptMove(IPhysicsWorld world, CapsuleShape capsule, Vector3 start, Vector3 target,
    in MoveTuning t, bool grounded, out bool steppedUp, out float steppedFloorY)
{
    steppedUp = false; steppedFloorY = 0f;
    Vector3 full = target - start;
    float fullLen = full.Length();
    if (fullLen <= 1e-6f) return target;

    float maxStep = MathF.Max(0.01f, t.CapsuleRadius * SubstepFraction);
    int substeps = (int)MathF.Ceiling(fullLen / maxStep);
    if (substeps < 1) substeps = 1;
    Vector3 stepDelta = full / substeps;

    Vector3 pos = start;
    for (int i = 0; i < substeps; i++)
    {
        pos = SlideSubstep(world, capsule, pos, stepDelta, t, grounded, out bool stepped, out float floorY);
        if (stepped) { steppedUp = true; steppedFloorY = floorY; }
    }
    return pos;
}

private static Vector3 SlideSubstep(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector3 delta,
    in MoveTuning t, bool grounded, out bool steppedUp, out float steppedFloorY)
{
    steppedUp = false; steppedFloorY = 0f;
    for (int iter = 0; iter < SlideIterations; iter++)
    {
        float dist = delta.Length();
        if (dist <= 1e-6f) break;
        Vector3 dir = delta / dist;
        if (!world.SweepCapsule(capsule, Pose.At(pos), dir, dist, out SweepHit hit))
        {
            pos += delta;
            break;
        }
        pos += dir * MathF.Max(0f, hit.Distance - SkinWidth);
        Vector3 remaining = delta - dir * hit.Distance;
        Vector3 n = hit.Normal;
        if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
        else break;

        // Step-up: only while grounded, only over a near-vertical contact (a riser/wall), only on the
        // horizontal remainder. Climbs a stair tread; a real wall has no ledge within StepHeight so it slides.
        if (grounded && MathF.Abs(n.Y) < StepUpNormalY &&
            TryStepUp(world, capsule, pos, remaining, t, out Vector3 stepped))
        {
            steppedUp = true; steppedFloorY = stepped.Y;
            pos = stepped;
            break;
        }

        delta = remaining - Vector3.Dot(remaining, n) * n;
    }
    return pos;
}

/// <summary>Classic up/forward/down step probe over the horizontal remainder: sweep up by
/// <see cref="MoveTuning.StepHeight"/> (headroom), sweep forward, sweep down; accept only if it lands on a
/// walkable-slope ledge strictly higher than the start (a stair tread/curb). A vertical wall has no such ledge
/// within StepHeight, so this returns false and the caller slides. Returns the stepped capsule centre.</summary>
private static bool TryStepUp(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector3 remaining,
    in MoveTuning t, out Vector3 stepped)
{
    stepped = pos;
    Vector3 horiz = new(remaining.X, 0f, remaining.Z);
    float horizLen = horiz.Length();
    if (horizLen <= 1e-6f) return false;
    Vector3 horizDir = horiz / horizLen;
    float step = t.StepHeight;

    // 1. Up by StepHeight (stop short of any ceiling).
    Vector3 up = pos;
    if (world.SweepCapsule(capsule, Pose.At(pos), Vector3.UnitY, step, out SweepHit upHit))
        up.Y += MathF.Max(0f, upHit.Distance - SkinWidth);
    else
        up.Y += step;

    // 2. Forward along the horizontal remainder from the raised pose.
    Vector3 fwd = up;
    if (world.SweepCapsule(capsule, Pose.At(up), horizDir, horizLen, out SweepHit fwdHit))
        fwd += horizDir * MathF.Max(0f, fwdHit.Distance - SkinWidth);
    else
        fwd += horizDir * horizLen;
    // No forward progress above the obstacle => it is a wall, not a step.
    float advanced = Vector3.Distance(new Vector3(fwd.X, 0f, fwd.Z), new Vector3(pos.X, 0f, pos.Z));
    if (advanced <= 1e-4f) return false;

    // 3. Down by StepHeight to settle onto the ledge; must be walkable slope and strictly higher than pos.
    if (!world.SweepCapsule(capsule, Pose.At(fwd), -Vector3.UnitY, step + SkinWidth, out SweepHit downHit))
        return false;
    if (downHit.Normal.Y < MathF.Cos(t.MaxSlopeRadians)) return false;   // too steep to stand on
    Vector3 landed = fwd; landed.Y -= MathF.Max(0f, downHit.Distance - SkinWidth);
    if (landed.Y <= pos.Y + 1e-4f) return false;                        // did not actually rise
    stepped = landed;
    return true;
}
```

Update the call in `Step` to capture the step result and feed the floor block:

```csharp
    pos = SweptMove(world, capsule, start, target, t, s.Grounded, out bool steppedUp, out float steppedFloorY);
```

Then in step 4 (the floor block), after `float terrainGroundY = groundHeight(pos.X, pos.Z) + halfH; float groundY = terrainGroundY;`, add the stepped ledge as a floor and ground source, and include it in the `overProp` gate so the support sweep keeps tracking it:

```csharp
    if (steppedUp)
    {
        if (steppedFloorY > groundY) groundY = steppedFloorY;
        propGrounded = true;   // standing on the stepped ledge
    }
```

And change the existing `bool overProp = !s.Grounded || s.Position.Y > terrainGroundY + OnPropSkin;` to:

```csharp
    bool overProp = !s.Grounded || s.Position.Y > terrainGroundY + OnPropSkin || steppedUp;
```

This prevents the grounded-floor logic from snapping the stepped capsule back down to terrain, and keeps it grounded on the tread. Leave the rest of steps 4-5 unchanged.

- [ ] **Step 4: Run the stairs test + full physics suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Physics"`
Expected: PASS — stairs climb, and all prior swept/controller tests stay green. If the stairs test climbs only one step, that is acceptable per the assertion (Y rose > 0.2 m, advanced onto the stairs); the test asserts walkability, not reaching the top.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Locomotion/CharacterMovement.cs KhaozEngine.Tests/Physics/SweptCollisionTests.cs
git commit -m "locomotion: step-up probe wires StepHeight so stairs/curbs are walkable"
```

---

### Task 7: Release 8.3.0 — version, changelog, doc sweep, pack, tag

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>`), `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `CLAUDE.md`, `KhaozEngine.Locomotion/README.md`, `KhaozEngine.Render3D/README.md`, `docs/USING-KHAOZENGINE.md`.

- [ ] **Step 1: Bump the version**

Edit `Directory.Build.props`: `<KhaozEngineVersion>8.2.0</KhaozEngineVersion>` → `<KhaozEngineVersion>8.3.0</KhaozEngineVersion>`.

- [ ] **Step 2: Add the CHANGELOG entry (newest-first, one-line digest first sentence)**

Prepend an `## 8.3.0` section. First sentence is the digest; then bullet the behavior change (Part A) and the additive baker change (Part B). Example:

```markdown
## 8.3.0

Character movement now collide-and-slides against static meshes with a swept resolver (one-sided building
meshes are rich AND trap-proof, no tunneling), stairs/curbs are walkable via a step-up probe, and `ke-propbake`
bakes a leaning trunk-hull collider for every tree.

- **Locomotion (behavior change, all NetWorld/Simulation consumers):** `CharacterMovement.Step(in MoveState, …)`
  resolves the move by a substepped swept collide-and-slide over `IPhysicsWorld.SweepCapsule` instead of
  teleport-then-depenetrate. A fast move (jump/run/terminal fall) can no longer tunnel through a thin one-sided
  wall, and the capsule never enters a closed mesh (so it can never get stuck inside a building). Depenetration
  is retained as a residual-overlap settle pass. A new step-up probe finally wires `MoveTuning.StepHeight`, so
  stair treads/curbs below `StepHeight` are walkable. Deterministic; `world == null` is byte-identical. No
  `Step` signature change.
- **Render3D / PropCollisionBake (additive):** `BakeTrunkHull` bakes a tree's collision as a convex hull of the
  lower trunk that follows the leaning centreline (excludes canopy and wide low branches) instead of a vertical
  cylinder; `BakeTrunkCylinder` remains the degenerate fallback. `PropBakePlan.For` single-sources the
  per-prop bake decision.
- **ke-propbake (additive):** now writes a `.coll` for every prop (trees gain a trunk-hull collider where they
  had none) and a `.surf` only for walkable solids.
```

- [ ] **Step 3: Update the three guard-checked declarations**

- `docs/CONSUMERS.md` "Engine current version" → `8.3.0`.
- `docs/ROADMAP.md` "Current released version" → `8.3.0`.
- `README.md` `<PackageReference … Version="…">` example → `8.3.0`.

- [ ] **Step 4: Run the doc-version guard**

Run: `bash scripts/check-doc-versions.sh`
Expected: PASS (the three declarations match `<KhaozEngineVersion>` 8.3.0).

- [ ] **Step 5: Full doc sweep**

Update prose (no em-dashes):
- `CLAUDE.md` package map: Locomotion entry (swept collide-and-slide + step-up, `StepHeight` now wired); Render3D `PropCollisionBake` (`BakeTrunkHull` + `PropBakePlan`); `PropSurface.Tool` (emits a `.coll` per prop).
- `KhaozEngine.Locomotion/README.md`: describe the swept resolver + step-up (replace any teleport-then-depenetrate wording).
- `KhaozEngine.Render3D/README.md`: `PropCollisionBake` now bakes a trunk hull for trees; add `PropBakePlan`.
- `docs/USING-KHAOZENGINE.md`: movement section (swept collide-and-slide, step-up) and baker section (tree `.coll`).

Mechanical check:
Run: `grep -rln "BakeTrunkHull\|PropBakePlan\|swept\|step-up" --include='*.md' . ; grep -rln "vertical cylinder\|teleport.*depenetrat" --include='*.md' .`
Expected: the new names appear in the catalog/README/USING docs; no doc still describes trees as a vertical cylinder or movement as teleport-then-depenetrate (fix any that do).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (entire suite green).

- [ ] **Step 7: Pack to local-feed**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: all packable projects pack at 8.3.0 into `./local-feed`.

- [ ] **Step 8: Commit the release**

```bash
git add -A
git commit -m "locomotion(8.3.0): swept trap-proof character collision + trunk-hull baker"
```

- [ ] **Step 9: Tag (HELD — do not push)**

```bash
git tag v8.3.0
```

Do NOT push `main` or the tag. Per engine policy, the push + tag are held/batched and confirmed with the user first. The merge back to `main` and the push happen after review, on user confirmation.

---

## Self-Review (completed during planning)

- **Spec coverage:** Part A swept resolver → Task 5; step-up/stairs → Task 6; trunk hull → Task 2; `HullFromPoints` refactor → Task 1; `PropBakePlan` → Task 3; tool decouple → Task 4; release + doc sweep → Task 7. All spec test bullets mapped to a step.
- **Type consistency:** `HullFromPoints(IReadOnlyList<Vector3>)` defined Task 1, used Task 2. `PropBakePlan(PhysicsShape Coll, PropSurface? Surface)` / `.For` defined Task 3, used Task 4. `SweptMove`/`SlideSubstep` defined Task 5, extended (out params) Task 6 with all call sites updated. `BakeTrunkHull` returns `PhysicsShape` (hull or cylinder fallback) — tests assert the concrete type per fixture.
- **No placeholders:** every code step shows complete code; the one measured-threshold step (Task 5 Step 5) is an explicit re-derivation procedure for a deliberate behavior change, not a TODO.
