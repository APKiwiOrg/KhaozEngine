# Analytic terrain query ownership

Status: root approved technical option A and this design for [#1233](https://github.com/APKiwiOrg/KhaozEngine/issues/1233) on 2026-10-03 within the owner-approved Grimhollow pivot scope. The [implementation plan](../superpowers/plans/2026-10-03-ground-query-ownership.md) also passed independent review. Implementation is authorized through bounded dispatches. No implementation proof or release is claimed.

## Problem and selected boundary

The committed regression is `KhaozEngine.TileWorld.Physics.Tests/GroundMeshSlopeMovementTests.cs` at `1f23538ecd3abb829f366131dbb5bcf0d7207da6`. Root's compiled run exited 1 with a behavioral failure. With the 0.95 grade ground mesh registered, a 30 Hz walker advances -3.80340 m over 120 ticks. Its paired ground-omitted control advances 8.00003 m. The log is `/tmp/grimhollow-ground-slopes-red.log`.

The point-height terrain seat intersects the capsule's lower spherical cap on a tilted mesh. Ground penetration pushes the moving capsule downhill and upward. Movement then snaps it back to the point-height seat, recreating the overlap. The recorded ground-only and complete-world MTVs agree. The independent normal is walkable, and the eight recorded ticks contain no zero-normal hit.

The current TileWorld movement contract gives the terrain sampler ownership of ground and physics ownership of props, walls and walk surfaces. The complete physics world also needs ground geometry for column capture and dynamics. The selected correction keeps those responsibilities and exposes a restricted movement query view over that same complete world.

This is an additive public seam and a corrected composition at callers. An unchanged raw full-world `CharacterMovement.Step` call still gives both representations ownership of terrain. The supported TileWorld composition must select the movement view. No movement math, capsule dimensions, tuning, layers, slope classification or navigation algorithm changes belong to this fix.

## Alternatives and root selection

Higher scores are better. Root selected A on correctness, compatibility, maintainability, implementation cost and runtime lifecycle.

| Option | Correctness | Compatibility | Maintainability | Cost | Lifecycle | Total |
| --- | --- | --- | --- | --- | --- | --- |
| A: same-world query view excluding analytic terrain | 9 | 9 | 8 | 7 | 9 | 42 |
| B: authoritative physical capsule support seat | 9 | 6 | 7 | 4 | 7 | 33 |
| C: separate complete and movement simulations | 8 | 8 | 4 | 6 | 3 | 29 |

A removes the duplicate terrain correction without changing point-height movement, prop support or steep-ground rules. B would need terrain-versus-prop support provenance, a physical capsule seat at joins and edges, and changes to current point-height expectations. C duplicates static shapes and lifecycle and gives capture and movement separate simulation ownership. X-only holds and moving vertical-only MTV patches retain the incompatible seats and are rejected.

## Public seam

Add these proposed signatures in `KhaozEngine.Physics`:

```csharp
public interface IPhysicsWorldQueryView : IPhysicsWorld
{
    IPhysicsWorld SourceWorld { get; }
}

// New public default member of IPhysicsWorld.
IPhysicsWorldQueryView CreateQueryViewExcludingStatics(
    ReadOnlySpan<StaticHandle> excludedStatics);
```

The default factory throws `NotSupportedException`, including for an empty span. Existing backend classes and test doubles can retain their current implementations until they opt in. There is no silent unfiltered fallback. C# supports default interface members with bodies, so the new capability need not become a mandatory member of every current implementation. See the [official interface reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/interface).

The view extends the current mutable `IPhysicsWorld` because `CharacterMovement`, its controller consumers and `GroundMoveContext` already accept that type. Introducing a separate query-only consumer interface would require a wider signature migration. The new subtype makes the restricted capability explicit and denies mutations at runtime. It is a bounded adapter, not a security boundary, since `SourceWorld` identifies the owner.

`SourceWorld` is the exact logical `IPhysicsWorld` on which the factory was invoked and stays reference-stable for the view's lifetime. Bepu returns itself as source. A decorator that offers the factory must wrap the delegated view, preserve its exclusions and expose the decorator as source. Returning the raw inner view is incorrect when the movement context's `Physics` is the decorator. A decorator may retain the default unsupported factory instead. Do not unwrap identities to an assumed backend or compare only numeric origins.

## Query and lifecycle contract

The following inherited query signatures are unchanged:

```csharp
bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
    out RayHit hit, QueryFilter filter = default);
bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction,
    float maxDistance, out SweepHit hit, QueryFilter filter = default);
bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv);
```

The exclusion snapshot applies to every static query above. Rays and sweeps retain `QueryFilter.Mobility`. Penetration remains static-only, as it is today. The view does not implement or reinterpret `QueryFilter.Layers`.

| Operation | Live view behavior |
| --- | --- |
| `Raycast`, `SweepCapsule`, `ComputePenetration` | Query the same simulation, restricted by the copied seam-handle set |
| `GetDynamicPose`, `GetDynamicVelocity`, `IsAwake` | Forward read-only dynamic observations to the source |
| `Origin` | Read the source's current origin on every access |
| `SourceWorld` | Return the exact logical source reference |
| `CanRebase` | Return false, even when the source can rebase |
| `AddStatic`, `RemoveStatic`, `AddDynamic`, `RemoveDynamic` | Throw `NotSupportedException` before touching the source |
| `SetDynamicVelocity`, `AddConstraint`, `RemoveConstraint`, `SetConstraintTarget` | Throw `NotSupportedException` before touching the source |
| `Step`, `Rebase` | Throw `NotSupportedException` before touching the source |
| Factory invoked on a view | Throw `NotSupportedException`. Nested view composition is outside this fix |
| `Dispose` | Idempotently dispose only the view. Do not step, dispose or alter the source, its statics, dynamics, constraints or pool |

After disposal, operational methods and `Origin` throw `ObjectDisposedException`. `SourceWorld` and `CanRebase` remain inspectable metadata. The source must outlive the view. Existing global backend disposal semantics are outside scope.

The factory snapshots input once. `ReadOnlySpan<T>.ToArray()` supplies an owned copy when creating the view, as documented in the [official API reference](https://learn.microsoft.com/en-us/dotnet/api/system.readonlyspan-1.toarray?view=net-10.0). Changing the caller's array afterward does not change selection. Duplicate handles are harmless. Empty selection still returns a restricted, non-owning view with unfiltered query results, not the mutable owner. At creation, Bepu rejects any handle absent from its live static seam map with `ArgumentException`. Handles are world-local integers, so a foreign handle with the same numeric value cannot be distinguished without redesigning `StaticHandle`. Callers must supply handles from the stated source, and TileWorld obtains them from that source's own registration.

Use stable seam handles, never copied Bepu handle values. Resolve the live candidate through `_reverseHandles` before membership testing. Removing an excluded static and adding a new allowed static must not transfer the exclusion when Bepu recycles its internal handle. No persistent selection state is attached to physics bodies, and creating a view allocates its snapshot only once. Query membership adds no per-call selection allocation.

## Bepu query ownership

Today `BepuPhysicsWorld` has only unfiltered penetration. A filtered result cannot be inferred from its returned MTV, which contains no handle. Implement candidate selection before collision batching.

Put the three query implementations and their shared private cores in `BepuPhysicsWorld.Queries.cs`. This is a responsibility-based extraction of existing query code, with no collision-math change. Public unfiltered methods call those cores with no exclusions. The new view calls the same cores with `StaticQueryExclusions`.

For rays and sweeps, `AllowTest` combines the existing mobility gate with the exclusion check before testing the collidable. Apply the same root selection to compound-child callbacks. Excluded shapes cannot update the nearest-hit distance, so an allowed farther hit remains discoverable. Never reject the nearest returned hit after a query and report a miss.

For penetration, skip excluded static candidates in the existing overlap loop before submitting their shape to `CollisionBatcher`. The existing deepest positive allowed contact, sign, batching order and buffer cleanup remain unchanged. If excluded ground overlaps more deeply than an allowed wall, the result must still be the wall's correction. Do not substitute a second simulation or query only one candidate.

Keep view creation and denied/read-only operations in `BepuPhysicsWorld.QueryView.cs`. Keep the snapshot and live candidate membership in `StaticQueryExclusions.cs`. The existing `HitHandlers.cs` receives only the additional gate. Simulation contacts remain unfiltered, so dynamic bodies still collide with registered terrain.

## TileWorld registration and composition

`TileWorldColliders.AddTo(IPhysicsWorld world)` keeps registering every collider in its existing order, applying the origin once and preserving rollback/removal semantics. Record the exact handles whose collider kind is `Ground` in the returned registration.

Add:

```csharp
public IPhysicsWorldQueryView CreateMovementQueryView();
public IReadOnlyList<StaticHandle> GroundHandles { get; }
```

on `TileColliderRegistration`. This method requests a view excluding only its recorded ground handles. `GroundHandles` is a cached read-only collection in registration order, with the same post-removal metadata lifetime as `Handles`. It supplies the exact subset for a union across multiple registrations. It is explicit and lazy, so adding colliders to a backend or test double that lacks the new factory continues to work. Requesting movement selection on that backend throws rather than silently using the complete world.

`Wall`, `Blocked`, `Object`, `WalkSurface` and subsequently added non-ground statics remain visible. The full world retains its ground meshes before, during and after movement. Create the view once for a live registration and dispose it before its source. Rebuild selection when replacing registrations. A caller with multiple registrations supplies the union of their `GroundHandles` to the generic factory. Factory validation rejects removed handles, so original registration metadata must not be used to create a new view after removal. Snapshot selection does not automatically include future ground registrations.

The committed regression's complete scene must retain every ground handle and independently ray-check the ground. Only the movement input changes to the public view. Keep its original assertions, tuning and control. This proves the selected engine composition rather than deleting terrain from the fixture.

## Movement context and navigation capture

Preserve the existing five-parameter constructor and its optional defaults. Add a six-parameter overload with explicit arguments, so the existing constructor's binary signature and source overload resolution stay available:

```csharp
public GroundMoveContext(Func<float, float, float> groundHeight,
    Func<float, float, Vector3>? groundNormal = null,
    IPhysicsWorld? physics = null,
    Func<float, float, Vector2>? clampXz = null,
    Func<float, float, float, MovementMedium>? medium = null);

public GroundMoveContext(Func<float, float, float> groundHeight,
    Func<float, float, Vector3>? groundNormal,
    IPhysicsWorld? physics,
    Func<float, float, Vector2>? clampXz,
    Func<float, float, float, MovementMedium>? medium,
    IPhysicsWorldQueryView? movementQueries);

public IPhysicsWorldQueryView? MovementQueries { get; }
```

The old constructor delegates to the new overload with `movementQueries: null`. With null selection, the context's movement uses `Physics` exactly as before. With a view, require `ReferenceEquals(movementQueries.SourceWorld, physics)` at construction. A missing or different source throws `ArgumentException` naming `movementQueries`. The context retains the full `Physics` for `PhysicsNavBake.Capture`. Only `StepTowards` receives `MovementQueries`.

Freeze the origin from the full source at step entry. Require the selected view's origin and source identity to agree at entry and around every provider callback, using the existing `EnsureOrigin` boundary. A mismatch during a step throws `InvalidOperationException`. Rebasing the source between steps remains supported because the view reads live `Origin`. Separately built worlds with equal origins are not interchangeable.

`GroundTraversalProbe.TryEdge` sometimes creates a dry context when the original has a medium provider. Carry `context.MovementQueries` into that constructor. Otherwise dry navigation proofs revert to the complete-world movement path even though runtime movement selected the view. Preserve unit pace, dry medium, footprint checks, bounded budgets, capture columns and directed graph construction from the landed #1236 work.

## Required evidence

Preserve the actual RED and add bounded tests for nearest allowed ray/sweep/penetration, compound roots, mobility, snapshot mutation, handle reuse, unsupported factories, denied mutations, non-owning disposal, logical decorator source and live rebase.

The TileWorld matrix keeps the existing 0.95-grade 30 Hz walking fact. Add 0.75-grade walking, 0.95-grade running, 0.55-grade walking and one 0.95-grade 60 Hz walking case. Compare each selected complete world with its ground-omitted control. Idle point-height seating must have less than 1 mm planar drift. A 1.15-grade plane, about 49 degrees and beyond the default 48 degree retained-traction ceiling, must match the omitted-ground analytic slide over 60 ticks from an interior start.

Flat-ground composition must retain a raised floor and its ledge release, wall blocking, blocked tiles, a mountable 0.25 m step and a legitimate sloped prop. Complete-world column capture must retain authored terrain and routes, including a medium-bearing context whose dry proof carries selection. Include rebasing before and after view creation and default callers with no view.

All build/test/format/pack/GPU commands require separate root clearance and a separately inspected process gate. Tests run synchronously, once per defined stage, with exit code and discovered test count recorded. No local stress, command loops or client boot. Root owns final combined verification, integration, pushes, packing and release governance. Ride staged 20.18.0. No tag or consumer pin change is authorized here.

## Scope and documentation finish

The implementation updates the TileWorld, Physics, Bepu, Locomotion and Movement living contracts, PHYSICS-PIPELINE, relevant consumer examples and the staged changelog. This rationale remains design history. The implementation plan names exact source, test and documentation paths.

The #1225 shared tile-raycast rules and #913 validation-spec paths stay with their owners. Recheck changed-path ownership before code. No #1238, jump, mouse-look, persistence, query layers or broader contact-classification work is included. Root added the public ground-handle subset during self-review so multiple registrations can form an explicit exclusion union. Independent design and plan review approved the result without findings.
