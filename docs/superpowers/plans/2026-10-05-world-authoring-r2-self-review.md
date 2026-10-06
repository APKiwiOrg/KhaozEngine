# R2 full plan self-review and controller reconciliation

Checked `r2-full-plan-v4.md` against the CD1-approved design (`docs/design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md`), base C2 to C5, C8 and DG9.1 to DG9.6, F3 at `404fa519f` (accepted semantic design, not released API), controller reviews 1 to 3 with the D7 ruling, the approved final corrections of 2026-10-07 (D4 straddle refusal, D9 tagged legacy coverage, D7 attempted-pair budget, contact and witness dependency refresh), and source at `ca13d62d7`, which current `main` `54a1f358` does not change in any cited path. Read-only. The writer supplied the initial review. Root independently reviewed the changed v4 sections and reconciled the corrections below. Coordinator full-plan approval is still required. Publishable form: `docs/superpowers/plans/2026-10-05-world-authoring-r2-self-review.md`.

## Design requirement to task map

| Design requirement | Task and proof |
| --- | --- |
| Logical surfaces, bounded patches, row direction, rational units, int32 corners, 8-byte cells, presence bit | 4 `Patch_RoundTripsExactIntegersAndSignedSlots`, `Decode_RefusesMalformedPayloadBeforeAllocation` |
| Exact scalar arithmetic, checked growth, no snapping | 4 `ExactValue_NormalizesAndConvertsFloatsExactlyOrRefuses`, `Decode_RefusesOversizedInputUnreducedUnitsAndOverflow`, 12A outcome test, 12B `TinyLocalCoordinate_IsNotRepresentableNeverSnapped`, 13 `TinyLocalCoordinate_SupportIsNotRepresentable` |
| Exact vertex keys for any accepted subdivision and across lattices | 4 `Address_ReducesAndOneWorldVertexMatchesAcrossUnits` (1/2, 1/3, 1/64), 10 `Seam_ExactSharedVerticesAgreeAcrossLattices` (k 2, 3, 64) |
| Roles floor, ceiling, paint override, walls as strips | 4, 9 `ApplyPaintOverride`, 10 strips |
| Authoring layers group records only | Not modelled in R2, allocated to R9/R10 |
| Whole-cell apertures, finer patches with exact divisor and explicit shared subdivision, overlay cuts are paint only | 4 `Subdivision_LegalOnlyOnThePatchBoundaryOrAPresenceRim`, 9 `OverlayCut_NeverOpensOrClosesAPhysicalAperture`, rim tests, 10 seams, 11 |
| CD1 fine-patch conversion preserves geometry, diagonals, paint, cut coverage, flags and seam owners, or reports an authored difference | 11 (halves, thirds, sixty-fourths, rim owners, odd-k mid-edge, no drift, corner-cut classes, legacy fallback and arithmetic, retargeting, refusal before mutation), 17 undo and redo |
| Conversion on mixed-lattice footprints, no forced companion conversion | 11 `Conversion_OnAMixedFootprintRetargetsOnlyTheConvertedBound`, `Conversion_RefusesARegionThatSplitsAFootprintCellBeforeMutating`, 12B `ConvertedMixedPorch_ValidatesWithItsCoarseHalfFloor` |
| Atomic footprint-straddle refusal naming the footprint and cell with actionable alignment guidance, no automatic subdivision, no unrelated bound change (approved 2026-10-07) | D4, 11 `Conversion_RefusesARegionThatSplitsAFootprintCellBeforeMutating` (exact message, cell 1, digests unchanged), R9/R10 downstream row |
| Explicit, versioned compatibility tag for imported exterior lower coverage, exact recipe, finite caves and upper bounds refused (approved 2026-10-07) | D9, 5 `ReferenceValidator_AcceptsTheLegacyExteriorTagOnlyInItsExactRecipe` (space, upper and surface rules, policy id, byte value), 12A classifier and refinement tests (lattice rule), 12B and 13 `cave`, `upper-tag`, `foreign-lattice` variants |
| One classifier for validator, membership and support | D9 table, 12A `Classifier_SeparatesPhysicalLegacyHoleUntaggedAndInvalidCells` (agrees with compiled `LegacyFallbackCells`), 12B `LegacyExterior_ValidatorAndMembershipShareTheClassification`, 13 `LegacyExterior_ValidatorMembershipAndSupportAgreeAndRefuseOutsideTheRecipe` |
| Classified coverage without triangles, refinement stays geometric | 12A `LegacyExterior_RefinementCountsClassifiedCoverageWithoutFaces` (0 faces, `CompatibilityArea` 1, tag), D7 validation rule |
| Presence-0 holes never filled, untagged fallback never used, unloaded, corrupt and missing data never fallback | 12A, 12B (`hole`, `untagged`), 13 (stored source unloaded, `FlipPayloadByte`, `DeletePayload`) |
| Released bilinear arithmetic in one helper, frame and row-direction convention, no large-float round trip, no tolerance | D9, 12A `LegacyBilinear_MatchesReleasedHeightAtBitForBit` (Compatibility, bit equality against `HeightAt` on void, NoDraw, extreme, seam and derived-plane cases), 13 `LegacyFallback_UsesCellRelativeExactFractionsWithoutAWorldFloatRoundTrip` |
| Non-capture provenance, no fake face key, normal or clearance | D9, 13 `LegacyFallbackIsTaggedNonCapture` (`Face` and `Normal` null, tag, interval refusal, zero candidates), 15 relation rule, resolver v2 refuses non-`Supported` bindings |
| Policy identity | 5 (`kemap/legacy-exterior/1`, byte 4), 13 (tagged and untagged patch digests differ) |
| Existing legacy fallback fixtures valid under the same validator | 13 `SupportFixtures.LegacyFallback()` is the tagged row and validates empty, 11 `LegacyRowWithNoDraw()` is tagged and its accepted conversion retargets to `SupportFloor` |
| Seam, chain, strip, portal, link records referencing vertex keys | 5 records, 10 resolution and compile |
| Strip rules (lower at most upper, ruled split, zero-height end, facing, two-sided one owner) | 10 four strip tests |
| Portal band coverage, headers, risers, lintels, arches, exact sequences, gaps and overlaps | 12B `PortalBandCoverage_HeadersRisersAndLintels` (10 variants), `PortalBandCoverage_EachFaceIsEmittedOnce` |
| Owner versus membership, `(strip, side)` once | 5 validator, 12B faces once |
| Entrance as one transaction, return strip, roof thickness, exposed slope exterior | 12B fixture, 17 `TerrainTransaction_RestoresSeamSpaceAndIdentity` |
| Open band top only for two exteriors | 5 validator |
| Face identity, vertex owner at creation, lower key never moves ownership, deletion needs reassignment | 9 `MapFaceKey` tuple and order, 10 dependency tests, 17 `CornerOwner_...` |
| Collision-free identities under multi-edge subdivision | 9 `RimSubdivision_TwoEdgesOfOneTriangleAt64StayDisjointAndCollisionFree` |
| Compile once, legacy arithmetic, ceiling winding, strip normals, shared descriptors | 9, 13 `CanonicalEntranceSeamAndAperture_Agree` |
| Feather is paint only here | 9, R8 owns tessellation |
| Three identities: owner, space, domain | 5, 12B |
| Variable bounds, positive separation over the common lower/upper triangulation refinement including its vertices | D7, 12A (algorithm, near and far 1/2 against 1/3, crossings, degenerate and adversarial cases, budgets), 12B `RidgeUnderValley_...` (the only zero is a crossing neither lattice owns), `MixedResolutionBounds_ValidateOnTheCommonRefinement` |
| Mixed-resolution general rational bounds (D7 ruling) | 5 `..._ButAcceptsAnyRationalBound`, 8 bound enumeration tests, 12A, 12B, 11 |
| Explicit matching seam subdivisions stay strict | 10 seam theory, 12B `MixedResolutionWalls_StillRequireMatchingSubdivisions`, `PortalBandCoverage` `arch-mismatch` |
| Horizontal openings as portal planes, missing bound is `MissingGeometry` | 10, 12B `ShaftOpenings_...`, 13 `KnownHolesAreNoSupport_...` |
| Both shaft layouts | 12B `NativeCaveRepresentationContract` |
| Exterior declared, portal tie rules | 12B `ExactPortalAndOpeningPlanes_FollowTheOwnershipTieRules` |
| Membership statuses, stacked spaces, no fallback | 12B |
| Overlap rules (nested, alias, coincident, no blanket rule) | 12B, 13 `EqualHeightIndependentOwnersRefuseEvenWhenOneIsCurrent` |
| Imported Indoor masks keep bytes plus an explicit span | 4 `MapIndoorSpan`, 12B `ImportedIndoorMask_IsANestedDomainWithAnExplicitSpan` |
| Physical hole, overlay cut, NoDraw and void, missing data table | 9, 7 `SparseIndex_UnloadedIsNotEmpty`, D9, 12A, 12B, 13 `LegacyFallbackIsTaggedNonCapture` |
| Reserved Bridge preserved, never support | 4 flags, 9 and 18 cell bytes |
| Budgets: 64 by 64, 1 MiB, 65,536 faces, pages, query budgets, `CapacityExceeded` | 4, 5, 7, 8, 9 (`Compile_RefusesBeforeExceedingTheFaceBudget`), 12A, 12B, 13 |
| Refinement work and output budgets with no partial result | 12A outcome and 1/64 tests, 12B `MixedRefinementOutcomes_BecomeExplicitFindings` |
| Every attempted pair charged before bounding-box rejection, checked arithmetic, early refusal without hidden quadratic work (D7 settlement) | D7 step 4, 12A `Refine_ChargesEveryAttemptedPairBeforeBoundingBoxRejection` (8,192, default 65,536 and 262,143 refuse, a separate 2 by 2 grid completes 64 checks with 8 faces), `PairBudget_RefusesBeforeAnyComparisonAndCountsBoundingBoxPairsSeparately` (MapEditor.Tests internal counters: 262,144 charged and 0 comparisons on refusal, 64 comparisons, 16 positive boxes and 8 intersections for the small complete control, 6, 6, 6 and 5 for the tee) |
| Semantic and byte digests separate, mismatch is `Corrupt` | 5, 7, 8 `SchemeTwo_StaleSemanticDigestIsCorrupt` |
| Sparse index, occupied directory, unlisted empty only under verified pages, no enumeration | 7 (`PagesRead == 4`), 8 acquisition page counts |
| Bounded record lookup without scans | 5 `MapRecordRef`, 7 `IncidentRecords`, 8 `Acquire_*` tests |
| Bound-surface acquisition on any lattice, budgeted before reads | 8 `LatticeRanges_...`, `Acquire_EnumeratesExactly...`, `Acquire_BoundSlotBudgetRefusesBeforeReading`, `Acquire_UnrepresentableBoundRangeIsNotRepresentableNotCapacity` |
| Writer-owned `tiles/surfaces/`, manifest-last, sweep keep set | 7 |
| Windowed carry-forward and unloaded dependency refusal | 7 (slot-2 window fixture) |
| #1310 generation check, including surfaces-only windows | 1, 7 `SurfacesOnlyWindows_SecondSaveRefusesAsStale` |
| One completeness contract across load, window, source, refresh, clone, save, save-as, identity, transactions | 7 `SurfacesOnlyWindow_IsPartialEverywhere...`, 8 `SchemeTwo_RefusesAPartialView`, 16 `PartialDocument_NativeTerrainCommandRefuses` |
| `IsReserved` unchanged, `surfaces/` untouched | 7 namespace test |
| `IMapSurfaceSource` read-only, verified chain only, generation pinning versus immutable acquisition | D5, 7 snapshot tests, 8 `Acquisition_IsImmutableAgainstReturnedCopiesAndLaterSweeps` |
| Bounded monolithic embedding, resolver 2 for surfaces | 7, 6 |
| Scheme 2 invariant under forms and repacking | 8 |
| Scope identity distinct, factory-only, frame and scope from the acquisition | 8 `ScopedIdentity_IsFactoryOnly...`, 15 `QueryResults_AreFactoryOnly` |
| One snapshot and witness across membership, support, relations | 15 `OneAcquisition_SharesOneWitness...` |
| R8 residency, publication, fencing, eviction | Downstream table |
| 8 MiB nav budget | Global Constraints, downstream table |
| Frames, int64 strings, explicit unit changes | 4, 11 |
| Legacy arithmetic and region anchor | 9 |
| `MapFramePoint`, integer anchor first, `CompileInFrame` local route, R1 transform unchanged | 12B, 14 |
| Whole-metre anchors, exact offsets, 1/3 m cells | 9, 14 |
| Precision table and named vertical risk | 14 |
| Import differential and physics tolerance | 9, 18, R3 |
| Height-aware support order, highest eligible, continuity only through seams | 13 |
| Resolver-1 recipe, resolver-2 bindings, separate entry, adoption | 6, 13 |
| Next free format, pure migration, closed schema, token once | 6 |
| Resolver-v1 execution equals released expectations | 2, 6 |
| Transaction seam, typed write sets, rejection invariants, effects | 16 |
| J2.1 smoothing and halo refusal, GUI and service equivalence | 17 |
| Fixture provenance, private exhaustive oracle, R11/G2 refreeze | 3, 18, D1 |
| Private harness: one project, missing inputs fail, secure nonpublic output, every failure route sanitized, explicit format, no PR exposure | D1, 2 rump guards, 3 guard tests (11 cases), 18, candidate gate |
| Bounded synthetic fixtures (no stress) | All fixtures. The largest are the k = 64 conversion and the 1/64 m floor under a 1 m roof (one 64 by 64 patch each), justified as the high-subdivision and budget proofs |
| F3 producer boundary, no #1299 duplication, no adapter claim | D5, 13 `EnumerateCandidates`, 15 |
| #458 facts with provenance, input points, exact apertures, authored state, certainty fields, no policy | 15, D5 R3 plan input |
| R3 physical request with caller-supplied segment or path, no route planner, explicit status | D5 R3 plan input |
| Downstream R3 to G5, including the immutable contact evidence (`e1b6e04c`) and Task 3 witness details (`e74e3274`) as R3/G1b dependencies, and the importer's duty to declare `LegacyExteriorV1` | Downstream allocation (R3, R9/R10, R11 and G2 rows), D9 |
| Verification through the slot, exit 75 reported, no retry helper, owner-only tags, owner-released prerequisite | Verification conventions, approval gate, release section |

## Review Focus check

Each line has its test in the owning task: Task 1 and Task 7 stale windows (including zero tile entries), Task 9 overlay cut beside a hole, Tasks 4 and 10 negative slot edges, Task 13 equal-height owners, Task 12B exact portal and opening planes.

## Step scan

- Every named test has an executable body with exact values (166 methods: v3's 157 plus one in Task 5, five in Task 12A across MapDoc, MapEditor and Compatibility tests, one in Task 12B and two in Task 13). No test is a comment-only name. No assertion block was moved out of its task.
- Green counts updated: Task 5 15, Task 12A 12 MapDoc plus 1 MapEditor plus 3 Compatibility, Task 12B 15 membership and 10 refinement, Task 13 12, Task 18 Compatibility 4. Task 11 stays 17 (assertions added to existing tests).
- Mechanical check over the plan: every `Map*`, fixture and harness identifier used in test code is defined in an Interfaces line, a fixture paragraph, the D5 R3 block or the public register. Remaining matches were test method names and BCL members.
- Hand-checked arithmetic: the k = 2 and k = 3 conversion corners from the two centre planes, the `HalfUnderThird` cell (vertices, five areas summing to 1/9, six pairs), the `FanFromSouthMidpoint` tee (six vertices, five faces), the NW-SE against SW-NE crossing, the ridge and valley minimum, the 1/64 counts (8,192 faces, 16,384 pairs, 4,225 vertices), the `long.MaxValue` crossing denominator `2p - 1`, the `int.MaxValue` unit numerator `5p^2`, the boundary-touch range 63 to 63, and the precision slot and frame positions.
- v4 hand checks, recomputed for attempted pairs rather than relabelled: `HalfUnderThird` 3 by 2 = 6 attempted, 6 positive boxes, 5 faces of 1/72, 1/24, 1/72, 1/36, 1/72 (sum 1/9). Tee 2 by 3 = 6, 6, 5 with areas 1/12, 1/6, 1/4, 1/6, 1/3 in (Lower, Upper) order (sum 1). NW-SE against SW-NE 4, 4, 4. Coincident 4, 4, 2. 1/64 m floor 16,384, 16,384, 8,192 faces, 4,225 vertices. Identical 16 by 16 grids 262,144 attempted, 1,024 positive boxes, 512 faces, 289 vertices, unit separation. Legacy row centres 3.0 m and 4.5 m, exact in `float`. `32001.1f` is 32001.099609375. The straddling porch cells are 1 and 65, so the message names cell 1.
- Corrections made during this review: the far-frame comment fraction (0.099609375, not 0.1015625), the straddle assertion uses `Contains` because the exception may carry a prefix, and an ambiguous complete-view `Unavailable` assertion was dropped from Task 12A because a complete view reports an absent patch as known empty. Unavailable data is proven through stored and filtered sources in Task 13.
- Corrections made during this review: `MapLatticeRanges` became the single home of cell arithmetic, the v3 12B membership count was 14 and is now 15, two mixed fixtures gained matching ceiling subdivisions, D5 no longer claims `RefineFootprintCell` returns a witness, D7 pins one finding format including `missing geometry`, the Task 18 harness helper types and the Task 7 packing overload are now declared, and the `NewProp` citation is line 80.

## Type and interface consistency

- Introduced per task: exact scalars, `MapExactXz`, `MapExactOverflowException`, addresses, frames, patch types (4), records and digests (5), recipe and `MapSurfaceSet` (6), storage, completeness, scopes, limits and sources (7), scheme 2, factory-only witnesses and identity, `MapScopedSurfaces`, `MapLatticeRanges` (8), compiler types and `MapFaceKey` order (9), boundary types and opening keys (10), conversion types (11), refinement types, `MapRefinementWork`, `MapCommonRefinement`, `MapLowerCellClass`, `MapLegacyCellTag`, `MapLowerCellClassification`, `MapLowerCellClassifier` and `MapLegacyBilinear` (12A), `MapLegacyExteriorRecipe` and `MapBoundKind.LegacyExteriorV1` (5), `MapFramePoint`, membership and validator (12B), support types (13), frame-local types (14), relation and aperture types (15), transaction effects (16), terrain edits (17), harness comparison helpers (18).
- Status enums are consistent: every query and acquisition enum carries `CapacityExceeded` and `NotRepresentable`, and every factory-only result carries its acquisition's `MapReadWitness`. v4 appends `Invalid` to `MapMembershipStatus`, `MapSupportStatus` and `MapRelationStatus` for a D9 recipe violation, matching `MapRefinementStatus.Invalid`.
- `MaxPairChecks` and `PairChecks` replace `MaxCandidatePairs` and `CandidatePairs` everywhere (Global Constraints, D7, Task 12A types and tests, Task 12B). No old name remains.
- `BoundFaces` now takes the footprint and one of its bounds. Its only callers are `RefineFootprintCell` and `RefinementFixtures.LedgeBoundFaces`, both updated.
- Shared fixtures cross projects only by `<Compile Link>` of `*Fixtures.cs`. No test project references another. `FilteredSurfaceSourceFixtures.cs` follows that naming so the link includes it.

## Dependency and conflict check

Strictly serial: 1, 2, 3, ..., 11, 12A, 12B, 13, ..., 18. Task 10 now lists Task 8 as a dependency because it uses `CompleteView`. Files touched by more than one task: `MapTiledFile.cs`, `MapTiledFile.Save.cs`, `MapTileIndex.cs` (1, 6, 7), `MapCanonical.cs` and the schema (6, 7), `MapSurfacePatchCodec.cs` (4, 5), `MapSurfaceSemantics.cs` (5, 7), `NativeDocumentSnapshot.cs` (6, 16), `FormatFourResolverExpectationTests.cs` and `FormatFourFixtures.cs` (2, 6), `LegacyOracleConverter.cs` (9, 18), the harness project (2, 3, 18). Task 12A adds a new Compatibility test file and only reads `LegacyOracleConverter`. Each later edit is named in its task. No baseline-listed file is edited.

## Estimate check

The per-task column sums to 34 to 57.5, plus 4 to 6 review, 38 to 63.5, presented as 38 to 64, the coordinator's provisional allowance and the one current estimate. D6 shows the history (29 to 49, 34 to 58, 37 to 62) with only the last row current. The v4 corrections are absorbed in existing rows, mostly Task 12A, without changing the column. That consumes slack rather than adding a row, which is a stated assumption, not a measured cost.

## Proportion

v4 is 3,105 lines and about 350 KB. v3 was 2,866 lines and 311 KB, v2 1,561 lines and 202 KB. The design is 797 lines, so the plan is about 3.9 times its length. The v4 growth is the D9 decision and table, the pair-count table, the legacy fixture and nine executable test bodies. No settled section was rewritten. The growth is the restored assertion bodies required by controller review 2, plus the D7 algorithm and fixtures, the corrected R3 boundary and the harness rules. No class wrappers, usings or repeated shell blocks were reintroduced. Root may still judge it heavy. Root retains the inline assertions because task execution needs the complete proof at the owning step.

## Residual risks

1. The vertical precision proof may expose the named risk. The plan reports, never relaxes.
2. Hand-derived exact expectations in Tasks 11, 12A and 12B are unexecuted. A wrong expected value would show up as a red test at execution, never as a relaxed assertion.
3. The centroid fan adds interior vertices. R3 should confirm it is acceptable for physics and nav capture, together with the focused contact evidence at `e1b6e04c` and the Task 3 witness bounds at `e74e3274`, neither of which is released or G1b proof.
4. `IncidentRecords` is writer-maintained bookkeeping. A stale list would hide a record from a scope. `VerifyTiled` reports it and transactions recompute it, but large-world maintenance is R8/R9 territory.
5. The private oracle is enforced by controller runs, not public CI. That is the accepted cost of D1.
6. Footprint straddle refusal on conversion is conservative and coordinator-approved. Authors align conversion regions or split footprints, guided by R9/R10, until a later authoring decision says otherwise.
7. Refinement cost is bounded per footprint cell, not per validation. Whole-document validation stays a bounded-fixture and transaction operation, and large-world validation remains R8/R9 work.
8. The attempted-pair budget is conservative. A legitimate cell whose clipped polygon counts multiply past 65,536 refuses even when few boxes overlap. That is the accepted simple bound. An explicitly bounded spatial join would be a later, separately approved change.
9. D9 coverage exists only where the importer declares the tag. R11/G2 must declare it on imported exterior footprints, or those cells validate as `missing bound`, which is the safe failure direction.
10. The bilinear bit-equality proof covers tile size 1, the imported recipe. Any other legacy tile size is outside the recipe and refused, not approximated.

## Controller reconciliation, 2026-10-07

- Removed the writer's proposed full 262,144-comparison run. CD7 authorizes early refusal only for
  the 512 by 512 face case. It now charges the mathematical demand and performs zero pair checks.
  A separate 2 by 2 grid proves successful accounting with 64 attempts, 16 positive boxes, 8 faces
  and 9 vertices. No expanded execution or estimate follows.
- Corrected the claim that an anchored float always has an exactly representable fractional part.
  At -2^-30 the remainder is 1 - 2^-30, which the released subtraction rounds to 1f. Task 4 already
  defines MapExactValue.ToSingle as correctly rounded. D9 now explicitly uses that conversion,
  preserving released arithmetic without world-float round trips or geometry snapping. A bitwise
  assertion accompanies the existing legacy oracle test. No new public helper was introduced.
- Made the untagged-legacy classifier row explicit regardless of corner range. It cannot fall through
  to Physical merely because it has ordinary short-range corners.
- Accepted the HalfUnderThird area multiset assertion together with exact vertices, source primitive
  assertions and deterministic near/far output checks. The area listing is not an unverified order
  requirement. Canonical generated key order remains mandatory under D7.
- Confirmed tagged fallback is limited to the declared imported exterior recipe, has no face/normal,
  remains outside candidate enumeration and cannot validate finite caves, holes or unavailable data.
  Invalid recipe has a typed outcome. New resolver-2 bindings require real supported geometry, while
  resolver-1 arithmetic remains its separate compatibility contract.
- Source review only. No task test, runtime command, private oracle or GPU capture has run. The
  complete canonical plan is submitted for separate coordinator review, not execution approval.
