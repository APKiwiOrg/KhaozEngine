# Filtered Tile Installation Implementation Plan

> **For agentic workers:** Use the approved execution method and read the spec before implementation.
> This design-only dispatch authorizes neither execution nor subagent or reviewer dispatch.
> When execution is separately authorized, use the relevant `executing-plans` or
> `subagent-driven-development` skill. Steps use checkboxes for tracking.

**Goal:** Install a consumer-selected tile collider subset with canonical engine provenance and authenticated
ordinary movement registration, and ship supported labelled support-query capture in the same release.

**Architecture:** Add a required-predicate overload to `TileWorldColliders.AddTo` and retain
`TileColliderRegistration` as the sole installation owner. One internal installer snapshots original indices,
copies existing provenance and performs the existing rollback transaction. Ordinary authentication, bounds
capture, conservative resident extent and navigation serialization remain unchanged. A public scoped facade wraps
the existing support recorder, with exclusive caller labels and the existing joined worker-record reduction.

**Tech Stack:** C# on .NET 10, xUnit, KhaozEngine.TileWorld.Physics, Locomotion, Movement and the Bepu test backend.

**Spec:** [FILTERED-TILE-INSTALLATION-2026-10-24.md](../../design/FILTERED-TILE-INSTALLATION-2026-10-24.md).

## Global Constraints

- Baseline `4bb5b0462`, released `v21.12.2`. Reconcile live main through the owning orchestrator before integration.
- Preserve `AddTo(IPhysicsWorld)`, `TileCollider`, every `Build` overload and `TileColliderOptions` signatures.
- Keep one provenance implementation, one bounds implementation and one installation transaction.
- Every filtered registration retains the existing captured `TileWorldColliders.MovementExtent`, unchanged.
- Evaluate the predicate once per collider before any insertion. Store decisions, not the callback.
- Preserve unfiltered collider hashes, registration identities, navigation formats and pinned baseline bytes.
  Historical nav comparisons normalize only the engine-version identity and its lengths, as defined in Task 1.
- Run local builds and tests through `build-slot`. Use `--heavy` for the final full suite and packaging.
- No local load, timing, repeated test loops, live consumer clients, world bakes or baseline growth approvals.
- Use focused new types and test files within KESIZE. No exemption or baseline increase.
- Namespace new tile tests under `KhaozEngine.Tests.TileWorld.Physics`. No new dependency edges are needed.
- Public measurement wraps the existing recorder in KhaozEngine.Physics. No second counter path or consumer friend access.
- Counter/route sums use long throughout. Retention and retry maxima use max, not sum.
- SS-R3 forbids CPU-, SIMD- and schedule-dependent absolute counter pins. All worker fixtures are bounded and untimed.
- Scopes are synchronous and thread-affine. Restore prior diagnostics and preserve physical retention balance.
- This design commit has no version bump. Task 7 chooses the implementation batch's version using current tags.
- Tags, publishing and Grimhollow repinning remain outside this implementation plan's execution authorization.

## Review Focus

- A callback or worker band throws. Installation and diagnostics must unwind their own state and preserve the primary failure.
- Partial placed owners and rebased skewed decks must retain original aliases and absolute transformed bounds.
- Ground is omitted, or everything is omitted. Exclusions name only installed handles and analytic coverage remains.
- Reused handles and static mutation must refuse substitution while supported rebases remain authenticated.
- Nested labels and joined workers must count once, preserve live retention balance and mark partial memory evidence incomplete.

## File responsibilities

`TileWorldColliders.cs` retains the public entry points. A new `TileColliderInstallation.cs` owns selection and
the one add transaction. `TileColliderRegistration.cs` retains lifetime ownership and gains the immutable
index mapping. Its Movement partial consumes compact selected provenance. `TileColliderProvenance.cs`,
`OrdinaryMovementRegistration*.cs` and `PhysicsNavBake*.cs` are reference contracts, not alternate places to
implement filtering. New tests separate compatibility, installation, movement and navigation concerns.

`SupportQueryDiagnostics` remains the only recorder. Extract its existing enum and count value model into
`SupportQueryCounter.cs` and `SupportQueryCounts.cs`, without duplicating them. `SupportQueryCapture.cs` owns
thread scope lifetime. `SupportQueryMeasurement.cs` owns detached labelled results and reduction.
`SupportQueryMetrics.cs` owns the public read-only counter, route and retention models.
`WorkerDiagnostics.cs` alone bridges measurements into the existing Contact navigation workers.
Tasks 1 to 4 remain unchanged. Task 5 owns capture, Task 6 owns parallel and public consumer proofs, and
Task 7 documents and versions both capabilities together. Execute these tasks in order.

### Task 1: Freeze unfiltered installation compatibility

**Files:**

- Create `KhaozEngine.TileWorld.Physics.Tests/TileInstallationCompatibilityTests.cs`.
- Create `KhaozEngine.TileWorld.Physics.Tests/Fixtures/TileInstallationBaseline.json`.
- Create `KhaozEngine.TileWorld.Physics.Tests/Fixtures/TileInstallationBaseline.navbake`.
- Modify `KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj` only to embed those fixtures.
- Modify `KhaozEngine.TileWorld.Physics.Tests/TileWorldNavigationBakeTests.cs` to share its existing fixture inputs.
- Reference `TileMovementRegistrationTests.cs`, `TileWorldNavigationBakeTests.cs` and
  `KhaozEngine.Movement.Tests/ContactNavigationBaselineTests.cs` without replacing their existing expectations.

**Interfaces:**

- Consumes existing `TileWorldColliders.Build(document, catalogs, worldId, options)` and `AddTo(IPhysicsWorld)`.
- Produces embedded pre-feature baseline fixtures and
  `TileInstallationCompatibilityTests.UnfilteredInstallationMatchesPreFeatureBaseline()`.
- Baseline JSON records canonical owner rows, transformed bound float bits, exclusions, extent float bits and
  semantic digest. The nav fixture is the complete `GroundNavigationBake.WriteTo(Stream)` output with only its
  engine-version string and length normalized to `0.0.0-golden`, and its enclosing identity-block length adjusted.
  Every other identity, header and payload byte is pinned. Normalization is a historical test oracle only,
  following `ContactNavigationBaselineTests.NormalizeFile`, and does not change production loading.
  Identify rows and exclusions by original collider index, never by backend-local handle integers.
- Produces `internal static byte[] TileInstallationCompatibilityTests.NormalizeNavigationFile(byte[] file)` for
  Task 3. Validate the file framing, replace only the identity's leading UTF-8 engine-version string and its
  UInt16 length at offset 52, and adjust the header's UInt32 identity length at offset 8. Preserve the remaining
  identity bytes, all other header bytes and the complete payload verbatim. Use public byte operations without
  adding access to Movement internals.
- Make the existing `DeckDoorWorld()`, `Origin`, `Options` and `Profiles` fixture members internal, with their
  current values unchanged, so the compatibility test uses the same inputs without copying their definitions.

- [ ] Add the compatibility test and generate its fixtures using only the old public entry point at this task's
  starting commit, before any production edit. Use `EveryKindWorld()` for collider and owner data, explicit
  world id `filtered-installation-baseline`, and the existing `DeckDoorWorld()`/`Options`/profiles case for
  navigation. Do not rename its existing source keys or change its tuning. Capture actual baseline values once,
  normalize only the nav file's engine-version identity and its lengths as above, commit them, then remove the
  temporary fixture-writing path from the test.
- [ ] Assert the existing EveryKindWorld hash remains
  `6aa23030d83786938861832e5f220a95614c896da011eb21c14ef31a5e651851`. Compare all JSON fields bitwise and the
  entire normalized navigation file bytewise. Keep the existing seven normalized contact-navigation baseline literals.
- [ ] Add `HistoricalNavigationOracleIgnoresOnlyEngineVersion`. Two copies differing only in the engine-version
  string, including different encoded lengths and the adjusted identity-block length, must normalize to the same
  frozen bytes. A version-only release change must pass without regenerating the fixture.
- [ ] Add `HistoricalNavigationOracleRejectsOtherChanges` with bounded mutations of pinned source, option,
  profile, collider, column and graph bytes, plus other header bytes. Each must fail its historical comparison.
  Keep all JSON fields bitwise pinned and preserve every non-version nav byte in the normalizer. Keep existing
  engine-version load-mismatch tests unchanged.
- [ ] Run the new baseline test on unchanged production code. This task establishes a green historical oracle,
  so it does not manufacture a red feature assertion. Later tasks must not regenerate these fixtures.

```csharp
Assert.Equal("6aa23030d83786938861832e5f220a95614c896da011eb21c14ef31a5e651851", actualColliderHash);
Assert.Equal(frozenRegistrationJson, actualRegistrationJson);
Assert.Equal(frozenNavigationBytes, NormalizeNavigationFile(actualNavigationBytes));
```

```bash
build-slot --label filtered-baseline -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter FullyQualifiedName~TileInstallationCompatibilityTests
```

Expected: passing baseline test, no warnings. Review generated fixture sizes and keep the bounded fixtures only.

- [ ] Commit the explicit files with `test(tileworld): pin unfiltered installation compatibility`.

### Task 2: Add filtered installation with canonical movement metadata

**Files:**

- Modify `KhaozEngine.TileWorld.Physics/TileWorldColliders.cs`.
- Create `KhaozEngine.TileWorld.Physics/TileColliderInstallation.cs`.
- Modify `KhaozEngine.TileWorld.Physics/TileColliderRegistration.cs` and its `.Movement.cs` partial as needed for compact metadata.
- Create `KhaozEngine.TileWorld.Physics.Tests/FilteredTileInstallationTests.cs`.
- Create `KhaozEngine.TileWorld.Physics.Tests/FilteredTileMovementRegistrationTests.cs`.
- Extend `TileInstallationCompatibilityTests.cs` and the existing rollback test fixture in `TileWorldPhysicsQueryTests.cs`.
- Modify the test-only `TileWorldMovementNavigationTests.Scene` constructor to accept
  `Func<TileCollider, bool>? include = null` after its existing optional parameters. Null continues to call the
  original overload. Non-null calls the new overload, allowing the same navigation fixture to prove both paths.

**Interfaces:**

- Produces `TileColliderRegistration TileWorldColliders.AddTo(IPhysicsWorld world, Func<TileCollider, bool> include)`.
- Produces `IReadOnlyList<int> TileColliderRegistration.ColliderIndices`.
- Internal single entry point is
  `TileColliderRegistration TileColliderInstallation.Install(IPhysicsWorld world, IReadOnlyList<TileCollider> colliders, TileColliderProvenance[] provenance, string worldId, OrdinaryMovementExtent extent, Func<TileCollider, bool>? include)`.
  Only the internal null predicate means retain all. The public filtered overload rejects null.
- Consumes the existing `TileColliderRegistration` constructors, removal path and
  `OrdinaryMovementRegistration.Builder.Add/Exclude/Build`. No new authentication API.

- [ ] Write these feature tests first and run the two new classes to observe the missing overload/property failure.
  Do not stub the public feature merely to make the tests compile.

| Test | Required assertions |
| --- | --- |
| `PredicateIsCalledOnceInCanonicalAbsoluteOrder` | Callback sequence equals `Colliders`, before any add. Nonzero origin does not alter callback poses. |
| `SelectedHandlesRetainOriginalIndices` | Mixed selection gives strictly increasing retained indices. `Handles.Count == ColliderIndices.Count`. No rejected collider reaches `AddStatic`. |
| `EmptyAndGroundOnlySelectionsRetainResidentExtent` | Empty selection adds nothing, has empty handles and exclusions, and yields `registration.Extent == colliders.MovementExtent`. Ground-only selection excludes all its installed handles. |
| `NullArgumentsAndPredicateFailureDoNotInsertStatics` | Both null-world calls and null predicate throw. A late callback throws the same exception and records zero adds/removes. |
| `SelectedAddFailureRollsBackOnlyAddedHandles` | Inject failure at selected add ordinal 2 with skipped original indices. Exactly two added selected handles are removed. Pre-existing static survives. |
| `SelectedCleanupFailurePreservesExceptionOrderAndRetry` | Add exception is first aggregate entry. Removal retries only failed selected handles. Repeated removal/disposal never removes a successful handle twice. |
| `FilteredOwnersAndGroundExclusionsAreCanonical` | Ground binds Unresolved and is excluded. Retained non-ground binds Known to original `blocked/0/x/z` or `placed/id`. Rejected rows are absent from the expected digest. |
| `PartialPlacedOwnerSelectionDoesNotRenameOrMergeMembers` | Retain one corner edge and one skewed walk surface, omit their siblings. Compare with a manual expected builder using original rows and `0..int.MaxValue`. |
| `FilteredOwnerDigestIgnoresRegionLoadAndHandleAllocationOrder` | Build equivalent regions in reverse load order and preconsume handles in one source. Selected original indices, owner keys and semantic digest agree despite different local handles. |
| `SelectedBoundsStayAbsoluteAndContainTransformedVertices` | At origin `(40.5, 3.25, -24.75)`, compare digest to original captured rows. Independently enumerate selected mesh vertices and rotated-box corners and verify containment. Include an exterior skewed deck and water extent. |
| `FilteredSnapshotsDoNotObserveLaterPolicyOrBuilderChanges` | Flip callback closure and mutate builder after Build. Existing owners, exclusions, semantic digest, Generation and GeometryGeneration stay equal to saved values. No callback runs again. |
| `SameWorldIdAndHandleValuesCannotAuthenticateAnotherSource` | Two Bepu sources use the same world id and overlapping handle values. Own lease is Known, other source is Invalid. Builder.Build rejects a foreign lease. |
| `StaticMutationInvalidatesButStepAndRebaseDoNotRefreshSnapshot` | Step and rebase retain Bepu static generation and authenticate Known. Static add/remove makes old snapshot Unresolved. Published generation properties never change. After local Remove, creating another registration throws. |
| `AllTrueAndDefaultMatchPreFeatureBaseline` | Both overloads match Task 1 owner/bounds/exclusion/extent/digest fixtures. Original indices cover the full range. Build hash is unchanged before and after either installation. |

Representative assertions for the metadata and authentication cases, in their respective test scopes:

```csharp
Assert.Equal(expectedOriginalIndices, installed.ColliderIndices);
Assert.Equal(installed.Handles.Count, installed.ColliderIndices.Count);
Assert.Equal(colliders.MovementExtent, registration.Extent);
Assert.Equal(expectedBuilder.Build(ownLease).SemanticDigest, registration.SemanticDigest);
Assert.Equal(MovementAvailability.Known, registration.Authenticate(ownLease));
Assert.Equal(MovementAvailability.Invalid, registration.Authenticate(otherSourceLease));
Assert.Equal(savedGeneration, registration.Generation);
Assert.Equal(savedGeometryGeneration, registration.GeometryGeneration);
Assert.Equal(MovementAvailability.Unresolved, registration.Authenticate(afterStaticMutation));
```

- [ ] Implement selection in the new installer. Snapshot retained original indices and existing provenance rows
  before insertion. Pass compact provenance and handles to the existing registration. Keep all selected Ground
  rows in provenance and only their handles in exclusions. The retained extent is the supplied original extent.
- [ ] Route the old `AddTo` method through the same installer and remove its old transaction body. Preserve the
  existing public method signature. Expose a read-only copy of the index mapping, retained after removal.
- [ ] Reuse `TileColliderProvenance.Capture` output without new bounds math. Keep source leases and immutable
  generations in the existing ordinary builder. Document callback purity and compact `Handles` semantics in XML.
- [ ] Run the feature, compatibility and existing movement-registration tests. The first run above is red,
  the completed run below must be green. If a registration test is already green once selection is correct,
  retain it as proof rather than distort production code to create another failure cycle.

```bash
build-slot --label filtered-installation -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter 'FullyQualifiedName~FilteredTile|FullyQualifiedName~TileInstallationCompatibilityTests|FullyQualifiedName~TileMovementRegistrationTests|FullyQualifiedName~TileWorldPhysicsQueryTests'
```

Expected: all selected classes pass, zero warnings, original compatibility fixtures unchanged.

- [ ] Stage the exact changed files and commit `feat(tileworld): install filtered colliders with canonical provenance`.

### Task 3: Prove complete capture and filtered contact navigation

**Files:**

- Create `KhaozEngine.TileWorld.Physics.Tests/FilteredTileNavigationTests.cs`.
- Reference `GroundMeshQueryOwnershipTests.cs`, `OrdinaryTileWaterTests.cs` and `TileWorldNavigationBakeTests.cs`.
- Do not add test-only access to `PhysicsNavBake.Columns` or modify `PhysicsNavBake.Capture`.

**Interfaces:**

- Consumes Task 2's overload and index mapping, plus existing `CreateMovementRegistration(string worldId)`.
- Uses public `PhysicsNavBake.Capture`, `GroundNavigationBake.Create` and `WriteTo(Stream)` for capture comparisons.
- Consumes Task 1's frozen normalized nav fixture and `TileInstallationCompatibilityTests.NormalizeNavigationFile`.
- Produces selected-vs-manual-reference navigation proofs and default-vs-all-true byte comparisons.

- [ ] Add `FilteredCaptureMatchesInstalledStaticsRatherThanMovementExclusions`. Build a bounded flat water-bed
  fixture with a Blocked water tile, a retained dry Blocked tile and a selected ground mesh. Apply a test-local
  equivalent of the game's water-material predicate. Assert a downward physical ray sees the water bed rather
  than the omitted box top. It still sees the dry box. The movement view excludes only installed ground.
- [ ] Independently install the same subset in a second Bepu world using a direct canonical loop and one origin
  subtraction. Use equivalent movement registrations through the existing builder. Capture identical bounds,
  sources, classifier and legacy profile in both worlds, serialize with `GroundNavigationBake.WriteTo` and
  `Assert.Equal(referenceBytes, filteredBytes)`. The oracle does not call the new installer. Compare to an
  all-installed control and assert its bytes differ, preventing a ground-only or empty capture false positive.
- [ ] Add `FilteredWaterBedSupportsOrdinaryWadingNavigation`. Build a Contact WadeOnly profile over the selected
  water bed using certified ground, water and boundary from one world identity. Assert a bounded bed-to-bed
  route is Complete and remains below the absent Blocked top. A retained dry obstacle still prevents crossing.
  Use a fresh source-bound registration per source and no game-specific backend wrapper.
- [ ] Add `FilteringPhysicalGroundDoesNotRewriteAnalyticCoverage`. Reject a ground mesh while keeping resident
  analytic coverage. Complete capture has no physical floor in its column. A body querying captured analytic
  ground remains supported there. This pins the difference between physics selection and terrain declarations.
- [ ] Add `PolicySourceDigestChangeRefusesPersistedContactBake`. Use two bounded installations of the same built
  colliders, one omitting the Blocked water collider and one retaining it. Keep complete collider hashes, engine
  version, capture options, all other source entries and Contact tuning identical. Explicitly add a
  `policy/colliders` source through `NavBakeSources.AddHashOf` using different canonical policy-version/input
  bytes. Bake each installation with its matching sources. Both matching loads must return `Loaded`. Loading
  either unchanged file against the other policy's expectation must return `SourcesChanged`. Only the expected
  policy-source digest changes in each refusal check, with no file, graph checksum, engine-version or tuning edit.
- [ ] Add `AllTrueCaptureMatchesDefaultAndHistoricalBytes` using Task 1's navigation fixture and unchanged inputs.
  Compare complete normalized default and all-true files to the frozen fixture through Task 1's helper, which
  normalizes only the engine-version string and its lengths and pins every other identity, header and payload
  byte. Also require complete raw default/all-true file equality in the same build. Keep sources alive and
  unmodified until capture and profile construction finish. Do not regenerate fixtures or weaken existing
  engine-version load-mismatch checks.

```csharp
Assert.Equal(referenceBytes, filteredBytes);
Assert.False(allInstalledBytes.SequenceEqual(filteredBytes));
Assert.Equal(NavPathStatus.Complete, waterBedRoute.Status);
Assert.Equal(frozenNavigationBytes, TileInstallationCompatibilityTests.NormalizeNavigationFile(defaultNavigationBytes));
Assert.Equal(frozenNavigationBytes, TileInstallationCompatibilityTests.NormalizeNavigationFile(allTrueNavigationBytes));
Assert.Equal(defaultNavigationBytes, allTrueNavigationBytes);
```

- [ ] Run the new integration cases once. These can already pass after Task 2, because they prove the seam's
  composition rather than add a second capture implementation. Any failure blocks the feature and belongs in
  Task 2's owning mechanism, with a focused red regression before changing that mechanism.

```bash
build-slot --label filtered-navigation -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter 'FullyQualifiedName~FilteredTileNavigationTests|FullyQualifiedName~TileInstallationCompatibilityTests|FullyQualifiedName~TileWorldNavigationBakeTests'
```

Expected: all selected cases pass and both historical fixtures remain unchanged.

- [ ] Commit `test(tileworld): prove filtered capture and ordinary navigation` with the new test file only.

### Task 4: Pin the historical #1352 traversal at its exact scale

**Files:**

- Create `KhaozEngine.Movement.Tests/AnalyticTerrainRiserTests.cs`.
- Consume `Fixtures/OrdinaryContactScene.cs`. Extend the helper only if test setup requires it.
- Reference `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundCoreTests.Steps.cs` and the current ground core.
- No controller production change is planned from the current non-reproduction assessment.

**Interfaces:**

- Uses `EffectiveMovementBinding.StepTowards(in FramedMovementState state, Vector2 direction, bool run, float dt, MovementQueryLease queries, MovementBoundary? boundary = null)`.
- Uses the corresponding `CharacterMovement.StepTowards` direct path with Contact tuning.
- Produces permanent exact-scale evidence for #1352, separately from tile filtering.

- [ ] Add `HalfMetreAnalyticRiserCrossesAndSettlesPaced` at 30 and 60 Hz for direct and ordinary paths. Set
  `height(x,z) = x < 1 ? 0 : 0.5`, normal `UnitY`, `StepHeight = 0.6`, `RunSpeed = 6`,
  `MaxStepClimbSpeed = 3.5`, half-height `0.75` and radius `0.25`. Start grounded at centre `(0,0.75,0)`.
  Use extent `(-20,-4,-20)..(20,4,20)`, analytic range `0..0.5`, and matching rectangular boundary.
  Run +X for exactly one simulated second. Assert final X exceeds 2, feet settle to 0.5 within contact skin,
  Grounded remains true and each positive feet rise is at most `3.5 * dt + contactSkin`. Compare direct and
  ordinary accepted states each tick. These are fixed simulation steps, not repeated test runs or wall-clock waits.
- [ ] Add `AnalyticRiserAtStepHeightIsAdmittedAndAboveIsBlocked` for rises `0.6` and `0.61` at the same boundary,
  with the analytic extent adjusted to each known rise. Assert the former crosses and the latter stays before x 1.
- [ ] Add `SteepContinuousTerrainRemainsUnclimbable` with height `tan(60 degrees)*x`, its true analytic normal,
  extent `(-20,-40,-20)..(20,40,20)`, analytic range `-35..35` and a 20 degree slope gate.
  Assert no upward walking progress. A continuous slope
  must not be reclassified as a legal riser by a relaxed height test.

```csharp
Assert.True(final.Position.X > 2f);
Assert.InRange(final.Position.Y - 0.75f, 0.5f - contactSkin, 0.5f + contactSkin);
Assert.True(final.Grounded);
Assert.True(positiveFeetRise <= 3.5 * dt + contactSkin);
Assert.Equal(directState, ordinaryState);
```

- [ ] Run the targeted tests. Expected green on the baseline ground algorithm, supported by the already passing
  0.3 m analytic traversal regression. If the exact historical test fails, retain its deterministic evidence
  and stop this task for a root-cause spec amendment. Do not silently introduce a clamp, a looser slope gate,
  more retries or a new climb budget. The design report does not claim the original 0.5 m trace was rerun.

```bash
build-slot --label filtered-analytic-riser -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~AnalyticTerrainRiserTests|FullyQualifiedName~OrdinaryAnalyticGroundTests|FullyQualifiedName~OrdinaryMovementRegistrationTests'
```

Expected: green traversal and authentication tests. A red exact-scale regression blocks consumer handoff.

- [ ] Commit `test(locomotion): pin analytic terrain riser traversal`. Leave #1352 open for its owner and
  Grimhollow's actual terrain audit. Do not close it merely because synthetic cases pass.

### Task 5: Add scoped public support-query capture and labelled reduction

**Files:**

- Create `KhaozEngine.Physics/SupportQueryCounter.cs`, moving the existing enum and making it public.
- Create `KhaozEngine.Physics/SupportQueryCounts.cs`, moving and widening the existing internal count model.
- Create `KhaozEngine.Physics/SupportQueryCapture.cs`, `SupportQueryMeasurement.cs` and `SupportQueryMetrics.cs`.
- Modify `KhaozEngine.Physics/SupportQueryDiagnostics.cs` and `SupportQueryDiagnostics.ProofReuse.cs` for shared
  long storage, scope exchange metadata and live-retention carry, without adding a counting path.
- Create `KhaozEngine.Movement.Tests/SupportQueryCaptureTests.cs`, `SupportQueryMeasurementReductionTests.cs`
  and `SupportQueryCaptureRetentionTests.cs`.
- Modify `KhaozEngine.Movement.Tests/ParallelContactBakeTests.cs` for the internal long-array and boxed-long
  constructor fixtures. Adapt existing diagnostic test callers only where compilation requires the widened types.

**Interfaces:**

- Produces `SupportQueryCapture SupportQueryCapture.Begin(string label)`,
  `SupportQueryMeasurement SupportQueryCapture.Snapshot()` and `void SupportQueryCapture.Dispose()`.
- Produces `SupportQueryMeasurement SupportQueryMeasurement.Combine(IEnumerable<SupportQueryMeasurement> measurements)`,
  `SupportQueryMetrics Total` and `IReadOnlyDictionary<string, SupportQueryMetrics> ByLabel`.
- Produces `long SupportQueryMetrics.this[SupportQueryCounter counter]`, `SupportQueryRouteMetrics Routes`
  and `SupportProofReuseMetrics ProofReuse`. FI-8 fixes all route getters and the retention getters
  `long PeakKeys`, `long PeakBytes` and `bool HasCompleteLifetimes`.
- Uses existing `SupportQueryDiagnostics.Record(SupportQueryCounter counter, int count = 1)`, `Snapshot`,
  `Exchange`, `Merge` and `ProofReuseRetention`. Widen their internal storage/results, not the call-site count
  argument or per-session budgets. Preserve all existing public movement and physics signatures.
- Supplies Task 6 with internal thread-state label/completeness metadata, immutable result reduction and the
  zero-copy ownership rule for saving/restoring the old recorder. No public mutation or observer hook is added.

- [ ] Add these named tests before production edits. Confirm the missing public API failure using the focused
  command below. Do not add implementation stubs to conceal the initial red result.

| Test | Required assertions |
| --- | --- |
| `EveryPublicCounterReadsTheExistingRecorderAsLong` | For each of the 42 enum values, inject a distinct known count through the internal fixture and read it through the public indexer. Pin existing enum identities 0 through 41. Seed Find just below int.MaxValue, increment through Record, and assert the public long value crosses that boundary without wrap. |
| `NestedLabelsCountEachOperationOnce` | Record direct work under `player`, then nested `player/preflight`, then `cow`. Public Total equals the sum of exclusive rows. Parent Snapshot contains child work once. A sibling with the same label coalesces. Earlier snapshots do not mutate. |
| `CaptureRestoresEveryPriorValueWhenMeasuredWorkThrows` | Seed enabled and disabled prior states, all callbacks/observations, routes and an existing array. Throw inside using. Verify original state and array identity/content, callback identity and parent label are restored, with no measured counters leaking. Final partial result remains inspectable. |
| `ScopeMisuseDoesNotCorruptTheActiveRecorder` | Null/blank labels reject. A different thread's Snapshot/Dispose and out-of-order disposal/parent Snapshot throw before mutation. Correct LIFO close still succeeds. Repeated Dispose does not merge a child twice. |
| `DetachedMeasurementsCombineInOrdinalOrder` | Combine disjoint results in both orders and both associative groupings. All counters and normal routes sum, maximum retries and both retention peaks take max, completeness uses AND, keys are ordinal and case-sensitive. Empty input has zero totals and no labels. Undefined enum and null inputs reject. |
| `CaptureAndCombinationRefuseLongOverflow` | Known synthetic records at long.MaxValue plus one throw OverflowException in enabled recorder/merge or public Combine. They never expose wrapped negative totals. |
| `RetentionBalanceSurvivesNestedAndDisabledScopeBoundaries` | Use a bounded real source reuse session. Start/store before an inner capture and release inside it, then the reverse acquisition/release ordering. Restored live Keys/Bytes equal actual retained storage and finish at zero, including initially disabled diagnostics. No stale saved balance reappears. |
| `RetentionPeaksDistinguishCompleteAndPartialLifetimes` | A capture enclosing store/reset/dispose reports positive peaks, zero final live balance and complete lifetimes. An interval entering or ending with retained storage is incomplete even if it sees no growth. Public peaks equal that interval's own internal retention oracle. No hard-coded element-size or byte peak. |
| `DisabledThreadsAndPriorObserversStayIsolated` | A capture changes only its own thread. Fresh disabled threads remain disabled, counters do not increment and capture callbacks are suspended/restored. Snapshots allocate only on explicit capture paths. No test uses a clock or timing threshold. |

```csharp
Assert.Equal(expectedExistingRecorderValue, measured.Total[counter]);
Assert.Equal(measured.Total[SupportQueryCounter.Find],
    measured.ByLabel.Values.Sum(row => row[SupportQueryCounter.Find]));
Assert.True(measured.Total[SupportQueryCounter.Find] > int.MaxValue);
Assert.Equal(Math.Max(leftPeak, rightPeak), combined.Total.ProofReuse.PeakBytes);
Assert.False(partial.Total.ProofReuse.HasCompleteLifetimes);
Assert.Same(priorArray, restored.Counts);
```

```bash
build-slot --label support-query-capture -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportQueryCapture|FullyQualifiedName~SupportQueryMeasurementReductionTests'
```

- [ ] Move the enum/count definitions and implement the FI-8 immutable facade over those existing counts.
  Pin enum numeric identities and internal constructors. Widen the single recorder, routes, retention and
  arithmetic before exposing values. Fix internal fixture boxing and count-array types at their source.
- [ ] Implement FI-9 scope stacking and detached snapshots in the focused capture/result types. Keep dictionary
  grouping and allocations at scope/snapshot/reduction boundaries, not in Record. Restore all prior state in
  finally, carrying only the live retention change rather than restoring an obsolete physical balance.
- [ ] Implement FI-10 retention peak and completeness semantics using the existing retention events and baseline.
  Do not add a per-memo registry or change reuse budget, evaluation, identities or source disposal behavior.
- [ ] Run the focused command once after implementation. Also run the existing internal diagnostics and reuse
  regressions with the command below. Expected all selected tests pass, zero warnings and no frozen navigation
  fixture changes. Inspect disabled paths to confirm their existing gates and sole retention update remain.

```bash
build-slot --label support-query-existing-diagnostics -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ParallelContactBakeTests|FullyQualifiedName~ContactNavigationProofReuseTests|FullyQualifiedName~ContactNavigationProofReuseScheduleTests'
```

- [ ] Stage explicit changed files and commit `feat(physics): expose scoped support-query measurement`.

### Task 6: Add labelled parallel capture and public consumer proofs

**Files:**

- Modify `KhaozEngine.Physics/WorkerDiagnostics.cs` for immutable label/completeness propagation and existing reduction.
- Modify `KhaozEngine.Movement/ContactNavigationWorkers.cs` for the record handoff, disabled record-allocation
  guard and existing join/failure paths. Do not change its scheduler, graph algorithm or cleanup/failure precedence.
- Create `KhaozEngine.Movement.Tests/PublicSupportQueryBakeTests.cs` and `SupportQueryWorkerRestorationTests.cs`.
- Create `KhaozEngine.TileWorld.Physics.Tests/PublicSupportQueryMeasurementTests.cs`. This assembly has no
  Physics friend access. Use its existing fixture composition and references, without adding a friend grant.
- Reference `ParallelContactBakeTests.cs`, `ContactNavigationProofReuseTests.cs` and the bounded navigation fixtures.

**Interfaces:**

- Consumes the exact Task 5 public signatures and internal label/completeness metadata from FI-9/FI-10.
- Appends `string? MeasurementLabel = null` to internal `WorkerDiagnosticsPlan`, and
  `string? MeasurementLabel = null, bool HasCompleteLifetimes = true` to `WorkerDiagnosticsRecord`. Existing count, route and retention fields stay
  the single worker measurement payload. Optional new internal parameters preserve existing internal callers.
- Preserves `WorkerDiagnostics.Plan()`, `WorkerDiagnosticsScope.Enter(in WorkerDiagnosticsPlan plan, Action<ContactAllocationPhase, bool>? observer)`,
  `WorkerDiagnosticsRecord WorkerDiagnosticsScope.Exit()` and `WorkerDiagnostics.Merge(ReadOnlySpan<WorkerDiagnosticsRecord> records)`.
- Adds internal `void WorkerDiagnosticsScope.Restore()` as the single restoration helper used by Exit and the
  disabled path, which skips widened record allocation/materialization/reduction. Preserve internal allocation observers.
- Produces public-only bounded Contact capture evidence. Grimhollow consumes no internal worker APIs.

- [ ] Write the following tests. Run the first focused command to expose absent worker metadata/restoration
  behavior before changing the bridge. Cases already green after Task 5 remain composition evidence.

| Test | Required assertions |
| --- | --- |
| `PublicCaptureCollectsEveryJoinedWorkerUnderItsBodyLabel` | Bake a small real Contact profile under `player` at degrees 1 and 3, explicitly enabling stable Hold reuse for both. Compare graph bytes with capture off. At the caller join, compare public metrics to that run's own internal worker-plus-caller oracle and assert positive Find, bindings, physics/movement leases, reuse work and session peaks. Label totals account for all records once. |
| `WorkerCountPreservesLogicalProofWorkAndReportsRealSetup` | Use equivalent warmed worlds and matching explicit reuse policies. Degree 1/3 graph bytes, Find and EdgeSteps agree in the same test. Raw binding/pin/lease differences agree with the actual worker setup events, not an absolute CPU-dependent literal. Each run's public peaks match its own oracle and complete lifetimes are true. Do not assert equal peaks or all counters between degrees. |
| `WorkerRecordReductionIsOrderAndGroupingIndependent` | Feed known disjoint records in forward/reverse order and associative groupings. Compare every public counter/route, ordinal label and max/completeness result exactly. Read records once per reduction. This pins arithmetic, not a physics work-count baseline. |
| `FailedWorkerBuildMergesPartialWorkAndRestoresAllThreads` | Inject one bounded worker-band failure using existing build hooks. The selected original failure propagates after join. Every started worker contributes once, no unstarted worker contributes, partial counters/retention remain honest and caller/worker prior state and callbacks restore. A later bounded build starts clean. Preserve cleanup failure precedence. |
| `DisabledBakeDoesNotInstallPublicCaptureOrChangeBytes` | Without Begin, serial/parallel output equals captured output. Existing disabled state and callbacks restore, no label state appears, no capture objects, dictionaries or widened worker-record arrays are created by the disabled path. The existing internal allocation observers still run. Inspect the bridge guard as well as asserting state. |
| `NonFriendConsumerCapturesMovementAndCombinesBodyProfiles` | In TileWorld.Physics.Tests use only public physics/movement APIs to capture real Hold/Step work under two caller-supplied labels, including a nested preflight scope. Find and lease counters are positive, Total equals exclusive labels, immutable results combine in ordinal order and both scopes restore. Compile proves no friend access or reflection is needed. |

```bash
build-slot --label support-query-parallel -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~PublicSupportQueryBakeTests|FullyQualifiedName~SupportQueryWorkerRestorationTests|FullyQualifiedName~ParallelContactBakeTests'
build-slot --label support-query-public-consumer -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter 'FullyQualifiedName~PublicSupportQueryMeasurementTests|FullyQualifiedName~FilteredTileNavigationTests|FullyQualifiedName~TileInstallationCompatibilityTests'
```

- [ ] Extend the existing worker plan/scope/record path to carry the captured label and completeness baseline,
  isolate all worker state and merge records after every started thread joins. Keep caller observer delegates
  private to the caller, with the pre-existing explicit internal test opt-ins unchanged. Never transfer a
  mutable public capture object or an ExecutionContext to a worker.
- [ ] Preserve the current serial path. Attribute caller setup/pins and worker work once, using the original
  immutable build label for worker records. Restore live retention deltas even on exceptional exit. No support
  or controller increment call site changes are needed to collect workers. Reuse Restore for the disabled
  path and gate widened worker-record allocation/materialization/reduction on the existing Enabled flag.
- [ ] Run both focused commands once after implementation. Expected green, zero warnings, exact graph bytes
  and no SS-R3-dependent absolute work pins. These small fixed-fixture checks authorize no timing or load.
- [ ] Stage explicit changed files and commit `feat(physics): aggregate labelled worker support measurements`.

### Task 7: Document both public APIs, select the version and verify the batch

**Files:**

- Modify `KhaozEngine.TileWorld.Physics/README.md`, `KhaozEngine.Physics/README.md`,
  `KhaozEngine.Movement/README.md`, `docs/USING-KHAOZENGINE.md` and this spec's indexed status.
- Modify `Directory.Build.props`, `CHANGELOG.md` and every declaration selected by `scripts/check-doc-versions.sh`.
- Modify `docs/INDEX.md` only to reflect the implementation's actual status.

**Interfaces:**

- Documents the exact Task 2 and Task 5 APIs, FI-1 to FI-11 contracts, Task 4 traversal result and Task 6 worker proof.
- Produces one version/changelog commit and a verified branch for the owning orchestrator.

- [ ] Add an example calling `colliders.AddTo(physics, include)` with a neutral policy. Explain original-index
  mapping, absolute predicate poses, preserved resident extent, selected-only ground exclusions, source lifetime
  and the complete-set meaning of `colliders.Hash`. Require an explicit installation-policy source in
  `NavBakeSources` for both bake creation and the runtime `NavBakeExpectation`. Show
  `sources.AddHashOf("policy/colliders", policyBytes)` with canonical policy-version/input bytes, and require its
  digest to change when those semantics or inputs change. State that Contact registration, live environment
  identity, format 2 and runtime profile validation do not automatically persist this entry or replace it.
  Document Task 3's matching-load and policy-only `SourcesChanged` proof. Keep Grimhollow's material policy in Grimhollow.
- [ ] Add a public-only capture example with two body labels, using/finally restoration, Snapshot and Combine.
  Explain synchronous thread ownership, exclusive nested attribution, ordinal labels, cumulative snapshots,
  disjoint combination, worker aggregation and actual worker overhead. Define max-per-session retention bytes,
  completeness and diagnostic allocation overhead. Require complete source lifetimes for bake peak evidence.
  State that the API supplies counts, not timers or process RSS. Keep approved hosted timing/bake gates intact.
- [ ] Sweep every Markdown file for `TileWorldColliders`, `TileColliderRegistration`, `AddTo`, `GroundHandles`
  and the former all-colliders/index-for-index wording. Also sweep `SupportQueryDiagnostics`, `SupportQueryCounter`,
  `SupportQueryCapture`, `SupportQueryMeasurement`, `SupportProofReuse`, worker diagnostics and private-only
  measurement wording across every Markdown file. Update living references, preserve dated history.
- [ ] Re-read live main, `<KhaozEngineVersion>` and newest release tags. Ride a staged version if present.
  Otherwise this additive API takes the next free minor, nominally `21.13.0` only if `21.12.2` is still current
  and released. Do not assume that number is free. Put the version and matching newest changelog entry in the
  same commit, and update the guarded version declarations in that commit. The changelog distinguishes the
  filtered seam and supported measurement from unchanged default output, and reports #1352 only to the extent
  actually proven. Both additive APIs ride this one selected version. Do not regenerate Task 1's fixtures or
  refresh the seven existing contact-navigation baselines for version selection. The unchanged historical oracle
  must pass a version-only change through Task 1's engine-version normalization. Every other pinned byte and
  same-build raw default/all-true equality remain required, as do existing engine-version load refusals.
- [ ] After the orchestrator reconciles current main into the branch, run the final Release verification once.
  Preserve full command exit statuses. Do not loop or turn a tracked failure into an unrecorded retry.

```bash
mkdir -p local-feed
build-slot --label filtered-release-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label filtered-release-tests -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

Expected: zero build warnings, no new or changed test failures, unchanged compatibility fixtures and baseline
hashes, all guards exit 0. GPU, timing and hosted stress proofs are not implied by this result.

- [ ] Stage explicit documentation/version paths, run staged dash, prose and file-size checks plus
  `git diff --cached --check`, then commit `release(<selected-version>): stage filtered installation and measurement`.
  Use the numeric version verified in the preceding step as the commit scope.
- [ ] Return branch commits, enum/API contract, scoped enablement and restoration proof, worker/attribution and
  retention semantics, non-friend consumer proof and verification evidence to the owning orchestrator. The final
  release handoff must supply one exact tag/SHA/package/tool identity for Grimhollow Tasks 13, 15 and 17.
  Installer availability alone does not satisfy CA-R3. Integration and pushing
  follow the engine's release-lock rules there. Any pre-merge package proof uses
  `KHAOZENGINE_FEED=/tmp/khaozengine-filtered-tile-installer/feed` and
  `build-slot --heavy --label filtered-private-pack -- scripts/pack-local-feed.sh`.
  Shared-feed packing happens only from clean main after origin/main contains the integrated commit.
  No tag, published release or consumer repin is authorized by this plan.
