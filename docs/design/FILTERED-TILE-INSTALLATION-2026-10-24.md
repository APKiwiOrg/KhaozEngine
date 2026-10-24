# Filtered tile installation

Status: proposed design for Grimhollow contact adoption. Baseline `4bb5b0462`, released `v21.12.2`.

## Goal and boundary

Let a consumer choose which built tile colliders enter a physical world while the existing engine installation
retains canonical provenance, movement exclusions, absolute transformed bounds and source authentication.
`PhysicsNavBake.Capture` must sample the physical set actually installed.

Grimhollow omits Blocked boxes above water before installation. Its existing policy is in
`Grimhollow.Shared/HollowmerePhysics.cs`, method `IsBlockedOverWater`. The consumer research counted 7,574
omissions in the shipped world. Owner ruling CA-R2 chooses this engine seam. The policy remains game-owned.
The engine neither recognises Grimhollow materials nor changes what `TileWorldColliders.Build` produces.

This design does not adopt Contact in a consumer, edit a world, rebake Hollowmere, change controller tuning,
or author a release tag. Those are separate work with their existing approval gates.

## FI-1. Public API

Keep the existing public method, including its binary signature, and add a required-predicate overload:

```csharp
public TileColliderRegistration AddTo(IPhysicsWorld world);
public TileColliderRegistration AddTo(IPhysicsWorld world, Func<TileCollider, bool> include);
```

Add this property to the existing `TileColliderRegistration`:

```csharp
public IReadOnlyList<int> ColliderIndices { get; }
```

`ColliderIndices[i]` names the original `TileWorldColliders.Colliders` entry installed as `Handles[i]`.
Indices are strictly increasing. The unfiltered call returns `0..Colliders.Count-1`, preserving every existing
index relationship. Both collections are immutable snapshots retained after removal. Update the `Handles`
documentation to distinguish compact installed order from original collider indices.

Keep `TileCollider`'s constructor, positional record members and deconstruction unchanged. Keep
`TileColliderOptions` and every `Build` overload unchanged. No public owner key, bounds arithmetic, new
registration type, filter identifier or installed-subset hash is needed.

## FI-2. Selection and transaction

Evaluate `include` synchronously, once per built collider, in canonical collider order. True retains the whole
collider. False omits it entirely. A predicate can distinguish the two edges of a wall corner, but cannot
split mesh triangles or change a collider. Do not deduplicate colliders sharing an owner.

Finish evaluating all decisions before reading the installation origin or calling `AddStatic`. Then capture
`world.Origin` once and perform the existing add transaction for the retained sequence. The unfiltered entry
point uses the same transaction with every index retained and invokes no user callback.

The callback receives absolute document-space data, including the original pose. It must be a pure selection
policy for this call. It must not mutate the document, collider shapes or physical world, re-enter installation,
or depend on concurrently changing state. The engine stores the decisions, not the callback. There are no
callback invocations during movement registration, capture, stepping, removal or disposal.

Both overloads reject a null world. The new overload rejects a null predicate. Rejection occurs before any
physical mutation. A callback exception is rethrown unchanged and leaves the world untouched by this call.
An all-false predicate returns an empty, disposable registration without adding or removing any statics.

An add failure rolls back only successfully added selected handles. Reuse the existing removal machinery,
including retry-only-failed removal and exception order. If rollback also fails, retain the existing
`AggregateException(addFailure, cleanupFailure)` contract. Pre-existing world statics remain owned by their
caller. The registration never owns or disposes the physical world.

Put selection and the single add transaction in a focused internal `TileColliderInstallation` type. Remove
the old transaction body when routing both public methods through it. This is one installer with two entry
points, with no duplicated fallback or cleanup implementation.

## FI-3. Canonical provenance and represented bounds

For each retained original index, copy the existing `TileColliderProvenance` row at that index into the compact
installation metadata. The handle, original index and provenance row must stay aligned. Pass only those rows
to `TileColliderRegistration.CreateMovementRegistrationBuilder`.

Preserve owner spelling and feature ranges exactly:

- `ground/{rx}/{rz}/0` for a region mesh.
- `blocked/0/{x}/{z}` for a Blocked tile.
- `placed/{objectId}` for every wall edge, object or walk surface belonging to that placed object.
- Feature range `0..int.MaxValue`, as the current tile builder registers it.

`TileColliderProvenance.Capture` remains the sole implementation of tile bounds. It transforms mesh vertices
or all eight box corners by the collider's represented quaternion and position, then expands each extremum
outward by one float. Copy these captured absolute bounds. Do not reconstruct bounds from footprint size,
assume a rotated box is axis aligned, subtract the physics origin from metadata, or add a consumer copy of
this arithmetic. Installation continues to subtract the origin exactly once from the physical pose.

Canonical order is independent of the physical handle allocator. Omitted rows never enter movement
registration. Selected rows retain their original owner keys even if earlier colliders or another member of
the same owner were omitted.

## FI-4. Exclusions and resident extent

There are two distinct decisions. An omitted collider has no installed handle or provenance entry. An
installed Ground collider has both, and is excluded only from movement queries.

`GroundHandles` is exactly the selected Ground subset, in installed order. Both
`CreateMovementQueryView` and the ordinary registration builder use that same subset. A rejected Ground
mesh never appears as a phantom exclusion. Other selected colliders remain queryable through the movement
view. Complete physics retains the selected Ground meshes for navigation capture.

Every filtered installation retains the existing captured `TileWorldColliders.MovementExtent`, unchanged.
It is a conservative resident-domain declaration, not a tight box around selected statics. It already includes
resident regions, analytic ground range, transformed collider bounds and water extent. Keeping it preserves
analytic and water coverage when physical Ground or Blocked colliders are omitted, including an empty
physical selection. Omitted outliers may leave a conservative bound. That does not make them installed or
bindable. Do not infer geometry membership from the extent or shrink it to selected static bounds.

`combinedExtent` on `CreateMovementRegistrationBuilder` retains its existing semantics for consumers adding
other statics. This work does not change extent construction or ordinary domain arithmetic.

## FI-5. Authentication, generation and lifetime

Use `OrdinaryMovementRegistration.Builder.Build(IPhysicsQueryLease)` unchanged. The engine captures the
registration after installation and any deliberate supplemental statics, under a fresh lease of that exact
source. `SourceWorld`, `GeometryGeneration`, `Generation`, selected owner entries and exclusions form an
immutable registration snapshot. Editing a retained callback closure or continuing to edit a builder cannot
alter a built registration.

A lease from another physical source is Invalid even when world ids and integer handle values match.
A static-geometry mutation makes the previous snapshot Unresolved. Bepu's existing static-generation
authentication intentionally survives simulation ticks and origin rebases. Sources without a separate static
generation retain the existing stricter read-generation rule. Do not replace these rules with equality against
an installation-time geometry generation or silently refresh an old registration.

No installation-wide freeze is added. A consumer may create a new snapshot after deliberate changes using
the existing builder workflow. Mutating source statics after publishing a snapshot requires rebuilding the
environment before reuse. Local removal continues to prohibit new movement registration after any selected
handle has been removed. Removing an empty installation has no physical generation effect.

Dispose movement contexts and query views before the registration, and dispose the caller-owned source last.
Repeated registration disposal remains safe. Rollback cannot undo the source's generation history, so a failed
installation that temporarily added statics can invalidate a pre-existing registration even after cleanup.

## FI-6. Capture, identity and compatibility

Do not change `PhysicsNavBake.Capture`. It constructs `PhysicsColumnProbe(context.Physics)` and samples
statics at bounded column centres. Giving it the complete filtered installation makes omitted boxes absent
from raw capture and leaves selected ground and obstacles visible. Filtering only a movement view cannot
provide this behavior.

`TileWorldColliders.Hash` continues to describe the complete built collider sequence, independent of any
installation predicate. It must not be relabelled as a hash of installed statics. Different selected sets enter
the existing ordinary semantic digest through their retained provenance and exclusions. The existing
ordinary environment identity therefore distinguishes those sets without hashing a delegate or local handles.
Consumers persisting filtered bakes must include their policy inputs in `NavBakeSources` or use the existing
contact identity machinery. A full collider hash alone does not identify a filter policy.

The default call and an all-true predicate must preserve the same physical add order and poses, canonical
owners, exclusions, extent bits and registration semantic digest. Existing collider hashes, contact identity
version, navigation formats, pinned normalized navigation bytes and baseline hashes remain unchanged.
No baseline refresh is permitted to conceal a difference introduced by this seam.

## FI-7. #1352 assessment and acceptance

The historical issue describes a 0.5 m analytic jump below the configured `StepHeight`, stopping at x 0.933.
The current source already admits walkable analytic support within the step band, computes a step part and
pays it through `GroundPlacement.Paced` and `GroundCore.Tick.PayLag`.

At the baseline, the existing `GroundCoreStepTests.PartialDropKeepsTheRestOwed` regression crosses a
discontinuous 0.3 m analytic rise with `StepHeight = 0.4 m`, then a partial drop and idle debt payments.
It asserts walkable support, a partial paced ascent, the next support and final settled height. This is useful
current traversal evidence for the failure class, but is not the historical 0.5 m run or a Hollowmere audit.
The design report records the actual execution result.

Plan a separate permanent 0.5 m regression through both direct Contact and ordinary Contact with
`StepHeight = 0.6 m`, explicit upward normals and no physical obstacles. Assert crossing and paced settling
at 30 and 60 Hz. Include the exact step-height boundary, an above-height refusal and a steep continuous
slope control. No new controller fix is justified by the inspected source. If the exact regression is red,
stop the installer handoff for a measured root-cause amendment rather than add a height clamp or enlarge
the motion budget. Do not close #1352 from this design assessment.

## Acceptance and delivery

The implementation plan owns explicit cases for selected capture, canonical owners and aliases, transformed
bounds at a nonzero origin, conservative extent, immutable generations, foreign-source refusal, ground
exclusions, empty selection, callback failure, rollback and byte compatibility. It adds documentation and
chooses one additive minor version in its final task, subject to the live staged-version rule.

All local builds and tests use `build-slot`. The final full suite uses `--heavy`. No local stress, repeated
test loops, timing claims, live consumer runs or navigation world bakes belong to this feature.
The design-only commit changes no engine version. Implementation, integration, release and consumer adoption
remain separate checkpoints owned by their orchestrator.
