# Support cost, SG-12 option C phase 2, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the remaining lossless support cost the second attribution found, correct the cost evidence, and settle the hidden crate descent lead, without changing any limit or any support result.

**Architecture:** Same proof shape as phase 1. `SupportReferenceEvaluation` bypasses every optimization and is the oracle, `SupportFootprintEquivalence` proves bit identity, and counters carry the deterministic evidence. New work: live-only joins on the support path, a look-ahead short-circuit, exact-coordinate planar certificates, a bounded mesh world form, a one-interval exact-repeat memo, a cheaper admission certificate for non-supporting faces, and three evidence corrections.

**Tech Stack:** C# / .NET 10, BepuPhysics, xUnit, `KhaozEngine.Physics.Bepu`, `KhaozEngine.Locomotion`, `KhaozEngine.Game.Tests`.

**Spec:** `docs/design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md`, phase 2 rulings SC-10 to SC-17, on top of SC-1 to SC-9 and `docs/design/CONTACT-CONTROLLER-SUPPORT-GATES-2026-10-11.md`.

## Global Constraints

- No SG-12 limit, ceiling, tolerance or assertion epsilon changes anywhere. SC-16 and SC-17 correct evidence, they do not widen it.
- Every optimized Find and substep stays bit-identical to the reference evaluator (SC-9): status, height, height error, raw feature provenance, owner binding, XZ, every support read in a substep, and MoveState.
- The public `IPhysicsSupportFootprint.QuerySupportFootprint` output is unchanged (SC-10).
- Warm Find allocates 0 managed bytes, storage stays bounded, no support result is cached across axes, ticks, bodies or reconciles (SG-15). The SC-15 memo lives for one lease interval only.
- Preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity.
- The ordinary suite asserts counters and allocation, never wall time (SG-20).
- Every build and test through `build-slot --label <task> -- <command>`, `--heavy` for wide filters and timing. One focused command at a time, no loops, no parallel test processes, no stress. Create `local-feed/` before restore.
- Do not change `SupportSegmentPolygon` without re-checking `SupportContactPrefilter`'s margin derivation, and do not change `SupportNeighborhoodCurved` sign-only mode without re-checking its two arguments.
- Shipped text carries no em or en dashes and no prose semicolons. Run `sh scripts/check-dashes.sh staged`, `sh scripts/check-prose.sh staged` and `sh scripts/check-file-size.sh staged` before each commit. Commit subjects `area(scope): summary`, no attribution.
- Engine version rides the staged 21.2.0. Only Task 9 touches `CHANGELOG.md`.

## Review Focus

1. A certification change that later reads a pair SC-10 skips. Equivalence over every cost scene must fail if a skipped join is read (Task 3, `LiveJoinsMatchReferenceOnEveryCostScene`).
2. A look-ahead outcome that the short-circuit decides differently from the sampled path, including descent and medium handoffs (Task 4, `LookAheadShortCircuitMatchesSampledPath`).
3. A planar certificate that accepts a near-horizontal face the kernel would refuse, at represented values on each side (Task 5, `ExactPlanarCertificateKeepsKernelVerdicts`).
4. Mesh world-form memory on a large terrain mesh (Task 6, `MeshWorldFormStaysWithinCapacity`).
5. A memo hit across a lease boundary or a changed request bit (Task 7, `ExactRepeatMemoNeverCrossesAnInterval`).

---

### Task 1: Evidence corrections (SC-16)

**Files:** `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportCostSampler.cs`, `SupportCostExecution.cs`, `SupportGateCostScenes.cs` or the oracle helper they use, `SupportGateCostTests.cs`, and their tests.

- [ ] Write failing tests: `SubstepGeometryJudgesItsOwnReads` (a substep whose own reads and support match the oracle passes `G` even when its parent tick's end placement fails), `LookAheadOracleModelsSg6Refusals` (the pushed seat look-ahead read whose probe starts on the ceiling slab top is expected `Refused`), `CostRunDiscardsALeadingRow` (the artifact records a discarded leading row before the first measured row and no verdict reads it).
- [ ] Implement. The leading row runs a full measured sample of a fixed cheap scene and is written to the artifact under a name the verdict ignores. Keep the cold, priming and warm protocol of SG-20 for every measured row.
- [ ] Run `SupportCostArtifactTests`, `SupportCostSamplerTests` and `SupportGateCostTests` through build-slot, all pass.
- [ ] Commit `test(support-cost): judge substeps on their own reads, model SG-6 refusals and discard a leading row`.

### Task 2: The hidden crate descent (SC-17, #1437)

**Files:** a new focused test in `KhaozEngine.Game.Tests/Locomotion/Contacts/`, plus whichever of `KhaozEngine.Locomotion/Contacts/GroundSeat.cs` and `GroundPlacement.cs` the evidence implicates.

- [ ] Write a geometry test on the hidden crate cost scene (and a minimal tilted-box variant) that traces one paced climb tick substep by substep and asserts the feet never go below the walkable top the body stood on inside its own footprint.
- [ ] If it fails, find the root cause (systematic debugging), fix it properly, and add the regression test. Preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity. Run the locomotion contacts suite.
- [ ] If it passes and the 0.0577 m drop is explained by correct geometry, write the explanation in the report and derive `paced` in `SupportCostExecution` from the climb budget (owed rise above `MaxStepClimbSpeed * dt`) instead of the scene name list, with a test that the stairs, pushed seat and crate rows are paced and a flat row is not.
- [ ] Commit `fix(locomotion): ...` or `test(support-cost): derive paced placement from the climb budget`, and close or update #1437 in the report (the orchestrator posts it).

### Task 3: Live-only joins on the support path (SC-10)

**Files:** `KhaozEngine.Physics.Bepu/SupportFootprintCollector.cs` (`Publish`), the raw footprint query entry, `KhaozEngine.Physics/SupportQueryDiagnostics.cs`, new `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.LiveJoins.cs`.

- [ ] Write failing tests: `LiveJoinsMatchReferenceOnEveryCostScene` (Find and substep equivalence on every cost row), `RawFootprintKeepsTheFullJoinMatrix` (the public raw query's members and join words equal the reference), and a counter test that the support path proves fewer joins on the hidden crate than the reference.
- [ ] Implement a support-path mode that builds incidence and joins only for pairs whose members both have `UpwardLower > 0`. The raw public query keeps full joins. Under `SupportReferenceEvaluation` everything is built.
- [ ] Run SupportFootprintTests and the support and parity filter, all pass.
- [ ] Commit `perf(physics): build only the joins support certification reads`.

### Task 4: Look-ahead short-circuit (SC-13)

**Files:** `KhaozEngine.Locomotion/Contacts/GroundSignals.cs` (`Resolve`), the SG-16 look-ahead test, new `KhaozEngine.Game.Tests/Locomotion/Contacts/LookAheadShortCircuitTests.cs`.

- [ ] Write failing tests: `LookAheadShortCircuitMatchesSampledPath` (for ascending, descending, flat, explicit, wade and swim handoff inputs on the cost scenes, MoveState from the short-circuit equals MoveState with the look-ahead forced to sample), `LookAheadSkipsSamplesWhenAscentIsDecided` (a Find counter drop on the stairs dry tick), and move the SG-16 "all three look-ahead samples" row to the pushed seat or hidden crate scene where the samples decide.
- [ ] Implement the short-circuit before sampling. Provide the forced-sample switch for the test only.
- [ ] Run the locomotion contacts suite and the support and parity filter.
- [ ] Commit `perf(locomotion): skip look-ahead samples once ascent is decided`.

### Task 5: Exact-coordinate planar certificates (SC-11)

**Files:** `KhaozEngine.Physics.Bepu/SupportPlanarProposal.cs`, `SupportJoinIncidence` (certificate), new `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.ExactPlanar.cs`.

- [ ] Write failing tests: `FlatHullTopTakesThePlanarFastPath` (a counter shows the fast path on the flat hull row), `ExactPlanarCertificateKeepsKernelVerdicts` (a sweep at represented values on each side of the face boundary and the near-horizontal tilt where the certificate stops applying, comparing with the reference), and cost-row equivalence.
- [ ] Implement exact-expansion orientation on double coordinates, keeping the certificate sufficient.
- [ ] Run SupportFootprintTests and the support and parity filter.
- [ ] Commit `perf(physics): certify planar support on exact coordinates`.

### Task 6: Mesh world form and coplanar-neighbour certificate (SC-12)

**Files:** `KhaozEngine.Physics.Bepu/SupportNeighborhoodMesh.cs`, a new mesh world-form type beside `InstalledSupportWorldForm`, `SupportFootprintCollector.cs`, new `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.MeshWorldForm.cs`.

- [ ] Write failing tests: `MeshWorldFormMatchesReferenceOnEveryCostScene`, `WarmMeshFindCapturesNoTriangles` (a counter), `MeshWorldFormStaysWithinCapacity` (a large generated mesh touched across many queries never exceeds the declared per-static and global capacity, and eviction keeps results equal to the reference), `ChangedMeshPoseRebuildsTheForm`, `CoplanarNeighbourCertificateMatchesKernel` (boundary sweep as Task 5).
- [ ] Implement with the SC-4 publication rule, exact pose bits and an explicit capacity. Name the capacity constants and justify them in a comment.
- [ ] Run SupportFootprintTests and the support and parity filter.
- [ ] Commit `perf(physics): cache mesh support triangles in a bounded world form`.

### Task 7: One-interval exact-repeat memo (SC-15)

**Files:** `KhaozEngine.Locomotion/Contacts/FootSupport.cs` or the lease-scoped context that owns per-interval state, new `KhaozEngine.Game.Tests/Locomotion/Contacts/ExactRepeatMemoTests.cs`.

- [ ] Write failing tests: `ExactRepeatInsideOneIntervalHitsTheMemo` (the pushed seat dry tick's repeated query is served once), `ExactRepeatMemoNeverCrossesAnInterval` (a new lease, a changed request bit or a changed view identity misses), and tick equivalence on every cost row.
- [ ] Implement keyed by every request bit and the view identity, cleared with the lease. Warm allocation stays 0.
- [ ] Run the locomotion contacts suite and the support and parity filter.
- [ ] Commit `perf(locomotion): memoize an exact repeat support request within one interval`.

### Task 8: Admission certificate for non-supporting faces (SC-14)

**Files:** the extrema admission in `KhaozEngine.Physics.Bepu` (re-locate from `SupportFootprintPolygon` and the extrema phase), new `KhaozEngine.Game.Tests/Physics/SupportFootprintTests.AdmissionCertificate.cs`.

- [ ] Write failing tests: cost-row equivalence, `AdmissionCertificateKeepsUnresolvedReachable` (a constructed case where the kernel returns `Unresolved` still does), and a counter test on the stairs Find.
- [ ] Implement the vertical-face segment-against-disc certificate, kernel otherwise.
- [ ] Run SupportFootprintTests and the support and parity filter.
- [ ] Commit `perf(physics): admit non-supporting faces with a certified segment test`.

### Task 9: Measure, record and integrate

**Files:** `docs/superpowers/plans/proofs/2026-10-17-support-cost-option-c-phase2.md`, the spec status lines, `CHANGELOG.md` (append to the staged 21.2.0 entry), `docs/INDEX.md`.

- [ ] Record one local cost artifact and one attribution run at the branch head through `build-slot --heavy`, noting that local samples run at utility QoS.
- [ ] Write the cost record: per ordinary row the phase 1 hosted median, the local before and after, the counters each task changed, and which rows lossless work still cannot reach.
- [ ] Update the spec status, append one consumer-facing line to the 21.2.0 CHANGELOG entry, add the INDEX row.
- [ ] Full verification: Release build, `dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"` once through `build-slot --heavy`, and the dash, prose, file-size and doc-version checks. All pass.
- [ ] Commit `docs(support-cost): record the phase 2 cost`.
