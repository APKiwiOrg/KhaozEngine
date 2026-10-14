# Contact controller support gates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Find every eligible support surface inside the foot column, preserve physical refusals and prove the support path's geometry, lifetime and cost before adoption.

**Architecture:** Two retained downward sweeps propose contacts to one optional complete footprint query. Physics certifies installed geometry, admission, disc extrema and whole-polygon joins. Locomotion evaluates planes, selects support and binds its original raw owner under the caller's existing interval. Bounded workspace removes variable hot-path allocation without recycling public lifetime tokens.

**Tech Stack:** C# (.NET 10), System.Numerics, BepuPhysics, xUnit, KhaozEngine.Physics, KhaozEngine.Physics.Bepu, KhaozEngine.Locomotion, KhaozEngine.Game.Tests.

**Spec:** `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md`, approved rulings SG-1 to SG-24. Density reference `docs/superpowers/plans/2026-10-11-contact-controller-phase4b.md`.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/contact-controller-support-gates`, branch `feature/contact-controller-support-gates`. Every future writer first runs `git -C /Users/antonio/KhaozEngine/.worktrees/contact-controller-support-gates rev-parse --show-toplevel`, `git -C /Users/antonio/KhaozEngine/.worktrees/contact-controller-support-gates branch --show-current` and `git -C /Users/antonio/KhaozEngine/.worktrees/contact-controller-support-gates status --short`. Preserve unrelated changes. Integrate serially, one owner per file.
- Exactly SG-17's eleven tasks, in order, one implementer per task. Implementers never dispatch subagents, push, merge, tag or close issues. The orchestrator integrates.
- Keep SG-1's nine invariants, released tie order, inclusive reach, slope hysteresis, planar `h = min(p, m, j...)`, joined-steep suppression and exact analytic axis sampling. Arithmetic errors enclose answers and never extend reach.
- Closed disc `D` has axis `a` and radius `r >= 0.01 m`. The represented contact band stays `e = 0.0001f`, probe segment `0.01f`, proposal margin `0.001f`, shell contact skin `0.001f`, arrival `0.001 m` and explicit placement skin `0.001f`. Dry placement skin stays zero.
- With `B = [L, U]`, `T = sqrt(1 - c*c) / c`, `S = U + 0.001 + 2*r + 0.01`, admit the column over the disc expanded by `e`, from `L - max(r*T, 0.001) - e` to `S + e`, evaluated outward and enclosing actual float proposal paths. `c = 0` returns Unsupported. No chosen maximum slope or new error threshold.
- Admission retains the whole polygon. Compute `m` on its full intersection with closed `D`. Ring-only members join, cap and feed guards but publish no support. Joins use the released whole-polygon predicate over admitted members, including remote meetings. No closure outside the volume.
- Preserve the two sweeps, back-face policy, invalid-start and above-band guards. No extra probe, pagination, lower plausible answer or fallback to incomplete support. Missing footprint capability refuses contact-controller use.
- Cap raw static/element pairs at 256. At 257 return CapacityExceeded with diagnostic RequiredElements and untouched output spans. Curved parts have axis and footprint roles in two fixed slots, at most 512 witness records. Role tags never change raw feature IDs.
- SG-19's geometric witnesses are primary. Axis and footprint witnesses are attained, equality is walkable, curved parts never join, and every pose admitted by phase 2b remains covered. The virtual-probe fallback needs a new owner ruling.
- Borrow one authenticated physics lease and selected view for every support, analytic, producer and signal call. Scope guards run before backend or producer work. Preserve MaxRise, MaxDrop, transition origin, exclusion and whole-tick hold mapping. A required witness or cap outside the certified scope refuses.
- All engine tests below live in `KhaozEngine.Game.Tests`, namespaces `KhaozEngine.Tests.Physics` or `KhaozEngine.Tests.Locomotion.Contacts`. Existing owners confirm this layout. Allocation/global-state tests use `AllocSensitiveCollection` at `KhaozEngine.Game.Tests/AllocSensitiveCollection.cs:13`.
- Every build/test command below runs through build-slot. Exit 75 means nothing ran. Retry later without changing the test. Red and green invocations bracket real changes, never an unchanged repetition loop. Full suite only in Task 11, once, heavy lane. No local stress or timed warm-up loop.
- Timing is recorded only with `DOTNET_TieredCompilation=0` and `KHAOZ_SUPPORT_COST_ARTIFACT`, one first-use and one warm operation per row. Task 2 records the baseline, Task 9 records the changed implementation. Ordinary tests assert geometry, counters and allocation, never wall time.
- SG-12 warm limits in M2 Max units are Find 50 us ordinary / 500 us dense, actual ground substep 200 us / 1,000 us, whole moving tick with at most three ordinary timed substeps 1,000 us / 4,000 us. Support-path allocation is zero. Dry acquisition-inclusive tick permits only the observed 208-byte fixed lease overhead, with zero growth in Find count. Explicit ticks permit only measured, named one-use lifetime tokens (SG-22).
- Comparable ordinary whole ticks must also satisfy `candidate <= legacy + max(100 us, 0.25 * legacy)`. Report larger-substep and mixed-mode envelopes separately. Cold time, cold bytes, scratch high-water and stack are separate. Existing certification stack bound is 4,352 bytes, not a new-kernel allowance.
- No release, tag, engine pin, game change, navigation capture or adoption. #1347 needs every shape completeness row. #1334 needs SG-24's approved hosted proof. Aggregate actor CPU belongs to phase 6 under SG-21, not this gate.
- Ride staged `20.33.0` (`Directory.Build.props:25`, `CHANGELOG.md:8`). Draft-time newest tags are `v20.32.0`, `v20.31.0`, `v20.30.0`. Re-read at Task 11. No version/changelog changes before Task 11.
- Zero warnings. No em dash, en dash or prose semicolon. Commit subjects use `area(scope): summary`. Future delegated writers stop at their verified commit, with integration and issue closure owned by the orchestrator.

## Review Focus

- A finite input whose outward volume or float proposal conversion overflows must refuse before a producer or backend call, not sample a smaller column (Task 7).
- A custom small footprint must be rejected even when analytic terrain is its only possible support and no sweep would hit (Task 7).
- A relevant unsupported leaf among otherwise valid compound leaves must refuse atomically, not expose the valid lower floor (Task 4).
- A disposed composed lease or producer pin must stay expired after same-generation reacquisition, including exceptional workspace release (Task 8).
- A zero-time or failed legacy baseline and a missing hosted artifact must not produce a passing normalized verdict (Task 10).

---

### Task 1: Contract and oracle

**Files:**
- Create: `KhaozEngine.Physics/SupportFootprintQuery.cs` (optional contract and fixed witness semantics).
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintOracle.cs`, `SupportFootprintScenes.cs`, `SupportFootprintTests.cs` in the same directory (independent oracle, installed scenes, contract rows).
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportCompletenessTests.cs` (first failing #1347 row).
- Read: existing contract `KhaozEngine.Physics/SupportNeighborhoodQuery.cs:7`, `:24`, `:97`, oracle `KhaozEngine.Game.Tests/Physics/SupportNeighborhoodOracle.cs:22`, `:44`, `:62`, `:113`, `:130`, `:162`, `:183`, scene `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportScenes.cs:15`, `:41`, `:47`, `:72` and selection `KhaozEngine.Locomotion/Contacts/FootSupport.cs:209`.

**Interfaces:**
- Existing primitives are `IPhysicsWorld` (`KhaozEngine.Physics/IPhysicsWorld.cs:11`), `IPhysicsQueryLease` (`IPhysicsQueryLeaseSource.cs:16` in that directory), `Pose` (`Pose.cs:6`), `StaticHandle` (`Handles.cs:4`), `SweepHit` (`Queries.cs:13`), shape classes (`PhysicsShape.cs:7`) and `CapsuleFeatureStatus` (`CapsuleFeatureQuery.cs:7`). Standard .NET/xUnit types use their installed signatures.
- Produces readonly records `SupportFootprintProposal(CapsuleShape Probe, Pose Start, float Distance)` and `SupportFootprintRequest(Vector2 Axis, float Radius, double BandLower, double BandUpper, float CosMaxSlope, float ContactBand, SupportFootprintProposal AxisProposal, SupportFootprintProposal FootProposal)`. Proposals describe the original paths, not untrusted prior hits. The backend authenticates the lease before executing either retained sweep in the same complete operation.
- Produces `SupportWitnessRole { Axis, Footprint }`, `SupportProposalStatus { Miss, Contact, InvalidStart }`, `SupportProposalVerdict(SupportProposalStatus Status, double LowestLower, double LowestUpper)` and `SupportFootprintWitness(SupportWitnessRole Role, Vector3 Position, float PositionErrorMetres, Vector3 Normal, float NormalError)`.
- Produces `SupportFootprintMember(StaticHandle Static, SupportElementKind Kind, int ElementId, Vector3 PlanePoint, float PlanePointErrorMetres, Vector3 Normal, float NormalError, bool HasDiscMaximum, double DiscHeightLower, double DiscHeightUpper, byte ProposalMask, byte WitnessCount, SupportFootprintWitness First, SupportFootprintWitness Second)`. Polygon maxima occupy First with Footprint role, context-only polygons have zero witnesses. Curved roles order Axis then Footprint. ProposalMask bits 0/1 name the two certified contact neighborhoods, independently of disc eligibility.
- Produces `SupportFootprintResult` with Status, Members, Witnesses, RequiredElements, JoinWordsPerRow, QueryWorld, SourceWorld, Lease, Origin, GeometryGeneration, AxisVerdict and FootVerdict. Constants MaximumElements = 256, MaximumWitnesses = 512. Methods `static int JoinWordsFor(int members)`, `static SupportFootprintResult Refused(CapsuleFeatureStatus status, int requiredElements = 0)` and `static SupportFootprintResult Completed(IPhysicsWorld receiver, IPhysicsQueryLease lease, int members, int witnesses, SupportProposalVerdict axis, SupportProposalVerdict foot)`.
- Produces `IPhysicsSupportFootprint.QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request, Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default) -> SupportFootprintResult` and `AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) -> void`. No inheritance from, or changes to, the released neighborhood interface.
- Produces test-only `FootprintPartition { Boxes, Hulls, Compound, Mesh }`, `SupportFootprintScene : IDisposable` exposing World, Crate, Faces and Curves, and `SupportFootprintScenes.HiddenCrate(FootprintPartition partition, bool reverse = false) -> SupportFootprintScene`, `Fan(int count)`, `Grid(int cells)` and `Cluster(int rawMembers, bool curved, FootprintPartition partition)` returning the same scene type. Other cases use its World to install geometry before acquiring a lease.
- Produces test-only records `FootprintOracleWitness(SupportWitnessRole Role, OracleVector Position, OracleVector Normal)`, `FootprintOracleMember(StaticHandle Static, int ElementId, bool Admitted, bool HasDiscMaximum, double Plane, double Maximum, OracleVector Normal, IReadOnlyList<FootprintOracleWitness> Witnesses, bool WitnessInsideClosedDisc)` and `FootprintOracleSupport(SupportStatus Status, double Height, double HeightLower, double HeightUpper, OracleVector Normal, OracleVector Witness, StaticHandle? Static, int ElementId)`. Existing SupportStatus/SupportSample are at `KhaozEngine.Locomotion/Contacts/SupportSample.cs:8`, `:14`.
- Produces `SupportFootprintOracle.Volume(in SupportFootprintRequest request) -> (double Bottom, double Top)`, `Polygon(OracleFace face, in SupportFootprintRequest request) -> FootprintOracleMember?`, `Curved(OracleTangent part, in SupportFootprintRequest request) -> FootprintOracleMember?`, `Select(IReadOnlyList<FootprintOracleMember> members, in SupportFootprintRequest request) -> FootprintOracleSupport`, `PerPointSupremum(OracleTangent part, in SupportFootprintRequest request) -> (double Lower, double Upper)`.

- [ ] **Step 1:** Write `FootprintCapabilityIsSeparateAndSlotsKeepRawIds` using reflection as in `KhaozEngine.Game.Tests/Physics/CapsuleFeatureContractTests.cs:85`. Assert only the two new capability methods, unchanged released interface methods, 256 raw members, 512 slots and join stride 4 at 256. Write `HiddenCrateUnderSixtyDegreeWedge` first in the completeness fixture. Query axis `(0,0)`, feet 0, r 0.2, reach up/down 0.4, c = cos(45 degrees). Floor 0, crate top 0.1 over x `[0.05,0.12]`, z `[-0.1,0.1]`, wedge top `y = 0.25 + tan(60 degrees) * (x - 0.2)` over x `[0.15,0.25]`, z `[-0.2,0.2]`. Derive through installed floats, not nominal sweep distance.

```csharp
// HiddenCrateUnderSixtyDegreeWedge
Assert.Equal(SupportStatus.Walkable, sample.Status);
Assert.Equal(scene.Crate, sample.Static);
Assert.True(Math.Abs(sample.Height - installedCrateTop) <= sample.HeightError);
Assert.True(sample.HeightError <= 0.0005f);
// Oracle anchors: crate 0.1, steep wedge plane about -0.09641016.
// The roughly 0.303 probe centre is diagnostic, never an expected support height.
```

- [ ] **Step 2:** Implement the contract and independent oracle. Use installed float geometry with independent binary64/exact predicates, whole-volume admission, full-disc extrema, full-polygon mutual-below/meeting and SG-19 witnesses. Never call production clipping, joins, tangent extrema or selection to derive an expectation. Implement the scene builders, with raw-member counts proved rather than inferred from static count.
- [ ] **Step 3:** Run `build-slot --label sg1-contract -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~FootprintCapabilityIsSeparateAndSlotsKeepRawIds|FullyQualifiedName~HiddenCrateUnderSixtyDegreeWedge"`. Contract row passes, crate row must expose the baseline failure. Retain exact command, actual result and exit status with the task report. Do not suppress the row or change its expectation. It stays red until Task 7.
- [ ] **Step 4:** Commit only these explicit paths with `test(physics): freeze complete footprint support and hidden crate oracle`.

### Task 2: Cost harness and baseline

**Files:**
- Create: `KhaozEngine.Physics/SupportQueryDiagnostics.cs` (internal opt-in integer observer), `KhaozEngine.Locomotion/CharacterMovement.SupportCost.cs` (entry into the actual private down pass).
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportGateCostTests.cs`, `SupportGateCostScenes.cs`, `SupportCostArtifact.cs` (fixture, rows, serialization/verdict), `scripts/record-support-cost-exit.py` (finalize actual process exit metadata).
- Modify: `KhaozEngine.Physics/KhaozEngine.Physics.csproj:9` (friend access for Bepu, Locomotion and Game.Tests), `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundScenarioTests.cs:453`, `:485`, `CountingQueryView.cs:19` in that directory.
- Modify observer sites: `KhaozEngine.Locomotion/Contacts/FootSupport.cs:52`, `GroundCore.cs:290`, `GroundCore.Medium.cs:23`, `ContactController.Continuation.cs:45` in that directory, `KhaozEngine.Locomotion/MovementQueryLease.ContactSupport.cs:23`, `:60`, `MovementEnvironmentContext.cs:53`, `:60`, `:81` in that directory, `KhaozEngine.Physics.Bepu/SupportNeighborhoodCollector.cs:74`, `:110`, `:148`, `:184`, `SupportNeighborhoodMesh.cs:45`, `SupportNeighborhoodJoins.cs:10`, `BepuPhysicsWorld.QueryLease.cs:18`, `BoundedGeometryArithmetic.cs:100`, `CapsuleFeatureGeometryNumbers.cs:85`, `SupportNeighborhoodCurved.cs:156` in that directory.
- Create: `docs/verification/2026-10-11-support-gates-cost.json`. Preserve `docs/verification/2026-10-10-p2b-support-cost.json`.

**Interfaces:**
- Consumes Task 1's scenes and oracle. Existing `CharacterMovement.PropSupportFloor` is at `KhaozEngine.Locomotion/CharacterMovement.Collision.cs:700`, LowPropSupport at `CharacterMovement.LowProp.cs:26`, and the released stack/old loop at `GroundScenarioTests.cs:453`, `:485` above.
- Produces internal `SupportQueryCounter` enum and `SupportQueryCounts` value snapshot with matching integer fields Find, Proposals, Volumes, SelectedStatics, MeshLeaves, Polygons, Members, Extrema, IntervalFallbacks, ExactFallbacks, JoinPairs, JoinsProved, JoinsPruned, Unsupported, Ambiguous, CapacityExceeded, AnalyticSamples, Bindings, PhysicsLeases, MovementLeases, ProducerPins, GroundSubsteps, MediumRetries and ModeSegments. `SupportQueryDiagnostics.Enable(bool enabled)`, `Reset()`, `Record(SupportQueryCounter counter, int count = 1)` and `Snapshot() -> SupportQueryCounts` are thread-local and allocate nothing after installation. `SetGroundSubstepObserver(Action? begin, Action? end)`, `BeginGroundSubstep()` and `EndGroundSubstep()` install/invoke preallocated callbacks around each actual complete substep, including shell/seat/placement and all exits. Only the env-enabled timing fixture timestamps those callbacks. Test cleanup restores observer state.
- Produces `CharacterMovement.MeasureSupportDownPass(IPhysicsWorld world, in CapsuleShape capsule, in Vector3 pos, in Vector3 startPos, bool wasGrounded, in MoveTuning tuning, float halfHeight, float terrainGroundY, float groundY, bool steppedUp) -> float`, forwarding the existing method with no copied solver body.
- Produces test-only `SupportGateCostScenes.Rows() -> IEnumerable<object[]>`, record `SupportCostSample(string Operation, string Scene, bool GeometryPassed, string Outcome, double Height, double HeightError, Vector3 AchievedMotion, int Substeps, SupportQueryCounts Counts, long ColdBytes, long WarmBytes, double ColdMicroseconds, double WarmMicroseconds, long ScratchHighWaterBytes, int StackBytes)`, `SupportCostArtifact.Write(string path, IReadOnlyList<SupportCostSample> samples, string baseSha, string candidateSha, string command) -> void`, and `SupportCostVerdict.Evaluate(in SupportCostSample candidate, in SupportCostSample legacy, bool dense, double hostScale = 1) -> bool`. Artifact writing adds hardware/runtime/compilation metadata and preserves the baseline section. CLI `python3 scripts/record-support-cost-exit.py <artifact> <exit_status>` atomically finalizes the actual process exit after dotnet returns, refusing a missing/stale sample section. Exit 75 leaves the artifact untouched. Synthetic verdict tests may assert time arithmetic, never measured wall time.

- [ ] **Step 1:** Retire the automatic `SupportFindTiming` warm-up/repetition before any test run. Keep its geometry and deterministic allocation coverage. Add `CostRowsHaveSuccessfulGeometryAndRouteBounds` and `OneShotCostArtifact`. Include flat box/hull/mesh, analytic-only and analytic-plus-props, box/hull/mesh stairs, hidden crate, sphere/capsule/cylinder, 20,000-triangle grid (`Grid(100)`), fans 96/256 and over-capacity 257/300, clusters 8/32/128/256 as one mesh, compounds and split statics, and stacked irrelevant layers. Use body r 0.4, half-height 0.9, footprint 0.2, step 0.4 and `dt = 1f/30f` (`KhaozEngine.Locomotion/MoveTuning.cs:19`, `:27`, `:73`). Stair treads 0.35, risers 0.25, matching the existing cost rows.
- [ ] **Step 2:** Implement observer and fixture. Capture Find, each actual shell/seat/placement substep and acquisition-inclusive whole tick separately. Cover initial, stationary, moving, stair signals, wall retry, pushed seat, descending landing, slide/opposing face, explicit entry, rounding/final clamp, medium retry and dry/wade/swim handoff. Trace and assert route-derived bounds, including dry `Find <= 1 + 8*n`, old sweeps `3 + 36*n`, penetrations `5 + 28*n`, neighborhoods `2 + 16*n`, zero rays, eight medium retries per Resolve and eight mode segments. Count additional signal Finds (up to three with sample zero reused), explicit initial/rounded/clamp Finds and analytic/binding work explicitly. Never label a tick average as a substep.
- [ ] **Step 3:** In each row use separate candidate/legacy scenes and identical input states. Measure the real legacy down pass, its conditional tread work and whole legacy tick under the same selection/lease configuration. Setup, delegates, observer installation, oracle evaluation, output and JSON serialization occur outside sample windows. Use the executing thread's timestamps and allocated-byte counter. First use excludes scene construction and records cold JIT separately from lazy scratch. Baseline failures, including the hidden crate, are recorded as failed geometry and cannot receive a passing gate verdict.

```csharp
// CostRowsHaveSuccessfulGeometryAndRouteBounds, ordinary comparable rows
Assert.True(counts.Find <= derivedRouteFindBound);
Assert.Equal(0, warmFindBytes);
Assert.Equal(4352, baselineCertificationStackBytes);
// Refusal rows assert the intended refusal, never count it as successful support.
```

- [ ] **Step 4:** Run the deterministic baseline selection once with `build-slot --label sg2-counts -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CostRowsHaveSuccessfulGeometryAndRouteBounds"`. Successful baseline cases assert their geometry/counters/allocation. Known incorrect cases assert their failed-geometry flag and false gate verdict, retaining the separate Task 1 failure. Capture current variable explicit allocation without claiming SG-22 passes. Then run exactly one baseline timing invocation:

```bash
DOTNET_TieredCompilation=0 KHAOZ_SUPPORT_COST_ARTIFACT=/Users/antonio/KhaozEngine/.worktrees/contact-controller-support-gates/docs/verification/2026-10-11-support-gates-cost.json build-slot --label sg2-baseline -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact"
support_cost_exit=$?
python3 scripts/record-support-cost-exit.py docs/verification/2026-10-11-support-gates-cost.json "$support_cost_exit"
```

Record base `06b734c5722bc2cc95cea459255d6ec93e9236c7`, measured candidate SHA, hardware/architecture/runtime, compilation setting, exact command, actual exit status, every scene/input, status/height/error/motion, counters, absolute bytes, cold/warm times, candidate/legacy ratio and whole-tick delta. Save failed rows before reporting failure. Preserve this baseline section when Task 9 appends the changed section.
- [ ] **Step 5:** Commit `test(locomotion): record one-shot support cost and legacy baseline`.

### Task 3: Polygon footprint kernel

**Files:**
- Create: `KhaozEngine.Physics/SupportFootprintBounds.cs` (dependency-free outward bounds shared with scope guards), `KhaozEngine.Physics.Bepu/SupportFootprintPolygon.cs`, `SupportFootprintVolume.cs` in that directory (disc extrema and interval-volume adapter).
- Test: Task 1's `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.cs` and `SupportFootprintOracle.cs`.
- Read/reuse: `KhaozEngine.Physics.Bepu/SupportNeighborhoodPolygon.cs:12`, `:77`, `CapsuleFeatureGeometryNumbers.cs:7`, `:95`, `BoundedGeometryArithmetic.cs:8`, `:100`, `:240`, `GeometryVectorOperations.cs:26` in that directory.

**Interfaces:**
- Consumes Task 1's request/member and Task 2's counters. Existing `SupportPolygon`, `FeaturePoint`, `FeatureNumber`, `GeometryInterval`, `GeometrySign` and publish operations are at the cited owners.
- Produces readonly `SupportFootprintBounds(Vector3 Minimum, Vector3 Maximum)` with `static bool TryCreate(in SupportFootprintRequest request, out SupportFootprintBounds bounds)`, enclosing SG-3's outward V and both actual represented proposal paths. It returns false for c=0, overflow or unbounded conversion. Physics and locomotion share this dependency-free owner, so their envelopes cannot drift.
- Produces `SupportFootprintVolume.TryBounds(in SupportFootprintRequest request, out GeometryVector minimum, out GeometryVector maximum) -> bool` using those bounds, and `SupportFootprintPolygon.Measure(SupportPolygon polygon, in SupportFootprintRequest request, out SupportFootprintMember member) -> GeometrySign`. Positive means admitted, Negative means proven irrelevant, Unresolved means atomic query refusal. An admitted context-only polygon has HasDiscMaximum false and WitnessCount zero.

- [ ] **Step 1:** Write `PolygonMaximumUsesVertexEdgeAndDiscExtrema`. Axis `(0,0)`, r 0.2, plane `y=x`: an entirely inside triangle with X values 0.05/0.1/0.05 gives vertex `m=0.1`, a triangle over XZ `(-0.3,0.1),(0.3,0.1),(-0.3,0.3)` gives edge-circle `m=sqrt(r*r - installedZ*installedZ)`, and a square over `[-0.3,0.3]^2` gives uphill-disc `m=r`. A flat face at 0.1 gives m 0.1 with a valid deterministic witness. Assert containment against installed-float oracle, not decimal epsilon.

```csharp
// PolygonMaximumUsesVertexEdgeAndDiscExtrema
Assert.True(member.DiscHeightLower <= oracle.Maximum);
Assert.True(member.DiscHeightUpper >= oracle.Maximum);
Assert.True(oracle.WitnessInsideClosedDisc);
Assert.Equal(1, member.WitnessCount);
```

- [ ] **Step 2:** Add `AdmissionKeepsFullPolygonAndContextRing`. With plane `y=0.1+x`, footprint 0.2, B `[-0.4,0.125]`, a fragment x `[0.199,0.201]` retains m at the disc edge, about 0.3, and p 0.1, even though all its points in D are above U. Pin closed radial/reach ties and one represented float on either side. Ring-only face x `[0.20005,0.20009]` is admitted for e 0.0001, has no disc maximum and no contribution. Compare both volume endpoints outward. Include rotated ordinary slivers, exact zero-area and back/vertical orientation distinctions, keeping the existing documented exclusion domain.
- [ ] **Step 3:** Implement complete vertex, edge-circle and uphill-disc candidates with outward enclosures and exact sign fallback. Membership is intersection with V, independent of sweep witnesses or vertically clipped m. Retain uncertain possible candidates or return Unresolved, never drop them by epsilon. Record kernel scratch/stack and predicate counters.
- [ ] **Step 4:** Run `build-slot --label sg3-polygon -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~PolygonMaximumUsesVertexEdgeAndDiscExtrema|FullyQualifiedName~AdmissionKeepsFullPolygonAndContextRing"`. Expect PASS for every extremum stratum and boundary row.
- [ ] **Step 5:** Commit `feat(physics): certify polygon footprint admission and disc maxima`.

### Task 4: Footprint query

**Files:**
- Create: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.SupportFootprint.cs`, `SupportFootprintCollector.cs` in that directory (authenticated query and bounded collection/publication).
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.QueryView.cs:25`, `:76`, `SupportNeighborhoodMesh.cs:32`, `SupportNeighborhoodPolyhedra.cs:19`, `:59` in that directory (shared capture and volume traversal, no duplicate installation).
- Test: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.cs`, existing `SupportNeighborhoodTests.cs:236`, `:266`, `:290` in that directory.
- Read/reuse: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.CapsuleFeatures.cs:20`, `SupportNeighborhoodCollector.cs:169`, `SupportNeighborhoodJoins.cs:10` in that directory.

**Interfaces:**
- Consumes Tasks 1 to 3. Implements Task 1's capability on Bepu and its selected QueryView. Adds owner-private `QuerySupportFootprintCore(IPhysicsWorld receiver, IPhysicsQueryLease lease, in SupportFootprintRequest request, Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter, StaticQueryExclusions? exclusions) -> SupportFootprintResult` and `AssertFootprintCurrentCore(IPhysicsWorld receiver, in SupportFootprintResult result, IPhysicsQueryLease lease) -> void`. Existing exclusion type is at `KhaozEngine.Physics.Bepu/StaticQueryExclusions.cs:9`.
- Produces `SupportFootprintCollector.Begin(in SupportFootprintRequest request) -> bool`, `Collect(Simulation simulation, StaticHandle owner, in StaticDescription description) -> CapsuleFeatureStatus`, `Publish(IPhysicsWorld receiver, IPhysicsQueryLease lease, Span<SupportFootprintMember> members, Span<ulong> joins) -> SupportFootprintResult`. Bepu Simulation/StaticDescription follow existing signatures at `SupportNeighborhoodCollector.cs:110`. Existing exclusions are owned by `BepuPhysicsWorld.QueryView.cs:29`.
- Produces shared overload `SupportNeighborhoodMesh.Polygons(Simulation simulation, TypedIndex shape, in RigidPose pose, StaticHandle owner, in SupportFootprintBounds bounds, List<int> candidates, SupportPolygonPool pool, List<SupportPolygon> output) -> CapsuleFeatureStatus`, alongside its released signature. The transformed volume encloses all eight AABB corners, including rotated meshes. Both paths retain the same installed transform, winding, IDs and full polygons. Existing Bepu argument types are in the released signature at `SupportNeighborhoodMesh.cs:32` above.

- [ ] **Step 1:** Write `CompleteColumnFindsCrateAcrossPartitions`, `RemoteWholePolygonJoinsAndRingCapsMatchOracle`, `RawCapacityPublishesAtomically`, `RelevantUnsupportedCompoundLeafRefusesEverything` and `FootprintAuthenticatesBeforeCallingForeignLease`. Compare all four hidden-crate partitions, reversed insertion, multiple hiding slopes and stacked eligible tops to Task 1's full-volume oracle. Remote common-vertex joins use whole polygons and mutual-below, never localized adjacency. A face outside V cannot cap.

```csharp
// RawCapacityPublishesAtomically, flat up-facing fans at y=0, r=0.2
// Counts 96 and 256 complete, counts 257 and 300 refuse.
Assert.Equal(CapsuleFeatureStatus.CapacityExceeded, result.Status);
Assert.True(result.RequiredElements >= 257); // exactly 257 in the 257-member row
Assert.Equal(sentinelMembers, members);
Assert.Equal(sentinelJoins, joins);
// At 256: Members=256, JoinWordsPerRow=4, no raw coalescing.
```

- [ ] **Step 2:** Authenticate issuer/thread before monitor entry, then execute the two retained downward paths under that interval and selected view, with their original statics-only/back-face policy and arithmetic. Implement tree traversal over the outward V bounds and shared polygon capture. Certify both proposal contact neighborhoods during this operation, set ProposalMask, and return separate bounded contact-lowest verdicts. Invalid raised starts remain InvalidStart. Derive new above/below signs from certified installed contact geometry and enclosures, refusing an unestablished sign. Raw sweep Distance is not an above-band certificate. Relevant unsupported/ambiguous capture, unresolved extrema or joins refuse the union atomically. Until Task 6, relevant curved parts explicitly refuse rather than disappearing.
- [ ] **Step 3:** Sort by raw Static/ElementId, then witness role. Count each raw member once across volume and both contact contexts. Provision 256 members, their 512 fixed witness slots and 1,024 join words. Preserve required raw count diagnostics and all sentinel spans on refusal or exception. Authenticate issuer and thread before lease callbacks/monitor acquisition, then receiver/source/origin/generation/disposal checks at query and AssertFootprintCurrent.
- [ ] **Step 4:** Run `build-slot --label sg4-query -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~SupportFootprintTests&FullyQualifiedName!~Curved"`. Expect PASS for boxes/hulls/meshes, full-volume joins, short-span validation, exclusions, foreign receiver/source/thread, disposed view, expired and reacquired same-generation leases, removal/reuse and rebase. The relevant unsupported-compound test asserts no lower prefix is published.
- [ ] **Step 5:** Commit `feat(physics): publish complete selected footprint queries atomically`.

### Task 5: Curved extremum proofs

**Files:**
- Create: `docs/design/CONTACT-CONTROLLER-SUPPORT-CURVED-PROOFS-2026-10-11.md` (primitive/pose proofs and finite arithmetic obligations).
- Test: Task 1's `KhaozEngine.Game.Tests/Physics/SupportFootprintOracle.cs`, `SupportFootprintTests.cs`.
- Read: `KhaozEngine.Physics.Bepu/SupportNeighborhoodCurved.cs:18`, `:36`, `:65`, `:251`, existing `KhaozEngine.Game.Tests/Physics/SupportNeighborhoodCurvedTests.cs:26`, `:73`, `:105`, `:144`, `:163`, `:213`, `:258`.

**Interfaces:**
- Consumes Task 1's OracleTangent and curved oracle methods. Produces the proof document's named Sphere, CapsuleEndsAndBarrel, CylinderSide, CylinderCaps and TiltedPose certificates. These are mathematical sections, not new production APIs. Task 6 implements their attained witness, admission, slope-region and enclosure obligations.

- [ ] **Step 1:** Add `CurvedOracleReportsWitnessAndPerPointSupremum` with independent upper-surface and constrained-region calculations. Sphere centre `(0,-0.25,0)`, radius 0.25, axis `(0.35,0)`, r 0.2, c=cos(45 degrees): footprint witness X 0.15, Y -0.05, normal Y 0.8, tangent-at-axis -0.2. The axis misses the sphere, so no axis role exists. The SG-19 diagnostic height field `h=-(x-1)^2-100*z*z`, r 0.2 gives geometric witness `(0.2,0)` with contribution -0.96, while `(0.18,0.03)` gives -0.8776. The latter disproves identity with the per-point reading, not the geometric witness contract.

```csharp
// CurvedOracleReportsWitnessAndPerPointSupremum, sphere flank
Assert.Equal(SupportStatus.Walkable, oracle.Status);
Assert.True(oracle.HeightLower <= -0.2 && oracle.HeightUpper >= -0.2);
// Anisotropic diagnostic, evaluated exactly from its decimal rational inputs
Assert.Equal(-0.96, geometricContribution, 10);
Assert.Equal(-0.8776, alternatePointContribution, 10);
Assert.True(supremum.Lower >= alternatePointContribution);
```

- [ ] **Step 2:** Prove axis and footprint candidates for sphere, zero-length/upright/lying/tilted capsule and upright/horizontal/tilted cylinder with independent side and both caps. Use the phase 2b admitted installed-pose domain from the capture owner, including compound-composed poses, not a new tilt cutoff. For each part derive V admission, projection domain, attained maxima, slope equality and any highest-walkable-region alternative. Enumerate axis, boundary, tangency, barrel/end and cap/side ties. Prove closed-form strata where valid. Where invalid, specify a certified finite extremum solve and its termination/storage bound, without a fixed accuracy escape or regression to Unsupported for previously admitted ordinary poses.
- [ ] **Step 3:** Pin those proof strata in the oracle with exact installed geometry. Include hidden sphere/capsule/cylinder under the wedge, off-axis sphere/log, an axis that misses the solid and every admitted pose class. Record geometric witness contributions and the independent per-point supremum for each curved oracle row. A missing finite proof is a Task 5 stop, not permission for Task 6 to approximate.
- [ ] **Step 4:** Run `build-slot --label sg5-proofs -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CurvedOracle"`. Expect PASS for oracle/proof strata only. No backend code changes in this task.
- [ ] **Step 5:** Commit `docs(physics): prove attained curved footprint witnesses`.

### Task 6: Curved footprint parts

**Files:**
- Create: `KhaozEngine.Physics.Bepu/SupportFootprintCurved.cs`, `SupportCurvedArithmetic.cs` in that directory (SG-19 extrema and shared bounded arithmetic).
- Modify: Task 4's `KhaozEngine.Physics.Bepu/SupportFootprintCollector.cs` and existing `SupportNeighborhoodCurved.cs:251` in that directory (extract arithmetic, preserve released tangent query).
- Test: Task 1's `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.cs`, existing `SupportNeighborhoodCurvedTests.cs:258` in that directory and released equivalence fixture `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportNeighborhoodEquivalenceTests.cs:301`.

**Interfaces:**
- Consumes Task 5's proof certificates, Task 1's fixed roles and Task 3's volume. Existing `SupportCurvedLeaf` is at `SupportNeighborhoodCurved.cs:18` above.
- Produces `SupportFootprintCurved.Measure(StaticHandle owner, in SupportCurvedLeaf leaf, int part, in SupportFootprintRequest request, out SupportFootprintMember member) -> GeometrySign` and shared `SupportCurvedArithmetic` vector add/subtract/scale/divide/hull operations with the existing GeometryVector/GeometryInterval signatures (`SupportNeighborhoodCurved.cs:328` to `:342` above). Existing tangent evaluation keeps its arithmetic order and semantics.

- [ ] **Step 1:** Write `CurvedFootprintMatchesAllProvedPoseStrata` and `CurvedRawCapacityUsesTwoSlots`. Compare every Task 5 row with the independent oracle. Upright cylinder radius 0.4, length 0.5 gives top 0.5. Lying log radius 0.3, length 2, centre Y 0.3 gives top 0.6. Leaning cylinder radius 0.3, length 2, tilt 30 degrees retains separately derived cap/side geometry. At most two attained SG-19 roles per raw part, no joins or synthetic feature IDs.

```csharp
// CurvedRawCapacityUsesTwoSlots, overlapping distinct sphere statics,
// centre (0,-0.25,0), radius 0.25, query axis (0,0), r=0.2.
Assert.Equal(256, complete.Members);
Assert.Equal(512, complete.Witnesses);
Assert.True(members.All(m => m.WitnessCount == 2));
// 257 spheres: CapacityExceeded, RequiredElements=257, both spans untouched.
```

- [ ] **Step 2:** Implement the proved extrema against each installed leaf independently. Publish axis role when its upper surface meets the axis, then footprint role from the highest upper-surface point or SG-19's highest certified-walkable region. Equality at c is walkable. Do not reuse the old rounded-probe witness as the footprint maximum. Keep cylinder side/top/bottom raw part conventions. Extract only common bounded arithmetic from the old tangent kernel.
- [ ] **Step 3:** Run `build-slot --label sg6-curved -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~CurvedFootprintMatchesAllProvedPoseStrata|FullyQualifiedName~CurvedRawCapacityUsesTwoSlots|FullyQualifiedName~SupportNeighborhoodCurvedTests|FullyQualifiedName~SupportNeighborhoodEquivalenceTests"`. Expect PASS with the released query digest fixture unchanged.
- [ ] **Step 4:** Commit `feat(physics): certify curved footprint witnesses independently of occlusion`.

### Task 7: Locomotion integration

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/FootAnalyticGround.cs` (value analytic fact), `SupportCertification.Footprint.cs` in that directory (new contribution semantics in the existing certification owner).
- Modify: `KhaozEngine.Locomotion/Contacts/FootSupport.cs:52`, `:89`, `:126`, `SupportCertification.cs:38`, `ContactQueryContext.cs:101`, `GroundCore.cs:217`, `ShellGeometry.cs:16`, `ContactController.Leased.cs:31` in that directory.
- Modify: `KhaozEngine.Locomotion/MovementQueryLease.ContactQueries.cs:11`, `:42`, `MovementQueryLease.ContactSupport.cs:90`, `:142` in that directory.
- Modify test adapters: `KhaozEngine.Game.Tests/Locomotion/Contacts/CountingQueryView.cs:19`, `ContactLeasedQueryTests.Fixture.cs:65` in that directory, exposing the new optional capability without losing their call/fault counters.
- Test: Task 1's completeness fixture, existing `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportBoundaryTests.cs:92`, `:132`, `:141`, `FootSupportNeighborhoodTests.cs:212`, `:335`, `ContactLeasedQueryTests.cs:13`, `GroundReviewRegressionTests.cs:1` in that directory.

**Interfaces:**
- Consumes Tasks 1, 3, 4 and 6. Retains released `SupportCertification.CertifyNeighborhood` at the cited owner and Selection arithmetic at `FootSupport.cs:184` in that directory.
- Produces `FootAnalyticGround(float Height, Vector3 Normal)`, `FootSupport.DescribeFootprint(in FootSupportQuery query) -> SupportFootprintRequest` (cached probe shapes and original paths, without querying physics), and `FootSupport.Find(FootAnalyticGround? analytic, IPhysicsWorld? world, IPhysicsQueryLease? lease, in FootSupportQuery query) -> SupportSample`. Existing delegate overload (`FootSupport.cs:52`) samples the axis once and forwards a value. The leased caller passes `MovementAnalyticGround`'s value fields (`KhaozEngine.Locomotion/MovementContactSupportFacts.cs:8`), with no captured callbacks.
- Produces readonly `FootprintContribution(int MemberIndex, SupportContribution Contribution)` and `SupportCertification.CertifyFootprint(IPhysicsSupportFootprint capability, IPhysicsQueryLease lease, in SupportFootprintResult result, ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins, Vector2 axis, float cosMaxSlope, Span<FootprintContribution> contributions) -> int` in the new partial. Make the existing certification class partial to share plane arithmetic without duplication. Flatten at most 512 contributions while joins index the 256 raw members. MemberIndex retains original Static/ElementId for selection even when another plane supplies Normal. Context-only planes cap or suppress but never offer support.
- Preserves `MovementQueryLease.CheckContactSupportQuery(in MovementSupportRequest request, in FootSupportQuery query) -> MovementAvailability`, `FindContactSupport(..., out SupportSample sample, out MovementContactSupportBinding? binding) -> MovementAvailability` and binding methods at the cited owners. Adds internal `ShellGeometry.Validate(in MoveTuning tuning, in GroundCoreSettings settings) -> void`, preserving the existing default-footprint public ContactShell validation door (`KhaozEngine.Locomotion/Contacts/ContactShell.cs:13`).

- [ ] **Step 1:** Make the Task 1 crate row green for all partitions. Add `CompleteFindUsesTwoProposalsAndOneVolume`, `RimFloorAtLowerReachIsFound`, `AdmissionStraddlesProbeStart`, `FullRampMaximumDoesNotLoseValidAxisPlane` and `RingOnlyMemberNeverPublishesSupport`. The rim patch x `[0.195,0.201]`, z `[-0.01,0.01]`, y=-0.4 with no axis floor must return Walkable at its installed top for feet 0, r 0.2, reach down 0.4. A steep sliver at y 3.1 to 3.25 with axis plane 0.125 lies above S and is ignored, then the same sliver lowered by 2.4 straddles S and is admitted. For Task 3's ramp, `[0.199,0.201]` permits axis support about 0.1, while moving the fragment to `[0.15,0.16]` stops the probe above U=0.125 and refuses.

```csharp
// CompleteFindUsesTwoProposalsAndOneVolume, non-analytic successful row
Assert.Equal(2, counts.Proposals);
Assert.Equal(1, counts.Volumes);
// SphereFlankFootprintWitnessIsWalkable, replaces the old named flank row
Assert.Equal(SupportStatus.Walkable, sample.Status);
Assert.True(Math.Abs(sample.Height - installedSphereContribution) <= sample.HeightError);
Assert.True(sample.HeightError <= 0.0005f); // nominal contribution -0.2
```

- [ ] **Step 2:** Add `TooSmallFootprintRejectsBeforeEveryPath` with r 0.005, exactly 0.01 and represented neighbors below/above 0.01, analytic-only and sweep-miss worlds, and ground/shell tuning whose fraction times capsule radius lies on each side. The default fraction is 0.5, so capsule radius 0.01 rejects and radius 0.02 reaches the footprint minimum, subject to unchanged shell constraints. Add `ExpandedScopeRefusesBeforeProducerOrBackend` with c=0, finite arithmetic overflow, too-small Y/XZ scope, required witness/cap outside scope, valid expanded scope and float conversion at a coarse datum. Assert producer/backend counters zero on the pre-call scope failure.
- [ ] **Step 3:** Change Find to supply the original two proposal paths to one complete query, which runs/certifies both after issuer authentication. Remove Find's pre-query lease callbacks and raw physics calls so a foreign lease cannot reach a sweep's monitor first. Certify all contributions and apply proposal guards separately from bounded lowest/contact-context facts. Reuse selection's midpoint/tie order, analytic axis semantics and original provenance. Reject r below 0.01 at configuration on every path. CheckContactSupportQuery uses DescribeFootprint and Task 3's shared bounds before analytic sampling or backend work. Keep ContactSupportScope's body/reach/transition and binding witness-height checks. Never acquire inside Find or widen the scope.
- [ ] **Step 4:** Extend leased tests over initial support, ground travel, pushed-axis seat, descending landing, every signal sample (sample zero reused), opposing-face slide, rounded-axis binding, final clamp and medium retries. Require one interval and canonical owner binding. A refused start holds, refused target blocks, leased failure holds the whole tick and optional signal failure retains omission semantics. Preserve `ContactAboveTheBandRefuses` (shelf 0.4007), `StepJustAboveTheBandRefuses` (top 0.4005), `GeometryInsideTheLegStartRefuses`, `OverhangAboveTheBandIsIgnored` and `RefusedSurfaceBelowTheBandIsIgnored`. Keep valley/ridge/apex/saddle/plateau/bevel caps and back/vertical-fin rows. Derive and report each SG-23 expectation change instead of updating snapshots.
- [ ] **Step 5:** Run `build-slot --label sg7-integration -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~FootSupport|FullyQualifiedName~ContactLeasedQueryTests|FullyQualifiedName~GroundReviewRegressionTests|FullyQualifiedName~ContactControllerTests|FullyQualifiedName~ExplicitContact"`. Expect PASS, including the first crate row, sphere -0.2, 1 mm arrival, phase 3/4a/4b regressions and the adequately scoped headless explicit proof. No navigation capture.
- [ ] **Step 6:** Commit `fix(locomotion): select complete footprint support under the tick lease`.

### Task 8: Allocation inventory and lifetime tokens

**Files:**
- Create: `KhaozEngine.Locomotion/ContactTickWorkspace.cs`, `ContactCapsuleShapes.cs` in that directory (bounded internal workspace and exact-dimension shape reuse).
- Modify: `KhaozEngine.Locomotion/MovementEnvironmentContext.cs:81`, `MovementQueryLease.cs:50`, `MovementQueryLease.ContactSupport.cs:11`, `MovementQueryLease.Sweeps.cs:35`, `MovementQueryLease.Solids.cs:41`, `MovementScopeWitness.cs:31` in that directory.
- Modify: `KhaozEngine.Locomotion/Contacts/ContactController.Leased.cs:55`, `:84`, `LeasedContactQueryContext.cs:19`, `LeasedContactQueryContext.Motion.cs:51`, `ExplicitContactModes.cs:21`, `ShellGeometry.cs:37` in that directory.
- Modify fixture: `KhaozEngine.Game.Tests/Locomotion/Fixtures/EnvironmentAcquisitionFixture.cs:73`, `:90`, `:108`, `:140`, and its `EnvironmentAcquisitionFixture.ContactSupport.cs` partial in that directory (immutable prepared witness and genuinely expired fresh pin tokens).
- Test: Task 2's cost fixture, existing `KhaozEngine.Game.Tests/Locomotion/MovementEnvironmentContextTests.cs:1`, `KhaozEngine.Game.Tests/Physics/PhysicsQueryLeaseTests.cs:1`.

**Interfaces:**
- Consumes Task 7's analytic value route. Existing public composed lease, physics ReadLease and scope witness stay unique immutable lifetime identities (`MovementQueryLease.cs:35`, `BepuPhysicsWorld.QueryLease.cs:69`, `MovementScopeWitness.cs:8` at the owners above).
- Produces internal `ContactTickWorkspace` owned by MovementEnvironmentContext, passed into each fresh MovementQueryLease. It retains support consistency storage, reusable leased context/modes and exact-dimension shape slots. `Enter(MovementQueryLease lease) -> void`, `EndTick() -> void`, `EndInterval() -> void` guard ownership, clear borrowed references and distinguish per-tick analytic consistency from per-interval owner consistency.
- Produces `ContactCapsuleRole { Body, Shell, RoundedSweep, RoundedContact }` and `ContactCapsuleShapes.Get(ContactCapsuleRole role, float radius, float length) -> CapsuleShape` from bounded exact-bit dimension slots, provisioned outside the hot interval. Distinct roles prevent alternating body/shell calls from evicting each other. Structural capacity comes from the inventoried real route, not a new gameplay refusal limit.
- Adds reusable `LeasedContactQueryContext.Initialize(MovementQueryLease lease, MoveTuning tuning, MovementSelection selection, MovementTransitionContext transition, float placementOffset = PlacementSkin, WaterTraversalMode waterMode = WaterTraversalMode.DryOnly, MovementBoundary? boundary = null) -> void`, and `ExplicitContactModes.Initialize(ContactQueryContext queries, in MoveState state, in MoveTuning tuning, in WaterTraversalPolicy water, bool jump) -> void`. Constructors remain for existing test/cold callers. Hot execution uses retained workspace owners, fully reset each tick. No public token is retained for reacquisition.

- [ ] **Step 1:** Inventory warm allocation through `MovementEnvironmentContext.TryAcquire`, producer preparation/pin, composed lease/release binding, physics acquire/dispose, borrowed controller, support calls, shell calls, witness creation and release. Name exact bytes per runtime for MovementQueryLease plus release binding, physics ReadLease and producer pin token. Do not infer object size from historical 208 bytes or measure only a borrowed lease. Add `ExplicitTickAllocatesOnlyNamedLifetimeTokens` and `DisposedTokensStayExpiredAfterReacquisition` before optimizing.

```csharp
// ExplicitTickAllocatesOnlyNamedLifetimeTokens, full acquisition through disposal
Assert.Equal(movementTokenBytes + physicsTokenBytes + pinTokenBytes, warmExplicitTickBytes);
Assert.Equal(0, warmBorrowedSupportBytes);
Assert.Equal(0, warmBorrowedSampleAndBindingBytes);
// Compare stationary, moving, pushed, look-ahead and mixed-mode routes:
Assert.Equal(fewFindTokenBytes, manyFindTokenBytes);
// Dry tick: at most the observed 208 bytes, zero Find-dependent growth.
```

- [ ] **Step 2:** Cache immutable shapes by exact dimension bits, reuse bounded consistency arrays/workspace, retained contexts and modes, and prepare each immutable witness once. Preserve mutable-input revalidation and resource-ID limits in MovementScopeWitness.TryCreate. End the tick clears analytic entries, end the interval clears owner entries, both release borrowed references. Exceptional paths release workspace before reacquisition. Producer fixtures model a real fresh one-use pin, not a never-expiring fake or freshly allocated witness on every property read.
- [ ] **Step 3:** Assert stale composed lease, old footprint result, ReadLease and pin rejection after dispose/reacquire with unchanged geometry generation, wrong thread and exception during disposal. Compare cold/warm/interleaved controller output and owner consistency. If token bytes cannot meet the inventoried ceiling safely, report the named inventory for review. Do not pool/reset disposed public tokens or exclude acquisition from the window.
- [ ] **Step 4:** Run `build-slot --label sg8-allocation -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~ExplicitTickAllocatesOnlyNamedLifetimeTokens|FullyQualifiedName~DisposedTokensStayExpiredAfterReacquisition|FullyQualifiedName~MovementEnvironmentContextTests|FullyQualifiedName~PhysicsQueryLeaseTests|FullyQualifiedName~ContactLeasedQueryTests"`. Expect PASS with exact token totals returned in the task report. No timing invocation here.
- [ ] **Step 5:** Commit `perf(locomotion): retain bounded tick workspace without reviving leases`.

### Task 9: Lossless cost work

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/SupportNeighborhoodJoins.cs:10`, `:24`, `:35`, `SupportNeighborhoodCollector.cs:184` in that directory, Task 4's footprint collector and Task 7's CertifyFootprint.
- Test: existing `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportNeighborhoodEquivalenceTests.cs:301` and fixture `Fixtures/support-equivalence.txt` in that directory (read-only), Task 1's physics oracle rows, Task 2's cost fixture/artifact.
- Conditional only if SG-12 still fails: create `KhaozEngine.Physics.Bepu/InstalledSupportGeometryCache.cs`, modify `BepuPhysicsWorld.cs:125`, `:139`, `BepuPhysicsWorld.Rebase.cs:55`, shared capture owners from Task 4.

**Interfaces:**
- Consumes Tasks 1 to 8. Preserves `SupportNeighborhoodJoins.Joined(SupportPolygon first, SupportPolygon second, double band, SupportSegmentPolygon kernel) -> GeometrySign` and the full released join matrix.
- Produces an exact common-vertex/edge meeting fast path only after mutual-below certification, and an identical-plane/same-certified-kind cap skip without deleting raw members. If caching is justified, its file produces `InstalledSupportLeaf(int LeafId, Pose LocalPose, Vector3[] Vertices, int[][] Faces, Vector3[] Normals, SupportCurvedShape? CurvedShape, double Radius, double HalfLength)` and `InstalledSupportGeometry(IReadOnlyList<InstalledSupportLeaf> Leaves)`, with captured arrays privately owned and never modified after publication. `InstalledSupportGeometryCache.GetOrCapture(Simulation simulation, StaticHandle owner, TypedIndex installation, long generation, out InstalledSupportGeometry? geometry) -> CapsuleFeatureStatus`, `Remove(StaticHandle owner) -> void` and `Clear() -> void` key immutable local geometry by owner, installation identity and geometry generation. Evict at removal/reuse/replacement/rebase. Never cache query winners/refusals or differently rounded world transforms.

- [ ] **Step 1:** Write `SharedApexFastPathPreservesWholePolygonVerdicts` with 96 up-facing fan triangles at y=0, common apex 0, rim radius 1. There are 4,560 unordered pairs, compared with the historical 9,120 directed exact tests per old Find. Assert joins preserved and expensive meeting work pruned, not raw count reduced. Add remote vertex, shared edge, a represented crack on each side of e 0.0001, non-manifold incidence, split statics, intersecting planes and convex/concave crease rows. Interval uncertainty and refusal verdicts must match the old fixture.
- [ ] **Step 2:** Implement common-incidence proof and identical-plane cap skip. Prove `ColdWarmInterleavedAndPartitionsAgree`: identical installed inputs replay bit-identically from cold, warm, interleaved and rebuilt scratch. Equivalent mesh/compound/split partitions agree in status/height inside their error bounds, with genuine partition-local handles and canonical bindings. Add removal/reuse/replacement/rebase and changed exclusions to the same rows if geometry caching is introduced.

```csharp
// ColdWarmInterleavedAndPartitionsAgree
Assert.Equal(cold, warm);
Assert.Equal(cold, afterInterleavedQuery);
Assert.True(Math.Abs(mesh.Height - split.Height) <= mesh.HeightError + split.HeightError + installedGeometryGap);
Assert.Equal(mesh.Status, split.Status);
```

- [ ] **Step 3:** Leave geometry caching absent unless the new cost evidence identifies repeated installed capture as the cause of a remaining SG-12 failure. If that condition is proved, use the conditional owner above, same certified installed-pose operator and same output ordering. Verify once per changed algorithm, always against old-query equivalence and new-query oracle separately. No exact/rounded position result cache, adjacency substitute, smaller contact band, member coalescing or cached refusal.
- [ ] **Step 4:** Run `build-slot --label sg9-equivalence -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~SupportNeighborhoodEquivalenceTests|FullyQualifiedName~SupportFootprintTests|FullyQualifiedName~FootSupportCompletenessTests|FullyQualifiedName~ColdWarmInterleavedAndPartitionsAgree|FullyQualifiedName~CostRowsHaveSuccessfulGeometryAndRouteBounds"`. Expect PASS. Then append the changed measurements and SG-22 inventory to the artifact in one invocation, with the same settings/scenes as the baseline:

```bash
DOTNET_TieredCompilation=0 KHAOZ_SUPPORT_COST_ARTIFACT=/Users/antonio/KhaozEngine/.worktrees/contact-controller-support-gates/docs/verification/2026-10-11-support-gates-cost.json build-slot --label sg9-cost -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact"
support_cost_exit=$?
python3 scripts/record-support-cost-exit.py docs/verification/2026-10-11-support-gates-cost.json "$support_cost_exit"
```

Every successful row must meet deterministic bounds and allocation limits, and all non-refusal current geometry rows must succeed. Read the one-shot time verdict against 50/500 us Find, 200/1,000 us actual substep, 1,000/4,000 us tick and `legacy + max(100 us, 0.25*legacy)`. A time failure requires algorithm work, never a raised limit or another unchanged local timing run. A borderline local pass does not close #1334.
- [ ] **Step 5:** Commit `perf(physics): preserve complete support while removing redundant join work`.

### Task 10: Hosted measurement plumbing

**Files:**
- Modify: `.github/workflows/stress-test.yml:13`, `:47` in the engine worktree (opt-in measurement forwarding).
- Modify: Task 2's `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportCostArtifact.cs` (normalized verdict), add rows in `SupportGateCostTests.cs`.
- Upstream dependency: `/Users/antonio/ci-workflows/.github/workflows/stress-test.yml:18`, `:130`, `:183`, `/Users/antonio/ci-workflows/README.md:15`, in an isolated ci-workflows task worktree under its own contributor rules. The engine pins `fc4a188e74c1177bef17343d4e6a70cc4a403595`, whose input list and repeat/upload steps cannot forward either SG-20 environment setting or retain successful artifacts.

**Interfaces:**
- Consumes Task 2's artifact schema and Evaluate method. Produces an optional reusable-workflow `support-cost` boolean input, default false, forwarded by the engine caller. When true, require Release/load=0, set DOTNET_TieredCompilation=0, set KHAOZ_SUPPORT_COST_ARTIFACT to a unique `support-cost/run-N.json` per iteration and upload all cost artifacts on success or failure. Inputs enter shell only through env. Keep explicit ubuntu-26.04 and full-SHA action/workflow pins.
- Produces normalized acceptance with `k = legacyHostedFlatBoxTickUs / legacyM2MaxFlatBoxTickUs`. Scale every absolute SG-12 time ceiling by k, never bytes or counters. Comparable ordinary hosted tick requires `candidateHosted <= legacyHosted + max(k*100 us, 0.25*legacyHosted)`. Reject nonpositive/nonfinite baselines, missing/mismatched scenes, hardware/compilation metadata or failed geometry before normalization.

- [ ] **Step 1:** Write `HostedNormalizationScalesFasterAndSlowerHosts` and `InvalidHostedBaselineCannotPass`. M2 legacy flat-box tick 400 us gives faster host legacy 200 us, k=0.5, ordinary ceilings 25/100/500 us, dense ceilings 250/500/2,000 us and relative tick ceiling 250 us. Slower host legacy 800 us, k=2, ordinary ceilings 100/400/2,000 us, dense ceilings 1,000/2,000/8,000 us and relative tick ceiling 1,000 us. Boundary values pass and their next represented values fail. Zero/negative/nonfinite legacy, missing artifact or failed/stationary fallback row cannot pass.

```csharp
// HostedNormalizationScalesFasterAndSlowerHosts
Assert.Equal(0.5, fasterScale);
Assert.Equal(250, fasterRelativeTickCeilingUs);
Assert.Equal(2, slowerScale);
Assert.Equal(1000, slowerRelativeTickCeilingUs);
// InvalidHostedBaselineCannotPass
Assert.False(invalidBaselineVerdict);
Assert.False(missingArtifactVerdict);
```

- [ ] **Step 2:** Implement normalization and caller/shared-workflow plumbing with no dispatch. Check unique artifact paths, preserved logs/TRX behavior, upload `always()` behavior, fixed compilation and load=0 from static workflow/schema checks. Run `build-slot --label sg10-normalization -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~HostedNormalizationScalesFasterAndSlowerHosts|FullyQualifiedName~InvalidHostedBaselineCannotPass"`, expect PASS. Upstream static checks are `sh scripts/check-workflows.sh`, `sh scripts/check-agent-instructions.sh` and `ruby -ryaml -e 'Dir.glob(".github/workflows/*.{yml,yaml}").each { |path| YAML.parse_file(path) }'`, from its own worktree. No repeat run is launched.
- [ ] **Step 3:** Ruling SG-P1 (orchestrator): ci-workflows `AGENTS.md:26` prescribes one caller run with a small input (one iteration, no load) to prove a reusable-workflow change before callers move their pins. That run is the repository's plumbing check, not a load or stress test, so it runs here on GitHub-hosted runners with `iterations=1`, `load=0`, Release and the filter `FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact`. The implementer prepares the ci-workflows change in its own worktree under its own rules and the engine caller change pointing at that new ci-workflows commit, commits both locally and stops. The orchestrator lands ci-workflows, pushes this branch, dispatches exactly that one caller run (`gh workflow run stress-test.yml --ref feature/contact-controller-support-gates` with those inputs), and keeps the new pin only if the run exits 0 and uploads one artifact. Otherwise the caller change is reverted and the failure returns to this task. Any run with more than one iteration remains the SG-24 repeat proof and needs the owner's approval at the phase 6 stop.
- [ ] **Step 4:** Commit the engine caller, pin and verdict change with `ci(physics): forward and retain normalized support cost artifacts`. Report the ci-workflows commit. The orchestrator records the run URL, artifact name and exit status in the ledger. #1334 remains open with local evidence linked.

### Task 11: Gate review and verification

**Files:**
- Modify: `CHANGELOG.md:8`, `docs/INDEX.md:147`, `docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md:197`, `:282`, `docs/design/CONTACT-CONTROLLER-PHASE-2B-SUPPORT-NEIGHBORHOOD-2026-10-10.md:243`, support-gates spec status at `:7` in its file.
- Modify living API references: `KhaozEngine.Physics/README.md:63`, `KhaozEngine.Physics.Bepu/README.md:80`, `KhaozEngine.Locomotion/README.md:508`, `:711`, `docs/USING-KHAOZENGINE.md:8611`, `:8970`.
- Read: `Directory.Build.props:25`, `docs/CONTRIBUTOR-RULES.md:109`, `:158`, existing `scripts/check-doc-versions.sh:31`.
- Create: `docs/verification/2026-10-11-support-gates-final.json` (checked command exits, totals, geometric/cost gate dispositions, token inventory and unresolved hosted ruling).

**Interfaces:**
- Consumes Tasks 1 to 10 and their evidence. Produces the verified complete footprint capability and SG-23's six derived release-history classes. No new movement selector, profile/wire field, navigation capture or aggregate actor budget.

- [ ] **Step 1:** Fetch and reconcile current local/remote main into this task branch before final verification. Preserve local unpushed main work and resolve here, not on main. Inspect changed production/test/doc paths and outstanding task/ruling evidence.

```bash
git fetch origin
git merge main
git merge origin/main
git tag --sort=-v:refname | head -3
rg -n 'KhaozEngineVersion' Directory.Build.props
```

- [ ] **Step 2:** Ride staged 20.33.0 unless current main has advanced to another staged minor. Keep its pin and existing entry. Add the optional API and exactly these six SG-23 result classes to that entry: hidden support found, lower-band rim support found, polygon witness becomes exact disc maximum, SG-19 curved witnesses including Walkable sphere flank -0.2, additional admitted members supply released-predicate caps, and closed-disc-only support with ring members context-only plus configuration rejection below 0.01 m. Record derivations for changed expectations in task evidence, no snapshot updates. Update program/phase 2b known limits, docs index and living API use. Sweep all Markdown for changed names and obsolete first-contact/cost claims.
- [ ] **Step 3:** Run final verification once from this worktree, after reconciliation and final documentation edits. Create the gitignored local feed before restore if absent. No local timing environment in the full suite.

```bash
mkdir -p local-feed
build-slot --label sg11-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --label sg11-suite --heavy -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
build-slot --label sg11-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

Expect exit 0, zero warnings, every headless geometry/lifetime/counter/allocation row passing, unchanged released neighborhood fixture, and no changed ordinary error ceiling. Inspect actual exits and diffs. Record totals and normal GPU/live exclusions without claiming a GPU or windowed proof. A new failure or reconciliation change justifies targeted repair/reverification, never an unchanged test loop.
- [ ] **Step 4:** Review SG-1 to SG-24 against the evidence. #1347 can close only after polygon, every admitted curved pose, partition, refusal, boundary and adequately scoped explicit rows pass and the verified commit is pushed by the orchestrator. #1334 stays open until the normalized approved hosted proof passes. A failing per-operation gate returns to its owning algorithm task. SG-21's game aggregate budget and all remaining #438 adoption blockers stay at phase 6. Do not claim the whole gate complete while Task 10's ruling/prerequisite remains unresolved.
- [ ] **Step 5:** Commit explicit final docs/evidence paths with `docs(20.33.0): record complete footprint support gates`. Return the verified SHA, commands/exits, token bytes, changed result derivations and open hosted proof to the owning orchestrator. No release, tag, pin, game edit or navigation work.

## Self-review

- [x] Spec coverage. SG-3/4/7/19 contract and independent oracle in Task 1. SG-9/10/11/12/20 baseline in Task 2. Polygon and complete query in Tasks 3/4. Curved proof and implementation in Tasks 5/6. SG-1/2/5/6/8 and all SG-23 rows in Task 7. SG-13/22 inventory and lifetime in Task 8. SG-14/15 equivalence and cost work in Task 9. SG-24 plumbing/normalization in Task 10. SG-17/18/21/23 final evidence, staged version and separate gate dispositions in Task 11. All SG-16 row classes have an owner.
- [x] Step scan. Every task declares files, consumed/produced interfaces, named test geometry/assertions, a synchronous focused command and a commit subject. Task 1's intentional red crate is retained until Task 7. Task 5 writes proof/oracle only. Task 10 states its concrete external validation conflict instead of inventing approval.
- [x] Type consistency. Physics members remain raw Static/ElementId records with two role slots, the join matrix stays raw-member-indexed, and locomotion flattens at most 512 contributions without forging provenance. Shared dependency-free volume bounds serve backend and scope guards. Proposal paths execute inside authenticated physics, before which Find invokes no lease callback or raw sweep. Leased support passes value analytic facts and uses the existing composed lifetime.
- [x] Review Focus. Overflow and analytic-only minimum rows are owned by Task 7, mixed unsupported compounds by Task 4, same-generation expired tokens and exceptional workspace release by Task 8, and invalid hosted normalization/artifact rows by Task 10.
- [x] Proportion. Signatures, finite test cases, assertions and commands determine implementation. No solver bodies or timed loops are supplied. Eleven tasks match SG-17 without splitting.
