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
Curved line references are at the Task 2 commit.

| Site | Audited source | Disposition | Evidence and retained obligation |
| --- | --- | --- | --- |
| 1 | `InstalledPoseOperator.cs:64`, `:93`, `:112`, `:141` | changed | Explicit support door directly certifies represented columns. Legacy TryCreate, RealCoefficients and union remain. Scalar and SIMD capture is shared. |
| 2 | `InstalledPoseOperations.cs:55` | changed | Separate support admission feeds the same rounded composition enclosure and actual SIMD continuation. |
| 3 | `RepresentedGeometryTransforms.cs:40`, `:77` | legacy-retained | PosePoint stays in legacy wrappers. Planar support and its query/proposal domain gates no longer call it. |
| 4 | `CapsuleFeatureGeometry.cs:65`, `:147`, `:169` | changed | Operator-accepting capture and transform cores retain coordinate checks and expression order. Legacy wrappers retain correspondence. |
| 5 | `CapsuleFeaturePolyhedra.cs:48`, `:55`, `:169` | derived-unchanged | Vertices, edges, winding and cross normals derive from the support operator before validation. |
| 6 | `SupportNeighborhoodPolyhedra.cs:20`, `:63`, `:106` | changed | Both planar capture doors admit represented installed/composed leaf poses and pass their operator to the shared core. |
| 7 | `InstalledSupportGeometry.cs:39`, `:119` | changed | Cached local topology materializes vertices and normals from the represented operator. Compound composition uses support admission. |
| 8 | `InstalledSupportGeometryCache.cs:89`, `:100` | changed | Identity local-topology capture and child-pose admission use the support door. Cache keys and publication are unchanged. |
| 9 | `SupportNeighborhoodMesh.cs:32`, `:49`, `:80`, `:132`, `:176` | changed | Both mesh query doors and triangle materialization use the represented map. Local bounds use its actual inverse. |
| 10 | `InstalledSupportMeshWorldForm.cs:84`, `:148`, `:181` | derived-unchanged | Immutable triangle forms receive the new represented operator from traversal. Pose-bit keys, triangle ids and publication remain. |
| 11 | `SupportNeighborhoodPolygon.cs:112`, `:162`, `:217` | changed | Point-valued source map. BeginSource rejects non-point or mismatched columns before extracting exact binary32 entries. |
| 12 | `InstalledSupportWorldForm.cs:31`, `:104`, `:153` | derived-unchanged | World forms snapshot one represented leaf and its derived polygons and meetings. No source is patched after validation. |
| 13 | `InstalledSupportMemberFacts.cs:17` | derived-unchanged | Cached member facts are rebuilt from the represented polygons in the original operation order. |
| 14 | `SupportSourceMapComparer.cs:11` | changed | Source sharing hashes and compares all twelve binary32 patterns, including signed zeros. |
| 15 | `SupportSourcePolynomial.cs:46`, `:114`, `:130` | derived-unchanged | Production planar callers install nine constants. Generic box-coefficient support remains for synthetic arithmetic. |
| 16 | `SupportSourceCoefficient.cs:31`, `:74`, `:89` | derived-unchanged | Exact expansion arithmetic and its 128-part refusal remain. |
| 17 | `SupportFootprintSourcePredicates.cs:19`, `:52`, `:84`, `:139`, `:145`, `:165`, `:191` | changed | Nine exact constants and twelve-bit source reuse. Local subtraction, translation/axis subtraction, lifecycle and all formulas remain. |
| 18 | `SupportFootprintSourcePredicates.ProjectedSeparation.cs:10` | derived-unchanged | Same projected-separation expression and strict exclusion rule. Its source entries are now exact constants. |
| 19 | `SupportFootprintEdgeOperands.cs:26`, `:36`, `:63` | derived-unchanged | Edge operands and shared lengths derive from the represented polygon. Request reuse is unchanged. |
| 20 | `SupportFootprintPolygon.cs:32`, `:141`, `:267`, `:374` | derived-unchanged | Admission, extrema and closed strata consume the represented polygon/source. No formulas or bands changed. |
| 21 | `SupportFootprintPolygon.StairStratum.cs:12`, `:57` | derived-unchanged | Rectangle exactness remains literal. A trigonometric quarter-turn is not snapped. |
| 22 | `SupportFootprintProposalGeometry.cs:13`, `:24`, `:71`, `:117` | derived-unchanged | Proposal contacts use the same represented source. Exhaustive order and attained guards remain. |
| 23 | `SupportPlanarProposal.cs:13`, `:32`, `:52` | changed | Identity qualification reads Vector3 components directly. Exactness, lattice and range protections remain. |
| 24 | `SupportPlanarNeighbour.cs:10` | derived-unchanged | Sufficient contact certificate retains its conditions and receives represented derived operands. |
| 25 | `SupportRectangleContact.cs:34`, `:76`, `:117` | derived-unchanged | Rectangle and lowest shortcuts retain their exactness and finite-domain checks. |
| 26 | `SupportContactPrefilter.cs:46`, `:58`, `:92`, `:143` | derived-unchanged | Contact pruning retains arithmetic-error and chord requirements. Exact inputs do not erase arithmetic error. |
| 27 | `SupportVerticalAdmission.cs:58`, `:85` | derived-unchanged | Vertical admission keeps its separation margins and exhaustive fallback. |
| 28 | `SupportProjectedPolygonSeparation.cs:10`, `:54` | derived-unchanged | Projected pruning still requires strict source-certified separation. Equality cannot exclude a polygon. |
| 29 | `SupportNeighborhoodPolygon.cs:259`, `:271` | derived-unchanged | Closest-pair strata receive represented vertices, edges and cross normals with the same error arithmetic. |
| 30 | `SupportSegmentPolygon.Footprint.cs:8` | derived-unchanged | Independent proposal approach/front guards and contact band remain. |
| 31 | `SupportSourceIncidence.cs:24`, `:70` | derived-unchanged | Incidence uses the twelve-bit comparer. Every raw polygon link remains separate. |
| 32 | `SupportJoinIncidence.cs:13`, `:71` | derived-unchanged | Same-plane and common-vertex certificates retain their bounds and raw rows. |
| 33 | `SupportMeetingDomain.cs:15`, `:34` | derived-unchanged | Meeting denominator and rate domains remain. |
| 34 | `SupportNeighborhoodJoins.cs:21`, `:39`, `:66`, `:80` | derived-unchanged | Whole joins and mutual-below tests remain. Equal source maps never replace raw identity. |
| 35 | `SupportNeighborhoodCurved.cs:22`, `:49`, `:57`, `:60`, `:72`, `:104`, `:116`, `:354` | changed | Compound root, child pose and composition gates use the support door (Task 1 fix round 1). Both Leaf doors (`:72`, `:104`) now admit through TryCreateSupport. `SourceColumn` (`:22`) is the represented binary32 Y column, extracted only from exact, bit-matching represented entries. Axis (`:116`) is the outward normalization of that same point, and Across derives from Axis, so the tangent kernel (`:354`) and every footprint stratum read one column. Radius, half length, part ids and the circular primitive are unchanged. |
| 36 | `SupportCurvedSourceArithmetic.cs:37`, `:197`, `:222` | changed | `Reset(Vector3)` installs the column as exact constants. The Source operation, `_column` interval storage, source-polynomial coefficients and their compile branch are removed. The node graph, denominator signs, radical elimination, 4,096-node and 54-rent compile ceilings, degree-eight bound and Checkpoint/Restore are unchanged. Constant polynomials still decide undecided interval signs exactly. |
| 37 | `SupportCurvedRootRegions.cs:42`, `:78`, `:216` | derived-unchanged | Root isolation, endpoint exact signs, 1,024 live and 12 terminal cells are unchanged. Irrational coefficients still arrive as enclosures, so whole-region certificates remain necessary. Only the class comment now names a coefficient enclosure instead of a source family. |
| 38 | `SupportFootprintCurved.cs:42`, `:52`, `SupportFootprintCurved.Admission.cs:29`, `SupportFootprintCurved.Proposal.cs:28` | changed (source only) | Begin resets from the represented column. `_axisSign` reads the binary32 Y sign. Spherical, upright and horizontal strata, admission and lowest proposal contacts use the same constants. Finite-end clipping and inclusive equalities are unchanged. A represented column with exact zero Y now takes the closed-form horizontal stratum where the old box was tilted. |
| 39 | `SupportTiltedCurved.cs:13`, `:87` | derived-unchanged | Tilted coordination consumes the geometry owner below. Full-part admission and atomic refusal remain. |
| 40 | `SupportTiltedCurvedGeometry.cs:48` and partials listed below | derived-unchanged (source switched through site 36) | Begin resets from the represented column and keeps algebraic normalization, the axial and radial frame and every guarded boundary, stationary, contact and surface equation. The weak-sign clipping in NormalizedYBounds stays, and it is now exact for a signed zero column. |
| 41 | `SupportCurvedSurfaceAdmission.cs:24` | derived-unchanged | All coverage cells, maxima, caps and atomic refusal remain even where the point column resolves more signs. |
| 42 | `GeometryVectorOperations.cs:42`, `:77`, `SupportFootprintPublication.cs:8` | derived-unchanged | Normalization and binary32 publication still enclose arithmetic and conversion error. An irrational unit axis from a point column publishes a nonzero error, proved by RepresentedCurvedRadicalsRemainGuardedAfterPointSources. |
| 43 | `SupportFootprintCollector.cs:103`, `:135`, `:202`, `:277`, `SupportFootprintCollector.Geometry.cs:12`, `SupportFootprintCollector.Relevance.cs:21` | derived-unchanged | Cached, fallback and reference planar materialization now share represented capture. Collector formulas and build-before-copy remain. Curved leaves reach the support door through `InstalledSupportGeometry.Materialize` and its cache identity leaf (`InstalledSupportGeometryCache.cs:111`), with no dual admission on either public query. |
| 44 | `SupportNeighborhoodCollector.cs:118`, `:162`, `:194` | derived-unchanged | Uncached planar neighborhoods receive the same represented source. Curved capture at `:131` uses the represented column and support door. |
| 45 | `BepuPhysicsWorld.SupportFootprint.cs:13`, `:66`, `:114` | changed | Proposal pose-domain admission uses the support factory. Raw sweeps, lease, selections, versions and publication remain. |
| 46 | `BepuPhysicsWorld.SupportNeighborhood.cs:30`, `:78` | changed | Probe pose-domain admission uses the support factory. Static-only selection and query limits remain. |
| 47 | `KhaozEngine.Locomotion/Contacts/SupportCertification.Footprint.cs:27`, `:52`, `:79`, `:118` | derived-unchanged | Locomotion footprint certification receives represented published bounds. Provenance, steep suppression and direct joins remain. |
| 48 | `KhaozEngine.Locomotion/Contacts/SupportCertification.FootprintScope.cs:9` | derived-unchanged | Scope containment obligations are unchanged. |
| 49 | `KhaozEngine.Locomotion/Contacts/FootSupport.cs:101`, `:180`, `:214`, `:273` | derived-unchanged | FootSupport selection, reach, invalid-start guards and 1 mm rules are unchanged. |
| 50 | `KhaozEngine.Locomotion/Contacts/SupportContinuity.cs:80`, `:175`, `:204` | derived-unchanged | Raw continuity identities, direct joins and lease lifetime remain unchanged. |
| 51 | `KhaozEngine.Locomotion/Contacts/SupportRequestMemo.cs:80`, `:94`, `:98` | derived-unchanged | Exact query memo keys, reference bypass and epoch invalidation remain unchanged. |


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
