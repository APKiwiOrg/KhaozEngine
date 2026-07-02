# Collision-Shape Debug Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A toggleable, translucent, color-coded debug overlay that renders physics collision shapes over the live 3D scene, with an on-screen legend, built as the first layer of an extensible overlay framework.

**Architecture:** Headless shape-to-mesh conversion in `Render3D` (dependency-free convex-hull triangulator + per-kind mesh builder), a new general translucent-unlit depth-tested render primitive on `Scene3D` that future overlay layers reuse, a `CollisionShapeOverlay` (Enabled + Build + Draw) built on that primitive, and a domain-agnostic `OverlayLegend` widget in `Gui`. Render-only: no effect on the sim, `.coll` bakes, or determinism.

**Tech Stack:** C# / net10.0, `System.Numerics`, KhaozEngine `Render3D` (GltfMesh/ModelVertex/Scene3D/MeshBuilder), `Gpu` (pipeline/blend/depth seam), `Gui` (SpriteBatch/DiagnosticsOverlay pattern), `Physics` (PhysicsShape/Pose), xUnit + `GpuFact` for tests, Veldrid.SPIRV shaders.

## Global Constraints

- Engine version bump: **9.1.0** (additive minor). Single `<KhaozEngineVersion>` line in `Directory.Build.props`. Re-check for a concurrent bump at release time and take the next free version if 9.1.0 is claimed.
- No new package. Code lands in existing `KhaozEngine.Render3D`, `KhaozEngine.Gui`, `KhaozEngine.Tests`, `TerrainWalkSample`. No new dependency edges (Render3D already depends on Physics; Gui must not gain Render3D/Physics deps).
- No Bepu dependency in the render layer. The convex-hull triangulator is dependency-free.
- Render-only: zero effect on movement sim or determinism. Do not touch `PhysicsShape`, `Pose`, or any bake output.
- Every new behaviour ships with a headless test in `KhaozEngine.Tests` (no GPU in the required tests). GPU-only checks use `GpuFact`.
- No per-frame allocations in the overlay after build. Meshes built once per static set.
- Writing style: no em-dashes, no semicolons in prose/docs/comments/commit messages.
- Full doc sweep on release (see Task 10). GPU golden must be baked on all three backends before main is green (Task 9).

---

### Task 1: `ConvexHull3D` triangulator (headless)

Dependency-free 3D convex-hull triangulation so `ConvexHullShape.Points` can be rendered without Bepu.

**Files:**
- Create: `KhaozEngine.Render3D/Debug/ConvexHull3D.cs`
- Test: `KhaozEngine.Tests/Render3D/ConvexHull3DTests.cs`

**Interfaces:**
- Consumes: nothing (leaf).
- Produces: `public static class ConvexHull3D` with `public static (Vector3[] Vertices, int[] Indices) Triangulate(IReadOnlyList<Vector3> points)`. Outward-wound triangle triples. Returns `(Array.Empty<Vector3>(), Array.Empty<int>())` for degenerate input (fewer than 4 unique points, or all coplanar/collinear).

- [ ] **Step 1: Write the failing tests**

```csharp
// KhaozEngine.Tests/Render3D/ConvexHull3DTests.cs
using System.Numerics;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class ConvexHull3DTests
{
    static readonly Vector3[] Cube =
    {
        new(-1,-1,-1), new(1,-1,-1), new(1,1,-1), new(-1,1,-1),
        new(-1,-1, 1), new(1,-1, 1), new(1,1, 1), new(-1,1, 1),
    };

    [Fact]
    public void Cube_produces_twelve_outward_triangles()
    {
        var (v, i) = ConvexHull3D.Triangulate(Cube);
        Assert.Equal(12, i.Length / 3);
        AssertAllFacesOutward(v, i);
    }

    [Fact]
    public void Tetrahedron_produces_four_triangles()
    {
        var pts = new[] { new Vector3(0,0,0), new(1,0,0), new(0,1,0), new(0,0,1) };
        var (v, i) = ConvexHull3D.Triangulate(pts);
        Assert.Equal(4, i.Length / 3);
        AssertAllFacesOutward(v, i);
    }

    [Fact]
    public void Interior_points_do_not_add_faces()
    {
        var pts = new List<Vector3>(Cube) { new(0,0,0), new(0.5f,0.1f,-0.2f) };
        var (_, i) = ConvexHull3D.Triangulate(pts);
        Assert.Equal(12, i.Length / 3);
    }

    [Fact]
    public void Coplanar_input_returns_empty()
    {
        var pts = new[] { new Vector3(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0) };
        var (v, i) = ConvexHull3D.Triangulate(pts);
        Assert.Empty(v);
        Assert.Empty(i);
    }

    [Fact]
    public void Too_few_points_returns_empty()
    {
        var (v, i) = ConvexHull3D.Triangulate(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        Assert.Empty(v);
        Assert.Empty(i);
    }

    // Every triangle's outward normal must point away from the hull centroid, and every
    // input point must lie inside or on every face plane.
    static void AssertAllFacesOutward(Vector3[] v, int[] idx)
    {
        Vector3 c = Vector3.Zero;
        foreach (var p in v) c += p;
        c /= v.Length;
        for (int t = 0; t < idx.Length; t += 3)
        {
            Vector3 a = v[idx[t]], b = v[idx[t + 1]], cc = v[idx[t + 2]];
            Vector3 n = Vector3.Cross(b - a, cc - a);
            Assert.True(Vector3.Dot(n, a - c) > 0f, "face winding is not outward");
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~ConvexHull3DTests`
Expected: FAIL (type `ConvexHull3D` does not exist).

- [ ] **Step 3: Implement the triangulator**

Implement an incremental convex hull. Sketch (fill in the incremental face/horizon logic):

```csharp
// KhaozEngine.Render3D/Debug/ConvexHull3D.cs
using System.Numerics;

namespace KhaozEngine.Render3D.Debug;

/// <summary>Dependency-free 3D convex-hull triangulation for debug rendering.</summary>
public static class ConvexHull3D
{
    const float Eps = 1e-6f;

    public static (Vector3[] Vertices, int[] Indices) Triangulate(IReadOnlyList<Vector3> points)
    {
        Vector3[] pts = Dedupe(points);
        if (pts.Length < 4) return (System.Array.Empty<Vector3>(), System.Array.Empty<int>());

        // Build an initial tetrahedron from 4 non-coplanar extreme points.
        if (!InitialTetrahedron(pts, out int[] seed))
            return (System.Array.Empty<Vector3>(), System.Array.Empty<int>()); // coplanar/collinear

        var faces = new List<(int A, int B, int C)>();
        AddTetraFaces(pts, seed, faces); // 4 faces, each oriented outward vs the tetra centroid

        for (int p = 0; p < pts.Length; p++)
        {
            if (System.Array.IndexOf(seed, p) >= 0) continue;
            // Collect faces visible from pts[p] (point in front of face plane).
            var visible = new List<int>();
            for (int f = 0; f < faces.Count; f++)
                if (InFront(pts, faces[f], pts[p])) visible.Add(f);
            if (visible.Count == 0) continue; // interior or on-surface point

            // Horizon = edges bordering exactly one visible face. Remove visible faces,
            // then stitch new outward faces from each horizon edge to pts[p].
            var horizon = HorizonEdges(faces, visible);
            RemoveDescending(faces, visible);
            foreach (var (a, b) in horizon)
                faces.Add(OrientOutward(pts, a, b, p, faces));
        }

        return Emit(pts, faces);
    }

    static Vector3[] Dedupe(IReadOnlyList<Vector3> points)
    {
        var seen = new Dictionary<(int,int,int), Vector3>();
        foreach (var q in points)
        {
            var key = ((int)MathF.Round(q.X * 1e5f), (int)MathF.Round(q.Y * 1e5f), (int)MathF.Round(q.Z * 1e5f));
            seen.TryAdd(key, q);
        }
        return new List<Vector3>(seen.Values).ToArray();
    }

    // InitialTetrahedron, AddTetraFaces, InFront, HorizonEdges, RemoveDescending,
    // OrientOutward, Emit: standard incremental-hull helpers. InFront uses the face plane
    // normal (oriented outward vs the current hull centroid) with tolerance Eps. Emit
    // flattens (A,B,C) triples into (Vertices, Indices), compacting to used vertices.
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~ConvexHull3DTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Debug/ConvexHull3D.cs KhaozEngine.Tests/Render3D/ConvexHull3DTests.cs
git commit -m "feat(render3d): dependency-free convex-hull triangulator for debug overlay"
```

---

### Task 2: Overlay value types + palette (headless)

The small shared types the mesh builder and overlay use.

**Files:**
- Create: `KhaozEngine.Render3D/Debug/CollisionOverlayTypes.cs`
- Test: `KhaozEngine.Tests/Render3D/CollisionOverlayPaletteTests.cs`

**Interfaces:**
- Consumes: `KhaozEngine.Primitives.Color`, `KhaozEngine.Physics.PhysicsShape` + subtypes, `KhaozEngine.Physics.Pose`.
- Produces:
  - `public enum CollisionShapeKind { Box, Sphere, Capsule, Cylinder, ConvexHull, TriangleMesh }`
  - `public readonly record struct CollisionStatic(PhysicsShape Shape, Pose Pose)`
  - `public sealed class CollisionOverlayPalette` with `Color For(CollisionShapeKind kind)`, indexer settable per kind, `string NameFor(CollisionShapeKind kind)`, and `static CollisionShapeKind KindOf(PhysicsShape shape)`.

- [ ] **Step 1: Write the failing test**

```csharp
// KhaozEngine.Tests/Render3D/CollisionOverlayPaletteTests.cs
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class CollisionOverlayPaletteTests
{
    [Fact]
    public void KindOf_maps_every_shape_type()
    {
        Assert.Equal(CollisionShapeKind.Box, CollisionOverlayPalette.KindOf(new BoxShape(Vector3.One)));
        Assert.Equal(CollisionShapeKind.Sphere, CollisionOverlayPalette.KindOf(new SphereShape(1f)));
        Assert.Equal(CollisionShapeKind.Capsule, CollisionOverlayPalette.KindOf(new CapsuleShape(0.5f, 1f)));
        Assert.Equal(CollisionShapeKind.Cylinder, CollisionOverlayPalette.KindOf(new CylinderShape(0.5f, 1f)));
        Assert.Equal(CollisionShapeKind.ConvexHull, CollisionOverlayPalette.KindOf(new ConvexHullShape(new[] { Vector3.Zero })));
        Assert.Equal(CollisionShapeKind.TriangleMesh, CollisionOverlayPalette.KindOf(new TriangleMeshShape(new[] { Vector3.Zero }, new[] { 0 })));
    }

    [Fact]
    public void Default_colors_are_distinct_and_translucent()
    {
        var p = new CollisionOverlayPalette();
        var kinds = System.Enum.GetValues<CollisionShapeKind>();
        var seen = new HashSet<string>();
        foreach (var k in kinds)
        {
            var c = p.For(k);
            Assert.InRange(c.A, 0.01f, 0.9f); // translucent
            Assert.True(seen.Add($"{c.R:F2},{c.G:F2},{c.B:F2}"), $"duplicate hue for {k}");
        }
    }

    [Fact]
    public void Palette_color_is_overridable()
    {
        var p = new CollisionOverlayPalette();
        var custom = new KhaozEngine.Primitives.Color(0.1f, 0.2f, 0.3f, 0.4f);
        p[CollisionShapeKind.Box] = custom;
        Assert.Equal(custom, p.For(CollisionShapeKind.Box));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~CollisionOverlayPaletteTests`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement the types**

```csharp
// KhaozEngine.Render3D/Debug/CollisionOverlayTypes.cs
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Debug;

public enum CollisionShapeKind { Box, Sphere, Capsule, Cylinder, ConvexHull, TriangleMesh }

public readonly record struct CollisionStatic(PhysicsShape Shape, Pose Pose);

/// <summary>Per-kind color + display name for the collision overlay. Colors are translucent
/// and overridable by the game.</summary>
public sealed class CollisionOverlayPalette
{
    // Distinct hues at low alpha (Blender-proxy feel).
    readonly Color[] _colors =
    {
        new(0.90f, 0.20f, 0.20f, 0.35f), // Box       red
        new(0.20f, 0.55f, 0.95f, 0.35f), // Sphere    blue
        new(0.25f, 0.85f, 0.35f, 0.35f), // Capsule   green
        new(0.95f, 0.75f, 0.15f, 0.35f), // Cylinder  amber
        new(0.75f, 0.35f, 0.90f, 0.35f), // ConvexHull violet
        new(0.30f, 0.85f, 0.85f, 0.35f), // TriangleMesh cyan
    };

    static readonly string[] Names = { "Box", "Sphere", "Capsule", "Cylinder", "Convex hull", "Triangle mesh" };

    public Color For(CollisionShapeKind kind) => _colors[(int)kind];
    public Color this[CollisionShapeKind kind] { get => _colors[(int)kind]; set => _colors[(int)kind] = value; }
    public string NameFor(CollisionShapeKind kind) => Names[(int)kind];

    public static CollisionShapeKind KindOf(PhysicsShape shape) => shape switch
    {
        BoxShape => CollisionShapeKind.Box,
        SphereShape => CollisionShapeKind.Sphere,
        CapsuleShape => CollisionShapeKind.Capsule,
        CylinderShape => CollisionShapeKind.Cylinder,
        ConvexHullShape => CollisionShapeKind.ConvexHull,
        TriangleMeshShape => CollisionShapeKind.TriangleMesh,
        _ => throw new System.NotSupportedException($"Unsupported shape for overlay: {shape.GetType().Name}"),
    };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~CollisionOverlayPaletteTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Debug/CollisionOverlayTypes.cs KhaozEngine.Tests/Render3D/CollisionOverlayPaletteTests.cs
git commit -m "feat(render3d): collision overlay palette + shape-kind types"
```

---

### Task 3: `CollisionShapeMesh` conversion (headless)

The core: one `PhysicsShape` to one colored `GltfMesh` in local space.

**Files:**
- Create: `KhaozEngine.Render3D/Debug/CollisionShapeMesh.cs`
- Test: `KhaozEngine.Tests/Render3D/CollisionShapeMeshTests.cs`
- Reference (read, do not modify): `KhaozEngine.Render3D/MeshPrimitives.cs`, `KhaozEngine.Render3D/MeshBuilder.cs`, `KhaozEngine.Render3D/Models/GltfMesh.cs`

**Interfaces:**
- Consumes: `ConvexHull3D.Triangulate` (Task 1), `CollisionOverlayPalette` + `CollisionShapeKind` (Task 2), `GltfMesh`/`ModelVertex`/`MeshBuilder`/`MeshPrimitives`.
- Produces: `public static class CollisionShapeMesh` with `public static GltfMesh Build(PhysicsShape shape, CollisionOverlayPalette palette)`. Per-vertex `ModelVertex.Color` carries the kind color. Geometry conventions per the spec (box centered, cylinder base-aligned, capsule symmetric, hull/mesh raw, compound children composed by local pose and colored per child kind).

- [ ] **Step 1: Write the failing tests**

```csharp
// KhaozEngine.Tests/Render3D/CollisionShapeMeshTests.cs
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D.Debug;
using KhaozEngine.Render3D.Models;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class CollisionShapeMeshTests
{
    static readonly CollisionOverlayPalette P = new();

    static (Vector3 Min, Vector3 Max) Bounds(GltfMesh m)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in m.Vertices) { mn = Vector3.Min(mn, v.Position); mx = Vector3.Max(mx, v.Position); }
        return (mn, mx);
    }

    [Fact]
    public void Box_is_centered_with_full_extents()
    {
        var m = CollisionShapeMesh.Build(new BoxShape(new Vector3(1, 2, 3)), P);
        var (mn, mx) = Bounds(m);
        Assert.Equal(new Vector3(-1, -2, -3), mn, Comparer);
        Assert.Equal(new Vector3(1, 2, 3), mx, Comparer);
        Assert.True(m.Indices32.Length >= 36);
    }

    [Fact]
    public void Cylinder_is_base_aligned()
    {
        var m = CollisionShapeMesh.Build(new CylinderShape(0.5f, 2f), P);
        var (mn, mx) = Bounds(m);
        Assert.Equal(0f, mn.Y, 3);          // base at local Y=0
        Assert.Equal(2f, mx.Y, 3);          // top at length
        Assert.Equal(0.5f, mx.X, 2);        // radius
    }

    [Fact]
    public void Capsule_is_symmetric_with_total_height()
    {
        float r = 0.5f, len = 2f;
        var m = CollisionShapeMesh.Build(new CapsuleShape(r, len), P);
        var (mn, mx) = Bounds(m);
        float total = 2 * r + len;
        Assert.Equal(-total / 2f, mn.Y, 2);
        Assert.Equal(total / 2f, mx.Y, 2);
    }

    [Fact]
    public void Sphere_is_centered_radius()
    {
        var m = CollisionShapeMesh.Build(new SphereShape(1.5f), P);
        var (mn, mx) = Bounds(m);
        Assert.Equal(1.5f, mx.X, 1);
        Assert.Equal(-1.5f, mn.X, 1);
    }

    [Fact]
    public void ConvexHull_triangulates_points()
    {
        var pts = new[]
        {
            new Vector3(-1,-1,-1), new(1,-1,-1), new(1,1,-1), new(-1,1,-1),
            new(-1,-1, 1), new(1,-1, 1), new(1,1, 1), new(-1,1, 1),
        };
        var m = CollisionShapeMesh.Build(new ConvexHullShape(pts), P);
        Assert.Equal(12, m.Indices32.Length / 3);
    }

    [Fact]
    public void TriangleMesh_is_passed_through()
    {
        var v = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var i = new[] { 0, 1, 2 };
        var m = CollisionShapeMesh.Build(new TriangleMeshShape(v, i), P);
        Assert.Equal(3, m.Vertices.Length);
        Assert.Equal(new[] { 0u, 1u, 2u }, m.Indices32);
    }

    [Fact]
    public void Kind_color_is_baked_into_vertices()
    {
        var m = CollisionShapeMesh.Build(new BoxShape(Vector3.One), P);
        Vector4 expected = P.For(CollisionShapeKind.Box).ToVector4();
        Assert.All(m.Vertices, v => Assert.Equal(expected, v.Color));
    }

    [Fact]
    public void Compound_composes_child_local_pose_and_colors_per_kind()
    {
        var child = new CompoundChild(new BoxShape(new Vector3(0.5f)), new Pose(new Vector3(5, 0, 0), Quaternion.Identity));
        var m = CollisionShapeMesh.Build(new CompoundShape(new[] { child }), P);
        var (mn, mx) = Bounds(m);
        Assert.Equal(4.5f, mn.X, 2);   // 5 - 0.5, child shifted by local pose
        Assert.Equal(5.5f, mx.X, 2);
        Vector4 boxColor = P.For(CollisionShapeKind.Box).ToVector4();
        Assert.All(m.Vertices, v => Assert.Equal(boxColor, v.Color));
    }

    static readonly VecComparer Comparer = new();
    sealed class VecComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~CollisionShapeMeshTests`
Expected: FAIL (`CollisionShapeMesh` missing).

- [ ] **Step 3: Implement the converter**

Append geometry into shared vertex/index lists with a transform + color. Reuse `MeshPrimitives` where it fits (read `MeshPrimitives.cs` for exact signatures of `Box`, `Sphere`, `Cylinder`) and transform via `MeshBuilder`/matrix. Set `ModelVertex.Color` to the kind color on every emitted vertex.

```csharp
// KhaozEngine.Render3D/Debug/CollisionShapeMesh.cs
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Models;

namespace KhaozEngine.Render3D.Debug;

/// <summary>Converts a PhysicsShape into a colored GltfMesh in the shape's local space for
/// the debug overlay. Headless, no GPU.</summary>
public static class CollisionShapeMesh
{
    const int CircleSegments = 20;

    public static GltfMesh Build(PhysicsShape shape, CollisionOverlayPalette palette)
    {
        var verts = new List<ModelVertex>();
        var indices = new List<uint>();
        Append(shape, Matrix4x4.Identity, palette, verts, indices);
        return new GltfMesh(verts.ToArray(), indices.ToArray());
    }

    static void Append(PhysicsShape shape, Matrix4x4 xform, CollisionOverlayPalette palette,
        List<ModelVertex> verts, List<uint> indices)
    {
        switch (shape)
        {
            case CompoundShape compound:
                foreach (var c in compound.Children)
                    Append(c.Shape, PoseMatrix(c.Local) * xform, palette, verts, indices);
                return;

            case BoxShape box:
                // Centered box, full extents = 2 * HalfExtents.
                Emit(BoxGeometry(box.HalfExtents), Color(palette, shape), xform, verts, indices);
                return;

            case SphereShape sphere:
                Emit(SphereGeometry(sphere.Radius), Color(palette, shape), xform, verts, indices);
                return;

            case CapsuleShape capsule:
                // Cylinder + 2 hemisphere caps, symmetric about origin, total height 2r+len.
                Emit(CapsuleGeometry(capsule.Radius, capsule.Length), Color(palette, shape), xform, verts, indices);
                return;

            case CylinderShape cyl:
                // Base-aligned: spans local Y 0..Length (matches Bepu runtime lift).
                Emit(CylinderGeometry(cyl.Radius, cyl.Length), Color(palette, shape), xform, verts, indices);
                return;

            case ConvexHullShape hull:
                var (hv, hi) = ConvexHull3D.Triangulate(hull.Points);
                Emit((hv, hi), Color(palette, shape), xform, verts, indices);
                return;

            case TriangleMeshShape mesh:
                Emit((mesh.Vertices, mesh.Indices), Color(palette, shape), xform, verts, indices);
                return;

            default:
                throw new System.NotSupportedException($"Unsupported shape: {shape.GetType().Name}");
        }
    }

    static Vector4 Color(CollisionOverlayPalette p, PhysicsShape s) =>
        p.For(CollisionOverlayPalette.KindOf(s)).ToVector4();

    static Matrix4x4 PoseMatrix(Pose pose) =>
        Matrix4x4.CreateFromQuaternion(pose.Orientation) * Matrix4x4.CreateTranslation(pose.Position);

    static void Emit((Vector3[] V, int[] I) geo, Vector4 color, Matrix4x4 xform,
        List<ModelVertex> verts, List<uint> indices)
    {
        uint b = (uint)verts.Count;
        foreach (var p in geo.V)
        {
            Vector3 wp = Vector3.Transform(p, xform);
            verts.Add(new ModelVertex(wp, Vector3.UnitY, color, Vector2.Zero));
        }
        foreach (var i in geo.I) indices.Add(b + (uint)i);
    }

    // BoxGeometry, SphereGeometry, CapsuleGeometry, CylinderGeometry return
    // (Vector3[] positions, int[] indices). Build them directly or by pulling positions/
    // indices out of MeshPrimitives.Box/Sphere/Cylinder and rescaling. Cylinder base at
    // Y=0. Capsule symmetric about Y=0. Use CircleSegments for radial resolution.
}
```

Note: confirm the `ModelVertex` constructor arity against `Models/GltfMesh.cs` (a 4-arg `(Position, Normal, Color, Uv)` form exists; a 5th tangent arg may be required, pass `Vector4.Zero`). Confirm `MeshPrimitives` method signatures before reusing.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~CollisionShapeMeshTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Debug/CollisionShapeMesh.cs KhaozEngine.Tests/Render3D/CollisionShapeMeshTests.cs
git commit -m "feat(render3d): PhysicsShape to colored debug mesh conversion"
```

---

### Task 4: Translucent overlay render primitive on `Scene3D`

New general pass: unlit, depth-tested, alpha-blended, per-draw world via dynamic-offset UBO. First reused by the collision overlay, then by future overlay layers.

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/OverlayMeshRenderer.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.cs` (add `OverlayUnlitVert` / `OverlayUnlitFrag`)
- Modify: `KhaozEngine.Render3D/Scene3D.cs` (queue + `DrawOverlayMesh` public method + flush in `RenderInternal`)
- Reference (read, mirror): `KhaozEngine.Render3D/Rendering/TexturedBillboardRenderer.cs`, `KhaozEngine.Render3D/Rendering/BeamRenderer.cs`, `KhaozEngine.Render3D/Rendering/OverlayRenderer.cs`, `KhaozEngine.Render3D/Rendering/ModelRenderer.cs` (pipeline + frame UBO)
- Test: `KhaozEngine.Tests/Gpu/OverlayMeshRendererGpuTests.cs`

**Interfaces:**
- Consumes: `GpuDepthStencilState.DepthTestLessEqualNoWrite`, `GpuBlendAttachment.AlphaBlend`, `MeshHandle`, `IIsoCamera3D.ViewProjection`, `GltfMesh` vertex/index buffers via `Scene3D.LoadMesh`.
- Produces: `public void Scene3D.DrawOverlayMesh(MeshHandle mesh, Matrix4x4 world)` (queues a translucent unlit draw for the current frame). Internal `OverlayMeshRenderer` owning the pipeline/shaders/world UBO.

- [ ] **Step 1: Add the unlit shaders**

In `Internal/ShaderSources.cs`, add two GLSL 450 constants following the existing `LineVert`/`FillFrag` style. World comes from a per-draw dynamic UBO at binding 1, view-proj at binding 0.

```glsl
// OverlayUnlitVert
#version 450
layout(set=0, binding=0) uniform Frame { mat4 ViewProj; };
layout(set=0, binding=1) uniform Draw  { mat4 World; };
layout(location=0) in vec3 Position;
layout(location=2) in vec4 Color;   // ModelVertex.Color at location 2
layout(location=0) out vec4 vColor;
void main() { gl_Position = ViewProj * (World * vec4(Position, 1.0)); vColor = Color; }
```
```glsl
// OverlayUnlitFrag
#version 450
layout(location=0) in vec4 vColor;
layout(location=0) out vec4 oColor;   // single color target (the post-input), alpha via blend
void main() { oColor = vColor; }
```

Note: match the vertex input locations to the `ModelVertex` layout the model pipeline declares (Position=0, Normal=1, Color=2, Uv=3, Tangent=4). The overlay pipeline can declare the full `ModelVertex` layout and only read Position + Color. The output attachment set must match the framebuffer the pass renders into (see Step 3). If that FB is the 3-target model MRT, write to target 0 and set the other attachments to `PreserveDestination` blend.

- [ ] **Step 2: Implement `OverlayMeshRenderer`**

Mirror `TexturedBillboardRenderer` for lifecycle (ctor takes the graphics device + resource factory, builds pipeline, exposes Begin/SetFrameUniforms/Draw). Pipeline:

```csharp
_pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
{
    BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend, GpuBlendAttachment.PreserveDestination, GpuBlendAttachment.PreserveDestination },
    DepthStencil = GpuDepthStencilState.DepthTestLessEqualNoWrite,
    Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
    Topology = GpuPrimitiveTopology.TriangleList,
    ShaderSet = _shaders,           // CreateShadersFromSpirv(OverlayUnlitVert, OverlayUnlitFrag)
    ResourceLayouts = new[] { _layout },   // binding 0 Frame UBO, binding 1 Draw UBO (dynamic)
    VertexLayouts = new List<GpuVertexLayoutDescription> { modelVertexLayout },
    Outputs = modelOutputs,         // same outputs as the model FB it draws into
});
```

Per-draw world uses a dynamic-offset UBO (one UBO region per queued draw), NOT a per-instance vertex attribute. This avoids the Veldrid/Metal bug where instances past the first are dropped when a vertex shader indexes a per-instance buffer. Confirm the exact dynamic-offset UBO helper by reading how `ModelRenderer`/an existing renderer sets a dynamic UBO.

- [ ] **Step 3: Wire into `Scene3D`**

Add a per-frame queue and flush. Read `Scene3D.cs` `RenderInternal` to find the point after beams/decals and before `_post.Run`, and the model framebuffer (`_res.ModelFB`) it draws into (so the pass has the world depth buffer to test against):

```csharp
// fields
readonly List<(MeshHandle Mesh, Matrix4x4 World)> _overlayDraws = new();
OverlayMeshRenderer _overlay = null!;

// in Begin(): _overlayDraws.Clear();

public void DrawOverlayMesh(MeshHandle mesh, Matrix4x4 world) => _overlayDraws.Add((mesh, world));

// in RenderInternal, after DrawBeams(cl) / decals, before _post.Run(...):
if (_overlayDraws.Count > 0)
{
    _overlay.BeginPass(cl, _res);                       // bind pipeline (FB already the model FB)
    _overlay.SetFrameUniforms(cl, GpuClip.Correct(ActiveCamera.ViewProjection, _gd.Capabilities));
    foreach (var d in _overlayDraws)
    {
        if (!TryResolveMesh(d.Mesh, out var m)) continue;
        _overlay.DrawMesh(cl, m.Vb, m.Ib, m.IndexCount, m.IndexFormat, d.World);
    }
}
```

Match `TryResolveMesh`/mesh-record access to how `RenderInternal` already resolves `MeshHandle` to GPU buffers in the model run loop.

- [ ] **Step 4: Write a GPU smoke test**

```csharp
// KhaozEngine.Tests/Gpu/OverlayMeshRendererGpuTests.cs
// Uses the repo's GpuFact harness (see existing KhaozEngine.Tests/Gpu tests for the fixture).
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Debug;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public class OverlayMeshRendererGpuTests
{
    [GpuFact]
    public void Overlay_draw_does_not_throw_and_changes_pixels()
    {
        using var scene = GpuTestScene.Create(256, 256); // mirror existing Gpu test setup
        var mesh = scene.LoadMesh(CollisionShapeMesh.Build(new BoxShape(Vector3.One), new CollisionOverlayPalette()));
        scene.Begin();
        scene.DrawOverlayMesh(mesh, Matrix4x4.Identity);
        var pixels = scene.RenderToPixels();
        Assert.Contains(pixels, px => px.A > 0); // something was drawn
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~OverlayMeshRenderer`
Expected: PASS on a machine with a GPU (locally Metal). If the harness auto-skips GpuFact headless, it is skipped in CI non-GPU jobs (that is expected).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Rendering/OverlayMeshRenderer.cs KhaozEngine.Render3D/Internal/ShaderSources.cs KhaozEngine.Render3D/Scene3D.cs KhaozEngine.Tests/Gpu/OverlayMeshRendererGpuTests.cs
git commit -m "feat(render3d): translucent unlit depth-tested overlay mesh pass on Scene3D"
```

---

### Task 5: `CollisionShapeOverlay`

The first overlay layer. Headless-testable core (`BuildMeshes`) + thin GPU wrapper.

**Files:**
- Create: `KhaozEngine.Render3D/Debug/CollisionShapeOverlay.cs`
- Test: `KhaozEngine.Tests/Render3D/CollisionShapeOverlayTests.cs`

**Interfaces:**
- Consumes: `CollisionShapeMesh.Build` (Task 3), `CollisionStatic`/`CollisionShapeKind`/`CollisionOverlayPalette` (Task 2), `Scene3D.LoadMesh` + `Scene3D.DrawOverlayMesh` (Task 4).
- Produces:
  - `public static (GltfMesh Mesh, Matrix4x4 World)[] BuildMeshes(IReadOnlyList<CollisionStatic> statics, CollisionOverlayPalette palette, out IReadOnlyList<CollisionShapeKind> presentKinds)` (headless).
  - `public sealed class CollisionShapeOverlay : IDisposable` with `bool Enabled`, `CollisionOverlayPalette Palette`, `void Build(Scene3D, IReadOnlyList<CollisionStatic>)`, `void Draw(Scene3D)`, `IReadOnlyList<CollisionShapeKind> PresentKinds`, `Dispose()`.

- [ ] **Step 1: Write the failing tests (headless core)**

```csharp
// KhaozEngine.Tests/Render3D/CollisionShapeOverlayTests.cs
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class CollisionShapeOverlayTests
{
    [Fact]
    public void BuildMeshes_makes_one_mesh_per_static_with_pose_world()
    {
        var statics = new[]
        {
            new CollisionStatic(new BoxShape(Vector3.One), new Pose(new Vector3(3, 0, 0), Quaternion.Identity)),
            new CollisionStatic(new SphereShape(1f), new Pose(new Vector3(0, 5, 0), Quaternion.Identity)),
        };
        var built = CollisionShapeOverlay.BuildMeshes(statics, new CollisionOverlayPalette(), out var kinds);
        Assert.Equal(2, built.Length);
        Assert.Equal(new Vector3(3, 0, 0), built[0].World.Translation, PosCmp);
        Assert.Equal(new Vector3(0, 5, 0), built[1].World.Translation, PosCmp);
        Assert.Contains(CollisionShapeKind.Box, kinds);
        Assert.Contains(CollisionShapeKind.Sphere, kinds);
    }

    [Fact]
    public void PresentKinds_are_distinct()
    {
        var statics = new[]
        {
            new CollisionStatic(new BoxShape(Vector3.One), Pose.At(Vector3.Zero)),
            new CollisionStatic(new BoxShape(Vector3.One), Pose.At(Vector3.UnitX)),
        };
        _ = CollisionShapeOverlay.BuildMeshes(statics, new CollisionOverlayPalette(), out var kinds);
        Assert.Single(kinds);
        Assert.Equal(CollisionShapeKind.Box, kinds[0]);
    }

    [Fact]
    public void Compound_present_kinds_include_all_child_kinds()
    {
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(new BoxShape(Vector3.One), Pose.At(Vector3.Zero)),
            new CompoundChild(new SphereShape(1f), Pose.At(Vector3.UnitZ)),
        });
        _ = CollisionShapeOverlay.BuildMeshes(new[] { new CollisionStatic(compound, Pose.At(Vector3.Zero)) },
            new CollisionOverlayPalette(), out var kinds);
        Assert.Contains(CollisionShapeKind.Box, kinds);
        Assert.Contains(CollisionShapeKind.Sphere, kinds);
    }

    static readonly VecCmp PosCmp = new();
    sealed class VecCmp : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~CollisionShapeOverlayTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// KhaozEngine.Render3D/Debug/CollisionShapeOverlay.cs
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D.Models;

namespace KhaozEngine.Render3D.Debug;

public sealed class CollisionShapeOverlay : System.IDisposable
{
    readonly List<(MeshHandle Handle, Matrix4x4 World)> _draws = new();
    CollisionShapeKind[] _presentKinds = System.Array.Empty<CollisionShapeKind>();
    Scene3D? _scene;

    public bool Enabled { get; set; }
    public CollisionOverlayPalette Palette { get; set; } = new();
    public IReadOnlyList<CollisionShapeKind> PresentKinds => _presentKinds;

    public static (GltfMesh Mesh, Matrix4x4 World)[] BuildMeshes(
        IReadOnlyList<CollisionStatic> statics, CollisionOverlayPalette palette,
        out IReadOnlyList<CollisionShapeKind> presentKinds)
    {
        var result = new (GltfMesh, Matrix4x4)[statics.Count];
        var kinds = new SortedSet<CollisionShapeKind>();
        for (int i = 0; i < statics.Count; i++)
        {
            var s = statics[i];
            result[i] = (CollisionShapeMesh.Build(s.Shape, palette), World(s.Pose));
            CollectKinds(s.Shape, kinds);
        }
        presentKinds = new List<CollisionShapeKind>(kinds);
        return result;
    }

    static void CollectKinds(PhysicsShape shape, SortedSet<CollisionShapeKind> into)
    {
        if (shape is CompoundShape c) { foreach (var ch in c.Children) CollectKinds(ch.Shape, into); return; }
        into.Add(CollisionOverlayPalette.KindOf(shape));
    }

    static Matrix4x4 World(Pose p) =>
        Matrix4x4.CreateFromQuaternion(p.Orientation) * Matrix4x4.CreateTranslation(p.Position);

    public void Build(Scene3D scene, IReadOnlyList<CollisionStatic> statics)
    {
        _scene = scene;
        var built = BuildMeshes(statics, Palette, out var kinds);
        _presentKinds = new List<CollisionShapeKind>(kinds).ToArray();
        _draws.Clear();
        _draws.Capacity = built.Length;
        foreach (var (mesh, world) in built)
            _draws.Add((scene.LoadMesh(mesh), world));
    }

    public void Draw(Scene3D scene)
    {
        if (!Enabled) return;
        for (int i = 0; i < _draws.Count; i++)
            scene.DrawOverlayMesh(_draws[i].Handle, _draws[i].World);
    }

    public void Dispose()
    {
        // Release mesh handles if Scene3D exposes an unload path; otherwise clear the list.
        _draws.Clear();
    }
}
```

Note: `Draw` allocates nothing (index loop over a pre-sized list). If `Scene3D` exposes a mesh-unload API, call it in `Dispose` and on rebuild; if not, leave handles (documented limitation) and just clear.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~CollisionShapeOverlayTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Debug/CollisionShapeOverlay.cs KhaozEngine.Tests/Render3D/CollisionShapeOverlayTests.cs
git commit -m "feat(render3d): CollisionShapeOverlay (build once, draw translucent proxies)"
```

---

### Task 6: `OverlayLegend` widget (Gui)

Domain-agnostic swatch + label panel. No fade: snaps on/off (caller draws only while on).

**Files:**
- Create: `KhaozEngine.Gui/OverlayLegend.cs`
- Reference (read, mirror): `KhaozEngine.Gui/DiagnosticsOverlay.cs`, `KhaozEngine.Gui/GuiDraw.cs`
- Test: `KhaozEngine.Tests/Gui/OverlayLegendTests.cs`

**Interfaces:**
- Consumes: `SpriteBatch`, `SpriteFont`, `Texture2D`, `Rect`, `KhaozEngine.Primitives.Color`, `GuiDraw.Fill`/`Border`.
- Produces: `public sealed class OverlayLegend` with `void SetEntries(IReadOnlyList<LegendEntry>)`, `int EntryCount { get; }`, `Rect Measure(SpriteFont font)`, `void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport)`. `public readonly record struct LegendEntry(Color Swatch, string Label)`.

- [ ] **Step 1: Write the failing test (layout math, headless)**

```csharp
// KhaozEngine.Tests/Gui/OverlayLegendTests.cs
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public class OverlayLegendTests
{
    [Fact]
    public void SetEntries_updates_count()
    {
        var legend = new OverlayLegend();
        legend.SetEntries(new[]
        {
            new LegendEntry(new Color(1,0,0,0.4f), "Box"),
            new LegendEntry(new Color(0,0,1,0.4f), "Sphere"),
        });
        Assert.Equal(2, legend.EntryCount);
    }

    [Fact]
    public void Empty_legend_measures_zero()
    {
        var legend = new OverlayLegend();
        var font = KhaozEngine.Render2D.Surface2D.LoadDefaultFont(20f);
        var r = legend.Measure(font);
        Assert.Equal(0f, r.Width);
        Assert.Equal(0f, r.Height);
    }

    [Fact]
    public void Measured_height_grows_with_entries()
    {
        var font = KhaozEngine.Render2D.Surface2D.LoadDefaultFont(20f);
        var one = new OverlayLegend(); one.SetEntries(new[] { new LegendEntry(Color.White, "A") });
        var two = new OverlayLegend(); two.SetEntries(new[] { new LegendEntry(Color.White, "A"), new LegendEntry(Color.White, "B") });
        Assert.True(two.Measure(font).Height > one.Measure(font).Height);
    }
}
```

Note: confirm `Surface2D.LoadDefaultFont` works headlessly (it loads a CPU font; the sample calls it before any GPU draw). If it needs a device, drop the font-dependent tests and keep `SetEntries_updates_count`, verifying layout via the GPU golden instead.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~OverlayLegendTests`
Expected: FAIL.

- [ ] **Step 3: Implement (mirror DiagnosticsOverlay layout)**

```csharp
// KhaozEngine.Gui/OverlayLegend.cs
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

public readonly record struct LegendEntry(Color Swatch, string Label);

/// <summary>A domain-agnostic color-swatch + label panel for debug overlays. No visibility
/// state of its own: the caller draws it only while the overlay is on.</summary>
public sealed class OverlayLegend
{
    IReadOnlyList<LegendEntry> _entries = System.Array.Empty<LegendEntry>();

    const float Pad = 8f, SwatchSize = 14f, Gap = 8f, RowSpacing = 4f;

    public int EntryCount => _entries.Count;
    public void SetEntries(IReadOnlyList<LegendEntry> entries) => _entries = entries ?? System.Array.Empty<LegendEntry>();

    public Rect Measure(SpriteFont font)
    {
        if (_entries.Count == 0) return new Rect(0, 0, 0, 0);
        float rowH = MathF.Max(SwatchSize, font.LineHeight);
        float w = 0f;
        foreach (var e in _entries)
            w = MathF.Max(w, SwatchSize + Gap + font.Measure(e.Label).X);
        float h = _entries.Count * rowH + (_entries.Count - 1) * RowSpacing;
        return new Rect(0, 0, w + Pad * 2f, h + Pad * 2f);
    }

    public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport)
    {
        if (_entries.Count == 0) return;
        Rect size = Measure(font);
        // Top-left anchor with a small margin.
        var panel = new Rect(viewport.X + 12f, viewport.Y + 12f, size.Width, size.Height);
        GuiDraw.Fill(batch, white, panel, new Vector4(0.05f, 0.06f, 0.09f, 0.75f));
        GuiDraw.Border(batch, white, panel, 1f, new Vector4(0.25f, 0.28f, 0.34f, 0.9f));

        float rowH = MathF.Max(SwatchSize, font.LineHeight);
        float x = panel.X + Pad, y = panel.Y + Pad;
        foreach (var e in _entries)
        {
            GuiDraw.Fill(batch, white, new Rect(x, y + (rowH - SwatchSize) * 0.5f, SwatchSize, SwatchSize), e.Swatch.ToVector4());
            batch.DrawString(font, e.Label, new Vector2(x + SwatchSize + Gap, y + (rowH - font.LineHeight) * 0.5f), (Color)new Vector4(0.92f, 0.94f, 0.97f, 1f));
            y += rowH + RowSpacing;
        }
    }
}
```

Confirm `GuiDraw.Fill`/`Border` signatures and `font.Measure`/`font.LineHeight` against the real files.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~OverlayLegendTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/OverlayLegend.cs KhaozEngine.Tests/Gui/OverlayLegendTests.cs
git commit -m "feat(gui): domain-agnostic OverlayLegend swatch+label widget"
```

---

### Task 7: `TerrainWalkSample` integration + acceptance fixture

Hand-place the `blacksmith_proxy.coll` fixture, feed it to a `CollisionShapeOverlay`, toggle on F2, draw the legend while on.

**Files:**
- Modify: `TerrainWalkSample/Program.cs`
- Reference (read): `KhaozEngine.Tests/Physics/Fixtures/blacksmith_proxy.coll` (fixture), `KhaozEngine.Physics/PropCollisionFormat.cs` (`Read`), `KhaozEngine.Gui/DiagnosticsOverlay.cs` (draw wiring)

**Interfaces:**
- Consumes: `CollisionShapeOverlay`, `CollisionOverlayPalette`, `OverlayLegend`/`LegendEntry`, `PropCollisionFormat.Read`, `Input.WasPressed(Key.F2)`.
- Produces: a runnable sample toggling the overlay.

- [ ] **Step 1: Load a proxy fixture and place it**

In `OnLoad`, after the physics world exists, read a proxy shape from the fixture (copy the fixture file into the sample's content, or reference the test fixture path) and add it as a static at a visible pose near the walk path. Keep the `(shape, pose)` in a list:

```csharp
PhysicsShape proxy = PropCollisionFormat.Read(File.ReadAllBytes(_proxyFixturePath));
var proxyPose = new Pose(new Vector3(4f, terrain.GroundHeight(4f, 4f), 4f), Quaternion.Identity);
_physicsWorld.AddStatic(proxy, proxyPose);
_overlayStatics = new List<CollisionStatic> { new(proxy, proxyPose) };
```

- [ ] **Step 2: Build the overlay + legend**

```csharp
_collisionOverlay = new CollisionShapeOverlay();
_collisionOverlay.Build(_scene, _overlayStatics);
_legend = new OverlayLegend();
_legend.SetEntries(BuildLegendEntries(_collisionOverlay)); // map PresentKinds -> LegendEntry via Palette

static IReadOnlyList<LegendEntry> BuildLegendEntries(CollisionShapeOverlay o)
{
    var list = new List<LegendEntry>();
    foreach (var k in o.PresentKinds)
        list.Add(new LegendEntry(o.Palette.For(k), o.Palette.NameFor(k)));
    return list;
}
```

- [ ] **Step 3: Toggle on F2 in `OnUpdate`**

```csharp
if (Input.WasPressed(Key.F2)) _collisionOverlay.Enabled = !_collisionOverlay.Enabled;
```

- [ ] **Step 4: Draw in `OnDraw3D` / `OnDraw2D`**

```csharp
// OnDraw3D, after scene draws:
_collisionOverlay.Draw(_scene);

// OnDraw2D:
if (_collisionOverlay.Enabled) _legend.Draw(batch, _legendFont, _white, Viewport);
```

- [ ] **Step 5: Build the sample**

Run: `dotnet build TerrainWalkSample/TerrainWalkSample.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add TerrainWalkSample/Program.cs TerrainWalkSample/
git commit -m "feat(sample): F2 collision-overlay toggle + legend in TerrainWalkSample"
```

- [ ] **Step 7: Manual verify (user)**

Hand the user the boot command (see end of plan). They confirm F2 shows translucent proxies + legend.

---

### Task 8: Full test run

- [ ] **Step 1: Run the whole suite (non-GPU)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release`
Expected: all headless tests PASS. GpuFacts run locally on Metal, skipped where no GPU.

- [ ] **Step 2: Fix any red, re-run, commit fixes if any**

---

### Task 9: GPU golden (baked per-backend)

**Files:**
- Modify: `KhaozEngine.Tests/Gpu/OverlayMeshRendererGpuTests.cs` (add the golden `GpuFact`)
- Create (baked, not hand-written): `KhaozEngine.Tests/Gpu/goldens/collision_overlay.metal.txt`, `.d3d11.txt`, `.vulkan.txt`
- Reference (read, mirror): an existing golden `GpuFact` in `KhaozEngine.Tests/Gpu/`

**Interfaces:**
- Consumes: the overlay pass + `CollisionShapeMesh`, the existing golden-compare harness (`KE_UPDATE_GOLDENS`).

- [ ] **Step 1: Add the golden test over a fixture scene**

Fixed camera, one of each shape kind (box, sphere, capsule, cylinder, hull, compound) at known poses, overlay enabled, compare to `collision_overlay.<backend>.txt`. Mirror the existing golden test structure exactly (camera setup, `CompareOrBake` helper name, golden path convention).

- [ ] **Step 2: Bake locally (Metal)**

Run: `KE_UPDATE_GOLDENS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~collision_overlay`
Expected: writes `collision_overlay.metal.txt`. Commit it.

- [ ] **Step 3: Bake D3D11 + Vulkan via CI (REQUIRED before main is green)**

Trigger `cross-platform-gpu.yml` via `workflow_dispatch` with `bake=true` on the feature branch. Download the per-backend artifacts, commit `collision_overlay.d3d11.txt` and `collision_overlay.vulkan.txt`. A Metal-only bake turns main red on the other two runners.

- [ ] **Step 4: Commit goldens**

```bash
git add KhaozEngine.Tests/Gpu/OverlayMeshRendererGpuTests.cs KhaozEngine.Tests/Gpu/goldens/collision_overlay.*.txt
git commit -m "test(gpu): collision-overlay golden baked on Metal/D3D11/Vulkan"
```

---

### Task 10: Release 9.1.0 (docs, version, pack, hold push)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`, `KhaozEngine.Render3D/README.md`, `KhaozEngine.Gui/README.md`
- Reference: `scripts/check-doc-versions.sh`

- [ ] **Step 1: Re-check for concurrent bump**

Run: `git fetch && grep KhaozEngineVersion Directory.Build.props && git tag | tail -5`
If 9.1.0 is taken, use the next free version everywhere below.

- [ ] **Step 2: Bump version + changelog**

Set `<KhaozEngineVersion>9.1.0</KhaozEngineVersion>`. Add a newest-first `CHANGELOG.md` entry (one-line summary first): collision-shape debug overlay (translucent proxies + legend), new `Scene3D.DrawOverlayMesh` translucent-unlit pass, `CollisionShapeOverlay`/`CollisionShapeMesh`/`ConvexHull3D` in Render3D, `OverlayLegend` in Gui. Render-only, no sim/determinism change.

- [ ] **Step 3: Update the 3 guard-checked version strings**

`docs/CONSUMERS.md` engine current version, `docs/ROADMAP.md` current released version, `README.md` PackageReference example all to 9.1.0.

- [ ] **Step 4: Doc sweep**

- `docs/USING-KHAOZENGINE.md`: new section for the overlay API (Build/Enabled/Draw, DrawOverlayMesh, palette, legend).
- `KhaozEngine.Render3D/README.md`: add `CollisionShapeOverlay`, `CollisionShapeMesh`, `ConvexHull3D`, `Scene3D.DrawOverlayMesh`.
- `KhaozEngine.Gui/README.md`: add `OverlayLegend`.
- `docs/CONSUMERS.md`: note Ruinborne (now 9.0.1) bumps 9.0.1 to 9.1.0 to adopt the overlay (trivial same-major).
- Grep the new type names across all `*.md` to confirm no stale references.

- [ ] **Step 5: Verify doc guard + pack**

Run: `bash scripts/check-doc-versions.sh`
Expected: PASS.
Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: packs all packages at 9.1.0.

- [ ] **Step 6: Commit + tag (HOLD push)**

```bash
git add -A
git commit -m "release(9.1.0): collision-shape debug overlay"
git tag v9.1.0
```
Do NOT push. Confirm with the user before publishing (heavy-CI batch policy). The D3D11/Vulkan golden bake (Task 9 Step 3) needs the remote workflow, so coordinate that run with the user as part of the release.

- [ ] **Step 7: Merge back to main (per CLAUDE.md, when user approves finishing)**

`git fetch`, integrate any concurrent `main` into the branch first, re-run tests on the merged result, then merge to `main` and clean up the worktree.

---

## Self-review notes

- Spec coverage: hull triangulation (T1), palette/types (T2), per-kind mesh conversion incl. compound + placement conventions (T3), translucent depth-tested pass + DrawOverlayMesh (T4), overlay build/draw/no-alloc (T5), legend (T6), sample F2 + fixture (T7), full suite (T8), golden baked per-backend (T9), version/doc/pack/hold (T10). All spec sections mapped.
- Type consistency: `CollisionStatic`, `CollisionShapeKind`, `CollisionOverlayPalette.KindOf/For/NameFor`, `CollisionShapeMesh.Build`, `ConvexHull3D.Triangulate`, `Scene3D.DrawOverlayMesh`, `CollisionShapeOverlay.BuildMeshes/Build/Draw/PresentKinds`, `OverlayLegend.SetEntries/Measure/Draw`, `LegendEntry` used identically across tasks.
- Known confirm-at-execution points (flagged inline, not placeholders): exact `ModelVertex` ctor arity, `MeshPrimitives` signatures, `GpuFact`/golden harness helper names, `Scene3D.RenderInternal` injection point + mesh-handle resolution, dynamic-offset UBO helper, `Surface2D.LoadDefaultFont` headless-ness. Each task says to read the named file first.
