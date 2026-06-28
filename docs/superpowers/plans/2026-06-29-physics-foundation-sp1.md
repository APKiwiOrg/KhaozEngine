# Physics Foundation (SP1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a dependency-free `KhaozEngine.Physics` seam with a `KhaozEngine.Physics.Bepu` backend (BepuPhysics v2), fold the kinematic character controller onto real capsule-vs-mesh collision so the capsule rests on a prop's actual 3D surface (the domed-rock fix) and buildings get real interiors, and expose raycast/sweep/overlap queries. Props/buildings become real 3D shapes; terrain stays analytic; no dynamic bodies yet.

**Architecture:** A seam interface (`IPhysicsWorld`) with value-type shapes/poses/queries lives in a new dependency-free `KhaozEngine.Physics` leaf (Foundation line). A `KhaozEngine.Physics.Bepu` package is the only assembly that references the `BepuPhysics` NuGet and implements the seam. `Locomotion.CharacterMovement` gains a new `Step` overload that resolves horizontal motion by collide-and-slide against the seam and computes vertical support by a downward capsule probe against the seam, replacing the 2D `WorldColliders`/`WorldSurfaces` path. The netcode stack (`PlayerMoveSimulator`/`WorldServer`/`WorldClient`/`ShardedWorldServer`/`PlayerMovementSystem`) threads one `IPhysicsWorld` instead of the 2D collider/surface sets; the streamer adds/removes per-prop statics on chunk load/unload. The backend is swappable: if the determinism/AOT gate fails, only the backend body changes, behind the same seam.

**Tech Stack:** C# net10.0, BepuPhysics v2 (pure-managed, Apache 2.0), xUnit, System.Numerics. MonoGame-free.

## Global Constraints

- **Target / language:** `net10.0`, `Nullable=enable`, `ImplicitUsings=disable`, `LangVersion=latest` (inherited from `Directory.Build.props`).
- **One shared version line:** every packable project sets `<Version>$(KhaozEngineVersion)</Version>`; the bump happens once at the end of the batch (Task 9), not per task. Current value `7.70.0`. This batch breaks public ctors, so it is a **major** bump (`8.0.0`).
- **Seam is dependency-free:** `KhaozEngine.Physics` references only `System.Numerics` (+ `KhaozEngine.Primitives` if a shared type is wanted). `BepuPhysics` types appear ONLY inside `KhaozEngine.Physics.Bepu`. No consumer references Bepu directly.
- **Determinism model:** authoritative server + client prediction/reconcile (NOT lockstep). The Bepu `Step` runs single-threaded (null thread dispatcher) with fixed solver iteration counts so a single binary is run-to-run deterministic; cross-architecture bit-exactness is NOT required.
- **Every new behaviour ships a headless test** in `KhaozEngine.Tests` (xUnit). No real GPU/window in tests.
- **No em-dashes** in any code comment, doc, commit message, or changelog (use commas/periods/parens).
- **CHANGELOG.md entry in the SAME commit as the version bump** (Task 9), newest-first, one-line summary first sentence. Full doc sweep on the same commit (README catalog + repo-layout, root `CLAUDE.md` package map, `docs/CONSUMERS.md`, `docs/USING-KHAOZENGINE.md`, `docs/ROADMAP.md` item #2, `docs/CONSUMERS.md` "Engine current version", `README.md` `<PackageReference>` example).
- **`mkdir -p local-feed`** before any `dotnet restore`; pack with `dotnet pack -c Release -o ./local-feed` (Task 9 only).
- **Commit subjects:** conventional `area(scope): summary`; on the version-bump commit use the new version as scope (`physics(8.0.0): ...`).
- **Run the suite** with `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add `--filter` for a single test).

## File Structure

New:
- `KhaozEngine.Physics/` — the seam. `IPhysicsWorld.cs`, `PhysicsShape.cs` (shape hierarchy), `Pose.cs`, `PhysicsMaterial.cs`, `QueryFilter.cs`, `Handles.cs` (`StaticHandle`), `Queries.cs` (`RayHit`/`SweepHit`), `KhaozEngine.Physics.csproj`.
- `KhaozEngine.Physics.Bepu/` — `BepuPhysicsWorld.cs` (the `IPhysicsWorld` impl), `ShapeFactory.cs` (seam shape -> Bepu shape), `HitHandlers.cs` (ray/sweep callback structs), `KhaozEngine.Physics.Bepu.csproj`.
- `KhaozEngine.Physics.AotProbe/` — throwaway console for the NativeAOT gate (`IsPackable=false`, `PublishAot=true`).

Modified:
- `KhaozEngine.Locomotion/CharacterMovement.cs` (new `Step` overload + `SlideAndSupport` helper), `KhaozEngine.Locomotion.csproj` (add Physics seam ref; drop Collision in Task 8).
- `KhaozEngine.Render3D/Models/PropCollisionBake.cs` (new), `AssetManifest.cs` (`CollisionShape` field), `PropCollisionLoader.cs` (new).
- `KhaozEngine.PropSurface.Tool/Program.cs` (also bake the collision shape).
- `KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs` (add/remove statics).
- `KhaozEngine.NetWorld/PlayerMoveSimulator.cs`, `PlayerMovementSystem.cs`, `WorldServer.cs`, `WorldClient.cs`, `ShardedWorldServer.cs` (swap `WorldColliders?`/`WorldSurfaces?` -> `IPhysicsWorld?`).
- `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`, `KhaozEngine.Server/*.csproj`, `KhaozEngine.Game3D/*.csproj`, `KhaozEngine.Tests/*.csproj`, `KhaozEngine.slnx` (project membership).
- Samples under `MmoServerSample`, `NetworkedWalkSample`, `TerrainWalkSample`, `NetworkedWalkServer` (ctor call sites).

---

### Task 1: `KhaozEngine.Physics` seam package (contract + value types)

Pure our-code, no Bepu. Establishes the contract every later task builds on.

**Files:**
- Create: `KhaozEngine.Physics/Pose.cs`, `PhysicsMaterial.cs`, `QueryFilter.cs`, `Handles.cs`, `Queries.cs`, `PhysicsShape.cs`, `IPhysicsWorld.cs`, `KhaozEngine.Physics.csproj`
- Modify: `KhaozEngine.slnx`, `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`, `KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- Test: `KhaozEngine.Tests/Physics/SeamTypesTests.cs`

**Interfaces:**
- Produces:
  - `KhaozEngine.Physics.Pose` (readonly record struct): `Pose(Vector3 Position, Quaternion Orientation)`, static `Pose At(Vector3 p)` (identity orientation).
  - `KhaozEngine.Physics.PhysicsMaterial` (readonly record struct): `(float Friction, float Restitution)`, static `Default = new(1f, 0f)`.
  - `KhaozEngine.Physics.QueryFilter` (readonly record struct): `(uint Layers)`, static `All = default` (0 = match all).
  - `KhaozEngine.Physics.StaticHandle` (readonly record struct): `(int Value)`.
  - `KhaozEngine.Physics.RayHit` / `SweepHit` (readonly record structs): `(float Distance, Vector3 Point, Vector3 Normal, StaticHandle Body)`.
  - `KhaozEngine.Physics.PhysicsShape` (abstract) with sealed subclasses `SphereShape(float Radius)`, `CapsuleShape(float Radius, float Length)`, `BoxShape(Vector3 HalfExtents)`, `CylinderShape(float Radius, float Length)`, `ConvexHullShape(Vector3[] Points)`, `TriangleMeshShape(Vector3[] Vertices, int[] Indices)`, `CompoundShape(CompoundChild[] Children)` where `CompoundChild(PhysicsShape Shape, Pose Local)`.
  - `KhaozEngine.Physics.IPhysicsWorld : IDisposable` with: `StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)`, `void RemoveStatic(StaticHandle handle)`, `void Step(float dt)`, `bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default)`, `bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default)`, `bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)`.
  - `CapsuleShape.Length` is the cylindrical segment length: total capsule height = `Length + 2*Radius` (matches BepuPhysics' capsule convention, axis = local Y).

- [ ] **Step 1: Create the project file**

Create `KhaozEngine.Physics/KhaozEngine.Physics.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Physics</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <Description>Dependency-free 3D physics seam for KhaozEngine: IPhysicsWorld (static bodies + Step + raycast/sweep/overlap queries), value-type shapes (sphere/capsule/box/cylinder/convex hull/triangle mesh/compound), poses, materials, and query filters. The render-free, headless contract the character controller and netcode resolve against; backends (e.g. KhaozEngine.Physics.Bepu) implement it. Depends only on System.Numerics. Authoritative + client-predicted, not lockstep.</Description>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the value-type and contract source files**

Create `KhaozEngine.Physics/Pose.cs`:

```csharp
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>A rigid transform: world position + orientation. The pose of a static body or a query shape.</summary>
public readonly record struct Pose(Vector3 Position, Quaternion Orientation)
{
    /// <summary>A pose at <paramref name="position"/> with identity orientation.</summary>
    public static Pose At(Vector3 position) => new(position, Quaternion.Identity);
}
```

Create `KhaozEngine.Physics/PhysicsMaterial.cs`:

```csharp
namespace KhaozEngine.Physics;

/// <summary>Surface response for a body. Friction and restitution are mostly exercised by dynamic
/// bodies (sub-project 2); static-world collision in sub-project 1 carries the default.</summary>
public readonly record struct PhysicsMaterial(float Friction, float Restitution)
{
    /// <summary>Full friction, no bounce.</summary>
    public static readonly PhysicsMaterial Default = new(1f, 0f);
}
```

Create `KhaozEngine.Physics/QueryFilter.cs`:

```csharp
namespace KhaozEngine.Physics;

/// <summary>Layer mask for queries. <c>Layers == 0</c> (the default) matches every body.</summary>
public readonly record struct QueryFilter(uint Layers)
{
    /// <summary>Matches all layers.</summary>
    public static readonly QueryFilter All = default;
}
```

Create `KhaozEngine.Physics/Handles.cs`:

```csharp
namespace KhaozEngine.Physics;

/// <summary>An opaque handle to a static body added via <see cref="IPhysicsWorld.AddStatic"/>.</summary>
public readonly record struct StaticHandle(int Value);
```

Create `KhaozEngine.Physics/Queries.cs`:

```csharp
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The nearest ray intersection.</summary>
public readonly record struct RayHit(float Distance, Vector3 Point, Vector3 Normal, StaticHandle Body);

/// <summary>The nearest swept-shape time of impact.</summary>
public readonly record struct SweepHit(float Distance, Vector3 Point, Vector3 Normal, StaticHandle Body);
```

Create `KhaozEngine.Physics/PhysicsShape.cs`:

```csharp
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>A collision shape. Convex primitives plus a triangle mesh (for non-convex buildings/interiors)
/// and a compound (disjoint child shapes). A backend converts these to its own representation.</summary>
public abstract class PhysicsShape { }

/// <summary>A sphere of the given radius, centred on the body pose.</summary>
public sealed class SphereShape(float radius) : PhysicsShape
{
    public float Radius { get; } = radius;
}

/// <summary>An upright capsule (axis = local Y). <paramref name="length"/> is the cylindrical segment
/// length, so the total height is <c>Length + 2*Radius</c>.</summary>
public sealed class CapsuleShape(float radius, float length) : PhysicsShape
{
    public float Radius { get; } = radius;
    public float Length { get; } = length;
}

/// <summary>A box with the given half-extents in local space.</summary>
public sealed class BoxShape(Vector3 halfExtents) : PhysicsShape
{
    public Vector3 HalfExtents { get; } = halfExtents;
}

/// <summary>A cylinder (axis = local Y) of the given radius and length.</summary>
public sealed class CylinderShape(float radius, float length) : PhysicsShape
{
    public float Radius { get; } = radius;
    public float Length { get; } = length;
}

/// <summary>A convex hull of the given local-space points (solid props: rocks, trunks).</summary>
public sealed class ConvexHullShape(Vector3[] points) : PhysicsShape
{
    public Vector3[] Points { get; } = points;
}

/// <summary>A static triangle mesh (non-convex: buildings, interiors). Indices are triples.</summary>
public sealed class TriangleMeshShape(Vector3[] vertices, int[] indices) : PhysicsShape
{
    public Vector3[] Vertices { get; } = vertices;
    public int[] Indices { get; } = indices;
}

/// <summary>One child of a <see cref="CompoundShape"/>, placed at a local pose.</summary>
public readonly record struct CompoundChild(PhysicsShape Shape, Pose Local);

/// <summary>Several disjoint child shapes treated as one static body.</summary>
public sealed class CompoundShape(CompoundChild[] children) : PhysicsShape
{
    public CompoundChild[] Children { get; } = children;
}
```

Create `KhaozEngine.Physics/IPhysicsWorld.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The physics world the character controller and netcode resolve against: a set of static
/// bodies plus stepping and raycast/sweep/overlap queries. Headless and backend-agnostic. Authoritative
/// on the server and re-run identically in client prediction. Sub-project 1 has static bodies only;
/// dynamic bodies arrive in sub-project 2 behind the same interface.</summary>
public interface IPhysicsWorld : IDisposable
{
    /// <summary>Add a static body. Returns a handle for later removal.</summary>
    StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null);

    /// <summary>Remove a static body previously added.</summary>
    void RemoveStatic(StaticHandle handle);

    /// <summary>Advance the simulation by <paramref name="dt"/> seconds. Near-trivial while there are no
    /// dynamic bodies, but present so sub-project 2 drops in without an interface change.</summary>
    void Step(float dt);

    /// <summary>Cast a ray; returns the nearest hit. Used for ledge detection, jump targeting,
    /// line-of-sight, and downward ground probes.</summary>
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default);

    /// <summary>Sweep a capsule along <paramref name="direction"/>; returns the nearest time of impact.</summary>
    bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default);

    /// <summary>If the capsule at <paramref name="pose"/> overlaps any static body, output the minimum
    /// translation (direction * depth) that separates it; returns false when clear.</summary>
    bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv);
}
```

- [ ] **Step 3: Register the project in the solution and umbrellas**

In `KhaozEngine.slnx`, add (alphabetical context, after the `KhaozEngine.Particles` entry):

```xml
  <Project Path="KhaozEngine.Physics/KhaozEngine.Physics.csproj" />
```

In `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`, add to the `<ItemGroup>` of project references (after the `Persistence` line, keeping alphabetical):

```xml
    <ProjectReference Include="../KhaozEngine.Physics/KhaozEngine.Physics.csproj" />
```

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add to the project-reference `<ItemGroup>`:

```xml
    <ProjectReference Include="../KhaozEngine.Physics/KhaozEngine.Physics.csproj" />
```

- [ ] **Step 4: Write the failing test**

Create `KhaozEngine.Tests/Physics/SeamTypesTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class SeamTypesTests
{
    [Fact]
    public void Pose_At_IsIdentityOrientation()
    {
        Pose p = Pose.At(new Vector3(1f, 2f, 3f));
        Assert.Equal(new Vector3(1f, 2f, 3f), p.Position);
        Assert.Equal(Quaternion.Identity, p.Orientation);
    }

    [Fact]
    public void CapsuleShape_TotalHeight_IsLengthPlusTwoRadii()
    {
        var c = new CapsuleShape(radius: 0.4f, length: 1.0f);
        Assert.Equal(0.4f, c.Radius);
        Assert.Equal(1.0f, c.Length);
        // total height = length + 2*radius = 1.8 (a 1.8 m character)
        Assert.Equal(1.8f, c.Length + 2f * c.Radius, 3);
    }

    [Fact]
    public void Material_Default_IsFullFrictionNoBounce()
    {
        Assert.Equal(1f, PhysicsMaterial.Default.Friction);
        Assert.Equal(0f, PhysicsMaterial.Default.Restitution);
    }

    [Fact]
    public void Shapes_ExposeTheirData()
    {
        var hull = new ConvexHullShape(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ });
        Assert.Equal(4, hull.Points.Length);
        var mesh = new TriangleMeshShape(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, new[] { 0, 1, 2 });
        Assert.Equal(3, mesh.Indices.Length);
    }
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SeamTypesTests"`
Expected: FAIL to compile until Steps 1-3 are in place; once they are, PASS. (If it fails to build with "type or namespace KhaozEngine.Physics not found", the project references in Step 3 are missing.)

- [ ] **Step 6: Make it pass and commit**

Re-run the command in Step 5.
Expected: PASS (4 tests).

```bash
git add KhaozEngine.Physics KhaozEngine.slnx KhaozEngine.Foundation/KhaozEngine.Foundation.csproj KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Tests/Physics/SeamTypesTests.cs
git commit -m "physics(seam): KhaozEngine.Physics IPhysicsWorld + shapes/pose/queries"
```

---

### Task 2: Determinism + NativeAOT gate (minimal Bepu probe)

The cheap de-risk. Prove BepuPhysics steps headlessly, is run-to-run deterministic on one binary, and NativeAOT-publishes, BEFORE building the full backend. If any sub-step fails, STOP and switch Task 3's backend body to a hand-built capsule-vs-mesh + triangle-BVH implementation (the seam, controller, authoring, streaming, netcode, and tests in later tasks are unchanged).

**Files:**
- Create: `KhaozEngine.Physics.AotProbe/KhaozEngine.Physics.AotProbe.csproj`, `KhaozEngine.Physics.AotProbe/Program.cs`
- Modify: `KhaozEngine.slnx`
- Test: `KhaozEngine.Tests/Physics/BepuDeterminismGateTests.cs`, and add the `BepuPhysics` package to `KhaozEngine.Tests`

**Interfaces:**
- Consumes: nothing from earlier tasks (uses raw `BepuPhysics`).
- Produces: a recorded PASS/FAIL on (a) headless step, (b) run-to-run determinism, (c) NativeAOT publish. No engine API.

- [ ] **Step 1: Add BepuPhysics to the test project**

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add to the package `<ItemGroup>` (confirm the latest stable 2.x on nuget.org at install time and pin whatever you install; `2.4.0` is the known-good baseline):

```xml
    <PackageReference Include="BepuPhysics" Version="2.4.0" />
```

Run `mkdir -p local-feed && dotnet restore KhaozEngine.Tests/KhaozEngine.Tests.csproj` and confirm it restores.

- [ ] **Step 2: Write the determinism gate test**

Create `KhaozEngine.Tests/Physics/BepuDeterminismGateTests.cs`. This builds a tiny scene (one static box), casts a ray and sweeps a capsule, steps single-threaded, and asserts a second identical run on the same binary produces identical results. Use the BepuPhysics API of the version you installed; the structure:

```csharp
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Gate: BepuPhysics must step headlessly and be run-to-run deterministic on one binary.
// (Minimal narrow-phase/pose-integrator callbacks are required by Simulation.Create; copy the
// no-gravity, no-op variants from the BepuPhysics "Getting Started" docs for the installed version.)
public class BepuDeterminismGateTests
{
    private static (float rayT, float sweepT) RunOnce()
    {
        using var pool = new BufferPool();
        // NoNarrowPhase + NoPoseIntegrator callbacks: see Step 2 note. Single-threaded (null dispatcher).
        Simulation sim = Simulation.Create(pool, new GateNarrowPhaseCallbacks(),
            new GatePoseIntegratorCallbacks(), new SolveDescription(velocityIterationCount: 8, substepCount: 1));

        TypedIndex box = sim.Shapes.Add(new Box(2f, 2f, 2f));
        sim.Statics.Add(new StaticDescription(new Vector3(0f, 0f, 5f), box));

        sim.Timestep(1f / 60f, null);

        // Ray from origin toward +Z must hit the box front face at ~4 (5 - half-depth 1).
        var rayHandler = new GateRayHandler();
        sim.RayCast(Vector3.Zero, Vector3.UnitZ, 100f, ref rayHandler);

        // Sweep a capsule toward +Z; record time of impact.
        var capsule = new Capsule(0.4f, 1.0f);
        var sweepHandler = new GateSweepHandler();
        sim.Sweep(capsule, new RigidPose(Vector3.Zero), new BodyVelocity(Vector3.UnitZ), 100f, pool, ref sweepHandler);

        float rt = rayHandler.HitT, st = sweepHandler.HitT;
        sim.Dispose();
        return (rt, st);
    }

    [Fact]
    public void Bepu_StepsHeadlessly_AndIsRunToRunDeterministic()
    {
        var a = RunOnce();
        var b = RunOnce();
        Assert.True(a.rayT > 0f, "ray should hit the static box");
        Assert.Equal(a.rayT, b.rayT);     // exact: same binary, same inputs
        Assert.Equal(a.sweepT, b.sweepT);
    }
}
```

Add the small callback/handler structs (`GateNarrowPhaseCallbacks`, `GatePoseIntegratorCallbacks`, `GateRayHandler`, `GateSweepHandler`) in the same file, copied from the BepuPhysics demos for the installed version (no gravity, no-op narrow phase, hit handlers that record the nearest `t`). These same handler patterns are reused in Task 3.

- [ ] **Step 3: Run the determinism gate**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BepuDeterminismGateTests"`
Expected: PASS. If the two runs differ, set `substepCount`/`velocityIterationCount` explicitly and ensure the null (single-threaded) dispatcher is used; if they still differ, the gate has FAILED, record it and switch Task 3 to the hand-built backend.

- [ ] **Step 4: Create the NativeAOT probe**

Create `KhaozEngine.Physics.AotProbe/KhaozEngine.Physics.AotProbe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BepuPhysics" Version="2.4.0" />
  </ItemGroup>
</Project>
```

Create `KhaozEngine.Physics.AotProbe/Program.cs` that builds the same tiny scene, steps once, casts one ray, and prints the hit distance (reuse the callback/handler structs inline). The point is that it links and runs under AOT, not the numeric result.

Add to `KhaozEngine.slnx`:

```xml
  <Project Path="KhaozEngine.Physics.AotProbe/KhaozEngine.Physics.AotProbe.csproj" />
```

- [ ] **Step 5: Run the NativeAOT publish gate**

Run: `dotnet publish KhaozEngine.Physics.AotProbe/KhaozEngine.Physics.AotProbe.csproj -r osx-arm64 -c Release`
Then run the produced binary: `./KhaozEngine.Physics.AotProbe/bin/Release/net10.0/osx-arm64/publish/KhaozEngine.Physics.AotProbe`
Expected: publishes with no trim/AOT errors and prints a hit distance near `4`. If AOT publish errors on Bepu, record the gate as FAILED for AOT and note it for the iOS reach decision (does not block the desktop/server SP1, but is the signal the iOS target needs the hand-built backend).

- [ ] **Step 6: Commit the gate**

```bash
git add KhaozEngine.Physics.AotProbe KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Tests/Physics/BepuDeterminismGateTests.cs
git commit -m "physics(gate): Bepu headless determinism + NativeAOT publish probe"
```

---

### Task 3: `KhaozEngine.Physics.Bepu` backend implementing the seam

**Files:**
- Create: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.cs`, `ShapeFactory.cs`, `HitHandlers.cs`, `KhaozEngine.Physics.Bepu.csproj`
- Modify: `KhaozEngine.slnx`, `KhaozEngine.Server/KhaozEngine.Server.csproj`, `KhaozEngine.Game3D/KhaozEngine.Game3D.csproj`, `KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- Test: `KhaozEngine.Tests/Physics/BepuPhysicsWorldTests.cs`

**Interfaces:**
- Consumes: `KhaozEngine.Physics` seam (Task 1); the Bepu callback/handler patterns validated in Task 2.
- Produces: `KhaozEngine.Physics.Bepu.BepuPhysicsWorld : IPhysicsWorld` with a public ctor `BepuPhysicsWorld()`. A factory entry-point consumers use: `new BepuPhysicsWorld()` returns an empty world. Maps each `StaticHandle.Value` to a Bepu `StaticHandle`.

- [ ] **Step 1: Create the backend project**

Create `KhaozEngine.Physics.Bepu/KhaozEngine.Physics.Bepu.csproj` (backend-with-NuGet pattern, mirrors `Netcode.LiteNetLib`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Physics.Bepu</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <Description>BepuPhysics v2 backend for the KhaozEngine.Physics seam. Implements IPhysicsWorld over a single-threaded, deterministic BepuPhysics Simulation: static bodies from the seam shapes, Step, and raycast/sweep/overlap queries. Pure managed (no native libraries); the only assembly that references BepuPhysics, so consumers depend on the dependency-free seam and pick this backend like Netcode.LiteNetLib or WorldStore.Sqlite.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BepuPhysics" Version="2.4.0" />
    <ProjectReference Include="../KhaozEngine.Physics/KhaozEngine.Physics.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the shape factory and hit handlers**

Create `KhaozEngine.Physics.Bepu/ShapeFactory.cs`: a static `TypedIndex Add(Simulation sim, BufferPool pool, PhysicsShape shape)` that switches on the seam shape type and adds the corresponding Bepu shape (`Sphere`, `Capsule`, `Box`, `Cylinder`, `ConvexHull` via `ConvexHullHelper.CreateShape`, `Mesh` from triangles via the pool, `Compound`/`BigCompound` for compounds). For `TriangleMeshShape`, build a `Buffer<Triangle>` from the vertices/indices and create a `Mesh`. Confirm exact constructor signatures against the installed BepuPhysics 2.x.

Create `KhaozEngine.Physics.Bepu/HitHandlers.cs`: the `IRayHitHandler` and `ISweepHitHandler` structs that record the nearest hit (distance, normal, the Bepu collidable's `StaticHandle`), plus a filter check against `QueryFilter` (in SP1 the filter matches all, so the check is a pass-through but is wired for SP2). Reuse the patterns proven in Task 2.

- [ ] **Step 3: Write the failing backend test**

Create `KhaozEngine.Tests/Physics/BepuPhysicsWorldTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class BepuPhysicsWorldTests
{
    [Fact]
    public void Raycast_HitsAStaticBox()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 5f)));
        world.Step(1f / 60f);

        bool hit = world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out RayHit h);
        Assert.True(hit);
        Assert.Equal(4f, h.Distance, 2);                 // 5 - half-depth 1
        Assert.True(Vector3.Dot(h.Normal, -Vector3.UnitZ) > 0.9f);
    }

    [Fact]
    public void SweepCapsule_StopsBeforeAWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 2f, 0.5f)), Pose.At(new Vector3(0f, 1f, 5f)));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        bool hit = world.SweepCapsule(cap, Pose.At(new Vector3(0f, 1f, 0f)), Vector3.UnitZ, 100f, out SweepHit h);
        Assert.True(hit);
        Assert.True(h.Distance > 0f && h.Distance < 5f);  // contacts before the wall plane at z=4.5
    }

    [Fact]
    public void ComputePenetration_PushesOutOfAnOverlap()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        var cap = new CapsuleShape(0.4f, 1.0f);
        // capsule centre inside the box -> must report a separating translation
        bool overlap = world.ComputePenetration(cap, Pose.At(new Vector3(0.5f, 0f, 0f)), out Vector3 mtv);
        Assert.True(overlap);
        Assert.True(mtv.Length() > 0f);
    }

    [Fact]
    public void RemoveStatic_StopsHits()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        StaticHandle h = world.AddStatic(new BoxShape(new Vector3(1f, 1f, 1f)), Pose.At(new Vector3(0f, 0f, 5f)));
        world.RemoveStatic(h);
        world.Step(1f / 60f);
        Assert.False(world.Raycast(Vector3.Zero, Vector3.UnitZ, 100f, out _));
    }
}
```

- [ ] **Step 4: Implement `BepuPhysicsWorld`**

Create `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.cs`. Hold a `Simulation` (created single-threaded with fixed `SolveDescription`), a `BufferPool`, and an `int -> Bepu StaticHandle` map for the opaque `StaticHandle`. Implement:
- `AddStatic`: `ShapeFactory.Add` -> `Simulation.Statics.Add(new StaticDescription(pose.Position, pose.Orientation, shapeIndex))`; store and return a seam `StaticHandle(nextId++)`.
- `RemoveStatic`: look up the Bepu handle and `Simulation.Statics.Remove`.
- `Step`: `Simulation.Timestep(dt, null)` (null dispatcher = single-threaded deterministic).
- `Raycast`: `Simulation.RayCast(origin, direction, maxDistance, ref handler)`; translate the recorded hit to `RayHit`.
- `SweepCapsule`: `Simulation.Sweep(new Capsule(capsule.Radius, capsule.Length), new RigidPose(pose.Position, pose.Orientation), new BodyVelocity(direction), maxDistance, pool, ref handler)`; translate to `SweepHit`.
- `ComputePenetration`: query the broad phase for statics overlapping the capsule's bounding box, then for each run the convex-vs-shape collision test (collision batcher / `CollisionTask`) and accumulate the deepest separating translation; return the summed/deepest MTV. Confirm the exact overlap-query + manifold API against the installed BepuPhysics 2.x (the demos' "query" samples are the reference). A pragmatic first implementation: cast short rays / a zero-distance sweep around the capsule and use the contact normal*depth; refine if a test shows it is insufficient.
- `Dispose`: dispose the `Simulation` and `BufferPool`.

Reference the BepuPhysics "Getting Started" and "Queries" demo code for the exact method signatures at the installed version.

- [ ] **Step 5: Register and run**

Add to `KhaozEngine.slnx`:

```xml
  <Project Path="KhaozEngine.Physics.Bepu/KhaozEngine.Physics.Bepu.csproj" />
```

Add the backend to `KhaozEngine.Server` and `KhaozEngine.Game3D` umbrella csproj `<ItemGroup>`s:

```xml
    <ProjectReference Include="../KhaozEngine.Physics.Bepu/KhaozEngine.Physics.Bepu.csproj" />
```

Add to `KhaozEngine.Tests/KhaozEngine.Tests.csproj`:

```xml
    <ProjectReference Include="../KhaozEngine.Physics.Bepu/KhaozEngine.Physics.Bepu.csproj" />
```

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BepuPhysicsWorldTests"`
Expected: PASS (4 tests). Iterate on the `ComputePenetration` approach until the overlap test passes.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Physics.Bepu KhaozEngine.slnx KhaozEngine.Server KhaozEngine.Game3D KhaozEngine.Tests
git commit -m "physics(bepu): BepuPhysicsWorld backend - statics, step, raycast/sweep/penetration"
```

---

### Task 4: Fold the character controller onto the seam (collide-and-slide + support probe)

Add a NEW `CharacterMovement.Step` overload that takes `IPhysicsWorld?` and resolves horizontal motion by collide-and-slide and vertical support by a downward capsule probe. The existing `WorldColliders`/`WorldSurfaces` overloads stay until Task 8, so the build stays green.

**Files:**
- Modify: `KhaozEngine.Locomotion/CharacterMovement.cs`, `KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj`
- Test: `KhaozEngine.Tests/Physics/ControllerOnPhysicsTests.cs`

**Interfaces:**
- Consumes: `IPhysicsWorld`, `CapsuleShape`, `Pose`, `SweepHit` (Task 1); `MoveState`, `MoveCommand`, `MoveTuning` (existing).
- Produces:
  - `MoveState CharacterMovement.Step(in MoveState state, in MoveCommand cmd, float dt, Func<float,float,float> groundHeight, in MoveTuning tuning, Func<float,float,Vector3>? groundNormal, IPhysicsWorld? world, Func<float,float,Vector2>? clampXz = null)`.
  - private `static (float x, float z, float supportY) SlideAndSupport(IPhysicsWorld world, Vector3 fromCenter, float desiredX, float desiredZ, in MoveTuning tuning, float terrainSupport)` — depenetrate, sweep-and-slide horizontally, then downward-probe for support.
  - `CapsuleShape CharacterMovement.CapsuleFor(in MoveTuning tuning)` — `new CapsuleShape(tuning.CapsuleRadius, 2*tuning.CapsuleHalfHeight - 2*tuning.CapsuleRadius)`.

- [ ] **Step 1: Add the seam reference to Locomotion**

In `KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj`, add to the project-reference `<ItemGroup>`:

```xml
    <ProjectReference Include="../KhaozEngine.Physics/KhaozEngine.Physics.csproj" />
```

- [ ] **Step 2: Write the failing test (the dome-flank regression + walls + doorway)**

Create `KhaozEngine.Tests/Physics/ControllerOnPhysicsTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class ControllerOnPhysicsTests
{
    private static readonly MoveTuning Tuning = new(WalkSpeed: 3f, RunSpeed: 6f, CapsuleHalfHeight: 0.9f,
        MaxSlopeRadians: 0.9f);   // ~51 degrees

    private static float Flat(float x, float z) => 0f;

    [Fact]
    public void Capsule_RestsOnDomeFlank_WithoutPenetrating()
    {
        // A dome: a large sphere half-buried at the origin (top at y=1).
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new SphereShape(2f), Pose.At(new Vector3(0f, -1f, 0f)));
        world.Step(1f / 60f);

        // Stand on the rising flank (off-centre) and let several ticks settle.
        var state = new MoveState { Position = new Vector3(1.0f, 2.0f, 0f), Grounded = false };
        var cmd = new MoveCommand(Vector2.Zero, Run: false, CameraYaw: 0f, Jump: false);
        for (int i = 0; i < 30; i++)
            state = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world);

        // The capsule must not be sunk into the sphere: distance from sphere centre to capsule
        // segment >= sphereRadius + capsuleRadius - skin.
        float feetToCentre = state.Position.Y - Tuning.CapsuleHalfHeight - (-1f);   // sphere centre y = -1
        Assert.True(state.Position.Y > 0.2f, $"capsule should rest on the dome, was y={state.Position.Y}");
        Assert.True(state.Grounded);
    }

    [Fact]
    public void Capsule_CannotWalkThroughAWall()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(3f, 2f, 0.25f)), Pose.At(new Vector3(0f, 1f, 2f)));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), Run: false, CameraYaw: 0f, Jump: false); // walk +... forward is -Z
        // forward in this basis is -Z, so push toward the wall by yawing 180.
        var toward = new MoveCommand(new Vector2(0f, -1f), Run: false, CameraYaw: 0f, Jump: false);
        for (int i = 0; i < 120; i++)
            state = CharacterMovement.Step(state, toward, 1f / 60f, Flat, Tuning, groundNormal: null, world);

        Assert.True(state.Position.Z < 1.7f, $"blocked before the wall, was z={state.Position.Z}");
    }

    [Fact]
    public void Capsule_WalksThroughADoorwayGap()
    {
        // Two wall segments with a gap in the middle (a doorway).
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(1.5f, 2f, 0.25f)), Pose.At(new Vector3(-2.0f, 1f, 2f)));
        world.AddStatic(new BoxShape(new Vector3(1.5f, 2f, 0.25f)), Pose.At(new Vector3(2.0f, 1f, 2f)));
        world.Step(1f / 60f);

        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var toward = new MoveCommand(new Vector2(0f, -1f), Run: false, CameraYaw: 0f, Jump: false);
        for (int i = 0; i < 180; i++)
            state = CharacterMovement.Step(state, toward, 1f / 60f, Flat, Tuning, groundNormal: null, world);

        Assert.True(state.Position.Z > 3f, $"should pass through the gap, was z={state.Position.Z}");
    }

    [Fact]
    public void NullWorld_IsTerrainOnly_Unchanged()
    {
        var state = new MoveState { Position = new Vector3(0f, 0.9f, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, -1f), Run: false, CameraYaw: 0f, Jump: false);
        var moved = CharacterMovement.Step(state, cmd, 1f / 60f, Flat, Tuning, groundNormal: null, world: null);
        Assert.True(moved.Position.Z < state.Position.Z);  // moved forward freely
        Assert.Equal(0.9f, moved.Position.Y, 3);           // on flat ground + half-height
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ControllerOnPhysicsTests"`
Expected: FAIL to compile (no `Step(... IPhysicsWorld ...)` overload yet).

- [ ] **Step 4: Implement the overload + helper**

In `KhaozEngine.Locomotion/CharacterMovement.cs`, add `using KhaozEngine.Physics;` and the new overload + helpers. The overload mirrors the existing vertical `Step` but `Support` and the horizontal resolve go through the world:

```csharp
/// <summary>Vertical-physics step resolving collision against a 3D <see cref="IPhysicsWorld"/> (capsule
/// vs prop/building meshes): horizontal motion is collide-and-slid against the world, the support height
/// the capsule rests on is the higher of the terrain and a downward capsule probe (so it rests on a
/// prop's real surface, e.g. a domed rock, and on building floors), and step-up uses a sweep probe.
/// <paramref name="world"/> null = terrain only (unchanged). The same world + math runs on server and
/// client.</summary>
public static MoveState Step(in MoveState state, in MoveCommand cmd, float dt,
    Func<float, float, float> groundHeight, in MoveTuning tuning,
    Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world,
    Func<float, float, Vector2>? clampXz = null)
{
    if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

    CapsuleShape capsule = CapsuleFor(tuning);

    // Support height under (x,z): the higher of terrain and the prop surface a downward capsule probe finds.
    float Support(float sx, float sz)
    {
        float g = groundHeight(sx, sz);
        if (world is null) return g;
        float p = ProbeSupport(world, capsule, sx, sz, g, tuning);
        return p > g ? p : g;
    }

    // 1. Desired horizontal target from camera-relative input (slope-gated), WITHOUT collision yet.
    float speedScale = state.Grounded ? 1f : tuning.AirControl;
    (float dx, float dz) = DesiredHorizontal(state.Position.X, state.Position.Z, cmd, dt, tuning, groundNormal, speedScale);

    // 1b. Collide-and-slide the capsule (at the carried centre Y) from current to desired against the world.
    float x = dx, z = dz;
    if (world is not null)
    {
        Vector3 from = state.Position;
        Vector3 to = new(dx, state.Position.Y, dz);
        Vector3 slid = SlideHorizontal(world, capsule, from, to, tuning);
        x = slid.X; z = slid.Z;
    }
    if (clampXz is not null) { Vector2 c = clampXz(x, z); x = c.X; z = c.Y; }

    // 1c. Step-up gate: while grounded, a support rise taller than StepHeight is a wall (revert).
    float supBefore = Support(state.Position.X, state.Position.Z);
    if (state.Grounded && Support(x, z) - supBefore > tuning.StepHeight) { x = state.Position.X; z = state.Position.Z; }

    // 2-5. Identical vertical physics to the existing overload, using Support(x,z) for groundY.
    bool jumpRequested = cmd.Jump || state.JumpBufferRemaining > 0f;
    float jumpBuffer = cmd.Jump ? tuning.JumpBuffer : MathF.Max(0f, state.JumpBufferRemaining - dt);
    float vVel = state.VerticalVelocity - tuning.Gravity * dt;
    if (vVel < -tuning.MaxFallSpeed) vVel = -tuning.MaxFallSpeed;
    float y = state.Position.Y + vVel * dt;
    float groundY = Support(x, z) + tuning.CapsuleHalfHeight;
    bool grounded; float tSinceGround;
    if (vVel <= 0f && (y <= groundY || (state.Grounded && y <= groundY + tuning.GroundedEpsilon)))
    { y = groundY; vVel = 0f; grounded = true; tSinceGround = 0f; }
    else { grounded = false; tSinceGround = state.TimeSinceGrounded + dt; }
    if (jumpRequested && (grounded || tSinceGround <= tuning.CoyoteTime))
    { vVel = tuning.JumpSpeed; grounded = false; tSinceGround = tuning.CoyoteTime + dt; jumpBuffer = 0f; }

    return new MoveState
    {
        Position = new Vector3(x, y, z), VerticalVelocity = vVel, Grounded = grounded,
        TimeSinceGrounded = tSinceGround, JumpBufferRemaining = jumpBuffer,
    };
}

/// <summary>The upright capsule for a tuning: radius + cylindrical length so total height = 2*halfHeight.</summary>
public static CapsuleShape CapsuleFor(in MoveTuning tuning)
    => new(tuning.CapsuleRadius, MathF.Max(0.01f, 2f * tuning.CapsuleHalfHeight - 2f * tuning.CapsuleRadius));
```

Add the three private helpers in the same file:
- `DesiredHorizontal(x, z, cmd, dt, tuning, groundNormal, speedScale) -> (float x, float z)`: the camera-relative move + normalized diagonals + slope gate from the existing `ResolveHorizontal`, but WITHOUT the collider/clamp blocks (collision now happens in `SlideHorizontal`).
- `SlideHorizontal(world, capsule, from, to, tuning) -> Vector3`: depenetrate `from` (`ComputePenetration`, apply MTV), then iterate up to 4 times: `remaining = horizontalComponent(to - current)`; if `SweepCapsule(capsule, Pose.At(current), normalize(remaining), |remaining|, out hit)` then advance `current += dir * max(0, hit.Distance - skin)` and slide `remaining -= dot(remaining, hit.Normal) * hit.Normal`; else `current += remaining; break`. Keep Y = `from.Y`. Use `skin = 0.02f`.
- `ProbeSupport(world, capsule, x, z, terrainY, tuning) -> float`: sweep the capsule downward from a start a little above the head (`startCenterY = max(terrainY, currentFeet) + capsule height`) by a bounded distance; if it hits, the support surface Y = `hitCenterY - tuning.CapsuleHalfHeight`; else return `float.NegativeInfinity` (caller falls back to terrain). Use `SweepCapsule(capsule, Pose.At(new Vector3(x, startCenterY, z)), -Vector3.UnitY, maxProbe, out hit)` and `supportY = startCenterY - hit.Distance - tuning.CapsuleHalfHeight`.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ControllerOnPhysicsTests"`
Expected: PASS (4 tests). Tune `skin`, iteration count, and the probe start height if the dome-rest or doorway test is flaky.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Locomotion KhaozEngine.Tests/Physics/ControllerOnPhysicsTests.cs
git commit -m "physics(controller): CharacterMovement.Step over IPhysicsWorld (collide-and-slide + support probe)"
```

---

### Task 5: Bake per-prop collision shapes (authoring)

**Files:**
- Create: `KhaozEngine.Render3D/Models/PropCollisionBake.cs`, `KhaozEngine.Render3D/Models/PropCollisionLoader.cs`
- Modify: `KhaozEngine.Render3D/Models/AssetManifest.cs` (`CollisionShape` field), `KhaozEngine.PropSurface.Tool/Program.cs`
- Test: `KhaozEngine.Tests/Physics/PropCollisionBakeTests.cs`

**Interfaces:**
- Consumes: `GltfMesh.Vertices` (`ModelVertex[]`, `.Position`), `GltfMesh.Indices32` (`uint[]`); `PropSurfaceBake.IsWalkableSolid` (existing); `KhaozEngine.Physics` shapes.
- Produces:
  - `PropCollisionBake.Bake(GltfMesh normalizedMesh) -> PhysicsShape` (convex hull for a solid prop, triangle mesh for a building) + `PropCollisionBake.IsBuilding(GltfMesh) -> bool` (non-convex classifier; first cut: triangle count over a threshold or a manifest hint).
  - A binary format: `PropCollisionBake.Write(PhysicsShape, Stream)` / `PropCollisionLoader.Read(Stream) -> PhysicsShape` (magic + version + shape kind + payload).
  - `AssetEntry.CollisionShape` (`string?`, path to the baked `.coll` file), added to the ctor and JSON parse beside `Heightmap`.
  - `PropCollisionLoader.LoadAll(AssetManifest) -> IReadOnlyDictionary<string, PhysicsShape>`.

- [ ] **Step 1: Add the `CollisionShape` field to `AssetEntry`**

In `KhaozEngine.Render3D/Models/AssetManifest.cs`, add a `public string? CollisionShape { get; }` property, a trailing optional ctor parameter `string? collisionShape = null`, and parse `"collisionShape"` from the JSON entry beside `"heightmap"`. (Mirror the existing `Heightmap` handling exactly.)

- [ ] **Step 2: Write the failing bake test**

Create `KhaozEngine.Tests/Physics/PropCollisionBakeTests.cs`:

```csharp
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D.Models;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PropCollisionBakeTests
{
    [Fact]
    public void SolidProp_BakesAConvexHull_RoundTrips()
    {
        GltfMesh rock = TestMeshes.UnitIcosphere();    // helper: a small convex blob mesh
        PhysicsShape shape = PropCollisionBake.Bake(rock);
        Assert.IsType<ConvexHullShape>(shape);

        using var ms = new MemoryStream();
        PropCollisionBake.Write(shape, ms);
        ms.Position = 0;
        PhysicsShape loaded = PropCollisionLoader.Read(ms);
        var hull = Assert.IsType<ConvexHullShape>(loaded);
        Assert.True(hull.Points.Length >= 4);
    }

    [Fact]
    public void Building_BakesATriangleMesh()
    {
        GltfMesh house = TestMeshes.BoxRoomWithDoorway();  // helper: a non-convex room
        PhysicsShape shape = PropCollisionBake.Bake(house);
        Assert.IsType<TriangleMeshShape>(shape);
    }
}
```

(Add small `TestMeshes` helpers building a `GltfMesh` from hand-authored vertices/indices, or reuse an existing test-mesh helper if one exists in the test project.)

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropCollisionBakeTests"`
Expected: FAIL to compile (`PropCollisionBake` missing).

- [ ] **Step 4: Implement the bake + loader**

Create `PropCollisionBake.cs`: `Bake` reads `normalizedMesh.Vertices`/`Indices32`; if `IsBuilding(mesh)` returns a `TriangleMeshShape(vertices, indices)`, else computes a convex hull of the vertices (a simple, deterministic hull: for the first cut, pass the unique vertex set as `ConvexHullShape.Points` and let the backend's `ConvexHullHelper` build the hull; cap vertices by spatial down-sampling if over a budget, e.g. 64). `IsBuilding`: a building if the mesh is flagged as such (a manifest hint) or its triangle count exceeds a threshold AND it is not classified walkable-solid-as-a-single-blob; keep the first cut simple and note it as an open tuning item. `Write`/`Read`: magic `0x4B45434C` ("KECL"), version 1, a kind byte, then the payload (hull: count + Vector3s; mesh: vCount + Vector3s + iCount + ints).

Create `PropCollisionLoader.cs`: `Read(Stream)` inverse of `Write`; `LoadAll(AssetManifest)` reads each entry's `CollisionShape` path (skip entries without one) into the dictionary keyed by `entry.Id`.

- [ ] **Step 5: Bake the shape in `ke-propbake`**

In `KhaozEngine.PropSurface.Tool/Program.cs`, after the existing surface bake/write, also bake and write the collision shape for each walkable-solid prop: compute `PhysicsShape coll = PropCollisionBake.Bake(mesh);` write it to `<id>.coll` next to the `.surf`, and have the tool record the relative path (the manifest rewrite, if the tool rewrites the manifest, sets `collisionShape`). Keep it inside the same loop that currently bakes `.surf`.

- [ ] **Step 6: Run and commit**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropCollisionBakeTests"`
Expected: PASS.

```bash
git add KhaozEngine.Render3D/Models/PropCollisionBake.cs KhaozEngine.Render3D/Models/PropCollisionLoader.cs KhaozEngine.Render3D/Models/AssetManifest.cs KhaozEngine.PropSurface.Tool/Program.cs KhaozEngine.Tests/Physics/PropCollisionBakeTests.cs
git commit -m "physics(bake): per-prop collision shape (convex hull / triangle mesh) + ke-propbake"
```

---

### Task 6: Stream prop statics into the physics world on chunk load/unload

**Files:**
- Modify: `KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs`
- Test: `KhaozEngine.Tests/Physics/ChunkStaticsTests.cs`

**Interfaces:**
- Consumes: `IPhysicsWorld.AddStatic`/`RemoveStatic` (Task 1); the deterministic per-chunk scatter (`ScatterFor(coord)`) and the `ChunkLoad` handle (existing); per-prop `PhysicsShape` from `PropCollisionLoader.LoadAll` (Task 5).
- Produces: `Scene3DChunkSink` gains an optional ctor param `IPhysicsWorld? physics = null` and an optional `IReadOnlyDictionary<string, PhysicsShape>? collisionShapes = null`; on `Load`, for each scattered prop with a known shape it calls `AddStatic(shape, Pose)` and records the `StaticHandle`s on the `ChunkLoad`; on `Unload`, it removes them. Null physics = unchanged.

- [ ] **Step 1: Write the failing test (fake sink-driver, fake world)**

Create `KhaozEngine.Tests/Physics/ChunkStaticsTests.cs` using a fake `IPhysicsWorld` that counts `AddStatic`/`RemoveStatic`. (The streamer is already GPU-free headless-testable via a fake sink; here drive `Scene3DChunkSink.Load`/`Unload` directly, or assert via the fake world that load adds N statics and unload removes exactly those.) Assert: loading a chunk with K scattered solid props adds K statics; unloading the same chunk removes K; a chunk with `physics: null` adds none.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ChunkStaticsTests"`
Expected: FAIL to compile (no physics ctor param).

- [ ] **Step 3: Implement the hooks**

In `Scene3DChunkSink.cs`: add the two optional ctor params and store them. Extend the `ChunkLoad` record with `List<StaticHandle> Statics`. In `Load`, after `_loaded[coord] = load`, if `_physics is not null && _collisionShapes is not null`, loop `load.Props` and for each placement with `_collisionShapes.TryGetValue(p.Id, out shape)` add a static at the placement pose (position `(p.X, terrainHeight(p.X,p.Z), p.Z)`, orientation `Quaternion.CreateFromAxisAngle(UnitY, p.Yaw)`, shape scaled by `p.Scale` if non-unit; for SP1 a uniform-scale convex hull can be pre-scaled when added, or note scale handling as an open item) and append the handle to `load.Statics`. In `Unload`, before `_loaded.Remove(coord)`, if `_physics is not null` remove every handle in `load.Statics`.

- [ ] **Step 4: Run and commit**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ChunkStaticsTests"`
Expected: PASS.

```bash
git add KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs KhaozEngine.Tests/Physics/ChunkStaticsTests.cs
git commit -m "physics(streaming): Scene3DChunkSink adds/removes prop statics on chunk load/unload"
```

---

### Task 7: Wire the netcode stack to `IPhysicsWorld`

Switch the authoritative + predicted movement to thread one `IPhysicsWorld` and call the new `CharacterMovement.Step` overload. The build stays green because the new overload (Task 4) exists; this task changes the netcode ctors and call sites.

**Files:**
- Modify: `KhaozEngine.NetWorld/PlayerMoveSimulator.cs`, `PlayerMovementSystem.cs`, `WorldServer.cs`, `WorldClient.cs`, `ShardedWorldServer.cs`, `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`
- Test: `KhaozEngine.Tests/Physics/NetWorldPhysicsTests.cs`

**Interfaces:**
- Consumes: `IPhysicsWorld` (Task 1); the new `CharacterMovement.Step` overload (Task 4).
- Produces (replacing the `WorldColliders? colliders, WorldSurfaces? surfaces` parameter pair with `IPhysicsWorld? physics` in each):
  - `PlayerMoveSimulator(Func<float,float,float> groundHeight, MoveTuning tuning, Func<float,float,Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null)`.
  - `PlayerMovementSystem(... , IPhysicsWorld? physics = null)`.
  - `WorldServer(INetTransport, WorldServerConfig, Func<float,float,float>, MoveTuning, Func<float,float,Vector3>? = null, WorldBounds? = null, IPhysicsWorld? physics = null, IConnectionAuthenticator? = null)`.
  - `WorldClient(INetTransport, Func<float,float,float>, MoveTuning, WorldClientConfig? = null, byte[]? = null, Func<float,float,Vector3>? = null, WorldBounds? = null, IPhysicsWorld? physics = null)`.
  - `ShardedWorldServer(INetTransport, ShardedWorldServerConfig, Func<float,float,float>, MoveTuning, Func<float,float,Vector3>? = null, WorldBounds? = null, IPhysicsWorld? physics = null, IConnectionAuthenticator? = null)`.

- [ ] **Step 1: Add the seam reference to NetWorld**

In `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`, add the project reference:

```xml
    <ProjectReference Include="../KhaozEngine.Physics/KhaozEngine.Physics.csproj" />
```

(NetWorld may keep its `Collision` reference until Task 8 if other types use it; the movement path no longer needs it.)

- [ ] **Step 2: Write the failing test (server == client over the physics world)**

Create `KhaozEngine.Tests/Physics/NetWorldPhysicsTests.cs`: build one `IPhysicsWorld` with a wall, construct a `PlayerMoveSimulator(... physics: world)`, and assert that stepping a `PlayerMoveState` with a forward command into the wall blocks (matching the controller test), and that two simulators over the same world from the same state + commands produce identical trajectories (server == client). Assert the old behaviour for `physics: null` is unchanged movement.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetWorldPhysicsTests"`
Expected: FAIL to compile (ctor still takes `colliders`/`surfaces`).

- [ ] **Step 4: Swap the parameter pair to `IPhysicsWorld?` in all five types**

In `PlayerMoveSimulator.cs`: replace the `WorldColliders? colliders = null, WorldSurfaces? surfaces = null` ctor params with `IPhysicsWorld? physics = null`, store it, and change `Step` to call `CharacterMovement.Step(state.Move, command, dt, groundHeight, tuning, groundNormal, physics, clampXz)` (the new overload; note the parameter order: `world` then `clampXz`).

In `PlayerMovementSystem.cs`: same param swap; update the `CharacterMovement.Step(...)` call site (line ~60) to the new overload.

In `WorldServer.cs`, `WorldClient.cs`, `ShardedWorldServer.cs`: replace the `WorldColliders? colliders, WorldSurfaces? surfaces` pair with `IPhysicsWorld? physics` in the ctor and pass it through to the `PlayerMoveSimulator`/`PlayerMovementSystem` they build.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetWorldPhysicsTests"`
Expected: PASS. Then run the full suite to catch other call sites: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` (expect failures only in samples/tests that still pass `colliders`/`surfaces`, fixed in Task 8).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.NetWorld KhaozEngine.Tests/Physics/NetWorldPhysicsTests.cs
git commit -m "physics(netcode): thread IPhysicsWorld through PlayerMoveSimulator/WorldServer/WorldClient/Sharded"
```

---

### Task 8: Retire the 2D footprint path and fix consumers/samples

**Files:**
- Modify: `KhaozEngine.Locomotion/CharacterMovement.cs` (remove the `WorldColliders`/`WorldSurfaces` overloads), `KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj` (drop the Collision ref), `KhaozEngine.NetWorld/*` (drop the Collision ref + any residual usings), the samples (`MmoServerSample`, `NetworkedWalkServer`, `NetworkedWalkSample`, `TerrainWalkSample`, the `Game.Render3D` `CharacterController3D`), and any test that constructed `WorldColliders`/`WorldSurfaces` for movement.
- Test: existing suite must stay green.

**Interfaces:**
- Consumes: everything from Tasks 1-7.
- Produces: `CharacterMovement` exposes only the `IPhysicsWorld` overloads (plus the original terrain-only horizontal overload, updated to drop `WorldColliders`); no movement code references `KhaozEngine.Collision`.

- [ ] **Step 1: Remove the obsolete overloads**

In `CharacterMovement.cs`, delete the `WorldColliders? colliders`/`WorldSurfaces? surfaces` overloads of `Step` and the old `ResolveHorizontal` collider/surface branches. Keep (and, if it still references `WorldColliders`, update) the simple horizontal-only `Step(Vector3 position, ...)`: either drop its collider param entirely (terrain-only) or give it an `IPhysicsWorld?` slide, matching how its callers use it. Remove `using KhaozEngine.Collision;` from the file.

- [ ] **Step 2: Drop the Collision project reference where movement no longer needs it**

Remove `<ProjectReference Include="../KhaozEngine.Collision/...">` from `KhaozEngine.Locomotion.csproj` (and from `KhaozEngine.NetWorld.csproj` if nothing else there uses `Collision`). Leave `Collision` in the solution: `SpatialHashGrid` and the `.surf`/`PropFootprint`/`PropSurfaceBake` types remain (now feeding shape baking).

- [ ] **Step 3: Fix every consumer/sample call site**

Build the solution: `dotnet build KhaozEngine.slnx`. For each error where a sample/consumer constructed `WorldColliders`/`WorldSurfaces` and passed them to a `WorldServer`/`WorldClient`/`ShardedWorldServer`/`CharacterController3D`, replace with constructing a `BepuPhysicsWorld`, loading collision shapes (`PropCollisionLoader.LoadAll`), populating it (for the bounded-preset samples, add the same props the scatter places, or pass the streamer's physics world), and passing `physics:`. For `CharacterController3D` in `Game.Render3D`, swap its collider/surface fields for an `IPhysicsWorld?` and call the new overload.

- [ ] **Step 4: Run the full suite green**

Run: `dotnet build KhaozEngine.slnx && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: build succeeds, all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "physics(migrate): retire 2D WorldColliders/WorldSurfaces movement path; consumers on IPhysicsWorld"
```

---

### Task 9: Release (version bump, changelog, doc sweep, pack)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `CLAUDE.md`, `docs/CONSUMERS.md`, `docs/USING-KHAOZENGINE.md`, `docs/ROADMAP.md`
- Output: packages in `./local-feed`

**Interfaces:**
- Consumes: the completed Tasks 1-8.
- Produces: a `8.0.0` release of all packages including the two new ones.

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, set `<KhaozEngineVersion>8.0.0</KhaozEngineVersion>` (major: the movement/world ctors changed). Add `KhaozEngine.Physics` and `KhaozEngine.Physics.Bepu` to the comment enumerating packable packages.

- [ ] **Step 2: Add the CHANGELOG entry**

Prepend a `## 8.0.0` entry to `CHANGELOG.md` (newest-first), one-line summary first sentence, then the detail: new `KhaozEngine.Physics` seam + `KhaozEngine.Physics.Bepu` backend (BepuPhysics v2, pure-managed); `CharacterMovement.Step` now resolves capsule-vs-mesh collide-and-slide against an `IPhysicsWorld` (the domed-rock flank fix + building interiors + raycast/sweep/overlap queries); props/buildings bake 3D collision shapes (`ke-propbake`); the netcode ctors (`WorldServer`/`WorldClient`/`ShardedWorldServer`/`PlayerMoveSimulator`/`PlayerMovementSystem`) replace `WorldColliders?`/`WorldSurfaces?` with `IPhysicsWorld?` (BREAKING); the 2D footprint movement path is retired. Note terrain stays analytic and dynamic bodies are sub-project 2.

- [ ] **Step 3: Full doc sweep**

Update, and grep `KhaozEngine.Physics` across `*.md` + `CLAUDE.md` to confirm coverage:
- `README.md`: package-catalog table (+ the two new packages) and the repo-layout block; the `<PackageReference>` example version.
- `CLAUDE.md` (root): the `<KhaozEngineVersion>` package enumeration + the umbrella descriptions (`Foundation` gains `Physics`; `Server`/`Game3D` gain `Physics.Bepu`); add a Physics bullet to the package catalog.
- `docs/CONSUMERS.md`: the umbrella/package table + "Engine current version" -> `8.0.0`.
- `docs/USING-KHAOZENGINE.md`: a usage section for `IPhysicsWorld` + wiring `BepuPhysicsWorld` into the movement step.
- `docs/ROADMAP.md`: "Current released version" -> `8.0.0`; rewrite item #2 to reflect SP1 shipped and SP2 (dynamic bodies + replication) + SP3 (constraints) remaining; remove the resolved domed-prop limitation note.

- [ ] **Step 4: Verify the doc-version guard and tests**

Run: `bash scripts/check-doc-versions.sh && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: guard passes (three version strings match `8.0.0`), all tests PASS.

- [ ] **Step 5: Pack and commit**

```bash
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
git add -A
git commit -m "physics(8.0.0): KhaozEngine.Physics seam + Bepu backend; controller on mesh collision"
```

Do NOT tag or push here (the engine holds + batches publishes; confirm with the user before pushing per the release ritual).

---

## Self-Review

**Spec coverage** (each spec section -> task):
- Seam package + dep graph -> Task 1. Backend over Bepu -> Task 3. Determinism + AOT gate -> Task 2. Controller collide-and-slide + dome fix -> Task 4. Static prop/building shapes + bake -> Task 5. Streaming AddStatic/RemoveStatic -> Task 6. Netcode wiring (contract unchanged, no new replication) -> Task 7. Migration / retire 2D path -> Task 8. Major bump + changelog + doc sweep -> Task 9. Terrain-stays-analytic -> Task 4 `Support` (terrain delegate + physics down-probe). Out-of-scope items (dynamic bodies, terrain mesh, constraints) -> not implemented, noted in Task 9 ROADMAP rewrite.
- One gap to watch: `ComputePenetration` and the convex-hull build are the two Bepu spots whose exact API must be confirmed at the installed version (flagged in Task 3 Step 4). The determinism/AOT gate (Task 2) de-risks the API before Task 3.

**Placeholder scan:** no "TBD/TODO/implement later"; the two "confirm against installed BepuPhysics 2.x" notes are concrete engineering instructions naming the real API surface, not deferrals. Test code is shown in full; backend internals reference the specific Bepu calls (`Simulation.Create`/`Statics.Add`/`Timestep`/`RayCast`/`Sweep`).

**Type consistency:** `IPhysicsWorld`, `CapsuleShape(radius,length)`, `Pose.At`, `StaticHandle`, `RayHit`/`SweepHit`, `PhysicsShape` subclasses are defined in Task 1 and used unchanged in Tasks 3/4/6/7. The netcode ctors in Task 7 match the `physics:` parameter the samples adopt in Task 8. `CharacterMovement.Step(..., IPhysicsWorld? world, Func<float,float,Vector2>? clampXz = null)` parameter order is consistent between Task 4 (definition) and Task 7 (call site).
