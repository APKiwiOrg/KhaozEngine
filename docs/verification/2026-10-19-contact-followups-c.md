# Contact follow-ups C verification

## Task 1 baseline provenance

Production and support oracles were unchanged at `28273267c28785f566f4c8c50c84e008cd3e3df1`
when both freezes ran. The anchor capture compared all 1,952 original support-equivalence
rows and passed. Capture does not enable answer replacement or suppress a mismatch.
Every serialized float is its signed 32-bit pattern. Scene digests cover the original shape,
child, vertex, index and pose bits in construction order. The PRNG seeds and draw order remain.
The historical query generator is tested separately from the represented support oracle.

- Query fixture SHA-256 `1a77126404f2b33af252074ac40176a4d3ba462147f77a861b9de1169254b4b9`.
- Exact-install fixture SHA-256 ecda8a376cadedf630c0e39d48e0a7914ba3be4c1b570b1e84820b244b917e75.
- Exact-install rows 24. Identity, exact Y half-turn and cyclic installs, both zero signs,
  boxes, hull-centroid wrappers, coincident compound children and meshes. Complete footprint and
  neighborhood result values, provenance, witnesses, errors and join words are serialized as raw bits.
  Footprint reference capture is also compared bit for bit. Reference metadata is authenticated
  against the actual query world and lease before serialization.

The original zero-origin compound initial tick was the first red regression.
`CapturedStairExplicitSweepTests.CapturedStairZeroOriginInitialTickUsesRepresentedSupport`
compiled and failed with the following assertion, before any production or oracle edit.

```text
Assert.Equal() Failure: Values differ
Expected: Advanced
Actual:   Blocked
```

The refused request's axis is `(-0.14999999f, 0)`, radius `0.15f`, band
`[0.09999996423721313, 0.9000000655651093]`, slope cosine `0.66913056f`, contact band
`0.0001f`. The axis proposal starts at `(-0.14999999f, 0.91600007f, 0)`, the foot
proposal at `(-0.14999999f, 1.0560001f, 0)`. Both have identity orientation and
`0.80200005f` distance. These are round-trip operands from the actual request, not reconstructed
rounded diagnostics. The original test probe sizes and tuning remain unchanged.

## Audited source dispositions

The numbered table below follows the 51-site audit in research-1494 sections 1 to 4.
Task 1 covers the planar doors. Task 2 records sites 35 to 42 and the curved paths of sites 43 and 44.
Curved line references are at the Task 2 commit. Task 3 closes every site with the acceptance key below.

| Site | Audited source | Disposition | Evidence and retained obligation | Task 3 acceptance |
| --- | --- | --- | --- | --- |
| 1 | `InstalledPoseOperator.cs:64`, `:93`, `:112`, `:141` | changed | Explicit support door directly certifies represented columns. Legacy TryCreate, RealCoefficients and union remain. Scalar and SIMD capture is shared. | G, X, A |
| 2 | `InstalledPoseOperations.cs:55` | changed | Separate support admission feeds the same rounded composition enclosure and actual SIMD continuation. | G, X, A |
| 3 | `RepresentedGeometryTransforms.cs:40`, `:77` | legacy-retained | PosePoint stays in legacy wrappers. Planar support and its query/proposal domain gates no longer call it. | G, X, A |
| 4 | `CapsuleFeatureGeometry.cs:65`, `:147`, `:169` | changed | Operator-accepting capture and transform cores retain coordinate checks and expression order. Legacy wrappers retain correspondence. | G, X, A |
| 5 | `CapsuleFeaturePolyhedra.cs:48`, `:55`, `:169` | derived-unchanged | Vertices, edges, winding and cross normals derive from the support operator before validation. | G, X, A |
| 6 | `SupportNeighborhoodPolyhedra.cs:20`, `:63`, `:106` | changed | Both planar capture doors admit represented installed/composed leaf poses and pass their operator to the shared core. | G, X, A |
| 7 | `InstalledSupportGeometry.cs:39`, `:119` | changed | Cached local topology materializes vertices and normals from the represented operator. Compound composition uses support admission. | G, X, A |
| 8 | `InstalledSupportGeometryCache.cs:89`, `:100` | changed | Identity local-topology capture and child-pose admission use the support door. Cache keys and publication are unchanged. | G, X, A |
| 9 | `SupportNeighborhoodMesh.cs:32`, `:49`, `:80`, `:132`, `:176` | changed | Both mesh query doors and triangle materialization use the represented map. Local bounds use its actual inverse. | G, X, A |
| 10 | `InstalledSupportMeshWorldForm.cs:84`, `:148`, `:181` | derived-unchanged | Immutable triangle forms receive the new represented operator from traversal. Pose-bit keys, triangle ids and publication remain. | G, X, A |
| 11 | `SupportNeighborhoodPolygon.cs:112`, `:162`, `:217` | changed | Point-valued source map. BeginSource rejects non-point or mismatched columns before extracting exact binary32 entries. | G, X, A |
| 12 | `InstalledSupportWorldForm.cs:31`, `:104`, `:153` | derived-unchanged | World forms snapshot one represented leaf and its derived polygons and meetings. No source is patched after validation. | G, X, A |
| 13 | `InstalledSupportMemberFacts.cs:17` | derived-unchanged | Cached member facts are rebuilt from the represented polygons in the original operation order. | G, X, A |
| 14 | `SupportSourceMapComparer.cs:11` | changed | Source sharing hashes and compares all twelve binary32 patterns, including signed zeros. | S, G, X |
| 15 | `SupportSourcePolynomial.cs:46`, `:114`, `:130` | derived-unchanged | Production planar callers install nine constants. Generic box-coefficient support remains for synthetic arithmetic. | S, G, X |
| 16 | `SupportSourceCoefficient.cs:31`, `:74`, `:89` | derived-unchanged | Exact expansion arithmetic and its 128-part refusal remain. | S, G, X |
| 17 | `SupportFootprintSourcePredicates.cs:19`, `:52`, `:84`, `:139`, `:145`, `:165`, `:191` | changed | Nine exact constants and twelve-bit source reuse. Local subtraction, translation/axis subtraction, lifecycle and all formulas remain. | S, G, X |
| 18 | `SupportFootprintSourcePredicates.ProjectedSeparation.cs:10` | derived-unchanged | Same projected-separation expression and strict exclusion rule. Its source entries are now exact constants. | S, G, X |
| 19 | `SupportFootprintEdgeOperands.cs:26`, `:36`, `:63` | derived-unchanged | Edge operands and shared lengths derive from the represented polygon. Request reuse is unchanged. | S, G, X |
| 20 | `SupportFootprintPolygon.cs:32`, `:141`, `:267`, `:374` | derived-unchanged | Admission, extrema and closed strata consume the represented polygon/source. No formulas or bands changed. | S, G, X |
| 21 | `SupportFootprintPolygon.StairStratum.cs:12`, `:57` | derived-unchanged | Rectangle exactness remains literal. A trigonometric quarter-turn is not snapped. | S, G, X |
| 22 | `SupportFootprintProposalGeometry.cs:13`, `:24`, `:71`, `:117` | derived-unchanged | Proposal contacts use the same represented source. Exhaustive order and attained guards remain. | S, G, X |
| 23 | `SupportPlanarProposal.cs:13`, `:32`, `:52` | changed | Identity qualification reads Vector3 components directly. Exactness, lattice and range protections remain. | S, G, X |
| 24 | `SupportPlanarNeighbour.cs:10` | derived-unchanged | Sufficient contact certificate retains its conditions and receives represented derived operands. | S, G, X |
| 25 | `SupportRectangleContact.cs:34`, `:76`, `:117` | derived-unchanged | Rectangle and lowest shortcuts retain their exactness and finite-domain checks. | S, G, X |
| 26 | `SupportContactPrefilter.cs:46`, `:58`, `:92`, `:143` | derived-unchanged | Contact pruning retains arithmetic-error and chord requirements. Exact inputs do not erase arithmetic error. | S, G, X |
| 27 | `SupportVerticalAdmission.cs:58`, `:85` | derived-unchanged | Vertical admission keeps its separation margins and exhaustive fallback. | S, G, X |
| 28 | `SupportProjectedPolygonSeparation.cs:10`, `:54` | derived-unchanged | Projected pruning still requires strict source-certified separation. Equality cannot exclude a polygon. | S, G, X |
| 29 | `SupportNeighborhoodPolygon.cs:259`, `:271` | derived-unchanged | Closest-pair strata receive represented vertices, edges and cross normals with the same error arithmetic. | S, G, X |
| 30 | `SupportSegmentPolygon.Footprint.cs:8` | derived-unchanged | Independent proposal approach/front guards and contact band remain. | S, G, X |
| 31 | `SupportSourceIncidence.cs:24`, `:70` | derived-unchanged | Incidence uses the twelve-bit comparer. Every raw polygon link remains separate. | J, G |
| 32 | `SupportJoinIncidence.cs:13`, `:71` | derived-unchanged | Same-plane and common-vertex certificates retain their bounds and raw rows. | J, G |
| 33 | `SupportMeetingDomain.cs:15`, `:34` | derived-unchanged | Meeting denominator and rate domains remain. | J, G |
| 34 | `SupportNeighborhoodJoins.cs:21`, `:39`, `:66`, `:80` | derived-unchanged | Whole joins and mutual-below tests remain. Equal source maps never replace raw identity. | J, G |
| 35 | `SupportNeighborhoodCurved.cs:22`, `:49`, `:57`, `:60`, `:72`, `:104`, `:116`, `:354` | changed | Compound root, child pose and composition gates use the support door (Task 1 fix round 1). Both Leaf doors (`:72`, `:104`) now admit through TryCreateSupport. `SourceColumn` (`:22`) is the represented binary32 Y column, extracted only from exact, bit-matching represented entries. Axis (`:116`) is the outward normalization of that same point, and Across derives from Axis, so the tangent kernel (`:354`) and every footprint stratum read one column. Radius, half length, part ids and the circular primitive are unchanged. | C, G, X |
| 36 | `SupportCurvedSourceArithmetic.cs:37`, `:197`, `:222` | changed | `Reset(Vector3)` installs the column as exact constants. The Source operation, `_column` interval storage, source-polynomial coefficients and their compile branch are removed. The node graph, denominator signs, radical elimination, 4,096-node and 54-rent compile ceilings, degree-eight bound and Checkpoint/Restore are unchanged. Constant polynomials still decide undecided interval signs exactly. | C, G, X |
| 37 | `SupportCurvedRootRegions.cs:42`, `:78`, `:216` | derived-unchanged | Root isolation, endpoint exact signs, 1,024 live and 12 terminal cells are unchanged. Irrational coefficients still arrive as enclosures, so whole-region certificates remain necessary. Only the class comment now names a coefficient enclosure instead of a source family. | C, G, X |
| 38 | `SupportFootprintCurved.cs:42`, `:52`, `SupportFootprintCurved.Admission.cs:29`, `SupportFootprintCurved.Proposal.cs:28` | changed (source only) | Begin resets from the represented column. `_axisSign` reads the binary32 Y sign. Spherical, upright and horizontal strata, admission and lowest proposal contacts use the same constants. Finite-end clipping and inclusive equalities are unchanged. A represented column with exact zero Y now takes the closed-form horizontal stratum where the old box was tilted. | C, G, X |
| 39 | `SupportTiltedCurved.cs:13`, `:87` | derived-unchanged | Tilted coordination consumes the geometry owner below. Full-part admission and atomic refusal remain. | C, G, X |
| 40 | `SupportTiltedCurvedGeometry.cs:48` and partials listed below | derived-unchanged (source switched through site 36) | Begin resets from the represented column and keeps algebraic normalization, the axial and radial frame and every guarded boundary, stationary, contact and surface equation. The weak-sign clipping in NormalizedYBounds stays, and it is now exact for a signed zero column. | C, G, X |
| 41 | `SupportCurvedSurfaceAdmission.cs:24` | derived-unchanged | All coverage cells, maxima, caps and atomic refusal remain even where the point column resolves more signs. | C, G, X |
| 42 | `GeometryVectorOperations.cs:42`, `:77`, `SupportFootprintPublication.cs:8` | derived-unchanged | Normalization and binary32 publication still enclose arithmetic and conversion error. An irrational unit axis from a point column publishes a nonzero error, proved by RepresentedCurvedRadicalsRemainGuardedAfterPointSources. | C, G, X |
| 43 | `SupportFootprintCollector.cs:103`, `:135`, `:202`, `:277`, `SupportFootprintCollector.Geometry.cs:12`, `SupportFootprintCollector.Relevance.cs:21` | derived-unchanged | Cached, fallback and reference planar materialization now share represented capture. Collector formulas and build-before-copy remain. Curved leaves reach the support door through `InstalledSupportGeometry.Materialize` and its cache identity leaf (`InstalledSupportGeometryCache.cs:111`), with no dual admission on either public query. | A, E, G, X |
| 44 | `SupportNeighborhoodCollector.cs:118`, `:162`, `:194` | derived-unchanged | Uncached planar neighborhoods receive the same represented source. Curved capture at `:131` uses the represented column and support door. | A, E, G, X |
| 45 | `BepuPhysicsWorld.SupportFootprint.cs:13`, `:66`, `:114` | changed | Proposal pose-domain admission uses the support factory. Raw sweeps, lease, selections, versions and publication remain. | A, E, G, X |
| 46 | `BepuPhysicsWorld.SupportNeighborhood.cs:30`, `:78` | changed | Probe pose-domain admission uses the support factory. Static-only selection and query limits remain. | A, E, G, X |
| 47 | `KhaozEngine.Locomotion/Contacts/SupportCertification.Footprint.cs:27`, `:52`, `:79`, `:118` | derived-unchanged | Locomotion footprint certification receives represented published bounds. Provenance, steep suppression and direct joins remain. | A, E, G, X |
| 48 | `KhaozEngine.Locomotion/Contacts/SupportCertification.FootprintScope.cs:9` | derived-unchanged | Scope containment obligations are unchanged. | A, E, G, X |
| 49 | `KhaozEngine.Locomotion/Contacts/FootSupport.cs:101`, `:180`, `:214`, `:273` | derived-unchanged | FootSupport selection, reach, invalid-start guards and 1 mm rules are unchanged. | A, E, G, X |
| 50 | `KhaozEngine.Locomotion/Contacts/SupportContinuity.cs:80`, `:175`, `:204` | derived-unchanged | Raw continuity identities, direct joins and lease lifetime remain unchanged. | J, E |
| 51 | `KhaozEngine.Locomotion/Contacts/SupportRequestMemo.cs:80`, `:94`, `:98` | derived-unchanged | Exact query memo keys, reference bypass and epoch invalidation remain unchanged. | M, E |


## Task 1 proof coverage

The focused Release selection covers the unchanged original tick, independent real/represented
discriminant disagreement, scalar/all-lane bit correspondence, signed zeros, norm endpoints,
range rejection, positive determinant, Gram/inverse enclosures and installed compound/hull-wrapper
composition. It also covers old-enclosure ambiguity, represented closed-disc equality and adjacent
radii, unused member/join tails, source-map rejection/constants/identity, distinct coincident raw
statics, mesh first form/cache hit/pose miss/reference and independently cross-derived normals.
The original feature/domain controls and both frozen fixtures are included.

The 24 baseline exact-install rows remain byte-identical. Frozen query questions remain identical
across all serialized integer patterns and scene digests. Historical query math lives in
`SupportEquivalenceQueryAnchors`, separate from the represented planar expected geometry.
The legacy feature/sweeps factory retains its original dual union and correspondence wrappers.
No polygon-extrema, proposal, contact, join, rectangle or pruning formula changed.

Synthetic support fixtures in SourceReuse, SourceIncidence, PlanarProposal and the main
SupportFootprintTests partial use the support factory. Their assertions are unchanged.
FC-R3 changes only obsolete interval-member accesses in BandHeights and SourceCorrections.
Every old assertion, expected value and test name remains for Task 3 reconciliation under FC-R1.


The focused old-contract selection reported 30 passing rows and 15 FC-R1 failures. These are the
six captured-stair boundary rows, the old box-uncertainty expectations in Source, BandHeights and
SourceCorrections, the old mesh-sliver skip and the rotated equivalence digest comparison.
Of 1,952 golden rows, 735 changed under identical frozen questions. Zero identity mesh, fan or
legacy feature rows changed. The complete test names and row parameters are in the Task 1 report.
Task 3 owns reconciliation. No old expectation was skipped, removed or rewritten.

## Task 2 curved source

`SupportCurvedLeaf.SourceColumn` is the represented Y column of the actual installed or composed leaf
pose, bit for bit, and Axis encloses its exact normalization. Both capture doors, cached, reference and
compound child order return the same leaf. The curved arithmetic owner installs that column as constants.
Sphere, capsule and cylinder statics and curved compound children reach zero dual admissions across
both public support queries. The legacy feature and sweeps factory keeps its dual contract.

Changed curved expectations, each against the represented exact or closed-form oracle:

- 28 lying-log end-seam rows that expected refusal now certify Complete. Every one was a box artifact.
- Synthetic box sources in TiltedRegions and LyingLogs became per-column point sources at the old box
  endpoints. Former capacity refusals across a box are replaced by per-column certificates. A genuine
  irrational double root and a genuine compile-ceiling failure keep the bounded refusal contracts.
- The quarter-turn boundary pose `(0.6, 0, 0.3741657, 0.70710677)` has an exactly horizontal represented
  column. It certifies in the closed-form stratum for all eight former tilted rows.
- The crest-region test now bounds height with the rational tilted certificate. Its binary64 locator plane
  misses the exact attained point by about 7e-9 on a 6e-8 tilt.
- Off-crest rows of the tiny-tilt side test keep exact witness, normal and maximum checks. The binary64
  per-point sampler cannot resolve a 1e-8 tilt there, so only the crest rows keep that sampler check.
- The proof and neighborhood oracles read the represented column of the actual composed leaf pose.
  Historical anchor callbacks are unchanged.

No tolerance, band, limit, storage bound or fallback-count assertion changed. No timing conclusion follows.

### Fix round 1 exact curved baseline

Ruling FC-R5 closes the CC-10 raw comparison gap. `ExactCurvedInstallControlsMatchBaseline` freezes
the complete public footprint and neighborhood outputs, using the existing raw-bit serializer and
unchanged result types. It never serializes `SupportCurvedLeaf` or its changed source-column fields.
The test includes raw static and element ownership, ordered witness roles, positions, normals, heights,
errors, request XZ, both proposal verdicts, all published join words and floating zero signs. Cached
and reference footprints must also match bit for bit.

- Baseline commit `9e63657a70b2643059ffe09e6acc1b10a423fa78`, unchanged production with Task 1's freeze harness.
- Detached scratch worktree `.worktrees/fu-c-curved-freeze` captured the final source once, then was removed with
  `git worktree remove --force` after copying the fixture and comparing the test source byte for byte.
- Fixture `KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/support-exact-curved-install-controls.txt`.
- Rows 72, comprising sphere, capsule, cylinder and a translated second compound capsule child, each
  at identity, `(0,1,0,0)` and cyclic `(0.5f,0.5f,0.5f,0.5f)`, with both planar-control zero signs and
  three XZ queries. Centre, offset and outside-axis overlap queries exercise both roles and proposal
  Contact and Miss verdicts. Compound output retains raw element id 256.
- Fixture SHA-256 `c860b3fd9169649a06cb9de81a2bf92fe9cfc11af10bc66543796b3cf178ded9`.
- Identical test-source SHA-256 `ccffcc571d2a3ca81c47b3763356c3685060db3bd5f6974f88f1bed8a5b39341`.
- Capture-disabled comparison at lane HEAD `d0a65bf36` passed all 72 rows without any production fix.
  The following `ExactInstallControlsMatchBaseline|RepresentedCurved` selection passed 28 tests.
- The original 24-row planar fixture remains byte-identical, SHA-256
  `ecda8a376cadedf630c0e39d48e0a7914ba3be4c1b570b1e84820b244b917e75`.

The final capture, comparison and focused check ran synchronously through build-slot, once each, with exit 0.
An initial sequence also passed, then the staged whitespace guard found trailing separators on empty
neighborhood rows. The serializer now trims only those separators. Its corrected source was captured
again at unchanged production `9e63657a7`, never at lane HEAD. Both captures have identical data apart
from the trailing separators. There was no expected-row edit, loop, timing run or full suite. Production and
legacy tests are unchanged, so the accepted 15 Task 1 and 9 Task 2 FC-R1 failures remain assigned to
Task 3. They were not rerun in this fix round.

## Task 3 acceptance key

Every source site above now has an explicit Task 3 proof disposition. No production source changed in
Task 3. The retained formulas, caps, direct raw identity and arithmetic guards are exercised through
the changed represented source rather than replaced.

- A: `CapturedStairExplicitSweepTests`. All original installations and traversals remain. The two
  initial and four traversal boundary rows use ordinary acceptance, 240 ticks and the exact 3D 1 mm
  arrival ball. Initial rows retain independent raw and certified shell controls. Traversals retain
  near-tangency evidence. `CapturedStairEdgeOracle.AssertRepresentedDisagreement` proves real D < 0,
  represented D > 0 and the production Positive edge sign on the same installed raw face and edge.
  The literal yaw independently pins c `33800000`, s `3F7FFFFF`, negative s `BF7FFFFF`.
- S: `InstalledRepresentedCoefficientBoundaryIsExactlyOnTheDisc` and
  `RepresentedVertexDiscKeepsClosedBoundaryAndAdjacentRadii` prove the exact zero and adjacent signs.
  `RepresentedSourceContactBandMatchesIndependentRationalVerdict` preserves the old legacy-box
  question but derives the verdict from represented rational geometry. The independent
  `UncertainContactBandSignRefusesBothSpans` retains genuine unresolved-height sentinel refusal.
  Walls are selected by exact zero upward source orientation. Nine degree-zero source constants,
  cold scratch bounds and zero warm allocation replace the old four/nine variable assumptions.
  Existing exact fallback tests remain. The sliver's represented doubled area is exactly
  `4 * binary32(1e-9f) * (X.X * Z.Z - X.Z * Z.X)`, positive under its installed yaw. Raw triangle 1
  remains. Exact degeneracy still skips, without a small-normal threshold.
- J: `RepresentedCoincidenceKeepsRawKeysAndIntervalAuthentication` retains both admitted top faces
  and all raw join words for separate statics and two compound children. Unequal statics establish
  separate-static identity, unequal element ids establish child identity. Each selected direct row
  contains exactly its raw neighbour. The existing `JoinedEdgeNeedsTheDirectSelectedPair` fixtures
  retain the non-transitive negative proof. Geometry equality does not revive interval evidence.
- C: `LyingLogSupportsItsTopLine` reads the actual installed composed centre through the independent
  represented curved oracle. The eight former horizontal source-box rows retain their exact poses
  and queries, assert represented Y is exactly zero and use the closed-form role proof. Finite ends,
  both role orders, curved radical and root-storage refusals remain in the acceptance selection.
- X: `RepresentedExactInstallsKeepBaselineBits` reads the frozen 24 planar and 72 curved rows. It
  compares status, roles, witness/normal, height/error, XZ, raw owners, joins and both zero signs.
  Cold, warm, interleaved and rebuilt public queries match reference results bit for bit. Warm
  allocation is zero. Existing nested and concurrent fixtures remain in the selection.
- E: `RepresentedRotatedTreadStillRejectsIndependentRoof` retains an accepted quarter-turn tread
  control, then checks both original raw proposals report InvalidStart under the independent roof.
  Find refuses without lower-floor fallback. The selection also retains hidden support, ring-only
  context, 256/257 capacity, 512 witness slots, sentinel tails, endpoint slopes/bands and invalid starts.
- M: The coincidence regression checks exact-repeat memo hits in the original lease and misses for
  changed signed-zero query bits, scope, analytic fact, view, exclusion, reacquisition, mutation,
  rebase and a recycled Bepu handle. The public static key stays different after handle reuse.
  Expired reads reject, and old arena handles reject after interval closure. Existing memo, direct
  continuity and leased-query fixtures retain their full authentication matrix.
- G: The generated per-row independent proof and the frozen bit comparison are described below.

## Task 3 frozen golden fields and represented derivation

FC-R7 field baseline commit `9e63657a70b2643059ffe09e6acc1b10a423fa78` has unchanged production.
A detached scratch worktree contained the byte-identical test-only field serializer, historical
question generator and installed geometry oracle. The latter two supply the same historical scene
and frozen questions, never a production answer. Only these test sources differed from baseline.
The single capture asserted every serialized row re-hashed to its original `support-equivalence.txt`
answer before writing the field baseline. No baseline was captured at HEAD. All three source `cmp`
checks exited 0 before scratch removal.

- Baseline `Locomotion/Contacts/Fixtures/support-equivalence-fields.jsonl`, 1,952 rows, 14,247,486 bytes.
- Baseline SHA-256 `67da85ebb30678c4168299a33115385c70546ca5b4a7a726b19749102d468fba`.
- Serializer SHA-256 `ff87db34457fc9bb41af2648d81e5c64fedbe22111f751ba29bdd55ce1781679`.
- The file-size guard applies to C# structure. This data fixture passes without a baseline change.
- Original anchor and exact-install control files are unchanged. Their hashes above still apply.

Exact reproduction, from the detached scratch worktree after copying the three byte-identical test
sources named above and creating `local-feed`:

```bash
env -u KHAOZ_SUPPORT_COST_ARTIFACT -u KHAOZENGINE_SUPPORT_EQUIVALENCE_CAPTURE -u KHAOZENGINE_SUPPORT_CONTROL_CAPTURE -u KHAOZENGINE_SUPPORT_CURVED_CONTROL_CAPTURE -u KHAOZENGINE_SUPPORT_ANCHOR_CAPTURE KHAOZENGINE_SUPPORT_FIELD_BASELINE_CAPTURE=KhaozEngine.Game.Tests/Locomotion/Contacts/Fixtures/support-equivalence-fields.jsonl build-slot --heavy --label fu-c-golden-freeze -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~FrozenEquivalenceFieldsRehashTheOriginalDigests'
```

`RepresentedGoldenChangesHaveIndependentPerRowDerivations` compares old and current raw field
patterns under the exact frozen question and scene digest. Its expected geometry comes from
`InstalledPoseOracle.Represented`, `SupportNeighborhoodOracle`, `SupportFootprintExactOracle` and
`SupportFootprintCurvedProofOracle`, not production predicates, cached results or the field baseline.
It checks independently reconstructed membership, raw identities, all joins, geometric witness and
normal errors, both slope classifications and rational contribution-height containment. Witness and
normal assertions use the published errors without an extra epsilon. The selected contribution
normal must come from an independently minimal joined plane.

The test-generated [per-row table](2026-10-19-contact-followups-c-golden-rows.md) records every old/new
digest, unchanged scene and frozen-question digests, changed field classes and independent derivation. Each baseline row
also holds the complete frozen question bits. Changed rows are 605 poly, 130 rotated mesh and 297
curved, totaling 1,032. The other 920 answers are unchanged. No identity mesh, fan or feature row
changed. There are zero underivable rows. Individual reviewed digest substitutions were applied from
that table without enabling the digest capture variable.

| Changed field class | Rows |
| --- | --- |
| Enclosure | 1,032 |
| Height | 561 |
| Normal | 693 |
| Witness | 537 |
| Eligibility, joins, raw identity | 0 |

Counts overlap because a row can change more than one class. Removed transform ambiguity narrows
numeric enclosures and can change the rounded witness/normal and derived height. It does not change
any recorded member, join or eligibility decision in these frozen questions. Exact fallback coverage
for genuine arithmetic and capacity uncertainty remains.

### Additional acceptance-selection reconciliations

The first complete Task 3 selection exposed seven older expectations beyond the inherited 24. All
are test-side transform-contract assumptions, not production failures. Original inputs remain.

- Three `PitchedPlateDiscardsOutsideExtremumBeforePlaneHeight` rows now compare the represented
  rational orientation and exact normal enclosure instead of demanding an interval straddling zero.
  The outside-edge radical proof remains. A resolved candidate plane encloses the independent
  rational/root derivation. An unresolved one must have a genuinely unresolved arithmetic divisor.
- `PitchedPlateSeparatesOutsideFaceBeforeOrientation` compares the represented rational orientation.
  Its strict projected-separation proof and complete-query control remain unchanged.
- `ProposalInteriorKeepsEveryDecidedFastEdge(true)` now expects the exact Zero fast edge and zero
  fallback calls. The real exact-fallback strata tests remain in the selection.
- `InstalledLocalGeometryOwnsArraysAndMatchesTheUncachedInstalledOperator` now explicitly supplies
  the represented support factory to its uncached control with null scratch, preserving the original
  independently owned arrays. Its private-array ownership assertions and bit-exact comparisons remain.
  An intermediate scratch-backed version corrupted read-only shared box topology when this test
  deliberately mutated the returned faces. The failing broad run exposed this test harness mistake,
  which was corrected at the caller without changing production.
- `SlopeAdjustedNormalIsNeverShared` becomes
  `RepresentedHorizontalNormalKeepsEverySlopeQueryIndependent`. This original horizontal yawed
  face now normalizes to exact `(0,1,0)`, so no transform-box slope correction exists. The test pins
  that exact normal and retains per-slope shared-fact hits and reference equality.

### Task 3 verification result

The required `fu-c-acceptance-green` selection exited 0 with 992 passed, zero failed and zero skipped.
It includes every inherited reconciliation, the generated independent golden derivations, exact
controls and all retained safety/lifetime fixtures. The capture-disabled frozen-field baseline reader
also exited 0 with one passed. Both test commands built Release successfully with no compiler warnings.
All original six stair boundary rows pass ordinary Complete/Advanced acceptance and arrival.
No CLI test or build remains running. Full solution build, full Game.Tests and full format verification
remain Task 7 work. No timing or load evidence was collected.

## Task 4 cumulative curved certification work

CC-6, CC-7 and acceptance F use one `SupportCurvedWork` per collector scratch. The fixed
1,048,576-event ceiling is 256 existing raw-member slots times 4,096 existing source-graph
slots. It is a pooled allowance, independent of the existing graph, compile and storage limits.
No tolerance, band, arrival rule or storage limit changed.

`SupportFootprintCollector.Begin` begins the account once for the complete request. Both arithmetic
families, tilted geometry, roots and surface admission share it. Source Reset, PolynomialSign,
checkpoint Restore, later leaves/parts, both proposal contexts and the below-band pass cannot refund
work. New, Sign, recursive Split and Compile, root cells, Refine iterations and admission cells charge
before their event, including predicate and graph cache hits. A denied event latches WorkLimit and
later arithmetic reads and cached geometry shortcuts cannot succeed. Publish checks the owner before
either caller span is written. Direct footprint callers have independent owners, and memo hits never
enter this backend path.

The owner snapshot keeps FootprintStatus Unresolved. Only the completed backend query observation
sets it to the actual returned status, before returning its scratch. The opt-in thread-local observer
is captured and restored by ThreadState. Fresh clears it, Reset retains it like the Find observer,
and disabled diagnostics do not affect enforcement or accounting. Nested callback queries borrow
independent scratch. Existing nested form-build and concurrent-reader fixtures also observe it.

### Behavioral red and focused verification

The inert owner compiled, then all six initial work tests failed behaviorally. The eighth event ran,
Sign returned Positive after exhaustion, root storage high water and both storage reasons were absent,
invalid limits were accepted, and the limit-32 collector returned Complete instead of Unsupported.
The historical 3,301,460-event query was not run.

The final required `fu-c-work-green` selection exited 0 with 709 passed, zero failed and zero skipped.
It builds the referenced Release projects without compiler warnings. This includes the complete
SupportFootprintTests and SupportNeighborhoodEquivalenceTests selections, 256/257 member atomicity,
exact represented controls, and the new failure and observer proofs. The final command adds only the
console detailed logger to retain counter evidence. All builds/tests ran serially through build-slot.
No full suite, timing, load, capture, cost artifact or hosted workflow ran.

### Fixed certified corpus

`CertifiedCurvedStrataFitTheCumulativeQueryCeiling` checks 42 fixed rows, each optimized and reference.
The rows cover sphere/upright, exact-horizontal, finite ends, compounds, tilted side/cap/round,
slope-boundary neighbours, both proposal contacts, a real below-band second pass, and the maximum
256 admitted raw members with 512 witnesses. Every observation is Complete, has Failure None,
Consumed at most 1,048,576, and a category sum equal to Consumed. Public members, witnesses, verdicts
and joins match reference output. Totals below include both modes, 84 observations.

| Event | Accepted events |
| --- | --- |
| New | 109,584 |
| Sign | 20,832 |
| Split | 0 |
| Compile | 116 |
| RootCell | 944 |
| Refine | 516 |
| AdmissionCell | 106 |
| Total | 132,098 |

The maximum row is `maximum-raw`, consuming 48,896 in each mode. Its categories are New 40,960,
Sign 7,936, and zero in the other five categories. The exact-horizontal and represented corpus
requires no Split events. `CurvedWorkPredicateRecursionChargesBeforeEachEntry` separately exercises
real radical Split and rational Compile recursion, denies the next recursive event at 64, proves no
refund after Restore, and proves the same expressions resolve to Zero in a fresh account.

### Failure and allocation proofs

- WorkLimit. Limit 7 accepts exactly one event in each category and rejects the eighth before its
  body. Failed charges and later failures leave the snapshot unchanged. Disabled diagnostics, nested
  checkpoints, source Reset, PolynomialSign, later roots, leaves/parts, both proposal contacts and
  BeginBelowBandPass cannot resume or refund work. A new request and nested owner are independent.
- Atomicity. A represented tilted cylinder with injected collector limit 32 refuses as Unsupported,
  Members 0 and Witnesses 0. Complete member copies, joins filled with 73UL and unused tails remain
  untouched. Find maps the actual backend refusal to Refused. Every storage reason also discards an
  already retained prefix before publication.
- RootStorage. The unresolved root-function seam reaches the unchanged 1,024 live-cell high water
  with Consumed below Limit, reports RootStorage and clears its output. Restore and later charges
  cannot replace it with WorkLimit.
- TerminalRoots. A quadratic with two roots and the existing per-solve one-root seam reports
  TerminalRoots with unexhausted work and zero roots returned. Later solves cannot replace the reason.
- AdmissionStorage. A closed zero-height slice through a tilted cylinder reaches the unchanged
  1,024 admission-cell high water, reports AdmissionStorage and returns Unresolved before WorkLimit.
- Warm allocation. The observed complete footprint query allocates zero bytes. The existing curved
  pose, uncertified storage, 256-member/512-witness and represented-control allocation assertions all
  remain green. Enforcement and diagnostic publication add no object per warm Find.

Full solution build, full Game.Tests, format verification and final living-contract publication remain
Task 7 work. Task 5 owns the original seam measurements and traversal failure gate.

Self-review also exercised a denied Sign inside `CapUniformlyUp`. Its cached upward bound originally
returned true after that denial. The added regression failed False versus True, then passed after
the shortcut checked the sticky owner again. The caller's existing refusal remains unchanged.

### Task 4 fix round 1: sticky success exits

FC-R9 is enforced at curved certificate exits through `SupportCurvedWork.Complete`. A failed owner
returns Unresolved signs, false booleans and default geometry, and clears witness, height and member
outputs even when an earlier nested step already resolved them. This covers optional band snaps,
cap normal refinement, cached cap bounds, side and round extrema, root refinement, surface admission,
both proposal contacts and their tilted and footprint callers. The helper neither charges nor changes
the first failure. The fixed ceiling, every tolerance and band, arrival rule and raw identity stay unchanged.

Two regressions failed before the fix. The represented cylinder rotated about Z by PI / 6 with radius
0.25, half-length 0.5 and owner limit 85 returned Positive from Contact at lower band
0.5551881949939084 after WorkLimit. A second fixture first completed the same root solve with bands
-2 and 2, then exhausted during optional cap upward refinement and retained a resolved witness.
Both now refuse or clear their output, preserve WorkLimit and keep Consumed at 85 on later attempts.
The Contact regression also checks cached cap bounds, surface output and root callbacks after failure.

## Task 5 initial represented end-seam gate

Each row installs a fresh `ContactFollowupScenes.Log(capsule, 0.3f, false)`, acquires one fresh
lease and makes exactly one Find. FeetY is 0.6f, foot radius 0.2f, both reaches 0.4f and slope
is the existing 45-degree cosine. Every row asserts Find 1, Proposals 2, one observation,
Limit 1,048,576 and the seven-category sum equal to Consumed. Counters are functional evidence.

| Shape | X bits | Status | Footprint | Failure | New | Sign | Split | Compile | RootCells | Refine | AdmissionCells | Consumed | Limit | Root high water | Admission high water |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Capsule | bf333332 | Walkable | Complete | None | 514 | 92 | 0 | 0 | 10 | 24 | 6 | 646 | 1048576 | 2 | 6 |
| Capsule | bf333333 | Walkable | Complete | None | 766 | 146 | 0 | 0 | 26 | 23 | 6 | 967 | 1048576 | 4 | 6 |
| Capsule | bf333334 | Walkable | Complete | None | 792 | 142 | 0 | 0 | 14 | 24 | 6 | 978 | 1048576 | 2 | 6 |
| Cylinder | bf333332 | Walkable | Complete | None | 64860 | 4942 | 0 | 0 | 266 | 161 | 1696 | 71925 | 1048576 | 4 | 352 |
| Cylinder | bf333333 | None | Complete | None | 65112 | 4960 | 0 | 0 | 192 | 72 | 1696 | 72032 | 1048576 | 4 | 352 |
| Cylinder | bf333334 | None | Complete | None | 65006 | 4880 | 0 | 0 | 8 | 0 | 1696 | 71590 | 1048576 | 1 | 352 |

The three X values are BitIncrement(-0.7f), -0.7f and BitDecrement(-0.7f), in table order.
The capsule passes. Its critical 967 is at most 10 * min(646, 978) = 6460 and all rows are
Complete/Walkable with Failure None. The original cylinder all-Walkable requirement was wrong.
FC-R10 corrects the premise using an independent exact rational/radical oracle. The upward
barrel's closest projected point is its end crest. With S = 16777215, N = S*S+1,
h = 1/2, r = 5033165/16777216, rho = 13421773/67108864 and A = -x-rho, its disc
membership is decided by E = N*A*A-(h*S-r)^2. The three exact signs are negative,
positive and positive. The crest is walkable and in band only in bf333332. Every upward cap
point has tangent contribution cy + sqrt(N)/2 + S*x below the reach band, with installed
cy = 10066331/33554432. For bf333334, N*A*A-(h*S+r)^2 is also positive, excluding
the entire cylinder. The floor is below band. Correct statuses are Walkable, None, None.
Production agrees on all three, with Complete footprints and Failure None.

The cost defect is independent. Each Find spends 1,696 admission cells in two passes, each
with 846 barrel membership search cells before attaining a point, one upward-cap cell and
one opposite-cap exclusion cell. The adjacent-neighbor bound conceals this uniformly hot seam.
Task 6 is a cost repair with unchanged public results, bounded per row by ten times the
interior cylinder Find at x = -0.25f as measured at a251a3bf4 (762, FC-R11). It may not change
either None to Walkable.

FU-R4 selected the crown branch under the original premise. FC-R10 explicitly retains that
certificate. The executable gate now asserts oracle statuses separately from integer cost.
No test is skipped.

### Whole-capsule source certificate

`TryGlobalCrown` runs only for the capsule footprint role, before Side. The exact sign of the
normalized source Y chooses the higher endpoint in Begin's canonical frame. Its nearest gap
to the original closed disc must be exactly zero. RoundEnd then proves the hemisphere and
requested slope and supplies its attained vertical radial vector. That vector has height radius,
so the source identity is centre.Y + abs(axis.Y) * halfLength + radius. The global enclosure is
evaluated from that expression, without comparing separately rounded endpoint heights for equality.
False retains the general path. Axis, Lowest, complete admission, both proposals and every other
raw member remain unchanged. The same certificate runs in optimized and reference evaluation.

The four installed attainment rows cover both quarter-turn axes and both physical ends, using
the actual represented sign independently for each pose. Rational/radical oracle bounds enclose
the attained global height and position inside the original disc and correct hemisphere. Normal Y
is exactly [1,1], and X/Z enclose zero. Direct Round uses zero root cells and zero refinements.
Raw key, Axis then Footprint role order, HeightError at most 0.0005f and bit-exact reference
equality are asserted through publication and public Find.

The 36 classified controls cover exact positive, negative and zero source Y, exact original-disc
boundary and adjacent represented positions, and nonzero lateral displacement. Outside or
unresolved classifications cannot certify. Exact horizontal Y declines and retains its general
Round path. A crown outside the original disc but inside the contact-band admission ring declines.
The independent low roof leaves the complete footprint and both InvalidStart proposal guards
intact, with a lower floor present, and public Find refuses in optimized and reference modes.

FC-R9 is also pinned at the final charged event of a successful crown proof. Denying that event
returns false with default outputs and preserves WorkLimit. A later Round remains Unresolved.

### Post-certificate capsule counters and arrival

Each counter row below again uses one fresh scene, lease and Find. All are Complete/Walkable
with Failure None, Find 1, Proposals 2 and Limit 1048576. The cylinder code was not changed
or remeasured after the certificate. Its initial cost evidence above remains Task 6's starting record.

| X bits | New | Sign | Split | Compile | RootCells | Refine | AdmissionCells | Consumed | Root high water | Admission high water |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| bf333332 | 502 | 94 | 0 | 0 | 10 | 24 | 6 | 636 | 2 | 6 |
| bf333333 | 502 | 94 | 0 | 0 | 10 | 23 | 6 | 635 | 2 | 6 |
| bf333334 | 812 | 150 | 0 | 0 | 14 | 24 | 6 | 1006 | 2 | 6 |

The capsule critical row satisfies 635 <= 10 * min(636,1006) = 6360.
The rounded-end traversal installs the work observer before its initial seat and every dry tick.
It throws on the first Failure other than None or Consumed above 10 * max(636,1006) = 10060,
before the Find can return and before another tick can run. Each dry tick owns and disposes its
lease and memo and authenticates the returned support. Default tuning, 240 ticks and the exact
Euclidean 0.001f arrival rule are unchanged. Traversal starts on the crest and walks to either
rounded end or down either walkable flank, without trying to mount the 0.6 m crest from the floor.

| Quarter-turn axis | Direction | Path | Ticks | Curved observations | Largest Consumed |
| --- | --- | --- | --- | --- | --- |
| Z | -1 | Rounded end | 45 | 91 | 923 |
| Z | +1 | Rounded end | 45 | 91 | 965 |
| X | -1 | Rounded end | 45 | 91 | 949 |
| X | +1 | Rounded end | 45 | 91 | 907 |
| Z | -1 | Down flank | 18 | 37 | 1919 |
| Z | +1 | Down flank | 18 | 37 | 1907 |
| X | -1 | Down flank | 18 | 37 | 1895 |
| X | +1 | Down flank | 18 | 37 | 1883 |

### Task 5 verification and pending gate

The initial integrity-only gate exited 0 with one passing test. The default logger hid passing
output, so one diagnostic-only invocation added detailed console logging to retain the six rows.
It also exited 0 with one passing test, before any certificate edit. No timing data decided a gate.

The inert false-returning crown produced the expected behavioral red, exit 1 with six failed
and one passed. All four installed attainment rows failed at crownCertified and the two control
groups failed at their first independently proved inside point. The public roof negative passed.
The first green selection exited 0 with 99 passed. Self-review added direct zero-root/refinement,
horizontal fallback and final-event work-denial assertions. The final required green selection
exited 0 with 100 passed, zero failed and zero skipped. Referenced Release projects built with
no compiler warnings. The selection was:

```bash
build-slot --heavy --label fu-c-crown-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~CapsuleCrown|FullyQualifiedName~RepresentedCapsuleRoundedEndTraversal|FullyQualifiedName~CurvedOracle|FullyQualifiedName~FootSupportBoundaryTests' --logger 'console;verbosity=detailed'
```

Task 5 took the required crown branch, retained by FC-R10. The cylinder results already match
the independent oracle (Walkable, None, None). Task 6 repairs its successful but expensive
barrel admission search. The combined record asserts per-row status separately from cost,
with bit-identical public results and the interior-cylinder cost bound required for Task 6. Full solution build, full Game.Tests, format verification and
living-contract publication remain Task 7 work. No tolerance, band, limit, identity or arrival
rule changed. No capture variables, cost artifacts, load, consumer launch or full suite was used.


## Task 6 finite cylinder admission cost repair (FC-R10, FC-R11)

Under FC-R11, `HasFiniteAdmissionWitness` offers one nearest-point candidate per part. The barrel
offers the upward top-line point (kind 0, b = 0) at the axial parameter that puts it under the
foot centre, clamped to [-1, 1]. Each individual cap offers the vertical projection of the foot
centre when it lies in the disc, otherwise the centre-directed rim point. Both parameters are
binary64 candidates only. An enclosure that misses the column declines a candidate before any
source work. Otherwise the existing `SurfacePointInside` expressions prove inclusive disc and
column membership exactly at the offered dyadic point. `CapUniformlyUp` protects upward cap
coverage. A true result proves one attained member point only. A false result retains the
original complete cell traversal, storage and refusal policy. It supplies no exclusion, coverage
or maximum claim. All source events charge the existing owner. Every success exit observes FC-R9.

The first Task 6 commit (df3ff6d8c) offered a fixed set: the four barrel end points and each
cap's centre and four cardinal rim points. Its inert helper produced the behavioral red before
Measure was changed. All three seam rows failed cost, four part/sign safety rows and the
closed-endpoint test failed at the first missing positive witness, the independent roof control
passed. That fixed set missed at the ordinary interior row and charged 1,286 source events before
the unchanged traversal, so FC-R11 replaced it.

| Cylinder X bits | Status | Consumed a251a3bf4 | Consumed fixed set | Consumed nearest point | Admission cells a251a3bf4, fixed, nearest | Root cells | Refine |
| --- | --- | --- | --- | --- | --- | --- | --- |
| bf333332 | Walkable | 71925 | 1655 | 1109 | 1696, 2, 2 | 266 | 161 |
| bf333333 | None | 72032 | 1762 | 1216 | 1696, 2, 2 | 192 | 72 |
| bf333334 | None | 71590 | 1320 | 774 | 1696, 2, 2 | 8 | 0 |
| be800000, interior x = -0.25f | Walkable | 762 | 2048 | 588 | 12, 12, 4 | 36 | 54 |

The gate compares every seam row with 10 * 762 = 7620, the interior count measured at a251a3bf4
and pinned as `PreAdmissionWitnessInteriorConsumed`. A reference measured after the change could
hide a regression. The interior row itself must stay at or below 762. Admission high water is 1
on every row. The two remaining seam cells are the opposite cap's exclusion, one in each pass.
The interior row's four remaining cells are the two caps' exclusions. No limit was changed.

`CertifiedCurvedStrataFitTheCumulativeQueryCeiling` was measured with the helper unwired
(a251a3bf4 behavior), at df3ff6d8c and after FC-R11. No row rises above its a251a3bf4 value.
Only the tilted cylinder rows move. Every other row is unchanged.

| Corpus row | a251a3bf4 | Fixed set | Nearest point |
| --- | --- | --- | --- |
| tilted-side | 822 | 1226 | 729 |
| tilted-cap | 1520 | 991 | 728 |
| slope-below | 827 | 1231 | 734 |
| slope-boundary | 1055 | 1459 | 962 |
| slope-above | 1055 | 1459 | 962 |

Before wiring Measure, the red run serialized the six unchanged cylinder and capsule rows.
`Physics/Fixtures/cylinder-admission-before.jsonl` pins production a251a3bf4 with SHA-256
`8175d7755a57577628f11f09a6adaf29f9a0285887852c121ef91a9d84996980`.
All float and double fields are integer bit strings, including signed zeros and NaNs. The
comparison includes public status, both occupied and unused role slots, witnesses, normals,
position/normal/height errors, XZ, owner and raw identity, disc bounds, both proposal verdicts,
origin/generation, counts and every published join word. Receiver/source/lease identity is
asserted in the live interval. All six rows match, optimized and reference outputs agree and
all six warmed Finds allocate zero bytes. Capsule Consumed stays 636, 635, 1006.
`Physics/Fixtures/cylinder-admission-interior-before.jsonl` pins the interior row the same way,
captured from production df3ff6d8c, SHA-256
`01ef72fffb5c2f4a15394ba69d63de905c44582ec633f09fe9c5b22ba146fe1e`. It matches after FC-R11.

The safety matrix covers both physical ends, both installed near-quarter-turn axes with positive
and negative represented Y, the independent 3-4-5 source columns, all cap cardinal points,
barrel versus cap and opposite-cap isolation, upward-only membership, inclusive radius/height
endpoints and an adjacent outside radius, outside columns, the top-line parameter clamped at
both ends against an unclamped point beyond the barrel, cap projections inside and outside the
disc, the lower barrel line and an off-line barrel column that the candidate misses and the full
traversal still finds. Final-event denial latches WorkLimit, blocks later helper
success and keeps Measure Unresolved. The independent roof retains both InvalidStart proposals,
a lower floor member and Refused public support. No maximum or proposal guard is bypassed.

Task 7 retains full solution build, full suite, format verification and living-contract publication.
No tolerance, band, limit, raw identity, arrival rule, version or release state changed.

The final combined selection passed 134 tests, exit 0, with no compiler warnings. It includes
CylinderAdmissionWitness, CylinderEndTangency, RepresentedEndTangencyCounterGate, CurvedOracle,
FootSupportBoundaryTests, CapsuleCrown, RepresentedCapsuleRoundedEndTraversal, CurvedWork and
CertifiedCurvedStrataFitTheCumulativeQueryCeiling. All local test invocations used build-slot,
synchronously and one at a time. No full suite, timing, stress or repeated load proof ran.

## Task 7 contract publication and final verification

FC-R12 supersedes the original staged-version instruction. Version 21.5.1 is
tagged at a1e826635. This task cuts patch 21.5.2, updates guarded package examples
and adds a new CHANGELOG entry dated 2026-10-19. The full released 21.5.1 suffix
is byte-identical to v21.5.1. No release, tag, tool or consumer pin follows.

Living contracts resolve #1494 and #1487, distinguish point-valued represented
support from retained feature/sweep sources, and publish bounded curved work and
its visible refusal reasons. CC-8, acceptance G and plan Task 6 match the shipped
FC-R11 nearest-point candidates and fixed 762/7620 cost bounds. Historical source
discriminants, golden evidence, timing and earlier counters remain unchanged.
Both conditional repairs ran. The capsule crown remains required, and cylinder
admission preserves every public output bit with full traversal on a miss.

The initial full Game.Tests run discovered 6,206 tests, with 6 failures, 6,199
passes and one pre-existing skip. Five failures came from stale test geometry.
TopHeightAt still used real-quaternion corners, and two direct-kernel helpers
used the dual factory before BeginSource. The helpers now use the independent
represented rational oracle or explicit support factory. Assertions, scene
inputs and production code are unchanged. Their necessary changed checks pass.

The sixth failure was DescendingRampAheadKeepsTheFlat(Box). Independent rational
geometry proves the exact-zero floor plane, positive ramp disc maximum and direct
whole-face join. The floor contribution is [-5 binary64 subnormal steps,0]. The
joined ramp contribution is symmetric [-5 steps,+5 steps], so its midpoint wins
under the existing selector before witness distance is compared. The selected
raw owner is the ramp, while published height and normal remain exactly 0 and
UnitY. The Mesh row keeps the floor owner. The renamed
DescendingRampAheadKeepsTheFlatHeight test asserts the independently derived
enclosures and their order, selected owner, exact float height and exact normal.
FC-R13 explicitly reserves this owner transition for whole-branch review.

After these test-side corrections, FC-R13 authorized one second full Game.Tests
run. The successor ran the following commands synchronously through build-slot,
once each on the final code. No test loops, timing, load or stress proof ran.

| Command | Exit | Result |
| --- | --- | --- |
| `build-slot --label fu-c-final-build-2 -- dotnet build KhaozEngine.slnx -c Release` | 0 | Build succeeded, 0 warnings, 0 errors |
| `build-slot --heavy --label fu-c-final-suite-2 -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --no-build` | 0 | 6,206 total, 6,205 passed, 0 failed, 1 pre-existing skip |
| `build-slot --heavy --label fu-c-final-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --verbosity diagnostic` | 0 | Formatted 0 of 8,400 files |

The skipped test remains
WallContactOwnColumnTests.The_perturbation_control_for_every_sensitivity_claim_in_this_chain.
The orchestrator separately verified all other 168 solution projects on this
source, exit 0, in task-7-other-suites.log. The successor did not rerun them.

Under FC-R8, diagnostic format output identifies the workspace message as
`Found project reference without a matching metadata reference` for
KhaozEngine.Localization.Analyzers.csproj. The existing Showcase project reference
is analyzer-only, with OutputItemType=Analyzer and ReferenceOutputAssembly=false.
The analyzer targets netstandard2.0 and ships under analyzers/dotnet/cs. This
dependency wiring is unchanged. Diagnostic verbosity exposes the underlying
message without suppressing it. All branch-changed code projects loaded their
configuration and ran both analysis passes. Game.Tests ran 5 code-style and 233
analyzer-reference analyzers, Physics.Bepu ran 5 and 154, and Physics ran 5 and 154.
No changed project was omitted. The complete output is retained in the owning
workspace's task-7-format.log.

No tolerance, band, limit, roof or footprint protection, raw identity, Euclidean
1 mm arrival or production source changed in Task 7. Native G1b, SG-12/#1334,
navigation, adoption and release remain separate gates.
