# Round 2 D: Range Steering, NPC Movement and Client Path Commands Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move NPC ground bodies and generate ordinary client walk-up/chase commands through one shape-range steering core, with real physics and client/server acceptance.

**Architecture:** MoveToRange owns a region follower and produces world direction plus status. NpcGroundMovement resolves that direction through the shared context. PlayerPathMovement transforms it into a precise MoveCommand, leaving prediction and authority on their existing path.

**Tech Stack:** C#/.NET 10, xUnit, Movement, Navigation, Locomotion, Bepu in tests and NetWorld loopback acceptance. Rides the round's selected version, nominally 20.18.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-2-DESIGN-2026-10-02.md`, sections 6 to 9, D3 to D6 and D9 to D16. Approved 2026-10-02. Branch 4 of 4. Read all three prerequisite Outcome blocks before starting.

## Global Constraints

- Unexecuted plan. Owner authorization to free the build slot is required before execution. This session authors plans only.
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

- [ ] **Step 1: Write tests.** Define a small scripted IRegionPathPlanner and real flat/profile fixtures in the test project. Include all Review Focus 1 to 3 scenarios, target motion, differing mover/target half-heights, solid box with reachable far face, final fraction below 0.001, no region member, a Hop waypoint and reset during cooldown. Check the final body position independently of reported status.
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
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~MoveToRangeTests|FullyQualifiedName~MoveToRangeAreaTests'`. Expected missing API or behavior failure.
- [ ] **Step 3: Implement range state and approach.** First measure observed reach, then plan with own feet. Target translation uses follower drift/cooldown. Changes to shape kind, dimensions, box yaw or range reset the route. Target identity, teleport and manual cancellation require caller Reset. Cap far-field travel to the next mandatory waypoint. Use the context's grounded commanded-travel bound including state speed scale and any pace multiplier above 1. Slowing medium only shortens travel. Compute near-field intersection against the actual target shape at the predicted support height, shrinking the magnitude without adding server tolerance. Validate the direct segment's area/graph and ground-step resolution. If it is blocked, retain the detour. Any preflight uses a copy of MoveState, with no world step or entity write. An unresolved vertical gap holds or continues the valid route, never teleports.
- [ ] **Step 4: Run** the same filter, then `FullyQualifiedName~PhysicsNavProfileTests|FullyQualifiedName~ReachGeometryTests` in Movement.Tests. Expected nonzero and zero failures. Report InRange only from observed geometry. Check final distance, not an expected command destination.
- [ ] **Step 5: Commit.** Subject `feat(movement): steer ground bodies to exact shape range`. Obtain task review.

### Task 2: NPC stepping and settling

**Files:**
- Create `KhaozEngine.Movement/NpcGroundMovement.cs`.
- Create `KhaozEngine.Movement.Tests/NpcGroundMovementTests.cs` and `NpcRangeNavigationAcceptanceTests.cs`.

**Interfaces:**
- `MoveState NpcGroundMovement.Step(in MoveState body,in RangeSteering steering,bool run,float dt,in MoveTuning tuning,GroundMoveContext context)`.
- `MoveState NpcGroundMovement.Hold(in MoveState body,float dt,in MoveTuning tuning,GroundMoveContext context)`.
- Both delegate to C's one context Step. Hold requests zero direction and still resolves support and gravity. Failed/waiting/unsupported/suspended steering requests zero horizontal direction, without clearing carried state.

- [ ] **Step 1: Write tests.** Real physics wall detour, stop-ring approach at range 0.6, footprint box interaction at 1.5, wading medium forwarding, idle settling, bounds, slope refusal and post-teleport Reset. Use a synthetic pen profile and a wider body to prove actual geometry, not only a planner radius. Assert no penetration and no range overshoot beyond numerical test tolerance for commanded grounded motion.
  ```csharp
  Assert.True(ReachGeometry.Within(finalBody, target, requestedRange));
  Assert.InRange(finalDistance, requestedRange - 0.001f, requestedRange);
  Assert.False(world.ComputePenetration(capsule, localFinalPose, out _));
  Assert.True(wetTravel < dryTravel);
  Assert.True(held.Grounded);
  ```
  finalBody comes from actual returned MoveState and this tuning's dimensions. localFinalPose subtracts the current physics origin. Numerical assertion tolerance is not a hidden gameplay reach tolerance.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~NpcGroundMovementTests|FullyQualifiedName~NpcRangeNavigationAcceptanceTests'`. Expected missing adapter API or proof failure.
- [ ] **Step 3: Implement the adapters only.** No archetype speeds, facing-to-target edits, FrostChill, NpcAgent, phase transition or lunge logic. A consumer publishes the returned state once and rechecks current reach after the step.
- [ ] **Step 4: Run** the same filter. Expected nonzero, zero failures and actual final geometric reach. Do not satisfy arrival assertions by snapping the position.
- [ ] **Step 5: Commit.** Subject `feat(movement): resolve NPC range steering through the shared ground core`. Obtain task review.

### Task 3: Client commands in the real camera basis

**Files:**
- Create `KhaozEngine.Movement/PlayerPathMovement.cs`.
- Create `KhaozEngine.Movement.Tests/PlayerPathMovementTests.cs`.

**Interfaces:**
- `MoveCommand PlayerPathMovement.Command(in RangeSteering steering,bool run,float cameraYaw)`.
- Use A's six-argument constructor with ScaleSpeedByAxis true, Jump false and FaceCamera false. Return idle requested motion for all non-Following states.
- At camera yaw y, right is `(cos(y),-sin(y))` and forward is `(-sin(y),-cos(y))` in world XZ. Project WorldDirection onto those axes. Preserve its length in [0,1]. Do not copy atan2 of a rendered NPC yaw.

- [ ] **Step 1: Write tests.** Canonical yaw 0, pi/2, pi and -pi/2, diagonal axes, fractional magnitude, tiny fraction, idle, invalid yaw and unsupported/suspended statuses. Read the command through CharacterMovement.CameraRelativeDir and a real CharacterMovement.Step, not only its stored axis.
  ```csharp
  Assert.True(command.ScaleSpeedByAxis);
  Assert.False(command.Jump);
  Assert.False(command.FaceCamera);
  Assert.InRange(Vector2.Distance(expectedWorldDirection, CharacterMovement.CameraRelativeDir(command)), 0f, 0.000001f);
  Assert.Equal(expectedTravel, moved.Position.Z, 6);
  Assert.Equal(Vector2.Zero, idle.Move);
  ```
  Compare all yaw directions within 1e-6, including pi/2 whose cosine rounds. Prediction and server consume the identical command, so their state parity remains exact.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~PlayerPathMovementTests'`. Expected missing API failure.
- [ ] **Step 3: Implement the pure command projection.** No raw input, camera object, player state write or netcode reference. Caller chooses run/walk and cancels automation when manual input wins.
- [ ] **Step 4: Run** the same filter. Expected nonzero and no failures. Verify actual travel magnitude for a tiny command below the old dead zone.
- [ ] **Step 5: Commit.** Subject `feat(movement): emit ordinary precise client path commands`. Obtain task review.

### Task 4: End-to-end player walk-up and chase proof

**Files:**
- Modify `KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj`, explicit Movement reference.
- Create `KhaozEngine.Server.Tests/NetWorld/PlayerRangeMovementAcceptanceTests.cs` and `PlayerRangeMovementTestRig.cs`.

**Interfaces:**
- Test rig owns a populated static physics scene, GroundMoveContext, captured profile, InMemoryTransportHub, normal WorldServer and WorldClient, with 30 Hz configuration and a join handshake. Give the client/server equivalent physics/providers in their normal composition.
- `Submit(in MoveCommand command)` calls the real client SendInput, which predicts, encodes and queues it. `Frame()` polls/ticks/polls normally. `Stop()` submits idle. Read final positions from public client/server state. Model the existing WorldClientLocalMovementTests wiring, extending it only for this fixture.

- [ ] **Step 1: Write bounded headless acceptance.** Client reads its predicted current capsule state, calls MoveToRange.Tick and PlayerPathMovement.Command, and submits exactly once per simulation tick. Route around a real wall to a solid target shape, then chase a moving capsule. Exercise tiny final fraction, target height, target death/cancellation supplied by the test game, manual override followed by Reset, and idle after arrival. Include one authoritative correction while fractional input is unacknowledged. Phase-offset serving from prediction as in the existing reconcile tests.
  ```csharp
  Assert.True(ReachGeometry.Within(authoritativeBody, target, range));
  Assert.Equal(authoritativePosition, reconciledPosition);
  Assert.Equal(18, capturedMoveFrame.Length);
  Assert.True(decodedLastApproach.ScaleSpeedByAxis);
  Assert.Equal(positionAfterManualMove, positionAfterIdle);
  Assert.Equal(Vector2.Zero, commandAfterCancellation.Move);
  ```
  Capture a real transmitted frame through a transport wrapper that forwards it unchanged. Compare prediction and authority at the same acknowledged command/tick, draining pending input before the final equality assertion. Do not call a server-side WalkTo, use a test-only position setter for arrival, or skip the codec. For chase, re-evaluate target and reach each tick. A final server reach check can reject target movement outside game tolerance.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~PlayerRangeMovementAcceptanceTests'`. Expected nonzero. This is a consumer-wiring acceptance gate, even if all components already pass their units.
- [ ] **Step 3: Correct only a proven seam failure.** Keep client generation and server authority on the normal command path. Do not solve a failing fraction by moving the player on the server.
- [ ] **Step 4: Run** the same filter plus `FullyQualifiedName~PreciseMovementReconcileTests|FullyQualifiedName~ClientReconcileTests`. Expected nonzero and no failures. Inspect commands, final positions and actual shape distances together.
- [ ] **Step 5: Commit.** Subject `test(movement): prove range movement through client prediction and authority`. Obtain task review.

### Task 5: Consumer documentation and round completion

**Files:**
- Modify Movement README, docs/USING-KHAOZENGINE.md, root README's package summary and staged CHANGELOG.
- Update the round design's shipped status only after verification, docs/INDEX.md and this plan's Outcome. Read the other three Outcomes and correct a prerequisite record only when the final acceptance found an actual contract change.

- [ ] **Step 1: Document complete NPC and client examples.** Show explicit package reference, shared world providers, capture/profile disposal, own feet, target shape, per-tick command submission, reset/cancellation and server reach/tolerance ownership. Sweep every Markdown mention of movement kernel, MoveToRange, player path following and PathFollower arrival. No claim that the engine owns brains or combat rules.
- [ ] **Step 2: Fetch and merge current main, then full verification once after slot authorization.**
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
- [ ] **Step 3: Commit.** Subject `docs(movement): document client and NPC drivers and round D outcome`. Integration owner merges and pushes main, serializes the shared-feed pack and verifies the feed. Workers stop at their verified commit.
- [ ] **Step 4: Hand off the released-pin boundary.** Only the owner starts the engine tag using scripts/tag-release.sh. Until then do not close engine #1223 as a released consumer capability. After verification report on Grimhollow epic #399 starting `P4 engine round 2:` with the actual version, interfaces and adoption prerequisites. Grimhollow P4 adopts the tagged pin on feature/continuous-movement and performs its normal vendored-feed and verification workflow. P5 owns shapes, areas, brains, interactions and walk-up. P6 owns combat. This plan edits no Grimhollow files.
