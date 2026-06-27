# Vertical character physics design (gravity + jump), sub-project A

Date: 2026-06-27
Status: approved design, ready for implementation plan
Area: engine (Locomotion + NetWorld) — character-physics part A of two (A: vertical; B: walkable surfaces)

## Context

Movement today is purely horizontal: `CharacterMovement.Step` ground-clamps `Y` to
`groundHeight(x,z) + halfHeight` every tick — there is no air, no jump, no falling. The static-world
collision piece (XZ side-blocking) is shipping separately. This adds the **vertical axis**: gravity,
jump, grounded/falling, **server-authoritative with client prediction from the start**, over the
**terrain only**. Standing on rocks/logs/buildings is **sub-project B** (it builds on this). Building
interiors / ledges are deferred (no interiors yet).

This piece is the riskiest because it extends the predicted/reconciled state to the vertical axis. It
ships value on its own (you can jump and fall off the rim/cliffs), so it goes first.

**Sequencing:** this edits `CharacterMovement.Step` and the movement layer, the same files as static
collision and sub-project B, so it runs **after static collision lands**, not concurrently.

## Components

- **`PlayerMoveState`** (NetWorld) gains `float VerticalVelocity` + `bool Grounded` (Position stays
  `Vector3`). These become part of the predicted/replicated state.
- **`MoveCommand`** (Locomotion) gains a `bool Jump` bit.
- **`MoveTuning`** gains `Gravity` (m/s²), `JumpSpeed` (launch velocity, m/s), `MaxFallSpeed`
  (terminal), and feel defaults `CoyoteTime` + `JumpBuffer` (small; default-tuned).
- **`CharacterMovement.Step`** — `Y` is no longer a pure function of XZ:
  1. XZ move as today (camera-relative, slope-gated; static-collision resolution if present). Air
     control: XZ still applies while airborne (optionally scaled by an `AirControl` factor, default 1).
  2. Vertical integrate: `VerticalVelocity -= Gravity*dt` (clamp to `-MaxFallSpeed`); `y += VerticalVelocity*dt`.
  3. Ground contact: `groundY = groundHeight(x,z) + halfHeight`. If `y <= groundY`: land
     (`y = groundY`, `VerticalVelocity = 0`, `Grounded = true`); else `Grounded = false`.
  4. Jump: if `cmd.Jump` and (`Grounded` or within `CoyoteTime`): `VerticalVelocity = JumpSpeed`,
     `Grounded = false`. (`JumpBuffer` lets a jump pressed just before landing fire on contact.)
- **Server sim** (`PlayerMoveSimulator` → `WorldServer`/`ShardedWorldServer`) runs the identical
  `Step` authoritatively and replicates the vertical state.
- **Client prediction / reconciliation** (`ClientPrediction` / `WorldClient`): the vertical fields are
  reconciled alongside XZ — a server snapshot corrects `y` / `VerticalVelocity` / `Grounded`, and the
  predicted re-simulation replays them. This is the hard part; the same `Step` runs in prediction so
  client and server stay identical.

## Demo

`TerrainWalkSample` (bounded preset): Space to jump, jump around, run off the rim/cliffs and fall, land
back on the terrain.

## Testing (headless)

- Gravity: a player off the ground falls, accelerating, clamped to `MaxFallSpeed`.
- Jump fires only when grounded (or within `CoyoteTime`); a buffered jump fires on landing.
- Landing clamps `y` to the ground and zeroes vertical velocity; `Grounded` flips correctly.
- **Authoritative parity**: the server `Step` and the client prediction produce identical vertical
  state for the same command stream (determinism).
- **Reconciliation**: an injected vertical misprediction is corrected and the replay converges (no
  permanent desync, no snap once converged).
- Airborne XZ control behaves per `AirControl`.

## Scope

### In scope

- `PlayerMoveState` vertical fields; `MoveCommand.Jump`; `MoveTuning` gravity/jump/fall/feel.
- `CharacterMovement.Step` vertical integration + jump + land + air control.
- Authoritative server + client prediction/reconciliation of the vertical axis.
- `TerrainWalkSample` jump demo.
- Headless tests; additive **minor** bump; docs (USING jump/gravity usage + tuning).

### Out of scope (named)

- **Standing on / jumping onto props & buildings** — sub-project B (`WorldSurfaces` + step-up).
- **Building interiors / ledges**, **step-height over low terrain ledges** — deferred (no interiors).
- **Double-jump, wall-jump, climbing, swimming, fall damage** (fall damage is a game concern).
- **Full physics engine** — still a kinematic character controller.

## Engine-first

`CharacterMovement`/`MoveCommand`/`MoveTuning` (Locomotion) + `PlayerMoveState`/prediction (NetWorld).
Every game gets jumping. Runs after static collision (shared movement files); B follows it.

## Open items to confirm during implementation

- Exactly how the vertical fields slot into the existing `IPredictedState`/`ClientPrediction` reconcile
  (extend the predicted state struct + the reconcile compare/replay to the vertical axis).
- Default tuning: `Gravity` (~ -20 to -30 m/s² feels gamey), `JumpSpeed`, `MaxFallSpeed`, `CoyoteTime`
  (~0.1 s), `JumpBuffer` (~0.1 s), `AirControl` (start 1.0).
- Grounded epsilon / skin so the player doesn't jitter between grounded/airborne on slopes.
