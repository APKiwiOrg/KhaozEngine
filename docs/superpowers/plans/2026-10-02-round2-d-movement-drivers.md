# Round 2 D: Range Steering, NPC Movement and Client Path Commands Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move NPC ground bodies and generate ordinary client walk-up/chase commands through one shape-range steering core, with real physics and client/server acceptance.

**Architecture:** MoveToRange owns a region follower and produces world direction plus status. NpcGroundMovement resolves that direction through the shared context. PlayerPathMovement transforms it into a precise MoveCommand, leaving prediction and authority on their existing path.

**Tech Stack:** C#/.NET 10, xUnit, Movement, Navigation, Locomotion, Bepu in tests and NetWorld loopback acceptance. Rides the round's selected version, nominally 20.18.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-2-DESIGN-2026-10-02.md`, sections 6 to 9, D3 to D6 and D9 to D16. Approved 2026-10-02. Branch 4 of 4. Read all three prerequisite Outcome blocks before starting.

## Global Constraints

- The owner approved the design, freed the build slot, chose subagents and authorized execution through the whole program. A, B and C are complete. D executes with explicit root clearance before every build or test command.
- Execution worktree `/Users/antonio/KhaozEngine/.worktrees/round2-movement-drivers`, branch `feature/round2-movement-drivers`, from reconciled engine main containing A, B and C.
- Ride A's selected 20.x version, nominally 20.18.0. Extend its entry. Do not bump or tag independently.
- Movement keeps exactly its three references. Only Server.Tests adds NetWorld acceptance wiring. No entity, brain, archetype, action queue or game dependency enters Movement.
- Range is observed shape distance with zero client tolerance. Grimhollow's server tolerance, target validity, manual cancellation and action dispatch remain game-owned.
- NPC steps publish one accepted state per simulation tick. Player adapter never writes MoveState or a server player position. Commands carry no dt and no jump request.
- An unreachable, partial-waiting or unsupported route never becomes an unconditional direct press. Suspended ground automation does not modify an airborne arc or movement commitment.
- One building worker. Focused tests per task, one full Release suite at finish. No repeated or stress tests. Reviews per task and whole branch. Workers do not integrate, push, pack or tag.
- Zero warnings and no file-size baseline growth. Test namespaces under KhaozEngine.Tests. No em/en dashes or prose semicolons. Record departures in Outcome before integration.

## Review Focus

1. Speed scale or a fast tick can skip a mandatory corridor turn or stop ring. Task 1 `FastScaledApproachCapsTheWaypointAndRangeTravel`.
2. A tall target directly above the body must not count as arrived from XZ proximity. Task 1 `DifferentHeightAndHalfHeightDoNotProduceFalseArrival`.
3. A failed route or cooldown gap can escape a cow pen through direct approach. Task 1 `EveryFallbackKeepsTheAreaGuard`.
4. Camera yaw 0 faces -Z, unlike a copied visual-yaw convention. Task 3 `CommandUsesTheEngineCameraBasisAtEveryQuadrant`.
5. An internal fractional vector can pass unit tests while its transmitted command runs at full speed. Task 4 `FractionalWalkUpSurvivesTheRealClientServerPath`.

---

### Task 1: Shared region and stop-ring steering

**Files:**
- Create `KhaozEngine.Movement/MoveToRange.cs`, `MoveToRange.Goal.cs`, `MoveToRange.Approach.cs`, `RangeSteering.cs` and `RangeMoveStatus.cs`.
- Create `KhaozEngine.Movement.Tests/MoveToRangeTests.cs` and `MoveToRangeAreaTests.cs`.

**Interfaces:**
- Enum RangeMoveStatus has Following, InRange, WaitingForPath, Unreachable, UnsupportedTransition and Suspended.
- `readonly record struct RangeSteering(Vector2 WorldDirection,RangeMoveStatus Status)`.
- `MoveToRange(GroundNavigation navigation,PathFollowConfig? follow = null)` and explicit low-level `MoveToRange(IRegionPathPlanner planner,NavSpace space,Func<Vector3,Vector3,bool> allowsSegment,PathFollowConfig? follow = null)`.
- `RangeSteering Tick(in MoveState body,in MoveTuning tuning,in ReachTarget target,float range,bool run,float dt,GroundMoveContext context)` and `void Reset()`.
- Use B's region follower and C's exact reach and profile contracts. Cached region membership seats this body's capsule at the candidate feet. The bounding circle is a conservative search extent only.
- Reject invalid/nonfinite inputs and mismatched profile geometry. Suspended means zero requested direction while airborne or committed. InRange means the supplied current body already passes exact reach.

- [x] **Step 1: Write tests.** Define a small scripted IRegionPathPlanner and real flat/profile fixtures in the test project. Include all Review Focus 1 to 3 scenarios, target motion, differing mover/target half-heights, solid box with reachable far face, final fraction below 0.001, no region member, a Hop waypoint and reset during cooldown. Check the final body position independently of reported status.
  ```csharp
  Assert.Equal(RangeMoveStatus.Following, outside.Status);
  Assert.InRange(outside.WorldDirection.Length(), 0f, 1f);
  Assert.Equal(RangeMoveStatus.InRange, actualMember.Status);
  Assert.Equal(Vector2.Zero, waiting.WorldDirection);
  Assert.Equal(RangeMoveStatus.UnsupportedTransition, hop.Status);
  Assert.Equal(RangeMoveStatus.Suspended, airborne.Status);
  Assert.False(areaWasEscaped);
  ```
  Cover speed scale 2, run 5 and dt=1/30, plus a larger but bounded dt to catch waypoint skipping. The script must implement both planner overloads and count replans. Reset has to plan fresh rather than waiting on the previous target's cooldown.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~MoveToRangeTests|FullyQualifiedName~MoveToRangeAreaTests'`. Expected missing API or behavior failure.
- [x] **Step 3: Implement range state and approach.** First measure observed reach, then plan with own feet. Target translation uses follower drift/cooldown. Changes to shape kind, dimensions, box yaw or range reset the route. Target identity, teleport and manual cancellation require caller Reset. Cap far-field travel to the next mandatory waypoint. Use the context's grounded commanded-travel bound including state speed scale and any pace multiplier above 1. Slowing medium only shortens travel. Compute near-field intersection against the actual target shape at the predicted support height, shrinking the magnitude without adding server tolerance. Validate the direct segment's area/graph and ground-step resolution. If it is blocked, retain the detour. Any preflight uses a copy of MoveState, with no world step or entity write. An unresolved vertical gap holds or continues the valid route, never teleports.
- [x] **Step 4: Run** the same filter, then `FullyQualifiedName~PhysicsNavProfileTests|FullyQualifiedName~ReachGeometryTests` in Movement.Tests. Expected nonzero and zero failures. Report InRange only from observed geometry. Check final distance, not an expected command destination.
- [x] **Step 5: Commit.** Subject `feat(movement): steer ground bodies to exact shape range`. Obtain task review.

### Task 2: NPC stepping and settling

**Files:**
- Create `KhaozEngine.Movement/NpcGroundMovement.cs`.
- Create `KhaozEngine.Movement.Tests/NpcGroundMovementTests.cs` and `NpcRangeNavigationAcceptanceTests.cs`.

**Interfaces:**
- `MoveState NpcGroundMovement.Step(in MoveState body,in RangeSteering steering,bool run,float dt,in MoveTuning tuning,GroundMoveContext context)`.
- `MoveState NpcGroundMovement.Hold(in MoveState body,float dt,in MoveTuning tuning,GroundMoveContext context)`.
- Both delegate to C's one context Step. Hold requests zero direction and still resolves support and gravity. Failed/waiting/unsupported/suspended steering requests zero horizontal direction, without clearing carried state.

- [x] **Step 1: Write tests.** Real physics wall detour, stop-ring approach at range 0.6, footprint box interaction at 1.5, wading medium forwarding, idle settling, bounds, slope refusal and post-teleport Reset. Use a synthetic pen profile and a wider body to prove actual geometry, not only a planner radius. Assert no penetration and no range overshoot beyond numerical test tolerance for commanded grounded motion.
  ```csharp
  Assert.True(ReachGeometry.Within(finalBody, target, requestedRange));
  Assert.InRange(finalDistance, requestedRange - 0.001f, requestedRange);
  Assert.False(world.ComputePenetration(capsule, localFinalPose, out _));
  Assert.True(wetTravel < dryTravel);
  Assert.True(held.Grounded);
  ```
  finalBody comes from actual returned MoveState and this tuning's dimensions. localFinalPose subtracts the current physics origin. Numerical assertion tolerance is not a hidden gameplay reach tolerance.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~NpcGroundMovementTests|FullyQualifiedName~NpcRangeNavigationAcceptanceTests'`. Expected missing adapter API or proof failure.
- [x] **Step 3: Implement the adapters only.** No archetype speeds, facing-to-target edits, FrostChill, NpcAgent, phase transition or lunge logic. A consumer publishes the returned state once and rechecks current reach after the step.
- [x] **Step 4: Run** the same filter. Expected nonzero, zero failures and actual final geometric reach. Do not satisfy arrival assertions by snapping the position.
- [x] **Step 5: Commit.** Subject `feat(movement): resolve NPC range steering through the shared ground core`. Obtain task review.

### Task 3: Client commands in the real camera basis

**Files:**
- Create `KhaozEngine.Movement/PlayerPathMovement.cs`.
- Create `KhaozEngine.Movement.Tests/PlayerPathMovementTests.cs`.

**Interfaces:**
- `MoveCommand PlayerPathMovement.Command(in RangeSteering steering,bool run,float cameraYaw)`.
- Use A's six-argument constructor with ScaleSpeedByAxis true, Jump false and FaceCamera false. Return idle requested motion for all non-Following states.
- At camera yaw y, right is `(cos(y),-sin(y))` and forward is `(-sin(y),-cos(y))` in world XZ. Project WorldDirection onto those axes. Preserve its length in [0,1]. Do not copy atan2 of a rendered NPC yaw.

- [x] **Step 1: Write tests.** Canonical yaw 0, pi/2, pi and -pi/2, diagonal axes, fractional magnitude, tiny fraction, idle, invalid yaw and unsupported/suspended statuses. Read the command through CharacterMovement.CameraRelativeDir and a real CharacterMovement.Step, not only its stored axis.
  ```csharp
  Assert.True(command.ScaleSpeedByAxis);
  Assert.False(command.Jump);
  Assert.False(command.FaceCamera);
  Assert.InRange(Vector2.Distance(expectedWorldDirection, CharacterMovement.CameraRelativeDir(command)), 0f, 0.000001f);
  Assert.Equal(expectedTravel, moved.Position.Z, 6);
  Assert.Equal(Vector2.Zero, idle.Move);
  ```
  Compare all yaw directions within 1e-6, including pi/2 whose cosine rounds. Prediction and server consume the identical command, so their state parity remains exact.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~PlayerPathMovementTests'`. Expected missing API failure.
- [x] **Step 3: Implement the pure command projection.** No raw input, camera object, player state write or netcode reference. Caller chooses run/walk and cancels automation when manual input wins.
- [x] **Step 4: Run** the same filter. Expected nonzero and no failures. Verify actual travel magnitude for a tiny command below the old dead zone.
- [x] **Step 5: Commit.** Subject `feat(movement): emit ordinary precise client path commands`. Obtain task review.

### Task 4: End-to-end player walk-up and chase proof

**Files:**
- Modify `KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj`, explicit Movement reference.
- Modify `KhaozEngine.NetWorld/WorldClient.Frame.cs`, additive read-only `LocalPredictedState` in absolute coordinates, per execution preflight ruling D0.3.
- Create `KhaozEngine.Server.Tests/NetWorld/PlayerRangeMovementAcceptanceTests.cs` and `PlayerRangeMovementTestRig.cs`.

**Interfaces:**
- Test rig owns a populated static physics scene, GroundMoveContext, captured profile, InMemoryTransportHub, normal WorldServer and WorldClient, with 30 Hz configuration and a join handshake. Give the client/server equivalent physics/providers in their normal composition.
- `Submit(in MoveCommand command)` calls the real client SendInput, which predicts, encodes and queues it. `Frame()` polls/ticks/polls normally. `Stop()` submits idle. Read final positions from public client/server state. Model the existing WorldClientLocalMovementTests wiring, extending it only for this fixture.
- Client steering reads `WorldClient.LocalPredictedState.Move`, never the interpolated and correction-offset `LocalRenderState`. The new property returns the existing private predictor's current simulation state as a value copy. Prove the distinction under phase-offset presentation and real reconciliation. This adds no position setter or dependency edge.

- [x] **Step 1: Write bounded headless acceptance.** Client reads its predicted current capsule state, calls MoveToRange.Tick and PlayerPathMovement.Command, and submits exactly once per simulation tick. Route around a real wall to a solid target shape, then chase a moving capsule. Exercise tiny final fraction, target height, target death/cancellation supplied by the test game, manual override followed by Reset, and idle after arrival. Include one authoritative correction while fractional input is unacknowledged. Phase-offset serving from prediction as in the existing reconcile tests.
  ```csharp
  Assert.True(ReachGeometry.Within(authoritativeBody, target, range));
  Assert.Equal(authoritativePosition, reconciledPosition);
  Assert.Equal(18, capturedMoveFrame.Length);
  Assert.True(decodedLastApproach.ScaleSpeedByAxis);
  Assert.Equal(positionAfterManualMove, positionAfterIdle);
  Assert.Equal(Vector2.Zero, commandAfterCancellation.Move);
  ```
  Capture a real transmitted frame through a transport wrapper that forwards it unchanged. Compare prediction and authority at the same acknowledged command/tick, draining pending input before the final equality assertion. Do not call a server-side WalkTo, use a test-only position setter for arrival, or skip the codec. For chase, re-evaluate target and reach each tick. A final server reach check can reject target movement outside game tolerance.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~PlayerRangeMovementAcceptanceTests'`. Expected nonzero. This is a consumer-wiring acceptance gate, even if all components already pass their units.
- [x] **Step 3: Correct only a proven seam failure.** Keep client generation and server authority on the normal command path. Do not solve a failing fraction by moving the player on the server.
- [x] **Step 4: Run** the same filter plus `FullyQualifiedName~PreciseMovementReconcileTests|FullyQualifiedName~ClientReconcileTests`. Expected nonzero and no failures. Inspect commands, final positions and actual shape distances together.
- [x] **Step 5: Commit.** Subject `test(movement): prove range movement through client prediction and authority`. Obtain task review.

### Task 5: Consumer documentation and round completion

**Files:**
- Modify Movement README, docs/USING-KHAOZENGINE.md, root README's package summary and staged CHANGELOG.
- Modify NetWorld README for the simulation-state versus presentation-state contract from D0.3.
- Update the round design's shipped status only after verification, docs/INDEX.md and this plan's Outcome. Read the other three Outcomes and correct a prerequisite record only when the final acceptance found an actual contract change.

- [x] **Step 1: Document complete NPC and client examples.** Show explicit package reference, shared world providers, capture/profile disposal, own feet, target shape, per-tick command submission, reset/cancellation and server reach/tolerance ownership. Sweep every Markdown mention of movement kernel, MoveToRange, player path following and PathFollower arrival. No claim that the engine owns brains or combat rules.
- [x] **Step 2: Fetch and merge current main, then full verification once after slot authorization.**
  ```bash
  mkdir -p local-feed
  dotnet build KhaozEngine.slnx -c Release
  dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
  sh scripts/check-dashes.sh --tree
  sh scripts/check-prose.sh --tree
  sh scripts/check-file-size.sh --tree
  bash scripts/check-doc-versions.sh
  ```
  Expected zero warnings, no failures, nonzero counts and passing guards. Obtain whole-branch review. Record actual API, remaining issues, costs and each departure in Outcome. No local test loops.
- [x] **Step 3: Commit.** Subject `docs(movement): document client and NPC drivers and round D outcome`. The worker documentation commits are `ca8104dd5` and `41fdf85f2`, with reconciliation merge `26372dc8`. Integration owner main merge, push, canonical feed pack and feed verification remain pending. Workers stop at their verified commit.
- [ ] **Step 4: Hand off the released-pin boundary.** Only the owner starts the engine tag using scripts/tag-release.sh. Until then do not close engine #1223 as a released consumer capability. After verification report on Grimhollow epic #399 starting `P4 engine round 2:` with the actual version, interfaces and adoption prerequisites. Grimhollow P4 adopts the tagged pin on feature/continuous-movement and performs its normal vendored-feed and verification workflow. P5 owns shapes, areas, brains, interactions and walk-up. P6 owns combat. This plan edits no Grimhollow files.

## Outcome

Status: Plan D implementation, scoped reviews, consumer documentation and full Release verification are complete
on the reconciled 20.18.0 branch. Whole-branch final review, integration to current engine main, push, canonical
package-feed packing and release remain with the integration owner. No tag or released-pin game adoption is claimed.

### Public API and consumer contract

The exact public surface is:

```csharp
public enum RangeMoveStatus
{
    Following, InRange, WaitingForPath, Unreachable, UnsupportedTransition, Suspended
}

public readonly record struct RangeSteering(Vector2 WorldDirection, RangeMoveStatus Status);

public sealed class MoveToRange
{
    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow = null);
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow = null);
    public RangeSteering Tick(in MoveState body, in MoveTuning tuning, in ReachTarget target,
        float range, bool run, float dt, GroundMoveContext context);
    public void Reset();
}

public static class NpcGroundMovement
{
    public static MoveState Step(in MoveState body, in RangeSteering steering,
        bool run, float dt, in MoveTuning tuning, GroundMoveContext context);
    public static MoveState Hold(in MoveState body, float dt,
        in MoveTuning tuning, GroundMoveContext context);
}

public static class PlayerPathMovement
{
    public static MoveCommand Command(in RangeSteering steering, bool run, float cameraYaw);
}
```

`WorldClient.LocalPredictedState` is the absolute copy of the current predicted simulation state. Use its
`Move` for geometry and command generation. `LocalRenderState` remains the presentation state with interpolation
and correction offsets. Range is observed true 3D shape reach with zero client tolerance. The game supplies the
nominal range, target validity, cancellation and server tolerance, applying that tolerance once at the authority
boundary.

Suspension status takes precedence over `InRange` after input validation and current reach evaluation. Shape,
dimensions, box yaw and range changes reset internally. A changed immutable profile requires a new follower
instance. Caller `Reset` is for target identity changes, teleport, manual input, target death or invalidity and
cancellation. The copied follower acceptance radius is `min(supplied, 1e-5 m)`, including supplied zero, so a
caller can hold at float resolution. A valid partial corridor continues bounded travel until exhaustion. An
exhausted partial corridor waiting on cooldown, an unreachable or refused route, and an unsupported Hop route hold
with zero input. Near-ring approach uses up to 32 live copy-state bisection candidates plus endpoint and final
preflights. It does not step the world or publish an entity.

NPC consumers call the shared context once, publish one accepted state per simulation tick and recheck reach from
the accepted state. Player automation emits ordinary client commands through prediction, the codec and authority.
The engine supplies no server-side player follower, action queue, NPC brain or combat rule. Player commands use
the camera basis with yaw zero facing world negative Z, `ScaleSpeedByAxis=true`, `Jump=false` and
`FaceCamera=false`. Nonfinite or unknown requests become finite idle commands and oversized finite directions
are clamped.

### Task evidence

| Task | Commit | Focused evidence | Adjacent evidence |
| --- | --- | ---: | ---: |
| 1, range steering | `8cecf61f8` | 33 passed | 105 passed |
| 2, NPC stepping | `91cb6a3b7` | 26 passed, 18 adapter and 8 real physics | Included in focused count |
| 3, player commands | `eb203b489` | 38 passed | Included in focused count |
| 4, real client path | `e3917347b` | 7 passed | 8 passed |
| 5, docs and reconciliation | `ca8104dd5`, `41fdf85f2`, `26372dc8` | Documentation commits and reconciled main | Root final verification below |

All four runtime task reviews were approved without findings. Early RED runs were expected missing API failures.
The first nonzero GREEN runs were fixture assumptions or diagnostics that were corrected in tests and reports.
Task 1 reached 33 focused and 105 adjacent passes after its corrections. Task 2 reached 26 focused passes after
replacing an unsupported exact dry displacement promise. Task 4 reached 7 focused and 8 adjacent passes after
switching to the populated-world replay oracle and current-target reach. No runtime kernel fix was required.

### Full verification and guards

Against clean reconciled source at `41fdf85f2`, Release build exited 0 in 35.76 seconds with zero warnings and
zero errors. The standard no-build Release suite with `Category!=LiveSocket` exited 0 across 29 assemblies with
23,580 passed, 0 failed, 1,275 skipped and 24,855 total cases. The dash, prose, file-size, agent-instruction and
doc-version guards all exited 0. No stress run, repeated full run or overlapping build occurred. Every D build and
test command was separately process-gated by root and the worker, with no runtime command overlap.

### D rulings with reason and cost

- D0.1 authorized the full program after owner approval and build-slot release. Reason: the original design-only
  boundary was superseded by explicit owner authorization. Cost: none, and no tagging authority was added.
- D0.2 reports `Suspended` before actionable `InRange` for airborne or committed automation after validation and
  reach evaluation. Reason: ground automation must not advertise arrival while suspended. Cost: callers needing
  pure airborne reach use `ReachGeometry` directly, with no physics state edit.
- D0.3 added only the read-only absolute `LocalPredictedState` copy. Reason: `LocalRenderState` includes
  interpolation and correction offsets and cannot govern exact stop-ring steering. Cost: one additive property and
  documentation and test distinction, with no setter or dependency edge.
- D0.4 allowed independent NPC and pure command work in isolated child worktrees. Reason: the owner requested safe
  concurrency while one build slot remained serialized. Cost: task branches and integration checks.
- D1.1 clamps copied follower `AcceptRadius` to `min(supplied, 1e-5 m)` while preserving supplied zero and all
  other controls. Reason: a broad point tolerance can consume a mandatory physical turn. Cost: tighter waypoint
  tracking and possible holds at float resolution. Generic Navigation behavior is unchanged.
- D1.2 corrected fixtures to assert actual height, footprint and collision convergence. Reason: nominal endpoints
  and native floor tangency do not prove collision-free geometry. Cost: a test-only contact tolerance and bounded
  simulation steps, with no production reach relaxation.
- D1.3 uses up to 32 copy-state shared-core bisection candidates plus endpoint and final preflights. Reason: live
  support, medium, bounds and collision affect actual approach geometry. Cost: bounded live-query overhead on an
  arrival tick, with no world step, entity publish or snap.
- D2.1 allows native floor tangency only with zero lateral MTV and vertical magnitude at most 1e-6. Reason: the
  approved native contact query has float floor tangency. Cost: test-only one-micrometre vertical contact allowance.
- D2.2 replaced an exact dry displacement promise with literal commanded velocity and positive actual dry travel.
  Reason: the native floor sweep subtracts contact skin from the 3D walk and gravity direction. Cost: no unsupported
  exact native-contact travel claim, with wet less than dry and all reach and obstacle assertions retained.
- D3.1 maps malformed, nonfinite and unknown command input to finite idle, storing invalid yaw as zero. Reason:
  avoid invalid wire floats and match the precise-input fail-closed contract. Cost: malformed automation holds
  silently and callers diagnose it upstream.
- D3.2 clamps oversized finite directions with a double norm before camera projection. Reason: bound pace and avoid
  maximum-float overflow. Cost: bounded numeric work and ordinary component rounding.
- D3.3 tests `CameraRelativeDir` unit heading separately from actual fractional travel. Reason: the shipped helper
  returns a unit heading while `Step` consumes the fraction. Cost: test helpers and numeric assertion tolerance only.
- D4.1 uses an independent populated-world `PlayerMoveSimulator` replay oracle over served state and actual commands.
  Reason: native contact changes nominal travel and authority distance. Cost: an eight-line test helper, with no new
  file, friend access or dependency.
- D4.2 uses current-target reach and nominal-ring checks instead of an incidental chase Z direction. Reason: a legal
  ring can be reached through X travel. Cost: no uncontracted directional fixture promise.
- D4.3 corrected stale XML documentation to list the public local-state views. Reason: `LocalPredictedState` is a
  third frame-unwrapped view beside render state and snapshot. Cost: comments only.
- D5.1 revised examples and prose for partial travel, status precedence, internal resets, profile replacement,
  accepted-state publication, one client submission and the game-owned boundary. Reason: the draft examples and
  broad hold wording could mislead consumers. Cost: documentation clarification only.
- D5.2 reconciled current main before full verification and retained both changelog sections. Reason: concurrent
  navigation, physics and server lookup fixes were required for the verified source. Cost: merge and living-guidance
  updates plus one full suite against merged source. Issues 1232, 1236 and 1238 were fixed outside Plan D and are
  not attributed to this implementation.
- D5.3 synchronizes the `MoveToRange.Tick` XML with validated suspension precedence and removes the repeated design
  status that said Plan D was pending. Reason: conflicting comments and status text remained after implementation
  and verification. Cost: comments and status text only, with no runtime change.

### Remaining gates and adoption boundary

Issue 1233 remains an adoption prerequisite for steep meshes and filtered ground. Small local physics coordinates
or rebasing remain required for physics precision. No full Hollowmere startup guarantee is claimed. Issue 1238 is
resolved by the concurrent reconciled `PhysicsColumnProbe` progress fix. Whole-branch final review, merge and push
to main, canonical package-feed packing, engine tagging and released-pin Grimhollow adoption remain pending owner
action. This plan changed no Grimhollow files.
