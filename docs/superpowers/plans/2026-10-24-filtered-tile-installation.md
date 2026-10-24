# Filtered Tile Installation Implementation Plan

> **For agentic workers:** Use the approved execution method and read the spec before implementation.
> This design-only dispatch authorizes neither execution nor subagent or reviewer dispatch.
> When execution is separately authorized, use the relevant `executing-plans` or
> `subagent-driven-development` skill. Steps use checkboxes for tracking.

**Goal:** Install a consumer-selected tile collider subset with canonical engine provenance and authenticated
ordinary movement registration, so complete physics and navigation capture observe the same installed set.

**Architecture:** Add a required-predicate overload to `TileWorldColliders.AddTo` and retain
`TileColliderRegistration` as the sole installation owner. One internal installer snapshots original indices,
copies existing provenance and performs the existing rollback transaction. Ordinary authentication, bounds
capture, conservative resident extent and navigation serialization remain unchanged.

**Tech Stack:** C# on .NET 10, xUnit, KhaozEngine.TileWorld.Physics, Locomotion, Movement and the Bepu test backend.

**Spec:** [FILTERED-TILE-INSTALLATION-2026-10-24.md](../../design/FILTERED-TILE-INSTALLATION-2026-10-24.md).

## Global Constraints

- Baseline `4bb5b0462`, released `v21.12.2`. Reconcile live main through the owning orchestrator before integration.
- Preserve `AddTo(IPhysicsWorld)`, `TileCollider`, every `Build` overload and `TileColliderOptions` signatures.
- Keep one provenance implementation, one bounds implementation and one installation transaction.
- Every filtered registration retains the existing captured `TileWorldColliders.MovementExtent`, unchanged.
- Evaluate the predicate once per collider before any insertion. Store decisions, not the callback.
- Preserve unfiltered collider hashes, registration identities, navigation formats and pinned baseline bytes.
- Run local builds and tests through `build-slot`. Use `--heavy` for the final full suite and packaging.
- No local load, timing, repeated test loops, live consumer clients, world bakes or baseline growth approvals.
- Use focused new types and test files within KESIZE. No exemption or baseline increase.
- Namespace new tile tests under `KhaozEngine.Tests.TileWorld.Physics`. No new dependency edges are needed.
- This design commit has no version bump. Task 5 chooses the implementation batch's version using current tags.
- Tags, publishing and Grimhollow repinning remain outside this implementation plan's execution authorization.

## Review Focus

- A callback throws late or its closure later changes. Installation must leave no partial set or deferred policy.
- A selector removes one wall-corner edge. The retained edge must keep the original shared placed owner.
- A rotated walk surface sits outside a resident region in a rebased world. Its absolute bounds must remain canonical.
- Ground is omitted, or everything is omitted. Exclusions must name only installed handles and analytic coverage must remain.
- Handles are reused in another source or after mutation. Authentication must refuse substitution while preserving supported rebases.

## File responsibilities

`TileWorldColliders.cs` retains the public entry points. A new `TileColliderInstallation.cs` owns selection and
the one add transaction. `TileColliderRegistration.cs` retains lifetime ownership and gains the immutable
index mapping. Its Movement partial consumes compact selected provenance. `TileColliderProvenance.cs`,
`OrdinaryMovementRegistration*.cs` and `PhysicsNavBake*.cs` are reference contracts, not alternate places to
implement filtering. New tests separate compatibility, installation, movement and navigation concerns.

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
  semantic digest. The nav fixture is the complete `GroundNavigationBake.WriteTo(Stream)` output.
  Identify rows and exclusions by original collider index, never by backend-local handle integers.
- Make the existing `DeckDoorWorld()`, `Origin`, `Options` and `Profiles` fixture members internal, with their
  current values unchanged, so the compatibility test uses the same inputs without copying their definitions.

- [ ] Add the compatibility test and generate its fixtures using only the old public entry point at this task's
  starting commit, before any production edit. Use `EveryKindWorld()` for collider and owner data, explicit
  world id `filtered-installation-baseline`, and the existing `DeckDoorWorld()`/`Options`/profiles case for
  navigation. Do not rename its existing source keys or change its tuning. Capture actual baseline values once,
  commit them, then remove the temporary fixture-writing path from the test.
- [ ] Assert the existing EveryKindWorld hash remains
  `6aa23030d83786938861832e5f220a95614c896da011eb21c14ef31a5e651851`. Compare all JSON fields bitwise and the
  entire navigation file bytewise. Keep the existing seven normalized contact-navigation baseline literals.
- [ ] Run the new baseline test on unchanged production code. This task establishes a green historical oracle,
  so it does not manufacture a red feature assertion. Later tasks must not regenerate these fixtures.

```csharp
Assert.Equal("6aa23030d83786938861832e5f220a95614c896da011eb21c14ef31a5e651851", actualColliderHash);
Assert.Equal(frozenRegistrationJson, actualRegistrationJson);
Assert.Equal(frozenNavigationBytes, actualNavigationBytes);
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
- [ ] Add `AllTrueCaptureMatchesDefaultAndHistoricalBytes` using Task 1's navigation fixture, unchanged inputs and
  complete byte comparison. Keep sources alive and unmodified until capture and profile construction finish.

```csharp
Assert.Equal(referenceBytes, filteredBytes);
Assert.False(allInstalledBytes.SequenceEqual(filteredBytes));
Assert.Equal(NavPathStatus.Complete, waterBedRoute.Status);
Assert.Equal(frozenNavigationBytes, allTrueNavigationBytes);
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

### Task 5: Document the seam, select the version and verify the batch

**Files:**

- Modify `KhaozEngine.TileWorld.Physics/README.md`, `docs/USING-KHAOZENGINE.md` and this spec's indexed status.
- Modify `Directory.Build.props`, `CHANGELOG.md` and every declaration selected by `scripts/check-doc-versions.sh`.
- Modify `docs/INDEX.md` only to reflect the implementation's actual status.

**Interfaces:**

- Documents the exact Task 2 API, its FI-1 to FI-6 contracts and the Task 4 result.
- Produces one version/changelog commit and a verified branch for the owning orchestrator.

- [ ] Add an example calling `colliders.AddTo(physics, include)` with a neutral policy. Explain original-index
  mapping, absolute predicate poses, preserved resident extent, selected-only ground exclusions, source lifetime
  and the complete-set meaning of `colliders.Hash`. State that persisted filter policy inputs need their own
  navigation source identity. Keep Grimhollow's material policy in Grimhollow.
- [ ] Sweep every Markdown file for `TileWorldColliders`, `TileColliderRegistration`, `AddTo`, `GroundHandles`
  and the former all-colliders/index-for-index wording. Update living reference text, preserve dated history.
- [ ] Re-read live main, `<KhaozEngineVersion>` and newest release tags. Ride a staged version if present.
  Otherwise this additive API takes the next free minor, nominally `21.13.0` only if `21.12.2` is still current
  and released. Do not assume that number is free. Put the version and matching newest changelog entry in the
  same commit, and update the guarded version declarations in that commit. The changelog distinguishes the
  filtered seam from unchanged default output and reports #1352 only to the extent actually proven.
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
  `git diff --cached --check`, then commit `release(<selected-version>): stage filtered tile installation`.
  Use the numeric version verified in the preceding step as the commit scope.
- [ ] Return the branch commits and verification evidence to the owning orchestrator. Integration and pushing
  follow the engine's release-lock rules there. Any pre-merge package proof uses
  `KHAOZENGINE_FEED=/tmp/khaozengine-filtered-tile-installer/feed` and
  `build-slot --heavy --label filtered-private-pack -- scripts/pack-local-feed.sh`.
  Shared-feed packing happens only from clean main after origin/main contains the integrated commit.
  No tag, published release or consumer repin is authorized by this plan.
