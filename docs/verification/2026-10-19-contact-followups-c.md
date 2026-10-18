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
Task 1 covers the planar doors. Curved-specific source changes belong to Task 2.

| Site | Audited source | Task 1 disposition | Evidence and retained obligation |
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
| 35 | `SupportNeighborhoodCurved.cs:19`, `:69`, `:101`, `:109`, `:217` | legacy-retained | Curved source and tangent capture are explicitly deferred to Task 2. |
| 36 | `SupportCurvedSourceArithmetic.cs:33`, `:42`, `:200` | legacy-retained | Curved source-column constants and removal of alternative-transform nodes belong to Task 2. |
| 37 | `SupportCurvedRootRegions.cs:42`, `:78`, `:216` | derived-unchanged | Curved root isolation remains for Task 2 source adoption and later work enforcement. |
| 38 | `SupportFootprintCurved.cs:40`, `:65`, `SupportFootprintCurved.Admission.cs:6`, `SupportFootprintCurved.Proposal.cs:10` | derived-unchanged | Curved footprint, admission and proposal formulas are untouched in Task 1. |
| 39 | `SupportTiltedCurved.cs:13`, `:87` | derived-unchanged | Tilted curved coordination is untouched in Task 1. |
| 40 | `SupportTiltedCurvedGeometry.cs:46`, `:137`, `:260` and partials listed below | derived-unchanged | Tilted curved frame and all listed partials are untouched in Task 1. Task 2 owns source adoption. |
| 41 | `SupportCurvedSurfaceAdmission.cs:24` | derived-unchanged | Curved whole-surface protection, cells and atomic refusal remain. |
| 42 | `GeometryVectorOperations.cs:42`, `:77`, `SupportFootprintPublication.cs:8` | derived-unchanged | Normalization, witness conversion and arithmetic/publication errors remain. |
| 43 | `SupportFootprintCollector.cs:103`, `:135`, `:202`, `:277`, `SupportFootprintCollector.Geometry.cs:12`, `SupportFootprintCollector.Relevance.cs:21` | derived-unchanged | Cached, fallback and reference planar materialization now share represented capture. Collector formulas and build-before-copy remain. |
| 44 | `SupportNeighborhoodCollector.cs:118`, `:162`, `:194` | derived-unchanged | Uncached planar neighborhoods receive the same represented source. Curved changes wait for Task 2. |
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
