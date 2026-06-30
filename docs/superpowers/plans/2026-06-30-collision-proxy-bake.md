# Collision-proxy Bake Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a building bake a SEPARATE simplified collision proxy (a `CompoundShape` of convex pieces) distinct from its full-detail render mesh, so a capsule never wedges in cluttered interior geometry while still standing on floors, stairs, ledges, and furniture.

**Architecture:** Each building authors a simplified proxy GLB (separate convex blocks). A new bake path hulls each proxy object into one convex child of a `CompoundShape`, normalized into the render mesh's frame so it overlays exactly. The `.coll` format gains Box + Compound kinds; a public `PhysicsShapeScale.Uniform` helper scales the new shapes; `ke-propbake` reads a `collisionProxy` manifest field. Convexity (unique shortest-exit MTV) structurally removes the wedge/pin/freeze class; the 8.8.1 resolver invariant is untouched. The proxy path is opt-in, so anything without a proxy bakes exactly as today (additive minor 8.11.0).

**Tech Stack:** C# / net10.0, xUnit, BepuPhysics v2 (behind `KhaozEngine.Physics.Bepu`), SharpGLTF (glTF load + in-process test fixtures), `System.Numerics`.

## Global Constraints

- Engine version line: bump `<KhaozEngineVersion>` in `Directory.Build.props` from `8.10.0` to `8.11.0` (re-check `origin/main` + `git tag` for a concurrent bump at release; take the next FREE version if claimed).
- Deterministic + byte-identical client vs server: both heads read the SAME committed `.coll`; the bake itself must be reproducible (stable logical-node child order, deterministic vertex sort). No wall-clock, no randomness.
- Keep the 8.8.1 resolver invariant ("a wall never holds the capsule up against gravity"). Do NOT edit `CharacterMovement.cs`.
- Full engine suite (~2496 tests) stays green; every new behaviour ships with a headless test.
- TEST AGAINST REAL GEOMETRY, never synthetic flat quads (they give zero-normal contacts that do not reproduce real building wedges).
- No em-dashes / semicolons in shipped prose (CHANGELOG, READMEs, docs, comments).
- `.coll` wire kinds are stable: existing `KindConvexHull=1`, `KindTriangleMesh=2`, `KindCylinder=3` never renumber; new kinds append as `KindBox=4`, `KindCompound=5`. Format `Version` stays `1` (additive kinds).
- Engine phase (Tasks 1-8) must ship + pack to `local-feed` BEFORE the Ruinborne phase (Tasks 9-10) adopts the pin. Hold the `git tag` + push for explicit user confirmation.

---

## File Structure

Engine (`~/KhaozEngine`, this worktree):
- `KhaozEngine.Physics/PhysicsShapeScale.cs` (NEW) - public uniform shape scaler, all kinds incl. Box/Compound.
- `KhaozEngine.Physics/PropCollisionFormat.cs` (MODIFY) - recursive Write/Read, Box + Compound kinds.
- `KhaozEngine.Terrain.Render3D/ChunkStatics.cs` (MODIFY) - `ScaleShape` delegates to the public helper.
- `KhaozEngine.Render3D/Models/GltfLoader.cs` (MODIFY) - add `LoadGroups`.
- `KhaozEngine.Render3D/Models/PropCollisionBake.cs` (MODIFY) - add `BakeProxy`.
- `KhaozEngine.Render3D/Models/PropBakePlan.cs` (MODIFY) - add `ForProxy` overload.
- `KhaozEngine.Render3D/Models/AssetManifest.cs` (MODIFY) - `collisionProxy` field.
- `KhaozEngine.PropSurface.Tool/Program.cs` (MODIFY) - proxy-aware bake.
- Tests under `KhaozEngine.Tests/` (NEW/MODIFY) - format, scale, loader, bake, real-proxy scan.
- `KhaozEngine.Tests/Physics/Fixtures/blacksmith_proxy.coll` (NEW, committed binary fixture).
- Docs + version strings + CHANGELOG + ROADMAP delete.

Consumer (`~/Ruinborne`, after the engine ships):
- `Ruinborne.Client/assets/buildings/*_collision.glb` (NEW, 7 authored proxies).
- `Ruinborne.Client/assets/buildings/buildings.manifest.json` (MODIFY) - `collisionProxy` per building.
- Re-baked `Ruinborne.Client/assets/buildings/*.coll`.
- `Ruinborne.Core/RuinbornePhysics.cs` (MODIFY) - `ScaleShape` delegates to the public helper.
- `Directory.Build.props` (MODIFY) - pin 8.11.0.

---

# Phase 1: Engine (ships 8.11.0)

## Task 1: Public `PhysicsShapeScale.Uniform` helper

Lift the existing all-kinds scale logic out of the internal `ChunkStatics.ScaleShape` into a public helper in the dependency-free `KhaozEngine.Physics` leaf, so both the Render3D-side `ChunkStatics` and the headless Foundation-side `RuinbornePhysics` can delegate. `ChunkStatics.ScaleShape` already handles Box + Compound, so this is a lift-and-delegate (no new scaling math), but `RuinbornePhysics` currently throws on those kinds and will be fixed by delegating in Phase 2.

**Files:**
- Create: `KhaozEngine.Physics/PhysicsShapeScale.cs`
- Modify: `KhaozEngine.Terrain.Render3D/ChunkStatics.cs:64-109`
- Test: `KhaozEngine.Tests/Physics/PhysicsShapeScaleTests.cs` (new)

**Interfaces:**
- Produces: `public static class PhysicsShapeScale { public static PhysicsShape Uniform(PhysicsShape shape, float scale); }` in namespace `KhaozEngine.Physics`. Scales every kind (Sphere/Capsule/Cylinder/Box geometry fields; ConvexHull/TriangleMesh vertex positions; Compound recurses children + scales each child local-pose POSITION, orientation unchanged). `scale == 1` (within 1e-6) returns the original instance.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PhysicsShapeScaleTests
{
    [Fact]
    public void Scale1_ReturnsSameInstance()
    {
        var box = new BoxShape(new Vector3(1, 2, 3));
        Assert.Same(box, PhysicsShapeScale.Uniform(box, 1f));
    }

    [Fact]
    public void Box_ScalesHalfExtents()
    {
        var box = new BoxShape(new Vector3(1, 2, 3));
        var scaled = Assert.IsType<BoxShape>(PhysicsShapeScale.Uniform(box, 2f));
        Assert.Equal(new Vector3(2, 4, 6), scaled.HalfExtents);
    }

    [Fact]
    public void Compound_ScalesChildGeometryAndPosePositionNotOrientation()
    {
        Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        var child = new CompoundChild(new BoxShape(new Vector3(1, 1, 1)), new Pose(new Vector3(4, 0, 0), rot));
        var compound = new CompoundShape(new[] { child });

        var scaled = Assert.IsType<CompoundShape>(PhysicsShapeScale.Uniform(compound, 3f));
        Assert.Single(scaled.Children);
        var sc = scaled.Children[0];
        Assert.Equal(new Vector3(3, 3, 3), Assert.IsType<BoxShape>(sc.Shape).HalfExtents);
        Assert.Equal(new Vector3(12, 0, 0), sc.Local.Position);   // pose position scaled
        Assert.Equal(rot, sc.Local.Orientation);                  // orientation preserved
    }

    [Fact]
    public void ConvexHull_ScalesPoints()
    {
        var hull = new ConvexHullShape(new[] { new Vector3(1, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 3), Vector3.Zero });
        var scaled = Assert.IsType<ConvexHullShape>(PhysicsShapeScale.Uniform(hull, 2f));
        Assert.Equal(new Vector3(2, 0, 0), scaled.Points[0]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PhysicsShapeScaleTests"`
Expected: FAIL to compile, "PhysicsShapeScale does not exist".

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Physics/PhysicsShapeScale.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Returns a new <see cref="PhysicsShape"/> with all geometry scaled uniformly by a single factor.
/// Convex-hull / triangle-mesh vertex positions are pre-multiplied; primitive (sphere/capsule/cylinder/box)
/// length fields are scaled; <see cref="CompoundShape"/> children recurse and each child's local-pose POSITION
/// is scaled (orientation unchanged). A scale of 1 (within 1e-6) returns the original instance unchanged. The
/// single public home for per-placement uniform shape scaling, shared by the Render3D-side chunk-statics loader
/// and any headless consumer (e.g. an authoritative game server) that must scale a baked shape before adding it
/// as a static. Non-uniform (per-axis) scale is intentionally not modelled (the prop scatter emits one uniform
/// scale per placement).</summary>
public static class PhysicsShapeScale
{
    /// <summary>A new shape with every dimension scaled by <paramref name="scale"/>; the original instance when
    /// <paramref name="scale"/> is 1 (within 1e-6).</summary>
    public static PhysicsShape Uniform(PhysicsShape shape, float scale)
    {
        if (shape is null) throw new ArgumentNullException(nameof(shape));
        if (MathF.Abs(scale - 1f) < 1e-6f) return shape;

        return shape switch
        {
            SphereShape s        => new SphereShape(s.Radius * scale),
            CapsuleShape c       => new CapsuleShape(c.Radius * scale, c.Length * scale),
            CylinderShape cy     => new CylinderShape(cy.Radius * scale, cy.Length * scale),
            BoxShape b           => new BoxShape(b.HalfExtents * scale),
            ConvexHullShape h    => ScaleConvexHull(h, scale),
            TriangleMeshShape m  => ScaleTriangleMesh(m, scale),
            CompoundShape co     => ScaleCompound(co, scale),
            _ => throw new NotSupportedException($"PhysicsShapeScale.Uniform: unsupported shape type {shape.GetType().Name}."),
        };
    }

    static ConvexHullShape ScaleConvexHull(ConvexHullShape h, float scale)
    {
        Vector3[] src = h.Points;
        var dst = new Vector3[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * scale;
        return new ConvexHullShape(dst);
    }

    static TriangleMeshShape ScaleTriangleMesh(TriangleMeshShape m, float scale)
    {
        Vector3[] src = m.Vertices;
        var dst = new Vector3[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * scale;
        return new TriangleMeshShape(dst, m.Indices);
    }

    static CompoundShape ScaleCompound(CompoundShape co, float scale)
    {
        CompoundChild[] src = co.Children;
        var dst = new CompoundChild[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            CompoundChild child = src[i];
            dst[i] = new CompoundChild(
                Uniform(child.Shape, scale),
                new Pose(child.Local.Position * scale, child.Local.Orientation));
        }
        return new CompoundShape(dst);
    }
}
```

- [ ] **Step 4: Run the new test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PhysicsShapeScaleTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Delegate `ChunkStatics.ScaleShape` to the public helper**

In `KhaozEngine.Terrain.Render3D/ChunkStatics.cs`, replace the whole `ScaleShape` method body and its four private helpers (`ScaleShape`, `ScaleConvexHull`, `ScaleTriangleMesh`, `ScaleCompound`, lines 57-109) with a single delegating method:

```csharp
        /// <summary>Return a new shape with all geometric dimensions scaled uniformly by
        /// <paramref name="scale"/> (delegates to the public <see cref="PhysicsShapeScale.Uniform"/> helper in
        /// KhaozEngine.Physics, the single home for per-placement shape scaling). A scale of 1 returns the
        /// original instance unchanged.</summary>
        /// <remarks>Limitation: non-uniform (per-axis) scale is not modelled (the scatter emits a single uniform
        /// <c>Scale</c> float per placement).</remarks>
        internal static PhysicsShape ScaleShape(PhysicsShape shape, float scale)
            => PhysicsShapeScale.Uniform(shape, scale);
```

Keep the existing `using KhaozEngine.Physics;` at the top (already present). `AddAll` still calls `ScaleShape(shape, p.Scale)` unchanged.

- [ ] **Step 6: Run the chunk-statics tests to verify the delegation kept behaviour**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ChunkStaticsTests"`
Expected: PASS (unchanged behaviour).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Physics/PhysicsShapeScale.cs KhaozEngine.Terrain.Render3D/ChunkStatics.cs KhaozEngine.Tests/Physics/PhysicsShapeScaleTests.cs
git commit -m "physics: public PhysicsShapeScale.Uniform helper; ChunkStatics delegates"
```

---

## Task 2: Box + Compound kinds in the `.coll` format

Refactor `PropCollisionFormat.Write`/`Read` to recurse over a shape (so a compound's children serialize), and add `KindBox=4` + `KindCompound=5`. Existing kinds 1/2/3 stay byte-identical.

**Files:**
- Modify: `KhaozEngine.Physics/PropCollisionFormat.cs`
- Test: `KhaozEngine.Tests/Physics/PropCollisionFormatTests.cs` (new)

**Interfaces:**
- Consumes: `Pose(Vector3 Position, Quaternion Orientation)`, `CompoundChild(PhysicsShape Shape, Pose Local)`, `CompoundShape(CompoundChild[] Children)`, `BoxShape(Vector3 HalfExtents)` (all existing in `KhaozEngine.Physics`).
- Produces: `PropCollisionFormat.Write`/`Read` round-trip `BoxShape` (kind 4) and `CompoundShape` (kind 5, children recursive, each with a 7-float local pose). `internal const byte KindBox = 4; internal const byte KindCompound = 5;`

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PropCollisionFormatTests
{
    static PhysicsShape RoundTrip(PhysicsShape shape)
    {
        using var ms = new MemoryStream();
        PropCollisionFormat.Write(shape, ms);
        ms.Position = 0;
        return PropCollisionFormat.Read(ms);
    }

    [Fact]
    public void Box_RoundTrips()
    {
        var box = new BoxShape(new Vector3(0.5f, 1.5f, 2.5f));
        var loaded = Assert.IsType<BoxShape>(RoundTrip(box));
        Assert.Equal(box.HalfExtents, loaded.HalfExtents);
    }

    [Fact]
    public void Compound_OfHullAndBoxAtNonIdentityPoses_RoundTrips()
    {
        var hull = new ConvexHullShape(new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(0,1,0), new Vector3(0,0,1) });
        Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(hull, new Pose(new Vector3(2, 0, 0), Quaternion.Identity)),
            new CompoundChild(new BoxShape(new Vector3(1, 2, 3)), new Pose(new Vector3(0, 5, 0), rot)),
        });

        var loaded = Assert.IsType<CompoundShape>(RoundTrip(compound));
        Assert.Equal(2, loaded.Children.Length);

        var c0 = loaded.Children[0];
        Assert.Equal(new Vector3(2, 0, 0), c0.Local.Position);
        Assert.Equal(4, Assert.IsType<ConvexHullShape>(c0.Shape).Points.Length);

        var c1 = loaded.Children[1];
        Assert.Equal(new Vector3(0, 5, 0), c1.Local.Position);
        Assert.Equal(rot, c1.Local.Orientation);
        Assert.Equal(new Vector3(1, 2, 3), Assert.IsType<BoxShape>(c1.Shape).HalfExtents);
    }

    [Fact]
    public void ByteIdentical_AcrossTwoWrites()
    {
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(new BoxShape(new Vector3(1, 1, 1)), new Pose(new Vector3(1, 2, 3), Quaternion.Identity)),
        });
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        PropCollisionFormat.Write(compound, a);
        PropCollisionFormat.Write(compound, b);
        Assert.Equal(a.ToArray(), b.ToArray());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropCollisionFormatTests"`
Expected: FAIL ("unsupported shape type BoxShape" / "unsupported shape kind") thrown by the current flat switch.

- [ ] **Step 3: Refactor Write/Read to recurse and add the new kinds**

In `KhaozEngine.Physics/PropCollisionFormat.cs`:

Add the two kind constants beside the existing ones:

```csharp
    internal const byte KindConvexHull = 1;
    internal const byte KindTriangleMesh = 2;
    internal const byte KindCylinder = 3;
    internal const byte KindBox = 4;
    internal const byte KindCompound = 5;
```

Replace the body of `Write(PhysicsShape shape, Stream stream)` so it writes the header once then recurses:

```csharp
    public static void Write(PhysicsShape shape, Stream stream)
    {
        if (shape == null) throw new ArgumentNullException(nameof(shape));
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        WriteShape(w, shape);
    }

    // Writes a single shape's kind byte + payload. Recurses for compound children. No magic/version here (the
    // top-level Write emits those once), so the existing kind 1/2/3 byte layout is unchanged.
    static void WriteShape(BinaryWriter w, PhysicsShape shape)
    {
        switch (shape)
        {
            case ConvexHullShape hull:
                w.Write(KindConvexHull);
                w.Write(hull.Points.Length);
                foreach (Vector3 p in hull.Points)
                { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                break;
            case CylinderShape cyl:
                w.Write(KindCylinder);
                w.Write(cyl.Radius);
                w.Write(cyl.Length);
                break;
            case TriangleMeshShape mesh:
                w.Write(KindTriangleMesh);
                w.Write(mesh.Vertices.Length);
                foreach (Vector3 v in mesh.Vertices)
                { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
                w.Write(mesh.Indices.Length);
                foreach (int idx in mesh.Indices)
                    w.Write(idx);
                break;
            case BoxShape box:
                w.Write(KindBox);
                w.Write(box.HalfExtents.X); w.Write(box.HalfExtents.Y); w.Write(box.HalfExtents.Z);
                break;
            case CompoundShape compound:
                w.Write(KindCompound);
                w.Write(compound.Children.Length);
                foreach (CompoundChild child in compound.Children)
                {
                    WritePose(w, child.Local);
                    WriteShape(w, child.Shape);
                }
                break;
            default:
                throw new NotSupportedException($"PropCollisionFormat.Write: unsupported shape type {shape.GetType().Name}");
        }
    }

    static void WritePose(BinaryWriter w, Pose pose)
    {
        w.Write(pose.Position.X); w.Write(pose.Position.Y); w.Write(pose.Position.Z);
        w.Write(pose.Orientation.X); w.Write(pose.Orientation.Y); w.Write(pose.Orientation.Z); w.Write(pose.Orientation.W);
    }
```

Replace the body of `Read(Stream stream)` so it reads the header once then recurses:

```csharp
    public static PhysicsShape Read(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        uint magic = r.ReadUInt32();
        if (magic != Magic)
            throw new InvalidOperationException(
                $"PropCollisionFormat: bad magic 0x{magic:X8} (expected 0x{Magic:X8}).");

        byte version = r.ReadByte();
        if (version != Version)
            throw new InvalidOperationException(
                $"PropCollisionFormat: unsupported version {version} (expected {Version}).");

        return ReadShape(r);
    }

    static PhysicsShape ReadShape(BinaryReader r)
    {
        byte kind = r.ReadByte();
        switch (kind)
        {
            case KindConvexHull:
            {
                int count = r.ReadInt32();
                var points = new Vector3[count];
                for (int i = 0; i < count; i++)
                    points[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                return new ConvexHullShape(points);
            }
            case KindCylinder:
            {
                float radius = r.ReadSingle();
                float length = r.ReadSingle();
                return new CylinderShape(radius, length);
            }
            case KindTriangleMesh:
            {
                int vCount = r.ReadInt32();
                var verts = new Vector3[vCount];
                for (int i = 0; i < vCount; i++)
                    verts[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int iCount = r.ReadInt32();
                var indices = new int[iCount];
                for (int i = 0; i < iCount; i++)
                    indices[i] = r.ReadInt32();
                return new TriangleMeshShape(verts, indices);
            }
            case KindBox:
            {
                float hx = r.ReadSingle(), hy = r.ReadSingle(), hz = r.ReadSingle();
                return new BoxShape(new Vector3(hx, hy, hz));
            }
            case KindCompound:
            {
                int childCount = r.ReadInt32();
                var children = new CompoundChild[childCount];
                for (int i = 0; i < childCount; i++)
                {
                    Pose local = ReadPose(r);
                    PhysicsShape child = ReadShape(r);
                    children[i] = new CompoundChild(child, local);
                }
                return new CompoundShape(children);
            }
            default:
                throw new InvalidOperationException(
                    $"PropCollisionFormat: unknown shape kind {kind}.");
        }
    }

    static Pose ReadPose(BinaryReader r)
    {
        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var orient = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return new Pose(pos, orient);
    }
```

(Remove the now-replaced flat switch bodies inside the old `Write`/`Read`. The `Read(string path)`, `LoadDirectory`, and `Load` helpers are unchanged.)

- [ ] **Step 4: Run the format tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropCollisionFormatTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the existing bake tests to confirm kinds 1/2/3 are byte-unchanged**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropCollisionBakeTests"`
Expected: PASS (existing convex-hull / cylinder / mesh round-trips unaffected).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Physics/PropCollisionFormat.cs KhaozEngine.Tests/Physics/PropCollisionFormatTests.cs
git commit -m "physics(coll): recursive write/read + Box (kind 4) + Compound (kind 5) shapes"
```

---

## Task 3: `GltfLoader.LoadGroups` (per-object groups)

The proxy bake needs one vertex group per authored Blender object. The existing `GltfLoader.Load` flattens the whole scene into one mesh; add a grouping load that returns one `GltfMesh` per logical glTF node-with-mesh, in logical-node order (deterministic), world-baked the same way `BuildRigid` bakes node transforms.

**Files:**
- Modify: `KhaozEngine.Render3D/Models/GltfLoader.cs:59` (add a method near `Load`)
- Test: `KhaozEngine.Tests/Render3D/GltfLoaderGroupsTests.cs` (new)

**Interfaces:**
- Produces: `public static IReadOnlyList<GltfMesh> GltfLoader.LoadGroups(string path)` - one `GltfMesh` per logical node-with-mesh (world-transform baked), plus any mesh referenced by no node loaded at identity, in stable logical-node-then-mesh order. Throws `InvalidOperationException` if there are no triangles at all.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class GltfLoaderGroupsTests
{
    // Two separate box-ish meshes placed at two node transforms => two groups, each carrying its own
    // node-transformed geometry. (One triangle per object is enough to assert grouping + placement.)
    static string WriteTwoObjectGlb()
    {
        VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(Vector3 p) =>
            new(new VertexPositionNormal(p, Vector3.UnitY));

        var a = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>("a");
        a.UsePrimitive(MaterialBuilder.CreateDefault()).AddTriangle(
            V(new Vector3(0, 0, 0)), V(new Vector3(1, 0, 0)), V(new Vector3(0, 0, 1)));

        var b = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>("b");
        b.UsePrimitive(MaterialBuilder.CreateDefault()).AddTriangle(
            V(new Vector3(0, 0, 0)), V(new Vector3(1, 0, 0)), V(new Vector3(0, 0, 1)));

        var scene = new SceneBuilder();
        scene.AddRigidMesh(a, Matrix4x4.Identity);
        scene.AddRigidMesh(b, Matrix4x4.CreateTranslation(10, 0, 0));

        string path = Path.Combine(Path.GetTempPath(), $"ke_groups_{System.Guid.NewGuid():N}.glb");
        scene.ToGltf2().SaveGLB(path);
        return path;
    }

    [Fact]
    public void LoadGroups_ReturnsOneGroupPerObject_NodeTransformBaked()
    {
        string path = WriteTwoObjectGlb();
        try
        {
            var groups = GltfLoader.LoadGroups(path);
            Assert.Equal(2, groups.Count);
            // Each group is its own object; the second is translated +10 in X by its node transform.
            float maxX0 = groups[0].Vertices.Max(v => v.Position.X);
            float maxX1 = groups[1].Vertices.Max(v => v.Position.X);
            Assert.True(maxX0 < 5f, $"group 0 should sit near origin, maxX={maxX0}");
            Assert.True(maxX1 > 9.9f, $"group 1 should be translated to ~x=10, maxX={maxX1}");
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GltfLoaderGroupsTests"`
Expected: FAIL to compile, "LoadGroups does not exist".

- [ ] **Step 3: Add `LoadGroups`**

In `KhaozEngine.Render3D/Models/GltfLoader.cs`, add right after the `Load` method (line 59). `AppendMeshCorners` and `MeshAssembler.Build` are already in this class/assembly:

```csharp
        /// <summary>Load a rigid glb/glTF as ONE <see cref="GltfMesh"/> per logical node-with-mesh (object),
        /// world-transform baked exactly as <see cref="Load"/> bakes it, in stable logical-node-then-mesh order.
        /// Unlike <see cref="Load"/> (which flattens the whole scene into one mesh) this preserves the authoring
        /// object boundaries, so an authored collision proxy modelled as separate convex blocks bakes one convex
        /// piece per block. A mesh referenced by no node is loaded once at identity (parity with <see cref="Load"/>).
        /// Deterministic group order (logical-node index, then any un-noded meshes) so a re-bake is reproducible.</summary>
        public static IReadOnlyList<GltfMesh> LoadGroups(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            var groups = new List<GltfMesh>();

            // One group per (node -> mesh), in logical-node order.
            foreach (var node in root.LogicalNodes)
            {
                if (node.Mesh is null) continue;
                var corners = new List<MeshCorner>();
                AppendMeshCorners(corners, node.Mesh, node.WorldMatrix);
                if (corners.Count > 0) groups.Add(MeshAssembler.Build(corners));
            }

            // Parity with Load: a mesh referenced by no node still contributes once, at identity.
            foreach (var mesh in root.LogicalMeshes)
            {
                bool placed = false;
                foreach (var node in root.LogicalNodes) { if (node.Mesh == mesh) { placed = true; break; } }
                if (placed) continue;
                var corners = new List<MeshCorner>();
                AppendMeshCorners(corners, mesh, Matrix4x4.Identity);
                if (corners.Count > 0) groups.Add(MeshAssembler.Build(corners));
            }

            if (groups.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);
            return groups;
        }
```

Confirm the file already has `using System.Collections.Generic;` (it uses `List<MeshCorner>` in `BuildRigid`). If not present, add it.

- [ ] **Step 4: Run the loader test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GltfLoaderGroupsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/GltfLoader.cs KhaozEngine.Tests/Render3D/GltfLoaderGroupsTests.cs
git commit -m "render3d(gltf): LoadGroups - one GltfMesh per logical node (proxy object boundaries)"
```

---

## Task 4: `PropCollisionBake.BakeProxy`

Bake an authored proxy (per-object groups) into a `CompoundShape` of convex hulls, normalized into the RENDER mesh's frame so it overlays the visual building exactly.

**Files:**
- Modify: `KhaozEngine.Render3D/Models/PropCollisionBake.cs`
- Test: `KhaozEngine.Tests/Physics/PropCollisionBakeProxyTests.cs` (new)

**Interfaces:**
- Consumes: `GltfLoader.LoadGroups` (Task 3) output shape (`IReadOnlyList<GltfMesh>`); `CompoundShape`/`CompoundChild`/`Pose`/`ConvexHullShape`; the existing private `HullFromPoints`.
- Produces: `public static CompoundShape PropCollisionBake.BakeProxy(GltfMesh renderRaw, float heightMeters, IReadOnlyList<GltfMesh> proxyGroups)` - derives the normalization transform (scale = `heightMeters` / raw render height, drop base to 0, recenter XZ on the raw render bounds) from `renderRaw`, applies it to every proxy group, hulls each group, and returns a compound with one `ConvexHullShape` child per group at `Pose.Identity` (geometry carries world position), in group order. A group too small/coplanar to hull is skipped (logged via the return having fewer children); if NO group survives, throws `InvalidOperationException`.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PropCollisionBakeProxyTests
{
    // A solid axis-aligned box group from min..max, as a GltfMesh (12 triangles). Reuses TestMeshes-style
    // construction kept local so the proxy bake test is self-contained.
    static GltfMesh Box(Vector3 min, Vector3 max)
    {
        Vector3[] c =
        {
            new(min.X,min.Y,min.Z), new(max.X,min.Y,min.Z), new(max.X,min.Y,max.Z), new(min.X,min.Y,max.Z),
            new(min.X,max.Y,min.Z), new(max.X,max.Y,min.Z), new(max.X,max.Y,max.Z), new(min.X,max.Y,max.Z),
        };
        int[] tris = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        var verts = new ModelVertex[c.Length];
        for (int i = 0; i < c.Length; i++) verts[i] = new ModelVertex { Position = c[i] };
        var idx = new uint[tris.Length];
        for (int i = 0; i < tris.Length; i++) idx[i] = (uint)tris[i];
        return new GltfMesh(verts, idx);
    }

    [Fact]
    public void BakeProxy_OneHullPerGroup_NormalizedIntoRenderFrame()
    {
        // Raw render mesh spans y 0..4 raw; declared height 8 m => scale x2, base already 0, XZ centred on 0.
        GltfMesh renderRaw = Box(new Vector3(-1, 0, -1), new Vector3(1, 4, 1));
        var groups = new List<GltfMesh>
        {
            Box(new Vector3(-1, 0, -1), new Vector3(1, 0.5f, 1)),   // floor slab
            Box(new Vector3(-1, 0, -1), new Vector3(-0.8f, 4, 1)),  // one wall
        };

        var compound = PropCollisionBake.BakeProxy(renderRaw, 8f, groups);
        Assert.Equal(2, compound.Children.Length);

        // Each child is a convex hull, placed at identity (geometry carries position).
        foreach (var child in compound.Children)
        {
            Assert.IsType<ConvexHullShape>(child.Shape);
            Assert.Equal(Quaternion.Identity, child.Local.Orientation);
        }

        // Render-frame normalization: scale 2x means the floor slab's top sits near y=1 (0.5 raw * 2).
        var floorPts = ((ConvexHullShape)compound.Children[0].Shape).Points;
        float maxY = float.MinValue;
        foreach (var p in floorPts) maxY = MathF.Max(maxY, p.Y);
        Assert.InRange(maxY, 0.9f, 1.1f);
    }

    [Fact]
    public void BakeProxy_IsDeterministic_ByteIdenticalReBake()
    {
        GltfMesh renderRaw = Box(new Vector3(-1, 0, -1), new Vector3(1, 4, 1));
        var groups = new List<GltfMesh>
        {
            Box(new Vector3(-1, 0, -1), new Vector3(1, 0.5f, 1)),
            Box(new Vector3(0.8f, 0, -1), new Vector3(1, 4, 1)),
        };

        byte[] Bake()
        {
            var compound = PropCollisionBake.BakeProxy(renderRaw, 8f, groups);
            using var ms = new MemoryStream();
            PropCollisionBake.Write(compound, ms);
            return ms.ToArray();
        }

        Assert.Equal(Bake(), Bake());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropCollisionBakeProxyTests"`
Expected: FAIL to compile, "BakeProxy does not exist".

- [ ] **Step 3: Add `BakeProxy`**

In `KhaozEngine.Render3D/Models/PropCollisionBake.cs`, add inside the `PropCollisionBake` class (after `Bake`). It mirrors `PropLoader.Normalize`'s formula deliberately (the proxy is authored in the render glb's raw frame, so applying the render mesh's exact normalization overlays them):

```csharp
        /// <summary>Bake an authored collision PROXY (one convex piece per object) into a
        /// <see cref="CompoundShape"/> of convex hulls, normalized into the RENDER mesh's frame so it overlays the
        /// visual building exactly. The proxy is authored in the render glb's RAW coordinate frame (import the
        /// render glb, model convex blocks on top, export the blocks only), so the bake derives the render mesh's
        /// normalization (uniform scale to <paramref name="heightMeters"/>, drop base to y=0, recenter XZ on the
        /// raw render bounds) from <paramref name="renderRaw"/> and applies that SAME transform to every proxy
        /// group. Each group is hulled (<see cref="HullFromPoints"/>, deterministic) into one
        /// <see cref="ConvexHullShape"/> child at identity local pose (the hull points carry world position,
        /// matching <see cref="BakeConvexHull"/>). Child order = group order, so a re-bake is byte-reproducible.
        /// A convex child can never trap the capsule (unique shortest exit), so a proxy of convex pieces retires
        /// the one-sided-mesh wedge/pin class while keeping floors/stairs/ledges/furniture standable. A group with
        /// fewer than 4 non-coplanar points is skipped; an all-empty proxy throws.</summary>
        public static CompoundShape BakeProxy(GltfMesh renderRaw, float heightMeters, IReadOnlyList<GltfMesh> proxyGroups)
        {
            if (renderRaw == null) throw new ArgumentNullException(nameof(renderRaw));
            if (proxyGroups == null) throw new ArgumentNullException(nameof(proxyGroups));

            // Render mesh normalization (same formula as PropLoader.Normalize): scale to height, base->0, XZ centre.
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);
            foreach (ModelVertex v in renderRaw.Vertices) { mn = Vector3.Min(mn, v.Position); mx = Vector3.Max(mx, v.Position); }
            float rawHeight = mx.Y - mn.Y;
            if (rawHeight <= 1e-6f) throw new InvalidOperationException("BakeProxy: render mesh has no measurable height.");
            float scale = heightMeters / rawHeight;
            float cx = (mn.X + mx.X) * 0.5f, cz = (mn.Z + mx.Z) * 0.5f, baseY = mn.Y;

            Vector3 Normalize(Vector3 p) => new((p.X - cx) * scale, (p.Y - baseY) * scale, (p.Z - cz) * scale);

            var children = new List<CompoundChild>(proxyGroups.Count);
            foreach (GltfMesh group in proxyGroups)
            {
                var pts = new List<Vector3>(group.Vertices.Length);
                foreach (ModelVertex v in group.Vertices) pts.Add(Normalize(v.Position));
                if (pts.Count < 4 || IsCoplanar(pts)) continue;     // not a solid convex piece, skip
                children.Add(new CompoundChild(HullFromPoints(pts), Pose.At(Vector3.Zero)));
            }
            if (children.Count == 0)
                throw new InvalidOperationException("BakeProxy: no proxy group produced a convex hull (all empty/coplanar).");
            return new CompoundShape(children.ToArray());
        }
```

`HullFromPoints`, `IsCoplanar`, and `Pose` are already in scope (`IsCoplanar` is `static` in this class; `Pose` via `using KhaozEngine.Physics;`).

- [ ] **Step 4: Run the proxy bake tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropCollisionBakeProxyTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/PropCollisionBake.cs KhaozEngine.Tests/Physics/PropCollisionBakeProxyTests.cs
git commit -m "render3d(propbake): BakeProxy - compound-of-convex-hulls building proxy in render frame"
```

---

## Task 5: `collisionProxy` manifest field

Add the optional `collisionProxy` field to `AssetEntry` + the manifest DTO + parser (resolved against the manifest directory like `file`/`heightmap`/`collisionShape`).

**Files:**
- Modify: `KhaozEngine.Render3D/Models/AssetManifest.cs`
- Test: `KhaozEngine.Tests/Render3D/AssetManifestTests.cs` (add a test)

**Interfaces:**
- Produces: `AssetEntry.CollisionProxy` (`string?`), set from manifest JSON key `collisionProxy`, path-resolved like `File`. The `AssetEntry` constructor gains a trailing optional `string? collisionProxy = null` parameter.

- [ ] **Step 1: Write the failing test**

Add to `KhaozEngine.Tests/Render3D/AssetManifestTests.cs`:

```csharp
    [Fact]
    public void Parse_ReadsAndResolvesCollisionProxy()
    {
        string json = """
        { "props": [ { "id": "blacksmith", "file": "blacksmith.glb", "heightMeters": 5.0,
                       "collisionProxy": "blacksmith_collision.glb" } ] }
        """;
        var manifest = AssetManifest.Parse(json, "/kit");
        var e = manifest.Find("blacksmith")!.Value;
        Assert.Equal(System.IO.Path.Combine("/kit", "blacksmith_collision.glb"), e.CollisionProxy);
    }

    [Fact]
    public void Parse_NoCollisionProxy_IsNull()
    {
        string json = """{ "props": [ { "id": "rock", "file": "rock.glb", "heightMeters": 1.0 } ] }""";
        var manifest = AssetManifest.Parse(json, "/kit");
        Assert.Null(manifest.Find("rock")!.Value.CollisionProxy);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AssetManifestTests"`
Expected: FAIL to compile, "AssetEntry does not contain CollisionProxy".

- [ ] **Step 3: Add the field**

In `KhaozEngine.Render3D/Models/AssetManifest.cs`:

Add the property + constructor parameter to `AssetEntry` (after `CollisionShape`, around line 40):

```csharp
        /// <summary>Path to an authored simplified collision PROXY glTF (<c>&lt;id&gt;_collision.glb</c>) for this
        /// prop, or null when none. When set, <c>ke-propbake</c> bakes the <c>.coll</c> from the proxy (a compound
        /// of convex pieces) instead of the full render mesh. Resolved against the manifest directory like
        /// <see cref="File"/>.</summary>
        public string? CollisionProxy { get; }
        public AssetEntry(string id, string file, float heightMeters, string source, string license,
                          ColliderShape? collider = null, bool surface = false, string? heightmap = null,
                          string? collisionShape = null, string? collisionProxy = null)
        {
            Id = id; File = file; HeightMeters = heightMeters; Source = source; License = license; Collider = collider;
            Surface = surface; Heightmap = heightmap; CollisionShape = collisionShape; CollisionProxy = collisionProxy;
        }
```

In `Parse`, resolve it and pass it (after the `collisionShape` resolution, around line 106):

```csharp
                string? collisionShape = string.IsNullOrWhiteSpace(p.CollisionShape) ? null : ResolveFile(p.CollisionShape!, baseDir);
                string? collisionProxy = string.IsNullOrWhiteSpace(p.CollisionProxy) ? null : ResolveFile(p.CollisionProxy!, baseDir);
                entries.Add(new AssetEntry(p.Id!, ResolveFile(p.File!, baseDir), p.HeightMeters,
                                           p.Source ?? "", p.License ?? "", ParseCollider(p.Id!, p.Collider),
                                           p.Surface, heightmap, collisionShape, collisionProxy));
```

In the `Dto.Entry` class, add the JSON property (after `CollisionShape`, around line 147):

```csharp
                [JsonPropertyName("collisionProxy")] public string? CollisionProxy { get; set; }
```

- [ ] **Step 4: Run the manifest tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AssetManifestTests"`
Expected: PASS (new + existing).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/AssetManifest.cs KhaozEngine.Tests/Render3D/AssetManifestTests.cs
git commit -m "render3d(manifest): optional collisionProxy field (authored building proxy glb)"
```

---

## Task 6: `PropBakePlan.ForProxy` + proxy-aware `ke-propbake`

Give `PropBakePlan` an overload that takes a pre-baked proxy collision shape and applies the surface-bake rule from the render mesh, then wire the tool to use it when `collisionProxy` is set.

**Files:**
- Modify: `KhaozEngine.Render3D/Models/PropBakePlan.cs`
- Modify: `KhaozEngine.PropSurface.Tool/Program.cs`
- Test: `KhaozEngine.Tests/Physics/PropBakePlanTests.cs` (add a test)

**Interfaces:**
- Consumes: `PropCollisionBake.BakeProxy` (Task 4); `PropSurfaceBake.IsWalkableSolid`/`Bake`.
- Produces: `public static PropBakePlan PropBakePlan.ForProxy(GltfMesh normalizedRender, PhysicsShape proxyColl)` - `Coll = proxyColl`; `Surface = IsWalkableSolid(normalizedRender) ? Bake(normalizedRender) : null`.

- [ ] **Step 1: Write the failing test**

Add to `KhaozEngine.Tests/Physics/PropBakePlanTests.cs`:

```csharp
    [Fact]
    public void ForProxy_UsesProxyColl_AndSurfaceFromRenderMesh()
    {
        GltfMesh render = TestMeshes.UnitIcosphere();   // walkable solid => gets a surface
        var proxy = new KhaozEngine.Physics.CompoundShape(new[]
        {
            new KhaozEngine.Physics.CompoundChild(
                new KhaozEngine.Physics.BoxShape(new System.Numerics.Vector3(1, 1, 1)),
                KhaozEngine.Physics.Pose.At(System.Numerics.Vector3.Zero)),
        });

        PropBakePlan plan = PropBakePlan.ForProxy(render, proxy);
        Assert.Same(proxy, plan.Coll);     // proxy compound is the collision shape
        Assert.NotNull(plan.Surface);      // walkable solid => surface baked from the render mesh
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropBakePlanTests"`
Expected: FAIL to compile, "ForProxy does not exist".

- [ ] **Step 3: Add the overload**

In `KhaozEngine.Render3D/Models/PropBakePlan.cs`, add a method to the `PropBakePlan` struct:

```csharp
        /// <summary>Plan the bakes for a prop whose collision is an AUTHORED proxy (a compound of convex pieces,
        /// already baked via <see cref="PropCollisionBake.BakeProxy"/>): the proxy is the collision shape, and the
        /// walkable top-surface heightmap is still derived from the normalized RENDER mesh (only for an
        /// <see cref="PropSurfaceBake.IsWalkableSolid"/> prop), so the surface contract is unchanged.</summary>
        public static PropBakePlan ForProxy(GltfMesh normalizedRender, PhysicsShape proxyColl) => new(
            proxyColl,
            PropSurfaceBake.IsWalkableSolid(normalizedRender) ? PropSurfaceBake.Bake(normalizedRender) : null);
```

Ensure the file has `using KhaozEngine.Physics;` (it already references `PhysicsShape` via the record's `Coll` field, so the using is present).

- [ ] **Step 4: Run the plan tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropBakePlanTests"`
Expected: PASS (new + existing).

- [ ] **Step 5: Wire the tool to use the proxy when present**

In `KhaozEngine.PropSurface.Tool/Program.cs`, replace the per-entry bake block (the `GltfMesh mesh; try { mesh = PropLoader.LoadProp(entry); } ...` through `PropBakePlan plan = PropBakePlan.For(mesh);`) with a proxy-aware version:

```csharp
    GltfMesh mesh;            // normalized render mesh (for the .surf + non-proxy .coll)
    PropBakePlan plan;
    try
    {
        if (!string.IsNullOrWhiteSpace(entry.CollisionProxy))
        {
            // Authored proxy: bake the .coll from the proxy (compound of convex pieces) in the render mesh's frame.
            GltfMesh renderRaw = GltfLoader.Load(entry.File);
            mesh = PropLoader.Normalize(renderRaw, entry.HeightMeters);
            var proxyGroups = GltfLoader.LoadGroups(entry.CollisionProxy!);
            PhysicsShape proxyColl = PropCollisionBake.BakeProxy(renderRaw, entry.HeightMeters, proxyGroups);
            plan = PropBakePlan.ForProxy(mesh, proxyColl);
        }
        else
        {
            mesh = PropLoader.LoadProp(entry);
            plan = PropBakePlan.For(mesh);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"  ! {entry.Id}: {ex.Message}"); continue; }
```

Add `case CompoundShape => "compound",` to the `collKind` switch so the tool reports the proxy kind:

```csharp
    string collKind = plan.Coll switch
    {
        CompoundShape     => "compound",
        TriangleMeshShape => "triangle-mesh",
        CylinderShape     => "cylinder",
        ConvexHullShape   => "convex-hull",
        _                 => "shape",
    };
```

(The tool already has `using KhaozEngine.Physics;` and `using KhaozEngine.Render3D;`.)

- [ ] **Step 6: Build the tool to confirm it compiles**

Run: `dotnet build KhaozEngine.PropSurface.Tool/KhaozEngine.PropSurface.Tool.csproj -c Debug`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Render3D/Models/PropBakePlan.cs KhaozEngine.PropSurface.Tool/Program.cs KhaozEngine.Tests/Physics/PropBakePlanTests.cs
git commit -m "propbake(tool): proxy-aware bake - ForProxy plan + collisionProxy path"
```

---

## Task 7: Real-proxy goal-metric test + `blacksmith_proxy.coll` fixture

This is the crux verification. Author the blacksmith proxy in Blender, bake it through the new pipeline, commit the baked `.coll` as a real fixture, and add a scan harness asserting ~0 wedges / stairs climb / standable. The blacksmith proxy authored here is REUSED in Phase 2 (authored once).

**Files:**
- Create: `KhaozEngine.Tests/Physics/Fixtures/blacksmith_proxy.coll` (committed binary)
- Modify: `KhaozEngine.Tests/Physics/RealBuildingCollisionTests.cs` (add the scan harness)
- Working content (committed to Ruinborne in Phase 2, but authored now): `blacksmith_collision.glb`

**Interfaces:**
- Consumes: `BepuPhysicsWorld`, `CharacterMovement.Step`, `PropCollisionFormat.Read`, `PhysicsShapeScale.Uniform`.

- [ ] **Step 1: Author the blacksmith proxy in Blender (via the Blender MCP)**

Import `~/Ruinborne/Ruinborne.Client/assets/buildings/blacksmith.glb`. Model the proxy as separate convex objects ON TOP of it, in the render glb's raw frame:
- 4 exterior wall slabs as thin boxes, leaving a GAP for the doorway (so 4-6 wall-segment objects, not a closed box).
- A floor slab box covering the interior footprint.
- The anvil, forge, and workbench each as their own box object (kept: standable + bumpable, per the spec - NOT dropped).
- If the blacksmith has steps/a raised platform, a single wedge object for the ramp.
- Do NOT model the roof, eaves, dormers, or thin trim (dropped).
Delete the render mesh. Export `blacksmith_collision.glb` (proxy objects only) to a scratch path. Keep it for reuse.

- [ ] **Step 2: Bake the proxy `.coll` through the new pipeline**

Add a temporary manifest entry (or a tiny scratch manifest) with `"collisionProxy": "blacksmith_collision.glb"` for the blacksmith and run the built tool:

Run: `dotnet run --project KhaozEngine.PropSurface.Tool/KhaozEngine.PropSurface.Tool.csproj -- <scratch>/blacksmith.manifest.json`
Expected: `+ blacksmith: baked ... + blacksmith.coll (compound)`.

Copy the baked `blacksmith.coll` to `KhaozEngine.Tests/Physics/Fixtures/blacksmith_proxy.coll`.

- [ ] **Step 3: Write the scan-harness test**

Add to `KhaozEngine.Tests/Physics/RealBuildingCollisionTests.cs` (a sibling of the existing `building_with_eaves` harness; it scales the proxy by `RuinborneWorld.BuildingScale` = 1.5 and the live tuning, matching the alpha):

```csharp
    static CompoundShape BlacksmithProxy()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Physics", "Fixtures", "blacksmith_proxy.coll");
        var shape = (CompoundShape)PropCollisionFormat.Read(path);
        return (CompoundShape)PhysicsShapeScale.Uniform(shape, Scale);   // Scale = 1.5 (BuildingScale)
    }

    // Settle a capsule dropped at (sx, sz), then test whether it can both WALK and JUMP away. A "wedge" is a spot
    // the settled capsule can do neither from. Returns true when wedged.
    static bool IsWedged(IPhysicsWorld world, MoveState settled)
    {
        // Try to walk out in 8 compass directions; if any nets real horizontal progress, not wedged.
        for (int d = 0; d < 8; d++)
        {
            float yaw = d * MathF.PI / 4f;
            var s = settled;
            Vector2 start = new(s.Position.X, s.Position.Z);
            for (int i = 0; i < 60; i++)
                s = CharacterMovement.Step(s, new MoveCommand(new Vector2(0, -1f), run: true, cameraYaw: yaw, jump: false),
                                           1f / 60f, Flat, Tuning, null, world);
            if (Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), start) > 0.5f) return false;
        }
        // Try to jump out (up + each direction); if it leaves the start cell or clearly rises and moves, not wedged.
        for (int d = 0; d < 8; d++)
        {
            float yaw = d * MathF.PI / 4f;
            var s = settled;
            Vector2 start = new(s.Position.X, s.Position.Z);
            for (int i = 0; i < 90; i++)
                s = CharacterMovement.Step(s, new MoveCommand(new Vector2(0, -1f), run: true, cameraYaw: yaw, jump: (i % 45 == 0)),
                                           1f / 60f, Flat, Tuning, null, world);
            if (Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), start) > 0.5f) return false;
        }
        return true;
    }

    [Fact]
    public void ScanningInsideAndAroundTheBlacksmithProxy_FindsNoWedges()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(BlacksmithProxy(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var wedges = new List<string>();
        int total = 0;
        for (float sx = -6f; sx <= 6f; sx += 0.75f)
        for (float sz = -6f; sz <= 6f; sz += 0.75f)
        {
            var start = new MoveState { Position = new Vector3(sx, 6f, sz), Grounded = false };
            // Settle: drop and let it come to rest (or slide off) over ~2 s.
            var s = start;
            for (int i = 0; i < 120; i++)
                s = CharacterMovement.Step(s, new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false),
                                           1f / 60f, Flat, Tuning, null, world);
            if (!s.Grounded) continue;          // never settled here (in a wall volume); not a stand spot
            total++;
            if (IsWedged(world, s)) wedges.Add($"({sx:F1},{sz:F1})");
        }

        Assert.True(wedges.Count == 0,
            $"capsule wedged (cannot walk OR jump out) at {wedges.Count}/{total} settled spots in/around the blacksmith " +
            $"proxy: {string.Join("  ", wedges)}");
    }
```

Add the needed usings to the file if missing: `System.Collections.Generic`, `KhaozEngine.Physics` (for `CompoundShape`, `PhysicsShapeScale`). `Scale`, `Tuning`, `Flat` already exist in the class.

- [ ] **Step 4: Run the scan test**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RealBuildingCollisionTests"`
Expected: PASS. If wedges are reported, return to Step 1 and adjust the proxy (thicken/merge the offending pieces, widen the doorway gap, simplify a tight corner), re-bake, re-copy the fixture, re-run. Iterate until 0 wedges.

- [ ] **Step 5: Add a stairs/standable assertion (only if the blacksmith has a raised area; otherwise add a flat-floor standable assertion)**

```csharp
    [Fact]
    public void StandingOnTheBlacksmithFloor_HoldsTheCapsule()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(BlacksmithProxy(), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // Drop at the interior centre; it must come to rest grounded on the floor slab (not fall through).
        var s = new MoveState { Position = new Vector3(0f, 6f, 0f), Grounded = false };
        for (int i = 0; i < 180; i++)
            s = CharacterMovement.Step(s, new MoveCommand(Vector2.Zero, false, 0f, false), 1f / 60f, Flat, Tuning, null, world);
        Assert.True(s.Grounded, $"capsule should stand on the interior floor, ended grounded={s.Grounded} y={s.Position.Y:F2}");
        Assert.True(s.Position.Y > 0.1f, $"floor should hold the capsule above the world base, y={s.Position.Y:F2}");
    }
```

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~RealBuildingCollisionTests"`
Expected: PASS.

- [ ] **Step 6: Commit (fixture + tests)**

```bash
git add KhaozEngine.Tests/Physics/Fixtures/blacksmith_proxy.coll KhaozEngine.Tests/Physics/RealBuildingCollisionTests.cs
git commit -m "tests(physics): real blacksmith proxy fixture + wedge-scan goal-metric harness"
```

---

## Task 8: Release 8.11.0 (engine ritual)

Bump the version, write the changelog, sweep docs, delete the roadmap entry, pack to local-feed. HOLD the tag + push for explicit user confirmation.

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>`)
- Modify: `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/PHYSICS-PIPELINE.md`, `docs/USING-KHAOZENGINE.md`, `KhaozEngine.Physics/README.md`, `KhaozEngine.Render3D/README.md`

- [ ] **Step 1: Re-check for a concurrent version bump**

```bash
git fetch
git tag | sort -V | tail -5
grep KhaozEngineVersion Directory.Build.props
```
If `origin/main` already advanced to 8.11.0 or tagged `v8.11.0`, take the next FREE version everywhere below.

- [ ] **Step 2: Run the FULL suite green before bumping**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all pass (~2496+ the new tests).

- [ ] **Step 3: Bump the version**

In `Directory.Build.props`, change `<KhaozEngineVersion>8.10.0</KhaozEngineVersion>` to `<KhaozEngineVersion>8.11.0</KhaozEngineVersion>`.

- [ ] **Step 4: Add the CHANGELOG entry (newest-first, tight first sentence, no em-dashes/semicolons)**

Prepend to `CHANGELOG.md`:

```markdown
## 8.11.0

Additive: buildings can bake a SEPARATE simplified collision PROXY (a compound of convex pieces) distinct from the
full-detail render mesh, so a capsule never wedges in cluttered interior geometry while still standing on floors,
stairs, ledges, and furniture. Opt-in per prop; anything without a proxy bakes exactly as before. Consumers re-bake
their building `.coll` to adopt.

- **`KhaozEngine.Physics` (format + scaling).** `PropCollisionFormat` gains two stable wire kinds, `KindBox = 4`
  (half-extents) and `KindCompound = 5` (child count, then per child a 7-float local pose + a nested shape,
  recursive). `Write`/`Read` now recurse over a shape; existing kinds 1/2/3 are byte-identical and `Version` stays
  1 (the new kinds are additive). New public `PhysicsShapeScale.Uniform(shape, scale)` scales every shape kind
  including Box and Compound (child geometry plus each child's local-pose position; orientation unchanged), the
  single home for per-placement uniform shape scaling.
- **`KhaozEngine.Render3D` (bake).** `GltfLoader.LoadGroups(path)` loads one `GltfMesh` per logical glTF
  node-with-mesh (object boundaries preserved, deterministic order), and `PropCollisionBake.BakeProxy(renderRaw,
  heightMeters, proxyGroups)` hulls each proxy object into one convex child of a `CompoundShape`, normalized into
  the render mesh's frame so the proxy overlays the building exactly. A convex child can never trap the capsule
  (unique shortest exit), so a proxy of convex pieces retires the one-sided-mesh wedge/pin problem class
  structurally rather than by accumulating resolver invariants. The 8.8.1 gravity-authoritative resolver invariant
  is unchanged and composes with the proxy.
- **Manifest + `ke-propbake`.** A new optional `collisionProxy` manifest field (`<id>_collision.glb`) points at the
  authored proxy. When set, the tool bakes the `.coll` from the proxy (reported kind `compound`); otherwise the
  full-mesh / hull / cylinder bake is unchanged. The `.surf` walkable-top heightmap is still baked from the render
  mesh. `PropBakePlan.ForProxy` carries the surface rule.
- **Internal.** `KhaozEngine.Terrain.Render3D.ChunkStatics.ScaleShape` now delegates to `PhysicsShapeScale.Uniform`
  (the old per-type mirror is retired).
- Coverage: format Box + nested-Compound round-trip + byte-identity; `LoadGroups` per-object grouping with node
  transforms; `BakeProxy` one-hull-per-group render-frame normalization + deterministic re-bake; `PhysicsShapeScale`
  Box/Compound; and a REAL baked blacksmith proxy fixture scanned for wedges (no spot where the capsule can neither
  walk nor jump out) with the interior floor standable. Full suite green.
```

- [ ] **Step 5: Update the three guard-checked doc-version strings**

- `docs/CONSUMERS.md`: "Engine current version" -> 8.11.0.
- `docs/ROADMAP.md`: "Current released version: **8.11.0**".
- `README.md`: the `<PackageReference>` example version -> 8.11.0.

Run: `bash scripts/check-doc-versions.sh`
Expected: passes (all three match 8.11.0).

- [ ] **Step 6: Delete the ROADMAP "Collision-proxy bake pipeline" entry**

In `docs/ROADMAP.md`, remove the whole `**Collision-proxy bake pipeline (structural, unscheduled).** ...` paragraph (it has shipped; the detail now lives in the changelog).

- [ ] **Step 7: Full doc sweep (the non-guard-checked docs)**

- `docs/PHYSICS-PIPELINE.md`: in the "Static bodies" side-flow, note buildings can bake a compound-of-convex PROXY (`BakeProxy`) when a `collisionProxy` is authored, in addition to the full `TriangleMesh`; the seam shape list already includes Compound.
- `KhaozEngine.Physics/README.md`: document the new format kinds (Box/Compound) and `PhysicsShapeScale.Uniform`.
- `KhaozEngine.Render3D/README.md`: document `GltfLoader.LoadGroups` and `PropCollisionBake.BakeProxy`.
- `docs/USING-KHAOZENGINE.md`: add a short "Building collision proxies" section (author a `<id>_collision.glb` of separate convex blocks, add `collisionProxy` to the manifest, re-bake).

Mechanical check:

```bash
grep -rn "BakeProxy\|LoadGroups\|PhysicsShapeScale\|collisionProxy\|KindCompound\|KindBox" --include=*.md . CLAUDE.md
```
Confirm every doc that should mention each new name does, and no stale doc contradicts it.

- [ ] **Step 8: Pack to local-feed**

```bash
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
```
Expected: all packable projects pack at 8.11.0.

- [ ] **Step 9: Commit the release**

```bash
git add -A
git commit -m "release(8.11.0): collision-proxy bake pipeline (compound-of-convex building proxies)"
```

- [ ] **Step 10: STOP. Ask the user to confirm tag + push.**

Do NOT `git tag v8.11.0` or push yet. Surface to the user: the engine is committed + packed to local-feed; tagging publishes to GitHub Packages for all 4 games. Ask whether to (a) merge to `main` + tag + push now, or (b) hold and batch. Per the engine ritual, the merge-to-main / tag / push is held for explicit confirmation.

---

# Phase 2: Ruinborne consumer (after the engine 8.11.0 is packed to local-feed)

> Do NOT start Phase 2 until Task 8 has packed 8.11.0 to `~/KhaozEngine/local-feed`. Work in a Ruinborne worktree per its repo rules.

## Task 9: Author the remaining 6 proxies + manifest + re-bake

**Files:**
- Create: `Ruinborne.Client/assets/buildings/{inn,bell_tower,house_1,house_2,house_3,well}_collision.glb` (+ reuse `blacksmith_collision.glb` from Task 7)
- Modify: `Ruinborne.Client/assets/buildings/buildings.manifest.json`
- Re-baked: `Ruinborne.Client/assets/buildings/*.coll`

- [ ] **Step 1: Author each proxy in Blender (Blender MCP)**

For each of inn, bell_tower, house_1, house_2, house_3, well: import the render `.glb`, model separate convex blocks (exterior walls with door gaps, floor slab, any stairs as a wedge, substantial interior furniture as boxes), drop roof/eaves/dormers/trim, delete the render mesh, export `<id>_collision.glb` beside the source. Copy the already-authored `blacksmith_collision.glb` from Task 7 into the kit dir.

- [ ] **Step 2: Add `collisionProxy` to the manifest**

In `Ruinborne.Client/assets/buildings/buildings.manifest.json`, add `"collisionProxy": "<id>_collision.glb"` to all 7 building entries.

- [ ] **Step 3: Re-bake the building `.coll`**

Run (pin the engine tool version or run from the engine worktree):
`dotnet run --project ~/KhaozEngine/KhaozEngine.PropSurface.Tool/KhaozEngine.PropSurface.Tool.csproj -- ~/Ruinborne/Ruinborne.Client/assets/buildings/buildings.manifest.json`
Expected: each building reports `(compound)`.

- [ ] **Step 4: Commit the content**

```bash
git add Ruinborne.Client/assets/buildings/
git commit -m "buildings: authored collision proxies + re-baked compound .coll"
```

## Task 10: Pin 8.11.0, delegate ScaleShape, verify all 7

**Files:**
- Modify: `Ruinborne.Core/RuinbornePhysics.cs`
- Modify: `Directory.Build.props` (Ruinborne)
- Test: `Ruinborne.Tests/RuinbornePhysicsTests.cs`

- [ ] **Step 1: Pin the engine version**

In Ruinborne's `Directory.Build.props`, set `<KhaozEngineVersion>` (or the equivalent pin) to `8.11.0`. Restore against the engine local-feed.

- [ ] **Step 2: Delegate `RuinbornePhysics.ScaleShape` to the public helper**

In `Ruinborne.Core/RuinbornePhysics.cs`, replace the private `ScaleShape` + its `ScalePoints`/`ScaleMesh` helpers with a delegation (it now also handles compound, which the old mirror threw on):

```csharp
        // Per-placement uniform scale of a shape via the public engine helper (KhaozEngine.Physics 8.11.0+),
        // which handles every shape kind including the new building-proxy compound-of-convex. Replaces the old
        // local mirror that only handled cylinder/hull/mesh and threw on compound.
        static PhysicsShape ScaleShape(PhysicsShape shape, float scale)
            => PhysicsShapeScale.Uniform(shape, scale);
```

- [ ] **Step 3: Run the wedge-scan over all 7 re-baked buildings**

Extend `Ruinborne.Tests/RuinbornePhysicsTests.cs` with a parametrized test that, for each building id, loads its re-baked `.coll`, scales by `BuildingScale`, adds it as a static, and runs the same wedge-scan harness (settle + walk-out + jump-out) asserting 0 wedges and a standable interior floor. Use the live Ruinborne tuning.

```csharp
    [Theory]
    [InlineData("blacksmith")]
    [InlineData("inn")]
    [InlineData("bell_tower")]
    [InlineData("house_1")]
    [InlineData("house_2")]
    [InlineData("house_3")]
    [InlineData("well")]
    public void EachBuildingProxy_HasNoWedges(string id)
    {
        // load <id>.coll, scale by RuinborneWorld.BuildingScale, add to a BepuPhysicsWorld, run the settle +
        // walk-out + jump-out scan (mirroring the engine RealBuildingCollisionTests harness), assert 0 wedges.
        // (Full body mirrors the engine harness; see KhaozEngine RealBuildingCollisionTests for the helper shape.)
    }
```

Run: `dotnet test Ruinborne.Tests/Ruinborne.Tests.csproj --filter "FullyQualifiedName~RuinbornePhysicsTests"`
Expected: PASS for all 7. Iterate on any proxy that reports wedges (Task 9 Step 1).

- [ ] **Step 4: Full Ruinborne build + test**

Run: `dotnet test Ruinborne.Tests/Ruinborne.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Ruinborne.Core/RuinbornePhysics.cs Directory.Build.props Ruinborne.Tests/RuinbornePhysicsTests.cs
git commit -m "physics: adopt engine 8.11.0 collision-proxy bake; ScaleShape delegates; scan all 7 buildings"
```

- [ ] **Step 6: Hand back for the alpha deploy**

Surface to the user: all 7 building proxies re-baked + verified, engine 8.11.0 pinned. The alpha deploy is the user's action (provide the one-click boot command for a local windowed playtest from the Ruinborne worktree first).

---

## Self-Review

**1. Spec coverage:**
- Representation (compound-of-convex) -> Tasks 2, 4. Covered.
- Authoring workflow -> Task 7 Step 1, Task 9 Step 1. Covered.
- Bake path (LoadGroups + BakeProxy + render-frame normalization) -> Tasks 3, 4. Covered.
- Format extension (Box + Compound) -> Task 2. Covered.
- Public scale helper + delegation -> Tasks 1, 10. Covered.
- Manifest field + tool -> Tasks 5, 6. Covered.
- Testing incl. real-proxy scan -> Tasks 4, 7, 10. Covered.
- Determinism/byte-identity -> Task 4 (deterministic re-bake), Global Constraints. Covered.
- Rollout (all 7 buildings, hold tag) -> Tasks 8, 9, 10. Covered.
- Out of scope items -> not implemented (correct).

**2. Placeholder scan:** Task 9/10 Blender authoring + the Ruinborne `[Theory]` body are described concretely but not full code, because they are content authoring + a mirror of the engine harness body (Task 7 Step 3 has the full harness code to copy). Acceptable: the engine harness is the single source of the scan code; the Ruinborne test references it rather than re-deriving a different version.

**3. Type consistency:** `PhysicsShapeScale.Uniform(PhysicsShape, float)`, `PropCollisionFormat` kinds 4/5, `GltfLoader.LoadGroups(string) -> IReadOnlyList<GltfMesh>`, `PropCollisionBake.BakeProxy(GltfMesh, float, IReadOnlyList<GltfMesh>) -> CompoundShape`, `PropBakePlan.ForProxy(GltfMesh, PhysicsShape) -> PropBakePlan`, `AssetEntry.CollisionProxy` are used consistently across tasks. `Scale`/`Tuning`/`Flat` reused from the existing `RealBuildingCollisionTests`. Consistent.
