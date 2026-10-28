# Filtered tile installation

Status: installer and public measurement implemented, staged together for 21.13.0, which replaced the untagged 21.12.3. Baseline `4bb5b0462`,
released `v21.12.2`. Living contracts are in the [TileWorld.Physics README](../../KhaozEngine.TileWorld.Physics/README.md#filtered-installation),
[Physics README](../../KhaozEngine.Physics/README.md#support-query-measurement) and
[Movement README](../../KhaozEngine.Movement/README.md#labelled-support-query-capture). The design below records
the accepted requirements. Release and consumer adoption remain separate gates.

## Goal and boundary

Let a consumer choose which built tile colliders enter a physical world while the existing engine installation
retains canonical provenance, movement exclusions, absolute transformed bounds and source authentication.
`PhysicsNavBake.Capture` must sample the physical set actually installed.

Owner ruling CA-R3 adds supported public support-query measurement to this same release. Grimhollow
CA-23 to CA-25 need engine-owned nested work, caller labels, parallel-worker aggregation and proof-reuse
retention evidence. The existing private diagnostics are not a consumer contract. Outer Step/Hold counts
and reflection cannot replace these measurements.

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
Consumers persisting filtered bakes must explicitly identify the installation policy and all its inputs in
`NavBakeSources`, both when creating the bake and in its runtime `NavBakeExpectation`. Use
`sources.AddHashOf("policy/colliders", policyBytes)` with canonical bytes naming the policy version and its
inputs. Change that source digest when the policy semantics or inputs change. A full collider hash alone does
not identify a filter policy. Creating an ordinary Contact registration or using navigation format 2 does not
insert this source entry. The live environment identity and runtime profile validation do not replace it.
A policy-only source change must refuse a stored bake even with identical complete collider hashes and tuning.

The default call and an all-true predicate must preserve the same physical add order and poses, canonical
owners, exclusions, extent bits and registration semantic digest. Existing collider hashes, contact identity
version, navigation formats, pinned normalized navigation bytes and baseline hashes remain unchanged.
Historical full-file comparisons normalize only the engine-version string, its encoded length and the enclosing
identity-block length, following `ContactNavigationBaselineTests.NormalizeFile`. Every other identity, header
and payload byte stays pinned. Same-build default and all-true serialized files remain equal without
normalization. This test-only normalization does not relax engine-version load-mismatch checks.
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

## FI-8. Public support-query measurement surface

Add the API below to the dependency-free `KhaozEngine.Physics` namespace and package. It wraps the existing
`SupportQueryDiagnostics` recorder. Keep `SupportQueryCounts`, `SupportQueryDiagnostics`, its `ThreadState`,
worker plumbing and observer delegates internal. No new consumer friend assembly is permitted.

```csharp
public sealed class SupportQueryCapture : IDisposable
{
    public static SupportQueryCapture Begin(string label);
    public SupportQueryMeasurement Snapshot();
    public void Dispose();
}

public sealed class SupportQueryMeasurement
{
    public SupportQueryMetrics Total { get; }
    public IReadOnlyDictionary<string, SupportQueryMetrics> ByLabel { get; }
    public static SupportQueryMeasurement Combine(IEnumerable<SupportQueryMeasurement> measurements);
}

public sealed class SupportQueryMetrics
{
    public long this[SupportQueryCounter counter] { get; }
    public SupportQueryRouteMetrics Routes { get; }
    public SupportProofReuseMetrics ProofReuse { get; }
}

public sealed class SupportProofReuseMetrics
{
    public long PeakKeys { get; }
    public long PeakBytes { get; }
    public bool HasCompleteLifetimes { get; }
}
```

These declarations specify signatures, not implementation bodies. The capture constructor is private, so
Begin is its only entry point. All result constructors are internal.
`SupportQueryRouteMetrics` is a sealed immutable class with public `long` getters named `WallRetries`,
`PushedPlacements`, `MediumResolves`, `MaximumMediumRetries`, `GroundSegments`, `SlideSegments`, `AirSegments`,
`WaterSegments`, `RoundedReads` and `MediumHandoffs`, matching the internal route fields.

`SupportQueryCounter` is public in its own file. Explicit values 0 through 41 retain their original order,
from `Find` through `InitialHoldReuseHits`. Values 42 through 47 append `NpcLegacyHolds`, `NpcContactHolds`,
`NpcLegacySteps`, `NpcContactSteps`, `PlayerLegacySteps` and `PlayerContactSteps`. The
[Physics counter contract](../../KhaozEngine.Physics/README.md#support-query-measurement) defines their outer
entry points and the authoritative player population. There is no Count sentinel. The original range includes
all existing proposal/volume/static/mesh/member/extrema/join work, refusals, analytic samples, bindings, physics
and movement leases, producer pins, ground/medium/mode work, world forms, footprint operands/certificates,
Find memo, footprint reuse, EdgeSteps and initial Hold reuse. New counters append values. Never renumber or
reuse a value. The indexer rejects an undefined value with `ArgumentOutOfRangeException`.

Use `long` throughout the single internal counter storage, count snapshots, route totals, retention totals and
merges, not just in the public adapter. Do not accumulate an `int` and widen its already overflowed value.
Existing bounded per-session budgets and `Record`'s `int count = 1` argument stay unchanged. Enabled recorder
arithmetic and public aggregation are checked, throwing `OverflowException` rather than wrapping. The enum
promotion and new types are additive. Existing public signatures, package dependencies, formats and identities
stay unchanged. Do not promote the 42-argument positional internal count record into a permanent public ABI.

## FI-9. Scope lifetime and attribution

`Begin` opts in only on the calling managed thread. Null labels throw `ArgumentNullException`. Empty or
whitespace-only labels throw `ArgumentException`. Other labels are preserved exactly and compared ordinally,
without trimming, case folding or culture. Labels are caller descriptions, such as `player`, `cow` or
`player/preflight`, never engine-inferred body identities.

A capture installs a fresh enabled counter/route recorder and suspends prior internal observation callbacks.
It saves the entire prior thread state, including enabled state, count-array identity/content, ground and Find
observers, ground observation, curved-work observer, routes and retention state. Scope operations use an
internal capture frame, not a label lookup or dictionary mutation on every counter increment.

Scopes can nest synchronously. Direct work belongs exclusively to the innermost label. A child has its own
recorder. Closing it restores the parent's recorder without adding child counts to that recorder, and attaches
its immutable labelled result once to the parent's completed children. Parent snapshots combine direct work
and child rows. Thus `Total` counts every recorded operation once, and summing label counters equals `Total`.
Two occurrences of the same label combine into one row. A nested Find is an engine increment under the active
label, not a second measurement to add to its enclosing Step/Hold. Prior unrelated diagnostics receive none
of the capture's counters. They resume with their original counters and callbacks.

`Snapshot` returns a detached cumulative result, including completed child and joined worker work. It never
resets counters. Only the active top scope may snapshot before disposal. After disposal, its creating thread
can retrieve its frozen final snapshot. Result objects may be read on any thread. Earlier results cannot
change after further work, nesting, disposal or combination. `ByLabel` enumerates in ordinal label order.
An empty capture still exposes its label with zero metrics. Combining an empty sequence returns zero totals
and no labels. Null sequences or null elements throw `ArgumentNullException`.

`Dispose` finalizes once and restores state in a `finally`, including when snapshot materialization fails.
A normal `using` restores state when the measured operation throws and leaves a partial, inspectable result.
Dispose is idempotent after successful close. Wrong-thread use, out-of-order disposal or a parent snapshot
while its child is active throws `InvalidOperationException` before altering either scope. The caller can then
close scopes correctly. Do not hold a scope across `await`, `Task.Run` or a thread handoff. There is no
`AsyncLocal`, process-global enable switch or implicit propagation to arbitrary consumer tasks. Independent
consumer threads start their own scopes, join, then call `Combine` on their disjoint completed measurements.
Do not combine both a parent snapshot and its child snapshot, or two cumulative snapshots of one interval.
`Combine` adds the supplied measurements, it does not deduplicate overlapping evidence.

## FI-10. Worker aggregation and retention meaning

Extend the existing `WorkerDiagnostics.Plan/Enter/Exit/Merge` path, not the support call sites. The plan captures
immutable enablement and the current label. It never sends the caller's capture object, mutable recorder or
observer delegates to another thread. Preserve the existing explicitly opted-in internal test observers.
Each worker installs fresh counters and scope metadata with its existing live retention balance, records
nested work locally, exits in `finally` and restores its own prior
state. After joining every started worker, the caller merges each record exactly once in worker-index order,
under the label captured for that build. Caller-side snapshot, binding construction, first pins and disposal
are counted on the caller. Do not count them again as worker contributions.

Ordinary degree-one Contact baking remains on its current serial path. Parallel phases still join before
returning or throwing. Include every started worker's partial record before propagating the existing selected
failure, interrupt or cleanup failure. Never invent contributions for unstarted workers. A new build starts
with new records, and a failed build must not leak enablement or labels into a later one. Measurement does not
change phase scheduling, cutoff selection, source lifetime, graph bytes or default reuse choices.

Counter values and ordinary route fields add. `MaximumMediumRetries`, `PeakKeys` and `PeakBytes` take the
maximum, never the sum. `HasCompleteLifetimes` combines with logical AND, with true as the empty identity.
These operations make aggregation independent of record arrival order and grouping. All supplied records are
included once. This is determinism of reduction, not a claim that every execution does identical work.
Extra worker bindings, leases and pins are real work. Cache publication, reuse policy and speculative work
before a failure cutoff can change counts. Report raw totals. Do not normalize away worker overhead or claim
that all counters, failure-path counts or peaks are invariant across worker counts, processors or schedules.

Keep `SupportQueryDiagnostics.ProofReuseRetention` as the only retention accounting path. Its keys count
occupied memo entries. Bytes charge the existing owned table capacity and member/join payload by element size,
including spare fixed table slots. They exclude CLR headers, allocator overhead, unrelated scratch, initial-Hold
maps, GC allocation totals and process RSS. Peaks are the largest single proof-reuse session capacities
reported by retention events in the interval. They are not simultaneous process-wide or summed-worker peaks.
This matches the existing recorder's `max(sessionKeys/sessionBytes)` semantics. A caller needing RSS measures
it separately on the approved hosted run.

Live retention balance is physical state and must survive a scope switch. Seed an entering capture with the
current live Keys/Bytes but reset its observed session peaks. On exit restore saved callbacks/counters/routes
and saved observed peaks, carrying the live Keys/Bytes delta through to the restored state. Do not restore
obsolete live balances or zero them because measurement stopped. Acquiring in one scope and releasing in
another must never manufacture a negative live balance. Apply the same rule to worker enter/exit restoration.
This is a correction to scope bookkeeping, not a second retention recorder or per-memo observer registry.

A row has `HasCompleteLifetimes` only when live retained Keys and Bytes are zero at entry and at the sampled
boundary for each contributing interval/worker record. Otherwise its observed peaks may omit a pre-existing
steady session, and the flag is false. Snapshots remain useful for counters but cannot claim complete retained
memory evidence. Child scopes and repeated labels combine this flag conservatively. Wrap the complete source
reuse lifetime, from before its first store through reset/disposal, for the CA-24 bake evidence. All joined bake
source groups must close before its final measurement. Partial snapshots and failure records remain marked
incomplete where needed. `PeakKeys` and `PeakBytes` are independent maxima and need not come from one session.

Disabled support-query paths keep their current enablement gates and the existing unconditional live-retention
balance. No second increment stream, per-record label lookup, clock read, callback dispatch, allocation, lock,
thread hop or global scan is introduced when disabled. Counter widening and capture bookkeeping do not enable
instrumentation implicitly. In particular, do not enlarge disabled worker-record allocations after widening
counters. Gate record allocation/materialization and reduction on existing enablement, using the same state
restoration path without records when disabled. Keep existing internal allocation-observer behavior intact.
Enabled capture and snapshot allocations are diagnostics overhead, not movement
or proof-reuse retained bytes. Establish labels/scopes outside timed regions where possible, materialize results
afterwards and disclose enabled instrumentation in hosted evidence. This API supplies structural evidence,
not timer values or an allocation-free profiling promise. Timing and allocation budgets remain separately
approved harness work.

## FI-11. Measurement acceptance and consumer handoff

Tests must prove every public counter maps to the existing recorder, including values above `int.MaxValue`,
without calling an internal writer through the public API. Internal unit fixtures may inject known records to
exercise the adapter and reducers. Real movement and bounded Contact bake tests must collect through public
capture, without reflection or private diagnostics in the consumer-facing fixture.

SS-R3 applies. Compare same-run relationships and raw results from controlled equivalent fixtures. Serial
and degree-three bounded bakes use identical explicit stable-Hold reuse settings and warmed equivalent world
forms, then compare graph bytes, label membership and logical proof work such as Find and EdgeSteps. Check
worker setup overhead through same-run relationships, not a machine-wide absolute counter literal. Check
same-record merge permutations and groupings exactly. Do not pin CPU-, SIMD- or schedule-dependent extrema,
member, fallback, footprint or retention byte literals. A changed worker count may report a different measured
peak. Assert complete lifetimes and agreement with each run's own internal retention oracle, not peak equality
between different schedules. Do not turn a functional worker-degree comparison into a timing or load run.

Acceptance includes nested exclusive labels, immutable snapshots, disabled isolation, synchronous exception
restoration, wrong-thread/LIFO refusal, repeated disposal, empty/invalid inputs, overflow, retention across
scope boundaries, partial lifetimes, successful and failed worker joins, and byte parity with capture disabled.
Use a non-friend test assembly for a small real public-only capture/combination proof. No consumer gains friend
access. Existing engine diagnostic and cost-sampler tests continue to compile after internal widening.

Living docs must show exact namespace/package, enablement, `using` restoration, label semantics, snapshot and
combination rules, parallel bake behavior, maxima/byte/completeness meanings and thread/async restrictions.
The handoff to Grimhollow names the single installer/measurement release tag, source SHA, package version and
matching tool version. Its Tasks 13, 15 and 17 remain blocked until that release is available and adopted.
Implementation of this design does not authorize a release, game pin change, world bake, SG-21 run or timing.

## Acceptance and delivery

The implementation plan owns explicit cases for selected capture, canonical owners and aliases, transformed
bounds at a nonzero origin, conservative extent, immutable generations, foreign-source refusal, ground
exclusions, empty selection, callback failure, rollback and byte compatibility. It adds scoped public
measurement and bounded worker proofs before the final documentation and version task, which chooses one
additive minor version subject to the live staged-version rule. Both APIs ship together under CA-R3.

All local builds and tests use `build-slot`. The final full suite uses `--heavy`. No local stress, repeated
test loops, timing claims, live consumer runs or navigation world bakes belong to this feature.
The design-only commit changes no engine version. Implementation, integration, release and consumer adoption
remain separate checkpoints owned by their orchestrator.
