# KhaozEngine.Movement

Opt-in composition of Locomotion, Navigation and Physics. Add this package explicitly to a client or
server that needs body-aware movement geometry. It stays outside every umbrella and carries no backend,
input or rendering dependency.

## Exact 3D reach

`MovementBody` is an upright capsule in absolute metres. `Centre` is the capsule centre, `Radius` is
positive, and `HalfHeight` includes the rounded ends and must be at least the radius. All values must be
finite. The capsule's vertical axis extends `HalfHeight - Radius` above and below its centre.

`ReachTarget.Capsule(in MovementBody)` preserves the target's own dimensions.
`ReachTarget.Box(Vector3 centre, Vector3 halfExtents, float yawRadians = 0f)` uses exact box faces and
corners. Its X/Z half-extents must be positive, its Y half-extent may be zero, and all inputs must be
finite. Positive yaw rotates local +X toward world -Z, matching
`Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians)` on a physics pose.
`ReachTarget.Point(Vector3 position)` accepts any finite position, including zero. Default bodies and
targets are invalid and operations refuse them with `ArgumentException`.

```csharp
using System.Numerics;
using KhaozEngine.Movement;

var actor = new MovementBody(new Vector3(0f, 0.75f, 0f), 0.25f, 0.75f);
var other = new MovementBody(new Vector3(1.5f, 0.75f, 0f), 0.25f, 0.75f);
var target = ReachTarget.Capsule(other);
float distance = ReachGeometry.Distance(actor, target); // 1 metre between edges
bool reached = ReachGeometry.Within(actor, target, 0.75f, tolerance: 0.25f);
```

`Distance(in MovementBody, in ReachTarget)` returns the shortest 3D edge distance, clamped to zero for
overlap. It uses double intermediates and refuses results above `float.MaxValue` with
`ArgumentOutOfRangeException` before returning a float. `Within(in MovementBody, in ReachTarget,
float range, float tolerance = 0f)` compares the wider distance directly to range plus tolerance.
Both threshold inputs must be finite and nonnegative. Their sum must fit the finite float range or
`Within` throws `ArgumentOutOfRangeException`. There is no gameplay epsilon. A distance above the
finite float range simply fails a valid `Within` query.
