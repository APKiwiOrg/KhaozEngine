# KhaozEngine.Physics

Dependency-free 3D physics seam. `IPhysicsWorld` is the render-free, headless contract the character
controller and the netcode resolve against: authoritative on the server, re-run identically in client
prediction. Backends implement it (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu)
explicitly, it is in no umbrella). Depends only on `System.Numerics`.

## Types

- **`IPhysicsWorld`** - static bodies (`AddStatic`/`RemoveStatic`), `Step(dt)`, and the queries:
  `Raycast` (nearest hit), `SweepCapsule` (nearest time of impact, what the swept collide-and-slide in
  `Locomotion` uses), `ComputePenetration` (minimum translation to separate an overlapping capsule).
- **Shapes** - `SphereShape`, `CapsuleShape` (upright, local Y), `BoxShape` (half-extents),
  `CylinderShape`, `ConvexHullShape` (solid props), `TriangleMeshShape` (non-convex buildings/interiors),
  and `CompoundShape` (`CompoundChild[]`, disjoint children each at a local `Pose`).
- **`Pose`** - world position + orientation record struct. `Pose.At(position)` for identity orientation.
- **`PhysicsMaterial`** - friction + restitution, `PhysicsMaterial.Default` is full friction, no bounce.
- **`QueryFilter`** - layer mask for queries, default (`QueryFilter.All`) matches every body.
- **`StaticHandle`**, **`RayHit`**, **`SweepHit`** - opaque body handle and the query result structs.
- **`PhysicsShapeScale.Uniform(shape, scale)`** - a new shape with all geometry scaled uniformly
  (compound child poses included). For per-placement scatter scale before `AddStatic`.
- **`PropCollisionFormat`** - the KECL `.coll` binary format: `Write`/`Read` a single `PhysicsShape`,
  plus the headless manifest-free loaders `LoadDirectory(dir)` (every `<id>.coll` keyed by file name)
  and `Load(entries)`. A GPU-less server loads the same baked shapes a client predicts against,
  byte-identical, so queries match and prediction reconciles. The glTF bake that PRODUCES `.coll`
  files stays in `KhaozEngine.Render3D` / `ke-propbake`.

## Usage

```csharp
using KhaozEngine.Physics;

IPhysicsWorld world = new BepuPhysicsWorld();   // from KhaozEngine.Physics.Bepu

// Headless server: load baked shapes and build the same world the client has.
var shapes = PropCollisionFormat.LoadDirectory("content/collision");
StaticHandle rock = world.AddStatic(
    PhysicsShapeScale.Uniform(shapes["rock_big"], 1.3f),
    Pose.At(new Vector3(10f, groundY, -4f)));

if (world.Raycast(origin, direction, 50f, out RayHit hit))
    Console.WriteLine($"hit {hit.Body} at {hit.Point}");

world.RemoveStatic(rock);
```

Static bodies only for now. Dynamic bodies arrive later behind the same interface, so `Step(dt)` is
already part of the contract.

No render, window, or GPU dependency. In the `Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
