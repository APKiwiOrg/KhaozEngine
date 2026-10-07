# Capsule Feature Correspondence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans to implement this plan task-by-task.
> Use subagent-driven-development only when routed capacity becomes available and ownership is explicit.
> Steps use checkbox syntax for tracking.

**Goal:** Prove finite capsule/static feature correspondence, then use that proof to repair the legacy
low-lip support and navigation resting-anchor defect without changing raw layer identity.

**Architecture:** Physics exposes one optional query with complete incident geometry and explicit
refusals. Bepu owns installed-shape access and bounded numerical proof. Locomotion owns support
eligibility, while navigation proves directed movement between private resting anchors.

**Tech Stack:** C#, System.Numerics, Bepu 2.4, xUnit, existing query leases and navigation capture.

**Spec:** [Approved feature specification](../specs/2026-10-07-capsule-feature-correspondence-design.md)
at `44acfe9b9`, approved by the owner on 2026-10-07.

## Global Constraints

- Execute inline while Gauge has no assignable worker. Do not duplicate the swimming implementation.
- Max two explicitly owned bounded heavy commands, through the primary/secondary shared wrappers.
- No test loops, stress, arbitrary ray inset, blanket tolerance widening or weakened safety assertions.
- Preserve the exact read-lease instance and selected receiver, source/origin/generation and exclusions.
- Refusals/exceptions leave face spans untouched. No usable partial output.
- Reuse the spec's statuses, domains, caps and error ceilings exactly. They are proof targets.
- No release, game pin, native producer, authored world change or bake format change in this plan.
- Ride current staged engine metadata after fresh-main reconciliation. Shared feed/main writes remain
  exclusive. The required engine post-push pack is coordinated separately from local test slots.
- A failed gate stops its dependent implementation, while unrelated approved work may continue.

## Review Focus

- A wrong-thread query must fail before trying to acquire a lease-owned monitor. Task 2 tests this.
- Equal metadata on successive leases must not revive old output. Task 2 tests reference identity.
- Different finite faces on one static must not become the same support. Tasks 4 and 5 test this.
- A coplanar crack or non-manifold edge must not be welded by epsilon. Task 5 tests exact incidence.
- A navigation proof must preserve raw layer identity and refuse a roof squeeze after lifting its
  capsule. Tasks 6 and 7 retain the original arrival, footprint and roof controls.

---

### Task 1: Integrate the existing read-lease prerequisite

**Files:** The owner-confirmed subset of swimming commit `80734c9b1`, including
`IPhysicsQueryLeaseSource.cs`, Bepu `QueryLease.cs`, owner/query/view/rebase fencing,
`PhysicsQueryLeaseTests.cs`, and their Physics/Bepu/consumer documentation.

**Interfaces:** Consume the existing `AcquireQueryReadLease()` and `IPhysicsQueryLease` unchanged.
Produce a verified shared-main prerequisite, not a second lease implementation.

- [ ] Obtain exact extraction ownership, original RED/GREEN evidence and caveats from swimming.
- [ ] Create `fix/physics-query-lease-prerequisite` from current local engine main. Extract only the
  reviewed lease slice, excluding swimming plan progress and unrelated contacts/volumes/sweep code.
- [ ] Review every mutation and query entry point, view disposal, stale handle/rebase and exception
  paths against the preserved tests. Preserve source behavior unless a failing regression requires a fix.
- [ ] Reconcile fresh main. Run the original lease filter and named affected regressions once on the
  extracted tree, then the required full Release build/tests/format/guards on its exact final tree.
- [ ] Preserve separate source/proof commits, integrate normally and coordinate post-push pack and CI.
  Merge the verified prerequisite into this task branch before live feature-query implementation.

### Task 2: Pin the optional contract and lifetime tests

**Files:** Create `KhaozEngine.Physics/CapsuleFeatureQuery.cs`,
`KhaozEngine.Game.Tests/Physics/CapsuleFeatureContractTests.cs` and
`KhaozEngine.Game.Tests/Physics/CapsuleFeatureLifetimeTests.cs`.

**Interfaces:** Produce the two `IPhysicsCapsuleFeatures` methods exactly as specified.
`CapsuleFeatureResult` exposes `Status`, `Written`, `RequiredCapacity`, `QueryWorld`, `SourceWorld`,
`Lease`, `Origin`, `GeometryGeneration`, `Target`, `LeafId`, `FeatureId`, `Kind`, `AxisPoint`,
`GeometryPoint`, `SeparationLower`, `SeparationUpper`, `PositionErrorMetres` and `NormalError`.
`CapsuleIncidentFace` exposes `FaceId`, geometric `Normal`, `NormalError` and incidence kind.
Constructors/factories enforce structural invariants only, never claim numerical proof.

- [x] Write contract RED covering default Unresolved, all refusal statuses, malformed bounds/counts,
  finite normal/point invariants and the exact optional method signatures. Use reflection only to
  bootstrap absent types, then direct calls after implementation.
- [x] Run `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release -m:1
  --filter FullyQualifiedName~CapsuleFeatureContractTests`. Require intended missing-contract failures.
- [x] Implement the immutable contract and direct tests. Run the same finite filter once GREEN.
- [x] Write scripted-provider lifetime tests with distinct owners/views, wrong-thread entry,
  disposed receiver, expired lease, a new same-generation lease and untouched sentinel face spans.
  No backend geometry success is faked or inferred from these protocol tests.
- [x] Prove RED/GREEN for the lifetime validation path, preserving actual owner lease authentication.
  Wrong-thread tests use bounded synchronization and `finally` cleanup, not sleeps or load loops.
- [x] Commit the contract and focused proof. Document that structural validity is not completeness.

Recorded proof: `docs/verification/2026-10-07-feature-completed-values.json` and
`docs/verification/2026-10-07-feature-lifetime-protocol.json`. The backend exposes only the
validated lifetime/refusal path until the geometric proof is implemented.

### Task 3: Prove bounded geometric arithmetic

**Files:** Create `KhaozEngine.Physics.Bepu/BoundedGeometryArithmetic.cs`,
`KhaozEngine.Physics.Bepu/CapsuleFeaturePredicates.cs`,
`KhaozEngine.Game.Tests/Physics/BoundedGeometryArithmeticTests.cs`,
`KhaozEngine.Game.Tests/Physics/CapsuleFeaturePredicateTests.cs`, and
`docs/design/CAPSULE-FEATURE-NUMERICAL-PROOF-2026-10-07.md`.

**Interfaces:** Internal `GeometryInterval` in the shared arithmetic file provides outward `Lower`/`Upper`, arithmetic and verified
square-root enclosures. `GeometrySign` distinguishes Negative, Zero, Positive and Unresolved.
`BoundedGeometryArithmetic` owns transform enclosures and the bounded exact arithmetic.
`CapsuleFeaturePredicates` provides feature-specific finite membership, orientation and closest-feature ordering with
the spec's 4,096-bit exact-predicate refusal cap. No caller interprets Unresolved as zero or absence.

- [x] Write finite independent dyadic/rational tests for cancellation, representable boundaries,
  positive/negative zero, degenerate triangles, skinny triangles, interval ordering, transform
  rounding and overflow. Expected values come from an independent test oracle or closed form.
- [x] Run `FullyQualifiedName~CapsuleFeaturePredicateTests` in Game.Tests Release and inspect RED.
- [x] Implement bounded predicates and intervals. Verify square-root endpoints by squared bounds.
  Return Unresolved if enclosure cannot be established within the work cap.
- [x] Write the operation-level enclosure argument for transforms, dot/cross/division/root,
  normalization, witness reconstruction and float output. Cover the exact spec domain and ceilings.
- [x] Run GREEN once and have the proof checked independently before treating it as a backend
  certificate. A finite passing sample alone does not complete this task.
- [x] Commit the kernel/proof. Do not change the spec's bounds to make failing cases pass.

### Task 4: Prove selected-view box and convex features

**Files:** Create `BepuPhysicsWorld.CapsuleFeatures.cs`, `CapsuleFeatureGeometry.cs`, and
`KhaozEngine.Game.Tests/Physics/CapsuleFeaturePolyhedronTests.cs`. Modify the private Bepu
`QueryView` forwarding and existing shape-cache lifecycle points only with swimming coordination.

**Interfaces:** Implement `QueryCapsuleFeature` and `AssertFeatureCurrent` over the live owner-issued
lease. Internal geometry consumes actual installed `TypedIndex` shapes/poses, not source descriptors.

- [x] Write RED for box face, edge and vertex incidence, convex hull recentering, flattened child
  transforms, ambiguous disconnected minima and unsupported curved shapes. Include selected-floor
  exclusion, same-static unrelated faces and exact sentinel-span preservation on every refusal.
- [x] Run `FullyQualifiedName~CapsuleFeaturePolyhedronTests` once and inspect intended failures.
- [x] Implement private bounded scratch, complete candidate enumeration and atomic output commit.
  Authenticate thread/lease before the monitor. Bind successful results to the original lease and
  receiver. Lazy immutable geometry caches do not mutate physical generation.
- [x] Run GREEN plus only the affected lease/lifetime filter. Prove cache disposal and rebase behavior.
- [x] Commit the backend slice. No LowPropSupport call site changes yet.

### Task 5: Prove finite mesh incidence and the actual corner rule

**Files:** Extend `CapsuleFeatureGeometry.cs` through a cohesive mesh-incidence helper if needed.
Create `KhaozEngine.Game.Tests/Physics/CapsuleFeatureMeshTests.cs` and
`KhaozEngine.Movement.Tests/LowLipTraversalTests.FeatureContract.cs`.

**Interfaces:** Mesh output uses the same complete feature/incident-face contract. The legacy
eligibility helper consumes only that output, never backend shape types or contact-manifold IDs.

- [x] Write RED for all six origin mesh corners and their translations, interior seam aliases,
  exact open boundaries, non-manifold/reversed faces, concave edges, cracks, disconnected coplanar
  patches, local/source/caller capacity refusal and one unsupported leaf in a compound.
- [x] Run the two named classes, keeping the filter below 25 terms.
- [x] Implement exact finite incidence and conservative local enumeration. Enclose every exclusion.
  Refuse uncertain minima/topology and preserve front-side membership.
- [x] Prove the spec's sole eligible top at a convex top/wall edge and front-side open top boundary.
  Prove competing tops, underside, wall, dome, concave and unresolved neighborhoods refuse.
- [x] Run GREEN and independently review geometry plus numerical proof. The existing 24 diagnostic
  observations stay intact and are not relabeled as the acceptance oracle.
- [x] Commit. Only a successful Tasks 3 to 5 proof opens the runtime gate.

Recorded geometry gates: `docs/verification/2026-10-08-installed-pose-green.json`,
`2026-10-08-feature-mesh-green.json`, `2026-10-08-low-lip-feature-correspondence.json` and
`2026-10-08-low-lip-eligibility-green.json`. Additional corner/capacity integration facts passed
first validation after their backend RED/GREEN. No artificial RED was introduced for working code.

### Task 6: Repair legacy low-prop support behind the proof gate

**Files:** Modify `KhaozEngine.Locomotion/CharacterMovement.LowProp.cs` and add a cohesive
`CharacterMovement.LowPropFeatures.cs` only if the classification responsibility needs its own file.
Tests stay in `KhaozEngine.Movement.Tests/LowLipTraversalTests.*.cs` and existing stair/roof fixtures.

**Interfaces:** Preserve `LowPropSupport`'s existing near-flat sweep path. The optional path uses the
same selected view and current read interval, its actual proposed resting capsule pose, and the
feature contract. Missing capability/refusal retains current behavior.

- [ ] Retain the observed 4.25 cm positive failures. Add a runtime RED for the x -0.375 to -0.125
  directed approach and a bounded holding/approach proof over the threshold gap.
- [ ] Implement only the approved walkable/non-flat fallback. Require the whole near-contact
  interval within [-0.1 mm,+0.1 mm], robust threshold comparisons and every existing rise/clearance
  protection. Do not snap to a geometric plane or accept an unrelated static.
- [ ] Run LowLip runtime GREEN, the original 2.5 cm control, roof/dome/wall/slope/wrong-layer controls,
  and the seven-stair prior-art regression slice serially. Stop any control failure.
- [ ] Commit and document unchanged authority and tolerance boundaries.

Task 6 status on 2026-10-08: the support fallback passes the non-flat Hold rows and all 39 Game
low-prop, wall, dome, slope and stair controls. The x -0.375 to -0.125 approach still stops 6.75 mm
short. [The measured mechanism](../../design/LOW-LIP-APPROACH-MECHANISM-2026-10-08.md) is moving
collision response, outside this plan's scope, so Task 6 GREEN waits for an owner scope decision.

### Task 7: Prove private resting anchors and finish

**Files:** Modify `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs` through a cohesive
`LegacyRestingAnchors.cs` helper. Extend `LowLipTraversalTests.Resting.cs` and profile tests.
Update package README/consumer docs, changelog and deterministic profile identity where required.

**Interfaces:** Anchors remain private to legacy profile proof. Public graph Feet, raw heights,
layer identities and bake layout stay unchanged. Explicit profiles use their existing dispatch.

- [ ] Preserve the four raw-point acceptance hypotheses in history and convert them to the
  owner-approved strict-probe refusals. Add separate RED for candidate/self-edge and bidirectional
  anchor traversal, same raw layer and raised-capsule headroom/footprint refusal.
- [ ] Run RED. Implement bounded Hold-derived legacy anchors only after real footprint/feature and
  layer checks. Invoke unchanged `TryEdge` between anchors with the existing 1 mm arrival rule.
- [ ] Run GREEN, profile/bake-reader compatibility and explicit-profile isolation controls. If raw
  graph consumers cannot use the proved anchors safely without a format/API change, stop at that
  concrete incompatibility instead of declaring route success.
- [ ] Reconcile fresh main and shared profile identity changes. Obtain the whole-branch review,
  then run one full Release solution build, solution tests excluding LiveSocket, solution format
  and required repository guards on the exact final candidate. Resolve only evidence-backed failures.
- [ ] Integrate and push main under exclusive ownership, run the required coordinated main pack,
  preserve evidence and update #1270. A released game adoption and unchanged real bridge Complete/
  600-step proof remain explicitly gated, so do not close consumer #416 prematurely.

## Outcome and rulings

- Owner approved the feature spec on 2026-10-07 and asked to finish without more routine plan approvals.
  This plan executes that approved scope and does not add release/private-data authority.
- The read-lease prerequisite is existing swimming work. Integrate it once with that owner's handoff.
- Inline execution is used while routed capacity is exhausted. Fresh review remains required at the
  geometric/numerical gate and at final integration. No unassigned worker is silently dispatched.
