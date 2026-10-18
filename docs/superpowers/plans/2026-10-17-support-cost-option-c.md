# Support cost, SG-12 option C, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ordinary-scene `FootSupport.Find` meet the SG-12 limits by removing repeated work, and correct the hosted verdict's yardstick, without changing any limit or any support result.

**Architecture:** A test-reachable reference evaluator (today's code path) is the oracle for every optimization. The cached installed geometry gains an immutable, pose-applied world form. Cheap certified rejects run before expensive derivation. Topology proves joins inside one convex leaf. Curved kernels go interval-first. The verdict code computes `k` as a median over all legacy rows and reports oracle-correct refusals as envelope rows.

**Tech Stack:** C# / .NET 10, BepuPhysics, xUnit, `KhaozEngine.Physics.Bepu`, `KhaozEngine.Game.Tests`.

**Spec:** `docs/design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md` (rulings SC-1 to SC-9), on top of `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md`.

## Global Constraints

- No SG-12 limit, ceiling, tolerance or assertion epsilon changes anywhere.
- Every optimized Find returns results bit-identical to the reference evaluator: status, height, height error, raw feature provenance, owner binding and XZ (SC-9).
- Warm Find allocates 0 managed bytes and storage stays bounded (SG-12). No support result is cached across axes, ticks, bodies or reconciles (SG-15).
- The ordinary suite asserts counters and allocation, never wall time (SG-20).
- Every build and test runs through `build-slot --label <task> -- <command>`, with `--heavy` for the full suite and the cost invocation. One focused test command at a time. No loops of runs, no parallel test processes, no stress.
- Create `local-feed/` before restore (`mkdir -p local-feed`). Build `dotnet build KhaozEngine.slnx -c Release` at zero warnings.
- Shipped text, comments and docs carry no em or en dashes and no prose semicolons. Run `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree` and `sh scripts/check-file-size.sh --tree` before each commit.
- Commit subjects use `area(scope): summary`. No attribution trailers.
- Engine version rides the staged `21.1.0`. Only the final task touches `CHANGELOG.md`.

## Review Focus

1. A static pose that changes between Finds without passing through `StaticGeometryChanged`. The exact pose-bit check must refuse the published form and match the reference (Task 2, `PoseBitMismatchMaterializesWithoutPublishing`).
2. Concurrent first use of one static's world form by parallel bake workers. One publication, identical results on every reader (Task 2, `ConcurrentReadersShareOneWorldForm`).
3. Compound statics with rotated or offset children. The form is keyed by every child's local pose bits as well as the static pose (Task 2, `CompoundChildPosesKeyTheWorldForm`).
4. A member exactly at the contact band boundary, with represented values on each side. The prefilter must keep it (Task 3, `PrefilterKeepsBandBoundaryMembers`).
5. An allocation observer installed during the first build. The build runs outside the key lock and the first publication wins (Task 2, `ObservedBuildPublishesFirstWins`).

---

### Task 1: Corrected hosted calibration and envelope rows (SC-2, SC-3)

**Files:**
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportCostArtifact.cs` (`SupportCostVerdict.TryHostedScale`, `CalibrationTick`, `Evaluate`)
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportCostArtifactTests.cs`
- Modify: `docs/verification/2026-10-11-support-gates-cost.json` (one new section)
- Modify: `docs/verification/2026-10-14-support-cost-hosted-1334.md` (append a section, keep the original verdict text)
- Modify: `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md` (one sentence under SG-24 pointing at SC-2)

**Interfaces:**
- Produces: `internal readonly record struct HostedScale(double K, int Rows, double LowerQuartile, double UpperQuartile)` and `internal static bool TryHostedScale(JsonObject? m2MaxReference, JsonObject? hosted, out HostedScale scale)`. Update every caller.
- Produces: an `Envelope` outcome from `Evaluate` for oracle-correct `None` Finds and oracle-blocked stationary substeps, counted in neither passes nor rejections.

- [ ] **Step 1: Write the failing tests.** In `SupportCostArtifactTests`:
  - `HostedScaleIsTheMedianOverComparableLegacyRows`: build 41 legacy rows where 40 have ratio 2.0 and the flat box legacy tick has ratio 0.6. Assert `K == 2.0`, `Rows == 41`, and both quartiles equal 2.0.
  - `HostedScaleRefusesBelowFortyRows`: 39 comparable rows. Assert `TryHostedScale` returns false.
  - `HostedScaleIgnoresRowsMissingOrFailedInEitherSection`: a row present only in hosted, and a row whose M2 sample is unsuccessful, are not counted.
  - Keep the existing faster-host and slower-host examples, rewritten to the median form, with the same expected verdicts.
  - `OracleCorrectRefusalIsAnEnvelopeRow`: a Find row with candidate `None` equal to its oracle evaluates to `Envelope`. `OracleMismatchStaysAGeometryFailure`: candidate `None` with an oracle `Walkable` stays a `G` rejection. `OracleBlockedStationarySubstepIsAnEnvelopeRow`: a stationary substep whose oracle says blocked is `Envelope`.
- [ ] **Step 2: Run them and see them fail.** `build-slot --label sc-t1 -- dotnet test KhaozEngine.Game.Tests -c Release --filter "FullyQualifiedName~SupportCostArtifactTests"`. Expected: the new tests fail to compile or fail.
- [ ] **Step 3: Implement.** Comparable rows are every sample whose operation starts with `Legacy`, present and successful in both sections, matched by scene and operation, with M2 warm time above 0.5 us. `K` is the median of `hosted / M2`. Quartiles use the same ordering. Keep the metadata, scene-input and tuning-input checks.
- [ ] **Step 4: Run the tests and see them pass** with the same command.
- [ ] **Step 5: Commit** `test(support-cost): scale hosted runs by the median legacy ratio and report envelope rows`.
- [ ] **Step 6: Record the M2 reference at the hosted commit.** `git worktree add --detach /Users/antonio/KhaozEngine/.worktrees/sc-ref-376971dda 376971dda`, `mkdir -p local-feed` there, build Release through `build-slot --heavy`, then run the cost invocation exactly as `SupportGateCostTests` prints it (`DOTNET_TieredCompilation=0 KHAOZ_SUPPORT_COST_ARTIFACT=<path> dotnet test ... --filter <the one-shot row>`) once, through `build-slot --heavy`. Merge the artifact into `2026-10-11-support-gates-cost.json` as section `reference-376971dda` with the same field set as the existing sections. Remove the temporary worktree.
- [ ] **Step 7: Re-evaluate the 20 approved artifacts** in `/Users/antonio/KhaozEngine/.superpowers/sdd/1334-hosted-support-cost/artifacts/run-1..20.json` against `reference-376971dda` with the new verdict code (a test-project helper or the existing evaluation entry, not a new script outside the repo). Append `## Corrected calibration (SC-2, SC-3)` to the hosted record: per-iteration `K`, rows and quartiles, ordinary-row pass and reject counts with envelope rows listed separately, dense rows listed without a time verdict (SC-1), and the median candidate-to-limit ratio per operation family. Do not edit the original sections.
- [ ] **Step 8: Commit** `docs(support-cost): record the M2 reference and corrected #1334 verdict`.

### Task 2: Reference evaluator, equivalence harness and installed world form (SC-4, SC-9)

**Files:**
- Create: `KhaozEngine.Physics.Bepu/SupportReferenceEvaluation.cs`
- Create: `KhaozEngine.Physics.Bepu/InstalledSupportWorldForm.cs`
- Modify: `KhaozEngine.Physics.Bepu/InstalledSupportGeometry.cs`, `KhaozEngine.Physics.Bepu/SupportFootprintCollector.cs` (`Collect`), `KhaozEngine.Physics.Bepu/SupportNeighborhoodPolyhedra.cs` (`Polygons`)
- Modify: `KhaozEngine.Physics/SupportQueryDiagnostics.cs` (counter `WorldFormBuilds`)
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintEquivalence.cs`, `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.WorldForm.cs`

**Interfaces:**
- Produces: `internal static class SupportReferenceEvaluation { internal static bool Active { get; } internal static Scope Begin(); }`, a thread-scoped switch where `Scope` is a disposable struct restoring the previous value. While active, every optimization in this plan is bypassed and Find runs today's evaluator.
- Produces: `InstalledSupportWorldForm`, an immutable world-space form built from one `InstalledSupportGeometry` and one exact static pose (plus compound child local poses), holding the certified world vertices, pose operator results, validated normals and the query-independent polygon facts `SupportNeighborhoodPolyhedra.Polygons` derives. `InstalledSupportGeometry` publishes at most one form, first-wins, with the same observer rule as `InstalledSupportGeometryCache.Slot`.
- Produces: `SupportQueryCounter.WorldFormBuilds`, incremented once per form built (published or not).
- Produces: `SupportFootprintEquivalence.AssertMatchesReference(...)`, which runs one Find or substep both ways and compares every field named in the Global Constraints. Tasks 3 to 5 reuse it.

- [ ] **Step 1: Write the failing tests** in `SupportFootprintTests.WorldForm.cs`:
  - `WorldFormMatchesReferenceOnEveryCostScene`: every `SupportGateCostScenes` row, run cold, then warm, then interleaved with another scene, matches the reference.
  - `WarmFindBuildsNoWorldForm`: on `flat box`, the first Find records `WorldFormBuilds == 1` and the second records `0`.
  - `ChangedStaticPoseRebuildsWorldForm`: move the static through the world API, then Find. One new build, and the result matches the reference at the new pose.
  - `PoseBitMismatchMaterializesWithoutPublishing`: a lookup with pose bits that differ from the published form returns the reference result and leaves the published form unchanged.
  - `CompoundChildPosesKeyTheWorldForm`: a compound with a rotated, offset child matches the reference, and a changed child local pose does not reuse the old form.
  - `RebuiltWorldMatchesReference`: the same scene rebuilt from scratch gives bit-identical results.
  - `ConcurrentReadersShareOneWorldForm`: parallel readers under the existing `QueryGate`, using the `BoundedThread` helper, return identical results with one publication.
  - `ObservedBuildPublishesFirstWins`: with an allocation observer installed during the first build, the result matches the reference and exactly one form is published.
  - `WarmFindWithWorldFormAllocatesNothing`: warm Find allocates 0 bytes on `flat box`, `flat hull` and `stairs box`.
- [ ] **Step 2: Run them and see them fail.** `build-slot --label sc-t2 -- dotnet test KhaozEngine.Game.Tests -c Release --filter "FullyQualifiedName~SupportFootprintTests"`.
- [ ] **Step 3: Implement.** Build the form with the existing arithmetic in the existing order (`InstalledPoseOperator.TryCreate`, `TransformVertices`, `ValidateInstalledNormals`, and the polygon derivation now in `SupportNeighborhoodPolyhedra.Polygons`). Per-query operand state stays in collector scratch. Bind query polygons from the form without recomputing pose arithmetic. Mesh statics keep today's path.
- [ ] **Step 4: Run the focused tests, then the existing support and parity suites** (`--filter "FullyQualifiedName~Support|FullyQualifiedName~ConcurrentQueryParity|FullyQualifiedName~ParallelContactBake"`). All pass.
- [ ] **Step 5: Commit** `perf(physics): cache the pose-applied world form of installed support geometry`.

### Task 3: Certified rejects before derivation (SC-5, SC-6)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintPolygon.cs` (`Measure`), `KhaozEngine.Physics.Bepu/SupportFootprintCollector.cs` (`ContactMember`, `ContactHeight`)
- Modify: `KhaozEngine.Physics/SupportQueryDiagnostics.cs` (counters `PolygonOperands`, `PrefilterRejects`)
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.Prefilter.cs`

**Interfaces:**
- Consumes: `SupportReferenceEvaluation`, `SupportFootprintEquivalence.AssertMatchesReference`, and the world form's cached normal sign and orientation from Task 2.

- [ ] **Step 1: Write the failing tests:**
  - `RejectedPolygonsBeginNoOperands`: warm `flat box` Find records `PolygonOperands` equal to the polygons that pass the bounds and column reject, which is fewer than `Polygons`.
  - `PrefilterRejectsOnlyCertifiedSeparation`: on `stairs box` and `hidden crate`, `PrefilterRejects > 0`, and every cost scene matches the reference.
  - `PrefilterKeepsBandBoundaryMembers`: a polygon placed at the contact band boundary, using represented values one step inside and one step outside, gives the same member verdict as the reference on both sides.
  - `PrefilterNeverHidesAnUnresolvedKernel`: a constructed near-degenerate member where the reference kernel returns `Unresolved` still returns `Unresolved`.
- [ ] **Step 2: Run them and see them fail** (`--filter "FullyQualifiedName~SupportFootprintTests.Prefilter"` style filter on the class).
- [ ] **Step 3: Implement.** In `Measure`, do the certified bounds and column reject before `BeginFootprintOperands`. The prefilter compares the proposal's contact capsule (segment, probe radius and contact band) against the polygon's certified bounds and returns `Negative` only when separation beyond the band is certified. Otherwise the kernel runs unchanged.
- [ ] **Step 4: Run the focused tests and the Task 2 equivalence tests.** All pass.
- [ ] **Step 5: Commit** `perf(physics): reject support polygons and members before deriving them`.

### Task 4: Topology-proved joins inside one convex leaf (SC-7)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintCollector.cs` (`Publish`), `KhaozEngine.Physics.Bepu/SupportNeighborhoodJoins.cs`
- Modify: `KhaozEngine.Physics/SupportQueryDiagnostics.cs` (counter `TopologyJoins`)
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.TopologyJoins.cs`

- [ ] **Step 1: Write the failing tests:**
  - `SameLeafSharedEdgeJoinsByTopology`: `stairs box` records `TopologyJoins > 0` and fewer geometric `JoinsProved` than the reference run, with a matching result.
  - `DifferentStaticsStillJoinGeometrically`: two separate box statics touching along an edge record `TopologyJoins == 0` for that pair and match the reference.
  - `MutualBelowStillCertifiedForTopologyJoins`: a leaf whose shared-edge faces fail mutual-below certification gives the same refusal or verdict as the reference.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** Faces of one installed convex leaf that share a captured local edge (`CapsuleFeaturePolyhedronEdge`) take their meeting from topology. The mutual-below certification and every cross-leaf or cross-static join stay geometric.
- [ ] **Step 4: Run the focused tests, the Task 2 equivalence tests and `SupportFootprintTests.JoinCost`.** All pass.
- [ ] **Step 5: Commit** `perf(physics): prove same-leaf support joins from captured topology`.

### Task 5: Interval-first curved kernels and their phase bracket (SC-8)

**Files:**
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintCurved.cs` (`Measure`, `Begin`), `KhaozEngine.Physics.Bepu/SupportFootprintCollector.Geometry.cs` (`CollectCurves`), `KhaozEngine.Physics.Bepu/InstalledSupportWorldForm.cs` (add the curved leaves' pose-only constants)
- Modify: `KhaozEngine.Physics/ContactAllocationDiagnostics.cs` (phase `SupportCurved`)
- Create: `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.CurvedInterval.cs`

- [ ] **Step 1: Write the failing tests:**
  - `CurvedRowsMatchReference`: `sphere`, `capsule` and `cylinder` cost rows match the reference cold and warm.
  - `CurvedSourceArithmeticRunsOnlyOnUnresolvedSigns`: on the three curved rows, the source-arithmetic fallback count is below the reference run's count. Add a counter if none exists.
  - `CurvedCollectionHasItsOwnPhase`: a phase recorder sees `SupportCurved` brackets on a sphere Find.
  - The existing SG-19 curved witness tests (`SupportFootprintTests.CurvedGuards`, `SupportFootprintCurvedProofOracle`) pass unchanged.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** Extend the Task 2 world form with each curved leaf's pose-only `Begin` constants, built once under the same publication rule, and read them from there. Evaluate admission and maxima in interval arithmetic, and fall back to the source-tracking arithmetic only when a sign is unresolved.
- [ ] **Step 4: Run the focused tests and the Task 2 equivalence tests.** All pass.
- [ ] **Step 5: Commit** `perf(physics): evaluate curved support interval-first`.

### Task 6: Measure, record and integrate

**Files:**
- Create: `docs/superpowers/plans/proofs/2026-10-17-support-cost-option-c.md`
- Modify: `docs/design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md` (status line and measured outcome), `CHANGELOG.md` (21.1.0 entry), `docs/INDEX.md` (spec and record lines)

- [ ] **Step 1: Record one local cost artifact** at the branch head with the SG-20 command `SupportGateCostTests` prints, once, through `build-slot --heavy`, and one `SupportCostAttributionTests.OneShotPhaseAttribution` run the same way.
- [ ] **Step 2: Write the cost record.** Per ordinary row: before (from `.superpowers/sdd/sg12-support-cost/research-report.md`) and after warm Find, substep and tick in M2 us against the SG-12 limit, phase shares, and the counters each task added. Mark one-shot values as single samples. List every ordinary row still over its limit with its dominant phase.
- [ ] **Step 3: Update the spec's status and expected outcome** with the measured values, the `CHANGELOG.md` 21.1.0 entry with one consumer-facing line on support query cost, and `docs/INDEX.md`.
- [ ] **Step 4: Full verification:** `build-slot --label sc-final --heavy -- dotnet build KhaozEngine.slnx -c Release`, then `build-slot --label sc-final --heavy -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`, then the dash, prose and file-size checks. All pass.
- [ ] **Step 5: Commit** `docs(support-cost): record the option C cost after the support optimizations`.
