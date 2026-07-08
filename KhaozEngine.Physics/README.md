# KhaozEngine.Physics

Dependency-free 3D physics seam. `IPhysicsWorld` is the render-free, headless contract the character
controller and the netcode resolve against: authoritative on the server, re-run identically in client
prediction. Backends implement it (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu)
explicitly, it is in no umbrella). Depends only on `System.Numerics`.

## Types

- **`IPhysicsWorld`** - static bodies (`AddStatic`/`RemoveStatic`), dynamic bodies
  (`AddDynamic`/`RemoveDynamic`/`GetDynamicPose`/`GetDynamicVelocity`/`SetDynamicVelocity`/`IsAwake`),
  `Step(dt)`, and the queries: `Raycast` (nearest hit), `SweepCapsule` (nearest time of impact, what the
  swept collide-and-slide in `Locomotion` uses), `ComputePenetration` (minimum translation to separate an
  overlapping capsule). Dynamic-body stepping is deterministic under a fixed dt.
- **Shapes** - `SphereShape`, `CapsuleShape` (upright, local Y), `BoxShape` (half-extents),
  `CylinderShape`, `ConvexHullShape` (solid props), `TriangleMeshShape` (non-convex buildings/interiors,
  static only), and `CompoundShape` (`CompoundChild[]`, disjoint children each at a local `Pose`). A dynamic
  body takes any of these except a triangle mesh. Base-aligned cylinder/hull shapes rest base-on-ground.
- **`DynamicBodyDescription`** - the mass/inertia + initial-motion knobs for `AddDynamic`:
  `WithMass(mass)`, plus optional `LinearVelocity`/`AngularVelocity`/`SleepThreshold`. Mass &lt;= 0 = an
  infinite-mass (kinematic) body: unmoved by gravity/impacts, moved only by its velocity.
- **`Pose`** - world position + orientation record struct. `Pose.At(position)` for identity orientation.
- **`PhysicsMaterial`** - friction + restitution, `PhysicsMaterial.Default` is full friction, no bounce.
  A dynamic body's `Restitution` (0..1) drives an approximate, deterministic game-feel bounce that decays
  geometrically with restitution (NOT a true coefficient of restitution: a bounded post-solve reflection, exact
  apex not analytically pinned).
- **`QueryFilter`** - layer mask for queries, default (`QueryFilter.All`) matches every body.
- **`StaticHandle`**, **`DynamicBodyHandle`**, **`RayHit`**, **`SweepHit`** - opaque body handles and the query result structs.
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

// Dynamic bodies fall under gravity and are stepped by Step(dt):
DynamicBodyHandle crate = world.AddDynamic(
    new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)),
    Pose.At(new Vector3(10f, 8f, -4f)),
    DynamicBodyDescription.WithMass(10f));
world.Step(1f / 60f);
Pose cratePose = world.GetDynamicPose(crate);

if (world.Raycast(origin, direction, 50f, out RayHit hit))
    Console.WriteLine($"hit {hit.Body} at {hit.Point}");

world.RemoveDynamic(crate);
world.RemoveStatic(rock);
```

No render, window, or GPU dependency. In the `Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
