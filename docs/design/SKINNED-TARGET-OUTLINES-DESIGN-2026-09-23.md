# Skinned target outlines

Status: implemented for staged 20.5.0. Approved by the owner on 2026-09-23.

Tracking: [KhaozEngine #1053](https://github.com/APKiwiOrg/KhaozEngine/issues/1053).
Consumer: [Grimhollow #64](https://github.com/APKiwiOrg/Grimhollow/issues/64), rigged humanoid item E3.

## Goal

Allow a posed `SkinnedMeshHandle` and any rigid parts it carries to contribute to one existing
`MeshOutlineGroup`. The result must be one outer screen space silhouette with the current pixel width, group
colour, dissolve, alpha cutout, and occlusion rules. GPU skinning and the CPU fallback must outline the same bent
pose.

Outline submission is independent of ordinary scene submission. A caller may submit a skinned outline without a
matching `DrawSkinned` call, or submit the same mesh with a different pose. The outline pass therefore owns the
pose snapshot it needs and never searches the main draw queue for a matching caster.

## Scope

This design adds:

- grouped plain and dissolved skinned outline submission on `Scene3D`
- the matching `ITileWorldScene` surface and `Scene3DTileWorldScene` forwarding
- one mixed rigid and skinned union in the existing full and visible masks
- posed mask rendering through GPU skinning when `UseGpuSkinning` is true
- posed mask rendering through CPU deformation when `UseGpuSkinning` is false
- alpha cutout preservation for ordinary plain and dissolved skinned colour draws and both outline masks
- headless contract coverage and bent pose pixel proofs on Metal, Direct3D 11, and Vulkan

The existing rigid outline API, legacy inverted hull silhouette API, composite algorithm, group style, and tile
world object selection policy keep their current contracts. This work does not make the outline pass own
animation, choose a pose, or retain caller memory after submission.

Skinned alpha cutout in shadow maps is outside this target outline change and tracked by
[#1097](https://github.com/APKiwiOrg/KhaozEngine/issues/1097). The current key light skinned path has no cutout
variant, and the point shadow path has no skinned caster path. Those paths do not share the target outline
resources or acceptance proof and need a separate renderer decision.

## Public API

`Scene3D` gains the grouped methods requested by issue #1053:

```csharp
public void DrawSkinnedOutline(
    MeshOutlineGroup group,
    SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices,
    Matrix4x4 world);

public void DrawSkinnedOutlineDissolved(
    MeshOutlineGroup group,
    SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices,
    Matrix4x4 world,
    float dissolve,
    bool dissolveComplement);
```

It also gains the same one part convenience shape as the rigid plain overload:

```csharp
public void DrawSkinnedOutline(
    SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices,
    Matrix4x4 world,
    Color color,
    float widthPixels);

public void DrawSkinnedOutline(
    SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices,
    Matrix4x4 world,
    Color color,
    float widthPixels,
    MeshOutlineOcclusion occlusion);
```

The convenience overload starts a group and submits one skinned part. Dissolved submission remains grouped,
matching the current rigid surface.

`ITileWorldScene` gains the two grouped methods with compatibility defaults:

```csharp
void DrawSkinnedOutline(
    MeshOutlineGroup group,
    SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices,
    Matrix4x4 world) { }

void DrawSkinnedOutlineDissolved(
    MeshOutlineGroup group,
    SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices,
    Matrix4x4 world,
    float dissolve,
    bool dissolveComplement) =>
    DrawSkinnedOutline(group, mesh, boneMatrices, world);
```

The shipped adapter forwards both calls directly to `Scene3D`. The defaults follow the existing outline seam:
older headless implementations continue compiling and ignore an outline they cannot render.

## Submission and lifetime

`MeshOutlineGroup` remains the only group handle. A group may contain rigid parts, skinned parts, or both. Its
owner, frame, style, occlusion, and stale handle checks do not change.

`Scene3D` changes its internal outline part from a rigid only record into a tagged rigid or skinned record. A
skinned record contains the handle, world transform, dissolve values, and a slice into an outline owned list of
composed bone matrices. The list is separate from `_boneMatrices`, which belongs to ordinary skinned draws.

Submission follows this order:

1. Validate the group owner, frame, and index exactly as rigid submission does.
2. If the skinned handle is invalid or already unloaded, treat the call as a no-op, matching `DrawSkinned`.
3. Require the supplied pose length to equal the loaded mesh bone count and remain within
   `SkinningMath.MaxBonesPerDraw`.
4. Compose each supplied joint matrix with the mesh inverse bind matrix and append the result to the outline pose
   list.
5. Append the skinned part only after composition succeeds.

This copies the effective pose during the call. The caller may mutate or release its span afterward. A failed
bone count check leaves the group and pose list unchanged. `Scene3D.Begin` clears the outline groups and their pose
storage together. An unloaded handle encountered later during rendering is skipped, as a rigid outline part is
today.

No main draw identity, queue index, visibility classification, palette slot, or CPU deformed vertex offset is
stored in an outline part. Those values belong to the main and shadow passes and are not valid evidence that an
outline submission exists.

## Material and alpha cutout semantics

`SkinnedMeshEntry` retains two values that `LoadSkinnedMesh(SkinnedGltfMesh, SurfaceMaps)` currently drops:

- `AlphaCutoff`
- an albedo only target outline material set, using the same albedo texture as the ordinary skinned material

The texture overload creates the outline material set when its texture handle is valid. The `SurfaceMaps`
overload passes `maps.AlphaCutoff` into the entry and creates the outline set when an albedo map exists. An absent
albedo uses the outline renderer's white default. A positive cutoff with that white default keeps every fragment,
which matches the ordinary colour path.

The ordinary plain and dissolved skinned colour paths must agree with the outline mask before cutout can be called
parity. The per draw values stop overloading one field:

- `SpecParams.z` carries alpha cutoff on both CPU and GPU skinned draws
- the existing `InstanceData.Dissolve` carries CPU dissolve threshold and edge width
- `P[3].zw` carries GPU dissolve threshold and edge width while `P[3].x` keeps the dynamic geometry flag

The existing 128 byte GPU skinned header and its 256 byte slot do not grow. `ModelDissolveFrag` reads the existing
`vDissolve` varying instead of treating `vSpecParams.zw` as dissolve values. `SkinnedModelVert` emits the new
GPU dissolve varying at the next contiguous location. `SkinnedModelDissolveFrag` reads it and uses
`vSpecParams.z` for the alpha test. Plain fragments continue using `vSpecParams.z` as they already do.

Both outline mask fragments already alpha test before writing coverage. The new skinned mask vertex supplies the
same UV and posed world position inputs, and the queued draw supplies the retained cutoff and outline material set.
Alpha transparent texels are therefore genuine silhouette holes in both the full and visible masks.

## Renderer data flow

`TargetOutlineRenderer` keeps one ordered draw queue per group and adds skinned draw commands to that queue. The
full mask is cleared once, every rigid and skinned part draws into it, then the visible mask does the same when the
group uses scene depth. The group is composited once. Rigid and skinned parts must not be rendered and composited
as separate groups because that would draw borders through their overlap.

### GPU skinning

The renderer adds one skinned mask vertex shader. It reads the existing skinned vertex layout, blends the submitted
palette with the same zero weight identity fallback as `SkinnedModelVert`, transforms the posed position through
the part world matrix, and emits the same `vUv` and absolute `vWorldPos` contract as the rigid mask vertex.

The rigid and skinned mask pipelines share the existing full and visible fragment shaders, material layout, mask
targets, rasterizer state, and composite. The skinned pipelines add a vertex only dynamic palette layout. The
outline renderer owns a growable palette buffer because an outline may have no corresponding main draw palette.
It packs one slot for each queued skinned outline part and uploads the packed image before the group mask draws.

### CPU fallback

The renderer uses the cached source `SkinnedVertex` array and the submitted composed palette to run
`SkinningMath.SkinVertex`. It appends the resulting `ModelVertex` values to an outline owned scratch list, uploads
that list once for the group, and records each draw with its base vertex. Those draws then use the existing rigid
mask pipelines and the same index buffer as the loaded skinned mesh.

CPU outline deformation cannot reuse the ordinary CPU skinned stream. The ordinary stream omits outline only
submissions, applies main pass culling, and assigns offsets from main draw order. Keeping an outline owned stream
makes the independent submission contract explicit.

`UseGpuSkinning` selects the path when the frame is rendered. Both paths consume the same copied, composed pose.
Frames without skinned outline parts allocate or upload no new skinning resources.

## Dissolve and occlusion

Skinned parts use the rigid outline rules without reinterpretation.

- The full mask applies geometry and alpha cutout, but not a partial dissolve. Keeping the full projected envelope
  suppresses dither holes as interior strokes.
- The visible mask applies the exact fragment keep rule. Ordinary dissolve keeps `mask >= threshold`, while the
  complement keeps `mask < threshold`.
- A non-complement part at dissolve `1` and a complement part at dissolve `0` are fully invisible and skipped so
  they do not enlarge the union envelope.
- `MeshOutlineOcclusion.SceneDepth` uses the visible mask and the existing source and destination depth tests.
  A partially dissolved skinned target therefore clips the external outline to visible body coverage while the
  full mask still prevents outlines inside its dither holes.
- `MeshOutlineOcclusion.None` retains the current rigid behavior. It composites from the full union and skips the
  visible pass, so partial dissolve does not punch holes into a through geometry marker. Fully invisible endpoint
  parts are still skipped.

The issue phrase "clips the outline with the body" means the existing visible coverage rule for the default
scene depth mode. It does not change the full mask rule or introduce outlines around dissolve noise holes.

An outline only submission still obeys occlusion. With scene depth it tests the posed target against whatever
depth the scene did write. With no matching body draw, background depth does not suppress it. With occlusion
`None`, it follows the full posed silhouette through all scene geometry.

## Failure and resource handling

Skinned load becomes transactional across vertex buffer, index buffer, ordinary CPU material set, ordinary GPU
material set, and outline material set creation. If any later creation fails, every resource created by that call
is disposed before the exception escapes and no handle slot is committed.

Unloading a skinned mesh retires its outline material set with its vertex buffer, index buffer, and ordinary
material sets. Scene disposal also disposes it. Shadow layout replacement preserves the outline set and cutoff
because neither depends on a shadow texture.

Outline palette and CPU vertex buffer growth follows the renderer's existing grow then replace rule. A new buffer
and its resource set are fully created before becoming current. Failure disposes the partial replacement and
leaves the old capacity usable for a retry. Replaced resources remain alive until no submitted command can read
them. Mask target allocation and retry behavior stay unchanged.

An invalid group throws before any pose is copied. A valid group with an invalid mesh handle is a no-op. A valid
mesh with the wrong pose length throws `ArgumentException`. Fully dissolved parts perform no GPU work. A resource
failure aborts the render call and never composites a partial group.

## Verification

### Headless contracts

Add focused tests for:

- one group collecting rigid and skinned parts while preserving one group count and the total part count
- wrong scene and stale frame rejection before pose storage changes
- pose length validation and the 128 bone cap
- immediate pose copying, proved by mutating the caller array after submission
- `Begin` clearing groups, skinned pose slices, and part counts together
- invalid and unloaded skinned handles producing no outline draw
- skinned outline command recording with zero ordinary skinned instances, for both `UseGpuSkinning` values
- separate outline and ordinary pose storage when the same skinned handle is submitted twice with different poses
- GPU palette slot offsets and CPU base vertex offsets across multiple skinned parts
- mixed queue order and one full pass, one visible pass, and one composite per nonempty group
- plain and dissolved shader source contracts keeping live varyings contiguous for FXC
- `SurfaceMaps.AlphaCutoff` reaching plain and dissolved CPU and GPU skinned colour parameters and outline parameters
- skinned load rollback, capacity growth rollback, unload retirement, and final disposal
- `ITileWorldScene` compatibility defaults and exact `Scene3DTileWorldScene` forwarding

### Pixel and golden proofs

Add a `Golden` GPU fixture that renders a bent skinned tube overlapping a rigid held part in one outline group. The
bend must move several bones far enough that a rest pose mask visibly misses. Capture the scene through GPU
skinning and the CPU fallback.

For each path, pixel assertions must prove:

- the border follows the bent pose and does not follow the rest pose
- rigid and skinned overlap has no internal stroke
- no outline pixel replaces ordinary target coverage from an unoutlined baseline
- an opaque foreground wall suppresses the default outline without creating an occlusion cut line
- a partial dissolve changes visible external border coverage without outlining its interior dither holes
- a skinned alpha cutout hole is absent from the ordinary body and is a genuine outline silhouette hole
- an outline only skinned submission renders the bent silhouette without a main draw
- the same skinned handle drawn ordinarily in its rest pose and outlined in a visibly bent pose follows ONLY the
  submitted outline pose, for both CPU and GPU skinning

Compare the CPU and GPU captures within the existing skinning parity tolerance. Also commit one reference grid per
live backend family for each path through `GoldenCompare.AssertOrUpdate`. The Metal, Direct3D 11, and Vulkan CI
legs bake and verify their own families as `docs/CROSS-PLATFORM.md` requires. A final image golden is paired with
the direct pixel invariants because the coarse grid alone cannot prove the intermediate full and visible mask
rules.

The focused implementation checks are:

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~TargetOutline|FullyQualifiedName~SkinnedQueue|FullyQualifiedName~TileWorldSceneSkinned"

KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~TargetOutline"
```

The finished implementation also runs the repository Release build, non-live test suite, shader validation, and
whole tree guards required by `AGENTS.md` and `docs/CROSS-PLATFORM.md`.

## Constraints

- The caller remains responsible for supplying a valid current pose every frame.
- The existing maximum of 128 bones per draw remains unchanged.
- Mask shaders remain two sided, matching the ordinary model coverage contract.
- Shader inputs remain contiguous across every live input range, with deliberate sinks for declared vertex
  attributes that the mask does not otherwise use.
- Final output pixel width, render scale, MSAA resolve behavior, group submission order, and composite placement
  remain unchanged.
- Alpha cutout uses the loaded albedo alpha and cutoff. Normal, roughness, tint, lighting, and dissolve edge colour
  do not affect mask coverage.
- No skinned outline data is persisted across `Scene3D.Begin`.

## Alternatives considered

### Always CPU skin outline geometry

This would reuse the rigid mask pipelines and minimize shader work, but it would add per vertex CPU deformation
and upload to the default GPU skinning path. It also weakens the bent pose proof by testing a different deformation
implementation from the main GPU draw. Reject it as the universal path. Keep it only as the explicit CPU fallback.

### Reuse a matching ordinary skinned draw

This could share the main palette or CPU vertex offset, but it would make outlines depend on main queue identity,
culling, submission order, and pose equality. It fails the required outline only case and makes tile world
forwarding fragile. Reject it.

### Composite rigid and skinned parts separately

This avoids a heterogeneous mask queue, but each composite sees the other part as empty space and paints an
internal border where the body meets a held item. That violates the defining union contract of
`MeshOutlineGroup`. Reject it.

## Acceptance

- One `MeshOutlineGroup` forms one outer silhouette from any mix of rigid and skinned parts.
- GPU and CPU outlines follow the submitted bent pose and agree within the established skinning tolerance.
- Plain and dissolved skinned outlines work through `Scene3D` and `Scene3DTileWorldScene`.
- Partial dissolve preserves the full mask envelope and clips only visible source coverage in scene depth mode.
- Alpha cutout agrees between ordinary plain and dissolved skinned colour and both outline masks.
- Outline submission works without a corresponding ordinary skinned draw.
- Invalid lifetime and allocation cases leave no retained pose data, leaked resources, or partial composite.
- Metal, Direct3D 11, and Vulkan pass the headless shader contracts, pixel invariants, and their backend goldens.
- After Grimhollow adopts the released engine pin, its rigged target outline golden shows the skinned body and
  rigid held pieces as one bent, dissolving silhouette before the live avatar switch.
