# Analytic Terrain Query Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans to implement each assigned task. Root assigns execution through subagent-driven-development and reviews each gate. Do not spawn children in this lane.

**Goal:** Resolve engine #1233 by keeping analytic terrain in charge of movement while retaining ground geometry in the complete physics world for capture and dynamics.

**Architecture:** A non-owning `IPhysicsWorldQueryView` excludes stable ground seam handles from every movement query over the same Bepu simulation. TileWorld exposes the registration's selection. `GroundMoveContext` retains complete capture `Physics`, uses the optional view only for movement and preserves it in dry traversal proofs.

**Tech Stack:** C#/.NET 10, System.Numerics, the dependency-free Physics seam, the existing opt-in BepuPhysics 2.4 backend and xUnit headless tests.

**Spec:** [ANALYTIC-TERRAIN-QUERY-OWNERSHIP-DESIGN-2026-10-03.md](../../design/ANALYTIC-TERRAIN-QUERY-OWNERSHIP-DESIGN-2026-10-03.md).

## Global constraints

- Root approved technical option A, the design and this plan on 2026-10-03 within the owner-approved pivot scope. Independent review approved implementability without findings. No implementation proof or release is claimed.
- Worktree `~/KhaozEngine/.worktrees/grimhollow-ground-slopes`, branch `fix/grimhollow-ground-slopes`, original regression HEAD `1f23538ecd3abb829f366131dbb5bcf0d7207da6`. Each dispatch gives the absolute worktree and current reconciled starting HEAD.
- Preserve the pushed regression and its meaningful assertions. Actual RED is root's compiled exit-1 run in `/tmp/grimhollow-ground-slopes-red.log`. Do not rerun it merely to recreate known evidence.
- Preserve unrelated work. Inspect changed-path ownership before editing. Do not touch #1225 tile raycast rules or #913 validation-spec paths.
- No query layers, movement math, capsule tuning, slope model or navigation algorithm redesign. The unfiltered query path and callers omitting a view retain their current semantics.
- Source ownership is exact reference identity, including logical decorators. The view follows the same source's live origin and never owns its simulation lifecycle.
- Ride staged 20.18.0. No engine tag, game pin, ref operation, push, merge, pack or cleanup is assigned to the implementer here.
- Every build/test/format/pack/GPU command needs separate root clearance and the implementer's separate inspected process gate. One shared Mac CPU slot. Commands run synchronously and exit codes are recorded.
- No repeated test-command loop, stress proof or client boot. Root runs final combined engine verification after reconciliation.
- No em/en dash glyphs or prose semicolons. Keep warnings at zero. No file-size baseline increase or hook bypass.
- Implementation starts only through root's bounded task dispatch. Workers stop at their reviewed task boundaries and do not integrate their own changes.

## Review focus

1. A decorator returns a backend-root view with the wrong logical `SourceWorld`, or a second world shares the same numeric origin. Task 3 tests exact identity refusal and correct decorator identity.
2. An excluded Bepu handle is recycled, or the caller mutates its input array. Task 1 tests stable seam selection and snapshot ownership.
3. An excluded nearest hit hides an allowed farther hit, or excluded ground wins the deepest penetration. Task 1 tests candidate filtering before ray/sweep narrowing and penetration batching.
4. Idle or steep terrain becomes prop support because only sweeps were filtered. Task 2 tests all-query selection through idle and analytic slide parity.
5. A medium-bearing context loses selection in its dry proof clone, erasing routes or disagreeing with runtime movement. Task 3 tests complete capture with selected dry movement proofs.

## File and responsibility map

| Files | Responsibility |
| --- | --- |
| `KhaozEngine.Physics/IPhysicsWorld.cs`, new `IPhysicsWorldQueryView.cs` in that package | Additive unsupported-by-default factory and logical source identity |
| `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.cs`, new `BepuPhysicsWorld.Queries.cs` | Move the existing three query methods into their owning partial module, add shared cores with optional exclusions, preserve unfiltered math |
| New `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.QueryView.cs` | Factory validation, view operations, denied mutations and view-only disposal |
| New `KhaozEngine.Physics.Bepu/StaticQueryExclusions.cs`, existing `HitHandlers.cs` | Snapshot stable seam handles and gate live candidates before query resolution |
| `KhaozEngine.TileWorld.Physics/TileWorldColliders.cs`, `TileColliderRegistration.cs` | Preserve full registration and expose the exact Ground subset as movement selection |
| `KhaozEngine.Movement/GroundMoveContext.cs`, `GroundMoveContext.Coordinates.cs`, `GroundTraversalProbe.cs` | Separate capture and movement inputs while preserving origin and dry-proof contracts |
| New `KhaozEngine.Game.Tests/Physics/StaticQuerySelectionTests.cs`, `PhysicsQueryViewLifecycleTests.cs` | Backend selection and non-owning capability contracts |
| `KhaozEngine.TileWorld.Physics.Tests/GroundMeshSlopeMovementTests.cs`, `CharacterOnTileWorldTests.cs`, new `GroundMeshQueryOwnershipTests.cs` | Keep the observed RED expectations and prove composed terrain/solid boundaries |
| `KhaozEngine.Movement.Tests/GroundMoveContextTests.cs`, new `GroundMoveQuerySelectionTests.cs`, `KhaozEngine.TileWorld.Physics.Tests/TileWorldMovementNavigationTests.cs` | Context identity, selected origin and complete-world capture/dry proof integration |

No new project or project dependency is needed. Keep test namespaces `KhaozEngine.Tests.*`. Put new fixtures in the named files rather than growing the 299-line diagnostic fixture with another concern.

## Verification gate used by every task

Before each proposed command below, obtain the separate root clearance, then inspect `ps -axo pid,ppid,etime,pcpu,command` yourself. Confirm no build, test, format, pack, GPU/client or runner work occupies the shared slot. If occupied, return control to root without launching the command. Do not infer clearance from elapsed time or an earlier gate.

Run the chosen command synchronously from the guarded root. Do not use `--no-build` for new or changed tests. Capture the exit code, discovered count, expected failure or pass and relevant output. Compile errors, missing dependencies and an empty filter are not behavioral RED or GREEN. One RED and one GREEN per task are the intended bounded cycle. A new failing concern returns to diagnosis and a new root clearance, not a command loop.

### Task 1: Complete same-world static query selection

**Files:** Create the two Physics/Bepu interfaces/modules and backend test files in the map. Modify `IPhysicsWorld.cs`, `BepuPhysicsWorld.cs` and `HitHandlers.cs`. `BepuPhysicsWorld.Queries.cs`, `BepuPhysicsWorld.QueryView.cs` and `StaticQueryExclusions.cs` are new files.

**Interfaces:**

- Produces `IPhysicsWorldQueryView : IPhysicsWorld` with `IPhysicsWorld SourceWorld { get; }`.
- Produces public default `IPhysicsWorldQueryView IPhysicsWorld.CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> excludedStatics)` whose default throws `NotSupportedException`.
- Bepu implements the same public signature. Its snapshot validates each requested handle against the live `_handles` static seam map. Duplicates collapse, empty input returns a view, absent/stale IDs throw `ArgumentException` naming `excludedStatics`.
- Private query-core signatures in `BepuPhysicsWorld.Queries.cs` are `bool RaycastCore(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter, StaticQueryExclusions? exclusions)`, `bool SweepCapsuleCore(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter, StaticQueryExclusions? exclusions)` and `unsafe bool ComputePenetrationCore(CapsuleShape capsule, Pose pose, out Vector3 mtv, StaticQueryExclusions? exclusions)`.
- `StaticQueryExclusions` snapshots requested seam handles and uses the owner's live reverse map for `bool Allows(CollidableReference collidable)`. Dynamic candidates pass this static gate. Existing mobility gating remains independent.

- [ ] **Step 1: Add compilable declarations and the failing selection tests.** Add the marker interface and unsupported default factory only. Leave Bepu without its factory override for this RED. Invoke the new API through `IPhysicsWorld`, since default interface members are not concrete-class methods. In `StaticQuerySelectionTests`, add `ExcludedNearestRayStillFindsAllowedStatic`, `ExcludedNearestSweepStillFindsAllowedStatic`, `ExcludedDeepestOverlapStillFindsAllowedWall`, `CompoundRootExclusionAppliesToChildren` and `ExclusionsRetainQueryMobility`.

For ray/sweep selection, use boxes with half-extents `(0.5, 0.5, 0.5)` at `(2, 0, 0)` and `(5, 0, 0)`, a +X query from zero over 8 m, and a radius-0.3, length-0.9 capsule for the sweep. Exclude the nearer box, require the farther box's seam handle, then require the nearer handle through the owner. For penetration, use that capsule at `(0, 0.75, 0)`, an excluded floor box with half-extents `(4, 0.25, 4)` at `(0, 0.25, 0)`, and an allowed wall box with half-extents `(0.25, 2, 4)` at `(0.4, 1, 0)`. Require the selected MTV to match an independently queried wall-only reference world within 0.00003 m, while the complete world's MTV differs. Test an excluded compound root, not individual child IDs. Mobility assertions keep static/dynamic/all filtering and nullable dynamic `Body` semantics intact.

- [ ] **Step 2: Add lifecycle and unsupported-backend tests.** In `PhysicsQueryViewLifecycleTests`, add `InputSnapshotSurvivesArrayMutation`, `RemovedExcludedHandleDoesNotExcludeReplacement`, `EmptySelectionReturnsViewWithOwnerQueryParity`, `MissingOrStaleStaticIsRejected`, `DefaultFactoryIsExplicitlyUnsupported`, `ViewMutationsCannotTouchOwner`, `ViewDisposalLeavesOwnerUsable`, `DisposedViewRejectsOperationalCalls` and `ViewReadsOwnerOriginAfterRebase`.

Test every denied live operation: `AddStatic(PhysicsShape, Pose, PhysicsMaterial?)`, `RemoveStatic(StaticHandle)`, `AddDynamic(PhysicsShape, Pose, DynamicBodyDescription, PhysicsMaterial?)`, `RemoveDynamic(DynamicBodyHandle)`, `SetDynamicVelocity(DynamicBodyHandle, Vector3, Vector3)`, `AddConstraint(in ConstraintDescription)`, `RemoveConstraint(ConstraintHandle)`, `SetConstraintTarget(ConstraintHandle, float)`, `Step(float)`, `Rebase(Vector3)` and the factory on a view. Each throws `NotSupportedException` before changing source results, poses or origin. Dynamic read methods forward correctly. View disposal is idempotent, leaves the owner queryable/steppable, and operational calls including `Origin` then throw `ObjectDisposedException`. `SourceWorld` stays inspectable and `CanRebase` stays false. A minimal existing-style test double uses the default factory and must throw for both empty and nonempty selection.

- [ ] **Step 3: Run the separately cleared RED command once.**

```sh
dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~StaticQuerySelectionTests|FullyQualifiedName~PhysicsQueryViewLifecycleTests" --logger "console;verbosity=detailed"
```

Expected: compiled tests fail when Bepu still uses the unsupported default factory. The explicit unsupported-backend fact passes. Record the nonzero exit and exact exception. The existing slope RED remains prior evidence for the consumer failure.

- [ ] **Step 4: Implement the backend selection and view.** Move existing query methods into their owning query partial without changing their numeric behavior. Public methods call the cores with `exclusions: null`. Give ray/sweep handlers optional `StaticQueryExclusions`, applying mobility AND exclusions in both `AllowTest` forms. Penetration skips excluded static candidates before `batcher.AddDirectly`. Preserve manifold sign, deepest-positive selection and `finally` cleanup. This is new filtered-penetration behavior, not an existing backend capability.

The view is a private nested type in the query-view partial, so it can call private query cores. It stores the owner, exclusion snapshot and a disposed flag. Delegate the three dynamic observations and live origin. Deny mutations, nested factories and owner disposal exactly as Step 2 pins. Check disposal before operational calls. The owner outlives the view. Do not add global owner-disposal infrastructure or change `QueryFilter.Layers`.

- [ ] **Step 5: Run the separately cleared GREEN command once.** Use Step 3's command unchanged. Expected: every discovered selection/lifecycle fact passes, exit 0 and no compile warning. Record results.
- [ ] **Step 6: Present Task 1 evidence and explicit changed paths to root for review.** No push, merge, pack or ref operation. Do not start a new task while root has a substantive seam correction pending.

### Task 2: Expose and prove TileWorld movement selection

**Files:** Modify `TileWorldColliders.cs`, `TileColliderRegistration.cs`, `GroundMeshSlopeMovementTests.cs` and the scene helper in `CharacterOnTileWorldTests.cs`. Create `GroundMeshQueryOwnershipTests.cs`.

**Interfaces:**

- Consumes Task 1's factory and source-bearing view.
- Produces `public IPhysicsWorldQueryView TileColliderRegistration.CreateMovementQueryView()`.
- Produces cached `public IReadOnlyList<StaticHandle> GroundHandles { get; }` in registration order, with the same post-removal metadata lifetime as `Handles`.
- Retains `public TileColliderRegistration TileWorldColliders.AddTo(IPhysicsWorld world)` unchanged.
- Retains the existing internal two-argument registration constructor for rollback. Add an internal overload with `(IPhysicsWorld world, StaticHandle[] handles, StaticHandle[] groundHandles)` for successful registration.

- [ ] **Step 1: Declare the new helper with an unsupported throwing stub and add composed failing tests.** `GroundMeshQueryOwnershipTests` creates a complete registered world and requests the public movement view. Assert `ReferenceEquals(view.SourceWorld, fullWorld)`, complete-world ground rays and selected-view omission of ground while retained non-ground shapes still hit. Add `GroundHandleSubsetMatchesCanonicalKinds` and `MultipleRegistrationsUseUnionExclusion`. The latter registers two ground descriptions on the same owner, proves excluding one still reveals the other, then supplies the union of the public ground subsets and proves both are excluded while the complete owner remains populated. Do not remove ground from the complete scene.
- [ ] **Step 2: Preserve the original observed fact and add the bounded slope matrix.** Keep its tuning, full-world ground ray, retained bench, omitted control and all outcome thresholds. Route only its `CharacterMovement.Step` input through the public view once implemented. The diagnostic decorator must wrap a selected view with `SourceWorld` equal to that decorator, retaining query recording instead of forwarding the backend-root identity.

New theory rows are `(riseCmPerTile: 75, run: false, hz: 30)`, `(95, true, 30)`, `(55, false, 30)` and `(95, false, 60)`, each 120 ticks. Use walk 2, run 5, half-height 0.75, radius 0.3, maximum slope PI/4 and step 0.4. Control travel is `speed * 120 / hz` and final-window travel is `speed * 30 / hz`. Require finite/grounded/dry state, point-height clearance within 0.02 m, no lateral drift over 0.05 m and per-tick forward motion no greater than commanded travel plus 0.06 m. Compare selected complete-world states with ground-omitted controls within 0.00003 m. Original fact thresholds remain 0.5 m total, 0.1 m final window and 0.455 m rise.

- [ ] **Step 3: Add explicit terrain and retained-solid boundaries.** Add `IdleOnRegisteredWalkableSlopeDoesNotDrift` for 120 ticks on 0.95 grade, plus 30 walking ticks then 120 idle ticks. Require planar drift under 0.001 m, stable grounding and point-height feet. Add `RegisteredSteepSlopeMatchesAnalyticSlide` on a 115 cm/tile plane from X=30.5, Z=-30.5 for 60 ticks at 30 Hz. Compare all carried states to the omitted-ground control, remain finite and refuse stable traction beyond the default retained-traction ceiling.

On registered flat terrain, require a raised deck to support and release at its edge, walls and blocked tiles to stop, a 0.25 m box step to mount, and an elevated sloped prop to remain visible and standable. The step box has half-extents `(3, 0.125, 2)`, centre `(16, 0.125, -30.5)`, and the body starts `(10.5, 0.75, -30.5)` walking east for 120 ticks at 30 Hz. Require arrival on the box top with centre Y within 0.02 m of 1.0, grounding and no tunnel. Select the view in the existing `CharacterOnTileWorldTests.Scene` without weakening its door, deck, blocked tile, ramp, water or rebase assertions. Add null-world and props-only controls with unchanged point-height behavior.

- [ ] **Step 4: Run the separately cleared RED command once.**

```sh
dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~GroundMeshSlopeMovementTests|FullyQualifiedName~GroundMeshQueryOwnershipTests|FullyQualifiedName~CharacterOnTileWorldTests" --logger "console;verbosity=detailed"
```

Expected: compiled new composed tests fail at the unsupported TileWorld helper. If the original scene has not yet selected the helper, its existing progress failure remains. Do not classify a compile error or empty filter as RED.

- [ ] **Step 5: Implement exact ground-handle registration and helper.** During successful `AddTo`, collect the handles whose canonical collider kind is Ground. Keep all other shapes and the original index-for-index public `Handles`. Pass the subset to the registration, expose its cached read-only wrapper as `GroundHandles`, and have its helper call the source factory. Preserve rollback, origin subtraction and retryable removal. Do not eagerly request backend selection in `AddTo`. Create the movement view once per scene, keep complete-world fixture rays on the owner, and dispose the view before the owner. Recreate it on registration replacement.
- [ ] **Step 6: Run the separately cleared GREEN command once.** Use Step 4's command unchanged. Expected: exit 0, every composed fact discovered and passing, meaningful original progress/control assertions intact. Record selected travel and complete-world ground evidence.
- [ ] **Step 7: Present Task 2 evidence and explicit changed paths to root for review.** No unrelated fixture or world-content changes.

### Task 3: Preserve full capture and selected dry movement proofs

**Files:** Modify `GroundMoveContext.cs`, `GroundMoveContext.Coordinates.cs`, `GroundTraversalProbe.cs` and the context helper in `TileWorldMovementNavigationTests.cs`. Create `KhaozEngine.Movement.Tests/GroundMoveQuerySelectionTests.cs`. Keep existing `GroundMoveContextTests.cs` expectations unchanged and add any needed default compatibility assertion there.

**Interfaces:**

- Consumes Tasks 1 and 2's source-bearing query view.
- Preserves the existing five-parameter `GroundMoveContext` constructor and all optional defaults. Adds a six-parameter overload with signature `GroundMoveContext(Func<float, float, float> groundHeight, Func<float, float, Vector3>? groundNormal, IPhysicsWorld? physics, Func<float, float, Vector2>? clampXz, Func<float, float, float, MovementMedium>? medium, IPhysicsWorldQueryView? movementQueries)`. Every argument in the new overload is explicit, avoiding new optional-overload ambiguity. The old constructor delegates with null selection.
- Produces `public IPhysicsWorldQueryView? MovementQueries { get; }`.
- `Physics` still names the complete caller-owned world. Effective movement input is `MovementQueries ?? Physics`. `CharacterMovement` and `PhysicsNavBake` public signatures do not change.

- [ ] **Step 1: Add the constructor overload/property and compiled failing context tests before redirecting movement.** Add `ExistingFiveParameterConstructorRemainsAvailable`, `SelectedMovementUsesSameSourceWhilePhysicsStaysComplete`, `ConstructorRejectsDifferentSourceEvenWithEqualOrigin`, `ConstructorRejectsSelectedViewWithoutPhysics`, `LogicalDecoratorSourceIsAccepted`, `ForwardedBackendIdentityThroughDecoratorIsRejected`, `SelectedOriginTracksSourceRebaseBetweenSteps` and `SelectedOriginMutationDuringProviderIsRejected` in the new test class. Verify the old constructor exists by its five parameter types and that old source calls retain their behavior.

Use a decorator whose factory wraps its delegated view and sets `SourceWorld` to the decorator, plus a deliberately incorrect forwarding decorator. Assert exact reference identity, not underlying backend equality. Missing/different source throws `ArgumentException` naming `movementQueries`. Provider-time source/selected origin mutation throws `InvalidOperationException` and a subsequent clean step can recover. Check default `MovementQueries == null` calls against the existing context's behavior, including terrain-only null `Physics`.

- [ ] **Step 2: Add the capture/dry proof integration fact to `KhaozEngine.TileWorld.Physics.Tests/GroundMeshQueryOwnershipTests.cs`.** Add `CompleteCaptureAndDryProofRetainSelectedMovement` using a drawn 0.95-grade interior patch, the selected registration view and a non-null medium provider. This project already references Movement and TileWorld, so Movement.Tests gains no new project reference. `PhysicsNavBake.Capture` must capture the real ground heights from complete `Physics`, and `BuildProfile` must admit a supported route on the walkable patch. Capture with a filtered world alone would yield missing ground and must not be the fixture. The medium callback must not be used to pace dry proofs. Preserve existing directed edges, footprint policy, budgets and route assertions.

Update `TileWorldMovementNavigationTests.Scene.Context` through the explicit six-parameter overload, with its existing height/normal, `physics: World`, `clampXz: null`, existing medium and `movementQueries: registration.CreateMovementQueryView()`. Keep direct world rays and `PhysicsColumnProbe` capture complete. Preserve its existing water classification, hole, fence, door, seam and rebased coordinate expectations. Source rebasing after view creation is allowed before capture, but origin changes during capture remain rejected as before.

- [ ] **Step 3: Run the separately cleared context RED command once.**

```sh
dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter "FullyQualifiedName~GroundMoveQuerySelectionTests|FullyQualifiedName~GroundMoveContextTests" --logger "console;verbosity=detailed"
```

Expected: compiled selected-query or identity facts fail while movement still uses full Physics and constructor validation is absent. Default-context facts remain passing. The separately named TileWorld integration fact is exercised by Step 6.

- [ ] **Step 4: Wire selection without changing movement or navigation algorithms.** Preserve the old constructor by delegating to the new overload with null selection. Validate exact source identity in the new overload. Freeze the full Physics origin at step entry and pass the selected view only to `StepTowards`. Extend the existing `EnsureOrigin` checks to the selected origin and source identity around callbacks. Use all six arguments in `GroundTraversalProbe`'s dry constructor, preserving height/normal/Physics/bounds and passing `medium: null, movementQueries: context.MovementQueries`. Keep complete `context.Physics` in capture and all profile admission logic unchanged. Do not make equal-origin independent worlds acceptable.
- [ ] **Step 5: Run the separately cleared context GREEN command once.** Use Step 3's command unchanged. Expected exit 0 with identity, origin and default compatibility facts passing. Record results.
- [ ] **Step 6: Run the separately cleared composed navigation command once.**

```sh
dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~GroundMeshQueryOwnershipTests|FullyQualifiedName~TileWorldMovementNavigationTests" --logger "console;verbosity=detailed"
```

Expected: exit 0, populated-ground capture and medium-bearing dry proofs pass without analytic fallback for missing columns. This is a new integration slice, not a repeat of a prior unchanged passing check.
- [ ] **Step 7: Present Task 3 evidence and explicit changed paths to root for review.** Respect the landed #1236 semantics. No backlog or navigation algorithm takeover.

### Task 4: Document the public composition and hand over verification

**Files:** Modify `KhaozEngine.Physics/README.md`, `KhaozEngine.Physics.Bepu/README.md`, `KhaozEngine.TileWorld.Physics/README.md`, `KhaozEngine.Locomotion/README.md`, `KhaozEngine.Movement/README.md`, `docs/PHYSICS-PIPELINE.md`, `docs/USING-KHAOZENGINE.md`, `CHANGELOG.md`, this plan's checkboxes and the design/index status only when supported by evidence.

**Interfaces:** Consumes the exact signatures from Tasks 1 through 3. Produces living usage for complete-world capture plus selected movement, retaining owner-started release boundaries.

- [ ] **Step 1: Sweep Markdown for changed names and invalid old composition.** Use `rg -n` across Markdown for `IPhysicsWorld`, `TileColliderRegistration`, `GroundMoveContext`, `colliders.Ground`, `PhysicsNavBake`, and the old raw-world TileWorld movement snippet. Update existing owned reference surfaces, not unrelated design histories. Use actual implemented names/signatures. Add a scoped entry to the staged 20.18.0 changelog without a version bump or tag.
- [ ] **Step 2: Update examples to keep one complete owner and one non-owning view.** Show registration, `CreateMovementQueryView()`, movement with the view and the explicit six-argument context overload retaining height/normal/bounds/medium while using `physics: fullWorld, movementQueries: view`. Show view disposal before source disposal. Explain unsupported backend behavior, decorator logical source, registration replacement, multiple-registration exclusion union and source-local handle provenance. The old raw full-world dual-owner call must not remain the TileWorld example.
- [ ] **Step 3: Run the separately cleared focused legacy collision slice once.**

```sh
dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~ControllerOnPhysicsTests|FullyQualifiedName~WalkableSlopeNoStepUpTests|FullyQualifiedName~RestStabilityTests.IdleOnWalkablePropSlope_DoesNotDrift|FullyQualifiedName~SingleRiserMountTests|FullyQualifiedName~ConsumerStairBaseMountTests" --logger "console;verbosity=normal"
```

Expected exit 0. This proves default prop queries after their shared-core extraction and the retained floor/wall/step boundaries. Do not broaden to the full suite locally at this checkpoint.
- [ ] **Step 4: Prepare non-CPU governance checks for root's permitted finish window.** Required commands are `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-agent-instructions.sh --tree`, `bash scripts/check-doc-versions.sh` and `git diff --check`. Follow failures as written and preserve unrelated edits. If root requires separate clearance for these checks, obtain it before execution. Do not edit a size baseline without its owner's approval.
- [ ] **Step 5: Give root the task evidence, scoped diff and remaining final verification.** Root reconciles current main and runs the full Release build/test/format and guarded pack proof with its own clearances. Do not claim whole-engine verification from these filtered facts. Preserve the original RED SHA, new command exit codes, discovered counts and complete-ground evidence in the handoff.
- [ ] **Step 6: Commit only when the later execution brief explicitly assigns it and required evidence is available.** Stage explicit changed paths, preserve unrelated staging and use proposed subject `fix(physics): separate analytic terrain movement queries`. The current planning documents remain uncommitted for root review. Root owns integration, push, packing and release. Do not bypass hooks or create a tag.

## Self-review and current state

The plan maps seam selection/lifecycle to Task 1, the original RED and terrain/prop boundaries to Task 2, same-source capture and dry proofs to Task 3, and living documentation plus evidence handoff to Task 4. The five Review Focus cases each have a named owning test. Signatures and property names match the design. Backend penetration selection is explicit required implementation. No query layer capability is assumed.

Root self-review added the public ground-handle subset and its union regression. Independent design and plan review approved the corrected documents without findings. No task checkbox is complete and no test command in this plan has run. Root assigns implementation after committing this approved plan.
