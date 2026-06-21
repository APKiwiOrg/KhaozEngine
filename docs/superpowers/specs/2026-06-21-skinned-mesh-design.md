# Runtime skinned / deformable mesh support in Render3D

Status: approved design, pre-implementation
Date: 2026-06-21
Engine area: KhaozEngine.Render3D (+ shaders, GltfLoader, tests, docs)

## Problem

KhaozEngine has no skeletal or vertex animation. Every moving part is a whole rigid
mesh moved by a per-instance transform (`Scene3D.Draw(mesh, matrix, tint)`). To fake a
writhing tentacle, SpaceGame chains ~8 rigid cone segments per tentacle and transforms
each per frame: it reads as a jointed robot arm, not flesh, and costs ~65 draw calls per
creature. Consumers (SpaceGame first, then Hardpoint / Nullwake / future games) need
organic, code-driven mesh deformation: soft-body wobble, bending limbs, cables, recoil
flex. The bottleneck is the missing engine capability.

## Goal

A smooth mesh can bend/deform at runtime under pure code control, consistent with the
engine's code-driven philosophy (no required glTF keyframe/animation tracks). One skinned
draw replaces many rigid-segment draws.

Non-goals (scope guardrails): full glTF keyframe-animation playback. Reading authored glb
rigs (bind poses + joint/weight attributes) IS in scope; sampling animation *tracks* over
time is not. Runtime bone control is the priority.

## Design overview: three layers

Everything lands in **KhaozEngine.Render3D** (already depends on KhaozEngine.Gpu +
SharpGLTF, so the package graph is unchanged). The existing rigid path (`ModelVertex`,
`ModelRenderer`, `Scene3D.Draw`) is left untouched; skinning is a parallel path.

- **Core (general substrate):** `SkinnedMeshHandle`, the `SkinnedVertex` format,
  `SkinnedModelRenderer` (its own pipeline + vertex shader + shared bone buffer), and the
  `Scene3D.LoadSkinnedMesh` / `Scene3D.DrawSkinned` API.
- **Loaders:** `GltfLoader.LoadSkinned(path)` (reads authored glb rigs) and a
  procedurally-defined bone set (bones defined in code, not glb).
- **Helper (optional, additive):** `SkinnedMeshBuilder.BuildTube(...)` plus a
  polyline→frames utility. This is the turn-key path for elongated bending shapes
  (tentacle, cable, limb). Future Layer-2 helpers (Bezier-spline-to-bones, cloth/ribbon
  strip, lattice cage, glb keyframe sampler) can land later as more small classes without
  touching the core or the public API anyone depends on.

Rationale for this layering (vs raw-bones-only or spline-as-headline): bone-palette
skinning is the universal primitive every deformation reduces to, so it stays general and
unconstrained; one focused helper covers the dominant code-driven pattern without baking a
single mental model (a spline) into the top-level API.

## Public API (Scene3D)

```csharp
// Load
SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh);
SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, TextureHandle texture);
void UnloadSkinnedMesh(SkinnedMeshHandle h);

// Draw (queued, like Draw). boneMatrices are per-frame JOINT WORLD transforms
// (model space) - one per bone in the mesh's skin.
void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices,
                 Matrix4x4 model, Color tint);
// Overloads mirroring the rigid path (Material / emissive+spec) as needed.
```

Supporting types:

```csharp
public readonly struct SkinnedMeshHandle { int Index; int Generation; } // mirrors MeshHandle

// Produced by GltfLoader.LoadSkinned or SkinnedMeshBuilder.
public sealed class SkinnedGltfMesh
{
    SkinnedVertex[] Vertices;
    ushort[]        Indices;
    Matrix4x4[]     InverseBind;   // one per bone
    int             BoneCount;
    Matrix4x4[]     RestPose;      // joint world transforms at bind (for identity/default)
}
```

## Skinned vertex format and bone math

```csharp
public struct SkinnedVertex
{
    public Vector3 Position;     // 0
    public Vector3 Normal;       // 12
    public Vector4 Color;        // 24
    public Vector2 Uv;           // 40
    public Vector4 BoneIndices;  // 48  (float-encoded, up to 4 bones)
    public Vector4 BoneWeights;  // 64  (normalized at load)
    public const uint SizeInBytes = 80;
}
```

Float-encoded bone indices keep the vertex layout portable across GL / Metal / Vulkan and
avoid integer-attribute pitfalls; bone counts are small so the extra bytes are immaterial.
Weights are normalized at load (sum to 1; a zero-weight vertex falls back to identity).

The **handle stores the inverse-bind matrices**. `DrawSkinned` takes per-frame **joint
world transforms** (model space) - exactly what a tentacle chain or a posed glb rig
produces. The engine composes, per bone, on the CPU (bone counts are tiny):

```
skinMatrix[b] = boneMatrices[b] * InverseBind[b]
```

Per vertex the shader blends: `skin = Σ weightᵢ · skinMatrix[indexᵢ]`, then
`world = Model · skin · position` and `normalW = mat3(Model · skin) · Normal`.

**Identity invariant (the core test):** pass the rest pose as `boneMatrices` and every
`skinMatrix == identity`, so the mesh does not move. For a glb skin, `InverseBind[b]` is
the authored inverse-bind and `RestPose[b]` is the bind-pose joint world transform. For a
procedural mesh, the builder records each bone's rest transform and stores
`InverseBind[b] = inverse(restWorld[b])`.

The color path is **untouched**: skinning only rewrites position + normal, so the
fragment shader is reused verbatim and `albedo = vColor · vTint · texRgb` holds.
baseColorFactor / tint / texture semantics are unchanged.

## Shared bone buffer + instanced skinned draws

One growable **read-only structured buffer** (SSBO, `set 1`, vertex stage) holds every
skinned draw's composed `skinMatrix` array for the frame. Rationale: a structured buffer
supports a large, growable, dynamically-indexed palette cleanly, unlike a size-capped UBO.

Each `DrawSkinned` appends its composed bones to the buffer and records an **offset**. The
offset rides as a **per-instance vertex attribute**, alongside the existing instance stream
(model rows + tint + emissive + spec). Because the offset is per-instance, multiple skinned
draws of the *same* mesh (e.g. 8 tentacles sharing one tube mesh) collapse into **one
instanced draw**, each instance reading its own bone range.

This reuses the rigid path's machinery: grow-by-2x buffer capacity, and group-by-mesh into
runs (a `GroupSkinnedInstances` mirroring the existing pure `GroupInstances`). The frame
UBO (viewproj + lights, `set 0`) and the material/texture resource set are shared unchanged
with the rigid pipeline.

`SkinnedModelRenderer` is a parallel class to `ModelRenderer` owning: the skinned pipeline,
the skinned vertex shader, the bone SSBO + its resource layout (`set 1`), per-frame bone
append + upload, and the instanced skinned draw. `Scene3D.Begin()` clears the skinned queue
and resets the bone buffer; `RenderInternal` renders the rigid pass, then the skinned pass.

## Shaders

A new **skinned vertex shader** only; the fragment shader is reused as-is (so the lit
color/lighting path is identical). The skinned vertex shader adds:

- `set 1, binding 0`: `readonly buffer Bones { mat4 bones[]; };`
- vertex inputs `BoneIndices` (location 4) and `BoneWeights` (location 5)
- per-instance `IBoneOffset` (moves the instance attributes up by 2 locations; harmless,
  separate pipeline)
- skin assembly with a zero-total-weight guard → identity
- `world = Model · skin · vec4(Position,1)`; normal via `mat3(Model · skin)`

All outputs to the fragment stage (vColor, vTint, vNormalW, vWorldPos, vUv, vEmissive,
vSpecParams, vDepth) match the rigid vertex shader so the shared fragment shader links.

## glTF skin loading

`GltfLoader.LoadSkinned(path)` extends the existing SharpGLTF loader to read:

- `JOINTS_0` (unsigned byte/short vec4) and `WEIGHTS_0` (float vec4) vertex accessors
- the skin's inverse-bind matrices (`skin.GetInverseBindMatrix(i)`) and joint node list
  (`skin.GetJoint(i)`) → `InverseBind[]`, `BoneCount`, `RestPose[]`

Embedded glb images stay ignored (consumers bind PNG albedo separately, as today). The
skinned mesh assembler carries joints/weights through welding (the weld key includes
joint+weight so verts with different rigging are not merged). If a primitive has no
JOINTS_0/WEIGHTS_0, `LoadSkinned` fails clearly (use `GltfLoader.Load` for rigid meshes).

## Procedural helper

```csharp
SkinnedGltfMesh SkinnedMeshBuilder.BuildTube(
    float radius, float length, int ringSegments, int radialSegments,
    int boneCount, Axis axis = Axis.Z);
```

Returns a tube along the chosen axis with `boneCount` bones evenly spaced along it. Each
vertex ring is weighted to its 1-2 nearest bones with a smooth cross-boundary falloff
(linear blend in the overlap band) so bending reads as flesh, not facets. Inverse-bind is
derived from the evenly-spaced rest layout, so the identity pose leaves the tube straight.

A small parallel-transport `PolylineFrames` utility converts a chain of points (+ an up
hint) into per-joint world transforms, for consumers that have only positions. SpaceGame's
tentacle already computes per-segment positions + rotations, so it supplies its bone
matrices directly and may skip this utility.

## Tests (headless, no GPU)

The blend is factored into a pure `SkinningMath` class so it is testable without a device:

- **identity:** rest pose as bone matrices → skin matrix == identity → vertex unmoved
- **single bone:** vertex fully weighted to one rotating bone → transforms exactly like it
- **two-bone blend:** 50/50 weight between two bones → averaged transform
- **weight normalization:** un-normalized input weights normalized at load
- **procedural tube:** identity pose unchanged; bending one bone curves the tip toward it
- **glb skin read:** build a minimal rigged mesh in-memory with SharpGLTF → assert correct
  InverseBind / joints / weights round-trip through `LoadSkinned`
- **bone-buffer packing:** multiple skinned draws → correct per-draw offsets / runs
  (mirrors the existing `GroupInstances` test)

Every new behaviour ships with a headless test, per the engine rule.

## Determinism, docs, release

- **Determinism:** skinning is presentation-only. Document that bone math / `DrawSkinned`
  must not touch sim / RNG / netcode.
- **docs/USING-KHAOZENGINE.md:** add the skinned-mesh usage section; remove or qualify the
  "no skeletal animation" / "no vertex animation" statements that this changes.
- **Release ritual:** bump `<KhaozEngine5xVersion>` in `Directory.Build.props`; add the
  CHANGELOG.md entry + the one-line CHANGENOTES.md digest; update the three version-
  declaration sites the doc guard checks (docs/CONSUMERS.md engine line, docs/ROADMAP.md
  current version, README.md PackageReference example); `dotnet pack -c Release -o
  ./local-feed`; commit; `git tag vX.Y.Z`; push main + tag. Additive feature → minor bump.

## Consumer follow-up (out of scope here)

After this ships, SpaceGame pins the new version and replaces the 8×8 rigid cone-segment
draws in `QueueEnemyMeshes` (MultiplayerReplicatedRunView) with a single skinned tentacle
mesh driven by the existing `SlathTentacleLayout` chain (its per-segment transforms become
bone matrices). That work is a separate SpaceGame chat.
