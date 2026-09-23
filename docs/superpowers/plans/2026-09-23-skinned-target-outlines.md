# Skinned Target Outlines Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a posed `SkinnedMeshHandle` and its rigid carried parts form one existing pixel-width `MeshOutlineGroup`, with independent pose ownership, CPU and GPU skinning parity, dissolve and alpha cutout coverage, tile-world forwarding, and backend-specific pixel evidence.

**Architecture:** `Scene3D` owns a copied composed pose slice for every submitted skinned outline part, separate from ordinary skinned draw storage. `TargetOutlineRenderer` keeps one ordered heterogeneous queue per group and feeds rigid, GPU-skinned, and CPU-skinned geometry into the existing full and visible masks before one composite. Skinned mesh loading retains the alpha cutoff and an albedo-only outline material, while ordinary skinned colour shaders carry alpha cutoff and dissolve in separate fields.

**Tech Stack:** .NET 10, C# 14, KhaozEngine Render3D and TileWorld.Render3D, GLSL 450 compiled through the engine GPU backends, xUnit, `FakeGpuDevice`, `RecordingGpuCommandList`, `Render3DSnapshot`, `GoldenCompare`, Metal, Direct3D 11, Vulkan

**Spec:** `docs/design/SKINNED-TARGET-OUTLINES-DESIGN-2026-09-23.md`

## Global Constraints

- Engine issue [#1053](https://github.com/APKiwiOrg/KhaozEngine/issues/1053) owns this implementation.
- Cascaded key-light skinned alpha cutout and any other skinned shadow cutout work remain in scoped follow-up [#1097](https://github.com/APKiwiOrg/KhaozEngine/issues/1097). This plan changes ordinary colour draws and target-outline masks only.
- `Scene3D` remains the submission owner. `TargetOutlineRenderer` remains the mask, scratch-buffer, and composite owner.
- Outline submission is independent of ordinary scene submission. It does not search `_skinnedInstances`, `_boneMatrices`, `_gpuSkinnedDraws`, or `_cpuSkinnedDraws` for a matching draw.
- The caller supplies one current pose per outline submission. The scene copies the effective composed pose before the call returns and retains no caller memory.
- One `MeshOutlineGroup` may mix rigid and skinned parts. It produces one full mask, at most one visible mask, and one composite.
- `SkinningMath.MaxBonesPerDraw` remains 128.
- `UseGpuSkinning` selects the outline deformation path at render time. Both paths consume the same copied composed pose.
- The 128-byte GPU skinned colour header and its 256-byte aligned slot do not grow.
- `SpecParams.z` carries alpha cutoff on CPU and GPU skinned colour draws. CPU dissolve stays in `InstanceData.Dissolve`. GPU dissolve uses `P[3].zw`, while `P[3].x` remains the dynamic geometry flag.
- Full target masks apply geometry and alpha cutout but ignore partial dissolve. Visible masks apply the exact ordinary or complement dissolve keep rule.
- A non-complement part at dissolve `1` and a complement part at dissolve `0` issue no mask draw and cannot enlarge the union envelope.
- Mask shaders remain two sided. Vertex inputs and every live varying range remain contiguous, with deliberate sinks for declared but otherwise unused attributes.
- Alpha cutout reads only loaded albedo alpha and cutoff. Normal, roughness, tint, lighting, and dissolve edge colour do not affect outline coverage.
- Frames with no skinned outline part allocate and upload no outline skinning buffer.
- Skinned load and outline buffer growth are transactional. A failed creation commits no handle or replacement and a render failure composites no partial group.
- Grown-out GPU resources stay alive until no submitted command can read them. The outline renderer may follow its existing conservative retirement list and dispose them with the renderer.
- `Scene3D.Begin` clears outline groups, parts, and copied outline poses together.
- Preserve rigid outline behavior, legacy inverted-hull silhouettes, target mask formats, MSAA resolve rules, pixel width, render scale, group order, and composite placement.
- Follow KESIZE. Put new cohesive behavior in new partial files and focused helper types. Do not raise `.filesize-baseline` or add an exemption.
- Every task ends with its focused Release tests green before its commit. Do not carry a knowingly red task into the next review gate.
- Re-read the live version, newest tag, and current `origin/main` before staging a release version. Ride an existing unreleased version. If the live version still equals its release tag, take the next free minor because this is a new additive renderer capability.
- At plan time `Directory.Build.props` is `20.1.0` and the newest tag is `v20.1.0`, so the expected next free version is `20.2.0`. Recompute this at execution time.
- Do not create a release tag. The owner decides when to run `scripts/tag-release.sh`.
- Do not add em dash or en dash glyphs to Markdown or C# comments. Do not add prose semicolons to Markdown.

## Review Focus

1. A wrong-scene group, stale-frame group, or wrong-length pose must leave both the group part count and copied pose count unchanged. `SkinnedTargetOutlineApiTests.Invalid_submission_is_atomic` in Task 1 pins this.
2. A valid group with an invalid or unloaded skinned handle must remain a no-op, including when the mesh unloads after submission but before rendering. `SkinnedTargetOutlineApiTests.Invalid_and_unloaded_handles_do_not_retain` in Task 1 and `SkinnedTargetOutlineRendererTests.Unloaded_after_submission_records_no_mask_draw` in Task 5 pin this.
3. Mutating the caller pose after submission, or drawing the same handle ordinarily with a different pose, must not change the outline pose. `SkinnedTargetOutlineApiTests.Submission_copies_the_composed_pose` in Task 1 and `SkinnedTargetOutlinePoseGoldenTests.Ordinary_rest_pose_and_bent_outline_use_only_their_own_poses` in Task 7 pin this.
4. A resource-set or buffer failure during skinned load, GPU palette growth, or CPU stream growth must dispose only the partial candidate, keep the prior capacity usable, and issue no partial composite. `SkinnedMeshMaterialLifetimeTests.Load_is_transactional_through_outline_material_creation` in Task 3 and the two growth rollback tests in Task 6 pin this.
5. Partial dissolve and alpha cutout must agree across ordinary CPU and GPU colour, the full mask, and the visible mask. Fully invisible endpoints must do no GPU work. `SkinnedColorCutoutContractTests` in Task 4, `SkinnedTargetOutlineRendererTests.Fully_invisible_endpoints_record_no_outline_work` in Task 5, and the dissolve and cutout pixel rows in Task 7 pin this.

---

## File and Responsibility Map

### Production files to create

- `KhaozEngine.Render3D/Scene3D.SkinnedTargetOutlines.cs`: public skinned outline overloads, group-first validation, copied composed pose storage, and skinned part factories.
- `KhaozEngine.Render3D/Scene3D.SkinnedMeshLoading.cs`: cohesive skinned mesh upload transaction and `SkinnedMeshEntry` state moved out of the oversized `Scene3D.cs` partial.
- `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.Skinned.cs`: GPU-skinned and CPU-skinned outline enqueue and draw behavior layered onto the existing renderer.
- `KhaozEngine.Render3D/Rendering/TargetOutlineSkinningStore.cs`: transactional outline-owned palette and CPU vertex buffers, persistent CPU images, conservative grown-resource retirement, and one-upload methods.

### Production files to modify

- `KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs`: tagged rigid or skinned parts, common group lifetime, render-time handle resolution, mixed submission order, endpoint skipping, and one composite per group.
- `KhaozEngine.Render3D/Scene3D.cs`: remove the skinned loading and entry blocks moved to their focused partial, retain alpha cutoff in CPU and GPU draw records, and clear or dispose new state through its owners.
- `KhaozEngine.Render3D/Scene3D.GpuSkinning.cs`: pass the dedicated GPU dissolve vector to the fixed-size header packer.
- `KhaozEngine.Render3D/Scene3D.Unload.cs`: retire the skinned outline material with the other per-mesh resources.
- `KhaozEngine.Render3D/Scene3D.ShadowLayoutReplacement.cs`: preserve `AlphaCutoff` and `OutlineMaterialSet` while replacing only shadow-dependent ordinary material sets.
- `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs`: pack `P[3].zw` without changing `SkinnedHeaderBytes` or `SkinnedMainSlotBytes`.
- `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.cs`: become a partial class, rename the rigid enqueue explicitly, share the ordered tagged queue, and initialize and dispose skinned resources.
- `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs`: separate alpha cutoff from CPU and GPU dissolve varyings.
- `KhaozEngine.Render3D/Internal/ShaderSources.TargetOutline.cs`: add the skinned mask vertex shader with the same UV and absolute-world-position output contract.
- `KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs`: compatibility defaults and exact `Scene3DTileWorldScene` forwarding.

### Test files to create

- `KhaozEngine.Render.Tests/Render3D/SkinnedTargetOutlineApiTests.cs`: frame-local handles, validation ordering, pose copy, mixed counts, independent ordinary and outline storage, and endpoint bookkeeping.
- `KhaozEngine.Render.Tests/Render3D/SkinnedMeshMaterialLifetimeTests.cs`: retained cutoff and outline set, load rollback, unload retirement, scene disposal, and shadow-layout preservation.
- `KhaozEngine.Render.Tests/Render3D/SkinnedColorCutoutContractTests.cs`: CPU and GPU plain and dissolved payloads plus shader source contracts.
- `KhaozEngine.Render.Tests/Render3D/SkinnedTargetOutlineRendererTests.cs`: GPU and CPU command recording, queue order, offsets, pass counts, no-main-draw behavior, unloaded handles, endpoint skips, and buffer rollback.
- `KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlineScene.cs`: shared bent tube, rigid held part, cutout texture, wall, dissolve, baseline, and pixel classification fixture.
- `KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlineGoldenTests.cs`: mixed union, body preservation, occlusion, dissolve, and cutout pixel invariants.
- `KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlinePoseGoldenTests.cs`: bent versus rest, outline-only, mismatched ordinary and outline poses, CPU and GPU parity, and committed-grid assertions.

### Existing test support and docs to modify

- `KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs`: deterministic buffer-creation failure injection needed by transaction tests.
- `KhaozEngine.Render.Tests/Gpu/RecordingGpuCommandList.Draws.cs`: record indexed draw base vertex so CPU outline offsets are asserted at the command seam.
- `KhaozEngine.Render.Tests/Render3D/TargetOutlineApiTests.cs`: keep existing rigid behavior pinned beside the new skinned API.
- `KhaozEngine.Render.Tests/Render3D/TargetOutlineRendererTests.cs`: preserve existing mask and draw-buffer retry coverage.
- `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs`: fixed header size, `P[3]` field ownership, and contiguous shader varying contracts.
- `KhaozEngine.Render.Tests/TileWorld/TileWorldSceneSkinnedTests.cs`: compatibility defaults and exact forwarding for both skinned outline methods, plus updated dissolve payload assertions.
- `KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs`: validate the shipped rigid and skinned outline mask pairs through real FXC output, including the skinned seven-element vertex signature.
- `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs`: enumerate the three existing rigid outline pairs and the two new skinned mask pairs, raising the catalog count from 43 to 48.
- `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`: regenerate after the ordinary skinned shader edits and again after all five outline pairs enter the shipped catalog.
- `KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs` and `VulkanShaderBindingTableTests.cs`: update the pinned 43-program catalog counts to 48 when the five pairs enter.
- `KhaozEngine.Render.Tests/Gpu/VulkanDescriptorLimitTests.cs` and `VulkanLayoutCompatibilityTests.cs`: add all five shipped outline pipeline layouts and update the 37-layout and pair-count assertions to 42.
- `KhaozEngine.Render.Tests/Gpu/VulkanSpirvByteEqualityTests.cs`: update the pinned emitted-stage count from 94 to 104.
- `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`, `msl-hashes/metal-msl.sha256.txt`, and `spirv-hashes/vulkan-spirv.sha256.txt`: regenerate through their guarded writers after each task that changes shipped shaders.
- `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_cpu.metal-native.txt`
- `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_cpu.direct3d11-native.txt`
- `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_cpu.vulkan-native.txt`
- `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_gpu.metal-native.txt`
- `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_gpu.direct3d11-native.txt`
- `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_gpu.vulkan-native.txt`
- `KhaozEngine.Render3D/README.md`: package quick reference for skinned grouped outlines and cutoff behavior.
- `docs/USING-KHAOZENGINE.md`: public API examples, pose lifetime, tile-world surface, CPU and GPU behavior, dissolve, cutoff, and shadow follow-up.
- `docs/INDEX.md`: mark the design implemented for the staged version.
- `docs/design/SKINNED-TARGET-OUTLINES-DESIGN-2026-09-23.md`: change status only after implementation and all backend evidence are complete.
- `CHANGELOG.md`: player-facing engine capability in the staged version entry.
- `Directory.Build.props`, `README.md`, and guarded `docs/USING-KHAOZENGINE.md` package references: version staging only when the live version and tag check requires it.

---

### Task 1: Add Scene-Owned Skinned Outline Submission and Pose Snapshots

**Files:**

- Create: `KhaozEngine.Render3D/Scene3D.SkinnedTargetOutlines.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedTargetOutlineApiTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/TargetOutlineApiTests.cs`

**Interfaces:**

- Consumes: `MeshOutlineGroup`, `SkinnedMeshHandle`, `SkinnedMeshEntry.InverseBind`, `SkinningMath.Compose`, `SkinningMath.MaxBonesPerDraw`, `Scene3D.Begin`
- Produces: the four public `Scene3D.DrawSkinnedOutline` overloads from the spec, `DrawSkinnedOutlineDissolved`, a tagged `MeshOutlinePart`, and outline-owned composed pose slices
- Produces for later tasks: `MeshOutlinePart.Kind`, `SkinnedMesh`, `BoneStart`, `BoneCount`, `_outlineBoneMatrices`, and `OutlinePoseMatrixCount`

- [ ] **Step 1: Add failing tests for the public overloads, mixed grouping, atomic validation, and copied pose ownership.**

Use a `FakeGpuDevice` scene and a small `SkinnedMeshBuilder.BuildTube` mesh. Name the test class `SkinnedTargetOutlineApiTests` so it is included by the final focused filter.

```csharp
[Fact]
public void One_group_collects_rigid_and_skinned_parts_with_one_group_count()
{
    using SceneHarness h = SceneHarness.Create();
    MeshHandle rigid = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
    SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.25f, 2f, 4, 6, 3, Axis.Z);
    SkinnedMeshHandle skinned = h.Scene.LoadSkinnedMesh(tube);
    h.Scene.Begin();
    MeshOutlineGroup group = h.Scene.BeginMeshOutline(Color.White, 1.25f);

    h.Scene.DrawMeshOutline(group, rigid, Matrix4x4.Identity);
    h.Scene.DrawSkinnedOutline(group, skinned, tube.RestPose, Matrix4x4.Identity);

    Assert.Equal(1, h.Scene.MeshOutlineGroupCount);
    Assert.Equal(2, h.Scene.MeshOutlinePartCount);
    Assert.Equal(tube.BoneCount, h.Scene.OutlinePoseMatrixCount);
}

[Fact]
public void Submission_copies_the_composed_pose()
{
    using SceneHarness h = SceneHarness.Create();
    SkinnedGltfMesh tube = Tube();
    SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
    Matrix4x4[] pose = BentPose(tube);
    h.Scene.Begin();
    MeshOutlineGroup group = h.Scene.BeginMeshOutline(Color.White, 1.25f);

    h.Scene.DrawSkinnedOutline(group, mesh, pose, Matrix4x4.Identity);
    Matrix4x4 copied = h.Scene.OutlinePoseMatrixAt(1);
    pose[1] = Matrix4x4.CreateScale(99f);

    Assert.Equal(copied, h.Scene.OutlinePoseMatrixAt(1));
    Assert.NotEqual(pose[1], h.Scene.OutlinePoseMatrixAt(1));
}
```

Add `Invalid_submission_is_atomic` with all of these rows:

- a group from another scene with a valid mesh
- a group from the prior frame with a valid mesh
- a valid group, valid mesh, and pose one matrix short
- a valid group, valid mesh, and a 129-bone mesh

Capture `MeshOutlinePartCount` and `OutlinePoseMatrixCount` before every call and assert neither changes after the expected `ArgumentException`. The wrong-scene and stale-frame rows must prove the group error occurs before pose validation or copying.

Add `Invalid_and_unloaded_handles_do_not_retain` for `default(SkinnedMeshHandle)` and a handle unloaded before submission. Both use a valid group and assert zero part and pose growth. Task 5 separately proves that unloading after submission produces no mask draw.

Add tests that both convenience overloads create one group with one part and the requested occlusion, that dissolve clamps to `0..1`, and that `Begin` clears group, part, and pose counts together.

- [ ] **Step 2: Run the new test class and confirm it fails at compile time on the missing API.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedTargetOutlineApiTests"
```

Expected: exit `1`, with compiler errors naming `DrawSkinnedOutline`, `DrawSkinnedOutlineDissolved`, and the internal pose observables.

- [ ] **Step 3: Add the exact public API in the new partial.**

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

The grouped methods call one private `AddSkinnedOutlinePart`. The convenience methods call `BeginMeshOutline` and then the grouped plain method.

- [ ] **Step 4: Make group validation one shared operation and validate it before touching mesh or pose state.**

Move the existing rigid predicate into this helper in `Scene3D.TargetOutlinePass.cs`:

```csharp
MeshOutlineDrawGroup RequireMeshOutlineGroup(MeshOutlineGroup group)
{
    if (group.Owner != _outlineOwner || group.Frame != _outlineFrame
        || group.Index < 0 || group.Index >= _meshOutlineGroups.Count)
        throw new ArgumentException(
            "outline group does not belong to this scene and frame.", nameof(group));
    return _meshOutlineGroups[group.Index];
}
```

Call it first from rigid and skinned submission. After it returns, an invalid or unloaded skinned handle is a no-op. A live handle then validates pose length and the 128-bone cap before appending anything.

- [ ] **Step 5: Add an append-only composed pose helper that is atomic on validation failures.**

```csharp
static (int Start, int Count) AppendOutlinePose(
    List<Matrix4x4> destination,
    ReadOnlySpan<Matrix4x4> boneMatrices,
    Matrix4x4[] inverseBind)
{
    if (boneMatrices.Length != inverseBind.Length)
        throw new ArgumentException(
            $"boneMatrices length {boneMatrices.Length} must equal the mesh bone count {inverseBind.Length}.",
            nameof(boneMatrices));
    if (boneMatrices.Length > SkinningMath.MaxBonesPerDraw)
        throw new ArgumentException(
            $"a skinned mesh has {boneMatrices.Length} bones, over the {SkinningMath.MaxBonesPerDraw}-bone per-draw cap.",
            nameof(boneMatrices));

    int start = destination.Count;
    for (int i = 0; i < boneMatrices.Length; i++)
        destination.Add(SkinningMath.Compose(boneMatrices[i], inverseBind[i]));
    return (start, boneMatrices.Length);
}
```

Append the tagged part only after this helper returns. The helper does not pad to 128 because the renderer store owns its slot padding.

- [ ] **Step 6: Replace the rigid-only record with an explicit tagged record.**

```csharp
enum MeshOutlinePartKind
{
    Rigid,
    Skinned,
}

readonly record struct MeshOutlinePart(
    MeshOutlinePartKind Kind,
    MeshHandle Mesh,
    SkinnedMeshHandle SkinnedMesh,
    Matrix4x4 World,
    float Dissolve,
    bool DissolveComplement,
    int BoneStart,
    int BoneCount)
{
    public static MeshOutlinePart Rigid(
        MeshHandle mesh, Matrix4x4 world, float dissolve, bool complement) =>
        new(MeshOutlinePartKind.Rigid, mesh, default, world, dissolve, complement, 0, 0);

    public static MeshOutlinePart Skinned(
        SkinnedMeshHandle mesh, Matrix4x4 world, float dissolve, bool complement,
        int boneStart, int boneCount) =>
        new(MeshOutlinePartKind.Skinned, default, mesh, world, dissolve, complement,
            boneStart, boneCount);
}
```

Keep `MeshOutlinePartCount` as the total across both tags. Add internal `OutlinePoseMatrixCount` and `OutlinePoseMatrixAt(int)` only as exact test observables. `BeginMeshOutlineFrame` clears `_outlineBoneMatrices` beside groups and the part count.

- [ ] **Step 7: Run the new and existing outline API tests green.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedTargetOutlineApiTests|FullyQualifiedName~TargetOutlineApiTests"
```

Expected: exit `0`, with no skipped tests.

- [ ] **Step 8: Commit the independently reviewable submission contract.**

```bash
git add KhaozEngine.Render3D/Scene3D.SkinnedTargetOutlines.cs \
  KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedTargetOutlineApiTests.cs \
  KhaozEngine.Render.Tests/Render3D/TargetOutlineApiTests.cs
git commit -m "render3d(outline): own skinned pose snapshots"
```

---

### Task 2: Open the Tile-World Skinned Outline Door

**Files:**

- Modify: `KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs`
- Modify: `KhaozEngine.Render.Tests/TileWorld/TileWorldSceneSkinnedTests.cs`

**Interfaces:**

- Consumes: Task 1 grouped `Scene3D` methods
- Produces: compatible default methods on `ITileWorldScene` and exact forwarding on `Scene3DTileWorldScene`

- [ ] **Step 1: Add failing compatibility and forwarding tests.**

Extend `Legacy_scene_refuses_every_skinned_operation` only for the existing load and colour methods. Add a separate outline compatibility test because the outline defaults are intentionally no-op rather than refusal:

```csharp
[Fact]
public void Legacy_scene_ignores_skinned_outline_methods()
{
    ITileWorldScene scene = new LegacyTileWorldScene();
    var group = new MeshOutlineGroup(7);
    var mesh = new SkinnedMeshHandle(3, 2);
    Matrix4x4[] pose = { Matrix4x4.Identity };

    scene.DrawSkinnedOutline(group, mesh, pose, Matrix4x4.Identity);
    scene.DrawSkinnedOutlineDissolved(
        group, mesh, pose, Matrix4x4.Identity, 0.65f, dissolveComplement: true);
}
```

Add `Scene3D_adapter_forwards_skinned_outline_and_dissolve` that loads a tube, begins one group, calls both adapter methods with distinct worlds and poses, then asserts one group, two parts, and two copied pose slices on the wrapped scene.

- [ ] **Step 2: Run the tile-world skinned tests and confirm the missing members fail compilation.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~TileWorldSceneSkinnedTests"
```

Expected: exit `1`, with missing `ITileWorldScene.DrawSkinnedOutline` and `DrawSkinnedOutlineDissolved` members.

- [ ] **Step 3: Add the compatibility defaults exactly as designed.**

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

These defaults keep older headless implementations compiling and ignore an outline they cannot render.

- [ ] **Step 4: Forward both methods directly from `Scene3DTileWorldScene`.**

```csharp
public void DrawSkinnedOutline(
    MeshOutlineGroup group, SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world) =>
    _scene.DrawSkinnedOutline(group, mesh, boneMatrices, world);

public void DrawSkinnedOutlineDissolved(
    MeshOutlineGroup group, SkinnedMeshHandle mesh,
    ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
    float dissolve, bool dissolveComplement) =>
    _scene.DrawSkinnedOutlineDissolved(
        group, mesh, boneMatrices, world, dissolve, dissolveComplement);
```

- [ ] **Step 5: Run the tile-world skinned and existing outline LOD tests green.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~TileWorldSceneSkinnedTests|FullyQualifiedName~TileWorldTargetOutlineLodTests"
```

Expected: exit `0`, with no skipped tests.

- [ ] **Step 6: Commit the tile-world seam.**

```bash
git add KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs \
  KhaozEngine.Render.Tests/TileWorld/TileWorldSceneSkinnedTests.cs
git commit -m "tileworld(outline): forward skinned groups"
```

---

### Task 3: Retain Skinned Cutoff Materials Transactionally

**Files:**

- Create: `KhaozEngine.Render3D/Scene3D.SkinnedMeshLoading.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.Unload.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.ShadowLayoutReplacement.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedMeshMaterialLifetimeTests.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/Scene3DUnloadRetireTests.cs`

**Interfaces:**

- Consumes: `_model.CreateMaterialSet`, `_model.CreateSkinnedMaterialSet`, `_targetOutlines.CreateMaterialSet`, `SurfaceMaps.AlphaCutoff`, `GpuRetireQueue`
- Produces: `SkinnedMeshEntry.AlphaCutoff`, `SkinnedMeshEntry.OutlineMaterialSet`, a transactional `LoadSkinnedInternal`, and correct unload, disposal, and shadow-layout carryover
- Produces for Tasks 4 through 6: live per-mesh cutoff and outline material used by ordinary colour payloads and both masks

- [ ] **Step 1: Add deterministic buffer failure injection to the fake factory.**

Add an absolute one-based `ThrowOnBufferCreate` property beside the existing texture and resource-set controls:

```csharp
internal int ThrowOnBufferCreate { get; set; }

public IGpuBuffer CreateBuffer(in GpuBufferDescription description)
{
    if (ThrowOnBufferCreate == Buffers.Count + 1)
        throw new InvalidOperationException("planned buffer creation failure");
    var buffer = new FakeBuffer(description.SizeInBytes);
    Buffers.Add(buffer);
    return buffer;
}
```

- [ ] **Step 2: Write failing load, rollback, retirement, disposal, and shadow-layout tests.**

`SkinnedMeshMaterialLifetimeTests` must cover these exact cases:

- `SurfaceMaps.AlphaCutoff` is retained on a live entry
- a valid texture overload creates an outline material even with cutoff zero
- a `SurfaceMaps` load with an albedo creates CPU, GPU, and outline material sets
- a `SurfaceMaps` load without albedo creates no outline set and relies on the renderer white default
- a failure creating the GPU material disposes vertex buffer, index buffer, and CPU material
- a failure creating the outline material disposes both buffers and both ordinary material sets
- no failed load increments `LiveSkinnedMeshCount`
- retry after clearing the failure returns a valid handle
- shadow-layout replacement changes only the two ordinary shadow-sampling sets and preserves the exact outline set object and cutoff
- scene disposal disposes the outline set

Use resource counts before the load to isolate resources created by the call. The core rollback row should read:

```csharp
[Fact]
public void Load_is_transactional_through_outline_material_creation()
{
    using Harness h = Harness.Create();
    Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
    int buffersBefore = h.Factory.Buffers.Count;
    int setsBefore = h.Factory.ResourceSets.Count;
    h.Factory.ThrowOnResourceSetCreate = setsBefore + 3;

    Assert.Throws<InvalidOperationException>(() =>
        h.Scene.LoadSkinnedMesh(Tube(), new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.5f)));

    Assert.Equal(0, h.Scene.LiveSkinnedMeshCount);
    Assert.All(h.Factory.Buffers.Skip(buffersBefore), b => Assert.True(b.Disposed));
    Assert.All(h.Factory.ResourceSets.Skip(setsBefore), s => Assert.True(s.Disposed));

    h.Factory.ThrowOnResourceSetCreate = 0;
    Assert.True(h.Scene.LoadSkinnedMesh(
        Tube(), new Scene3D.SurfaceMaps(albedo, alphaCutoff: 0.5f)).Generation > 0);
}
```

Update the existing unload-retire test to expect three resource sets and to assert the outline set remains alive until the same retirement boundary as the two ordinary sets.

- [ ] **Step 3: Run the lifetime tests and confirm the current non-transactional load fails.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedMeshMaterialLifetimeTests|FullyQualifiedName~Scene3DUnloadRetireTests.UnloadSkinnedMesh"
```

Expected: exit `1`. The first failures should show two rather than three material sets, leaked buffers or sets after the injected third-set failure, and missing cutoff state.

- [ ] **Step 4: Move the cohesive skinned load block and entry type into the new partial.**

Move the existing `LoadSkinnedMesh` overloads, `LoadSkinnedInternal`, and nested `SkinnedMeshEntry` from `Scene3D.cs` into `Scene3D.SkinnedMeshLoading.cs`. This is a responsibility split, not an arbitrary line split. Keep public signatures stable.

Change the internal call shape to:

```csharp
SkinnedMeshHandle LoadSkinnedInternal(
    SkinnedGltfMesh mesh,
    IGpuTexture? albedo,
    IGpuTexture? normal,
    IGpuTexture? roughness,
    float alphaCutoff)
```

The untextured and texture overloads pass `0f`. The `SurfaceMaps` overload passes `maps.AlphaCutoff`.

- [ ] **Step 5: Build every resource before allocating the handle slot.**

Use nullable locals and one `try` block in this exact order:

1. vertex buffer
2. index buffer
3. ordinary CPU material set when any ordinary map exists
4. ordinary GPU material set when any ordinary map exists
5. albedo-only outline material set when albedo exists
6. bounds and entry value
7. `_skinnedSlots.Alloc`
8. list commit and handle return

The catch disposes all five nullable resources and rethrows. Do not allocate a slot before all resource creation and CPU state construction succeeds.

```csharp
IGpuResourceSet? outlineMaterial = albedo is null
    ? null
    : _targetOutlines.CreateMaterialSet(albedo);

var entry = new SkinnedMeshEntry(
    vertexBuffer, indexBuffer, mesh.Indices32.Length, mesh.IndexFormat,
    material, skinnedMaterial, outlineMaterial, alphaCutoff,
    mesh.InverseBind, in bounds);
```

Add `LiveSkinnedMeshCount` as an internal diagnostic beside `LiveMeshCount`.

- [ ] **Step 6: Retire and dispose the outline set everywhere the entry dies.**

`UnloadSkinnedMesh` retires vertex buffer, index buffer, CPU material, GPU material, and outline material in one retirement batch. `Scene3D.Dispose` disposes all five live resources. `CommitMaterialSets` reconstructs an entry with the replacement CPU and GPU sets while carrying the original outline set and cutoff unchanged. `CollectLiveMaterialSets` does not include the outline set because it contains no shadow binding.

- [ ] **Step 7: Run lifetime, unload, and shadow-layout tests green.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedMeshMaterialLifetimeTests|FullyQualifiedName~Scene3DUnloadRetireTests|FullyQualifiedName~ShadowReconfigure"
```

Expected: exit `0`, with no skipped headless tests.

- [ ] **Step 8: Commit the material lifetime transaction.**

```bash
git add KhaozEngine.Render3D/Scene3D.SkinnedMeshLoading.cs \
  KhaozEngine.Render3D/Scene3D.cs \
  KhaozEngine.Render3D/Scene3D.Unload.cs \
  KhaozEngine.Render3D/Scene3D.ShadowLayoutReplacement.cs \
  KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedMeshMaterialLifetimeTests.cs \
  KhaozEngine.Render.Tests/Render3D/Scene3DUnloadRetireTests.cs
git commit -m "render3d(skinned): retain outline cutout materials"
```

---

### Task 4: Separate Ordinary Skinned Alpha Cutoff from Dissolve

**Files:**

- Modify: `KhaozEngine.Render3D/Scene3D.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.GpuSkinning.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedColorCutoutContractTests.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs`
- Modify: `KhaozEngine.Render.Tests/TileWorld/TileWorldSceneSkinnedTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt`

**Interfaces:**

- Consumes: Task 3 `SkinnedMeshEntry.AlphaCutoff`, existing `InstanceData.Dissolve`, fixed-size `PackSkinnedMainSlot`
- Produces: cutoff in `SpecParams.z`, CPU dissolve in `InstanceData.Dissolve`, GPU dissolve in `P[3].zw`, and contiguous `vDissolve` at location 9
- Produces for Task 7: ordinary colour coverage that can be compared directly with both outline masks

- [ ] **Step 1: Add failing payload tests for all four colour combinations.**

Build one mapped tube with `alphaCutoff: 0.5f`. Render through `FakeGpuDevice` and a payload-capturing `RecordingGpuCommandList` for these rows:

| Skinning path | Colour path | Expected cutoff | Expected dissolve |
|---|---|---:|---:|
| CPU | plain | `SpecParams.Z == 0.5f` | `InstanceData.Dissolve == Vector2.Zero` |
| CPU | dissolved | `SpecParams.Z == 0.5f` | `InstanceData.Dissolve == new Vector2(0.65f, 0.14f)` |
| GPU | plain | `P[2].z == 0.5f` | `P[3].zw == Vector2.Zero` |
| GPU | dissolved | `P[2].z == 0.5f` | `P[3].zw == new Vector2(0.65f, 0.14f)` |

Assert `ModelRenderer.SkinnedHeaderBytes == 128` and `SkinnedMainSlotBytes == 256` in the same test class. Select the GPU header upload by its destination buffer size and parse the first two `Matrix4x4` values from the captured bytes.

- [ ] **Step 2: Add failing shader source contracts.**

Pin all of these exact source facts:

- `ModelDissolveFrag` declares `layout(location=9) in vec2 vDissolve;`
- `ModelDissolveFrag` samples `vec4 texRgba` and alpha-tests with `vSpecParams.z`
- `ModelDissolveFrag` reads threshold and edge width from `vDissolve`
- `SkinnedModelVert` writes `vDissolve = P[3].zw;` at location 9
- `SkinnedModelDissolveFrag` declares location 9, alpha-tests with `vSpecParams.z`, and dissolves with `vDissolve`
- in both dissolve fragments the alpha test appears before the dissolve discard
- the live varying locations are contiguous from 0 through 9

```csharp
[Fact]
public void Dissolve_fragments_keep_cutoff_and_dissolve_in_separate_varyings()
{
    Assert.Contains("layout(location=9) in vec2 vDissolve;", ShaderSources.ModelDissolveFrag);
    Assert.Contains("layout(location=9) out vec2 vDissolve;", ShaderSources.SkinnedModelVert);
    Assert.Contains("vDissolve = P[3].zw;", ShaderSources.SkinnedModelVert);
    Assert.Contains("layout(location=9) in vec2 vDissolve;", ShaderSources.SkinnedModelDissolveFrag);
    Assert.Contains("texRgba.a < vSpecParams.z", ShaderSources.ModelDissolveFrag);
    Assert.Contains("texRgba.a < vSpecParams.z", ShaderSources.SkinnedModelDissolveFrag);
}
```

- [ ] **Step 3: Run the new contracts and confirm current field overloading fails.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedColorCutoutContractTests|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~TileWorldSceneSkinnedTests"
```

Expected: exit `1`. Current dissolved CPU and GPU payloads place `0.65` in `SpecParams.Z`, and both dissolve fragments lack the new cutoff test and dedicated dissolve input.

- [ ] **Step 4: Pack CPU skinned instance data with stable field ownership.**

For plain and dissolved CPU records, construct:

```csharp
Vector4 specParams = new(
    item.Material.Specular,
    item.Material.Shininess,
    entry.AlphaCutoff,
    0f);
Vector2 dissolveParams = item.Dissolving
    ? new Vector2(item.DissolveThreshold, item.DissolveEdgeWidth)
    : Vector2.Zero;
```

Assign `SpecParams = specParams` and `Dissolve = dissolveParams`. Keep the dissolve edge colour in `Emissive`. Update the `InstanceData` comment so it no longer claims `ModelDissolveFrag` overloads `SpecParams.zw`.

- [ ] **Step 5: Carry the same values in the fixed GPU header.**

Add `Vector2 Dissolve` to `GpuSkinnedDraw`. Change `PackSkinnedMainSlot` to accept `Vector2 dissolve` before the optional dynamic flag and pack the final matrix row as:

```csharp
_skinnedHeaderScratch[1] = new Matrix4x4(
    tint.X, tint.Y, tint.Z, tint.W,
    emissive.X, emissive.Y, emissive.Z, emissive.W,
    specParams.X, specParams.Y, specParams.Z, specParams.W,
    isDynamic, 0f, dissolve.X, dissolve.Y);
```

Pass the draw's dedicated dissolve vector from `DrawGpuSkinnedMain`. Do not change either header size constant.

- [ ] **Step 6: Rewrite the two dissolve fragments to alpha-test first and dissolve second.**

Both fragments sample `vec4 texRgba`, run this test first, then evaluate noise:

```glsl
if (vSpecParams.z > 0.0 && texRgba.a < vSpecParams.z) discard;
float threshold = clamp(vDissolve.x, 0.0, 1.0);
float edgeW = max(vDissolve.y, 1e-3);
```

Use `texRgba.rgb` for lighting. In `SkinnedModelVert`, declare and write `vDissolve` at location 9. Keep `vDynamic` at location 8 and `P[3].x` unchanged.

- [ ] **Step 7: Update the tile-world dissolve expectation.**

The existing adapter test must now assert `packed.SpecParams.Z == 0f` for its unmasked mesh and `packed.Dissolve == new Vector2(0.65f, 0.14f)`. This is an intentional contract correction, not a relaxed assertion.

- [ ] **Step 8: Regenerate the corpus and three pinned shader hashes for the changed skinned programs.**

```bash
KE_WRITE_SHADER_CORPUS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~ShaderCorpusTests"
KE_UPDATE_HLSL_HASHES=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~D3D11HlslByteEqualityTests.EveryShippedProgramsEmittedHlsl_MatchesItsPinnedHash"
KE_UPDATE_MSL_HASHES=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~MetalMslByteEqualityTests.EveryShippedProgramsEmittedMsl_MatchesItsPinnedHash"
KE_UPDATE_SPIRV_HASHES=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~VulkanSpirvByteEqualityTests.EveryShippedProgramsSpirv_MatchesItsPinnedHash"
```

Expected: every command exits `0`. Only the rows derived from `ModelDissolve`, `SkinnedModel`, and
`SkinnedModelDissolve` move. Inspect all four diffs and confirm the catalog key set is unchanged in this task.

- [ ] **Step 9: Run colour payload, layout, tile-world, and shader source tests green.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedColorCutoutContractTests|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~TileWorldSceneSkinnedTests|FullyQualifiedName~ShaderSourceValidationTests|FullyQualifiedName~ShaderCorpusTests|FullyQualifiedName~D3D11HlslByteEqualityTests|FullyQualifiedName~MetalMslByteEqualityTests|FullyQualifiedName~VulkanSpirvByteEqualityTests"
```

Expected: exit `0`, with no skipped headless tests and clean corpus and hash comparisons.

- [ ] **Step 10: Commit the ordinary colour parity contract.**

```bash
git add KhaozEngine.Render3D/Scene3D.cs \
  KhaozEngine.Render3D/Scene3D.GpuSkinning.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs \
  KhaozEngine.Render3D/Internal/ShaderSources.Model.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedColorCutoutContractTests.cs \
  KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs \
  KhaozEngine.Render.Tests/TileWorld/TileWorldSceneSkinnedTests.cs \
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt \
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt
git commit -m "render3d(skinned): separate cutoff from dissolve"
```

---

### Task 5: Render GPU-Skinned Parts in the Existing Group Union

**Files:**

- Modify: `KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs`
- Modify: `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.cs`
- Create: `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.Skinned.cs`
- Create: `KhaozEngine.Render3D/Rendering/TargetOutlineSkinningStore.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.TargetOutline.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedTargetOutlineRendererTests.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/TargetOutlineRendererTests.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanShaderBindingTableTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanDescriptorLimitTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanLayoutCompatibilityTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanSpirvByteEqualityTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt`

**Interfaces:**

- Consumes: Task 1 tagged parts and outline pose slices, Task 3 outline material and cutoff, existing full and visible mask fragments
- Produces: `TargetOutlineRenderer.EnqueueRigid`, `EnqueueSkinnedGpu`, ordered tagged commands, skinned full and visible pipelines, and a transactional outline-owned palette store
- Produces for Task 6: the common queue tag and `TargetOutlineSkinningStore` CPU buffer surface

- [ ] **Step 1: Add failing headless command tests for GPU-skinned outlines.**

Use a real `Scene3D` over `FakeGpuDevice`, a target framebuffer, and `RecordingGpuCommandList`. Disable starfield and shadows to keep unrelated commands small. Add these exact tests:

- `Gpu_outline_records_with_zero_ordinary_skinned_instances`
- `Gpu_palette_slots_advance_by_TargetOutlineSkinningStore_PaletteSlotBytes`
- `Mixed_rigid_and_gpu_skinned_parts_keep_submission_order_in_both_masks`
- `Mixed_group_records_one_full_pass_one_visible_pass_and_one_composite`
- `Fully_invisible_endpoints_record_no_outline_work`
- `Unloaded_after_submission_records_no_mask_draw`
- `Scene_depth_group_with_no_live_parts_records_no composite`
- `Rigid_only_group_allocates_and_uploads_no_skinning_buffer`

The no-main-draw test queues only `DrawSkinnedOutline`, asserts `Scene3D.SkinnedInstanceCount == 0`, renders, then asserts two indexed mask draws and one fullscreen composite for the default scene-depth group.

The endpoint test submits a non-complement part at `1f` and a complement part at `0f`. Compare its command tally with an otherwise empty frame. It must add zero updates, indexed draws, clears, framebuffer binds, and composites.

- [ ] **Step 2: Add failing shader contracts for the skinned mask vertex.**

Pin this exact public shader shape in `UboLayoutTests`:

```csharp
[Fact]
public void Skinned_target_outline_vertex_has_contiguous_inputs_outputs_and_palette()
{
    string source = ShaderSources.TargetOutlineSkinnedMaskVert;
    Assert.Contains("layout(set=2, binding=0) uniform Palette", source);
    Assert.Contains("mat4 bones[128];", source);
    for (int location = 0; location <= 6; location++)
        Assert.Contains($"layout(location={location}) in", source);
    Assert.Contains("layout(location=0) out vec2 vUv;", source);
    Assert.Contains("layout(location=1) out vec3 vWorldPos;", source);
    Assert.Contains("wsum < 1e-8", source);
    Assert.Contains("vWorldPos = world.xyz + RenderOrigin.xyz;", source);
}
```

Also assert the existing full and visible fragments are reused unchanged, both alpha-test before coverage, only the visible fragment evaluates dissolve noise, and both mask pipelines use `GpuFaceCull.None`.

- [ ] **Step 3: Run the renderer and shader tests and confirm missing GPU outline behavior.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedTargetOutlineRendererTests|FullyQualifiedName~TargetOutlineRendererTests|FullyQualifiedName~UboLayoutTests"
```

Expected: exit `1`, with missing `TargetOutlineSkinnedMaskVert`, no GPU palette upload, and no skinned mask draw.

- [ ] **Step 4: Introduce a transactional outline skinning store.**

`TargetOutlineSkinningStore` owns no group policy. It owns only buffers, layouts, CPU images, and their lifetime.

```csharp
internal sealed class TargetOutlineSkinningStore : IDisposable
{
    internal static readonly uint PaletteSlotBytes =
        (uint)SkinningMath.MaxBonesPerDraw * 64;

    internal IGpuResourceLayout PaletteLayout { get; }
    internal IGpuResourceSet PaletteSet { get; }

    internal void EnsurePaletteCapacity(int slotCount);
    internal void PackPalette(int slot, ReadOnlySpan<Matrix4x4> bones);
    internal void UploadPalette(IGpuCommandList commands);

    internal IGpuBuffer EnsureCpuVertexCapacity(int vertexCount);
    internal void UploadCpuVertices(
        IGpuCommandList commands, ReadOnlySpan<ModelVertex> vertices);
}
```

`EnsurePaletteCapacity` uses grow, create, then replace. Allocate the fresh byte image, buffer, and one-slot dynamic resource set first. If resource-set creation throws, dispose the fresh buffer and leave the old buffer, set, capacity, and image unchanged. After successful creation, add the old set and buffer to the store's conservative retirement list, then commit all fresh fields.

`PackPalette` clears the complete 128-matrix slot to identity before copying the part's actual composed matrices. This prevents a shorter pose from reading stale matrices if malformed vertex indices ever escape load validation.

Do not allocate the CPU vertex buffer in this task. The method surface exists for Task 6, while its nullable backing buffer remains absent until called.

- [ ] **Step 5: Add the skinned mask vertex shader.**

Use the exact `SkinnedVertex` input layout at locations 0 through 6. Blend the palette with the same raw weights and zero-sum identity fallback as `SkinnedModelVert`. Tangent is not needed by either mask fragment, but it must remain a declared contiguous input and feed a `1e-30` sink with normal, colour, bone indices, and tangent so FXC retains the complete input signature.

```glsl
layout(set=0, binding=0) uniform Draw {
    mat4 ViewProj; mat4 World; vec4 Params; vec4 RenderOrigin;
};
layout(set=2, binding=0) uniform Palette { mat4 bones[128]; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 BoneIndices;
layout(location=5) in vec4 BoneWeights;
layout(location=6) in vec4 Tangent;
layout(location=0) out vec2 vUv;
layout(location=1) out vec3 vWorldPos;
```

After skinning, compute `world = World * localPos`, add the attribute sink to `world.x`, emit `vUv`, absolute `vWorldPos`, and `ViewProj * world` exactly like the rigid mask vertex.

- [ ] **Step 6: Make `TargetOutlineRenderer` partial and give its queue an explicit geometry tag.**

Rename the existing `Enqueue` method to `EnqueueRigid`. Keep its arguments stable. Replace `QueuedDraw` with:

```csharp
enum QueuedGeometry
{
    Rigid,
    GpuSkinned,
    CpuSkinned,
}

readonly record struct QueuedDraw(
    QueuedGeometry Geometry,
    IGpuBuffer VertexBuffer,
    IGpuBuffer IndexBuffer,
    int IndexCount,
    GpuIndexFormat IndexFormat,
    IGpuResourceSet MaterialSet,
    int DrawIndex,
    int PaletteSlot,
    int BaseVertex,
    int PoseStart,
    int PoseCount);
```

Rigid commands use palette slot `-1`, base vertex `0`, and an empty pose slice. Initialize the skinned full and
visible shader sets, their layouts, and `TargetOutlineSkinningStore` in the renderer constructor. The two skinned
mask pipelines need the actual full and visible framebuffer outputs, so create them beside the rigid pipelines in
`CreateTargets` and dispose them in `DisposeTargets`. A resize or resource-generation rebuild recreates both
families against the new outputs.

- [ ] **Step 7: Add `EnqueueSkinnedGpu` without consulting any ordinary draw queue.**

```csharp
public void EnqueueSkinnedGpu(
    IGpuBuffer restVertexBuffer,
    IGpuBuffer indexBuffer,
    int indexCount,
    GpuIndexFormat indexFormat,
    IGpuResourceSet? materialSet,
    ReadOnlySpan<Matrix4x4> composedBones,
    int drawIndex,
    Matrix4x4 world,
    float alphaCutoff,
    float dissolve,
    bool dissolveComplement,
    Vector3 renderOrigin)
```

Allocate one logical palette slot per queued GPU-skinned outline part. Copy the supplied composed matrices into a group-local `_gpuPaletteBones` list and record its start and count on the command. Write the same 160-byte draw payload as a rigid command and append one `GpuSkinned` command in group order. Do not allocate or replace a GPU buffer during enqueue. The method never reads ordinary scene instance count, visibility, palette slot, or culling state.

- [ ] **Step 8: Bind the correct pipeline and palette while preserving one ordered queue.**

At the start of `Render`, ensure palette capacity for the logical slot count, pack each recorded pose slice, and upload the palette once when the group has a GPU-skinned part. Complete all of this before binding or clearing a mask target. In `DrawQueue`, choose rigid or skinned full and visible pipelines per command. A GPU-skinned command binds draw set 0, material set 1, palette set 2 at `PaletteSlotBytes * slot`, then the rest vertex and index buffers. Rigid commands retain their current bindings. Do not split the queue or composite between geometry kinds.

`CreateTargets` builds both skinned mask pipelines with the exact `SkinnedVertex` layout and the corresponding
live full and visible framebuffer outputs, fragments, material layout, rasterizer, depth, and blend states.

- [ ] **Step 9: Route tagged `Scene3D` parts to the renderer.**

In `DrawTargetOutlines`, preserve endpoint filtering and group order. For a rigid tag, retain current handle resolution and call `EnqueueRigid`. For a skinned tag:

1. validate the skinned slot generation again
2. skip a null unloaded entry
3. slice `_outlineBoneMatrices` with `BoneStart` and `BoneCount`
4. call `EnqueueSkinnedGpu` with `entry.OutlineMaterialSet` and `entry.AlphaCutoff` when `UseGpuSkinning` is true

Do not read `_skinnedInstances` or `_boneMatrices`. Count mask draws only after a live part is enqueued. Add the composite to frame stats only when the renderer reports a nonempty queue.

- [ ] **Step 10: Enumerate all five outline shader pairs and regenerate the corpus.**

Add stable names `TargetOutlineRigidFull`, `TargetOutlineRigidVisible`, and `TargetOutlineComposite` for the three existing renderer programs, plus `TargetOutlineSkinnedFull` and `TargetOutlineSkinnedVisible` for the new ones. Update the documented graphics-pair count and the exact count assertions in `D3D11HlslByteEqualityTests` and `VulkanShaderBindingTableTests` from 43 to 48. Add five real outline pipeline rows to `VulkanDescriptorLimitTests.ShippedPipelines` and map all five program names to them. Update the descriptor and layout compatibility counts from 37 to 42, their pair count to `42 * 42`, and the SPIR-V emitted-stage count from 94 to 104. Ensure `D3D11FxcValidationTests` compiles the emitted HLSL for all five pairs and checks the full seven-element skinned vertex input signature. Do not add test-only shader variants.

```bash
KE_WRITE_SHADER_CORPUS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~ShaderCorpusTests"
KE_UPDATE_HLSL_HASHES=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~D3D11HlslByteEqualityTests.EveryShippedProgramsEmittedHlsl_MatchesItsPinnedHash"
KE_UPDATE_MSL_HASHES=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~MetalMslByteEqualityTests.EveryShippedProgramsEmittedMsl_MatchesItsPinnedHash"
KE_UPDATE_SPIRV_HASHES=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~VulkanSpirvByteEqualityTests.EveryShippedProgramsSpirv_MatchesItsPinnedHash"
```

Expected: every command exits `0`, five new program keys and their generated corpus and hash rows, plus no
orphaned key. Inspect all four generated diffs before continuing.

- [ ] **Step 11: Run GPU outline headless contracts and shader validation green.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedTargetOutlineRendererTests|FullyQualifiedName~TargetOutlineRendererTests|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~ShaderCorpusTests|FullyQualifiedName~D3D11FxcValidationTests|FullyQualifiedName~D3D11HlslByteEqualityTests|FullyQualifiedName~MetalMslByteEqualityTests|FullyQualifiedName~VulkanSpirvByteEqualityTests|FullyQualifiedName~VulkanShaderBindingTableTests|FullyQualifiedName~VulkanDescriptorLimitTests|FullyQualifiedName~VulkanLayoutCompatibilityTests|FullyQualifiedName~D3D11RegisterNumberingTests"
```

Expected: exit `0`. On a non-Windows host, the real FXC row may be skipped by its existing platform gate. Its source and corpus siblings must pass headlessly.

- [ ] **Step 12: Commit the GPU-skinned group path.**

```bash
git add KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs \
  KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.cs \
  KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.Skinned.cs \
  KhaozEngine.Render3D/Rendering/TargetOutlineSkinningStore.cs \
  KhaozEngine.Render3D/Internal/ShaderSources.TargetOutline.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedTargetOutlineRendererTests.cs \
  KhaozEngine.Render.Tests/Render3D/TargetOutlineRendererTests.cs \
  KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs \
  KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanShaderBindingTableTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanDescriptorLimitTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanLayoutCompatibilityTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanSpirvByteEqualityTests.cs \
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt \
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt
git commit -m "render3d(outline): render gpu skinned groups"
```

---

### Task 6: Add the CPU Fallback and Prove Resource Rollback

**Files:**

- Modify: `KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs`
- Modify: `KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.Skinned.cs`
- Modify: `KhaozEngine.Render3D/Rendering/TargetOutlineSkinningStore.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/RecordingGpuCommandList.Draws.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/SkinnedTargetOutlineRendererTests.cs`
- Modify: `KhaozEngine.Render.Tests/Render3D/TargetOutlineRendererTests.cs`

**Interfaces:**

- Consumes: Task 5 tagged queue and store, cached source `SkinnedVertex[]`, copied composed pose, `SkinningMath.SkinVertex`, existing rigid mask pipelines
- Produces: `EnqueueSkinnedCpu`, one group-local deformed `ModelVertex` upload, per-draw base vertices, transactional CPU growth, and render-abort behavior

- [ ] **Step 1: Extend the command recorder to retain indexed base vertices.**

Change the test-only record and note method without changing command forwarding:

```csharp
internal readonly record struct IndexedDraw(
    uint IndexCount,
    int VertexOffset,
    IGpuPipeline? Pipeline,
    IGpuBuffer? VertexBuffer,
    IGpuBuffer? IndexBuffer);
```

Pass `vertexOffset` from `DrawIndexed` into `NoteIndexedDraw`. Existing assertions that read the other members remain source compatible after their constructor sites are updated.

- [ ] **Step 2: Add failing CPU path, offset, independence, and rollback tests.**

Add these exact tests to `SkinnedTargetOutlineRendererTests`:

- `Cpu_outline_records_with_zero_ordinary_skinned_instances`
- `Cpu_parts_share_one_vertex_upload_and_advance_base_vertex_offsets`
- `Cpu_and_gpu_paths_read_the_same_outline_pose_snapshot`
- `Outline_pose_is_separate_from_the_ordinary_pose_for_the_same_handle`
- `Gpu_palette_growth_failure_keeps_the_old_capacity_and_aborts_before_composite`
- `Cpu_vertex_growth_failure_keeps_the_old_capacity_and_aborts_before_composite`
- `Grown_resources_stay_alive_until_renderer_disposal`

For two identical tubes with `vertexCount = tube.Vertices.Length`, the first full-mask CPU draw must carry base vertex `0`, the second `vertexCount`. The visible pass repeats the same two offsets. Assert exactly one CPU vertex upload for the group.

For each failure test:

1. render one small group successfully to establish old capacity
2. record the old buffer and set objects
3. inject the next buffer or set creation failure
4. submit enough skinned parts to force growth
5. assert the render call throws
6. assert the command list recorded no mask clear, mask draw, or composite for the failed group
7. assert the old objects are not disposed
8. clear the failure and render a group that fits the old capacity successfully
9. force growth again and assert successful replacement
10. assert replaced objects remain alive until renderer disposal, then are disposed

- [ ] **Step 3: Run the renderer tests and confirm the CPU route is missing.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedTargetOutlineRendererTests|FullyQualifiedName~TargetOutlineRendererTests"
```

Expected: exit `1`, with no CPU mask draw, no base vertex progression, and no CPU growth rollback surface.

- [ ] **Step 4: Add `EnqueueSkinnedCpu` and an outline-owned deformed vertex list.**

```csharp
public void EnqueueSkinnedCpu(
    ReadOnlySpan<SkinnedVertex> sourceVertices,
    IGpuBuffer indexBuffer,
    int indexCount,
    GpuIndexFormat indexFormat,
    IGpuResourceSet? materialSet,
    ReadOnlySpan<Matrix4x4> composedBones,
    int drawIndex,
    Matrix4x4 world,
    float alphaCutoff,
    float dissolve,
    bool dissolveComplement,
    Vector3 renderOrigin)
```

The method records `baseVertex = _cpuVertices.Count`, appends `SkinningMath.SkinVertex(source, composedBones)` for each source vertex, writes the common draw payload, and appends a `CpuSkinned` queue command. It does not upload immediately.

`BeginGroup` clears the CPU vertex list, `_gpuPaletteBones`, queue, GPU palette part count, and CPU and GPU part flags together.

- [ ] **Step 5: Make CPU stream growth transactional and upload once.**

`TargetOutlineSkinningStore.EnsureCpuVertexCapacity` creates the new vertex buffer before replacing the current one. On failure, its capacity and current buffer remain unchanged. On success, retire the old buffer conservatively and commit the new buffer and capacity.

At the start of `Render`, before `BindTargets` or any mask command:

1. when the GPU-skinned command count is positive, ensure that many palette slots
2. when the CPU vertex list is nonempty, ensure capacity for its exact vertex count
3. when the GPU-skinned command count is positive, pack every recorded pose slice and upload the palette once
4. when the CPU vertex list is nonempty, upload it once

Only after all four steps succeed may the renderer allocate or bind mask targets and clear them. This ordering proves a growth failure cannot composite a partial group.

- [ ] **Step 6: Draw CPU-skinned commands through the existing rigid mask pipelines.**

A `CpuSkinned` command binds the CPU scratch vertex buffer and the original mesh index buffer, then calls:

```csharp
commands.DrawIndexed(
    (uint)draw.IndexCount,
    instanceCount: 1,
    indexStart: 0,
    vertexOffset: draw.BaseVertex,
    instanceStart: 0);
```

It uses the rigid full or visible pipeline because the scratch vertices are `ModelVertex`. It retains queue order with neighboring rigid and GPU-skinned commands.

- [ ] **Step 7: Select CPU or GPU from `UseGpuSkinning` only when the frame renders.**

For a live skinned part in `DrawTargetOutlines`, slice the copied outline pose once. If `UseGpuSkinning` is false, resolve `_skinnedCpuVerts[handle.Index]`, skip a null source, and call `EnqueueSkinnedCpu` with `entry.OutlineMaterialSet` and `entry.AlphaCutoff`. A submission made while one path was selected must render through the other path if the property changes before `RenderInternal`, because selection belongs to render time.

- [ ] **Step 8: Run all target outline headless contracts green.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~TargetOutline|FullyQualifiedName~SkinnedQueue|FullyQualifiedName~TileWorldSceneSkinned"
```

Expected: exit `0`, with no skipped headless tests. This is the design's focused device-free implementation command.

- [ ] **Step 9: Commit the CPU fallback and resource guarantees.**

```bash
git add KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs \
  KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.Skinned.cs \
  KhaozEngine.Render3D/Rendering/TargetOutlineSkinningStore.cs \
  KhaozEngine.Render.Tests/Gpu/RecordingGpuCommandList.Draws.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedTargetOutlineRendererTests.cs \
  KhaozEngine.Render.Tests/Render3D/TargetOutlineRendererTests.cs
git commit -m "render3d(outline): add cpu skinned fallback"
```

---

### Task 7: Prove Bent Poses, Mixed Silhouettes, Cutout, Dissolve, and All Backend Goldens

**Files:**

- Create: `KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlineScene.cs`
- Create: `KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlineGoldenTests.cs`
- Create: `KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlinePoseGoldenTests.cs`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_cpu.metal-native.txt`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_cpu.direct3d11-native.txt`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_cpu.vulkan-native.txt`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_gpu.metal-native.txt`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_gpu.direct3d11-native.txt`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_gpu.vulkan-native.txt`

**Interfaces:**

- Consumes: all prior tasks, `Render3DSnapshot`, `GpuReadback`, `GoldenGrid`, `GoldenCompare.AssertOrUpdate`, backend-specific golden families
- Produces: direct pixel invariants for every acceptance case, CPU versus GPU parity at `0.08`, and one committed reference grid per path and live backend

- [ ] **Step 1: Build a reusable test scene whose bend visibly departs from rest.**

`SkinnedTargetOutlineScene` owns constants, mesh builders, pose builders, capture variants, and pixel classifiers. Use `W = 480`, `H = 320`. Build a six-bone tube with enough rings that several downstream bones move more than one outline width:

```csharp
internal static SkinnedGltfMesh Tube() =>
    SkinnedMeshBuilder.BuildTube(0.42f, 3.8f, 12, 12, 6, Axis.Z);

internal static Matrix4x4[] BentPose(SkinnedGltfMesh tube, float perJoint = 0.38f)
{
    Matrix4x4[] bent = (Matrix4x4[])tube.RestPose.Clone();
    Matrix4x4 accumulated = Matrix4x4.Identity;
    Vector3 previousRest = tube.RestPose[0].Translation;
    Vector3 jointPosition = previousRest;
    for (int bone = 0; bone < tube.BoneCount; bone++)
    {
        Vector3 restPosition = tube.RestPose[bone].Translation;
        jointPosition += Vector3.Transform(restPosition - previousRest, accumulated);
        accumulated = Matrix4x4.CreateRotationY(perJoint) * accumulated;
        bent[bone] = Matrix4x4.CreateTranslation(-restPosition)
            * accumulated
            * Matrix4x4.CreateTranslation(jointPosition);
        previousRest = restPosition;
    }
    return bent;
}
```

The primary showcase draws:

- the bent tube ordinarily and into one outline group
- one rigid held box overlapping the bent tip, ordinarily and in the same group
- a foreground blue wall covering part of the body
- a 0.45 partial dissolve on a second placement
- one alpha-cutout skinned quad made from four `SkinnedVertex` values, two triangles, one identity bone, and UV corners `(0,0)` through `(1,1)`. Bind an 8 by 8 RGBA texture whose central 4 by 4 texels have alpha zero and whose other texels are opaque.

Configure a flat dark background, no starfield, no whole-scene post outline, shadows off, match-viewport scale, and anti-aliasing off for direct pixel classification. Use distinct body, wall, and rim colours with non-overlapping channel thresholds.

- [ ] **Step 2: Add a baseline capture for every assertion.**

Each property test captures the same scene without target outlines first. Additional controls capture rest-pose and bent-pose masks separately. Do not classify target interior from the outlined frame because that would allow the outline itself to define its own exclusion set.

The helper exposes these capture variants with exact inputs rather than boolean combinations whose meaning is hidden:

```csharp
internal static byte[] CaptureMixed(bool gpuSkinning, bool outlined);
internal static byte[] CapturePose(bool gpuSkinning, PoseCase poseCase);
internal static byte[] CaptureOcclusion(bool gpuSkinning, bool outlined);
internal static byte[] CaptureDissolve(bool gpuSkinning, DissolveCase dissolveCase);
internal static byte[] CaptureCutout(bool gpuSkinning, bool outlined);
```

`PoseCase` has `RestBody`, `BentBody`, `BentOutlineOnly`, and `RestBodyBentOutline`. `DissolveCase` has `SolidBaseline`, `PartialBody`, `PartialSceneDepthOutline`, and `PartialThroughGeometryOutline`.

- [ ] **Step 3: Write the mixed union and body-preservation pixel tests.**

Run both rows for `gpuSkinning = false` and `true`.

`Mixed_rigid_and_skinned_parts_have_no_internal_stroke` identifies the overlap seam from separate rigid-only and skinned-only masks. It asserts zero rim-classified pixels in a two-pixel band wholly inside the union and at least 80 rim pixels on the external boundary.

`Outline_never_replaces_ordinary_target_coverage` scans every rim pixel in the outlined capture and asserts the unoutlined baseline did not classify it as body. It also asserts at least 80 rim pixels so the exclusion is not vacuous.

```csharp
for (int pixel = 0; pixel < outlined.Length / 4; pixel++)
{
    if (!SkinnedTargetOutlineScene.IsRim(outlined, pixel)) continue;
    rimPixels++;
    Assert.False(SkinnedTargetOutlineScene.IsBody(baseline, pixel),
        $"outline replaced ordinary target coverage at pixel {pixel}");
}
Assert.True(rimPixels >= 80, $"mixed target drew only {rimPixels} rim pixels");
```

- [ ] **Step 4: Write the occlusion, dissolve, and alpha-cutout pixel tests.**

For each CPU and GPU row:

- `Opaque_wall_suppresses_default_outline_without_an_occlusion_cut_line` asserts zero changed baseline wall pixels and at least 40 external rim pixels on the unobscured side.
- `Partial_dissolve_changes_visible_external_border_without_interior_dither_strokes` asserts the partial scene-depth rim set differs from the solid rim set by at least 20 pixels, while zero rim pixels appear inside the solid body's eroded interior mask.
- `Through_geometry_partial_dissolve_uses_the_full_envelope` asserts the rim support of the partial through-geometry capture matches the solid full-envelope support within two edge pixels and contains no rim around interior dissolve holes.
- `Skinned_alpha_cutout_hole_is_absent_from_body_and_is_an_outline_hole` asserts the baseline centre hole contains zero body pixels, the hole boundary contains at least 20 rim pixels, and no rim pixel covers an opaque baseline texel.

The dissolve test uses the same world transform and pose in every control. It changes only outline occlusion and partial dissolve state.

- [ ] **Step 5: Write bent-pose, outline-only, mismatched-pose, and parity tests.**

`Bent_outline_follows_bent_pose_and_not_rest_pose` builds rest-only and bent-only contribution masks. The outlined bent capture must contain at least 80 rim pixels adjacent to bent-only coverage and zero rim pixels adjacent only to rest-only coverage.

`Outline_only_submission_draws_the_bent_silhouette` submits no `DrawSkinned` call, asserts a background-only baseline, at least 80 bent rim pixels, and zero rest-only rim pixels.

`Ordinary_rest_pose_and_bent_outline_use_only_their_own_poses` draws the ordinary body in rest pose and outlines the same handle in bent pose. It asserts body coverage remains at rest, rim coverage follows the bent control, and no rim follows the rest-only tip. This test runs both `UseGpuSkinning` values and is the second pin for Review Focus item 3.

`Cpu_and_gpu_bent_outline_frames_match` compares the complete mixed captures through the existing skinning tolerance:

```csharp
const float ParityTolerance = 0.08f;
GoldenGridComparison comparison = GoldenGrid.Compare(
    GoldenGrid.Downsample(cpu, W, H),
    GoldenGrid.Downsample(gpu, W, H),
    ParityTolerance);
Assert.True(comparison.Passed,
    $"CPU and GPU skinned outlines diverged beyond {ParityTolerance}, worst {comparison.WorstDiff:0.###}");
```

- [ ] **Step 6: Add exactly two committed-grid assertions.**

Use the primary showcase capture and stable names:

```csharp
[GpuFact]
public void Golden3D_SkinnedTargetOutline_Cpu()
{
    byte[] frame = SkinnedTargetOutlineScene.CaptureMixed(
        gpuSkinning: false, outlined: true);
    GoldenCompare.AssertOrUpdate("skinned_target_outline_cpu", frame, W, H);
}

[GpuFact]
public void Golden3D_SkinnedTargetOutline_Gpu()
{
    byte[] frame = SkinnedTargetOutlineScene.CaptureMixed(
        gpuSkinning: true, outlined: true);
    GoldenCompare.AssertOrUpdate("skinned_target_outline_gpu", frame, W, H);
}
```

Keep the direct pixel tests beside these grids. The 32 by 18 average grid is regression evidence for the final image and cannot replace the full and visible mask inferences.

- [ ] **Step 7: Run the property tests on a real local backend and verify they fail before implementation corrections, then pass.**

```bash
KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "(FullyQualifiedName~SkinnedTargetOutlineGoldenTests|FullyQualifiedName~SkinnedTargetOutlinePoseGoldenTests)&FullyQualifiedName!~Golden3D_SkinnedTargetOutline" \
  -- RunConfiguration.TreatNoTestsAsError=true
```

Before the renderer work is correct, expected failures include zero bent rim pixels, a rest-pose rim, internal overlap strokes, ordinary cutout body pixels, or CPU and GPU parity beyond `0.08`. After correction, expected exit is `0` with zero skipped property tests. The committed-grid rows run in bake mode in the next step.

- [ ] **Step 8: Bake the local backend references and inspect the full-resolution evidence.**

```bash
KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 dotnet test \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~Golden3D_SkinnedTargetOutline" \
  -- RunConfiguration.TreatNoTestsAsError=true
```

Expected: exit `0`, two grids for the active backend, and two `.bake.png` evidence images under `KhaozEngine.Render.Tests/Gpu/goldens-evidence`. Inspect both PNGs before accepting the reference. Confirm the tube is bent, the held rigid part joins the same outer rim, the wall occludes the default rim, the cutout hole is open, and the partial dissolve has no interior outline noise.

- [ ] **Step 9: Create a bake-seed commit and push only the task branch so CI can produce all three families.**

```bash
git add KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlineScene.cs \
  KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlineGoldenTests.cs \
  KhaozEngine.Render.Tests/Gpu/SkinnedTargetOutlinePoseGoldenTests.cs \
  KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_*.metal-native.txt
git commit -m "test(outline): prove skinned target pixels"
outline_branch=$(git branch --show-current)
git push -u origin "$outline_branch"
```

This push is for the controlled backend bake. Do not merge it. The task is not complete until all six family files are committed and verified.

- [ ] **Step 10: Dispatch the controlled all-backend bake for only this fixture.**

```bash
gh workflow run cross-platform-gpu.yml \
  --ref "$outline_branch" \
  -f bake=true \
  -f legs=all \
  -f tier=push \
  -f disableGpuDiskCache=false \
  -f renderTestFilter='FullyQualifiedName~SkinnedTargetOutline'
```

Find the exact dispatched run for the branch and wait for it:

```bash
run_id=$(gh run list --workflow cross-platform-gpu.yml --branch "$outline_branch" \
  --commit "$(git rev-parse HEAD)" --event workflow_dispatch --limit 1 \
  --json databaseId --jq '.[0].databaseId')
gh run watch "$run_id" --exit-status
```

Expected: exit `0` from all `metal-native`, `direct3d11-native`, and `vulkan-native` bake legs. The strict zero-skipped GPU gate must remain green.

- [ ] **Step 11: Download and add exactly six backend-owned grids.**

```bash
bake_dir=$(mktemp -d)
gh run download "$run_id" -n goldens-metal-native -D "$bake_dir/metal-native"
gh run download "$run_id" -n goldens-direct3d11-native -D "$bake_dir/direct3d11-native"
gh run download "$run_id" -n goldens-vulkan-native -D "$bake_dir/vulkan-native"
find "$bake_dir" -type f -name 'skinned_target_outline_*.txt' \
  -exec cp {} KhaozEngine.Render.Tests/Gpu/goldens/ \;
find KhaozEngine.Render.Tests/Gpu/goldens -maxdepth 1 -type f \
  -name 'skinned_target_outline_*.txt' -print | sort
```

Expected: exactly the six paths listed in this task's file block, two from each backend family. Inspect each leg's two bake PNGs from the artifacts before committing. Do not copy one backend's grid into another family.

- [ ] **Step 12: Commit the complete backend reference set.**

```bash
git add KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_cpu.*.txt \
  KhaozEngine.Render.Tests/Gpu/goldens/skinned_target_outline_gpu.*.txt
git commit -m "test(outline): add skinned backend goldens"
git push origin "$outline_branch"
```

- [ ] **Step 13: Dispatch a non-bake all-backend verification against the committed grids.**

```bash
gh workflow run cross-platform-gpu.yml \
  --ref "$outline_branch" \
  -f bake=false \
  -f legs=all \
  -f tier=push \
  -f disableGpuDiskCache=false \
  -f renderTestFilter='FullyQualifiedName~SkinnedTargetOutline'
```

Resolve and wait for the new verification run:

```bash
verify_run_id=$(gh run list --workflow cross-platform-gpu.yml --branch "$outline_branch" \
  --commit "$(git rev-parse HEAD)" --event workflow_dispatch --limit 1 \
  --json databaseId --jq '.[0].databaseId')
gh run watch "$verify_run_id" --exit-status
```

Expected: exit `0` on all three live backends, no golden rewrite, no skipped target-outline GPU test, and no validation error.

---

### Task 8: Publish the Consumer Contract, Stage the Live Version, Reconcile Main, and Run the Full Release Gate

**Files:**

- Modify: `KhaozEngine.Render3D/README.md`
- Modify: `docs/USING-KHAOZENGINE.md`
- Modify: `docs/INDEX.md`
- Modify: `docs/design/SKINNED-TARGET-OUTLINES-DESIGN-2026-09-23.md`
- Modify when the live version check requires a bump: `Directory.Build.props`
- Modify when the live version check requires a bump: guarded package reference lines in `README.md`
- Modify: `CHANGELOG.md`

**Interfaces:**

- Consumes: the complete shipped API and verified behavior from Tasks 1 through 7, repository version and release rules
- Produces: living consumer documentation, historical design status, one staged engine version, packed local packages, and a branch verified against current `origin/main`

- [ ] **Step 1: Sweep every changed public name and behavior before editing docs.**

```bash
git grep -n -w 'DrawSkinnedOutline' -- '*.md' '*.cs'
git grep -n -w 'DrawSkinnedOutlineDissolved' -- '*.md' '*.cs'
git grep -n -w 'DrawMeshOutline' -- '*.md'
git grep -n -w 'UseGpuSkinning' -- '*.md'
git grep -n -w 'AlphaCutoff' -- '*.md'
git grep -n -w 'ITileWorldScene' -- '*.md'
```

Read every hit that states the old rigid-only outline surface, old skinned cutoff behavior, old dissolve field ownership, or tile-world surface. Do not change unrelated historical release notes.

- [ ] **Step 2: Update the package README consumer contract.**

In the target-outline section of `KhaozEngine.Render3D/README.md`, document:

- the two grouped skinned methods and two convenience overloads
- one mixed rigid and skinned outer silhouette
- copied pose lifetime and frame-local group lifetime
- outline-only submission and ordinary versus outline pose independence
- GPU and CPU path selection through `UseGpuSkinning`
- partial dissolve full-mask and visible-mask rules
- ordinary colour and outline alpha cutout parity
- cascaded key-light skinned alpha cutout remains follow-up #1097

Update the skinned material paragraph so `SurfaceMaps.AlphaCutoff` explicitly reaches plain and dissolved ordinary colour plus target outline masks.

- [ ] **Step 3: Add one copy-ready API example to the living guide.**

Place it beside the existing target-outline and skinned mesh sections in `docs/USING-KHAOZENGINE.md`:

```csharp
MeshOutlineGroup target = scene.BeginMeshOutline(
    new Color(1f, 0.82f, 0.1f, 1f),
    widthPixels: 1.25f);

scene.DrawSkinnedOutline(target, body, currentPose, bodyWorld);
scene.DrawMeshOutline(target, heldItem, heldItemWorld);

scene.DrawSkinnedOutlineDissolved(
    target,
    fadingBody,
    currentPose,
    bodyWorld,
    dissolve: fade.Cover,
    dissolveComplement: false);
```

State that the scene copies `currentPose` during each call, the caller may reuse or mutate the array afterward, no ordinary `DrawSkinned` is required, and a different ordinary pose does not affect the outline. Add the matching `ITileWorldScene` signatures and compatibility-default behavior in the tile-world section.

- [ ] **Step 4: Mark the design implemented only after all three backend families pass.**

Change the design status and its `docs/INDEX.md` row to `Implemented for staged` followed by the exact version printed in Step 6, only after Task 7's non-bake all-backend run is green. Reference engine #1053 and scoped shadow follow-up #1097. Keep the design as history and move all usage instructions into the living docs.

- [ ] **Step 5: Fetch and merge current main before selecting the version.**

```bash
git fetch origin
git merge origin/main
```

Resolve conflicts on the task branch while preserving concurrent work. Re-run the focused headless and local GPU commands from Tasks 6 and 7 after any renderer or shader conflict.

- [ ] **Step 6: Re-read the live version and release tags, then apply the repository rule exactly.**

```bash
. scripts/tag-standard.sh
current_version=$(tag_props_version < Directory.Build.props)
latest_tag=$(git tag --sort=-v:refname | head -1)
printf 'current=%s latest=%s\n' "$current_version" "$latest_tag"
```

Decision:

- If `current_version` is ahead of `latest_tag` after stripping its leading `v`, ride that staged version. Add this feature to its newest changelog entry and use that version in the design status.
- If they are equal, choose the next free minor after checking both local and remote tags. At the planning snapshot this means `20.2.0`. Update `<KhaozEngineVersion>`, the four guarded package references in root `README.md`, and every guarded package reference in `docs/USING-KHAOZENGINE.md` in the same change.
- If a concurrent merge has staged or released `20.2.0`, recompute the next free minor rather than reusing it.

Use a minor for this new additive renderer capability. Do not create a release tag.

- [ ] **Step 7: Write the changelog entry in the staged version.**

Add a concise player-facing entry under the newest version heading. It must state that a posed skinned target and rigid held parts now form one pixel-width silhouette on CPU and GPU skinning, that dissolve and albedo cutout agree with the ordinary body, and that tile-world scenes forward the grouped methods. Link #1053. State the #1097 shadow limit only in the source note, not as a claimed shipped shadow feature.

- [ ] **Step 8: Run the doc and structure guards before the final build.**

```bash
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

Expected: every command exits `0`. If KESIZE fails, move cohesive behavior into the focused partial or helper already named in this plan. Do not update the baseline upward and do not add an exemption.

- [ ] **Step 9: Run the focused implementation and real-GPU proof synchronously.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~TargetOutline|FullyQualifiedName~SkinnedQueue|FullyQualifiedName~TileWorldSceneSkinned"
```

Expected: exit `0`, no skipped headless tests.

```bash
KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedTargetOutline" \
  -- RunConfiguration.TreatNoTestsAsError=true
```

Expected: exit `0`, zero skipped target-outline GPU tests on the active backend, both committed grids verified, and every direct pixel invariant green.

- [ ] **Step 10: Run the full repository Release gate synchronously from the worktree root.**

```bash
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
```

Expected: both commands exit `0`, with zero warnings and zero failures. GPU facts are expected to remain skipped in the plain suite according to their existing gate. Task 7 supplies their strict live execution.

- [ ] **Step 11: Run the shader validation slice explicitly.**

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~ShaderCorpusTests|FullyQualifiedName~ShaderSourceValidationTests|FullyQualifiedName~D3D11FxcValidationTests|FullyQualifiedName~UboLayoutTests"
```

Expected: exit `0`. On non-Windows hosts, retain the existing platform skip for the real FXC compiler row. The all-backend Task 7 workflow must show the Direct3D 11 FXC step green.

- [ ] **Step 12: Pack the cumulative local feed through the guarded script.**

```bash
scripts/pack-local-feed.sh
scripts/check-local-feed.sh
```

Expected: both commands exit `0`, packages written under the main checkout's `local-feed`, the staged package bytes match the current tree, and no released version is overwritten.

- [ ] **Step 13: Commit documentation and version staging together.**

Stage only paths changed by this task. If a version bump was required, include `Directory.Build.props`, all guarded version declarations, and `CHANGELOG.md` in this one release-staging commit.

```bash
git add KhaozEngine.Render3D/README.md \
  docs/USING-KHAOZENGINE.md \
  docs/INDEX.md \
  docs/design/SKINNED-TARGET-OUTLINES-DESIGN-2026-09-23.md \
  CHANGELOG.md \
  Directory.Build.props \
  README.md
git commit -m "release(20.2.0): stage skinned target outlines"
```

If execution rides a different live staged version, substitute that exact version in the subject. If no version file changed because another unreleased version was already staged, keep the changelog and docs in this commit and use that staged version in the subject.

- [ ] **Step 14: Re-run the complete final gate on the exact committed tree.**

```bash
git status --short --branch
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
scripts/pack-local-feed.sh
scripts/check-local-feed.sh
git diff --check origin/main...HEAD
```

Expected: each command exits `0`, the task branch is clean, and the pack script confirms the staged version. Record every exit code in the handoff.

- [ ] **Step 15: Verify the exact final commit on all three GPU backends.**

Push the verified task branch and dispatch the same non-bake filter used in Task 7:

```bash
outline_branch=$(git branch --show-current)
git push origin "$outline_branch"
gh workflow run cross-platform-gpu.yml \
  --ref "$outline_branch" \
  -f bake=false \
  -f legs=all \
  -f tier=push \
  -f disableGpuDiskCache=false \
  -f renderTestFilter='FullyQualifiedName~SkinnedTargetOutline'
```

Resolve and wait for the final dispatched run:

```bash
final_gpu_run_id=$(gh run list --workflow cross-platform-gpu.yml --branch "$outline_branch" \
  --commit "$(git rev-parse HEAD)" --event workflow_dispatch --limit 1 \
  --json databaseId --jq '.[0].databaseId')
gh run watch "$final_gpu_run_id" --exit-status
```

Expected: exit `0` for the Metal, Direct3D 11, and Vulkan legs against the committed family files, zero skipped target-outline GPU rows, and a green Direct3D 11 FXC validation step.

- [ ] **Step 16: Hand the verified branch to the integrating owner.**

The integrating owner follows the repository's normal merge-to-main and push flow, closes #1053 after the pushed merge, and keeps #1097 open. Leave `scripts/tag-release.sh` untouched until the owner explicitly starts a release.

---

## Final Acceptance Checklist

- [ ] One group contains any rigid and skinned mix and composites once.
- [ ] CPU and GPU masks follow the submitted bent pose and agree within `0.08`.
- [ ] Outline-only submission records and renders without an ordinary skinned draw.
- [ ] The same mesh may draw ordinarily at rest and outline bent, with no pose cross-talk.
- [ ] Plain and dissolved methods work through `Scene3D` and `Scene3DTileWorldScene`.
- [ ] Ordinary plain and dissolved CPU and GPU colour alpha-test with the retained cutoff.
- [ ] Both full and visible outline masks use the same albedo alpha and cutoff.
- [ ] Partial dissolve keeps the full envelope and clips only visible coverage in scene-depth mode.
- [ ] Fully invisible dissolve endpoints record no outline GPU work.
- [ ] Invalid lifetime, wrong pose length, and resource failures retain no partial state and composite nothing.
- [ ] Load, unload, shadow-layout replacement, scene disposal, palette growth, and CPU stream growth have explicit lifetime tests.
- [ ] Metal, Direct3D 11, and Vulkan verify both CPU and GPU committed grids plus all direct pixel invariants.
- [ ] The living docs carry the public contract, the design is marked implemented, and #1097 remains the skinned shadow cutout follow-up.
- [ ] Version staging follows the live version and tag state, packages are built into the cumulative local feed, and no release tag is created.
