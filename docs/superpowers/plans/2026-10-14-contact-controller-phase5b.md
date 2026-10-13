# Contact controller phase 5b: navigation implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ground navigation profiles on the ordinary contact controller, proved at contact resting anchors, persisted in KENB format 2 and validated against the runtime binding.

**Architecture:** Legacy raw capture, layering and footprint facts stay as they are. A contact profile proves each raw node at its resting anchor through a proof binding that shares the profile's ordinary environment, publishes the anchor as the node's physical pose through `NavGrid` resting heights, and stores those heights in format 2.

**Tech Stack:** C# .NET 10, xUnit, Bepu physics through `IPhysicsWorld` and leases.

**Spec:** `docs/design/CONTACT-CONTROLLER-PHASE-5B-NAVIGATION-2026-10-14.md` (revision 6). Read the ruling a task names before its code. Rulings are cited as P5B-n.

## Global Constraints

- Preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity. Never widen a tolerance.
- Do not restart completed proofs. Every legacy behavioural, round-trip and byte-identity test passes unchanged except the dispositions in P5B-12.
- A known proper fix is implemented rather than a workaround shipped. A defect found outside the task is reported, not fixed in passing.
- No release, tag, game pin, Grimhollow or world edits. No load or repeat runs.
- Contact Aquatic throws `NotSupportedException` with exactly: `Contact aquatic navigation is not supported yet. See https://github.com/APKiwiOrg/KhaozEngine/issues/1389.`
- Every build and test runs through `build-slot` (`--heavy` for the full suite). Format check: `dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore`, judged only on files the task touched (ten unchanged files fail on main, #1387).
- `TreatWarningsAsErrors` stays on. Zero warnings.
- No em dashes, en dashes or prose semicolons in docs, comments or messages.
- Version rides staged 20.37.0. Append to its `CHANGELOG.md` entry, never bump.
- Commit subjects use `area(scope): summary`.

## Review Focus

- A legacy bake, any mix of legacy profiles, must be byte-identical before and after this phase. Task 5 adds the full-file golden, and every task's reviewer checks no legacy path changed behaviour.
- A contact node whose anchor is in a different raw band than its raw surface (deck, rooftop, stacked lift) must never be admitted. Task 4 pins deck over ground and the stacked lift refusal.
- Water decided at the raw point instead of the anchor would silently drop valid submerged lips. Task 4 pins the submerged lip and its inverse through the real call chain.
- A runtime consumer that still reads raw heights where the body's pose is meant would make goals unreachable on contact graphs. Task 2 pins every call site and Task 5 runs `MoveToRange` across the low-lip bank both ways.
- A loaded contact graph must equal the fresh one, including resting heights at the band ends. Task 5 pins round-trip and boundary rows.

---

### Task 1: Effective bake profiles, doors and capture basis

**Files:**
- Modify: `KhaozEngine.Movement/NavBakeProfile.cs`, `KhaozEngine.Movement/PhysicsNavBake.cs`, `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`, `KhaozEngine.Movement/GroundNavigationBake.cs`, `KhaozEngine.Movement/GroundNavigationBake.Reader.cs`
- Create: `KhaozEngine.Movement/PhysicsNavCaptureBasis.cs`
- Test: `KhaozEngine.Movement.Tests/ContactNavigationRefusalTests.cs` (rewrite per P5B-12), `KhaozEngine.Movement.Tests/NavBakeCaptureBasisTests.cs` (new)

**Interfaces:**
- Produces: `internal EffectiveMovementBindingKind NavBakeProfile.ResolveKind()`. Returns Legacy or OrdinaryContact. Throws `NotSupportedException` with the Global Constraints message for OrdinaryContact with `Aquatic`, `ArgumentException` for malformed selections (`EffectiveMovementBinding.Preflight` for its kind).
- Produces: `internal const string NavBakeProfile.DeferredAquaticMessage`.
- Produces: `internal readonly record struct PhysicsNavCaptureBasis(IPhysicsQueryLeaseSource Source, long GeometryGeneration, Vector3 Origin)` and `internal PhysicsNavCaptureBasis? PhysicsNavBake.Basis`.
- Produces: `internal NavAreaClassifier PhysicsNavBake.Classifier`, kept from `Capture`.

- [ ] **Step 1: Write the failing tests**

In `ContactNavigationRefusalTests` replace the two "phase 5b" rows with:
- `ContactAquaticRefusesAtEveryDoor`: a contact tuning (`Controller = Contact`, `Water = SurfaceSwimmer`) with `Aquatic = true` throws `NotSupportedException` with exactly `NavBakeProfile.DeferredAquaticMessage` at `BuildProfile`, `GroundNavigationBake.Create` and `GroundNavigationBake.Load`. For `Load`, assert the stream position is still 0.
- `MalformedContactSelectionThrowsArgumentBeforeStreamRead`: a contact tuning with `Water = default` throws `ArgumentException` at all three doors, with the stream unread for `Load`.
- `LegacyConstructedContextRefusesContactProfile`: a contact profile on a context built with the legacy constructor throws `NotSupportedException` at `BuildProfile`.
- Keep the existing validation-order controls: a contact profile that is also malformed in geometry reports the geometry error first.

In `NavBakeCaptureBasisTests`:
- `CaptureRecordsBasisFromALease`: on a `BepuPhysicsWorld`, `Basis` is set, its generation equals the world's lease generation and its origin equals the context origin.
- `GeometryChangedAfterCaptureRefusesContactBuild`: add a static after `Capture`, then a contact `BuildProfile` throws `InvalidOperationException`.
- `ClassifierThatMutatesGeometryRefusesCapture`: a classifier that adds a static on its first call makes `Capture` throw `InvalidOperationException`.
- `WaterFieldWithoutMediumDelegateRefusesSampleWater`: a contact-constructed context with a water field and no medium delegate, `SampleWater = true`, throws `InvalidOperationException` at `Capture` whose message names the medium delegate.
- `WorldWithoutLeaseSourceKeepsLegacyCapture`: a physics world without `IPhysicsQueryLeaseSource` captures as today, `Basis` is null, and a contact `BuildProfile` on it throws `InvalidOperationException`.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `build-slot --label p5b-1 -- dotnet test KhaozEngine.Movement.Tests -c Release --filter "FullyQualifiedName~ContactNavigationRefusalTests|FullyQualifiedName~NavBakeCaptureBasisTests"`
Expected: FAIL (missing members, old refusal message).

- [ ] **Step 3: Implement the doors and basis**

`ValidateProfileController` is removed. `BuildProfile`, `Create` and the reader's `Plan` call `ResolveKind()` before any other work, so `Load` refuses before reading the stream. `Capture` follows P5B-3: acquire a lease, record the basis, dispose it, sample, then acquire a second lease and compare generation and origin. Leases never nest. Keep the existing contact refusal in `BuildProfile` for a well-formed OrdinaryContact profile with message `"Contact navigation profiles land in task 4."` so no partial contact path exists between tasks. Task 4 removes it.

- [ ] **Step 4: Run the tests and the Movement suite**

Run: `build-slot --label p5b-1 -- dotnet test KhaozEngine.Movement.Tests -c Release`
Expected: PASS, with only the rewritten refusal rows changed.

- [ ] **Step 5: Commit**

`git commit -m "feat(navigation): resolve bake profile kinds and record the capture basis"`

### Task 2: NavGrid resting heights and the pose split

**Files:**
- Modify: `KhaozEngine.Navigation/NavGrid.cs` (and the partial that stores surfaces), `KhaozEngine.Navigation/GridPathPlanner.Region.cs`, `KhaozEngine.Navigation/NavSpace.cs`, `KhaozEngine.Navigation/PathFollower.cs` (documentation), `KhaozEngine.Movement/GroundNavigation.cs`, `KhaozEngine.Movement/RouteStraightener.cs`, `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`
- Test: `KhaozEngine.Game.Tests/Navigation/NavGridRestingHeightTests.cs` (new), `KhaozEngine.Game.Tests/Navigation/SurfaceHeightCallSiteTests.cs` (new)

**Interfaces:**
- Produces: `public float? NavGrid.RestingHeightAt(int cx, int cz)`. Returns the resting height where set, otherwise `SurfaceHeightAt`.
- Produces: `public bool NavGrid.HasRestingHeights`.
- Produces: `public NavGrid NavGrid.WithRestingHeights(float[] restingHeights)`. Returns a new grid with every other field identical. The array has one entry per cell, finite on open cells, ignored on blocked cells, and is copied.
- Consumes: nothing from Task 1.

- [ ] **Step 1: Write the failing tests**

`NavGridRestingHeightTests`:
- `RestingHeightDefaultsToSurfaceHeight`: a grid without resting heights returns `SurfaceHeightAt` for every cell, open or blocked.
- `WithRestingHeightsKeepsTopology`: the new grid's `SurfaceHeightAt`, clearance, bands and blocked mask equal the source's, and `RestingHeightAt` returns the supplied values on open cells.
- `RegionGoalUsesRestingHeight`: one open cell with surface 0 and resting 0.0152. A region goal accepting feet within 1 mm of y 0.0152 is found and `FindPath` is not Unreachable. A goal at y 0 within 1 mm is not found.
- `LayerAtUsesRestingHeight`: two layers at the same XZ with surfaces 0 and 0.3 and resting heights 0.29 and 0.3. `NavSpace.LayerAt` at y 0.29 picks layer 0.

`SurfaceHeightCallSiteTests.ProductionCallSitesAreClassified` scans the production `.cs` files under `KhaozEngine.Navigation` and `KhaozEngine.Movement` for `SurfaceHeightAt(` and `RestingHeightAt(` calls, excluding `NavGrid` itself, and asserts the set equals:
- `RestingHeightAt`: `GroundNavigation.Resolve`, `RouteStraightener.TryFeet`, `PhysicsNavBake.Feet`, `GridPathPlanner.CellFeet`, `NavSpace.LayerAt`
- `SurfaceHeightAt`: `PhysicsNavBake.IsFloatNode`, `GroundNavigationBake.WriteProfile`, `NavLayerLinks.GenerateCore` (two sites), `NavHopLinks.Generate` (two sites)

Match on the enclosing file and method name. A new caller fails the test until it is added to one list.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `build-slot --label p5b-2 -- dotnet test KhaozEngine.Game.Tests -c Release --filter "FullyQualifiedName~NavGridRestingHeightTests|FullyQualifiedName~SurfaceHeightCallSiteTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

Move the five pose readers to `RestingHeightAt`. Leave the raw identity readers. Update the hop endpoint comment in `PathFollower.cs` to name `RestingHeightAt` for physical Y. Legacy grids never set resting heights.

- [ ] **Step 4: Run the Navigation and Movement suites**

Run: `build-slot --label p5b-2 -- dotnet test KhaozEngine.Game.Tests -c Release --filter "FullyQualifiedName~Navigation"` then `build-slot --label p5b-2 -- dotnet test KhaozEngine.Movement.Tests -c Release`
Expected: PASS, no legacy row changed.

- [ ] **Step 5: Commit**

`git commit -m "feat(navigation): separate resting pose from raw surface heights"`

### Task 3: Locomotion seams

**Files:**
- Modify: `KhaozEngine.Locomotion/EffectiveMovementBinding.cs` (factory), `KhaozEngine.Locomotion/ExplicitCharacterMovement.Placement.cs`, `KhaozEngine.Movement/ExplicitMotionProbe.cs`, `KhaozEngine.Movement/ExplicitMovementProfile.cs` (recipe version)
- Create: `KhaozEngine.Locomotion/EffectiveMovementBinding.Navigation.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/EffectiveMovementBindingProofTests.cs` (new), `KhaozEngine.Game.Tests/Locomotion/ExplicitCharacterMovementTests.Placement.cs`, `KhaozEngine.Movement.Tests/ExplicitMotionProbeTests.cs`, `KhaozEngine.Movement.Tests/ExplicitMovementProfileTests.cs`

**Interfaces:**
- Produces: `internal EffectiveMovementBinding EffectiveMovementBinding.CreateProofBinding()`. Only for OrdinaryContact. Shares the provider, environment context, selected view and composition. Its tuning is the source tuning with `WalkSpeed = 1`, `RunSpeed = 1`, `AirMomentum = false`. Disposing it never disposes shared state.
- Produces: `internal readonly record struct RestingAnchor(Vector3 Feet, SupportSample Support)`.
- Produces: `internal MovementAvailability EffectiveMovementBinding.TryFindRestingAnchor(in FramedMovementState state, MovementQueryLease queries, out RestingAnchor anchor)`. Rebuilds the selection, builds the placement queries and runs `ContactPlacement.Find` with `SettlePlacement`'s grounded reach (`StepHeight` up, `GroundedEpsilon` down). No water, footing or motion route. Known with a Walkable support inside the inclusive band from `state` feet minus `GroundedEpsilon` to feet plus `StepHeight` sets `anchor.Feet` to the composition's resting feet at the state's XZ. Every other result returns a non-Known availability or Known with `anchor = default` and `Support.Status` not Walkable.

- [ ] **Step 1: Write the failing tests**

`EffectiveMovementBindingProofTests`:
- `ProofBindingSharesEnvironmentAndNormalizesSpeeds`: for a source with `WalkSpeed = 0`, `RunSpeed = 7`, `AirMomentum = true`, the proof binding's tuning differs only in those three fields, a lease from the source scope is accepted by the proof binding, and disposing the proof binding leaves the source usable.
- `RestingAnchorFindsLipAboveRawPoint`: the #1270 low-lip bank fixture from the archived low-lip tests (raw bank point that the legacy Hold moves by about 15 mm). The anchor sits at the lip's certified support within the band.
- `RestingAnchorIgnoresWaterAtRawPoint`: a WadeOnly body with raw feet past the deep line and a lip above the line within `StepHeight`. The anchor is the lip, with no water refusal.
- `RestingAnchorRefusesOutOfBandSupport`: a support just above raw plus `StepHeight` returns no Walkable anchor.

`ExplicitCharacterMovementTests.Placement`:
- `SwimmerAtFloatLineKeepsPoseOverBedWithoutFooting`: half height 0.75, submersion 0.6, enter 0.65, exit 0.55, surface 0, bed -0.94, `GroundedEpsilon` 0.05, stiffness 8, dt 1/30. Settling a swimmer with feet at -0.9 returns Advanced at -0.9, and one Hold moves it by at most 1 mm. Run it at spawn, cold restore and teleport.
- `FallingBodyDeniedFootingKeepsVerticalVelocity`: a falling body whose seat is denied footing keeps its supplied vertical velocity.
- The existing DryOnly three-door `PlacementRefused` row and every continuation and transfer no-seat row stay unchanged and green.

`ExplicitMotionProbeTests`:
- `GroundedArrivalUsesTheOneMillimetreSphere`: accepts an arrival offset by 1 mm along one axis, refuses an offset of 0.8 mm in X and 0.8 mm in Y, at the Hold and at the destination, through `ProbeExplicitMotion`.
- The swimming arrival rows stay unchanged.

`ExplicitMovementProfileTests`: the pinned fingerprint moves to the value produced with route recipe version 2.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `build-slot --label p5b-3 -- dotnet test KhaozEngine.Game.Tests -c Release --filter "FullyQualifiedName~EffectiveMovementBindingProofTests|FullyQualifiedName~ExplicitCharacterMovementTests"` and the two Movement test classes.
Expected: FAIL.

- [ ] **Step 3: Implement**

`SettlePlacement` follows P5B-6 exactly: delay the position publication and the downward-velocity reset until footing is granted, and leave every initial refusal as it is. `ExplicitMotionProbe` grounded `Near` becomes `GroundTraversalProbe`'s 3D sphere. Bump the route recipe version from 1 to 2. Name every test whose expectation moved in the report.

- [ ] **Step 4: Run the Locomotion, placement and explicit suites**

Run: `build-slot --label p5b-3 -- dotnet test KhaozEngine.Game.Tests -c Release --filter "FullyQualifiedName~Locomotion"`, then `KhaozEngine.Movement.Tests`, then `KhaozEngine.Server.Tests --filter "FullyQualifiedName~Explicit"`.
Expected: PASS, with only the named dispositions changed.

- [ ] **Step 5: Commit**

`git commit -m "feat(locomotion): add navigation proof seams and seat only with footing"`

### Task 4: Contact navigation build

**Files:**
- Modify: `KhaozEngine.Movement/PhysicsNavBake.Profiles.cs`, `KhaozEngine.Movement/GroundNavigation.cs`, `KhaozEngine.Movement/GroundMoveContext.Contact.cs` (internal accessors only)
- Create: `KhaozEngine.Movement/ContactNavigationProof.cs` (anchor admission, Hold, edge and Stair proofs over `MovementStepResult`), `KhaozEngine.Movement/NavProfileIdentity.cs`
- Test: `KhaozEngine.Movement.Tests/ContactNavigationAdmissionTests.cs`, `KhaozEngine.Movement.Tests/ContactNavigationWaterTests.cs`, `KhaozEngine.Movement.Tests/ContactNavigationTraversalTests.cs` (all new)

**Interfaces:**
- Consumes: Task 1 `ResolveKind`, `Basis`, `Classifier`. Task 2 `WithRestingHeights`. Task 3 `CreateProofBinding`, `TryFindRestingAnchor`.
- Produces: `internal sealed record NavProfileIdentity(EffectiveMovementBindingKind Kind, MoveTuning Tuning)` with `float PlacementOffset` (0 for both kinds). `GroundNavigation.Identity` returns it. `BuildProfile` sets it.
- Produces: contact `GroundNavigation` instances whose layer grids carry resting heights equal to each admitted node's anchor feet Y.

- [ ] **Step 1: Write the failing tests**

`ContactNavigationAdmissionTests`:
- `LowLipBankAdmitsAnchoredNodes`: the raw bank point fails the legacy Hold, the contact profile admits it, its resting height is the anchor, and a Complete route crosses the bank.
- `DeckAndGroundKeepTheirOwnAnchors`: a deck over ground keeps both nodes, and neither resting height lies in the other's raw band.
- `RaisedBodyMustFitCapturedHeadroom`: a lip whose anchor plus body height exceeds raw height plus headroom refuses the node.
- `ShellCatchesOffAxisLowRoof`: an off-axis roof that the axis ray misses, low enough to block the shell at the anchor, refuses the node.
- `StackedLiftRefusesRatherThanIterating`: a second support within reach above the anchor refuses the node.
- `SidewaysSeatRefuses`: a placement that pushes the anchor more than 1 mm in XZ refuses the node.
- `LateSliceRefusalPublishesNoEdge`: an edge whose third slice returns a non-Advanced outcome is absent, and so is a Hold refusal after successful placement.
- `EnvelopeOutsideServedScopeRefuses`: `BuildProfile` throws `InvalidOperationException` before any proof.

`ContactNavigationWaterTests`:
- `WadeOnlyDeepMiddleIsUnwalkable`: a pool with a deep middle. Deep cells have no node, and a route between opposite shores plans around them.
- `SubmergedLipJudgedAtAnchor`: surface 0, half height 0.75, enter 0.65. Raw ground at feet -1 with a lip anchor at -0.96 keeps its node. The inverse, an anchor at -1.0, has none.
- `EnterFractionRows`: just below, at and just above the enter fraction, and one row inside the hysteresis band, for SurfaceSwimmer and WadeOnly.
- `DryOnlyHasNoNodeInWater`.
- `UnknownWaterRefuses`: a water field answering Unresolved at the anchor refuses the node.
- `LegacyPoolBakesUnchanged`: legacy profiles over the same pool, with and without `Aquatic`, produce the same bytes as before this phase.

`ContactNavigationTraversalTests`:
- `EdgesStepTheProofBinding`: a source profile with `WalkSpeed = 0` builds the same graph as one with unit speeds.
- `EdgeIntoDeepCellStopsAtTheWall`: a WadeOnly edge toward a deep cell is refused.
- `StairLinksProvedBetweenAnchors`: a stair fixture's links match those of a legacy bake of the same stair where anchors equal raw points.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `build-slot --label p5b-4 -- dotnet test KhaozEngine.Movement.Tests -c Release --filter "FullyQualifiedName~ContactNavigation"`
Expected: FAIL.

- [ ] **Step 3: Implement**

Remove Task 1's interim contact refusal. For a contact profile, `CandidateColumns` keeps its headroom filter and calls `ContactNavigationProof.Admit` (P5B-4 steps 1 to 5) instead of the raw Hold. Edge and Stair proofs call `ContactNavigationProof.TryEdge` (P5B-7). Every query takes its own lease from the proof binding's scope and checks the lease basis against `Basis`. `PhysicsNavBake.Feet` reads the resting height through Task 2's split. Legacy paths are untouched.

- [ ] **Step 4: Run the Movement suite**

Run: `build-slot --label p5b-4 -- dotnet test KhaozEngine.Movement.Tests -c Release`
Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(navigation): prove contact profiles at resting anchors"`

### Task 5: KENB format 2 and runtime compatibility

**Files:**
- Modify: `KhaozEngine.Movement/NavBakeIdentity.cs`, `KhaozEngine.Movement/NavBakeIdentity.Tuning.cs`, `KhaozEngine.Movement/GroundNavigationBake.Writer.cs`, `KhaozEngine.Movement/GroundNavigationBake.Reader.cs`, `KhaozEngine.Movement/GroundNavigation.cs`, `KhaozEngine.Movement/MoveToRange.cs`
- Test: `KhaozEngine.Movement.Tests/GroundNavigationBakeFormatTwoTests.cs`, `KhaozEngine.Movement.Tests/GroundNavigationValidateTests.cs`, `KhaozEngine.Movement.Tests/ContactMoveToRangeTests.cs` (new), `KhaozEngine.Movement.Tests/NavBakeIdentityTests.cs`, `KhaozEngine.Movement.Tests/GroundNavigationBakeRefusalTests.cs`

**Interfaces:**
- Consumes: Task 4 `NavProfileIdentity` and contact graphs with resting heights.
- Produces: `NavBakeIdentity.ContactFormatVersion = 2`.
- Produces: `public void GroundNavigation.Validate(EffectiveMovementBinding binding)` and `public void GroundNavigation.Validate(in MoveTuning tuning)` (P5B-10). The internal `ValidateTuning` is removed.

- [ ] **Step 1: Write the failing tests**

`NavBakeIdentityTests` gains `AllLegacyFullFileGolden`: a small all-legacy fixture written under the golden engine string has a pinned SHA-256 of the whole file. Compute the pin from the pre-change writer, commit it before touching the writer, and keep it unchanged afterwards.

`GroundNavigationBakeFormatTwoTests`:
- `LoadMatrix`: every cell of the P5B-9 table, plus a combined engine and profile mismatch returning EngineChanged.
- `IdentityCorruptionRows`: unknown kind byte, Legacy with foot fraction other than 0.5 or water other than default, OrdinaryContact with water mode 0, nonzero placement offset. Each is Corrupt.
- `ContactRoundTripIsByteExact`: write, load and rewrite are identical, and the loaded graph's resting heights, footprint admission and planner results equal the fresh graph's.
- `FullyOpenSampledWaterContactBakeExceedsFormatOneBound`: the reviewer's sizing (10000 cells, one surface per column, one contact profile, one fully open flat layer, one sampled water entry per column) round-trips.
- `RestingSectionCorruption`: a missing resting byte, a trailing byte and a resting height just outside the band are Corrupt. Anchors exactly at both band ends round-trip.
- `MixedLegacyAquaticAndContactRoundTrip` with `SampleWater`.
- In `GroundNavigationBakeRefusalTests`, move the version 2 unsupported row to version 3.

`GroundNavigationValidateTests`:
- Kind mismatch both ways (legacy graph with a contact binding and the reverse), fresh and loaded.
- Each contact identity field mismatch throws `ArgumentException` naming the field.
- A PublicExplicit binding whose tuning would pass the Legacy checks throws `NotSupportedException` naming #1389.

`ContactMoveToRangeTests`:
- `MismatchedBindingThrowsBeforePlanning`.
- `CrossesLowLipBankBothWays`: contact `MoveToRange` traverses and stops across the low-lip waypoints in both directions on fresh and loaded graphs.
- `ResolvePicksOwnLayerAtBandEdge`: a body resting at an anchor at the upper band end resolves to its own node.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `build-slot --label p5b-5 -- dotnet test KhaozEngine.Movement.Tests -c Release --filter "FullyQualifiedName~FormatTwo|FullyQualifiedName~ValidateTests|FullyQualifiedName~ContactMoveToRange|FullyQualifiedName~NavBakeIdentityTests"`
Expected: FAIL except the full-file golden, which passes on the unchanged writer.

- [ ] **Step 3: Implement**

Follow P5B-9 for identity fields, the resting section order, the bound `4 * C * Lmax * Nc` and the `9 * open` preflight. All-legacy bakes take the unchanged format 1 path. `MoveToRange` validates against the context's resolved binding before planning and before prediction.

- [ ] **Step 4: Run the Movement, TileWorld physics and Server navigation suites**

Run: `build-slot --label p5b-5 -- dotnet test KhaozEngine.Movement.Tests -c Release`, then `KhaozEngine.TileWorld.Physics.Tests`, then `KhaozEngine.Server.Tests --filter "FullyQualifiedName~Range"`.
Expected: PASS.

- [ ] **Step 5: Commit**

`git commit -m "feat(navigation): persist contact profiles in KENB format 2"`

### Task 6: Cost record, documentation and verification

**Files:**
- Create: `docs/superpowers/plans/proofs/2026-10-14-contact-controller-phase5b-cost.md`
- Modify: `KhaozEngine.Movement/README.md`, `KhaozEngine.Navigation/README.md` (if it documents heights), `docs/INDEX.md`, `docs/design/CONTACT-CONTROLLER-PHASE-5B-NAVIGATION-2026-10-14.md` (status line), `docs/design/CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md` (status line), `CHANGELOG.md` (20.37.0 entry)

- [ ] **Step 1: Cost record**

One cold and one warm local measurement, through `build-slot --heavy`, of a contact bake of the round-trip fixtures. Record calls and time per raw surface for anchor, placement and Hold, per edge proof, refused candidates by reason and peak resident arrays per profile. Add a conservative static upper bound and a Hollowmere-scale extrapolation (about one million columns). No repeat or load run.

- [ ] **Step 2: Documentation and changelog**

Movement README: contact navigation profiles, anchors, resting heights, format 2, `Validate`, the deferred selections naming #1389. Status lines in both phase 5 designs and `docs/INDEX.md` say 5b is implemented in staged 20.37.0. Append the player-neutral engine changelog lines to the 20.37.0 entry.

- [ ] **Step 3: Full verification**

Run: `build-slot --heavy --label p5b-6 -- dotnet build KhaozEngine.slnx -c Release`, then `build-slot --heavy --label p5b-6 -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`, the whitespace format check, and `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-agent-instructions.sh --tree`, `bash scripts/check-doc-versions.sh`.
Expected: zero warnings, zero failures, every guard 0, format failures only in the ten #1387 files.

- [ ] **Step 4: Commit**

`git commit -m "docs(navigation): record contact navigation cost and status"`
