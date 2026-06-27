# Animated Characters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** glTF animation-clip playback + a locomotion blend so capsules become characters that idle/walk/run and jump/fall.

**Architecture:** Render3D gains a pure (GPU-free) animation layer: `GltfLoader.LoadAnimations` reads SharpGLTF `LogicalAnimations` into `AnimationClip`s (per-joint TRS keyframe tracks keyed by glTF logical node index); `Skeleton` (attached to `SkinnedGltfMesh`) carries the joint hierarchy + rest-local TRS; `AnimationSampler` samples a clip into per-node local poses and composes the hierarchy into the joint-world bone palette that `Scene3D.DrawSkinned` already consumes; `AnimationPlayer` advances + crossfades two clips at the local-TRS level. Game.Render3D adds a `LocomotionStateMachine` (speed/grounded/vVel → idle/walk/run/jump/fall) and `AnimatedCharacter` wrapping it all. Client-cosmetic; no netcode changes.

**Tech Stack:** net10.0, C#, System.Numerics, SharpGLTF.Core (Render3D) / SharpGLTF.Toolkit (tests), xUnit.

## Global Constraints

- One shared engine version line `<KhaozEngineVersion>` in `Directory.Build.props`; this is ONE additive **minor** bump: 7.55.0 → **7.56.0**.
- No em-dashes anywhere. Conventional-commit subjects `area(7.56.0): summary` on the bump commit.
- New behaviour ships with a headless test in `KhaozEngine.Tests`. Pose/palette math needs no GPU.
- DrawSkinned consumes **joint-WORLD** matrices (it multiplies by inverse-bind internally). The sampler output is joint-world, length == `mesh.BoneCount`, in skin-joint order.
- System.Numerics row-vector convention: `world[i] = local[i] * world[parent[i]]`; `JointPose.ToMatrix = CreateScale(S) * CreateFromQuaternion(R) * CreateTranslation(T)` (matches SharpGLTF AffineTransform).
- Stay in scope. Do NOT build: animation events, root motion, IK, additive/facial layers, full blend trees, server/networked animation, prop flatten path.
- Full doc sweep on the bump (CLAUDE.md package map, README catalog if a package changes — none added here, USING animation section, CHANGELOG, the 3 guard declarations). Run `scripts/check-doc-versions.sh`.

---

## File Structure

Render3D (new, under `KhaozEngine.Render3D/Animation/`):
- `JointPose.cs` — TRS struct + ToMatrix + Lerp/Identity.
- `Skeleton.cs` — joint hierarchy (parent indices, rest-local poses, node→bone map, logical-node→skeleton-node lookup).
- `AnimationClip.cs` — `AnimationClip`, `JointTrack`, `Vector3Track`, `QuaternionTrack`, `InterpolationMode`.
- `AnimationSampler.cs` — static pure math: SamplePose / Compose / BlendPoses / SampleToBonePalette / wrap.
- `AnimationPlayer.cs` — stateful advance + crossfade.

Render3D (modify):
- `Models/SkinnedGltfMesh.cs` — add optional `Skeleton? Skeleton` (additive; existing ctors keep null).
- `Models/GltfLoader.cs` — `BuildSkinned` builds the Skeleton; add `LoadAnimations`.

Game.Render3D (new):
- `LocomotionStateMachine.cs` — `LocomotionState` enum, `LocomotionThresholds`, `LocomotionStateMachine.Evaluate`.
- `AnimatedCharacter.cs` — wraps mesh skeleton + clips + player + SM → bone palette.

Sample / assets:
- `TerrainWalkSample/assets/character/` — committed KayKit CC0 rigged+animated glb (+ LICENSE).
- `TerrainWalkSample/TerrainWalkSample.csproj` — copy the character asset to output.
- `TerrainWalkSample/Program.cs` — replace the capsule with the AnimatedCharacter.

Tests (new, under `KhaozEngine.Tests/Render3D/` + `KhaozEngine.Tests/Game/`):
- `Animation/JointPoseTests.cs`, `Animation/SkeletonComposeTests.cs`, `Animation/AnimationSamplerTests.cs`, `Animation/AnimationPlayerTests.cs`, `Animation/GltfLoadAnimationsTests.cs`, `Game/LocomotionStateMachineTests.cs`, `Game/AnimatedCharacterTests.cs`.

---

## Task 1: JointPose (TRS) value type

**Files:**
- Create: `KhaozEngine.Render3D/Animation/JointPose.cs`
- Test: `KhaozEngine.Tests/Render3D/Animation/JointPoseTests.cs`

**Interfaces:**
- Produces: `struct JointPose { Vector3 Translation; Quaternion Rotation; Vector3 Scale; static JointPose Identity; Matrix4x4 ToMatrix(); static JointPose Lerp(in JointPose a, in JointPose b, float t); static JointPose FromMatrix(Matrix4x4 m); }`

- [ ] **Step 1:** Write `JointPoseTests`: `Identity.ToMatrix()` == `Matrix4x4.Identity`; a pose with T=(1,2,3), R=identity, S=(1,1,1) → matrix translation (1,2,3); `Lerp(a,b,0)`==a, `Lerp(a,b,1)`==b, midpoint translation == average; `Lerp` rotation stays unit length.
- [ ] **Step 2:** Run, verify FAIL (type missing).
- [ ] **Step 3:** Implement `JointPose`. `ToMatrix() => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation)`. `Lerp` = `Vector3.Lerp` for T/S, `Quaternion.Slerp` (normalized) for R. `Identity` = T zero, R identity, S one. `FromMatrix` via `Matrix4x4.Decompose` (fallback to identity scale/rot on failure).
- [ ] **Step 4:** Run, verify PASS.
- [ ] **Step 5:** Commit.

## Task 2: Skeleton + compose

**Files:**
- Create: `KhaozEngine.Render3D/Animation/Skeleton.cs`
- Test: `KhaozEngine.Tests/Render3D/Animation/SkeletonComposeTests.cs`

**Interfaces:**
- Consumes: `JointPose`.
- Produces: `sealed class Skeleton { int[] ParentIndices; JointPose[] RestLocal; int[] NodeLogicalIndex; int[] JointToNode; int NodeCount; int BoneCount; int NodeForLogicalIndex(int logical); ctor(int[] parents, JointPose[] restLocal, int[] nodeLogicalIndex, int[] jointToNode); Matrix4x4[] ComposeRestPose(); }` (parents must be topologically ordered: a node's parent index < its own index, -1 for root).

- [ ] **Step 1:** Write `SkeletonComposeTests`: build a 2-node chain (node0 root, RestLocal T=(0,0,0); node1 parent 0, RestLocal T=(0,1,0)); JointToNode=[0,1]. `ComposeRestPose()[1].Translation` == (0,1,0). Add a third node child of node1 RestLocal T=(0,1,0): world translation == (0,2,0). `NodeForLogicalIndex` round-trips the supplied logical indices.
- [ ] **Step 2:** Run, verify FAIL.
- [ ] **Step 3:** Implement `Skeleton`. `ComposeRestPose`: `world[i] = i is root ? RestLocal[i].ToMatrix() : RestLocal[i].ToMatrix() * world[parent[i]]`; gather `bonePalette[b] = world[JointToNode[b]]`. `NodeForLogicalIndex` via a `Dictionary<int,int>` built lazily.
- [ ] **Step 4:** Run, verify PASS.
- [ ] **Step 5:** Commit.

## Task 3: AnimationClip + tracks

**Files:**
- Create: `KhaozEngine.Render3D/Animation/AnimationClip.cs`
- Test: `KhaozEngine.Tests/Render3D/Animation/AnimationSamplerTests.cs` (track-sampling cases first)

**Interfaces:**
- Produces:
  - `enum InterpolationMode { Linear, Step }`
  - `sealed class Vector3Track { float[] Times; Vector3[] Values; InterpolationMode Mode; Vector3 Sample(float t); float Duration; }`
  - `sealed class QuaternionTrack { float[] Times; Quaternion[] Values; InterpolationMode Mode; Quaternion Sample(float t); float Duration; }`
  - `sealed class JointTrack { int TargetNode; Vector3Track? Translation; QuaternionTrack? Rotation; Vector3Track? Scale; JointPose SampleLocal(in JointPose rest, float t); }`
  - `sealed class AnimationClip { string Name; float Duration; IReadOnlyList<JointTrack> Tracks; }`

- [ ] **Step 1:** Write track tests in `AnimationSamplerTests`: a `Vector3Track` Times [0,1] Values [(0,0,0),(2,0,0)] Linear → Sample(0.5)=(1,0,0); Step → Sample(0.5)=(0,0,0), Sample(1)=(2,0,0); clamp below first / above last. `QuaternionTrack` Linear slerps and stays unit length. `JointTrack.SampleLocal` overrides only the present channels (a track with only Translation keeps rest Rotation/Scale).
- [ ] **Step 2:** Run, verify FAIL.
- [ ] **Step 3:** Implement the tracks. `Sample`: binary/linear search the segment containing `t` (clamp to ends); Linear lerp/slerp by segment fraction; Step holds the left key. Empty track returns the rest channel value (handled in `SampleLocal`). `Duration` = last time (0 if empty).
- [ ] **Step 4:** Run, verify PASS.
- [ ] **Step 5:** Commit.

## Task 4: AnimationSampler (pose + compose + blend + wrap)

**Files:**
- Create: `KhaozEngine.Render3D/Animation/AnimationSampler.cs`
- Test: `KhaozEngine.Tests/Render3D/Animation/AnimationSamplerTests.cs` (append)

**Interfaces:**
- Consumes: `Skeleton`, `AnimationClip`, `JointPose`.
- Produces: `static class AnimationSampler { JointPose[] SamplePose(AnimationClip clip, Skeleton skel, float time); void Compose(Skeleton skel, ReadOnlySpan<JointPose> localByNode, Matrix4x4[] bonePaletteOut); Matrix4x4[] SampleToBonePalette(AnimationClip clip, Skeleton skel, float time); JointPose[] BlendPoses(ReadOnlySpan<JointPose> a, ReadOnlySpan<JointPose> b, float weight); static float Wrap(float time, float duration); }`

- [ ] **Step 1:** Append tests: `SamplePose` returns one pose per skeleton node; a node with no track keeps RestLocal; a node with a translation track at the sampled time gets the track value. `Compose` of `SamplePose` of an empty clip == `skel.ComposeRestPose()`. A 2-node chain animated so node1 translation goes (0,1,0)→(0,3,0) over [0,1]: at t=0.5 the composed world translation of bone 1 == (0,2,0). `BlendPoses` weight 0.5 of poses A (T=(0,0,0)) and B (T=(2,0,0)) → (1,0,0). `Wrap(1.2,1.0)`≈0.2, `Wrap(-0.1,1.0)`≈0.9, `Wrap(t,0)`==0.
- [ ] **Step 2:** Run, verify FAIL.
- [ ] **Step 3:** Implement. `SamplePose`: `pose[n] = clip-track-for(skel.NodeLogicalIndex[n]) is JointTrack jt ? jt.SampleLocal(skel.RestLocal[n], time) : skel.RestLocal[n]`. `Compose`: same hierarchy walk as `Skeleton.ComposeRestPose` but over the supplied poses. `SampleToBonePalette` = SamplePose then Compose. `BlendPoses` = per-node `JointPose.Lerp(a,b,weight)`. `Wrap` = `duration <= 0 ? 0 : time - floor(time/duration)*duration`.
- [ ] **Step 4:** Run, verify PASS.
- [ ] **Step 5:** Commit.

## Task 5: AnimationPlayer (advance + loop + crossfade)

**Files:**
- Create: `KhaozEngine.Render3D/Animation/AnimationPlayer.cs`
- Test: `KhaozEngine.Tests/Render3D/Animation/AnimationPlayerTests.cs`

**Interfaces:**
- Consumes: `Skeleton`, `AnimationClip`, `AnimationSampler`.
- Produces: `sealed class AnimationPlayer { ctor(Skeleton skel); AnimationClip? Current; bool IsBlending; void Play(AnimationClip clip, float crossfade = 0.15f); void Update(float dt); void GetBonePalette(Matrix4x4[] outPalette); Matrix4x4[] BonePalette(); float Time; }`

- [ ] **Step 1:** Write `AnimationPlayerTests`: `Play(clipA)` then `Update` advances `Time`, looping (Time stays < Duration after passing it). `Play(clipB, 0.2f)` sets `IsBlending` true; immediately the pose ≈ clipA's pose; after `Update(0.2f)` `IsBlending` false and pose ≈ clipB. `Play(sameClip)` does not restart `Time` or start a blend. `BonePalette` length == `skel.BoneCount`.
- [ ] **Step 2:** Run, verify FAIL.
- [ ] **Step 3:** Implement. State: `_to` clip + `_toTime`, `_from` clip + `_fromTime`, `_blend` (0..1), `_blendDur`. `Play`: if `clip==_to` no-op; else `_from=_to;_fromTime=_toTime;_to=clip;_toTime=0;_blendDur=crossfade>0 && _from!=null ? crossfade : 0; _blend = _blendDur>0?0:1`. `Update`: advance `_toTime`+`_fromTime` by dt (wrapped per clip Duration); if blending advance `_blend += dt/_blendDur`, clamp 1, clear `_from` at 1. Pose: sample `_to` local pose; if blending sample `_from`, `BlendPoses(from,to,_blend)`; compose. `Current=_to`, `IsBlending=_from!=null && _blend<1`, `Time=_toTime`.
- [ ] **Step 4:** Run, verify PASS.
- [ ] **Step 5:** Commit.

## Task 6: SkinnedGltfMesh carries an optional Skeleton

**Files:**
- Modify: `KhaozEngine.Render3D/Models/SkinnedGltfMesh.cs`
- Test: `KhaozEngine.Tests/Render3D/Animation/GltfLoadAnimationsTests.cs` (skeleton assertions)

**Interfaces:**
- Produces: `SkinnedGltfMesh.Skeleton` (`Skeleton?`), optional last ctor param `Skeleton? skeleton = null` on both ctors.

- [ ] **Step 1:** Write a test (in GltfLoadAnimationsTests): after `LoadSkinned` of the rigged 2-bone glb fixture, `mesh.Skeleton` is non-null, `BoneCount` matches, and `mesh.Skeleton.ComposeRestPose()` bone translations ≈ `mesh.RestPose` translations.
- [ ] **Step 2:** Run, verify FAIL (Skeleton null / member missing).
- [ ] **Step 3:** Add `public Skeleton? Skeleton { get; }` and the optional ctor param on both constructors (default null). (Task 7 populates it.)
- [ ] **Step 4:** This test stays red until Task 7; proceed.
- [ ] **Step 5:** Commit the type change.

## Task 7: GltfLoader builds the Skeleton + LoadAnimations

**Files:**
- Modify: `KhaozEngine.Render3D/Models/GltfLoader.cs`
- Test: `KhaozEngine.Tests/Render3D/Animation/GltfLoadAnimationsTests.cs`

**Interfaces:**
- Consumes: SharpGLTF `ModelRoot`, `Skin`, `Animation`; `Skeleton`, `AnimationClip`.
- Produces: `static IReadOnlyList<AnimationClip> GltfLoader.LoadAnimations(string path)`; `BuildSkinned` now attaches a `Skeleton`. Shared `static Skin? SelectSkin(ModelRoot root)`.

- [ ] **Step 1:** Author (in the test) a 2-bone rigged glb WITH an animation that translates bone1 from (0,1,0) to (0,3,0) over 1s (SharpGLTF `NodeBuilder.UseTranslation(track).WithPoint(...)`). Assert `LoadAnimations` returns 1 clip, Duration≈1, the clip has a `JointTrack` targeting bone1's node with a translation track, and sampling it at t=0.5 via `AnimationSampler.SampleToBonePalette(clip, mesh.Skeleton, 0.5)` yields bone1 world translation ≈ (0,2,0). Also assert the Task-6 rest-pose test now passes.
- [ ] **Step 2:** Run, verify FAIL.
- [ ] **Step 3:** Implement. Factor `SelectSkin(root)` (mesh-node skin preference then `LogicalSkins.FirstOrDefault`). `BuildSkeleton(root, skin)`: collect skeleton nodes = the union of every joint node and all its ancestors up to the scene root; topologically order (parents first); per node record parent index (within the set; -1 if its visual parent is outside the set), `RestLocal = JointPose.FromAffine(node.LocalTransform)`, `NodeLogicalIndex = node.LogicalIndex`; build `JointToNode[b]` = skeleton index of `skin.GetJoint(b).Joint`. Attach to the returned `SkinnedGltfMesh`. `LoadAnimations`: for each `root.LogicalAnimations`, group `anim.Channels` by `channel.TargetNode.LogicalIndex` into `JointTrack`s; per channel read the sampler keys (`GetTranslationSampler()/GetRotationSampler()/GetScaleSampler()` → `.GetLinearKeys()`/`.GetCubicKeys()`; map `InterpolationMode` STEP→Step else Linear; CUBICSPLINE → take the value tangent midpoint, treat as Linear); Duration = max key time across channels. Skip channels whose target node is not a skin joint-or-ancestor in the skeleton.
- [ ] **Step 4:** Run, verify PASS (and Task 6 test green).
- [ ] **Step 5:** Commit.

## Task 8: LocomotionStateMachine (Game.Render3D)

**Files:**
- Create: `KhaozEngine.Game.Render3D/LocomotionStateMachine.cs`
- Test: `KhaozEngine.Tests/Game/LocomotionStateMachineTests.cs`

**Interfaces:**
- Produces:
  - `enum LocomotionState { Idle, Walk, Run, Jump, Fall }`
  - `struct LocomotionThresholds { float WalkSpeed; float RunSpeed; static LocomotionThresholds Default; }` (Default WalkSpeed 0.1, RunSpeed 4.5)
  - `static class LocomotionStateMachine { static LocomotionState Evaluate(float horizontalSpeed, bool grounded, float verticalVelocity, LocomotionThresholds t); }`

- [ ] **Step 1:** Write tests: grounded speed 0 → Idle; speed 2 (≥Walk, <Run) → Walk; speed 6 (≥Run) → Run; airborne vVel +4 → Jump; airborne vVel -4 → Fall; airborne vVel 0 → Fall; airborne overrides speed (fast + airborne+rising → Jump).
- [ ] **Step 2:** Run, verify FAIL.
- [ ] **Step 3:** Implement: `if (!grounded) return verticalVelocity > 0 ? Jump : Fall; if (speed >= t.RunSpeed) return Run; if (speed >= t.WalkSpeed) return Walk; return Idle;`.
- [ ] **Step 4:** Run, verify PASS.
- [ ] **Step 5:** Commit.

## Task 9: AnimatedCharacter (Game.Render3D)

**Files:**
- Create: `KhaozEngine.Game.Render3D/AnimatedCharacter.cs`
- Test: `KhaozEngine.Tests/Game/AnimatedCharacterTests.cs`

**Interfaces:**
- Consumes: `Skeleton`, `AnimationClip`, `AnimationPlayer`, `LocomotionStateMachine`.
- Produces: `sealed class AnimatedCharacter { ctor(Skeleton skeleton, IReadOnlyDictionary<LocomotionState, AnimationClip> clips, LocomotionThresholds? thresholds = null, float crossfade = 0.15f); LocomotionState State; void Update(float horizontalSpeed, bool grounded, float verticalVelocity, float dt); Matrix4x4[] Pose { get; }; void CopyPose(Matrix4x4[] dst); }`

- [ ] **Step 1:** Write tests (build clips in code over a tiny hand-made Skeleton; one distinct translation per state): `Update(0, true, 0, dt)` sets `State==Idle` and `Pose` matches the idle clip's composed palette; `Update(6, true, 0, ...)` after the crossfade settles → `State==Run`; airborne rising → `State==Jump`; `Pose.Length == skeleton.BoneCount`; a missing clip in the dictionary falls back to Idle (no throw).
- [ ] **Step 2:** Run, verify FAIL.
- [ ] **Step 3:** Implement: hold an `AnimationPlayer`, the clip dict, thresholds, crossfade. `Update`: `State = Evaluate(...)`; resolve `clips.TryGetValue(State,...)` else `clips[Idle]`; `player.Play(clip, crossfade)`; `player.Update(dt)`. `Pose`/`CopyPose` from `player`. Play the initial Idle clip in the ctor so the first frame is posed.
- [ ] **Step 4:** Run, verify PASS.
- [ ] **Step 5:** Commit.

## Task 10: Commit the KayKit CC0 character asset

**Files:**
- Create: `TerrainWalkSample/assets/character/*.glb` + `LICENSE`/attribution.
- Modify: `TerrainWalkSample/TerrainWalkSample.csproj` (copy the asset to output).

- [ ] **Step 1:** Download a KayKit CC0 rigged+animated character glb (Character Pack: Adventurers, github.com/KayKit-Game-Assets, CC0). Inspect with a throwaway program: confirm `LoadSkinned` succeeds and `LoadAnimations` returns clips; print the clip names. Map the actual names → idle/walk/run/jump.
- [ ] **Step 2:** Commit the glb + a CC0 attribution note; add a `<None Include="assets/character/**" CopyToOutputDirectory="PreserveNewest" />` item.
- [ ] **Step 3:** Commit.

## Task 11: TerrainWalkSample uses the AnimatedCharacter

**Files:**
- Modify: `TerrainWalkSample/Program.cs`

- [ ] **Step 1:** In `OnLoad`: load the character via `GltfLoader.LoadSkinnedWithMaterial` (or `LoadSkinned`) + `LoadAnimations`; map clip names to a `Dictionary<LocomotionState, AnimationClip>`; `sc.LoadSkinnedMesh(...)`; build `AnimatedCharacter`. Keep the capsule mesh as a fallback only if the character fails to load.
- [ ] **Step 2:** In `OnUpdate`: compute horizontal speed from the XZ position delta over dt (or 0 on the settle frame); call `character.Update(speed, _character.Grounded, _character.VerticalVelocity, dt)`. Track a facing yaw that turns toward the horizontal move direction.
- [ ] **Step 3:** In `OnDraw3D`: `scene.DrawSkinned(handle, character.Pose, model, tint)` where `model` places the character feet-on-ground (`Position.Y - CapsuleHalfHeight`) and applies the facing yaw + any asset scale. Remove the capsule draw (keep behind the fallback).
- [ ] **Step 4:** Build + run headless smoke (`KE_MAX_FRAMES=3 dotnet run`) to confirm it renders without throwing.
- [ ] **Step 5:** Commit.

## Task 12: Release — version bump, docs, pack

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `CLAUDE.md`, `docs/USING-KHAOZENGINE.md`.

- [ ] **Step 1:** Bump `<KhaozEngineVersion>` to 7.56.0.
- [ ] **Step 2:** Add the newest-first `CHANGELOG.md` entry (one-line digest first sentence; list LoadAnimations/AnimationClip/Skeleton/AnimationSampler/AnimationPlayer in Render3D + LocomotionState/LocomotionStateMachine/AnimatedCharacter in Game.Render3D + the sample).
- [ ] **Step 3:** Update the 3 guard declarations (CONSUMERS "Engine current version", ROADMAP "Current released version", README `<PackageReference>` example). Run `scripts/check-doc-versions.sh` → pass.
- [ ] **Step 4:** Doc sweep: add the animation types to the CLAUDE.md Render3D + Game.Render3D package descriptions; add a USING-KHAOZENGINE animation usage section; grep the new type names across `*.md`.
- [ ] **Step 5:** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` all green; `dotnet pack -c Release -o ./local-feed`.
- [ ] **Step 6:** Commit the bump (all in one commit).

## Task 13: Release ritual — merge, tag, push, clean up

- [ ] Merge `feature/animated-characters` → main locally; re-run tests on the merged result; repack from the main root to `./local-feed`; `git tag v7.56.0`; push main + tag; remove the worktree + delete the merged branch.
- [ ] End with the windowed-validation `bash` block (boot TerrainWalkSample from the worktree path, or the main checkout if already merged).

## Self-Review notes

- Spec coverage: clip reading (T7), sampler+compose+interp+wrap (T3/T4), crossfade (T5), state machine (T8), AnimatedCharacter (T9), KayKit asset (T10), sample (T11), bone-palette-matches-hierarchy (T2/T4). All Testing-list items covered; GPU golden is optional and omitted (pure-math coverage is sufficient; if added later, bake on all 3 backends).
- Out-of-scope items explicitly not built (events/root-motion/IK/layers/blend-trees/networked).
- Type names consistent across tasks (JointPose, Skeleton, AnimationClip/JointTrack/Vector3Track/QuaternionTrack, AnimationSampler, AnimationPlayer, LocomotionState/LocomotionThresholds/LocomotionStateMachine, AnimatedCharacter).
</content>
</invoke>
