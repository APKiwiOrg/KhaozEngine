# Generic Swimming Foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans for the approved inline route,
> with one independent whole-branch review. Steps use checkbox syntax for tracking.

**Goal:** Implement opt-in generic surface swimming, surface jumps and non-swimmer boundaries
with complete collision and explicit environment-query availability, before native adapters.

**Architecture:** A physics-owner lease binds live solid queries to a prepared environment pin.
Locomotion composes canonical membership/support facts with one capsule solver. Movement and
NetWorld consume the same solver and state, without depending on MapDoc or rendering.

**Tech Stack:** .NET/C#, existing Physics/Bepu, Primitives, Locomotion, Movement and NetWorld.

**Spec:** [Immutable F3](../../design/SWIM-ENVIRONMENT-FACADE-F3-2026-10-06.md), accepted by migration
at engine 404fa519f, SHA-256 d508ac3b5bed503a30e12e044c91a9a33924f13184efe750961a8c496559f572.
Section 8's inherited F2 reference means F3 in this plan. No semantic change is made to F3.

**Status:** G1a Tasks 1 to 6 approved on 2026-10-06 by coordinator thread
938c801f-2df0-4298-8bd1-dc39e9015e60 under the owner's explicit overnight delegation, against
plan commit 49591549d and immutable F3. Execute inline sequentially. This is delegated design/plan
approval, not human visual acceptance, a release or native G1b approval. Request each local heavy
run window from the coordinator. Before Task 6, reconcile pivot's committed #1313 presentation
phase overload and preserve its ownership of ClientPrediction/AdvancePresentation changes.

## Global Constraints

- G1a is active under the recorded delegated coordinator approval. Do not widen that scope.
- Native G1b, R2/R3/R4, real native scope/seam/bake proofs, game adoption and WH1 carve stay gated.
- Migration owns #1300/#1301. No production MapDoc sampler, storage or residency scheduler here.
- Legacy mode is default and remains behaviorally unchanged. Explicit mode has no unknown-to-dry fallback.
- Surface band 0.03 m inclusive, contact skin 0.001 m, active contact margin at most 0.002 m.
- Limits are 64 coverage spans, 256 domain contacts and 8 corrections per segment. Exhaustion discards the whole tentative step.
- Water jump uses independent speed, initially derived by the consumer from a 1.0 m apex. No submerged ascent input.
- Swim pace is supplied by the consumer, initially 3 m/s, with its 0.65 backpedal scale.
- WorldFrame retains its released planar envelope and Y datum. No silent precision relaxation.
- Commands are consumed once even on explicit query refusal. No new jump epoch or hidden buffered submerged press.
- No game combat, inventory, journal or animation policy in the engine.
- No new third-party dependency, package or broad umbrella reference. Preserve GPU-free server closure.
- One build/test/bake at a time through the shared slot. No load/stress runs or repeat-until-green loops.
- Current repo AGENTS, KESIZE, Release checks, main reconciliation and docs sweep apply. Never tag.

Native dependency checkpoint, 2026-10-07: coordinator OA17/CD9 approved the complete R2 written plan
at engine `05e0d6948755e26b590c8fc2fd50c1c471b3f0c3`,
[`2026-10-05-world-authoring-r2-authored-terrain-paint.md`](https://github.com/APKiwiOrg/KhaozEngine/blob/05e0d6948755e26b590c8fc2fd50c1c471b3f0c3/docs/superpowers/plans/2026-10-05-world-authoring-r2-authored-terrain-paint.md).
This is an approved planned producer contract, not released API or G1b acceptance. Planned seams are
`MapScopedSurfaces.Acquire`, factory-owned identity/read witness, `MapSpaceMembership.Query(MapFramePoint)`,
`MapSupportQuery.Select/EnumerateCandidates`, and `MapSpaceRelations.Relate`. Immutable context reuse and
masked compilation preserve the view witness. The 256-patch/262144-face limits bound validation work,
not world size, and query contexts retain their declared query budgets. R2 Task 1 still requires the
actual owner-released R1 tag, successful publication and a fresh base check. R3/R4 plans and actual
adapter interfaces need their later approvals. Migration retains native producers, #1300 and #1301.

## Review Focus

1. A callback tries to mutate or rebase the live physics owner during a query lease (Task 1).
2. Two thin walls have tied impacts but no overlap at the original requested end pose (Tasks 2/4).
3. Water above a dry cave or below a bridge overlaps XZ but not the swept capsule (Tasks 3/4).
4. Correction after a predicted portal crossing reuses future support/space selection (Tasks 3/6).
5. A consumed press on unresolved data launches later when data becomes available (Tasks 5/6).

## Owner amendment presented with this plan

Replace only the swimming parent plan's P0 exit condition that blocks all generic work until
native R2/R3/R4 signatures are approved. Permit Tasks 1 to 6 below after owner approval of this
plan, using migration-accepted F3 and synthetic fixtures. That is G1a.

G1a explicitly includes enforced query leases and complete real-Bepu collision prerequisites.
It is not permission to implement just two tuning knobs while assuming those prerequisites.
Keep G1b for native adapter implementation, canonical native completeness, actual seam/bake
acceptance and game integration. No world edit, native release or art acceptance is included.

## Public API and package decisions

New types are proposed here for implementation, not claims about released signatures. Fields
are immutable unless explicitly identified as carried MoveState fields. Constructors validate
finite inputs and known-result invariants. Invalid/default keys never count as valid known data.

### Physics owns the live read boundary and solid contacts

Create `KhaozEngine.Physics/IPhysicsQueryLeaseSource.cs` containing:

```csharp
public interface IPhysicsQueryLeaseSource
{
    IPhysicsQueryLease AcquireQueryReadLease();
}
public interface IPhysicsQueryLease : IDisposable
{
    IPhysicsWorld SourceWorld { get; }
    Vector3 Origin { get; }
    long GeometryGeneration { get; }
    void AssertCurrent();
}
```

Keep this an optional capability interface. Bepu and its restricted query view implement it.
Legacy backends/test doubles remain source-compatible. Explicit movement refuses a missing capability.
The concrete Bepu lease is exclusive and thread-affine because existing query scratch buffers are
not safe for concurrent readers. Public queries on another thread use the same gate. Nested query
calls on the owning thread are allowed, acquiring another lease is not. Dispose is idempotent only
on the owning thread. Exceptions release the gate in finally.

Create `KhaozEngine.Physics/CapsuleContact.cs` with `CapsuleContact` containing `Vector3 Normal`,
`float Separation`, `bool Dynamic`, `int BodyHandle`, `int ChildIndex`, `int FeatureId`.
Separation is signed, positive while apart. These owner handles are local provenance only.
`CapsuleContactResult` contains `bool Complete`, `int Written`, `int RequiredCapacity` and
`float CertifiedErrorMetres`. No prefix is usable when Complete is false.

Create optional `IPhysicsCapsuleContacts` with:

```csharp
CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose,
    float maxSeparationMetres, Span<CapsuleContact> destination,
    QueryFilter filter = default);
```

Bepu/query views implement it using the real manifold batcher, all relevant broad-phase candidates,
the requested speculative margin and view exclusions. Include nonpenetrating contacts through the
inclusive margin. Child contacts are not reduced to the deepest one. Preserve legacy ComputePenetration.

### Locomotion owns facts, composition and policy

Create cohesive files, not one file per small record:

| File | Defined contract |
| --- | --- |
| `MovementEnvironmentIdentity.cs` | `MovementDomainKey`, `MovementSpaceKey`, `MovementSupportKey`, each `(string WorldId, string LocalId)`. `MovementQueryIdentity(string ClosureId, uint PolicyVersion, string ScopeDigest)`. `MovementFrameDescriptor(WorldFrame Frame, Vector3 PhysicsOrigin, ulong Epoch)` |
| `MovementEnvironmentFacts.cs` | F3 `MovementAvailability`, `MovementBodyQuery`, `MovementWaterPoint`, `MovementWaterInterval`, `MovementSupportRequest`, `MovementSupportCandidate`, `MovementSupportSet` |
| `MovementWaterCoverage.cs` | F3 `MovementMediumSweepQuery`, `MovementCoverageSpan`, `MovementDomainContact`, `MovementCoverageResult`, including both buffer counts and complete identity |
| `IMovementEnvironmentProvider.cs` | Prepare/pin interfaces below, no storage or native geometry implementation |
| `MovementEnvironmentContext.cs` | Lease acquisition order, identity/frame/scope checks, buffers and atomic tentative result handling |
| `MovementSupportResolver.cs` | Legal-space/portal and interval/slope/clearance filtering, highest support, canonical seam-only continuity |
| `MovementCapsuleResolver.cs` | Earliest swept impact, complete active constraints, projection and retrace, initial penetration and bounded correction |
| `WaterTraversalPolicy.cs` | `WaterTraversalMode`, `WaterTraversalPolicy` and surface/deep eligibility |
| `ExplicitCharacterMovement.cs` | Public explicit step and shared camera-relative/world-direction core |
| `WaterExcursionState.cs` | Enum None=0, Surface=1, AirborneFromWater=2 |

Pin the referenced value fields as follows. Availability and nullable data are validated together,
so a known wet result cannot omit its domain/interval, and a known dry result cannot invent one.

| Value | Fields |
| --- | --- |
| `MovementBodyQuery` | `Vector3 Centre`, `float Radius`, `float HalfHeight`, `MovementSpaceKey CurrentSpace`, `MovementSupportKey? CurrentSupport` |
| `MovementWaterInterval` | `float LowerY`, `float UpperY`, `float NominalSurfaceY`, `bool UpperIsFreeSurface`, `string LowerBoundaryId`, `string UpperBoundaryId` |
| `MovementWaterPoint` | `MovementAvailability Availability`, `MovementSpaceKey Space`, `MovementDomainKey? Domain`, `bool InWater`, `float SpeedScale`, `MovementWaterInterval? Interval` |
| `MovementTransitionContext` | `MovementSpaceKey OriginSpace`, `Vector3 StartFeet`. The provider certifies the legal portal/link path from this origin to the queried body, not a client-supplied permission bit |
| `MovementSupportRequest` | `MovementBodyQuery Body`, `float MaxRise`, `float MaxDrop`, `float MaxSlopeRadians`, `MovementTransitionContext Transition` |
| `MovementSupportCandidate` | `MovementSupportKey Owner`, `MovementSpaceKey Space`, `Vector3 Feet`, `Vector3 Normal`, `string? TraversedLinkId` |
| `MovementSupportSet` | `MovementAvailability Availability`, `int Written`, `int RequiredCapacity`, `MovementQueryIdentity Identity` |
| `MovementMediumSweepQuery` | `MovementBodyQuery Body`, `Vector3 Delta` |
| `MovementCoverageSpan` | `float EnterFraction`, `float ExitFraction`, `int ContactStart`, `int ContactCount`, `bool HasDryCoverage` |
| `MovementDomainContact` | `MovementDomainKey Domain`, `MovementSpaceKey Space`, `MovementWaterInterval Interval`, `Vector3 Normal`, `float Fraction`, `string BoundaryId`, `uint CoverageRegionHandle`. The final handle is pin-local provenance for the clipped region, never wire/persistent data |
| `MovementCoverageResult` | `MovementAvailability Availability`, `int SpansWritten`, `int RequiredSpanCapacity`, `int ContactsWritten`, `int RequiredContactCapacity`, `float CertifiedErrorMetres`, `MovementQueryIdentity Identity` |
| `MovementPreparationResult` | `MovementAvailability Availability`, `MovementQueryIdentity Identity` |

The provider interface is `Prepare(in MovementQueryScope scope) -> MovementPreparationResult`
outside the physics gate, then `TryPinPrepared(in MovementQueryScope scope,
IPhysicsQueryLease physicsLease, out IMovementEnvironmentPin? pin) -> MovementAvailability`.
Prepare starts no background work owned by the movement kernel. A native caller can prefetch
through its scheduler before stepping. Pin performs no I/O and returns Unresolved if not ready.

`MovementQueryScope` contains `Vector3 Min`, `Vector3 Max`, `float MaxRise`, `float MaxDrop`,
`string WorldId`, nullable `MovementSpaceKey? CurrentSpace`, `MovementQueryIdentity Identity`,
`MovementFrameDescriptor Frame`. The coordinator-approved, migration-compatible
[CS1 amendment](../../design/SWIM-COLD-SELECTION-SCOPE-AMENDMENT-2026-10-07.md) adds the producer-bound
WorldId and nullable acquisition hint. The provider exposes its actual served WorldId. Cold
reconstruction requires a null-hint witness and cannot reuse a selected-space certificate.
The pin exposes its certified scope and identity, actual source
world/generation/frame binding, and these methods:

```csharp
MovementWaterPoint SampleCentreWater(in MovementBodyQuery body);
MovementSupportSet EnumerateSupport(in MovementSupportRequest request,
    Span<MovementSupportCandidate> candidates);
MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
    Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts);
MovementAvailability RebuildSelection(in FramedMovementState state,
    out MovementSelection selection);
void AssertCurrent();
```

`IMovementEnvironmentPin` is disposable and implements no mutator. `MovementSelection` contains
stable occupied-space and nullable support keys plus identity. Local lookup handles may be cached
inside the pin only. RebuildSelection cannot reuse discarded predicted context. Native adapters
later prove canonical reconstruction or require an explicit transport amendment.

`MovementEnvironmentContext` constructor takes exact `IPhysicsWorldQueryView`, provider and
portable query identity. `TryAcquire(in MovementQueryScope scope, out MovementQueryLease? lease)`
returns MovementAvailability. It acquires source physics gate first, then prepared environment
pin. `MovementQueryLease` exposes the validated fact methods above and solid queries, and owns
release. It is not a second geometry provider. Caller commits pure state/output inside its scope,
then releases before any physics mutation. A queued writer revalidates frame/generation afterward.

`WaterTraversalPolicy` contains Mode, SurfaceJumpSpeed, SurfaceContactToleranceMetres=0.03,
ContactSkinMetres=0.001, MaxCoverageSpans=64, MaxDomainContacts=256, MaxCorrections=8.
All affect policy identity when they affect results. Existing MoveTuning provides capsule, pace,
directional scale, hysteresis, buoyancy, step/slope and gravity. Do not add unused cost knobs.

`FramedMovementState` contains `MoveState State`, `MovementFrameDescriptor Frame`, and nullable
`MovementSelection Selection`. This explicit kernel value is not a new wire format. NetWorld maps
its current framed state into it. On correction/restore/handoff, Selection is null and rebuilt.
`MovementStepResult` contains framed State and `MovementStepOutcome` (Advanced, Blocked,
EnvironmentUnresolved, EnvironmentInvalid, PlacementRefused, FrameMismatch). It cannot claim an
Advanced state after incomplete queries. Held state remains correctly framed, clears transient
step events and consumes no physics/world effect.

Public entry points:

The caller acquires the query lease, steps and publishes pure state/output before releasing it.
The explicit kernel receives that lease rather than acquiring and disposing one internally before
its caller can publish. The context remains the acquisition/composition object. This concretizes
F3's lifetime rule without changing its semantics.

```csharp
MovementStepResult ExplicitCharacterMovement.Step(in FramedMovementState state,
    in MoveCommand command, float deltaSeconds, in MoveTuning tuning,
    in WaterTraversalPolicy water, MovementQueryLease queries);
MovementStepResult ExplicitCharacterMovement.StepTowards(in FramedMovementState state,
    Vector2 direction, bool run, float deltaSeconds, in MoveTuning tuning,
    in WaterTraversalPolicy water, MovementQueryLease queries);
```

Retain existing CharacterMovement entry points and default behavior. Do not append facade
parameters throughout legacy overloads. Reuse existing collision math where its proof applies,
but never call the bypassing legacy SwimStep as explicit-mode collision.

## Task 1: Physics read lease and mutation fence

**Files:** Create the lease seam above and `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.QueryLease.cs`.
Modify Bepu core, QueryView, Queries and Rebase at their public entry boundaries.
Tests: new `KhaozEngine.Game.Tests/Physics/PhysicsQueryLeaseTests.cs` beside existing query-view tests.

**Mutation inventory:** AddStatic, RemoveStatic, AddDynamic, RemoveDynamic, SetDynamicVelocity,
AddConstraint, RemoveConstraint, SetConstraintTarget, Step, Rebase and Dispose. Internal removals
already inside a writer scope must not reacquire a read scope. Query-view Dispose must respect an
active lease that pins it. The complete owner remains responsible for all geometry generation.

- [ ] Write finite tests for every listed mutation under a lease, plus disposed lease, wrong-thread
      use, nested lease, thrown query cleanup and a writer after release. Assert mutation did not occur.
- [ ] Verify the environment pin-order integration in Task 3, where the real context exists. Task 1
      proves the underlying physical lease rather than asserting behavior of a fake-only composition.
- [ ] Run the new filtered fixture once, expecting missing capability/fence failures.
- [ ] Implement exclusive gate/lease and audited mutation/query entry guards. Keep diagnostics developer-only.
- [ ] Run the fixture and existing PhysicsQueryViewLifecycleTests/PhysicsRebaseTests once. Commit
      `feat(physics): add scoped read leases for movement queries`.

## Task 2: Complete capsule contacts on the real backend

**Files:** New seam/contact types above, `BepuPhysicsWorld.CapsuleContacts.cs`, QueryView forwarding.
Tests: new `KhaozEngine.Game.Tests/Physics/CapsuleContactCoverageTests.cs`.

- [x] Add tests using real Bepu solids for bed plus shallower wall, post plus low ceiling, two tied
      thin walls, positive separation exactly 0.002 m, beyond-margin exclusion, compound child contacts,
      insertion-order reversal and too-small destination buffer. Assert complete normal sets, not only counts.
- [x] Run RED, then implement complete candidate/manifold gathering with explicit margin and exclusions.
      Sort/reduce geometrically equivalent constraints deterministically. Never discard a distinct normal.
- [x] Validate error bounds against analytic box/capsule fixtures and existing mesh/hull regressions.
      Uncertifiable backend cases return incomplete, never clear.
- [x] Run new and affected penetration/query-view tests once. Commit
      `feat(physics): expose complete capsule contact coverage`.

## Task 3: Explicit environment values, context and support selection

The [approved Task 3 API details](../../design/SWIM-ENVIRONMENT-TASK3-API-DETAILS-2026-10-07.md)
complete the witness/pin signatures, finite capacity limits and six migration lifecycle/identity clarifications.

**Files:** New Locomotion fact/identity/provider/context/support files above.
Tests: new `KhaozEngine.Game.Tests/Locomotion/MovementEnvironmentContractTests.cs` and test-only
`Locomotion/Fixtures/AnalyticMovementEnvironment.cs` using real Bepu solids.

- [ ] Write failures for invalid/default values, source/generation/frame mismatches, incomplete scope,
      stale pin, capacity refusal and coordinate conversion. Assert no old-frame state is relabelled.
- [ ] Prove prepared-environment pinning occurs after the physical gate, preparation does no I/O under
      that gate, and failed pinning releases the real physical lease. Use observable mutation refusal and
      successful post-release writes, not only a fake call-order log.
- [ ] Add dry cave under ocean, stacked intervals, partial/flooded low-ceiling chambers, shaft portal,
      dry bridge and wet below-deck cases for both centre membership and full swept coverage.
- [ ] Add legal higher step, independent equal-height-owner ambiguity, missing geometry and a corrected
      pose after a predicted portal crossing. Assert legal filtering then highest support and fresh reconstruction.
- [ ] Implement values/context/selection composition. The test adapter is finite analytic fixture data,
      not a production MapDoc parser, terrain sampler or native storage implementation.
- [ ] Run filtered tests once after the change. Commit
      `feat(locomotion): add explicit scoped movement environment`.

## Task 4: Shared capsule traversal and opt-in deep boundary

**Files:** New resolver/policy/explicit step files above. Modify
`KhaozEngine.Movement/GroundMoveContext.cs`, `GroundMoveContext.Swim.cs` and `SwimTraversalProbe.cs`
only to opt into the shared explicit solver without changing legacy paths.
Tests: `CharacterMovementNonSwimmerTests.cs`, `ExplicitSwimCollisionTests.cs` and Movement profile tests.

- [ ] Write tests for 0.8 m and 1.5 m bodies, 0.65 enter threshold, permitted shallow movement, tangent
      slide, inward block, zero displacement, initial overlap and one step crossing a thin deep interval.
- [ ] Assert every corrected segment is traced, a dry centre cannot hide wet outer coverage, vertical
      bridge/cave motion never creates projected-water contacts, and invalid/capacity paths commit no prefix.
- [ ] Add earliest tied-impact active-contact tests and reversed insertion order. Test all contacts at
      TOI within skin+error, project feasible remaining velocity and retrace, including support corrections.
- [ ] Run RED, implement the shared resolver and explicit policy, then run the focused tests plus existing
      legacy CharacterMovementSwimTests and movement traversal fixtures once.
- [ ] Assert runtime and profile probe use the same resolver/outcomes, then commit
      `feat(locomotion): unify explicit swim collision and deep-water boundaries`.

The #426 coordination observation is within this existing explicit-solver boundary: released
v20.25.0 `CharacterMovement.Step` enters `SwimStep` without passing physics and returns before dry
collision. A post-bake wall therefore cannot characterize a failed swimming stop on that baseline.
Include a real-backend explicit swim traversal fixture with a newly added wall and the exact live
movement view/profile. Preserve the deliberately unconfigured legacy path. This planned coverage
does not resolve duck population/stop-spacing policy or claim shipped/native adoption.

## Task 5: Surface jump and water-origin arc

**Files:** Add carried enum to MoveState and new explicit fluid/air step concern under Locomotion.
Tests: new `KhaozEngine.Game.Tests/Locomotion/CharacterMovementSurfaceJumpTests.cs`.

- [ ] Write apex tests over 2 m/100 m lower bounds, 1.0 m and independent 0.5 m configured apex, inclusive
      0.03 m contact band and one float value either side. Add 30 Hz and far/deep framed equivalents.
- [ ] Assert below-band Space is consumed without buffering/ascent, first launch rises without buoyancy
      recapture, new airborne press refuses, ceiling clips ascent, and descent into another valid surface
      or supported bank/wading landing produces the correct excursion state.
- [ ] Assert 3 m/s forward/strafe and 1.95 m/s backward at scale 0.65 through the water-origin arc,
      independent of run selection. Known fully flooded intervals have no surface launch or nav node.
- [ ] Run RED, implement explicit excursion transitions and independent launch speed, run focused/legacy
      jump/swim fixtures once, then commit `feat(locomotion): add configurable surface water jumps`.

## Task 6: Network, persistence and synthetic profile proof

**Files:** NetWorld `MovementState.cs`, `PlayerMoveState.cs`, `PlayerMoveSimulator.cs`,
`PlayerMovementSystem.cs`, `MovementComponents.cs`, `MoveProtocol.cs`, `BuiltinBlobLayout.cs`,
`WorldServer.cs`, `ShardedWorldServer.cs` and narrow current configuration homes for opt-in contexts.
Include `WorldClient` prediction/reconciliation call sites so each simulation/replay read interval
remains leased until its pure state is stored. Do not release a simulator-internal lease before
the prediction owner publishes its result.
Keep pure water-origin state as the one appended byte, not local lease/support pointers.
Tests extend existing Server.Tests NetWorld swim and ShardedPlayerMoveSwim fixtures.

- [ ] Add failing snapshot encode/decode, old blob defaulting/migration, authoritative correction,
      pending-command replay, delayed remote sampling, cold restore, teleport/reset and cell handoff tests.
- [ ] Prove existing dequeue/ack consumes unresolved/invalid/refused presses. Restored data must not
      launch a consumed command. Test duplicate sequence and catch-up discard. No extra jump epoch.
- [ ] Clear/rebuild environment selection from corrected/transferred state, never predicted future.
      Require valid explicit destination classification before publishing restored state.
- [ ] Wire the explicit context through both heads and prediction, add excursion byte/handshake identity
      and registry/blob layout handling, preserving the default legacy composition.
- [ ] Add synthetic profile/seam equivalence and parameter identity tests in Movement.Tests. Label them
      generic-only evidence. Native G1b completeness and real bake/seam proofs remain pending.
- [ ] Run focused Server/Game/Movement fixtures once. Commit
      `feat(networld): preserve explicit water traversal through prediction and handoff`.

## Commands, review and finish

For each task's RED/GREEN run, use its named fixture in the matching project through the shared
slot. Example Task 1 command, from the engine worktree root:

```bash
/tmp/grimhollow-orch/slot-run.sh swim-lease-tests /tmp/swim-lease-tests.log -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~PhysicsQueryLeaseTests'
```

Expected RED is the specified missing behavior, not a broken fixture or unrelated compile error.
Expected GREEN is exit 0 with the selected tests passed. Broaden only for the task's named affected
regressions. Do not rerun a passed suite unchanged.

After Tasks 1 to 6: fresh whole-branch review against F3 and this plan, fix material findings with
focused red/green proof, reconcile current main and run required full engine Release build/test,
format and repository guards serially. Full test command is
`dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`.
Read current AGENTS and release rules for pin/changelog/pack checks at execution time. Coordinate
with the engine orchestrator and push verified work. Use a private feed for any pre-merge pack.
Do not tag or claim released packages. Game adoption waits for an owner-authorized release.

The plan self-review covers all six F1 dispositions and four F2 corrections, concrete mutation
entry points, package direction, all carried-state routes and error outcomes. Every Review Focus
line has an owning task. Full compile-ready test bodies are written at each RED step against these
exact contracts. If implementation reveals a semantic impossibility, stop that dependency and
return the measured evidence, not a weaker fallback hidden behind green synthetic tests.

## Execution checkpoint

Task 1 implemented under the delegated G1a approval. Initial runtime RED compiled and failed all
18 cases at the missing lease capability. The implemented optional seam and Bepu owner/view
fences passed all 18 focused cases, then all 20 named query-view/rebase/penetration-allocation
regressions. Both commands exited 0. No full engine suite, final format, whole-branch review,
version selection, main merge, pack or release is claimed at this task checkpoint.

The initial reflection bootstrap allowed a true runtime RED before the new interface existed.
Final tests use the public interface directly, with unchanged behavioral assertions. The environment
pin-order proof is assigned to Task 3's real context rather than a fake-only test in Task 1.
