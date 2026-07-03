# Vertical Character Physics (gravity + jump) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add server-authoritative, client-predicted vertical character physics (gravity, jump, grounded/falling) over terrain, in KhaozEngine.Locomotion + KhaozEngine.NetWorld.

**Architecture:** The pure `CharacterMovement.Step` gains a new overload taking/returning a `MoveState` (position + vertical velocity + grounded + coyote/buffer timers); it integrates gravity, lands/clamps to ground, and jumps (coyote + jump-buffer). The vertical state rides as a **replicated** `MovementState` ECS component, so it survives sharded cell handoff and reaches the client; `ClientPrediction.Reconcile` already rebases to the full authoritative basis and replays, so the vertical axis reconciles by carrying it in `PlayerMoveState`. The existing `Step(Vector3,...)→Vector3` overload is untouched, keeping this an additive **minor** bump.

**Tech Stack:** C# net10.0, xUnit headless tests, KhaozEngine ECS/Replication/Netcode.

## Global Constraints

- Engine version line: bump `<KhaozEngineVersion>` to **7.54.0** (highest existing tag is v7.53.1). One bump for the whole batch.
- No em-dashes anywhere (code, docs, commits).
- Every new behaviour ships with a headless test in `KhaozEngine.Tests` (construct state frame-by-frame; `dt` is a plain `float`).
- `AppWindow` is the only class touching input statics; controllers read the `InputState` snapshot only.
- Additive/minor: keep the existing `CharacterMovement.Step(Vector3,...)→Vector3` overload working; add capability via overloads/optional params.
- `PlayerMovementSystem` MUST stay stateless (no mutable instance fields): it runs concurrently per cell across the job scheduler.
- Doc sweep on every change: `CHANGELOG.md`, `CHANGENOTES.md`, `CLAUDE.md` package map note, `docs/USING-KHAOZENGINE.md` (jump/gravity usage + tuning), and the 3 guard-checked version declarations (`docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`). Run `scripts/check-doc-versions.sh`.
- In scope: vertical only, terrain only. OUT of scope (do not build): standing on/jumping onto props/buildings, building interiors/ledges, step-height over terrain ledges, double/wall-jump, climbing, swimming, fall damage, a physics engine.

---

## Default tuning (MoveTuning)

| Field | Default | Note |
|-------|---------|------|
| `Gravity` | 25 m/s² | downward accel magnitude (gamey) |
| `JumpSpeed` | 8 m/s | launch velocity (apex ≈ 1.28 m, airtime ≈ 0.64 s) |
| `MaxFallSpeed` | 50 m/s | terminal clamp |
| `CoyoteTime` | 0.1 s | jump grace after leaving ground |
| `JumpBuffer` | 0.1 s | jump pressed before landing fires on contact |
| `AirControl` | 1.0 | airborne XZ scale (1 = full) |
| `GroundedEpsilon` | 0.3 m | slope-stick skin so downhill doesn't jitter grounded/airborne |

## Step algorithm (the MoveState overload), jump-last ordering

1. Horizontal: camera-relative move (as today), scaled by `state.Grounded ? 1 : AirControl`; slope gate; static-collider resolve; optional `clampXz` (bounds).
2. Jump-buffer timer: `tSinceJump = cmd.Jump ? 0 : state.TimeSinceJumpRequested + dt`.
3. Gravity: `vVel = max(state.VerticalVelocity - Gravity*dt, -MaxFallSpeed)`; `y = state.Position.Y + vVel*dt`.
4. Ground contact (epsilon slope-stick): `groundY = groundHeight(x,z) + CapsuleHalfHeight`. If `vVel <= 0 && (y <= groundY || (state.Grounded && y <= groundY + GroundedEpsilon))` → land: `y = groundY; vVel = 0; grounded = true; tSinceGround = 0`. Else `grounded = false; tSinceGround = state.TimeSinceGrounded + dt`.
5. Jump (after contact, so a buffered jump fires on the landing tick): if `(grounded || tSinceGround <= CoyoteTime) && tSinceJump <= JumpBuffer` → `vVel = JumpSpeed; grounded = false; tSinceGround = CoyoteTime + dt` (consume coyote, no double-jump); `tSinceJump = JumpBuffer + dt` (consume buffer).
6. Return `{ Position=(x,y,z), VerticalVelocity=vVel, Grounded=grounded, TimeSinceGrounded=tSinceGround, TimeSinceJumpRequested=tSinceJump }`.

---

## File Structure

- Create `KhaozEngine.Locomotion/MoveState.cs` — kinematic vertical-aware state.
- Modify `KhaozEngine.Locomotion/MoveCommand.cs` — add `bool Jump`.
- Modify `KhaozEngine.Locomotion/MoveTuning.cs` — add vertical/feel fields.
- Modify `KhaozEngine.Locomotion/CharacterMovement.cs` — add `Step(in MoveState,...)` + shared `ResolveHorizontal`.
- Modify `KhaozEngine.NetWorld/PlayerMoveState.cs` — embed `MoveState`, forward `Position`/`VerticalVelocity`/`Grounded`.
- Create `KhaozEngine.NetWorld/MovementState.cs` — replicated ECS component (vVel/grounded/timers).
- Modify `KhaozEngine.NetWorld/MoveProtocol.cs` — register `MovementState` (typeId 2).
- Modify `KhaozEngine.NetWorld/PlayerMoveSimulator.cs` — step via MoveState; bounds via `clampXz`.
- Modify `KhaozEngine.NetWorld/PlayerMovementSystem.cs` — 4-arg ForEach incl. `MovementState`; bounds via `clampXz`.
- Modify `KhaozEngine.NetWorld/WorldServer.cs` — write/replicate `MovementState` each tick + at spawn.
- Modify `KhaozEngine.NetWorld/ShardedWorldServer.cs` — add `MovementState` at spawn; read vertical in `TryGetPlayerState`.
- Modify `KhaozEngine.NetWorld/WorldClient.cs` — build reconcile basis from `ReplicatedPosition` + `MovementState`.
- Modify `KhaozEngine.Game.Render3D/CharacterController3D.cs` — carry MoveState, jump on Space, expose Grounded/VerticalVelocity + tuning.
- Modify `TerrainWalkSample/Program.cs` — Space-to-jump messaging.
- Tests: `KhaozEngine.Tests/Locomotion/CharacterMovementVerticalTests.cs` (new), `KhaozEngine.Tests/NetWorld/VerticalPhysicsTests.cs` (new), `KhaozEngine.Tests/NetWorld/VerticalReconcileTests.cs` (new); update `KhaozEngine.Tests/NetWorld/PlayerMovementSystemTests.cs` helper.
- Docs as per Global Constraints.

---

### Task 1: Locomotion vertical core (MoveState + MoveCommand.Jump + MoveTuning + Step overload)

**Files:**
- Create: `KhaozEngine.Locomotion/MoveState.cs`
- Modify: `KhaozEngine.Locomotion/MoveCommand.cs`
- Modify: `KhaozEngine.Locomotion/MoveTuning.cs`
- Modify: `KhaozEngine.Locomotion/CharacterMovement.cs`
- Test: `KhaozEngine.Tests/Locomotion/CharacterMovementVerticalTests.cs`

**Interfaces:**
- Produces: `struct MoveState { Vector3 Position; float VerticalVelocity; bool Grounded; float TimeSinceGrounded; float TimeSinceJumpRequested; }`
- Produces: `MoveCommand(Vector2 move, bool run, float cameraYaw, bool jump = false)` + `bool Jump { get; }`
- Produces: `MoveTuning` gains `float Gravity, JumpSpeed, MaxFallSpeed, CoyoteTime, JumpBuffer, AirControl, GroundedEpsilon` (defaults above).
- Produces: `CharacterMovement.Step(in MoveState state, in MoveCommand cmd, float dt, Func<float,float,float> groundHeight, in MoveTuning tuning, Func<float,float,Vector3>? groundNormal = null, WorldColliders? colliders = null, Func<float,float,Vector2>? clampXz = null) → MoveState`

- [ ] **Step 1: Write failing tests** in `CharacterMovementVerticalTests.cs` covering: gravity accelerates a fall and clamps to `-MaxFallSpeed`; landing clamps `y` to groundY and zeroes vVel and sets Grounded; jump only when grounded; jump within coyote after walking off; no double-jump at apex; buffered jump fires on landing; airborne XZ scales by AirControl; determinism (same inputs → same output); old `Step(Vector3,...)` overload still ground-clamps.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter CharacterMovementVerticalTests` → FAIL (no MoveState overload).
- [ ] **Step 3: Implement** `MoveState`, `MoveCommand.Jump`, `MoveTuning` fields, and the `Step(in MoveState,...)` overload + `ResolveHorizontal` helper (per algorithm above). Keep `Step(Vector3,...)` delegating its XZ to the shared helper.
- [ ] **Step 4: Run** the full Locomotion test set (`--filter CharacterMovement`) → PASS (new + existing `CharacterMovementTests`/`CharacterMovementCollisionTests`).
- [ ] **Step 5: Commit** `locomotion: vertical character step (gravity, jump, coyote, buffer, air control)`.

### Task 2: PlayerMoveState vertical fields + PlayerMoveSimulator

**Files:**
- Modify: `KhaozEngine.NetWorld/PlayerMoveState.cs`
- Modify: `KhaozEngine.NetWorld/PlayerMoveSimulator.cs`
- Test: `KhaozEngine.Tests/NetWorld/VerticalPhysicsTests.cs` (start it here)

**Interfaces:**
- Produces: `PlayerMoveState { MoveState Move; Vector3 Position {get;set;}; float VerticalVelocity {get;set;}; bool Grounded {get;set;}; }`, `WithPosition(Vector2)` preserves vertical fields.
- Consumes: Task 1 `MoveState`, `CharacterMovement.Step(in MoveState,...)`.
- Produces: `PlayerMoveSimulator.Step` runs the MoveState overload and applies `WorldBounds` as `clampXz` inside the step (no post-step Y re-derive).

- [ ] **Step 1: Write failing tests**: simulator gravity (a player above ground falls); simulator jump command launches then falls back and lands; simulator bounds clamp keeps an airborne player airborne (Y not snapped to ground at the wall); existing `PlayerMoveSimulatorTests` still green.
- [ ] **Step 2: Run** `--filter VerticalPhysicsTests` → FAIL.
- [ ] **Step 3: Implement** `PlayerMoveState` (embed MoveState + forwarding props + WithPosition) and `PlayerMoveSimulator` (capture `clampXz = bounds is null ? null : bounds.Clamp`; call `CharacterMovement.Step(state.Move, ...)`).
- [ ] **Step 4: Run** `--filter "VerticalPhysics|PlayerMoveSimulator|MovementBounds|ClientReconcile"` → PASS.
- [ ] **Step 5: Commit** `networld: PlayerMoveState vertical fields + simulator gravity/jump/bounds`.

### Task 3: Replicated MovementState component + MoveProtocol registration

**Files:**
- Create: `KhaozEngine.NetWorld/MovementState.cs`
- Modify: `KhaozEngine.NetWorld/MoveProtocol.cs`
- Test: `KhaozEngine.Tests/NetWorld/VerticalPhysicsTests.cs`

**Interfaces:**
- Produces: `struct MovementState : IComponent { float VerticalVelocity; bool Grounded; float TimeSinceGrounded; float TimeSinceJumpRequested; }`
- Produces: `MoveProtocol.MovementTypeId = 2`; registry registers `MovementState` (write/read all 4 fields; no lerp).

- [ ] **Step 1: Write a failing test**: build `MoveProtocol.CreateRegistry()`, spawn an entity with a `MovementState`, `SnapshotWriter.WriteFiltered` → `ClientReplicationView.Apply` into a fresh world, assert the 4 fields round-trip exactly.
- [ ] **Step 2: Run** → FAIL (MovementState undefined).
- [ ] **Step 3: Implement** `MovementState` + register in `MoveProtocol.CreateRegistry()` (write `VerticalVelocity` float, `Grounded` byte, two timer floats; read symmetric; lerp `null`).
- [ ] **Step 4: Run** the round-trip test + `--filter MoveProtocol` → PASS.
- [ ] **Step 5: Commit** `networld: replicated MovementState component (vertical state on the wire)`.

### Task 4: PlayerMovementSystem vertical (sharded per-cell sim)

**Files:**
- Modify: `KhaozEngine.NetWorld/PlayerMovementSystem.cs`
- Modify: `KhaozEngine.Tests/NetWorld/PlayerMovementSystemTests.cs` (SpawnPlayer adds MovementState)

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: `PlayerMovementSystem.Update` runs the MoveState step over `<NetId, ReplicatedPosition, PendingMove, MovementState>`, bounds via `clampXz`, writes both `ReplicatedPosition` and `MovementState`.

- [ ] **Step 1: Write a failing test**: spawn an entity with ReplicatedPosition + PendingMove + MovementState above ground with a jump command; `Update` once; assert vVel became positive (launched). Add a falling test (no ground contact → vVel negative). Keep `Step_AdvancesOwnedPlayer_AlongCommand` and ghost/migrating skip tests green (update `SpawnPlayer` to add `MovementState`).
- [ ] **Step 2: Run** `--filter PlayerMovementSystem` → FAIL.
- [ ] **Step 3: Implement** the 4-arg ForEach (build MoveState from pos+ms, step, write back both; ghost/migrating skip unchanged; `clampXz = bounds?.Clamp`).
- [ ] **Step 4: Run** `--filter PlayerMovementSystem` → PASS.
- [ ] **Step 5: Commit** `networld: PlayerMovementSystem steps vertical state per cell`.

### Task 5: WorldServer vertical replication

**Files:**
- Modify: `KhaozEngine.NetWorld/WorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/VerticalPhysicsTests.cs`

**Interfaces:**
- Produces: `WorldServer` writes `MovementState` from `stateBySlot[slot]` each tick and at spawn; `SetPlayerState` writes both.

- [ ] **Step 1: Write a failing test** (loopback): a client sends a jump command; after the server tick, `TryGetPlayerState` shows VerticalVelocity > 0 (launched), then over more idle ticks lands back (Grounded true, vVel 0).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement**: in `Tick`, after `simulator.Step`, `world.Set(entity, MovementStateFrom(state))` alongside `ReplicatedPosition`; in `OnJoin` set `MovementState` from the settled state; in `SetPlayerState` set it too.
- [ ] **Step 4: Run** `--filter "VerticalPhysics|WorldServer|WorldRoundTrip"` → PASS.
- [ ] **Step 5: Commit** `networld: WorldServer replicates vertical movement state`.

### Task 6: ShardedWorldServer vertical (spawn + handoff survival)

**Files:**
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/VerticalPhysicsTests.cs`

**Interfaces:**
- Produces: `ShardedWorldServer` adds `MovementState` at spawn; `TryGetPlayerState` reads ReplicatedPosition + MovementState. Handoff carries it automatically (registered).

- [ ] **Step 1: Write a failing test** (loopback, sharded): jump command launches the player (vVel > 0 via `TryGetPlayerState`); plus a parity assertion that a player can jump while crossing a cell boundary and the vertical state is not reset (vVel preserved across handoff). Keep `ShardedWorldServerTests` green.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement**: `OnJoin` `cell.World.Set(e, new MovementState{...})` from settled state; `TryGetPlayerState` builds `PlayerMoveState` from both components; `SetPlayerState` resets MovementState to grounded.
- [ ] **Step 4: Run** `--filter "VerticalPhysics|ShardedWorldServer|ShardedWorldPersistence"` → PASS.
- [ ] **Step 5: Commit** `networld: ShardedWorldServer vertical state (spawn + survives handoff)`.

### Task 7: WorldClient reconcile basis + networked vertical reconcile tests

**Files:**
- Modify: `KhaozEngine.NetWorld/WorldClient.cs`
- Test: `KhaozEngine.Tests/NetWorld/VerticalReconcileTests.cs` (new)

**Interfaces:**
- Produces: `WorldClient.OnSnapshot` builds `basis.Move` from the local entity's `ReplicatedPosition` (XYZ) + `MovementState` (vVel/grounded/timers), then `Reset`/`Reconcile`.

- [ ] **Step 1: Write failing tests**: (a) server/client parity — drive the same command stream (incl. jumps) through a `PlayerMoveSimulator` server state and a `ClientPrediction`, assert identical vertical state every tick; (b) injected vertical misprediction (basis vVel/y differ) converges with no permanent desync and no snap once converged; (c) end-to-end loopback: client jumps, predicts up immediately, server agrees, reconciliation error stays tiny.
- [ ] **Step 2: Run** `--filter VerticalReconcile` → FAIL.
- [ ] **Step 3: Implement** the `WorldClient.OnSnapshot` basis build (read both components).
- [ ] **Step 4: Run** `--filter "VerticalReconcile|ClientReconcile|WorldRoundTrip"` → PASS.
- [ ] **Step 5: Commit** `networld: WorldClient reconciles the vertical axis`.

### Task 8: CharacterController3D jump + TerrainWalkSample demo

**Files:**
- Modify: `KhaozEngine.Game.Render3D/CharacterController3D.cs`
- Modify: `TerrainWalkSample/Program.cs`
- Test: `KhaozEngine.Tests/Render3D/CharacterController3DTests.cs`

**Interfaces:**
- Produces: `CharacterController3D` carries a `MoveState`; `Update` reads `input.WasPressed(Key.Space)` as the jump bit; exposes `bool Grounded`, `float VerticalVelocity`, plus `Gravity/JumpSpeed/MaxFallSpeed/CoyoteTime/JumpBuffer/AirControl/GroundedEpsilon` fields.

- [ ] **Step 1: Write failing tests** in `CharacterController3DTests.cs`: pressing Space while grounded makes `VerticalVelocity > 0` and `Grounded == false`; with no jump the controller stays grounded over many updates on flat ground; existing controller tests stay green.
- [ ] **Step 2: Run** `--filter CharacterController3D` → FAIL.
- [ ] **Step 3: Implement** controller MoveState + jump key + exposed state; in `TerrainWalkSample`, append "Space jump" to the console help (jump bit already flows via `Update`).
- [ ] **Step 4: Run** `--filter CharacterController3D` → PASS; `dotnet build TerrainWalkSample/TerrainWalkSample.csproj` → builds.
- [ ] **Step 5: Commit** `game3d+sample: CharacterController3D jump (Space) over terrain`.

### Task 9: Full suite, docs sweep, version bump, pack, release

**Files:**
- Modify: `Directory.Build.props` (→ 7.54.0), `CHANGELOG.md`, `CHANGENOTES.md`, `CLAUDE.md`, `docs/USING-KHAOZENGINE.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

- [ ] **Step 1: Run** the full suite `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` → all green.
- [ ] **Step 2: Bump** `<KhaozEngineVersion>` to `7.54.0`; add CHANGELOG (detailed, newest-first) + CHANGENOTES (one-line) entries; update USING (jump/gravity usage + tuning section); update the 3 guard declarations (CONSUMERS "Engine current version", ROADMAP "Current released version", README `<PackageReference>` example); update the `CLAUDE.md` Locomotion/NetWorld package-map note ("CharacterMovement.Step takes/returns Vector3" → note the MoveState vertical overload + MovementState replicated component).
- [ ] **Step 3: Run** `bash scripts/check-doc-versions.sh` → OK. Grep `MoveState`/`MovementState`/`Jump`/jump across `*.md` to confirm docs mention them and nothing stale remains.
- [ ] **Step 4: Pack** `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`.
- [ ] **Step 5: Commit** `release(7.54.0): vertical character physics (gravity + jump)`.
- [ ] **Step 6: Release (autonomous)**: merge worktree branch → `main`, repack from main root, `git tag v7.54.0`, push `main` + tag, remove worktree + delete branch.

---

## Self-Review

- **Spec coverage:** PlayerMoveState vertical fields (T2) ✓; MoveCommand.Jump (T1) ✓; MoveTuning gravity/jump/fall/feel (T1) ✓; Step vertical+jump+land+air control (T1) ✓; server sim WorldServer/ShardedWorldServer (T5/T6) ✓; client prediction/reconciliation vertical (T7 + reconcile mechanism unchanged) ✓; TerrainWalkSample jump (T8) ✓; headless tests (T1-T8) ✓; minor bump + docs (T9) ✓. Testing list: gravity+terminal (T1), jump grounded/coyote (T1), buffered jump on landing (T1), land clamps+zeroes (T1), server/client parity (T7), injected vertical misprediction converges (T7), airborne XZ per AirControl (T1) ✓.
- **Placeholders:** none — algorithm + tuning fully specified.
- **Type consistency:** `MoveState`, `MovementState`, `PlayerMoveState.Move`, `clampXz`, `MovementTypeId = 2` used consistently across tasks.
