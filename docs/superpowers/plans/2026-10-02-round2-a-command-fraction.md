# Round 2 A: Precise Ordinary Movement Commands Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve a final approach's speed fraction through ordinary player commands, NPC stepping, the move codec and reconciliation without changing legacy input behavior.

**Architecture:** Keep the existing constructors and step entry point. Add explicit precise-input overloads, with the player choice carried in a free flags bit of the existing 18-byte frame. Advance the engine wire gate once, while keeping persisted built-in payload layouts unchanged.

**Tech Stack:** C#/.NET 10, xUnit, Locomotion, NetWorld, loopback transport. Uses the integration owner's selected 20.18.0 version.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-2-DESIGN-2026-10-02.md`, sections 3 and 9, D7, D8 and D18. Approved 2026-10-02. Branch 1 of 4.

## Global Constraints

- EXECUTION STATUS. Tasks 1 to 3 and Task 4's documentation/version work are implemented on the task branch. The full branch build and test run remains pending with the controller.
- Execution worktree `/Users/antonio/KhaozEngine/.worktrees/round2-command-fraction`, branch `feature/round2-command-fraction`. Read AGENTS.md, contributor rules and the approved design. Fetch and reconcile engine main before creating it. Preserve unrelated work.
- Version authority is the integration owner. At authoring, main and v20.17.0 are `e585b8a01`. A opens 20.18.0 if free, or rides an appropriate staged 20.x version selected by that owner. Select once, update all four plans if the selection changes. Workers do not bump independently. B, C and D ride it.
- Keep the old MoveCommand constructor and StepTowards signature, legacy resolver arithmetic, command length and unflagged bytes. No new movement component, timestep field or persistence payload.
- Wire generation is 12 at authoring. Expect one advance to 13, after checking concurrent changes. This gate is separate from the game's protocol string.
- One building worker at a time. Focused red/green tests per task. One full Release suite at branch finish. No local repetitions or load tests. Workers do not push, pack, integrate or tag.
- New files own new concerns. No file-size baseline growth. Zero warnings. Test namespaces remain under KhaozEngine.Tests. No em or en dashes or prose semicolons.
- Each task finishes with review before the next. Record departures and their reason and cost in an Outcome section before integration. Do not label unexecuted tests as passed.

## Review Focus

1. A tiny final fraction below the old dead zone must still move. Task 1 `TinyPreciseAxesStillMove` and `TinyPreciseNpcDirectionStillMoves`.
2. An enormous finite axis must not overflow normalization into NaN or grant extra speed. Task 1 `ExtremeFiniteAxesStayFiniteAndAtMostFullSpeed`.
3. An old peer silently ignores the flags bit. Task 2 `PreviousWireGenerationCannotJoinEitherHost`.
4. A 19-byte move would alias the game's message envelope. Task 2 `PreciseMoveKeepsTheEighteenByteDemuxContract`.
5. Equivalent snapshot layouts at generations 12 and 13 must not become ambiguous migration candidates. Task 2 `PreviousGenerationBodiesKeepTheirBytes` and `UnstampedEquivalentGenerationsNormalizeWithoutAmbiguity`.

---

### Task 1: Precise intent in the shared movement core

**Files:**
- Modify `KhaozEngine.Locomotion/MoveCommand.cs`, existing constructor and properties.
- Modify `KhaozEngine.Locomotion/CharacterMovement.Horizontal.cs`, ResolveCameraRelative and ResolveWorldDir.
- Modify `KhaozEngine.Locomotion/CharacterMovement.cs`, existing StepTowards becomes a forwarder with unchanged signature.
- Create `KhaozEngine.Locomotion/CharacterMovement.PreciseDirection.cs`, the overload and robust precise normalization.
- Create `KhaozEngine.Game.Tests/Locomotion/PreciseMovementInputTests.cs`.

**Interfaces:**
- Retain `MoveCommand(Vector2 move, bool run, float cameraYaw, bool jump = false, bool faceCamera = false)`.
- Add `MoveCommand(Vector2 move, bool run, float cameraYaw, bool jump, bool faceCamera, bool scaleSpeedByAxis)` and `bool ScaleSpeedByAxis { get; }`. The retained constructor delegates with false.
- Add `MoveState CharacterMovement.StepTowards(in MoveState state, Vector2 worldDir, bool run, float dt, Func<float,float,float> groundHeight, in MoveTuning tuning, bool preserveSmallMagnitude, Func<float,float,Vector3>? groundNormal = null, IPhysicsWorld? world = null, Func<float,float,Vector2>? clampXz = null, Func<float,float,float,MovementMedium>? medium = null)`.
- Both precise forms feed the existing StepCore. Only precise forms use nonzero magnitude below the old dead zone. Clamp fraction to 1. Default idle remains idle.

- [x] **Step 1: Write the new tests and pin the unchanged baseline.** Use flat ground, grounded state at Y=0.75, radius 0.3, half-height 0.75, walk 2, run 5 and dt=1/30. Capture one legacy movement result before editing production code. Add the nine named tests, including the three in Review Focus 1 and 2, `LegacyFractionalAxesStillMoveAtFullSpeed`, `DefaultCommandIsStillIdle`, `PreciseNpcAndPlayerShareTheGroundCore`, `DirectionalScaleIsAppliedOnce`, `FalseOptInMatchesTheRetainedConstructor` and `NonFinitePreciseInputRemainsIdle`.

  Representative assertions for axis `(0,0.125)` at yaw 0, run true:
  ```csharp
  Assert.Equal(-5f / 30f, legacy.Position.Z, 6);
  Assert.Equal(-5f / 30f * 0.125f, precise.Position.Z, 6);
  Assert.True(tiny.Position.Z < 0f);
  Assert.True(float.IsFinite(extreme.Position.Z));
  Assert.InRange(MathF.Abs(extreme.Position.Z), 0f, 5f / 30f + 0.000001f);
  Assert.Equal(precise.Position, npc.Position);
  ```
  Compare old constructor with the new false overload over diagonal, idle, FaceCamera and directional-scale cases. For precise NPC parity use a canonical axis and yaw 0, avoiding a test oracle with a second camera convention. A true command whose axis is zero remains idle. NaN and infinity never poison output.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~PreciseMovementInputTests'`. Expected new API compile failure or failing new behavior, then preserve the baseline values. Do this only after build-slot authorization.
- [x] **Step 3: Implement the interfaces.** Scale by the largest absolute component before normalization to avoid squared-length overflow or underflow. Retain the legacy branches unchanged. Precise direction magnitude controls speed, not camera-facing direction. Keep the original StepTowards symbol as a forwarding overload.
- [x] **Step 4: Run** the same filter plus `FullyQualifiedName~CharacterMovementStepTowardsTests`. Expected nonzero counts and zero failures. The existing bit-for-bit parity test remains unchanged.
- [x] **Step 5: Commit explicit paths.** Subject `feat(locomotion): preserve explicit movement speed fractions`. Obtain the task review.

### Task 2: Codec, connection gate and unchanged persisted bodies

**Files:**
- Modify `KhaozEngine.NetWorld/MoveProtocol.cs`, generation and EncodeMove/TryDecodeMove around lines 284 to 340.
- Inspect `KhaozEngine.NetWorld/BuiltinBlobLayout.cs`, `CellBlobRewriter.cs`, `WireGenerationBlobMigration.cs`, `ProtocolHandshake.cs` and `WireGenerationAuthenticator.cs`. Change only if the new tests prove a required compatibility adjustment.
- Create `KhaozEngine.Server.Tests/NetWorld/MoveProtocolSpeedFractionTests.cs`.
- Add tests to `KhaozEngine.Server.Tests/NetWorld/WireGenerationBlobMigrationTests.cs`, using its private BodyAt helper and existing CellBlobFixtures.
- Create `KhaozEngine.Server.Tests/NetWorld/PreciseMovementWireHandshakeTests.cs`.

**Interfaces:**
- Encode ScaleSpeedByAxis as `0x04` in flags byte 12. Preserve run `0x01`, FaceCamera `0x02`, jump byte 17 and length 18.
- Decode with Task 1's six-argument constructor. Retain existing hostile-safe finite checks and ignored unknown bits.
- Advance `MoveProtocol.WireProtocolVersion` once, nominally 13. Do not change historical `MovementOwnerWireGeneration = 12`, built-in ids or payload sizes.

- [x] **Step 1: Write failing tests.** On unchanged Task 1 code, pin literal bytes for an unflagged seq 42, forward axis, yaw 0 command. Add precise true/false round trips, all other flags, nonfinite axes/yaw rejection, arbitrary remaining bits and length demux proofs. Test old/new wire Hello admission on both WorldServer and ShardedWorldServer using the existing handshake fixtures. Test generation-12 schema-v4 and unstamped schema-v3 bodies, including owner state, commitment and extension bytes.
  ```csharp
  Assert.Equal(18, wire.Length);
  Assert.Equal(0x04, wire[12] & 0x04);
  Assert.True(MoveProtocol.TryDecodeMove(wire, out _, out MoveCommand decoded));
  Assert.True(decoded.ScaleSpeedByAxis);
  Assert.Equal(command.Move, decoded.Move);
  Assert.Equal(DisconnectReason.IncompatibleVersion, oldPeerReason);
  Assert.Equal(bodyAtTwelve, normalizedBody);
  ```
  Normalizing a persisted header advances its generation stamp to current. Its body bytes must not change. Equivalent inferred generations are equivalent results, not an ambiguity. Retain existing MoveProtocolTests and GameMessageProtocolTests.
- [x] **Step 2: Run** `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~MoveProtocolSpeedFractionTests|FullyQualifiedName~PreciseMovementWireHandshakeTests|FullyQualifiedName~WireGenerationBlobMigrationTests'`. Expected new behavior fails.
- [x] **Step 3: Implement codec and generation changes.** Audit MoveCommand constructions and copies with rg. Prediction queues already carry the whole value, so do not add a parallel fraction channel. Extend migration logic only if a proof fails. No game protocol edit.
- [x] **Step 4: Run** the same filter plus `FullyQualifiedName~MoveProtocolTests|FullyQualifiedName~GameMessageProtocolTests|FullyQualifiedName~VersionHandshakeTests`. Expected nonzero counts, no failures and unchanged unflagged fixture bytes.
- [x] **Step 5: Commit explicit paths.** Subject `feat(networld): carry precise movement intent through the wire gate`. Obtain the task review.

### Task 3: Prediction and authority consume the same fraction

**Files:**
- Create `KhaozEngine.Server.Tests/NetWorld/PreciseMovementReconcileTests.cs`.
- Reuse the wiring patterns in existing ClientReconcileTests and WorldClientLocalMovementTests, without widening their project references.

**Interfaces:**
- Consume the real PlayerMoveSimulator, ClientPrediction, InMemoryTransportHub, WorldClient.SendInput and normal WorldServer queue. No fake direct server position writes.

- [x] **Step 1: Write the proof.** Send mixed ordinary, precise and idle commands at 30 Hz. Introduce one authoritative correction with unacknowledged precise commands and assert their replay position. Exercise speed scale, wading and FaceCamera directional scaling. Use bounded simulated frames with a phase offset between client submission and serving. Add `UnackedPreciseCommandsReplayTheirFraction`, `WireDecodedPreciseInputMatchesPrediction` and `IdleAfterPreciseInputDoesNotKeepMoving`.
  ```csharp
  Assert.Equal(authoritativeAtAcknowledgedTick.Position, predictedAtAcknowledgedTick.Position);
  Assert.Equal(expectedPreciseTravel, replayed.Position.Z, 6);
  Assert.Equal(stoppedPosition, afterIdle.Position);
  ```
- [x] **Step 2: Run** `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~PreciseMovementReconcileTests'`. A correctly implemented Task 2 can already pass. This is a wiring proof, not a requirement to manufacture a failure.
- [x] **Step 3: Correct any lost flag only at its actual copy site.** Preserve all other command fields. Do not introduce a separate simulation path.
- [x] **Step 4: Run** the same filter plus `FullyQualifiedName~ClientReconcileTests|FullyQualifiedName~WorldClientLocalMovementTests`. Expected nonzero, zero failures.
- [x] **Step 5: Commit.** Subject `test(networld): prove precise commands survive prediction and reconciliation`. Obtain the task review.

### Task 4: Documentation, round version and branch verification

**Files:**
- Modify `KhaozEngine.Locomotion/README.md`, `KhaozEngine.NetWorld/README.md`, `docs/USING-KHAOZENGINE.md` and `CHANGELOG.md`.
- Integration owner only, if opening the version, modify Directory.Build.props and every current declaration named by scripts/check-doc-versions.sh in the same version/changelog commit.
- Update this plan with Outcome and the actual version and wire generation selected.

- [x] **Step 1: Write and sweep the contract docs.** Explain both opt-ins, the preserved default, flags bit, exact length and automatic wire gate. Search every Markdown file for MoveCommand, StepTowards, wire generation and move frame. Preserve historical release facts. Version subject if opened `release(20.18.0): open the movement kernel round`, adjusted only by the integration owner if another version was selected.
- [ ] **Step 2: Fetch and merge current main into this branch, then run once from its root.** This is deferred until the execution build slot is authorized:
  ```bash
  mkdir -p local-feed
  dotnet build KhaozEngine.slnx -c Release
  dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
  sh scripts/check-dashes.sh --tree
  sh scripts/check-prose.sh --tree
  sh scripts/check-file-size.sh --tree
  bash scripts/check-doc-versions.sh
  ```
  Expected zero warnings, zero failures and every guard passing. Inspect exit codes and nonzero test counts. Report a flake with its issue, never loop. Obtain whole-branch review and record Outcome before landing.
- [ ] **Step 3: Commit the documentation and Outcome.** Subject `release(20.18.0): open the movement kernel round`. The integration owner merges and pushes main. Pack only after slot authorization and the pushed commit is on main. No tag. B, C and D consume this Outcome and ride the selected version.

## Outcome

Tasks 1 to 3 are implemented and reviewed on `feature/round2-command-fraction`. The integration owner selected
20.18.0 after reconciling current `main` and tags. This branch advances `MoveProtocol.WireProtocolVersion` from 12 to
13 while retaining the exact 18-byte move frame and the generation-12 persisted payload layout. Task 4 documents the
two opt-ins, the preserved defaults, the flags mapping, the automatic generation gate and the unchanged persisted
payloads. The round design and index now record Plan A as implemented on this task branch, with Plans B, C and D not
started and no release or consumer adoption.

Focused evidence is Task 1: 28 passed, Task 2: 99 passed, and Task 3: 6 focused plus 10 adjacent passed. The full
branch Release build and test suite remain pending and controller-owned. No full-suite pass is claimed here.

Two execution rulings are carried forward. Task 1 followed the named test cases rather than the erroneous prose count
of five in the two review-focus rows. The binding behavior and coverage remained unchanged, and the cost of choosing
the names incorrectly would have been a missed case caught by the task and final review. Task 1 also qualified only
the two existing XML `StepTowards` cref references to the retained signature because the additive overload caused
CS0419 under warnings-as-errors. Existing test bodies stayed unchanged. The cost of this departure was two
comment-only files, with no behavior change.
