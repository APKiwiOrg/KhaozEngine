# KhaozEngine.Physics.Bepu

BepuPhysics v2 backend for the [KhaozEngine.Physics](../KhaozEngine.Physics) seam.
**`BepuPhysicsWorld : IPhysicsWorld`** over BepuPhysics 2.4.0 (pure managed, Apache-2.0, no native
libraries) with a single-threaded, deterministic `Simulation` (null dispatcher, fixed solve description).
Opt-in and in NO umbrella: consumers depend on the dependency-free seam and add this backend explicitly,
the same pattern as `Netcode.LiteNetLib` or `WorldStore.Sqlite`. The only assembly in the engine that
references BepuPhysics.

## What it does

- **`AddStatic`/`RemoveStatic`** - converts every seam shape (sphere/capsule/box/cylinder/convex
  hull/triangle mesh/compound) to Bepu shapes. Removal frees the shape from the shape pool too, so a
  streaming load/unload cycle does not grow it unbounded.
- **`Step(dt)`**, **`Raycast`**, **`SweepCapsule`**, **`ComputePenetration`** - the last is one
  CollisionBatcher manifold query over every shape type, deepest contact wins.

## Base alignment and compound gotchas

The shape factory papers over three Bepu behaviours so seam shapes act like their baked geometry:

- Bepu recenters a `ConvexHull` on its centre of mass and centres a `Cylinder` on the body pose. Both
  get wrapped (or, inside a compound, pose-offset) so they stay base-aligned: a prop static placed at
  its base no longer sinks half its height into the ground.
- A Bepu compound's children must be FLAT convex leaves. Children of a `CompoundShape` are added as
  direct leaves with the recentering folded into their local pose, and a nested `CompoundShape` is
  recursed into the same builder pose-composed. A compound-of-compounds would throw in the broadphase
  sweep. Note the struct `CompoundBuilder` is passed by `ref` (a by-value copy builds an empty compound).
- A `TriangleMeshShape` inside a `CompoundShape` is rejected (`NotSupportedException`): mesh children
  break the sweep bounds. Bake building proxies as compounds of convex hulls instead.
- Mesh statics are one-sided (front faces only). Fine in practice: the swept collide-and-slide in
  `Locomotion` always approaches from a known-outside position.

## Usage

```csharp
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

IPhysicsWorld physics = new BepuPhysicsWorld();
physics.AddStatic(new BoxShape(new Vector3(2f, 1f, 2f)), Pose.At(new Vector3(0f, 1f, 5f)));
physics.Step(1f / 60f);
```

Wire it in through the `IPhysicsWorld? physics` ctor param on `CharacterController3D`
(local play), `WorldServer` (authoritative sim), and `WorldClient` (client prediction). Server and
client each build their own `BepuPhysicsWorld` from the same baked shapes
(`PropCollisionFormat.LoadDirectory`), so prediction resolves the exact collision the server does and
solid props do not rubber-band.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
