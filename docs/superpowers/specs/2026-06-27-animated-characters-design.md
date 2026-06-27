# Animated characters design (glTF animation-clip playback + locomotion blend)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Area: engine (Render3D + Game.Render3D) — turn capsules into animated characters

## Context

Everything in the world is a static capsule. The engine has the **rendering half** of skinned
characters — `SkinnedGltfMesh`, CPU skinning through a per-frame composed **bone palette** in `Scene3D`
(`_boneMatrices`; CPU-skinned to dodge a Veldrid/Metal bone-buffer bug), and `GltfLoader.LoadSkinned`
(reads bones/weights/inverse-bind). What's missing is the **playback half**: `GltfLoader` reads
materials + the rig but **not animation clips**, and nothing samples a clip into that bone palette.

This adds glTF animation-clip playback + a locomotion blend, so characters idle/walk/run and jump/fall.
It unblocks NPCs/villagers (the town needs people) and creatures/bosses across every game (SpaceGame's
`cunnuth -> octopus`, etc.). It's been deferred since the walkable slice.

### Locked decisions (from brainstorming)

1. **Locomotion: idle/walk/run crossfade by speed + jump/fall**, driven by the movement state already
   computed (horizontal speed, `Grounded`, `VerticalVelocity`).
2. **Client-cosmetic, NOT networked.** Each client picks the clip from the *already replicated* movement
   state — your avatar from your movement, remote players from their replicated position /
   `VerticalVelocity` / `Grounded`. The server stays authoritative on position only; **no netcode
   changes**.
3. **Character = a KayKit CC0 rigged+animated model** (idle/walk/run/jump clips), loaded through the
   **skinned** path (`LoadSkinned` + the new clip reader), not the flatten-prop path.

## Components

### Read animation clips — `Render3D` (beside the rig/skinning)

A `GltfLoader.LoadAnimations` (additive) reading SharpGLTF `LogicalAnimations`: per channel a target
joint + a TRS track; per sampler input times + output values + interpolation (LINEAR / STEP; CUBICSPLINE
if present). Produces an `AnimationClip` = named, per-joint TRS keyframe tracks + duration.

### Sample + compose the bone palette

`AnimationSampler`: sample a clip at time `t` → per-joint local TRS → compose the joint hierarchy → the
**bone palette** (joint world × inverse-bind) that `Scene3D`'s skinned draw consumes. Handles wrap/loop
and the interpolation modes. This is pure math (no GPU) and headless-testable.

### Player + crossfade

`AnimationPlayer`: holds the current clip + time, advances by `dt`, loops; **crossfades** between two
clips over a short blend time (per-joint TRS lerp/slerp by a blend weight) so idle↔walk↔run and
ground↔air transitions don't snap.

### Locomotion state machine

Maps `(horizontalSpeed, Grounded, VerticalVelocity)` → target clip + blend: idle (speed≈0), walk (low),
run (high) crossfaded by speed; **jump** (airborne, `VerticalVelocity > 0`), **fall** (airborne,
`VerticalVelocity < 0`). Small, data-driven thresholds.

### `AnimatedCharacter` — `Game.Render3D`

Wraps a `SkinnedGltfMesh` + its clips + the player + the state machine. Given the movement state
(`speed`, `Grounded`, `VerticalVelocity`) + `dt`, advances the animation and produces the bone palette
for the skinned draw. Used for the **local** player AND **remote** players (each from their own /
replicated movement). Replaces the capsule.

## Demo

`TerrainWalkSample`: the player capsule becomes the KayKit character — idle when still, walk/run by
speed, jump/fall off the vertical state. The networked sample animates remotes from replicated state.

## Testing

- **Clip reading** (headless): a glTF with animations parses into per-joint TRS tracks + duration.
- **Sampler** (headless): sampling between keyframes interpolates correctly (LINEAR), STEP holds, the
  loop wraps; the composed bone palette matches a hand-computed hierarchy for a tiny rig.
- **Crossfade** (headless): a mid-blend pose is the weighted TRS of the two clips.
- **State machine** (headless): speed thresholds pick idle/walk/run; `Grounded`/`VerticalVelocity` pick
  jump/fall.
- **Visual** (GPU golden, optional): the character posed at a fixed clip time renders correctly; if a
  golden is added, **bake it on all three backends** (Metal local + D3D11/Vulkan via
  `cross-platform-gpu.yml`) per the engine golden rule.

## Scope

### In scope

- `GltfLoader.LoadAnimations` + `AnimationClip` (Render3D).
- `AnimationSampler` (clip→bone palette) + `AnimationPlayer` (advance + crossfade) (Render3D).
- Locomotion state machine (idle/walk/run/jump/fall) + `AnimatedCharacter` (Game.Render3D).
- Drive the sample's player + remote players from the movement / replicated state.
- A committed KayKit CC0 character (skinned-ingest, animations preserved).
- Headless tests (+ optional cross-platform golden); additive **minor** bump; docs.

### Out of scope (named)

- **Animation EVENTS** (attack hitframes, footstep sounds) — combat/audio, later.
- **Root motion, IK, additive/facial layers, full blend trees** beyond the locomotion SM.
- **Server-side or networked animation** — it's cosmetic, derived from replicated movement.
- The **prop flatten path** — characters use the skinned path; props are unchanged.

## Engine-first

Clip reading + sampler + player in `Render3D` (with skinning); the locomotion SM + `AnimatedCharacter`
in `Game.Render3D`. Every game gets animated characters; unblocks NPCs/creatures/bosses.

## Sequencing note

This adds `GltfLoader.LoadAnimations` to `GltfLoader.cs` — the queued **GltfLoader rigid node-transform
fix** also edits that file. Let the node-transform fix land first (it's small + ready), then this, to
avoid two chats touching `GltfLoader.cs`.

## Open items to confirm during implementation

- Interpolation modes (LINEAR + STEP required; add CUBICSPLINE if the KayKit clips use it).
- Crossfade duration default (~0.15 s) and the walk/run speed thresholds.
- How remote-player speed is derived (replicated velocity if available, else position delta over dt).
- The skinned/animated kit ingest (preserve rig + animation channels; the prop decompress recipe may
  differ — do NOT flatten).
- Per-character CPU-skinning cost for many characters (LOD/cull is a later concern).
