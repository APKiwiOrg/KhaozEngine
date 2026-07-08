# KhaozEngine.Physics.Bepu

BepuPhysics v2 backend for the [KhaozEngine.Physics](../KhaozEngine.Physics) seam.
**`BepuPhysicsWorld : IPhysicsWorld`** over BepuPhysics 2.4.0 (pure managed, Apache-2.0, no native
libraries) with a single-threaded, deterministic `Simulation` (null dispatcher, fixed solve description,
4 substeps, uniform gravity applied in the pose integrator). Opt-in and in NO umbrella: consumers depend on
the dependency-free seam and add this backend explicitly, the same pattern as `Netcode.LiteNetLib` or
`WorldStore.Sqlite`. The only assembly in the engine that references BepuPhysics.

The default constructor uses Earth gravity `(0, -9.81, 0)`; `new BepuPhysicsWorld(Vector3.Zero)` gives the
static-only, non-falling behaviour. The solver `substepCount` (default 4) and `velocityIterationCount`
(default 8) are optional constructor parameters. Both are part of the determinism fingerprint for any world
containing dynamic bodies: a consumer with a determinism tripwire fingerprinted on the pre-dynamics backend
(one substep) can pin the old integrator with `new BepuPhysicsWorld(gravity, substepCount: 1)`. Changing
either value shifts the bit-exact result legitimately.

## What it does

- **`AddStatic`/`RemoveStatic`** - converts every seam shape (sphere/capsule/box/cylinder/convex
  hull/triangle mesh/compound) to Bepu shapes. Removal frees the shape from the shape pool too, so a
  streaming load/unload cycle does not grow it unbounded.
- **`AddDynamic`/`RemoveDynamic`** - adds a dynamic rigid body: a convex primitive, hull, or compound of
  convex leaves (NOT a triangle mesh: no closed volume, so no inertia). Inertia is derived from the shape
  and the `DynamicBodyDescription.Mass`; base-aligned cylinder/hull shapes use `BuildDynamicCompound` so
  they stay base-aligned exactly like statics. Mass &lt;= 0 makes a kinematic (infinite-mass) body. Removal
  frees the shape from the pool too (`RecursivelyRemoveAndDispose`), like statics.
- **`GetDynamicPose`/`GetDynamicVelocity`/`SetDynamicVelocity`/`IsAwake`** - pose/velocity query and set
  (setting velocity wakes the body); `IsAwake` reflects Bepu's natural island sleeping (a settled body
  sleeps and reports false until disturbed).
- **Restitution** - Bepu 2.4 has no restitution coefficient (its contact recovery velocity only recovers
  penetration, which yields a constant-height limit cycle, not a decaying bounce). So `BepuPhysicsWorld.Step`
  applies restitution as an explicit, deterministic, APPROXIMATE game-feel bounce (not a true coefficient of
  restitution): a restitutive dynamic body whose approach speed a contact arrested this step is returned
  `restitution x` that speed in the opposite direction, so the bounce decays geometrically with restitution.
  It is a bounded post-solve reflection (it can over-restitute by up to the contact recovery velocity, and the
  apex is not analytically pinned - a hard impact spreads its arrest over 2-3 substeps). Non-restitutive bodies
  are untouched.
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
