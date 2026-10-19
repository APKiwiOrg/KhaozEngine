# Contact Bake Proof Reuse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce #1401 contact-bake work with exact source-local completed-footprint reuse and certified same-source initial Hold reuse, preserving every serial bake byte.

**Architecture:** Physics exposes an optional non-owning reuse session, and Bepu owns complete immutable payloads keyed by exact request bits and authenticated view metadata. Movement preserves original raw source addresses, scopes one session around each same-layer outgoing loop and optionally copies a successful initial Hold under an explicit stable-provider contract. Travel, current-lease publication, selection and geometric protections retain their existing paths.

**Tech Stack:** C#/.NET 10, System.Numerics, BepuPhysics 2.4.0, existing Physics/Locomotion/Movement seams, xUnit headless Movement.Tests and Game.Tests.

**Spec:** `docs/design/CONTACT-BAKE-PROOF-REUSE-2026-10-19.md`, BB-1 to BB-14 and acceptance A to F. Read the owning checkout's `.superpowers/sdd/bake-option-b/research.md` and `progress.md` in full. BB-R1 requires both candidates in this lane. The spec preserves the operative evidence for checkouts without those notes.

## Global Constraints

- Preserve the Euclidean 0.001 m arrival ball, roof and footprint protections and raw layer identity.
- Raw physics identity remains `(StaticHandle, ElementId)`. Coincident members never coalesce.
- No tolerance, skin, band, epsilon, reach, work ceiling or existing storage limit may be widened or added to obtain a hit or meet a target.
- Preserve the independent 0.001 m placement offset, 0.0001 m contact/join band and 0.0005 m HeightError ceiling.
- Preserve 256 raw members, 512 witness slots and the cumulative 1,048,576-event curved-work ceiling, including its sticky refusal and existing storage bounds.
- The complete KENB bytes equal the current serial bake at the same engine identity for every pinned baseline and every supported parallel degree, independently of iteration order.
- Every miss, stale view, unsupported backend or refusal takes the full existing path, including its original refusal or exception when authentication fails.
- No wall-clock timing, load run, stress run, test loop, consumer launch, world bake, release or tag is authorized by this lane.
- Ride staged 21.5.2 without a version bump. The final implementation task appends to its CHANGELOG entry and rolls its date, preserving released history.
- Work only in `/Users/antonio/KhaozEngine/.worktrees/bake-option-b` on `feature/bake-option-b`, based on `f23e19ec0` plus the accepted design commit. Preserve accepted work and unrelated edits.
- Before each task, require that exact git toplevel and branch and a clean status. Stop on unexplained edits. One writer owns shared files at a time.
- Every build/test uses `build-slot --label bake-b-design -- <command>`, synchronously, one at a time. Add `--heavy` for broad selections and the final full test run. No shell test loops or timing flags.
- Full Release solution build, full solution test selection and format verification occur once, in Task 7. Intermediate tasks run only their named focused selections.
- Leave cost/timing artifact environment variables unset. Do not invoke the old cost harnesses, regenerate goldens, pack a shared feed or dispatch a workflow.
- Stage explicit owned paths, run staged guards and inspect the diff before every commit. Workers never push, merge, release, tag, comment on GitHub or dispatch agents. Integration belongs to the owning orchestrator.

## Review Focus

- A forwarding view can consume the one-shot join mode before forwarding. Key the mode actually consumed at the backend entry. Task 2 adds `ForwardedQueryConsumesJoinModeExactlyOnce`.
- A node's raw height can be replaced by the same resting height as another raw slot. Preserve its original ordinal before compaction. Task 3 adds `EqualRestingAnchorsKeepOriginalRawSourceAndLayer`.
- A cached Hold can immediately satisfy arrival with no travel lease afterward. Still authenticate that edge and keep logical slice zero consumed. Task 4 adds `ReusedHoldAuthenticatesAnImmediateArrivalAndKeepsItsSlice`.
- A worker object can run the next phase on a different OS thread. Reopen thread-affine sessions inside the source loop and leave no active entry at phase end. Task 5 adds `WorkerPhaseThreadChangeCannotInheritAReuseSession`.
- A large join matrix or spare array capacity can exceed the payload budget despite a small member count. Charge actual retained capacities and decline safely. Task 2 adds `JoinWordBoundaryAndOwnedCapacityRespectPayloadBudget`.

---

## File structure and proof conventions

Physics owns the optional capability in new `IPhysicsSupportFootprintReuseSource.cs`. Bepu owns
the exact request key, bounded table and payload publication in new cohesive
`SupportFootprintProofMemo.cs`, `SupportFootprintProofKey.cs` and
`BepuPhysicsWorld.SupportFootprintReuse.cs`. The existing query core keeps validation order.
Its selected QueryView forwards receiver identity and immutable exclusions to the same owner.
No new dependency edge or package is needed.

Locomotion's new `MovementQueryLease.ProofReuse.cs` bridges the hidden physics lease to that
capability. It exposes no producer state. Existing friend access already permits Movement to
call the bridge. Movement owns raw-source mapping in `ContactNavigationSources.cs`, source
scope/policy in `ContactNavigationProof.Reuse.cs` and successful numeric Hold state in
`ContactNavigationInitialHold.cs`. Make ContactNavigationProof partial without an arbitrary
split of unrelated code. Keep SupportRequestMemo, SupportContinuity and FootSupport lifetimes.

The only new public serial assertion is `NavBakeExecution.UseStableContactHoldReuse`, default
false. Capacity and bypass controls are internal test seams, not public tuning. Use existing
ContactNavigationBuildHooks to capture an immutable policy on the caller and pass it to workers.
New controls disable only this lane's two optimizations. Reuse-off counter reference means the
existing optimized serial path with these two optimizations disabled, including its existing
tick memo. `SupportReferenceEvaluation.Begin()` is a separate geometry oracle, not that counter
denominator, and must bypass both new reuse layers without seeding them.

Task 1 adds six additive counters to SupportQueryCounter/SupportQueryCounts together with
CounterCount, snapshots and subtraction. Four are the spec's FootprintReuse counters, the others
are EdgeSteps and InitialHoldReuseHits. New `SupportQueryDiagnostics.ProofReuse.cs` owns
retention state. Worker diagnostics carry/restore it with the existing thread recorder. Sum
events, take maximum for per-worker retention peaks, and require live retention zero after join.
Do not replay saved Volumes, Proposals or curved-work events on hits.

Test snippets pin assertions using locals from the named fixture. Each new test helper is
defined in its owning task. Expected payloads come from the real uncached query under identical
operands, with independent existing shape oracles retained. Compare every value by bits,
including all inline witness fields, joins, proposal bounds and signed zeros. Do not compare
managed struct padding or source-world handles across unrelated fixture installations.

At every task commit, stage only its listed changed paths, inspect the complete staged diff and
run these commands, each requiring exit 0.

```bash
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
git diff --cached --check
```

Tests precede production changes. New feature tests must produce an observed red before their
implementation. Protection controls can already pass and must not be made artificially red.
After a deterministic failure, fix its cause and rerun only affected coverage. Do not retry an
unchanged failure. Record reds, greens, actual commands/exits and counters in
`docs/verification/2026-10-19-bake-proof-reuse.md` as tasks complete.

### Task 1: Add exact completed-footprint reuse for the owning world (BB-1 to BB-5, BB-10, A)

**Files:**
- Create: `KhaozEngine.Physics/IPhysicsSupportFootprintReuseSource.cs`
- Create: `KhaozEngine.Physics.Bepu/SupportFootprintProofKey.cs`, `KhaozEngine.Physics.Bepu/SupportFootprintProofMemo.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportFootprintReuse.cs`
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportFootprint.cs`
- Modify: `KhaozEngine.Physics/SupportQueryDiagnostics.cs`, `KhaozEngine.Physics/WorkerDiagnostics.cs`
- Create: `KhaozEngine.Physics/SupportQueryDiagnostics.ProofReuse.cs`
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseTests.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseFixture.cs`
- Create: `docs/verification/2026-10-19-bake-proof-reuse.md`

**Interfaces:**
- Produces public `SupportFootprintReuseDomain(object ProfileIdentity, object SourceIdentity)`, an immutable token pair compared by reference identity, rejecting null tokens.
- Produces public `IPhysicsSupportFootprintReuseSource.TryOpenSupportFootprintReuse(IPhysicsQueryLease lease, out IPhysicsSupportFootprintReuseSession? session) -> bool`. False returns null without replacing an active session.
- Produces public session `ResetSource(in SupportFootprintReuseDomain domain) -> void`, `AssertCurrent(IPhysicsQueryLease lease) -> void` and `Dispose() -> void`. Reset clears payloads, thread and view authentication apply to all operations, and the session never owns the supplied lease.
- Produces internal `SupportFootprintProofKey.Create(in SupportFootprintRequest request, QueryFilter filter, bool certificationJoins) -> SupportFootprintProofKey`. Session/domain metadata from BB-3 binds the table outside its per-request key.
- Produces internal `SupportFootprintProofMemo(int keyCapacity = 512, int payloadBudgetBytes = 2 * 1024 * 1024)`, with `Reset() -> void`, `Dispose() -> void`, `TryCopy(IPhysicsWorld receiver, IPhysicsQueryLease lease, in SupportFootprintProofKey key, Span<SupportFootprintMember> members, Span<ulong> joins, out SupportFootprintResult result) -> bool`, and `Remember(in SupportFootprintProofKey key, in SupportFootprintResult result, ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins) -> void`. Only the backend calls these after authentication.
- Produces internal `SupportProofReuseRetention(int Keys, int Bytes, int PeakKeys, int PeakBytes)` and `SupportQueryDiagnostics.Retention -> SupportProofReuseRetention`. Task 2/3 use the snapshot to prove cleanup.
- Produces six named counters from File structure. EdgeSteps and InitialHoldReuseHits remain zero until their Movement call sites land. CounterCount becomes 42, with snapshot/worker restoration covering every slot.

- [ ] **Step 1: Write the capability red against the unchanged backend.**

`WorldDeclaresExactFootprintReuseCapability` inspects the world's implemented interface names.
This first test compiles before the new interface exists. Assert that the list contains the
exact name `KhaozEngine.Physics.IPhysicsSupportFootprintReuseSource`.

```csharp
Assert.Contains(world.GetType().GetInterfaces(),
    type => type.FullName == "KhaozEngine.Physics.IPhysicsSupportFootprintReuseSource");
```

- [ ] **Step 2: Run the capability test and verify the red.**

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~WorldDeclaresExactFootprintReuseCapability'
```

Expected: assertion failure for missing capability, not a restore/build error. Record the red.

- [ ] **Step 3: Write the typed session, payload and counter tests before implementing behavior.**

Add `IdenticalCrossLeaseQueryPublishesFreshCompleteWithOneEvaluation`,
`CompleteEmptyPayloadIsReusable`, `ResetAndDisposeReleaseOwnedPayload`,
`ReferenceQueryNeitherReadsNorSeedsReuse` and `WorkerProofReuseCountersRestoreAndMerge`.
The first query executes under lease A, the next identical query under lease B after A is
disposed, with one session and domain. Its original result must remain invalid under B.

```csharp
Assert.Equal(CapsuleFeatureStatus.Complete, second.Status);
Assert.Same(leaseB, second.Lease);
Assert.Same(world, second.QueryWorld);
Assert.Equal(firstPayloadBits, secondPayloadBits);
Assert.Equal(1, counts.Volumes);
Assert.Equal(1, counts.FootprintReuseHits);
Assert.Throws<InvalidOperationException>(() => footprint.AssertFootprintCurrent(first, leaseB));
Assert.Equal(0, afterDispose.Keys);
Assert.Equal(0, afterDispose.Bytes);
```

The fixture owns scenes, explicit leases and token domains. Its bit serializer enumerates all
SupportFootprintMember/Witness/Proposal fields and join words. Reference tests assert zero new
hits/stores in the reference scope and a subsequent optimized miss. Include invalid request,
insufficient member span and short join-span sentinels. Step 2 records the first observed red.

- [ ] **Step 4: Implement the optional world session and bounded exact payload table.**

Implement Interfaces and BB-3's full bitwise request/filter/mode equality. Authenticate receiver,
source, installation/static and lease generations and origin bits at opening, lookup and copy.
Capture immutable metadata only, never the opening lease object. Register one active session
per source receiver/thread, decline nesting and remove it in finally/disposal.

In QuerySupportFootprintCore keep Take and all original validation before lookup. Retain
EnterQuery and current query scratch semantics. A hit builds a new Completed result for the
current lease after all capacity checks, before touching either caller span. Copy/return has
no remaining fallible validation. A miss calls unchanged CollectFootprint and remembers
only Complete. Preserve caller RequiredElements, member/join failure and untouched-tail behavior.
Keep actual Volumes/Proposals accounting at evaluator work sites. Add bounded storage and all
counter/snapshot/merge changes together. Charge real retained capacities, without an allocation
test or graph/work limit change. Source reset discards every entry without eviction.

- [ ] **Step 5: Run the focused green selection.**

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseTests|FullyQualifiedName~WorkerProofReuseCounters'
```

Expected: exit 0. One actual evaluation and one hit across two leases, bit-identical payload,
fresh metadata, old-result rejection, bounded retention and reference bypass. Record counts.

- [ ] **Step 6: Stage explicit Task 1 paths, run staged guards and commit.**

Commit `feat(physics): reuse exact completed footprint payloads within a source session`.
No version, changelog, Movement grouping or Hold change.

### Task 2: Authenticate selected views and adversarial reuse boundaries (BB-3 to BB-5, BB-13, A)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.QueryView.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportFootprintReuse.cs`
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintProofKey.cs`, `KhaozEngine.Physics.Bepu/SupportFootprintProofMemo.cs`, `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportFootprint.cs` only for demonstrated key/publication defects
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseViewTests.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseKeyTests.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseSafetyTests.cs`
- Modify: `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseFixture.cs`, `docs/verification/2026-10-19-bake-proof-reuse.md`

**Interfaces:**
- Consumes Task 1's capability, session, exact key and retention/counter contracts.
- Produces the same capability on the exact Bepu QueryView. The owner core receives the selected receiver and its existing immutable StaticQueryExclusions. No entry is borrowed from the unrestricted world.
- Produces fixture `SerializePayloadBits(in SupportFootprintResult result, ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins) -> byte[]` and `NearMisses() -> TheoryData<string>` enumerating each BB-3 key component. Hold/source-domain values get their Movement tests in Tasks 3/4.
- Produces complete view, collision, payload-budget and refusal coverage for Task 3's selected-view bridge. No public cache-limit knobs.

- [ ] **Step 1: Write the selected-view capability red.**

Add `SelectedViewDeclaresExactFootprintReuseCapability`. Construct a real nonempty exclusion
view and assert it implements the new interface before opening a session. Task 1 implements
only the world, so this fails as an assertion.

- [ ] **Step 2: Run the selected-view test and verify the red.**

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SelectedViewDeclaresExactFootprintReuseCapability'
```

Expected: missing-capability red. Preserve the exact excluded raw handle in the proof record.

- [ ] **Step 3: Write every key, view, lifetime and atomicity control.**

`OneBitKeyDifferenceMisses` changes exactly one field per theory row. Enumerate all axis,
radius, double band, slope/contact-band, both proposal dimensions, all pose/quaternion
components, distances, filter fields and consumed join mode. Include signed zero and invalid
filter/contact-band neighbors, asserting their original exception/refusal rather than forcing
an in-domain query. `HashCollisionRequiresFullEquality` uses a test comparer with constant hash
and production equality. Separate profile/source token changes require reset and zero reuse.

`ChangedExclusionOrReceiverCannotReadAnotherViewsPayload` tests unrestricted, empty-exclusion,
nonempty-exclusion and equal-looking distinct views of one world, plus a foreign source.
`ForwardedQueryConsumesJoinModeExactlyOnce` inserts a forwarding query before the forwarded
request. Compare each actual consumed mode's full matrix to uncached, including after throw.

`StaleRemovedReusedRebasedAndWrongThreadSessionsNeverPublish` covers generation changes,
static removal/reuse, changed origin, view/source disposal and source-domain reset. Mutation
tests close the lease before mutation, then exercise the stale basis with a new lease. Do not
deadlock by mutating under an explicit read lease. A nested open declines and the outer remains
usable. Wrong-thread attempts reject before outputs or table mutation.

`JoinWordBoundaryAndOwnedCapacityRespectPayloadBudget` uses 65 or more raw members, nontrivial
joins across bit 63/64 and spare owned buffer capacity. Test 0 keys, 0 payload bytes, 512-key
saturation and the 2 MiB boundary through the internal memo constructor. Invalid sizes reject.
No capacity fallback changes a result. `RefusedOrShortSpanQueryCannotSeedReuse` covers every
refusal status, short member/join spans and a smaller injected curved-work limit, with sentinel
tails and original exceptions. Restoring the ordinary work policy must force a real evaluation.

```csharp
Assert.Equal(0, missDelta.FootprintReuseHits);
Assert.Equal(uncachedStatus, actual.Status);
Assert.Equal(uncachedRequiredElements, actual.RequiredElements);
Assert.Equal(beforeMemberSentinels, afterMemberSentinels);
Assert.Equal(beforeJoinSentinels, afterJoinSentinels);
Assert.InRange(retention.PeakKeys, 0, 512);
Assert.InRange(retention.PeakBytes, 0, 2 * 1024 * 1024);
```

- [ ] **Step 4: Implement exact selected-view session opening and any proven safety correction.**

Forward capability opening through the owner with the receiver and immutable exclusions.
Authenticate disposal both before opening and on later accesses. Reuse Task 1's payload
owner. Keep exceptions/validation and one-shot mode consumption in their original order.
The table is scoped to one authenticated receiver, not a global dictionary keyed only by source.
Repair only defects demonstrated by Step 3, with no tolerance or budget change.

- [ ] **Step 5: Run the selected-view and safety green selection.**

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseViewTests|FullyQualifiedName~SupportFootprintProofReuseKeyTests|FullyQualifiedName~SupportFootprintProofReuseSafetyTests|FullyQualifiedName~RemovedAndReusedBackendStorageCannotReviveOldGeometry|FullyQualifiedName~WrongThreadAndDisposedViewRefuseCurrentChecksBeforeBackendWork'
```

Expected: exit 0, full equality after forced collisions, no cross-view hit, stale publication or
partial writes, and bounded payloads on every fallback. Record executed test counts.

- [ ] **Step 6: Stage explicit Task 2 paths, run staged guards and commit.**

Commit `fix(physics): authenticate selected-view footprint reuse and refusal boundaries`.

### Task 3: Scope reuse to original same-layer sources in serial and parallel bakes (BB-1, BB-2, BB-6, BB-10, BB-12)

**Files:**
- Create: `KhaozEngine.Locomotion/MovementQueryLease.ProofReuse.cs`
- Create: `KhaozEngine.Movement/ContactNavigationSources.cs`, `KhaozEngine.Movement/ContactNavigationProof.Reuse.cs`
- Modify: `KhaozEngine.Movement/ContactNavigationProof.cs`, `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`, `KhaozEngine.Movement/PhysicsNavBake.ContactSchedule.cs`, `KhaozEngine.Movement/ContactNavigationWorkers.cs`, `KhaozEngine.Movement/ContactNavigationBuildHooks.cs`
- Create: `KhaozEngine.Movement.Tests/ContactNavigationProofReuseTests.cs`, `KhaozEngine.Movement.Tests/Fixtures/ContactNavigationReuseFixture.cs`
- Modify: `docs/verification/2026-10-19-bake-proof-reuse.md`

**Interfaces:**
- Consumes Task 2's selected-view session. The bridge calls it through MovementQueryLease's existing `_view` and `_physics` after AssertCurrent.
- Produces internal `MovementQueryLease.TryOpenSupportFootprintReuse(out IPhysicsSupportFootprintReuseSession? session) -> bool` and `AssertSupportFootprintReuseCurrent(IPhysicsSupportFootprintReuseSession session) -> void`. The second validates both the movement pin and borrowed current physics lease.
- Produces immutable `ContactNavigationRawSource(int ColumnX, int ColumnZ, int SurfaceOrdinal, int RawHeightBits)` and `ContactNavigationSource(ContactNavigationRawSource Raw, int Layer)`.
- Produces `ContactNavigationSources.Record(Vector3 rawFeet, in ContactNavigationRawSource raw) -> void`, `BindLayers(NavSpace rawSpace) -> void` and `TryGet(int layer, int x, int z, out ContactNavigationSource source) -> bool`. Retain original sample ordinals during admission and bind raw-grid cells before resting heights replace them. Ambiguous/unresolved mapping returns false, never picks by resting height.
- Produces `ContactNavigationProof.BeginSource(ContactNavigationSource source) -> IDisposable`, a source-domain marker, and `EnsureSourceSession(MovementQueryLease queries) -> void`, called on the first already-required Step lease. Opening/reset borrows that lease without adding an Acquire. Group disposal clears the active session/domain on the executing thread.
- Produces `ContactNavigationReusePolicy(bool Footprints, bool InitialHolds, bool ReferenceEvaluation)`, captured on the caller and passed to proofs/workers. Until Task 4, InitialHolds is false. Defaults enable capable footprint reuse only. ReferenceEvaluation disables both and propagates the reference scope into workers.
- Produces hook properties `DisableFootprintReuse` and `DisableInitialHoldReuse`, internal booleans, copied before workers start. They change execution only and never enter NavBakeIdentity.

- [ ] **Step 1: Write a real bake red before grouping production changes.**

Add `SameLayerSourceReusesFootprintsAcrossMovementLeases`. Build low-lip-bank with the
existing stable fixture. Assert actual backend hits across direction proofs and unchanged
existing hash. Task 2 has capability but no bake session, so hit count is zero.

```csharp
Assert.Equal("494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922", normalizedHash);
Assert.True(counts.FootprintReuseHits > 0);
Assert.Equal(0, counts.InitialHoldReuseHits);
```

- [ ] **Step 2: Run the real bake test and verify the red.**

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~SameLayerSourceReusesFootprintsAcrossMovementLeases'
```

Expected: zero-hit assertion red with the unchanged hash passing.

- [ ] **Step 3: Write source identity, callback and lifecycle tests.**

`EqualRestingAnchorsKeepOriginalRawSourceAndLayer` constructs distinct raw slots/columns/layers
that share a resting-height value. Assert their complete addresses and separate domains,
including a skipped raw slot before an admitted one. Repeat with parallel admission compaction.
`SourceResetAndUnsupportedViewUseTheOriginalProof` covers every next source, missing capability,
unresolved source mapping, independent profiles, admission and Stair phases, forced bypass
and saturated memo. Serialized bytes and provider request-address trace equal reuse-off.

`FootprintHitStillRunsAnalyticBindingAndMediumRoutes` compares the ordered callback addresses
and outcomes with only footprint reuse toggled, including analytic wins, physics wins and
deterministically injected producer failures. No stateful provider opt-in is required here.
`SourceScopeNeverNestsAnExplicitPhysicsLease` proves opening borrows the first Hold's existing
lease without adding an acquisition or pin, and the next travel lease follows normal disposal.
Preserve any unrelated outer non-nesting refusal.

```csharp
Assert.NotEqual(firstSource.Raw.SurfaceOrdinal, secondSource.Raw.SurfaceOrdinal);
Assert.Equal(referenceProviderTrace, reuseProviderTrace);
Assert.Equal(referenceFile, reuseFile);
Assert.Equal(0, afterSource.Keys);
Assert.Equal(0, afterSource.Bytes);
```

- [ ] **Step 4: Implement the bridge, raw map, immutable policy and source loops.**

Record raw ordinals before serial/parallel compaction, merge that metadata under the existing
cutoff rules and bind the pre-resting NavSpace to its original cells. Keep mapping ownership
separate from anchors. Reset domain for each accepted same-layer source, around the existing
direction loop, inside the worker thread that proves it. Open lazily at the first admitted
edge after its existing prechecks/cutoff, inside Step's existing Acquire, so an empty source or
rejected endpoint does no new producer work. Do not add an acquisition or pin. No constructor
or main-thread worker session is transferred into a worker.

Do not enable sessions during admission or Stair phases. Unwind group scopes on exceptions,
cutoff returns and phase ends. A stale source basis takes existing authentication refusal.
Pass policy explicitly to workers and restore any reference scope in finally. Keep source,
direction, band and failure ordering unchanged. Preserve every normal FootSupport callback.

- [ ] **Step 5: Run focused serial/parallel grouping and lifetime verification.**

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseTests|FullyQualifiedName~ParallelContactBakeFailureTests|FullyQualifiedName~WorkerDiagnosticsScopeRestoresEveryPriorThreadValue'
```

Expected: exit 0, real cross-lease hits, identical provider traces and bytes, no admission/Stair
reuse or retained source payload after completion. Initial Hold counts remain unchanged.

- [ ] **Step 6: Stage explicit Task 3 paths, run staged guards and commit.**

Commit `feat(navigation): scope exact footprint reuse to raw source direction groups`.

### Task 4: Share successful source Holds under stable providers (BB-7, BB-8, BB-9, BB-10, B)

**Files:**
- Create: `KhaozEngine.Movement/ContactNavigationInitialHold.cs`
- Modify: `KhaozEngine.Movement/NavBakeExecution.cs`, `KhaozEngine.Movement/ContactNavigationProof.cs`, `KhaozEngine.Movement/ContactNavigationProof.Reuse.cs`
- Modify: `KhaozEngine.Movement/ContactNavigationSnapshot.cs`, `KhaozEngine.Movement/ContactNavigationWorkers.cs`, `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`, `KhaozEngine.Movement/PhysicsNavBake.ContactSchedule.cs`
- Create: `KhaozEngine.Movement.Tests/ContactNavigationInitialHoldReuseTests.cs`
- Modify: `KhaozEngine.Movement.Tests/Fixtures/ContactNavigationReuseFixture.cs`, `KhaozEngine.Movement.Tests/ParallelContactBakeTests.cs`, `docs/verification/2026-10-19-bake-proof-reuse.md`

**Interfaces:**
- Consumes Task 3's source group, immutable policy and authenticated lease bridge. Initial Hold eligibility is independent of footprint capability.
- Produces public init-only `NavBakeExecution.UseStableContactHoldReuse -> bool`, default false. True asserts BB-8 for serial contact builds. Degree greater than one already supplies that contract. Legacy profiles ignore it.
- Produces internal `ContactNavigationHoldKey(in FramedMovementState initial, in MoveTuning tuning, in MovementQueryScope scope, MovementQueryLeaseId basis, float seconds, int maxSteps, object profileIdentity, ContactNavigationSource source, object providerBasisIdentity)`. It compares BB-7's complete exact bits and fixed token identities. Profile/provider tokens bind composition, footprint and the full authenticated snapshot. Scope comparison uses logical values, never a transient lease/pin/witness object. Changing any constituent misses or invalidates.
- Produces `ContactNavigationInitialHold.TryGet(in ContactNavigationHoldKey key, out MoveState state, out MovementFrameDescriptor frame, out Vector3 feet, out MovementStepOutcome outcome) -> bool`, `Remember(in ContactNavigationHoldKey key, in MoveState state, in MovementFrameDescriptor frame, Vector3 feet, MovementStepOutcome outcome) -> void` and `Reset() -> void`. Store no Selection, lease, binding or continuity.
- Produces `ContactNavigationProof.TryEdgeCore(Vector3 fromFeet, Vector3 toFeet, bool edgeProof) -> bool`. Existing TryEdge calls with true, Admit calls with false. EdgeSteps counts actual StepTowards calls only for edgeProof, including Stair but excluding admission.
- Produces serial snapshot authentication for reused Holds, preserving existing per-edge Acquire and final publication checks. Parallel snapshot retains its current failure-selection semantics.

- [ ] **Step 1: Write the parallel Hold red before changing the API or recurrence.**

Add `ParallelStableProvidersShareOneInitialHoldPerSource` on a small multi-direction flat scene,
degree 2. Assert an unchanged file and positive InitialHoldReuseHits. Task 3 runs every Hold.

```csharp
Assert.Equal(referenceFile, parallelFile);
Assert.True(counts.InitialHoldReuseHits > 0);
Assert.Equal(referenceEdgeSteps - counts.InitialHoldReuseHits, counts.EdgeSteps);
```

- [ ] **Step 2: Run the parallel Hold test and verify the red.**

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ParallelStableProvidersShareOneInitialHoldPerSource'
```

Expected: zero-Hold-hit assertion red. No new serial opt-in is assumed in this first test.

- [ ] **Step 3: Write opt-in, complete-state, slice and stale-provider tests.**

`StableSerialOptInMatchesParallelAndKeepsOneRealHold` compares disabled, footprint-only and
combined modes. Copy every numeric MoveState field, including private state represented by
public semantics, frame and outcome by bits. Verify the next real Step receives null Selection
and captures new bindings/continuity. First rejected Hold is never retained.

`SerialWithoutAssertionPreservesStatefulCallbackTrace` uses a deterministic call-count-sensitive
analytic/medium provider. Null and degree-1 execution without the flag retain all callbacks
and historical bytes. A legacy profile ignores the flag. False defaults do not reject a build.

`ReusedHoldAuthenticatesAnImmediateArrivalAndKeepsItsSlice` changes provider generation before
an otherwise immediate-arrival edge, requiring original refusal. For MaxEdgeProbeSteps 1 and
2 compare copied versus real Hold and exact arrival. Assert no extra travel slice is granted.
`EveryHoldKeyDifferenceMisses` changes each BB-7 input independently, including raw ordinal,
layer, tuning fields, dt, scope, water generation/extent, frame epoch and signed-zero state.
`MutationAfterLastHitRefusesSerialAndParallelPublication` pins final-check coverage.

```csharp
Assert.Equal(referenceNumericStateBits, copiedNumericStateBits);
Assert.Null(nextStepInput.Selection);
Assert.Equal(0, unstableCounts.InitialHoldReuseHits);
Assert.Equal(unstableReferenceTrace, unstableActualTrace);
Assert.Equal(referenceShortBudgetVerdict, reusedShortBudgetVerdict);
Assert.Equal(referenceFailureOrdinal, reusedFailureOrdinal);
```

- [ ] **Step 4: Implement the stable assertion and numeric-only successful Hold memo.**

Pass execution eligibility through profile/worker creation without changing bake options or
identity. Extend the existing snapshot owner for serial eligibility as needed, preserving
its water/frame/provider basis and final assertions. Never claim delegate reference equality
establishes stability without the caller assertion or existing parallel obligation.

Keep edge finite/endpoint-footprint checks first. A reused Hold acquires/authenticates/disposes
the current movement lease normally, then supplies the copied state. Keep Accepted, Near and
slice counting in their original order. The first successful Hold still uses StepTowards.
Travel remains the existing loop with Selection cleared before every real movement step.
Count actual edge Steps and saved Holds separately. Refusal, missing stable eligibility or
any key mismatch runs the complete original Hold. Never seed from placement/admission/Stairs.

Update ParallelContactBakeTests' diagnostic equality to compare equivalent eligibility modes.
Stable serial opt-in and parallel now have matching proof work. Keep separate expectations
for historical serial without opt-in. Do not erase the distinction by ignoring all new counters.

- [ ] **Step 5: Run the focused Hold and provider green selection.**

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationInitialHoldReuseTests|FullyQualifiedName~DiagnosticBuildAtDegreeEightMergesWorkerRecordsAndRestoresCallerState|FullyQualifiedName~ParallelContactBakeFailureTests'
```

Expected: exit 0, one real Hold per eligible source, exact copied state, historical unstable
serial callback behavior, unchanged failures/bytes and no additional arrival slice.

- [ ] **Step 6: Stage explicit Task 4 paths, run staged guards and commit.**

Commit `feat(navigation): reuse initial source Holds under stable provider contracts`.

### Task 5: Certify complete bytes, geometry, arithmetic and scheduling (BB-9, BB-12, BB-13, C and D)

**Files:**
- Create: `KhaozEngine.Movement.Tests/ContactNavigationProofReuseBaselineTests.cs`, `KhaozEngine.Movement.Tests/ContactNavigationProofReuseProtectionTests.cs`, `KhaozEngine.Movement.Tests/ContactNavigationProofReuseScheduleTests.cs`
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseGeometryTests.cs`
- Modify: `KhaozEngine.Movement.Tests/Fixtures/ContactNavigationReuseFixture.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseFixture.cs`
- Modify: `KhaozEngine.Movement.Tests/ParallelContactBakeScheduleTests.cs`, `KhaozEngine.Movement.Tests/ParallelContactBakeFailureTests.cs` only to add reuse-mode coverage
- Modify: `docs/verification/2026-10-19-bake-proof-reuse.md`

**Interfaces:**
- Consumes Task 1 to 4 production interfaces and the unchanged Baselines/BuildFixture/NormalizeFile helpers.
- Produces test-only `ContactNavigationReuseMode` with `Off`, `Footprints` and `Combined`, and fixture `Build(string fixture, ContactNavigationReuseMode mode, int degree) -> ContactNavigationBaselineTests.BaselineBake`. Combined sets stable serial opt-in, Off disables only this lane. Tests use production serialization for actual KENB bytes.
- Produces parameterized baseline rows for all seven fixtures at degrees 1, 2, 3, 8 and total-layer-rows + 1. A theory matrix is a finite correctness check, not repeated stress or a shell invocation loop.
- Produces fixture `ProveInOrder(bool reverseSources, bool reverseDirections) -> byte[]`. It calls the real BeginSource/TryEdge in the chosen order, materializes exits in canonical slots of the same fixture graph and serializes through the existing KENB writer. No production order switch is introduced.
- Produces full-payload geometry checks and named Review Focus thread-transition proof. Any discovered production defect returns to its owning task's exact contract with a deterministic red before a correction. No speculative production edit belongs to this certification task.

- [ ] **Step 1: Write the complete identity and geometry controls before any correction.**

`EveryPinnedFileAndDegreeMatchesUnchangedSerialBytes` compares Off serial, footprint-only
serial, Combined serial and Combined parallel at every named degree. Check actual KENB bytes
at one engine identity, every profile including mixed legacy, existing semantic assertions and
all seven exact BB-12 hashes. Retain original baseline values, no fixture or digest replacement.

`CurvedMeshAndCompoundPayloadsRemainBitExact` compares uncached and session-hit payloads for
tilted cylinders, capsule crowns, tiny faces, coincident statics/children, mesh edge joins,
compound hulls and the installed lane B/C certified-sweep scenes. Include direct-only endpoint
continuity after the hit, reference mode and independent roofs. Compare all fields and join
words with Task 2's serializer, retaining independent existing shape oracles.

```csharp
Assert.Equal(serialActualFile, candidateActualFile);
Assert.Equal(pinnedHash, normalizedHash);
Assert.Equal(uncachedPayloadBits, cachedPayloadBits);
Assert.Equal(referenceDirectJoinDecision, currentDirectJoinDecision);
```

- [ ] **Step 2: Add adversarial controller and arithmetic fixtures.**

`FootprintReuseKeepsPoolAndDirectionalStairRefusals` pins all eight level-support pool rejects,
four asymmetric stair pairs and 28 asymmetric row-band pairs. Test forbidden circular corner,
thin intervening wall, lifted roof, next-raw/headroom boundary, unsupported hole, lower layer
under upper deck and support changing inside the disc in `ProtectionCasesKeepSerialVerdict`.
Compare accepted/refused outputs with Off, without relaxing any guard.

`ArrivalReuseKeepsEuclideanEqualityAndAdjacentFloats` pins exact 0.001 m 3D equality and adjacent
float cases, including a residual where component-wise X/Z tests pass and Euclidean fails.
Include diagonal travel, near-zero and large positive/negative origins, centre/feet round trips
and MaxEdgeProbeSteps exhaustion. Use original operands and arithmetic order, no new epsilon.

```csharp
Assert.Equal(8, rejectedLevelPoolEdges);
Assert.Equal(4, asymmetricStairPairs);
Assert.Equal(28, asymmetricRowBandPairs);
Assert.Equal(referenceVerdict, reuseVerdict);
Assert.Equal(referencePublishedFeetBits, reusePublishedFeetBits);
```

- [ ] **Step 3: Add forced-order, failure and phase-thread cleanup coverage.**

`SourceAndDirectionOrderDoNotChangeBakeBytes` uses ProveInOrder to reverse independent source
and direction visitation while retaining canonical serialized slots.
`WorkerPhaseThreadChangeCannotInheritAReuseSession` deliberately runs successive phases on
distinct threads and proves no session is captured on construction or survives a group.
`ReuseFailureSelectionMatchesSerialUnderForcedBands` forces earlier/later producer failures
with completion gates, preserving lowest serial ordinal and whole-build refusal.
Cover partial setup failure, cutoff return, interrupted join, disposal failure precedence and
reference-policy propagation. No sleeps, retries or shared mutable cache are allowed.

These are tests-first certification controls. Passing controls remain passing. Any red requires
the proper fix in the owning module, recorded with exact failure evidence before its green.

- [ ] **Step 4: Run each focused certification selection once.**

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseBaselineTests|FullyQualifiedName~ContactNavigationProofReuseProtectionTests|FullyQualifiedName~ContactNavigationProofReuseScheduleTests|FullyQualifiedName~ParallelContactBakeScheduleTests|FullyQualifiedName~ParallelContactBakeFailureTests'
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseGeometryTests|FullyQualifiedName~SupportNeighborhoodEquivalenceTests|FullyQualifiedName~CapturedStairExplicitSweepTests'
```

Run synchronously, one command at a time. Expected: exit 0 for both, every matrix row executed,
unchanged hashes/actual bytes and exact geometry, no skipped required proof. Record counts,
degrees and deterministic order cases. No timing gate or new golden is implied.

- [ ] **Step 5: Stage explicit Task 5 paths and any individually reviewed demonstrated fix, run guards and commit.**

Commit `test(navigation): certify proof reuse bytes geometry and scheduling`.

### Task 6: Prove actual saved work and bounded retention (BB-10, BB-11, E)

**Files:**
- Create: `KhaozEngine.Movement.Tests/ContactNavigationProofReuseWorkTests.cs`, `KhaozEngine.Movement.Tests/Fixtures/ContactNavigationReuseWorkFixtures.cs`
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintProofReuseWorkTests.cs`
- Modify: `KhaozEngine.Movement.Tests/Fixtures/ContactNavigationReuseFixture.cs`, `docs/verification/2026-10-19-bake-proof-reuse.md`

**Interfaces:**
- Consumes all six new counters, existing Find/FindMemoHits/Volumes/Proposals and seven curved event categories, retention snapshot and the three modes from Task 5.
- Produces deterministic `ContactNavigationReuseWorkFixtures.WideLip(NavBakeExecution? execution = null) -> ContactNavigationBaselineTests.BaselineBake`, reconstructing the recorded 12 by 48 grid at X [-1.7, 1.3), Z [-6, 6), cell 0.25 m, same low-lip floor and 25 mm lip, capsule radius 0.3 m, DryOnly and 160 edge steps.
- Produces `ContactNavigationReuseWorkFixtures.SingleFlat(NavBakeExecution? execution = null) -> ContactNavigationBaselineTests.BaselineBake` from existing LoadMatrix geometry, X/Z [-0.5, 0.5), cell 1 m, radius 0.2 m, half-height 0.75 m, no water and 64 edge steps. Both retain 1/30 second slices and original tuning. Extend Task 5's fixture Build switch with these two names. No timestamp instrumentation.
- Produces committed target-versus-actual tables for all nine fixtures, accounting separately for the seven pinned totals, footprint-only reductions, saved Holds and combined work. Extra exact equivalences may exceed projections.

- [ ] **Step 1: Write the work and budget assertions before measuring.**

`FootprintOnlyMeetsRecordedTargetsWithoutSkippingFinds` uses BB-11's exact per-fixture table.
Assert unchanged bytes and EdgeSteps, identical Find/callback trace, and at least the projected
avoided Volumes. In the same test assert existing FindMemoHits retain their meaning. Keep
admission and Stair work in the denominator. Do not lower the reference denominator by
subtracting admission or pretend existing memo hits are new savings.

`CombinedModeSavesOnlyCertifiedInitialHolds` checks the exact projected saved-Hold counts and
reference EdgeSteps, separately from geometry reductions. Include wide-lip targets 29,234
avoided of 69,532 and 2,888 saved of 34,992. Single-flat has five footprint evaluations,
zero outgoing edge steps and no saved Holds.

```csharp
Assert.Equal(referenceFile, footprintOnlyFile);
Assert.Equal(referenceFind, footprintOnlyFind);
Assert.Equal(referenceEdgeSteps, footprintOnlyEdgeSteps);
Assert.True(referenceVolumes - footprintOnlyVolumes >= targetAvoided);
Assert.Equal(targetSavedHolds, combinedCounts.InitialHoldReuseHits);
Assert.Equal(referenceEdgeSteps - targetSavedHolds, combinedCounts.EdgeSteps);
Assert.InRange(retention.PeakKeys, 0, 512);
Assert.InRange(retention.PeakBytes, 0, 2 * 1024 * 1024);
Assert.Equal(0, retention.Keys);
Assert.Equal(0, retention.Bytes);
```

`CurvedHitDoesNotReplayCertificationWork` repeats identical successful curved payload requests
under successive leases, comparing all seven event counts to one evaluation. A work-limited
refusal remains uncached and consumes its real work again when called independently. Assert
enforcement still runs with diagnostics off. No curved percentage target is invented.

- [ ] **Step 2: Run the two deterministic work selections once.**

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseWorkTests' --logger 'console;verbosity=normal'
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseWorkTests'
```

Expected: exit 0 when byte, exact Hold, retention and measured target assertions hold. Print
only deterministic counters/fixture IDs. If a target assertion fails, retain the failing exit
and actual counters, identify domain/key/budget eligibility and report the shortfall. Never
force a hit, weaken equality, expand a budget, change geometry or mark that target accepted.
If a proper implementation defect is proved, write its focused regression before repairing
the owning code. A certificate-driven target shortfall remains explicit for the orchestrator.
There is no permission for timing, load, allocation or a repeated benchmark invocation.

- [ ] **Step 3: Record the complete work disposition.**

Write reference, footprint-only and combined counts, hits/misses/stores/budget skips and
retention peaks per fixture and degree. Derive the pinned 46.66% and wide 42.04% footprint
targets and the pinned 5.66% and wide 8.25% Hold targets separately. Do not add percentages.
Record actual reductions even when higher, all misses and any shortfall. Curved event counts
are actual work, not replayed historical work. State zero booked savings for candidates 3/4
and the rejected 5/6 reasons. Keep #1401 world-scale cost and #1334 timing acceptance open.

- [ ] **Step 4: Stage explicit Task 6 paths, run staged guards and commit the honest evidence.**

Commit `test(navigation): record exact proof reuse work and bounded storage`.
Return a failed target as an explicit blocker to performance acceptance, never a false green.

### Task 7: Publish contracts and finish staged 21.5.2 verification (BB-14, F)

**Files:**
- Modify: `KhaozEngine.Physics/README.md`, `KhaozEngine.Physics.Bepu/README.md`, `KhaozEngine.Movement/README.md`, `docs/USING-KHAOZENGINE.md`
- Modify: `docs/design/CONTACT-BAKE-PROOF-REUSE-2026-10-19.md`, `docs/INDEX.md`, `docs/verification/2026-10-19-bake-proof-reuse.md`
- Modify: `CHANGELOG.md` (append only to staged 21.5.2 and roll its date)

**Interfaces:**
- Consumes accepted Task 1 to 6 commits and their reds, exact payload/byte matrix, provider controls, counters and explicit shortfalls if any.
- Produces living capability/serial assertion documentation and a once-only full Release verification record. KhaozEngineVersion remains 21.5.2. No guarded version declaration, package/schema identity, release or integration action changes.

- [ ] **Step 1: Self-review all BB requirements and the measured acceptance before the full run.**

Map BB-1 to BB-14 and acceptance A to F to concrete tests/evidence. Verify the complete key,
current lease publication, raw ordinal mapping, stable serial opt-in and all five Review Focus
tests. A target shortfall is reported separately and blocks claiming that target, even if
correctness passes. Do not repeat a focused check already run after the last relevant edit.

- [ ] **Step 2: Update living docs and perform the Markdown name/behavior sweep.**

Document optional capability discovery, non-owning/session/source lifetime, fresh metadata,
exact key, 512/2 MiB limits, unsupported fallback and serial assertion examples. Describe
default serial callback behavior and the existing parallel stability obligation. State that
reference evaluation bypasses both layers and that existing interval evidence never escapes.
Search all Markdown for `SupportFootprint`, `SupportRequestMemo`, `NavBakeExecution`,
`UseStableContactHoldReuse`, `Hold`, `parallel contact` and `#1401`. Correct invalid living
claims. List any additional affected doc explicitly before staging. Preserve historical cost
records and original lane C evidence rather than rewriting them as new savings proof.

- [ ] **Step 3: Append the verified behavior to staged 21.5.2 and roll its date.**

Re-read Directory.Build.props, the latest tags and current main. Under BB-R1 this lane rides
the existing untagged 21.5.2 entry. Append exact completed-footprint and stable-source Hold
reuse with byte-identical contact bakes (#1401). Roll its entry date to the actual finishing
day. Preserve its existing represented-support/curved-work bullets and every released entry.
Do not bump KhaozEngineVersion or guarded declarations. If concurrent integration has released
or replaced that staged entry, report the changed premise to the orchestrator before editing.

- [ ] **Step 4: Run the full Release build, full solution tests and format verification once.**

```bash
build-slot --label bake-b-design -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
build-slot --heavy --label bake-b-design -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --verbosity diagnostic
```

Run synchronously and record each exit. Expected: zero build warnings/errors, full discovered
solution selection with zero failures and format exit 0 without changes or suppressed
workspace-load warnings. Record project counts/skips and verify both changed test projects
load for format. Plain tests skip GPU facts, and this CPU lane changes no GPU work.
No retry or second full suite without a new change/failure that justifies affected verification.

- [ ] **Step 5: Stage explicit changed paths and run staged and whole-tree governance.**

```bash
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --cached --check
```

Expected: all exit 0. Inspect the final diff for any changed golden, arithmetic/tolerance,
raw identity, callback default, version or historical timing claim. No manual client validation
or consumer pin adoption belongs to this lane.

- [ ] **Step 6: Commit explicit owned paths and return evidence to the orchestrator.**

Commit `docs(navigation): record proof reuse contracts and 21.5.2 verification`.
Return task commits, original reds, focused greens, once-only full command exits, exact file
matrix, actual counter targets/shortfalls and retention/provider/failure evidence. Stop at the
verified commit. No push, merge, GitHub comment, release, tag or subagent dispatch.

## Plan self-review and handoff

BB-1 to BB-5 map to Tasks 1/2, BB-6 to Task 3, BB-7/BB-8 to Task 4, BB-9/BB-12/BB-13 to
Tasks 2 to 5, BB-10 to Tasks 1/3/4/6, BB-11 to Task 6 and BB-14 to Task 7. Every acceptance
group has an executable proof. All five Review Focus items have a named test in the owning
task. Interfaces use the same session, token, raw source, policy, Hold and counter names across
tasks. Red capability/grouping/Hold assertions precede the corresponding production behavior.
General shape and arithmetic controls retain their independent oracles and original questions.
Capacity, reference, unsupported and stale paths never create a permissive alternative verdict.
The work targets cannot overrule correctness, and any shortfall remains a reported shortfall.

The design author commits only this plan, its spec and the index row, then stops. The owning
orchestrator reviews both written artifacts and chooses implementation execution separately.
This plan grants no new implementation, integration, release, timing or load authorization.
