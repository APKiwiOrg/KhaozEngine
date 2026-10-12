# Contact Controller Phase 5a Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the contact controller selectable on every ordinary movement head, with certified dry and wet queries and one consistent placement convention.

**Architecture:** Keep legacy dispatch and encoding intact. Ordinary contact bindings supply a resident environment to the existing leased kernel, with zero placement offset, while public explicit bindings retain their 0.001 m offset. Installers supply immutable provenance and declared extents, and each head uses its effective binding for motion, placement and replay.

**Tech Stack:** C# on .NET 10, dependency-free Physics and Locomotion seams, Movement composition, Bepu query views, TileWorld snapshots, xUnit headless tests.

**Spec:** `docs/design/CONTACT-CONTROLLER-PHASE-5-SELECTOR-AND-NAVIGATION-2026-10-13.md`, revision 6. Implement P5A-1 to P5A-16, A1 to A10 and the seven tasks for 5a. The 5b inputs and superseded review-response planner descriptions are context only.

## Global Constraints

- TreatWarningsAsErrors. Zero warnings. Fix warnings at source.
- No local load, stress or repeated test loops. Every local build or test goes through build-slot.
- Warm allocation assertions use the checked no-GC region helper SupportAllocationRegion, not the shared AllocAssert (issue #1374).
- Legacy behaviour and every frozen golden byte stay unchanged. Only the P5A-14 guards change, each listed in its task.
- Preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity. Never widen a tolerance.
- No em dashes, en dashes or prose semicolons in shipped text. Commit subjects use `area(scope): summary`.
- Version rides staged 20.34.0. Append to its CHANGELOG.md entry, do not bump.
- No release, tag, game pin, Grimhollow edit or world edit.

## Review Focus

1. A physics step or frame rebase changes the read generation without changing installed absolute statics. A valid registration must survive that operation, while replacement or handle reuse must refuse. Task 3 adds `RegistrationSurvivesStepAndRebaseButNotStaticReplacement`.
2. Caller buffers are one item short or an immutable producer contains more than the water limits. Refusal must be atomic, with no usable prefix. Tasks 3 and 4 add the exact-capacity and sentinel tests.
3. A controller changes tuning or a cell is unloaded while its view was borrowed. Reconfiguration must release owned resources once and leave borrowed consumers usable. Tasks 5 and 6 add teardown and reconfiguration tests.
4. Tile coordinates cross zero or world Z is reversed. Certified water must describe the same authored tiles as legacy point sampling, including negative coordinates. Task 4 adds `NegativeTilesAndReversedWorldZAgreeWithAuthoredColumns`.
5. A medium supplies pace but the selected policy is WadeOnly or DryOnly. Approach pacing must not invent swimmer authority or bypass full-path proof. Task 6 adds `ApproachPacingHonoursSelectedWaterPolicy`.

---

## Source map and ownership

Line numbers below refer to worktree baseline `da4cd0167d4c7285bd97a08320d5b1e3257f6a17` on `feature/contact-controller-phase5`. Later tasks find the same symbols if earlier edits move their lines. Run `pwd`, `git branch --show-current`, `git status --short` before each task. Work only in `/Users/antonio/KhaozEngine/.worktrees/contact-controller-phase5`, preserve unrelated edits, stage explicit paths and stop at each verified commit. Integration, push and release belong to the controller.

Public selector, field and registration contracts live in `KhaozEngine.Locomotion`. This preserves TileWorld.Physics's pinned references at `KhaozEngine.Tests/ArchitectureTests.cs:680-687`. Provider, served-domain construction and effective execution live in `KhaozEngine.Movement`. NetWorld and Game.Render3D add a Movement reference, with their package descriptions and living dependency catalog updated in the same task. No new third-party dependency or umbrella member is introduced.

| Acceptance | Owning test project and files | Task |
| --- | --- | --- |
| A1 | `KhaozEngine.Game.Tests/Locomotion/MovementSelectorTests.cs`, existing legacy/contact/wire suites, `KhaozEngine.Movement.Tests/NavBakeIdentityTests.cs`, `ExplicitMovementProfileTests.cs` | 1, 2, 7 |
| A2 | `KhaozEngine.Server.Tests/NetWorld/OrdinaryContactHeadTests.cs`, `OrdinaryContactReplayTests.cs`, `KhaozEngine.Movement.Tests/OrdinaryContactLocalHeadTests.cs`, `KhaozEngine.Game.Tests/Game/CharacterController3DContactTests.cs` | 5, 6 |
| A3 | `KhaozEngine.Game.Tests/Locomotion/MovementSelectorTests.cs`, `KhaozEngine.Movement.Tests/OrdinaryMovementEnvironmentTests.cs`, `KhaozEngine.Server.Tests/NetWorld/OrdinaryContactConfigurationTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPhysicsRegistrationTests.cs`, local-head files above | 1, 3, 5, 6 |
| A4 | `KhaozEngine.Movement.Tests/OrdinaryLevelWaterTests.cs`, `KhaozEngine.TileWorld.Physics.Tests/OrdinaryTileWaterTests.cs` | 3, 4 |
| A5 | `KhaozEngine.Movement.Tests/OrdinaryMovementEnvironmentTests.cs`, `OrdinaryMovementRegistrationTests.cs`, `OrdinaryAnalyticGroundTests.cs` | 3 |
| A6 | `KhaozEngine.TileWorld.Physics.Tests/MovementWaterFieldTests.cs`, `TileMovementRegistrationTests.cs` | 4 |
| A7 | `KhaozEngine.Movement.Tests/OrdinaryServedDomainTests.cs`, `KhaozEngine.Server.Tests/NetWorld/OrdinaryContactReplayTests.cs`, `KhaozEngine.TileWorld.Physics.Tests/TileMovementRegistrationTests.cs` | 3, 4, 5 |
| A8 | `KhaozEngine.Server.Tests/NetWorld/OrdinaryContactLifecycleTests.cs`, `OrdinaryContactReplayTests.cs`, `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactCompositionTests.cs` | 2, 5 |
| A9 | `KhaozEngine.Movement.Tests/ContactNavigationRefusalTests.cs` | 1 |
| A10 | `KhaozEngine.Server.Tests/NetWorld/OrdinaryContactAllocationTests.cs`, linked checked helper, `docs/superpowers/plans/proofs/2026-10-13-contact-controller-phase5a-cost.md` | 7 |

Test snippets specify method names, local fixture values and required assertions, not complete method bodies. Helpers introduced by a test stay in that test project. Production interfaces below are complete contracts, and every consumed released signature was checked in the cited source.

### Task 1: Selector contract, shared validation and direct dry dispatch

**Files:**
- Create: `KhaozEngine.Locomotion/MovementControllerKind.cs`, `MoveTuning.Controller.cs`, `MovementConfigurationValidation.cs`.
- Modify: `KhaozEngine.Locomotion/CharacterMovement.cs:42-66,97-107`, `CharacterMovement.PreciseDirection.cs:12-22`, `Contacts/ContactQueryContext.cs:90-94`, `Contacts/ContactController.cs:21-38`.
- Modify: `KhaozEngine.Movement/ExplicitMovementProfile.cs:34-38,54-56`, `PhysicsNavBake.Profiles.cs:37-42`, `GroundNavigationBake.cs:62-74`.
- Test: `KhaozEngine.Game.Tests/Locomotion/MovementSelectorTests.cs`, `KhaozEngine.Movement.Tests/ContactNavigationRefusalTests.cs`.
- Modify tests, and only these existing guards: `KhaozEngine.Movement.Tests/NavBakeIdentityTests.cs:60-72`, `ExplicitMovementProfileTests.cs:23-29,43-55`.

**Interfaces:**
- Consumes: positional `MoveTuning` constructor with 30 parameters, `MoveTuning.Default` (`MoveTuning.cs:14-44,70-75`), `ShellGeometry.Validate(in MoveTuning, in GroundCoreSettings)` (`Contacts/ShellGeometry.cs:20-41`), `ExplicitCharacterMovement.Valid(in MoveTuning)` (`ExplicitCharacterMovement.Queries.cs:46-57`).
- Consumes: `ContactController.Step(in MoveState, Vector2, float, bool, bool, float, Func<float,float,float>, in MoveTuning, Func<float,float,Vector3>?, IPhysicsWorld?, Func<float,float,Vector2>?, Func<float,float,float,MovementMedium>?, float? = null, GroundCoreSettings? = null)` (`Contacts/ContactController.cs:30-34`).
- Produces in namespace `KhaozEngine.Locomotion`:

```csharp
public enum MovementControllerKind { Legacy = 0, Contact = 1 }
// In readonly partial record struct MoveTuning, outside the positional list.
public MovementControllerKind Controller { get; init; } = MovementControllerKind.Legacy;
public float FootRadiusFraction { get; init; } = 0.5f;
public WaterTraversalPolicy Water { get; init; } = default;

internal static class MovementConfigurationValidation
{
    internal static void ValidateLegacy(in MoveTuning tuning);
    internal static void ValidateContactGeometry(in MoveTuning tuning);
    internal static void ValidateDryDirect(in MoveTuning tuning, IPhysicsWorld? physics);
    internal static void ValidateOrdinaryContact(in MoveTuning tuning,
        IPhysicsWorldQueryView physics, bool hasRegistration, bool hasBoundary,
        bool hasMediumDelegate, bool hasWaterField);
    internal static void ValidateExplicit(in MoveTuning tuning, in WaterTraversalPolicy water);
}
```

The boolean presence arguments keep Task 1 independent of Task 3's registration contract. All entry checks use this helper. Undefined selectors throw `ArgumentOutOfRangeException`. Geometry requires a finite fraction in `(0,1]`, product `>= 0.01f`, and the existing shell validation with that fraction. Ordinary and public explicit validation require all of `ExplicitCharacterMovement.Valid`, preserving its positive Gravity, MaxFallSpeed and SwimEnterDepthFraction. Ordinary requires non-Legacy Water, registration, boundary, the four P5A-9 view capabilities and no medium without a field. Missing configuration or conflicting water throws `ArgumentException`, missing capabilities throw `NotSupportedException`. Direct dry retains analytic-only `physics == null`, otherwise requires lease source and footprint. Legacy checks only the selector/default contact-only settings, without adding new constraints to its released tuning.

- [ ] **Step 1: Write selector, validation and navigation tests.**

Use Default for selector/equality, and a valid small-foot shell with radius `0.02f`, half-height `0.1f`, StepHeight `0.04f`, MaxSlopeRadians and traction band `0f` for the footprint threshold. Direct dry test world is Bepu, ground is zero, state centre Y is Default.CapsuleHalfHeight, clamp is a rectangle clamp, medium reports Dry.

```csharp
// DefaultsWithEqualityAndHashIncludeTheThreeInitProperties
Assert.Equal(MovementControllerKind.Legacy, MoveTuning.Default.Controller);
Assert.Equal(0.5f, MoveTuning.Default.FootRadiusFraction);
Assert.Equal(default(WaterTraversalPolicy), MoveTuning.Default.Water);
Assert.Equal(baseline, baseline with { });
Assert.NotEqual(baseline, baseline with { Controller = MovementControllerKind.Contact });
Assert.NotEqual(baseline, baseline with { FootRadiusFraction = 0.75f });
Assert.NotEqual(baseline, baseline with { Water = new(WaterTraversalMode.WadeOnly) });
Assert.Equal(4, new HashSet<MoveTuning> { baseline, contact, widerFoot, wader }.Count);
// ContactFootFloorIsInclusiveAndNeverRoundedUp
Assert.Equal(0.01f, valid.FootRadiusFraction * valid.CapsuleRadius);
MovementConfigurationValidation.ValidateContactGeometry(valid);
Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = 0f }));
Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = 1.001f }));
Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = float.NaN }));
Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = MathF.BitDecrement(0.5f) }));
// LegacyRefusesContactOnlySettingsIncludingLegacyModeWithHop
Assert.Throws<ArgumentException>(() => Legacy(baseline with { FootRadiusFraction = 0.75f }));
Assert.Throws<ArgumentException>(() => Legacy(baseline with { Water = new(WaterTraversalMode.Legacy, 1f) }));
// DirectContactAcceptsDryMediumAndClampAndPreservesDirectionChoice
Assert.Equal(dryReference, directResult);
Assert.Equal(preciseDryReference, preciseTowardsResult); // direction (0.0001f, 0)
Assert.Contains("IMovementWaterField", Assert.Throws<NotSupportedException>(WetDirect).Message);
// ExplicitPolicyOwnsWaterAndSelectableFootParticipatesInFingerprint
Assert.Equal(defaultFingerprint, explicitDefaultWater.Fingerprint);
Assert.Equal(defaultFingerprint, explicitMatchingWater.Fingerprint);
Assert.NotEqual(defaultFingerprint, explicitWiderFoot.Fingerprint);
Assert.Throws<ArgumentException>(ExplicitConflictingWater);
// ContactBuildProfileAndBakeCreateRefuseBeforeEncoding
Assert.Contains("phase 5b", Assert.Throws<NotSupportedException>(BuildContactProfile).Message);
Assert.Contains("phase 5b", Assert.Throws<NotSupportedException>(CreateMixedBake).Message);
Assert.Equal("b8d049d40ad365789f251269953c4ee1074440c24b80982f69b874f7cb9f08e3", goldenFingerprint);
```

Add theories for each missing dry/ordinary capability, invalid shell, zero kernel requirement and undefined selector. Ordinary checks here are helper tests. Actual configuration-point tests belong to Tasks 3, 5 and 6.

- [ ] **Step 2: Run the new tests and confirm missing selectors/refusals fail.**

```bash
build-slot --label p5a-1-red-game -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter FullyQualifiedName~MovementSelectorTests
build-slot --label p5a-1-red-movement -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationRefusalTests|FullyQualifiedName~ExplicitMovementProfileTests|FullyQualifiedName~NavBakeIdentityTests'
```

Expected red is missing selector/helper API or a failed new refusal assertion, never a changed golden expectation.

- [ ] **Step 3: Add the enum, three init properties and shared check with the signatures above.**

Preserve the positional constructor and deconstruction. Validate explicit profiles using their supplied Water, allowing tuning.Water only when default or identical. Replace the fixed foot encoding at `ExplicitMovementProfile.cs:55` with `tuning.FootRadiusFraction`, leaving byte order and default fingerprint unchanged. Its Controller remains irrelevant.

- [ ] **Step 4: Dispatch the two stateful CharacterMovement entry points after their existing direction resolution.**

In `Step` preserve FaceCamera's yaw argument. In the precise `StepTowards` overload preserve `preserveSmallMagnitude`, run and jump=false. Pass `new GroundCoreSettings(tuning.FootRadiusFraction)`. Legacy still calls the same `StepCore` with identical arguments. The position-only `Vector3 Step` overload refuses Contact with `NotSupportedException` naming the stateful overload, because it cannot carry the contact controller's state or physics (controller note N3). Replace CheckMedium's obsolete phase text with `Point-sampled water cannot certify contact movement. Use an ordinary movement head with IMovementWaterField.`

- [ ] **Step 5: Add early bake refusals and maintain the two P5A-14 guards.**

The three-argument BuildProfile owns the shared guard for both overloads. GroundNavigationBake.Create checks every profile before identity encoding. Neither encoder nor v1 decoder changes. Split reflected MoveTuning storage into the 30 positional property backing fields and exactly `{Controller, FootRadiusFraction, Water}` backing fields, assert `33` total and `27` retained encoder fields. In the explicit-profile reflection theory include FootRadiusFraction and mutate it to `0.45f`, a valid value, while continuing to exercise every previous float/bool. Update its recipe assertion to read tuning's foot. Report these as the only changed existing guards.

- [ ] **Step 6: Run focused regression tests and confirm all pass with zero warnings.**

```bash
build-slot --label p5a-1-game -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~MovementSelectorTests|FullyQualifiedName~CharacterMovement|FullyQualifiedName~ContactControllerTests'
build-slot --label p5a-1-movement -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationRefusalTests|FullyQualifiedName~NavBake|FullyQualifiedName~ExplicitMovementProfileTests'
```

- [ ] **Step 7: Commit only this task's listed files.**

Use explicit `git add` paths and `git commit --only <same paths> -m "feat(locomotion): select and validate contact movement"`.

### Task 2: Carry one contact composition through tick and lifecycle kernels

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ContactComposition.cs`, `ExplicitCharacterMovement.Composition.cs`.
- Modify: `KhaozEngine.Locomotion/ExplicitCharacterMovement.cs:11-27`, `ExplicitCharacterMovement.Placement.cs:11-110`, `ExplicitCharacterMovement.Continuation.cs:11-44`.
- Modify: `KhaozEngine.Locomotion/Contacts/ContactController.Leased.cs:11-14,35-36,60-69`, `LeasedContactQueryContext.cs:17-74`, `ContactPlacement.cs:10-42`.
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactCompositionTests.cs`. Retain every existing explicit suite and golden unchanged.

**Interfaces:**
- Consumes: Task 1's tuning/validation, `GroundCoreSettings(float FootRadiusFraction = 0.5f)` (`Contacts/GroundCore.cs:9-12`).
- Produces in `KhaozEngine.Locomotion.Contacts`:

```csharp
internal readonly record struct ContactComposition
{
    internal float PlacementOffset { get; }
    internal GroundCoreSettings Foot { get; }
    internal ContactComposition(float placementOffset, GroundCoreSettings foot);
    internal static ContactComposition Ordinary(in MoveTuning tuning);
    internal static ContactComposition PublicExplicit(in MoveTuning tuning);
    internal float RestingFeet(float supportHeight);
}
```

Only offsets `0f` and `0.001f` are valid. Ordinary uses `0f`, public explicit uses `LeasedContactQueryContext.PlacementSkin`, both use the tuning foot. RestingFeet is the single `h + s` operation, preserving the zero-offset addition order and the explicit outward-safe mapping.

- Produces internal overloads in `KhaozEngine.Locomotion.ExplicitCharacterMovement`. Keep each existing public signature unchanged and delegate it to the same-name internal overload with public explicit composition:

```csharp
internal static MovementStepResult Step(in FramedMovementState state, in MoveCommand command,
    float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water,
    MovementQueryLease queries, in ContactComposition composition, MovementBoundary? boundary = null);
internal static MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction,
    bool run, float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water,
    MovementQueryLease queries, in ContactComposition composition, MovementBoundary? boundary = null);
internal static MovementStepResult ValidatePlacement(in FramedMovementState state, in MoveTuning tuning,
    in WaterTraversalPolicy water, MovementQueryLease queries, in ContactComposition composition,
    MovementBoundary? boundary = null);
internal static MovementStepResult SettlePlacement(in FramedMovementState state, in MoveTuning tuning,
    in WaterTraversalPolicy water, MovementQueryLease queries, in ContactComposition composition,
    MovementBoundary? boundary = null);
internal static MovementStepResult ReclassifyContinuation(in FramedMovementState state, in MoveTuning tuning,
    in WaterTraversalPolicy water, MovementQueryLease queries, in ContactComposition composition,
    MovementBoundary? boundary = null);
```

- Add these primary internal signatures, retaining the existing optional boundary/faceYaw/settings StepLeased and float-placementOffset constructor/Initialize as compatibility wrappers. Wrapper settings, when supplied, form the composition's Foot. This keeps frozen internal parity suites compiling. Store composition rather than independent defaults. Old placement helpers forward only if existing tests call them.

```csharp
// KhaozEngine.Locomotion.Contacts.ContactController
internal static MovementStepResult StepLeased(in FramedMovementState state, Vector2 moveDir,
    float speedFraction, bool run, bool jump, float dt, in MoveTuning tuning,
    in WaterTraversalPolicy water, MovementQueryLease queries, in ContactComposition composition,
    MovementBoundary? boundary = null, float? faceYaw = null);
// KhaozEngine.Locomotion.Contacts.LeasedContactQueryContext
internal LeasedContactQueryContext(MovementQueryLease lease, MoveTuning tuning,
    MovementSelection selection, MovementTransitionContext transition,
    in ContactComposition composition, WaterTraversalMode waterMode = WaterTraversalMode.DryOnly,
    MovementBoundary? boundary = null);
internal void Initialize(MovementQueryLease lease, MoveTuning tuning,
    MovementSelection selection, MovementTransitionContext transition,
    in ContactComposition composition, WaterTraversalMode waterMode = WaterTraversalMode.DryOnly,
    MovementBoundary? boundary = null);
// KhaozEngine.Locomotion.Contacts.ContactPlacement
internal static void Validate(ContactQueryContext queries, Vector3 feet, in MoveTuning tuning,
    in ContactComposition composition);
internal static SupportSample Find(ContactQueryContext queries, Vector3 feet, in MoveTuning tuning,
    in ContactComposition composition, float rise, float drop,
    out MovementContactSupportBinding? binding);
internal static bool TrySeat(ContactQueryContext queries, Vector3 feet, in SupportSample support,
    in MoveTuning tuning, in ContactComposition composition, out Vector3 placed);
internal static bool Clear(ContactQueryContext queries, Vector3 feet, in MoveTuning tuning,
    in ContactComposition composition);
// KhaozEngine.Locomotion.ExplicitCharacterMovement
static LeasedContactQueryContext PlacementQueries(in MoveState state, in MoveTuning tuning,
    in ContactComposition composition, in WaterTraversalPolicy water, MovementQueryLease queries,
    in MovementSelection selection, MovementBoundary? boundary);
```

- [ ] **Step 1: Write offset and foot propagation tests.**

Use the existing QueryScene and box floor patterns (`ContactLeasedQueryTests.Parity.cs:16-118`). For both fractions `0.5f` and `0.75f`, radius `0.25f`, half-height `0.75f`, support h `0f`, assert exact centres. Place an off-axis ledge reached only by the larger foot to prove placement and continuation use it. A low roof at Y `1.5005f` with ordinary body top `1.5f` distinguishes zero from 0.001 without changing skin rules.

```csharp
// OrdinaryAndPublicExplicitComposeDifferentRestingFeetOnEveryDoor
Assert.Equal(0f, ordinary.PlacementOffset);
Assert.Equal(0.001f, explicitComposition.PlacementOffset);
Assert.Equal(0.75f, ordinary.Foot.FootRadiusFraction);
Assert.Equal(0f, ordinary.RestingFeet(0f));
Assert.Equal(0.001f, explicitComposition.RestingFeet(0f));
Assert.Equal(0.75f, ordinarySettled.State.State.Position.Y);
Assert.Equal(0.751f, explicitSettled.State.State.Position.Y);
Assert.Equal(ordinarySettled.State.State, ordinaryValidated.State.State);
Assert.Equal(ordinarySettled.State.State.Position, ordinaryContinuation.State.State.Position);
Assert.Equal(dryTick, ordinaryTick.State.State);
// CustomFootReachesPlacementTickAndContinuation
Assert.Equal(wideOwner, wideSettled.State.Selection!.Value.Support);
Assert.Equal(wideOwner, wideTick.State.Selection!.Value.Support);
Assert.Equal(wideOwner, wideContinuation.State.Selection!.Value.Support);
// OffsetZeroDoesNotPayExplicitSkinUnderLowRoofOrAtDeepLine
Assert.Equal(MovementStepOutcome.Advanced, lowRoofOrdinary.Outcome);
Assert.Equal(MovementStepOutcome.PlacementRefused, lowRoofExplicit.Outcome);
Assert.Equal(MovementStepOutcome.PlacementRefused, ordinaryAtWadeThreshold.Outcome);
Assert.Equal(wadeInput.Position, ordinaryAtWadeThreshold.State.State.Position);
Assert.Equal(before, ordinaryContinuation.State.State.JumpBufferRemaining);
```

Set Wade threshold exactly with surface Y `0f`, half-height `0.75f`, SwimEnterDepthFraction `0.65f`, centre `0f + 0.75f * (1f - 2f * 0.65f)`. The existing deep entry line is an inclusive placement refusal (`ExplicitContactDeepWaterTests.cs:118-129`), distinct from a spatial shore tangent. Assert unchanged state at and below that line, then test an admitted spatial shore tangent separately. Preserve existing cover skin, do not force a result by shifting the threshold.

- [ ] **Step 2: Run the focused tests and confirm missing internal composition fails.**

```bash
build-slot --label p5a-2-red -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter FullyQualifiedName~ContactCompositionTests
```

- [ ] **Step 3: Add composition and delegate all five public explicit entries through it.**

Thread it through tick initialization, exact validation, settling, continuation, foot queries, support seating and shell/body mapping. Replace fixed foot uses in LeasedContactQueryContext.Analytic and ContactPlacement.Find/TrySeat. ValidatePlacement's `ContactShell.Validate(tuning)` becomes `ShellGeometry.Validate(tuning, composition.Foot)` so exact placement checks the selected fraction. PlacementQueries borrows `queries.Workspace.Queries` and calls Initialize, rather than allocating a new context at `ExplicitCharacterMovement.Placement.cs:107-110`. That workspace already exists (`ContactTickWorkspace.cs:23`) and operations on one interval stay serial. All support heights remain geometric. Physical body checks include the chosen offset before bounds, water and ceiling proof. Shared h+s mapping does not replace existing rounding refusal or geometric kernel-feet arithmetic.

- [ ] **Step 4: Run the new tests and the complete existing explicit/contact slice.**

```bash
build-slot --label p5a-2-contact -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactCompositionTests|FullyQualifiedName~ContactLeasedQueryTests|FullyQualifiedName~ExplicitCharacterMovementTests|FullyQualifiedName~ExplicitContact|FullyQualifiedName~MovementSupportSelectionTests'
build-slot --label p5a-2-profile -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter FullyQualifiedName~ExplicitMovementProfileTests
```

Expected all pass, unchanged explicit values and zero warnings. No existing guard changes in this task.

- [ ] **Step 5: Commit the composition task with explicit paths.**

Commit subject `refactor(locomotion): compose contact placement and foot geometry`.

### Task 3: Ordinary environment, served domain, Level field and effective binding

**Files:**
- Create in `KhaozEngine.Locomotion`: `MovementWaterExtent.cs`, `IMovementWaterField.cs`, `MovementWaterField.cs`, `LevelMovementWaterField.cs`, `OrdinaryMovementExtent.cs`, `OrdinaryMovementRegistration.cs`, `OrdinaryMovementRegistration.Builder.cs`.
- Create in `KhaozEngine.Physics`: `IPhysicsStaticGeometrySource.cs` (N1's generation seam).
- Modify: `KhaozEngine.Physics.Bepu/BepuPhysicsWorld.cs:125-171`, `BepuPhysicsWorld.QueryView.cs:25-38`. Create `BepuPhysicsWorld.StaticGeometry.cs` for the separate counter and private `QueryOperation EnterStaticMutation()` helper. AddStatic/RemoveStatic replace their EnterMutation calls with this helper without growing the existing large file. QueryView forwards the new property in its existing file.
- Create in `KhaozEngine.Movement`: `OrdinaryMovementEnvironment.cs`, `OrdinaryMovementEnvironment.Pin.cs`, `OrdinaryMovementEnvironment.Coordinates.cs`, `OrdinaryServedDomain.cs`, `EffectiveMovementBinding.cs`, `EffectiveMovementBinding.Execution.cs`, `OrdinaryMovementBoundary.cs`.
- Test: `KhaozEngine.Movement.Tests/OrdinaryMovementEnvironmentTests.cs`, `OrdinaryMovementRegistrationTests.cs`, `OrdinaryAnalyticGroundTests.cs`, `OrdinaryServedDomainTests.cs`, `OrdinaryLevelWaterTests.cs`, `Fixtures/OrdinaryContactScene.cs`.
- Test: `KhaozEngine.Game.Tests/Physics/StaticGeometryGenerationTests.cs` for the seam's Bepu lifecycle.

**Interfaces:**
- Consumes Task 2's internal composition overloads through the existing Movement friend assembly (`KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj:18`).
- Provider signatures are `IMovementEnvironmentProvider.cs:7-29`. Contact support signatures are `IMovementContactSupportPin.cs:4-10`. Scope/frame/identity constructors are `MovementQueryScope.cs:27-42`, `MovementEnvironmentIdentity.cs:64-89`. Water facts are `MovementEnvironmentFacts.cs:30-38,55-64,86-95`, coverage constructors are `MovementWaterCoverage.cs`. Scope witnesses are created with `MovementScopeWitness.TryCreate(in MovementQueryScope, in MovementQueryIdentity, IReadOnlyList<string>, bool, out MovementScopeWitness?)` (`MovementScopeWitness.cs:31-53`).
- Produces in `KhaozEngine.Locomotion`:

```csharp
public readonly record struct MovementWaterExtent
{
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }
    public MovementWaterExtent(Vector3 minimum, Vector3 maximum);
}
public interface IMovementWaterField
{
    string SemanticIdentity { get; }
    long Generation { get; }
    MovementWaterExtent Extent { get; }
    MovementWaterPoint SampleCentreWater(in MovementBodyQuery body);
    MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans,
        Span<MovementDomainContact> contacts);
}
public static class MovementWaterField
{
    public static IMovementWaterField Level(string worldId, float surfaceY, float floorY);
}
internal sealed class LevelMovementWaterField : IMovementWaterField
{
    internal LevelMovementWaterField(string worldId, float surfaceY, float floorY);
    public string SemanticIdentity { get; }
    public long Generation { get; }
    public MovementWaterExtent Extent { get; }
    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body);
    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans,
        Span<MovementDomainContact> contacts);
}
public readonly record struct OrdinaryMovementExtent
{
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }
    public float? AnalyticMinimumY { get; }
    public float? AnalyticMaximumY { get; }
    public OrdinaryMovementExtent(Vector3 minimum, Vector3 maximum,
        float? analyticMinimumY = null, float? analyticMaximumY = null);
}
public sealed partial class OrdinaryMovementRegistration
{
    public string WorldId { get; }
    public IPhysicsWorld SourceWorld { get; }
    public long GeometryGeneration { get; }
    public long Generation { get; }
    public OrdinaryMovementExtent Extent { get; }
    public IReadOnlyList<StaticHandle> ExcludedStatics { get; }
    public static Builder CreateBuilder(string worldId, IPhysicsWorld source,
        OrdinaryMovementExtent extent);
    internal MovementAvailability TryBind(IPhysicsQueryLease lease, StaticHandle handle,
        int featureId, out MovementSupportKey owner);
    public sealed class Builder
    {
        internal Builder(string worldId, IPhysicsWorld source, OrdinaryMovementExtent extent);
        public Builder Add(StaticHandle handle, int firstFeatureId, int lastFeatureId,
            MovementSupportKey owner, Vector3 absoluteMinimum, Vector3 absoluteMaximum);
        public Builder Exclude(StaticHandle handle);
        public OrdinaryMovementRegistration Build(IPhysicsQueryLease physicsLease);
    }
}
```

Extent constructors reject non-finite/inverted bounds, partial or inverted analytic ranges and analytic limits outside the declared Y range. Builder Add uses an inclusive feature range, rejects invalid provenance/world/bounds, and permits explicit aliasing to one canonical owner. Conflicting overlapping owners are retained as ambiguity and TryBind returns Unresolved. Exclusions dominate binding, never erase the installer history. Build snapshots all inputs under the supplied source lease, checks every static's declared bounds against Extent, and never trusts a static handle as an owner string. Entire certified feature sets may use `[0,int.MaxValue]` when one owner owns the whole static. Registered metadata authenticates ownership, the physics proof authenticates which feature exists.

The field's extra `identity` input is necessary because MovementMediumSweepQuery has no identity member, while MovementCoverageResult requires the lease identity (N4). Query CurrentSpace supplies/echoes the ordinary space. Level rejects blank world, non-finite heights and `floorY >= surfaceY`, uses generation `0`, domain `new(worldId,"level")`, boundary ids `level/bed` and `level/surface`, and finite represented-XZ Extent `[-float.MaxValue,+float.MaxValue]` at `[floorY,surfaceY]` (N2). Surface contact is strict for centre-feet membership. The full capsule trace still includes tangent contacts and the finite bed.

- Produces optional namespace `KhaozEngine.Physics` capability `public interface IPhysicsStaticGeometrySource { long StaticGeometryGeneration { get; } }`. Bepu and its query view implement it. Increment the separate counter before static installation/removal that might partially mutate, and preserve it across Step, dynamics, constraints and pure Rebase. Keep the released GeometryGeneration rules unchanged. Registration captures this counter when available and binds that immutable registration into each current physics generation. Without it, require exact original GeometryGeneration, safely refusing changes until an installer publishes a fresh registration. It is not an additional P5A-9 configuration requirement.
- Produces in `KhaozEngine.Movement`:

```csharp
public sealed partial class OrdinaryMovementEnvironment : IMovementEnvironmentProvider
{
    public OrdinaryMovementEnvironment(IPhysicsWorldQueryView physics,
        OrdinaryMovementRegistration registration, in MoveTuning tuning,
        Func<float,float,float> groundHeight, Func<float,float,Vector3>? groundNormal,
        MovementBoundary boundary, IMovementWaterField? waterField = null);
    public string WorldId { get; }
    public MovementQueryIdentity Identity { get; }
    public MovementQueryScope CreateScope(in MovementFrameDescriptor frame);
    public MovementPreparationResult Prepare(in MovementQueryScope scope);
    public MovementAvailability TryPinPrepared(in MovementQueryScope scope,
        IPhysicsQueryLease physicsLease, out IMovementEnvironmentPin? pin);
}
internal sealed class OrdinaryMovementEnvironmentPin : IMovementEnvironmentPin, IMovementContactSupportPin
{
    internal OrdinaryMovementEnvironmentPin(OrdinaryMovementEnvironment owner,
        IPhysicsQueryLease physicsLease, in MovementQueryScope scope,
        MovementScopeWitness witness, long environmentGeneration);
    public IPhysicsWorldQueryView PhysicsView { get; }
    public long GeometryGeneration { get; }
    public long EnvironmentGeneration { get; }
    public MovementFrameDescriptor Frame { get; }
    public MovementScopeWitness Witness { get; }
    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body);
    public MovementSupportSet EnumerateSupport(in MovementSupportRequest request,
        Span<MovementSupportCandidate> candidates);
    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts);
    public MovementAvailability RebuildSelection(in FramedMovementState state,
        out MovementSelection selection);
    public MovementAvailability SampleAnalyticGround(in MovementSupportRequest request,
        out MovementAnalyticGround? ground);
    public MovementAvailability BindContactSupport(in MovementContactSupportRequest request,
        out MovementContactSupportBinding binding);
    public void AssertCurrent();
    public void Dispose();
}
internal static class OrdinaryServedDomain
{
    internal static MovementQueryScope Create(OrdinaryMovementRegistration registration,
        in MoveTuning tuning, IMovementWaterField? waterField,
        in MovementQueryIdentity identity, in MovementFrameDescriptor frame);
}
public enum EffectiveMovementBindingKind { Legacy = 0, OrdinaryContact = 1, PublicExplicit = 2 }
public sealed partial class EffectiveMovementBinding : IDisposable
{
    public EffectiveMovementBindingKind Kind { get; }
    public MoveTuning Tuning { get; }
    public WaterTraversalPolicy Water { get; }
    public bool UsesLease { get; }
    public static EffectiveMovementBinding Legacy(in MoveTuning tuning);
    public static EffectiveMovementBinding Ordinary(in MoveTuning tuning, IPhysicsWorld physics,
        OrdinaryMovementRegistration registration, Func<float,float,float> groundHeight,
        Func<float,float,Vector3>? groundNormal, MovementBoundary boundary,
        IMovementWaterField? waterField = null,
        Func<float,float,float,MovementMedium>? medium = null,
        IPhysicsWorldQueryView? movementQueries = null);
    public static EffectiveMovementBinding PublicExplicit(in MoveTuning tuning,
        MovementEnvironmentContext environment, in WaterTraversalPolicy water);
    public MovementQueryScope CreateOrdinaryScope(in MovementFrameDescriptor frame);
    public MovementAvailability TryAcquire(in MovementQueryScope scope, out MovementQueryLease? queries);
    public MovementBoundary CaptureOrdinaryBoundary(in MovementFrameDescriptor frame);
    public MovementStepResult Step(in FramedMovementState state, in MoveCommand command,
        float dt, MovementQueryLease queries, MovementBoundary? boundary = null);
    public MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction, bool run,
        float dt, MovementQueryLease queries, MovementBoundary? boundary = null);
    public MovementStepResult ValidatePlacement(in FramedMovementState state,
        MovementQueryLease queries, MovementBoundary? boundary = null);
    public MovementStepResult SettlePlacement(in FramedMovementState state,
        MovementQueryLease queries, MovementBoundary? boundary = null);
    public MovementStepResult ReclassifyContinuation(in FramedMovementState state,
        MovementQueryLease queries, MovementBoundary? boundary = null);
    public void Dispose();
}
internal sealed class OrdinaryMovementBoundary : MovementBoundary
{
    internal OrdinaryMovementBoundary(MovementBoundary absoluteBoundary,
        in MovementFrameDescriptor frame);
    public override string SemanticIdentity { get; }
    public override MovementAvailability Classify(Vector3 centre, out bool inside);
    public override MovementAvailability Constrain(Vector3 start, Vector3 displacement,
        out Vector3 constrained);
    public override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement,
        Vector3 storedEnd, out bool admitted);
}
```

Binding owns a newly created view from `IPhysicsWorld.CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle>)` (`IPhysicsWorld.cs:88`). Supplied views are borrowed after SourceWorld identity validation. The pin is also non-owning. PublicExplicit borrows its entire supplied context. Legacy never creates a view. Dispose is idempotent, refuses an active owned read, and never disposes source physics. Ordinary boundary configuration is absolute, captured into the lease's physics frame before the read. A rounding conversion that cannot preserve the existing certificate refuses, without adding tolerance.

CreateScope has no basis, delta, speed or replay horizon input. It is constant for a binding/frame. Identity is deterministic from `ordinary-contact-v1`, declared extent bits, sorted canonical registration provenance, selected exclusions, analytic height range, field SemanticIdentity and tuning geometry/foot/Water. Frame epoch and local handles are excluded. CurrentSpace is null in the cold scope, selection rebuild returns `new MovementSpaceKey(WorldId,"ordinary")` with no support. Cache the scope, translated boundary and witness by semantic resident selection/frame, independently of changing read generations. Its four resource ids name physics selection, registration, analytic field and water (including explicit dry absence), cover known-empty regions and fit the existing witness limits. Combine captured physics, registration and water generations with a tuple-to-monotonic-long cache, not a collision-prone hash. Capture/compare the tuple in AssertCurrent. Registration.Generation records its publication's captured GeometryGeneration. Replacing immutable provenance requires a new registration and head reconfiguration.

- [ ] **Step 1: Write environment, registration and coordinate tests.**

Use a bounded box world declared `[-20,-4,-20]` to `[20,4,20]`, analytic range `[0,0]`, and absolute support h=0. Acquire only through MovementEnvironmentContext (`MovementEnvironmentContext.cs:18-33`) and keep reads through assertions. Pin field conversions at physics origin `(8,10,-16)`, absolute surface `12`, bed `8`, column `(9,-15)`.

```csharp
// OrdinaryPinAuthenticatesWorldIdentityFrameAndGeneration
Assert.Equal(MovementAvailability.Invalid, wrongWorldStatus);
Assert.Equal(MovementAvailability.Invalid, wrongIdentityStatus);
Assert.Equal(MovementAvailability.Invalid, wrongFrameStatus);
Assert.Throws<InvalidOperationException>(() => stalePin.AssertCurrent());
Assert.Throws<ObjectDisposedException>(() => expiredPin.AssertCurrent());
// PinConvertsSurfaceBedAndContactColumnIncludingPhysicsYOrigin
Assert.Equal(2f, localPoint.Interval!.Value.UpperY);
Assert.Equal(-2f, localPoint.Interval!.Value.LowerY);
Assert.Equal(new Vector2(1,1), localContact.IntervalColumnXZ);
Assert.Equal(localSpace, localContact.Space);
Assert.Equal(absoluteQuery, observedFieldQuery); // centre = local centre + (8,10,-16)
// RegistrationRefusesMissingExcludedAmbiguousAndStaleFeatures
Assert.Equal(MovementAvailability.Unresolved, missing);
Assert.Equal(MovementAvailability.Unresolved, excluded);
Assert.Equal(MovementAvailability.Unresolved, ambiguous);
Assert.Equal(MovementAvailability.Unresolved, stale);
Assert.Equal(joinedOwner, splitOwner);
Assert.Equal(forwardOwners, reversedInstallationOwners);
Assert.NotEqual(cellAOwner, cellBOwner); // equal handle 0, distinct authored provenance
Assert.Equal(partitionedOriginalOwner, partitionedCopyOwner);
// RegistrationSurvivesStepAndRebaseButNotStaticReplacement
Assert.Equal(beforeStepStaticGeneration, afterStepStaticGeneration);
Assert.Equal(beforeStepStaticGeneration, afterRebaseStaticGeneration);
Assert.NotEqual(beforeStepReadGeneration, afterStepReadGeneration);
Assert.Equal(MovementAvailability.Known, currentRegistrationBinding);
Assert.Equal(MovementAvailability.Unresolved, replacedStaticBinding);
// BorrowedViewsSurviveBindingDisposalAndOwnedViewsReleaseOnce
Assert.Equal(MovementStepOutcome.Advanced, survivingBorrower.Outcome);
Assert.Equal(0, borrowedViewDisposeCount);
Assert.Equal(1, ownedViewDisposeCount);
Assert.Equal(MovementAvailability.Unresolved, supportEnumeration.Availability);
Assert.Equal(0, supportEnumeration.Written);
Assert.Equal(MovementStepOutcome.EnvironmentInvalid, malformedTraceStep.Outcome);
```

For malformed field data use wrong echo space, identity, unordered/gapped spans and excessive error separately. Match the existing lease's Invalid versus Unresolved outcome per malformed class, never weaken validation. Shared borrowed contexts are sequential, not simultaneous leases. Test an extent excluding declared analytic `[0,2]` with Maximum.Y `1f` refuses construction.

- [ ] **Step 2: Write analytic and served-domain tests.**

```csharp
// AnalyticLandingAboveStepHeightMatchesDirectDryInOneTick
Assert.True(start.Position.Y - tuning.CapsuleHalfHeight > tuning.StepHeight);
Assert.Equal(dryLanding, ordinaryLanding.State.State);
Assert.True(ordinaryLanding.State.State.Grounded);
Assert.Equal(0f, ordinaryLanding.State.State.Position.Y - tuning.CapsuleHalfHeight);
// AnalyticBarrierAboveStepHeightBlocksAsTerrainWall
Assert.Equal(dryBarrier, ordinaryBarrier.State.State);
Assert.True(ordinaryBarrier.State.State.Position.X < barrierX);
// AnalyticScopeFactsIgnoreRequestReachAndCertifyOnlyTheirDomain
Assert.Equal(narrowGround, wideGround);
Assert.Equal(MovementAvailability.Known, belowDomainStatus);
Assert.Null(belowDomainGround);
Assert.Equal(MovementAvailability.Unresolved, aboveDomainStatus);
Assert.Equal(MovementAvailability.Unresolved, nonfiniteStatus);
// ResidentScopeCoversMotionSignalsAndExteriorQueriesWithoutPlannerRetries
Assert.Equal(firstScope, replayScope);
Assert.Equal(firstScope, continuationScope);
Assert.Equal(1, distinctScopesInReplay);
Assert.Equal(drySignal, ordinarySignal.State.State);
Assert.True(ordinarySignal.State.State.ClimbRateEwma > 0f);
Assert.Equal(MovementStepOutcome.Advanced, playableEdge.Outcome);
Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, outsideDomain.Outcome);
Assert.Equal(input.Position, outsideDomain.State.State.Position);
```

Use a landing centre Y `2f`, VerticalVelocity `-20f`, dt `0.1f`, half-height `0.75f`, flat h=0. Barrier h rises by `1f > StepHeight 0.4f`. Served-domain theories include WalkSpeed `120f > RunSpeed 100f`, StrafeSpeedScale `4f` with FaceCamera, state SpeedScale `3f`, field SpeedScale `4f`, high airborne centre Y `20f`, foot exactly at declared X edge, and centre exactly at playable maxX `20f`. Declare fixture geometry/analytic ranges large enough for those bodies, independently of per-tick motion.

The steep row uses analytic slope `60f * MathF.PI / 180f`, ungrounded body, AirMomentum=true, AirControl=`1f`, SpeedScale=`1f`, zero vertical velocity, horizontal velocity `(0,100)`, contour run command, RunSpeed `100f`, Gravity `10f`, dt `0.1f`. Compare every MoveState bit to direct dry and assert displacement advances. SlideCore retains 100 m/s carry and adds 100 m/s contour steer (`Contacts/SlideCore.cs:106-127`), giving about 20 m of contour travel. This tests summed carry and drive, not a RunSpeed reach estimate.

```csharp
// SixtyDegreeContourCarryAdvancesWithTheDirectDryResult
Assert.Equal(100f, tuning.RunSpeed);
Assert.Equal(10f, tuning.Gravity);
Assert.Equal(0.1f, dt);
Assert.Equal(dryContour, ordinaryContour.State.State);
Assert.Equal(MovementStepOutcome.Advanced, ordinaryContour.Outcome);
Assert.NotEqual(start.Position, ordinaryContour.State.State.Position);
Assert.InRange(MathF.Abs(ordinaryContour.State.State.Position.Z-start.Position.Z), 19.99f, 20.01f);
// DeepLevelBuoyancyUsesResidentVerticalExtent
Assert.Equal(1000f, field.Extent.Maximum.Y);
Assert.Equal(-2000f, field.Extent.Minimum.Y);
Assert.Equal(0f, swimmer.Position.Y);
Assert.Equal(MovementStepOutcome.Advanced, rise.Outcome);
Assert.InRange(rise.State.State.Position.Y - swimmer.Position.Y, 263f, 265f);
```

The last assertion records the spec's approximately 264 m counterexample, it is not an admission tolerance. Use SurfaceSwimmer, zero vertical velocity, SwimSurfaceSubmersionFraction `0.5f`, SwimBuoyancyStiffness `10f` and dt `0.1f`, with no solids or horizontal input and analytic floor h=`-2000f`. Give the valid shell CapsuleRadius `1f`, CapsuleHalfHeight `2f`, StepHeight `0.4f`, MaxSlopeRadians `20f * MathF.PI / 180f`, TractionHysteresisRadians `0f`. This preserves target Y=1000 and stays within AirPass's 1024-substep cap. The damped spring at `SurfaceWaterMotion.cs:49-55` gives `1000 - 2000 / e`, approximately `264.2411`. Assert exact agreement with that double-evaluated recipe rounded as the existing kernel does, and declare resident vertical bounds including bed/surface. Do not use a radius=half-height sphere that cannot satisfy the knee-shell validation.

- [ ] **Step 3: Write Level water policy and capacity tests.**

`OrdinaryLevelWaterTests` has one theory per A4 row at radius `0.25f`, half-height `0.75f`, foot `0.5f`, surface `0f`, floor `-4f`, dt `1f/30f`, SurfaceJumpSpeed `7f`. Terrain/axis-aligned solids shape entry, exit, dry bridge bands and a tied solid corner. Level itself has no horizontal shore.

```csharp
// SurfaceEntryExitBuoyancyAndHopAtZeroOffset
Assert.True(entered.State.State.Swimming);
Assert.False(exited.State.State.Swimming);
Assert.True(buoyant.State.State.Position.Y > submerged.Position.Y);
Assert.Equal(WaterExcursionState.AirborneFromWater, hop.State.State.WaterExcursion);
Assert.Equal((float)(7d - (double)tuning.Gravity * dt), hop.State.State.VerticalVelocity);
// WadeOnlyShoreSlideAndDryOnlyRefusal
Assert.False(wade.State.State.Swimming);
Assert.True(wade.State.State.Position.Z > start.Position.Z);
Assert.True(wade.State.State.Position.Y >= deepLineY);
Assert.Equal(MovementStepOutcome.PlacementRefused, dryOnlyStart.Outcome);
// BedAndShallowToDeepDownSeatRespectFullBody
Assert.True(bed.State.State.Position.Y - tuning.CapsuleHalfHeight >= -4f);
Assert.True(downSeat.State.State.Swimming);
Assert.Equal(geometryExpected, downSeat.State.State);
// ThinDryBandAndTiedShoreSolidCornerNeverTunnel
Assert.Equal(geometryExpected, crossedBand.State.State);
Assert.True(corner.State.State.Position.X <= solidFaceX);
Assert.True(corner.State.State.Position.Y >= deepLineY);
// ForbiddenStartAndRecoveryPayLagOnlyAfterAdmission
Assert.Equal(MovementStepOutcome.PlacementRefused, forbidden.Outcome);
Assert.Equal(forbiddenInput.Position, forbidden.State.State.Position);
Assert.Equal(geometryExpected, recovered.State.State);
Assert.True(recovered.State.State.Position.Y > laggingInput.Position.Y);
// LevelTraceIsCompleteAndCapacityRefusalHasNoPrefix
Assert.Equal(0f, spans[0].EnterFraction);
Assert.Equal(1f, spans[result.SpansWritten-1].ExitFraction);
Assert.InRange(result.CertifiedErrorMetres, 0f, 0.001f);
Assert.Equal(identity, result.Identity);
Assert.Equal(MovementAvailability.CapacityExceeded, tooSmall.Availability);
Assert.Equal(0, tooSmall.SpansWritten);
Assert.Equal(0, tooSmall.ContactsWritten);
Assert.Equal(spanSentinels, spansAfterRefusal);
Assert.Equal(contactSentinels, contactsAfterRefusal);
```

Expectations come from independent geometry or the existing explicit water fixtures with only the offset composed to zero. Do not record a new implementation's output as its oracle. Test zero displacement, exact bed/surface tangent and wrong world. A dry null field certifies one all-dry span and Known dry point.

- [ ] **Step 4: Run the new environment slice and confirm missing production contracts fail.**

```bash
build-slot --label p5a-3-red -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~OrdinaryMovement|FullyQualifiedName~OrdinaryAnalytic|FullyQualifiedName~OrdinaryServed|FullyQualifiedName~OrdinaryLevel'
```

- [ ] **Step 5: Implement registration, the Level field and static generation binding.**

Use immutable snapshot maps, precomputed canonical ids and cached exclusions. Pin a snapshot to the exact current read generation only after authenticating the source and static-generation marker. AssertCurrent checks the physical lease and captured registration/water tuple. Stale registration cannot be rebound to replaced geometry. Level trace partitions full path at bed/surface contacts, emits owned free/closed intervals and classified tangencies with certified outward rounding. Mutable fields must advance Generation before changing their answers and a changed field invalidates the captured pin.

- [ ] **Step 6: Implement the constant served domain and provider.**

Start from the declared absolute extent, validate static bounds and analytic range, and include finite water vertical bounds. For ordinary finite fields require their extent fits the declared resident extent. Apply N2's special full-XZ Level interpretation. Construct horizontal margins as the sum of capsule radius, foot radius, 0.0001f support band, three capsule radii of GroundSignals look-ahead and the extra outward horizontal extent from `SupportFootprintBounds.TryCreate(FootSupport.DescribeFootprint(query), out bounds)` (`Contacts/FootSupport.cs:58-69`, `Physics/SupportFootprintBounds.cs:12-27`). Use the real footprint request/proposals, not a copied inflation formula. Include the enlarged traction gate as well as the bare gate. Vertical bounds include the highest geometry/water target plus `JumpSpeed*JumpSpeed/(2*Gravity)` or SurfaceJumpSpeed apex, full body height, support reach and footprint admission extrema. Set MaxRise/MaxDrop to the expanded domain height, with outward float conversions and overflow refusal. Scope Min/Max plus their Envelope are one fixed witnessed region, and analytic containment tests use that complete witnessed region.

Prepare accepts exactly CreateScope's cold scope, with matching world/identity/frame and no clipping. A body outside it receives the existing lease refusal, without retry or an attempt boundary. Analytic samples convert the exact axis to absolute coordinates and check the witnessed vertical region first: Known null below it, Unresolved above/non-finite. An in-region height outside its declared analytic range is Unresolved, otherwise return the scoped canonical analytic-ground fact. Request reach filters remain in FootSupport/terrain walls. EnumerateSupport always returns Unresolved with zero Written. BindContactSupport maps a null Static/FeatureId=-1 only to the captured analytic-ground owner, and finite statics through captured registration ranges. Convert water body/trace coordinates and interval Y/IntervalColumnXZ in both directions. Never relabel an interval without converting it.

- [ ] **Step 7: Implement effective binding factories and execution.**

Use Task 1 checks at creation. Ordinary factory creates/owns a view once when absent, rolls it back on failure, constructs provider/context once and selects ordinary composition. PublicExplicit retains caller scope/context/Water and selects public explicit composition. Execution delegates to Task 2's overloads. NetWorld will own its player-specific scope callback in Task 5, rather than introducing a NetWorld dependency into Movement. Binding Kind, rather than nullable explicit configuration, becomes the downstream switch.

- [ ] **Step 8: Verify environment behavior and unchanged contact support.**

```bash
build-slot --label p5a-3-movement -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~OrdinaryMovement|FullyQualifiedName~OrdinaryAnalytic|FullyQualifiedName~OrdinaryServed|FullyQualifiedName~OrdinaryLevel'
build-slot --label p5a-3-physics -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~StaticGeometryGenerationTests|FullyQualifiedName~MovementEnvironment|FullyQualifiedName~ContactCompositionTests|FullyQualifiedName~ContactLeasedQueryTests'
```

Expected all pass, no changed existing guards and zero warnings.

- [ ] **Step 9: Commit the environment task with explicit paths.**

Commit subject `feat(movement): certify ordinary contact environments`.

### Task 4: Immutable TileWorld water and installer provenance

**Files:**
- Modify: `KhaozEngine.TileWorld.Physics/TileMediumSampler.cs:18-54`, `TileWorldColliders.cs:20-26,91-113,131-161`, `TileColliderRegistration.cs:20-50`.
- Modify: `KhaozEngine.TileWorld.Physics/TileColliderBuilder.Ground.cs:15-55`, `TileColliderBuilder.Objects.cs:22-39,43-110,117-133` to append parallel provenance metadata without changing collider geometry, ordering or hash.
- Create: `KhaozEngine.TileWorld.Physics/TileMediumSampler.Field.cs`, `TileMediumSampler.Trace.cs`, `TileWaterColumn.cs`, `TileWaterColumnCoverage.cs`, `TileColliderProvenance.cs`, `TileWorldColliders.Movement.cs`.
- Test: `KhaozEngine.TileWorld.Physics.Tests/MovementWaterFieldTests.cs`, `OrdinaryTileWaterTests.cs`, `TileMovementRegistrationTests.cs`, `Fixtures/TileWaterCoverageOracle.cs`.
- The test project already references Movement (`KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj:21`), so no project-reference change is needed. Production references stay unchanged.

**Interfaces:**
- Consumes Task 3's field, extent, registration builder and binding APIs. Existing tile collection is `TileWaterBodies.Collect(TileWorldDocument, TileWorldCatalogs, RegionCoord, int)` (`TileWaterBodies.cs:34-82`). Four authored bed corners are read at `TileGroundTriangles.cs:91-98`. Tile-to-world Z reverses sign (`TileWorldSpace.cs:15-24`).
- Produces in `KhaozEngine.TileWorld.Physics`:

```csharp
public sealed partial class TileMediumSampler : IMovementWaterField
{
    // Keep MediumAt(float worldX,float worldZ,float feetY) and MediumDelegate unchanged.
    internal TileMediumSampler(TileWorldDocument document, TileWorldCatalogs catalogs,
        RegionCoord[] regions, string worldId);
    // Field members use the exact Task 3 signatures.
}
public sealed partial class TileWorldColliders
{
    public static TileWorldColliders Build(TileWorldDocument document, TileWorldCatalogs catalogs,
        string worldId, TileColliderOptions? options = null);
    public OrdinaryMovementExtent MovementExtent { get; }
    public Func<float,float,float> MovementGroundHeight { get; }
    public Func<float,float,Vector3> MovementGroundNormal { get; }
}
public sealed partial class TileColliderRegistration
{
    public OrdinaryMovementRegistration CreateMovementRegistration(string worldId);
}
internal readonly record struct TileColliderProvenance(string OwnerLocalId,
    Vector3 AbsoluteMinimum, Vector3 AbsoluteMaximum);
internal readonly record struct TileWaterColumn(MovementDomainKey Domain,
    Vector3 Minimum, Vector3 Maximum, string MinXBoundaryId, string MaxXBoundaryId,
    string MinZBoundaryId, string MaxZBoundaryId, string BedBoundaryId, string SurfaceBoundaryId);
internal static class TileWaterColumnCoverage
{
    internal static MovementCoverageResult Trace(ReadOnlySpan<TileWaterColumn> columns,
        in MovementMediumSweepQuery query, in MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts);
}
```

Existing Build and TileMediumSampler constructor forward through a stable content-derived world id, not DisplayName, so legacy callers remain source/binary compatible. The explicit-worldId overload is the authoritative adoption door. CreateMovementRegistration requires that same worldId for its field snapshot, authenticates installed source, and captures a fresh lease outside any movement read. It records ground provenance but explicitly excludes GroundHandles, matching the released analytic-ground movement selection (`TileColliderRegistration.cs:43-50`), so ordinary sweeps do not encounter unsupported triangle-mesh ground. MovementGroundHeight/Normal expose the captured analytic snapshot used with that registration. The caller can combine additional installed statics through Task 3's builder rather than treating AddTo's partial list as the complete world.

New builder overloads append `List<TileColliderProvenance> provenance` after the existing final `List<TileCollider> into` argument to AddGround, AddBlocked, AddWalls, AddObjects, AddWalkSurfaces and private AddEdge. Preserve old wrappers if callers/tests use them. Box helper gets no ownership concern, its calling installer appends provenance. The two arrays stay index-aligned through Build. Existing TileCollider positional fields and HashOf encoding are untouched.

- [ ] **Step 1: Write independent exact geometry, identity and snapshot tests.**

The independent test oracle computes distance between a capsule's axial segment and the finite box, with separate face/edge/corner path root calculations. Do not share production rounding or partition helpers. Link/copy the existing test-only analytic volume formula only as a second cross-check (`Fixtures/AnalyticCapsuleVolume.cs:14-91`), not as production code. Cover diagonal paths, face, edge, rounded vertical cap and tangent cases.

```csharp
// RoundedCornerDoesNotUseAnExpandedBox
var axis = new Vector3(-0.09f, 0f, -0.09f);
const float radius = 0.1f;
Assert.InRange(Math.Sqrt(0.09d*0.09d + 0.09d*0.09d), 0.1272d, 0.1274d);
Assert.False(oracle.Overlaps(axis, radius, 0.25f));
Assert.Equal(0, trace.ContactsWritten); // water world XZ [0,1] by [0,1]
Assert.True(spans[0].HasDryCoverage);
// ExactCoverageAgreesOnFacesEdgesCornersDiagonalTangencyAndVerticalCaps
Assert.Equal(oracle.OverlappingDomains, producerDomains);
Assert.True(certifiedEnterLower <= oracle.Enter && oracle.Enter <= certifiedEnterUpper);
Assert.True(certifiedExitLower <= oracle.Exit && oracle.Exit <= certifiedExitUpper);
Assert.InRange(result.CertifiedErrorMetres, 0f, 0.001f);
Assert.Equal(MovementContactOverlap.Tangent, tangentContact.Overlap);
Assert.Equal(MovementContactOverlap.Tangent, tangentContact.SpanOverlap);
// AdjacentBodiesKeepSurfaceAndRegionIdentityAndSuppressOwnInternalFaces
Assert.NotEqual(leftDomain, rightDifferentSurfaceDomain);
Assert.NotEqual(leftDomain, equalSurfaceNextRegionDomain);
Assert.Equal(leftSurface, leftContact.Interval.NominalSurfaceY);
Assert.Equal(rightSurface, rightContact.Interval.NominalSurfaceY);
Assert.DoesNotContain(internalSameDomainFaceId, publishedBoundaryIds);
// NegativeTilesAndReversedWorldZAgreeWithAuthoredColumns
Assert.Equal(legacyPoint.InWater, certifiedPoint.InWater);
Assert.Equal(authoredSurfaceY, certifiedPoint.Interval!.Value.NominalSurfaceY);
Assert.Equal(snapshotPoint, afterDocumentEditPoint);
// ExactCapsAndOneShortBuffersRefuseAtomically
Assert.Equal(MovementAvailability.Known, at64Spans.Availability);
Assert.Equal(MovementAvailability.CapacityExceeded, at65Spans.Availability);
Assert.Equal(MovementAvailability.Known, at256Contacts.Availability);
Assert.Equal(MovementAvailability.CapacityExceeded, at257Contacts.Availability);
Assert.Equal(0, refused.SpansWritten);
Assert.Equal(0, refused.ContactsWritten);
Assert.Equal(spanSentinels, spansAfterRefusal);
Assert.Equal(contactSentinels, contactsAfterRefusal);
```

Use tile `(0,-1)` for the world-space `[0,1] x [0,1]` corner, with TileSize=1, using TileWorldSpace instead of assuming tile Z equals world Z. Cover dry tiles, negative tile coordinates, beds with different lowest corners and surfaces exactly equal across regions. Assert every span forms a contiguous closed whole-path partition, with simultaneous domain contacts and HasDryCoverage accurately representing uncovered capsule volume. Contact capacity includes face multiplicity, not only domain count.

`TileMovementRegistrationTests` asserts ground local ids `ground/{rx}/{rz}/0`, objects `placed/{objectId}`, wall corners and walk surfaces alias to their placed owner, blocked tiles `blocked/{plane}/{x}/{z}`. Reversed region creation and static installation retain those owners. A shared static handle number in distinct registrations never substitutes provenance. Assert MovementExtent contains every transformed box corner/mesh vertex, the analytic range and water columns, including exterior objects, and registration is Unresolved after Remove.

- [ ] **Step 2: Add the nine A4 policy rows for TileWorld.**

Use the Task 3 assertion set with a TileMediumSampler field and geometry-derived zero-offset values. Add named methods `SurfaceEntryExitBuoyancyAndHopAtZeroOffset`, `WadeOnlyShoreSlideAndDryOnlyRefusal`, `BedAndShallowToDeepDownSeatRespectFullBody`, `ThinDryBandAndTiedShoreSolidCornerNeverTunnel` and `ForbiddenStartAndRecoveryPayLagOnlyAfterAdmission`. Give each scenario its own named theory row: `SurfaceEntry`, `SurfaceExit`, `BuoyancyAndHop`, `WadeWallAndSlide`, `DryOnly`, `BedAndDownSeat`, `ThinDryBand`, `TiedShoreSolidCorner`, `ForbiddenStartAndLagRecovery`. No byte-recorded production oracle.

- [ ] **Step 3: Run new tile tests and confirm missing field/registration APIs fail.**

```bash
build-slot --label p5a-4-red -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter 'FullyQualifiedName~MovementWaterFieldTests|FullyQualifiedName~OrdinaryTileWaterTests|FullyQualifiedName~TileMovementRegistrationTests'
```

- [ ] **Step 4: Snapshot columns, domains, beds and provenance beside existing outputs.**

Each collected region/body gets a deterministic domain from region coordinates and canonical body tile membership, not collection index or height alone. Each water tile is a finite column with lower Y at or below its four authored bed corners and upper Y at its body's free surface. Use authored min corner as lower Y when strictly below surface, otherwise the next represented float below surface. Include the rounding enclosure in the certified bound. Immutable snapshots retain no mutable document access for the new field, generation `0`, semantic identity from canonical columns. Legacy MediumAt remains exactly its existing one-surface lookup, including behavior below the new finite bed. This deliberate separation preserves A1.

Provenance is recorded where region and placed object identity still exist (`TileColliderBuilder.Ground.cs:15-35`, `Objects.cs:25-37,93-110`). Do not reconstruct provenance from identical geometry. TileGroundSampler currently retains the document (`TileGroundSampler.cs:32-40`), so new certified analytic delegates must use a captured document/height snapshot from the same Build, while old public samplers retain their released contract. MovementExtent uses captured loaded-world geometry/ranges. For non-contiguous resident islands do not claim unavailable analytic data in their bounding rectangle. Refuse a contact registration for that hole-containing declaration until it can supply complete scoped analytic facts.

- [ ] **Step 5: Implement exact capsule versus union-of-finite-columns coverage.**

For each column, extend only its Y interval by the capsule axial half-length `HalfHeight - Radius`. For path centre `p(t)`, squared distance to that extended box is the sum of three squared outside-axis distances. Cut `[0,1]` at axis slab crossings. On each interval solve the resulting quadratic `d2(t) <= Radius*Radius`, outward enclosing roots and represented endpoints. This is a rounded box, not three independently inflated slabs. Certify tangency separately from positive overlap and uncertain overlap.

Union all column occupancy cuts per domain. Split at every contact/top-kind/interval change, including vertical bed changes. Build the boundary of the union: subtract neighboring same-domain covered face patches, leaving partial exposed faces and their edges/corners. Preserve boundaries between different domains even at equal surfaces. Determine dry coverage from the exact capsule volume outside the union, never from its bounding box or its centre. Normals come from the closest box/union feature, with rounded edge/corner normals, and each contact has a valid witness IntervalColumnXZ inside its owning column. Stage complete results in reusable bounded scratch and publish only after capacity/error/identity validation. Limits remain 64 spans and 256 contacts. No unlimited tile scan workspace per tick or inflated-AABB certificate.

- [ ] **Step 6: Verify tiles and frozen legacy collider behavior.**

```bash
build-slot --label p5a-4-tile -- dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release
build-slot --label p5a-4-architecture -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter FullyQualifiedName~ArchitectureTests.TileWorldPhysics_ReferencesOnlyTileWorldPhysicsAndLocomotion
```

The area project is a focused suite, run once here. Expected unchanged legacy AddTo rollback/Hash/ground-view tests, new field rows passing, no guard edits and zero warnings.

- [ ] **Step 7: Commit the TileWorld producer task with explicit paths.**

Commit subject `feat(tileworld): certify ordinary water and support provenance`.

### Task 5: One effective binding through network heads and cell lifecycle

**Files:**
- Create: `KhaozEngine.NetWorld/OrdinaryPlayerMovement.cs`, `PlayerMoveSimulator.Binding.cs`, `PlayerMovementSystem.Binding.cs`, `WorldServer.MovementLifetime.cs`.
- Modify: `KhaozEngine.NetWorld/PlayerMoveSimulator.cs:23-62,75-86,142-155`, `PlayerMoveSimulator.Explicit.cs:12-75,93-107`, `PlayerMovementSystem.cs:54-76,109-114,149-188`, `PlayerMovementSystem.Explicit.cs:15-29`.
- Modify: `KhaozEngine.NetWorld/WorldServer.cs:157-165,208-253,540-548`, `WorldServer.Placement.cs:28-69`, `WorldServer.Sessions.cs:27-53`, `WorldServer.ResumeSpawn.cs:72-98`.
- Modify: `KhaozEngine.NetWorld/WorldClient.cs:85-140,412-434`, `WorldClient.ExplicitMovement.cs:13-34`, `ShardedWorldServer.cs:80-136,162`, `ShardedWorldServer.Frame.cs:100-162`, `ShardedWorldServer.Placement.cs:24-73`, `ExplicitCellAdmission.cs:14-80`.
- Modify: `KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj:3-18` to add Movement and update Description, `README.md` NetWorld dependency row, `KhaozEngine.NetWorld/README.md` configuration/read examples.
- Test: `KhaozEngine.Server.Tests/NetWorld/OrdinaryContactHeadTests.cs`, `OrdinaryContactConfigurationTests.cs`, `OrdinaryContactReplayTests.cs`, `OrdinaryContactLifecycleTests.cs`, `Fixtures/OrdinaryPlayerWorld.cs`.

**Interfaces:**
- Consumes Task 3's EffectiveMovementBinding. Existing public explicit scope is `Func<PlayerMoveState,WorldFrame,MovementQueryScope>` (`ExplicitPlayerMovement.cs:14-24`), read is `PlayerMoveReadScope` (`:30-64`), client replay keeps one read (`WorldClient.ExplicitMovement.cs:16-33`). Keep those public entry signatures.
- Add these config partial members in `OrdinaryPlayerMovement.cs`, namespace `KhaozEngine.NetWorld`:

```csharp
// WorldServerConfig and WorldClientConfig, on each type.
public IMovementWaterField? MovementWaterField { get; init; }
public OrdinaryMovementRegistration? MovementRegistration { get; init; }
public MovementBoundary? MovementBoundary { get; init; } // absolute ordinary boundary
// ShardedWorldServerConfig, against the installed physics of each cell.
public Func<CellSim,IMovementWaterField?>? MovementWaterFieldFactory { get; init; }
public Func<CellSim,OrdinaryMovementRegistration>? MovementRegistrationFactory { get; init; }
public Func<CellSim,MovementBoundary>? MovementBoundaryFactory { get; init; }
```

WorldServer/WorldClient keep their existing constructor signatures because config carries new members. Existing WorldBounds can supply a complete rectangle/circle certificate when explicit MovementBoundary is absent, using `WorldBounds.TryCaptureExplicit(Vector3 physicsOrigin,out MovementBoundary?)` (`WorldBounds.cs:24`), captured at absolute origin before constructing the ordinary binding. Refuse a custom WorldBounds without a certificate. Configured MovementBoundary wins consistently for ordinary motion/placement.

- Add full overloads, preserve the old constructors and route all of them through one private initialization path after all selections are known:

```csharp
public PlayerMoveSimulator(Func<float,float,float> groundHeight, MoveTuning tuning,
    Func<float,float,Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
    Func<float,float,float,MovementMedium>? medium, SamplerSpace samplerSpace,
    ExplicitPlayerMovement? explicitMovement, IMovementWaterField? movementWaterField,
    OrdinaryMovementRegistration? movementRegistration, MovementBoundary? movementBoundary);
public PlayerMovementSystem(Func<float,float,float> groundHeight, MoveTuning tuning,
    Func<float,float,Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
    Func<float,float,float,MovementMedium>? medium, WorldFrame frame, SamplerSpace samplerSpace,
    ExplicitPlayerMovement? explicitMovement, IMovementWaterField? movementWaterField,
    OrdinaryMovementRegistration? movementRegistration, MovementBoundary? movementBoundary);
// Assembly-internal borrowed-binding overloads for one cell runtime's shared binding.
internal PlayerMoveSimulator(Func<float,float,float> groundHeight, MoveTuning tuning,
    Func<float,float,Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
    Func<float,float,float,MovementMedium>? medium, SamplerSpace samplerSpace,
    EffectiveMovementBinding binding, ExplicitPlayerMovement? publicExplicit);
internal PlayerMovementSystem(Func<float,float,float> groundHeight, MoveTuning tuning,
    Func<float,float,Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
    Func<float,float,float,MovementMedium>? medium, WorldFrame frame, SamplerSpace samplerSpace,
    EffectiveMovementBinding binding, ExplicitPlayerMovement? publicExplicit);
// On simulator and movement system.
internal EffectiveMovementBinding Binding { get; }
public void Dispose();
```

Make simulator and system IDisposable. Standalone instances own their binding. WorldServer owns/disposes its simulator and gains IDisposable without disposing injected transport/physics. WorldClient already disposes transports and additionally disposes simulator. CellRuntime owns one `required EffectiveMovementBinding Binding { get; init; }`, and its system, clamp and admission borrow it. CellRuntime.Dispose releases adapters then binding before source cell physics removal. Remove its nullable Explicit as an execution selector, retain the public configuration only for its scope callback.

- Existing `BeginExplicitRead(in PlayerMoveState basis)` now opens both leased kinds and returns null only for Legacy. LastExplicitOutcome remains compatible for both. Its ordinary scope comes from Binding.CreateOrdinaryScope, never the player basis. Use the real physics Origin in MovementFrameDescriptor, epoch tracked between frames, and translate player coordinates between planar FrameAnchor and full physics origin including Y before all kernel entries/results. PublicExplicit keeps its released frame checks/callback contract. Capture ordinary boundary before acquisition and retain it in PlayerMoveReadScope.
- Change the existing internal `ExplicitCellAdmission` constructor to `(EffectiveMovementBinding binding, ExplicitPlayerMovement? publicExplicit, MoveTuning tuning, WorldFrame frame, IPhysicsWorld? physics, WorldBounds? bounds)`. Its Acquire signature stays `CellAdmissionRead Acquire(World staged,IReadOnlyDictionary<long,Entity> entities,CellImportPurpose purpose)`. Ordinary imports acquire the one constant scope. Only PublicExplicit keeps the existing callback-scope union. All placements delegate to binding methods.

- [ ] **Step 1: Write the network selection matrix and actual configuration-point refusals.**

Fixtures use paired worlds with the same registered boxes, absolute delegates, a framed island and field. For each head test idle/dry traversal, triggered stairs and wet entry/exit. Include FaceCamera=true with yaw `0.75f`, bound clamp at maxX, and a nonzero physics Y origin fixture. Compare MoveState values exactly, not only positions, and compare ordinary dry results to direct dry too.

```csharp
// EveryNetworkHeadMatchesOrdinaryCompositionDryWetAndStairSignals
Assert.Equal(reference.State.State, simulatorResult.Move);
Assert.Equal(reference.State.State, authoritativeResult.Move);
Assert.Equal(reference.State.State, cellMovementResult.Move);
Assert.Equal(authoritativeResult.Move, predictedResult.Move);
Assert.Equal(dryReference, ordinaryDryResult.Move);
Assert.Equal(reference.State.State.FacingYaw, simulatorResult.Move.FacingYaw);
Assert.True(stairResult.Move.ClimbRateEwma > 0f);
// NetworkConstructorsRefuseBeforeFirstTick
Assert.Throws<NotSupportedException>(ConstructWithoutSweep);
Assert.Throws<NotSupportedException>(ConstructWithUnselectablePhysics);
Assert.Throws<ArgumentException>(ConstructWithoutRegistration);
Assert.Throws<ArgumentException>(ConstructWithoutBoundary);
Assert.Throws<ArgumentException>(ConstructWithPointMediumOnly);
Assert.Throws<ArgumentException>(ConstructWithContactAndPublicExplicit);
Assert.Throws<ArgumentException>(ConstructWithZeroGravity);
Assert.Throws<ArgumentException>(ConstructWithZeroFallSpeed);
Assert.Throws<ArgumentException>(ConstructWithZeroSwimEnter);
Assert.Throws<ArgumentException>(ConstructLegacyWithSurfaceHop);
```

Run each refusal for simulator, system and the two single heads. Sharded constructor validates tuning and required factories before cell allocation. A bad returned cell view, registration or boundary fails that cell's RuntimeFor configuration before wiring/admitting it. Missing cell factories and two selections fail the sharded constructor. A configured registration factory returning null throws InvalidOperationException.

- [ ] **Step 2: Write replay and lifecycle tests across both server heads.**

Replay four commands under one read, including a preparing commitment that launches on the second command. Assert bit-equal replay/server states and one ordinary scope. For both heads cover spawn, saved resume/restore, teleport, configured spawn reset and failed exact correction. Sharded also covers same-cell placement, cross-cell teleport, persisted cell restore, transferred continuation and atomic refused destination import.

```csharp
// OrdinaryReplayKeepsOneResidentScopeIncludingCommitmentLaunch
Assert.Equal(serverStates, replayStates);
Assert.True(replayStates[1].Move.Commitment.IsActive);
Assert.Equal(1, acquiredMovementReadCount);
Assert.Equal(1, distinctPreparedScopeCount);
// EveryOrdinaryLifecycleDoorUsesOffsetZero
Assert.Equal(h + tuning.CapsuleHalfHeight, spawned.Move.Position.Y);
Assert.Equal(h + tuning.CapsuleHalfHeight, restored.Move.Position.Y);
Assert.Equal(h + tuning.CapsuleHalfHeight, teleported.Move.Position.Y);
Assert.Equal(h + tuning.CapsuleHalfHeight, reset.Move.Position.Y);
Assert.Equal(h + tuning.CapsuleHalfHeight, continued.Move.Position.Y);
Assert.Equal(beforeContinuation.Position, continued.Move.Position);
Assert.Equal(beforeContinuation.Move.JumpBufferRemaining, continued.Move.JumpBufferRemaining);
Assert.True(lowRoofAccepted);
Assert.True(wadeBoundaryAccepted);
Assert.False(forbiddenImportAccepted);
Assert.Equal(sourceOwnerBefore, sourceOwnerAfterRefusal);
Assert.Equal(epochBefore, epochAfterRefusal);
Assert.Equal(stateBefore, stateAfterRefusal);
// CellRuntimeSharesOneBindingAndReleasesItsOwnedViewBeforePhysics
Assert.Same(runtimeBinding, steppingBinding);
Assert.Same(runtimeBinding, settlingBinding);
Assert.Same(runtimeBinding, admissionBinding);
Assert.Equal(new[] { "movement-view", "physics" }, disposalOrder);
Assert.Equal(1, movementViewDisposeCount);
Assert.NotSame(oldRuntimeBinding, recreatedCellBinding);
```

For h=0 use exact centre `0.75f`, low roof Y `1.5005f`, and Task 2's Wade spatial shore tangent for acceptance. Add a refused teleport/import exactly on the inclusive depth line, asserting unchanged pose/epoch/ownership. Correction replay validates the authority's exact state and never settles it into a different pose. Failed basis publication remains deferred as today. A client declaration restricted to XZ `[-8,8]` holds an outside-island command even if server `[-20,20]` can advance. Scope equality is per binding/frame, not identical between differing server/client islands.

- [ ] **Step 3: Run the new network tests and confirm missing wiring fails.**

```bash
build-slot --label p5a-5-red -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~OrdinaryContactHeadTests|FullyQualifiedName~OrdinaryContactConfigurationTests|FullyQualifiedName~OrdinaryContactReplayTests|FullyQualifiedName~OrdinaryContactLifecycleTests'
```

- [ ] **Step 4: Resolve exactly one binding at each head or cell configuration point.**

Add the Movement project reference, update its living dependency declarations, and validate before allocating network resources where possible. Convert SamplerSpace.Frame delegates back to absolute providers before ordinary environment construction, including Y conversion for physics origins. Apply ordinary water/registration/boundary config. Contact plus PublicExplicit is two selections and refuses. Old constructor chaining must not validate an incomplete contact configuration before the full overload supplies registration/field/boundary.

- [ ] **Step 5: Replace every explicit-configuration execution branch with the effective binding.**

Convert simulator step/read validation, system stepping, single-server spawn/resume/placement, sharded teleport/reset, cell setup/settling/import/continuation, client prediction and reconciliation. Current-state branch inventory is `PlayerMoveSimulator.cs:144`, `WorldServer.Placement.cs:37,53`, `ShardedWorldServer.Placement.cs:33,45`, `ShardedWorldServer.Frame.cs:128,145,158`, `ExplicitCellAdmission.cs:65-67`, and `PlayerMoveSimulator.Explicit.cs:31,50,65,106`. Read-publication doors are `WorldServer.Sessions.cs:37`, `WorldServer.ResumeSpawn.cs:84`, `WorldServer.cs:542`, `PlayerMovementSystem.cs:163-166`, `WorldClient.cs:433`, `WorldClient.ExplicitMovement.cs:16-33`. Retain explicit configuration checks solely while resolving PublicExplicit and invoking its scope callback.

Wire contact cell runtimes before spawn/restore/import, after installer factories can publish their registration. Cell creation's existing callback order precedes the public CellCreated event (`ShardedWorldServer.cs:128-133`), so contact factories must install/snapshot from PhysicsWorldFactory or their own cell factory, rather than expecting CellCreated to have run. Dispose runtime bindings on CellRemoved and host teardown. Failed constructors release an engine-created view, while borrowed views remain usable. Never share a binding's sequential workspace across cell worlds.

- [ ] **Step 6: Verify network parity, lifecycle, generation 14 and existing explicit heads.**

```bash
build-slot --label p5a-5-network -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~OrdinaryContact|FullyQualifiedName~Explicit|FullyQualifiedName~PlayerMove|FullyQualifiedName~WaterExcursion|FullyQualifiedName~Wire|FullyQualifiedName~WorldServerSelfRescue|FullyQualifiedName~WorldServerRebuild|FullyQualifiedName~WorldClientReconnect'
```

Expected all pass. Add `WireGenerationRemainsFourteen` with `Assert.Equal(14, MoveProtocol.WireProtocolVersion)` against the released `public const int` (`MoveProtocol.cs:70`). No codec, persistence payload, prediction record or golden changes.

- [ ] **Step 7: Commit network integration with explicit paths.**

Commit subject `feat(networld): bind ordinary contact movement across lifecycle doors`.

### Task 6: Local context, approach pacing, CharacterController3D and resolver-1 refusal

**Files:**
- Create: `KhaozEngine.Movement/GroundMoveContext.Contact.cs`, `KhaozEngine.Game.Render3D/CharacterController3D.Contact.cs`.
- Modify: `KhaozEngine.Movement/GroundMoveContext.cs:11-41,58-97`, `GroundMoveContext.Coordinates.cs:13-53`, `SwimPace.cs:14-25`, `MoveToRange.cs:131-139`, `DirectMoveToRange.cs:114-120` for policy-aware point safety, `AssemblyInfo.cs:3-4` for the MapDoc.Physics friend.
- Modify: `KhaozEngine.Game.Render3D/CharacterController3D.cs:19-21,67-147,159-199`, `KhaozEngine.Game.Render3D/KhaozEngine.Game.Render3D.csproj:3-16` for Movement reference/description, root `README.md` dependency row.
- Modify: `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs:112-132`. Test: `KhaozEngine.MapDoc.Physics.Tests/MapPhysicsRegistrationTests.cs` resolver-1 contact refusal rows.
- Test: `KhaozEngine.Movement.Tests/OrdinaryContactLocalHeadTests.cs`, `KhaozEngine.Game.Tests/Game/CharacterController3DContactTests.cs`.

**Interfaces:**
- GroundMoveContext already accepts a borrowed view and checks SourceWorld (`GroundMoveContext.cs:23-40`), and precise stepping uses `CharacterMovement.StepTowards(...,bool preserveSmallMagnitude,...)` (`CharacterMovement.PreciseDirection.cs:12-21`). Existing callers use `NpcGroundMovement.Step/Hold` (`NpcGroundMovement.cs:11-24`), `MoveToRange.Tick(in MoveState,in MoveTuning,in ReachTarget,float,bool,float,GroundMoveContext)` (`MoveToRange.cs:67-68`) and `DirectMoveToRange.Tick(in MoveState,in MoveTuning,in ReachTarget,float,bool,bool,float,GroundMoveContext)` (`DirectMoveToRange.cs:46-47`). Keep those signatures.
- Produces on `KhaozEngine.Movement.GroundMoveContext`:

```csharp
public GroundMoveContext(Func<float,float,float> groundHeight,
    Func<float,float,Vector3>? groundNormal, IPhysicsWorld? physics,
    Func<float,float,Vector2>? clampXz, Func<float,float,float,MovementMedium>? medium,
    IPhysicsWorldQueryView? movementQueries, IMovementWaterField? movementWaterField,
    OrdinaryMovementRegistration? movementRegistration, MovementBoundary? movementBoundary);
public IMovementWaterField? MovementWaterField { get; }
public OrdinaryMovementRegistration? MovementRegistration { get; }
public MovementBoundary? MovementBoundary { get; }
public bool SupportsContactController { get; }
public void Dispose();
internal void RestrictToLegacy(string refusalMessage);
internal bool IsSwimmingForPacing(in MoveState body, in MoveTuning tuning, in MovementMedium medium);
```

Make GroundMoveContext IDisposable. Old constructor signatures stay. Capability is captured at construction, before the first contact call. New overload builds/owns the effective view once when needed, or borrows MovementQueries. ValidateTuning caches the last validated tuning by equality and one ordinary binding, replacing/disposal on tuning changes. A contact-capable construction without medium may have null field. Medium exposes a once-created absolute-coordinate point delegate derived from field.SampleCentreWater, using the tuning last admitted by ValidateTuning. Existing callers validate before pacing. IsSwimmingForPacing uses tuning.Water to suppress SurfaceSwimmer behavior for WadeOnly/DryOnly, leaving legacy ResolveSwimming unchanged.

Add `InternalsVisibleTo("KhaozEngine.MapDoc.Physics")` to Movement/AssemblyInfo.cs for RestrictToLegacy, since MapPhysicsRegistration's factory takes no tuning and can enforce refusal only on a later contact use. It calls RestrictToLegacy with `Resolver-1 MapDoc contexts are legacy-only. Native contact movement requires G1b.` Preserve the context factory's current public overloads.

- Produces on `KhaozEngine.Game.CharacterController3D`, kept in its Game.Render3D assembly, now partial and IDisposable:

```csharp
public MovementControllerKind Controller = MovementControllerKind.Legacy;
public float FootRadiusFraction = 0.5f;
public WaterTraversalPolicy Water = default;
public void Update(in InputState input, float dt, float cameraYaw,
    Func<float,float,float> groundHeight, Func<float,float,Vector3>? groundNormal,
    IPhysicsWorld? physics, Func<float,float,float,MovementMedium>? medium,
    IMovementWaterField? movementWaterField,
    OrdinaryMovementRegistration? movementRegistration, MovementBoundary? movementBoundary);
public void Dispose();
```

Keep the released Update overload and forward it to the full overload with null new arguments. It mirrors the existing mutable-field style, composes all old tuning fields unchanged plus the three new properties, and caches/revalidates by tuning equality and reference identity of physics, field, registration, boundary, medium and analytic delegates. Field generation and physics/frame currentness stay checked by the pin. Keep InputState consumption/CharacterFacing mapping at `CharacterController3D.cs:170-174`, with no raw input. For contact Update, move state into the physics-local frame, acquire the constant ordinary scope, step and publish _state before release, convert back to absolute position. Add `internal MoveState MovementState => _state` in the Contact partial for the existing Game.Tests friend to assert complete state equality. Dispose/reconfigure releases only owned view/binding.

- [ ] **Step 1: Write the local matrix and refusal tests.**

Use dry and wet worlds from Task 3, precise direction `(0.0001f,0)`, and a triggered stair row. Test GroundMoveContext via NpcGroundMovement, MoveToRange through an explicitly authored test graph, DirectMoveToRange via its returned steering and NpcGroundMovement, and CharacterController3D through real InputState key bits.

```csharp
// LocalHeadsMatchOrdinaryCompositionDryWetAndPreciseInput
Assert.Equal(ordinaryReference.State.State, npcResult);
Assert.Equal(ordinaryReference.State.State, rangeDrivenResult);
Assert.Equal(ordinaryReference.State.State, directDrivenResult);
Assert.Equal(dryReference, ordinaryDryResult);
Assert.True(preciseResult.Position.X > preciseInput.Position.X);
Assert.True(stairResult.ClimbRateEwma > 0f);
// CharacterControllerConsumesInputOnItsLeasedBranch
Assert.Equal(ordinaryReference.State.State.Position, controller.Position);
Assert.Equal(ordinaryReference.State.State.FacingYaw, controller.FacingYaw);
Assert.Equal(ordinaryReference.State.State.Swimming, controller.Swimming);
Assert.Equal(ordinaryReference.State.State.ClimbRate, controller.ClimbRate);
Assert.Equal(ordinaryReference.State.State.StepDeltaY, controller.StepDeltaY);
Assert.Equal(ordinaryReference.State.State, controller.MovementState);
// IncapableContextRefusesOnFirstContactUseAndCachesOnlyEqualTuning
Assert.False(incapable.SupportsContactController);
Assert.Throws<NotSupportedException>(FirstContactStep);
Assert.Equal(1, validationCountAfterEqualTuning);
Assert.Equal(2, validationCountAfterChangedFoot);
Assert.Throws<ArgumentException>(ContactWithZeroGravity);
Assert.Contains("G1b", Assert.Throws<NotSupportedException>(ResolverOneContactStep).Message);
// MutableControllerRevalidatesAndBorrowedViewStaysUsable
Assert.Throws<ArgumentOutOfRangeException>(UpdateAfterInvalidFootChange);
Assert.Equal(beforeInvalidChange, controller.Position);
Assert.Equal(1, disposedOwnedViewCount);
Assert.Equal(MovementStepOutcome.Advanced, secondBorrowerResult.Outcome);
```

Include first-use zero FallSpeed/SwimEnter, missing boundary/registration/field with delegate, capability lacking sweep, failed view creation and equality-cache invalidation. Reconfigure controller world, field, registration, boundary, medium and ground delegates independently. Compare all accessible controller signals and the internal MovementState through the existing Game.Tests friend, without reflection or a public state API.

- [ ] **Step 2: Pin field-derived approach pacing to selected policy.**

```csharp
// ApproachPacingHonoursSelectedWaterPolicy
Assert.Equal(fieldSurfaceY, context.Medium!(x,z,feetY).WaterSurfaceY);
Assert.False(context.IsSwimmingForPacing(wadeBody, wadeTuning, point));
Assert.False(context.IsSwimmingForPacing(dryBody, dryTuning, point));
Assert.True(context.IsSwimmingForPacing(swimBody, swimmerTuning, point));
Assert.Equal(RangeMoveStatus.Suspended, directSwimmer.Status);
Assert.Equal(ordinaryReference.State.State, wadingDirectDrivenResult);
Assert.Equal(geometryExpectedSwimPace, swimmerTravelBound);
```

Use WadeOnly for DirectMoveToRange's A2 crossing, which must progress/slide without becoming a swimmer. Add an explicit SurfaceSwimmer suspension row preserving its released behavior (N5). MoveToRange's swimmer row uses an authored aquatic test graph with SteerWhileSwimming enabled. Neither test builds contact navigation nor changes graph compatibility, bake format, arrival tolerance or raw layers.

- [ ] **Step 3: Run new local tests and confirm missing selection/field route fails.**

```bash
build-slot --label p5a-6-red-context -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter FullyQualifiedName~OrdinaryContactLocalHeadTests
build-slot --label p5a-6-red-controller -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter FullyQualifiedName~CharacterController3DContactTests
```

- [ ] **Step 4: Implement cached local binding and policy-aware point pacing.**

GroundMoveContext.Step preserves its recursion/origin guards and absolute conversion, selecting the binding after ValidateTuning. Contact motion always uses trace through the lease. Medium's point query governs pacing/settling only. Keep legacy DryContext behavior and GroundTraversalProbe unchanged, since navigation stays legacy-only. The new context's dry helper is never a route around contact water proof. Change SwimPace, MoveToRange settling and DirectMoveToRange.AllowsStep's next-step classification to call IsSwimmingForPacing rather than classify contact policies with the legacy threshold alone. DirectMoveToRange's point safety checks remain conservative, with no new swimmer steering.

- [ ] **Step 5: Implement CharacterController3D's leased branch and resolver-1 restriction.**

Compose tuning once per Update, validate before changing _state, and update the binding only on the specified cache-key change. No hidden dry dispatch from the contact branch. MapPhysicsRegistration records its restriction at context creation, so every later driver gets the G1b refusal before stepping. Add/update package dependency documentation with the Game.Render3D reference, preserving no renderer dependency in Movement.

- [ ] **Step 6: Verify local drivers, controller inputs and MapDoc refusal.**

```bash
build-slot --label p5a-6-movement -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~OrdinaryContactLocalHeadTests|FullyQualifiedName~GroundMoveContextTests|FullyQualifiedName~NpcGround|FullyQualifiedName~MoveToRange|FullyQualifiedName~DirectMoveToRange|FullyQualifiedName~SwimPace'
build-slot --label p5a-6-game -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter FullyQualifiedName~CharacterController3D
build-slot --label p5a-6-map -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter FullyQualifiedName~MapPhysicsRegistrationTests
```

Expected all pass, unchanged existing legacy driver/controller tests, no guard edits and zero warnings.

- [ ] **Step 7: Commit local-head integration with explicit paths.**

Commit subject `feat(movement): select ordinary contact in local heads`.

### Task 7: Documentation, allocation inventory, cost sample and final verification

**Files:**
- Modify: `CHANGELOG.md:8-32`, `docs/USING-KHAOZENGINE.md` matching Locomotion/Movement/NetWorld/TileWorld sections, `docs/DEPENDENCY-SEAMS.md` movement/physics composition entries, `docs/INDEX.md` controller status row.
- Modify: `KhaozEngine.Locomotion/README.md`, `KhaozEngine.Movement/README.md:49-88,845-865`, `KhaozEngine.NetWorld/README.md`, `KhaozEngine.TileWorld.Physics/README.md:18-42`, `KhaozEngine.Game.Render3D/README.md:44-65`.
- Modify stale text: `KhaozEngine.Server.Tests/NetWorld/ShardedClimbSignalExportTests.cs:111-114`, `docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md:210-218`. The CheckMedium and ExplicitMovementProfile stale comments already change in Task 1.
- Create: `KhaozEngine.Server.Tests/NetWorld/OrdinaryContactAllocationTests.cs`, `docs/superpowers/plans/proofs/2026-10-13-contact-controller-phase5a-cost.md`.
- Modify: `KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj` to link `../KhaozEngine.Game.Tests/Locomotion/Contacts/SupportAllocationRegion.cs` once. Keep its namespace `KhaozEngine.Tests.Locomotion.Contacts` and its checked behavior unchanged.

**Interfaces:**
- Consumes Task 5's simulator read/step and Task 3's binding. Existing checked helper is `SupportAllocationRegion.Start(bool enabled = true,long totalSize = 4 * 1024 * 1024)` (`Game.Tests/Locomotion/Contacts/SupportAllocationRegion.cs:20-25`). Existing lifetime inventory measures physics lease, environment pin, movement lease/release binding (`ContactLifetimeAllocationTests.cs:127-150`), with acquired explicit tick baseline `312` B on .NET 10 arm64 (`ContactLifetimeAllocationTests.StaticGeometry.cs:46-67`).
- Produces no new runtime interface. Allocation test class joins the existing Server.Tests `AllocSensitiveCollection` with `DisableParallelization=true` (`KhaozEngine.Server.Tests/AllocSensitiveCollection.cs:13-14`), with no duplicate definition. Cost proof records runtime/architecture/configuration, exact row/tuning, cold/warm bytes, elapsed sample and token sizes, source commit and guard dispositions.

- [ ] **Step 1: Write the allocation test before recording any cost.**

Measure allocations for each named token on the active runtime outside the tick comparison, including the production OrdinaryMovementEnvironmentPin and PlayerMoveReadScope. Cache witnesses, scope, delegate release bindings and field/registration lookup data before warm measurement. Run one cold and one warm tick under the checked no-GC region, not an AllocAssert wrapper. Use Bepu flat box, SurfaceSwimmer and a water-crossing row as separate functional measurements, without loops/repetition as a stress substitute.

```csharp
// WarmOrdinaryContactTickAllocatesOnlyNamedLifetimeTokensAndReadScope
Assert.Equal(MovementStepOutcome.Advanced, coldOutcome);
Assert.Equal(MovementStepOutcome.Advanced, warmOutcome);
Assert.Equal(physicsLeaseBytes + ordinaryPinBytes + movementLeaseAndReleaseBytes
    + playerReadScopeBytes, warmTickBytes);
Assert.Equal(0, kernelAndFieldWarmBytes);
Assert.Equal(referenceMove, measuredMove);
```

Measure token sizes rather than asserting `312 + guessed read size`, because the production pin layout differs from the fixture. Cache only new head release delegates, leaving MovementEnvironmentContext's released lease/release inventory unchanged. New ordinary lifetime tokens must be named in the proof. Do not change or relax the existing explicit inventory's assertions. Timing is one cold and one warm sample recorded only, no SG-12 pass claim and no threshold assertion.

- [ ] **Step 2: Run the allocation test and inspect any unexplained warm allocation.**

```bash
build-slot --label p5a-7-allocation -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~OrdinaryContactAllocationTests --logger 'console;verbosity=normal'
```

Expected named-token equality and zero warm kernel/field allocations. If it fails, trace the single measured allocation source, fix it, and rerun only that focused test for the new change. Never repeat tests to average a failure away. Save the single cold/warm sample and actual runtime values in the proof file.

- [ ] **Step 3: Complete the living documentation sweep and staged changelog entry.**

```bash
git grep -n -w -e MoveTuning -e GroundMoveContext -e CharacterController3D -e ExplicitMovementProfile -e IPhysicsSupportFootprint -e TileMediumSampler -e ExplicitMovement -- '*.md'
```

Explain selector defaults, all three validation paths, ordinary zero versus explicit 0.001, field/canonical registration/declared-domain setup, view lifetime, each head's configuration, borrowed explicit reads, G1b and 5b refusals, and unchanged generation 14. Correct the two listed stale comments and design capability name to IPhysicsSupportFootprint. Append 5a's player/consumer-facing capability to `## 20.34.0`, do not bump or tag. Re-read current version/tags at execution. If 20.34.0 was tagged meanwhile, stop and let the controller apply P5A-16 rather than violate this brief's staged-version constraint. Keep the remaining adoption gates and #1299 navigation acceptance open.

- [ ] **Step 4: Run the full Release build once from the worktree root.**

```bash
mkdir -p local-feed
build-slot --label p5a-final-build -- dotnet build KhaozEngine.slnx -c Release
```

Expected exit 0, zero errors and zero warnings.

- [ ] **Step 5: Run the full Release suite once, then the required whitespace and repository guards.**

```bash
build-slot --heavy --label p5a-final-tests -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
build-slot --label p5a-final-format -- dotnet format whitespace KhaozEngine.slnx --verify-no-changes --no-restore
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected all exit 0, zero failed tests and no whitespace modifications required. GPU-gated skips are expected, 5a is headless. Do not retry the full suite without a new code change or a diagnosed failure. KESIZE growth needs an owner decision, never update a baseline to bypass it. Run additional relevant guards demanded by hooks and fix failures as written.

- [ ] **Step 6: Commit documentation, measured proof and final validation changes with explicit paths.**

Commit subject `docs(movement): document ordinary contact runtime and validation`. Report each acceptance's test evidence, the unchanged v1 fingerprint, only Task 1's two P5A-14 guard dispositions, final command exit codes and the plan-note dispositions to the controller. Leave integration/push/release to the controller. A 5a completion does not complete #438 phase 5 or #1299.

## Self-review record

- Spec coverage: P5A-1/9/13/14 in Task 1, P5A-3 in Task 2, P5A-4/5/6 in Tasks 3/4, P5A-7/8/11 in Task 5, P5A-8/10/12 in Task 6, P5A-15/16 and A10 in Task 7. A1 to A10 have owners in the acceptance table.
- Step scan: tests pin names, assertions and exact spec values. Code steps define decisions/signatures and leave bodies to implementers, except the exact finite-column algorithm. Verification steps give commands and expected outcomes. Commits use explicit task paths.
- Type consistency: field query/output identity is explicit, composition stays internal, head callbacks stay NetWorld-owned, Movement holds the shared binding and registration lives in Locomotion so TileWorld's production edges stay frozen.
- Review Focus: each of the five listed classes has a named test in its owning task. Shared view lifetime and point pace are checked through the actual consuming heads.
- Proportion: seven tasks preserve the spec's boundaries. No method implementation transcript, new navigation work, hosted proof or game adoption is included. The source/spec conflicts below require the controller's disposition before execution.

## Plan notes for the controller

1. **N1, registration generation.** Bepu's GeometryGeneration advances on every Step and Rebase (`BepuPhysicsWorld.cs:419-421`, `BepuPhysicsWorld.Rebase.cs:57`, `QueryLease.cs:53-55`), so a permanently generation-bound installer snapshot would stale ordinary heads every tick. Proposed resolution is the optional StaticGeometryGeneration seam in Task 3. Capture the current physical generation per pin, preserve canonical registration only while static topology stays current, and retain strict original-generation fallback for other backends.
2. **N2, Level extent.** P5A-6 promises all XZ while P5A-5 requires a finite expanded float scope. An all-XZ field extent cannot itself be expanded into MovementQueryScope (`MovementQueryScope.cs:19-23`). Proposed resolution is finite represented-XZ field bounds plus the installer's finite resident horizontal declaration for Level certification. Include its whole finite vertical interval, but do not union float.MaxValue horizontal bounds into the served domain. The controller must accept this interpretation or amend the Level contract before execution.
3. **N3, position-only direct Step.** `CharacterMovement.Step(Vector3,...)` has no carried state or physics argument (`CharacterMovement.cs:42-45`), unlike the stateful API the spec's dry parity relies on. Proposed resolution is an explicit Contact refusal naming the stateful overload, with its legacy arithmetic unchanged.
4. **N4, field result identity.** MovementMediumSweepQuery carries Body and Delta, while MovementCoverageResult requires MovementQueryIdentity (`MovementWaterCoverage.cs`). Proposed resolution adds the identity argument to IMovementWaterField.TraceWater so the pin can supply its actual lease identity without mutable ambient field state.
5. **N5, DirectMoveToRange water parity.** DirectMoveToRange deliberately suspends swimmers and does not steer them (`DirectMoveToRange.cs:19-22,46-47`). Proposed resolution tests A2's water crossing under WadeOnly and retains SurfaceSwimmer suspension. Certified swimmer motion remains on NpcGroundMovement/MoveToRange. If A2 requires this driver to steer swimmers, that is a separate owner behavior decision.
6. **N6, resolver-1 configuration point.** MapPhysicsRegistration.CreateLegacyMoveContext takes no tuning (`MapPhysicsRegistration.cs:115-132`). Proposed resolution records a legacy-only marker at factory creation and refuses contact tuning at GroundMoveContext's first-use configuration check, naming G1b, through a narrow Movement friend entry.
7. **N7, TileWorld snapshot and residency.** TileCollider has no provenance and TileGroundSampler retains a mutable document (`TileCollider.cs:9`, `TileGroundSampler.cs:32-40`). Proposed resolution preserves legacy public outputs, captures provenance alongside collider creation and adds captured analytic data for the certified registration. Reject a rectangle declaring non-resident analytic holes until the producer can certify those holes, rather than making a Known-empty claim from a legacy zero-height fallback.
8. **N8, package and lifetime integration.** NetWorld and Game.Render3D lack a Movement reference, and WorldServer lacks Dispose (`NetWorld.csproj:11-18`, `Game.Render3D.csproj:10-15`, `WorldServer.cs:157`). Proposed resolution adds the composition dependency and explicit binding lifetime surfaces in Tasks 5/6 while preserving caller-owned physics/transports and TileWorld's pinned production edges.
