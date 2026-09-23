# Skinned Point-Light Shadows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every shadow-casting skinned draw contribute to affected static and dynamic point lights while preserving the static rigid cache, the current no-skinned pixels, and the existing point-shadow budgets.

**Architecture:** Gather point-shadow spheres before skinned compaction so an off-camera caster can survive for point work. Dynamic lights draw rigid and skinned casters into their existing base row. Static lights keep rigid data in the base atlas and draw current skinned poses into dense rows of a compact transient atlas. Receiver shaders use the existing base-only function when `ShadowParams.w < 0` and otherwise take the minimum base and transient depth at each logical hard or soft tap.

**Tech Stack:** .NET 10, C# partial renderers, KhaozEngine GPU interfaces, GLSL 450 cross-compiled to Metal MSL, Direct3D 11 HLSL, and Vulkan SPIR-V, xUnit headless and native GPU tests.

**Spec:** `docs/design/SKINNED-POINT-SHADOWS-DESIGN-2026-09-23.md`

**Issues:** Engine [#1054](https://github.com/APKiwiOrg/KhaozEngine/issues/1054) owns this feature. [#1097](https://github.com/APKiwiOrg/KhaozEngine/issues/1097) owns skinned colour and key-light `AlphaCutoff`. [#1098](https://github.com/APKiwiOrg/KhaozEngine/issues/1098) owns point-light MASK alpha testing. Neither alpha-cutout issue is part of this implementation.

## Global Constraints

- Execute this plan after the skinned target-outline plan on current `main` when both plans touch `Scene3D.cs`, `ModelRenderer.Skinned.cs`, `ShaderSources.Model.cs`, `ShippedShaderPrograms.cs`, or the three shader hash tables. Merge current `main` into the task branch before starting those files and preserve the outline interfaces already landed.
- Work in an isolated `feature/<name>` worktree. Preserve unrelated edits and stage explicit paths.
- Read `AGENTS.md`, `docs/CONTRIBUTOR-RULES.md`, this plan, and the design before implementation.
- Keep `LightShadow.Static` rigid signatures unchanged. Bone poses, skinned models, and skinned caster membership never enter `PointCasterSignature` and never spend `MaxStaticRebuildsPerFrame`.
- Keep `LightShadow.Dynamic` under `MaxDynamicLightsPerFrame`. A dynamic request outside that budget publishes no base row, gets no transient row, and records no skinned point work.
- Add no `PointShadowSettings` field and no draw overload. `castsShadows: true` remains the existing default.
- Keep CPU spatial decisions in absolute world space. Rebase only face uniforms, caster models, light positions, and exclusion corners immediately before GPU packing.
- Use the inflated absolute rest-pose sphere from `SkinnedCullSafetyFactor` for both pre-compaction retention and per-light caster selection.
- Allocate, resize, bind, and release the transient atlas only from `Scene3D.Begin`. Render-time scheduling may record demand but may not create GPU resources.
- Keep transient capacity at exact high water within a compatible base layout. Never let it exceed the live base row count.
- Treat the base texture and transient texture as one receiver binding transaction. A live set may never combine a new base texture with a stale or disposed transient texture.
- Keep the current hard and soft base-only GLSL functions textually unchanged. The `ShadowParams.w < 0` branch must call those functions before any transient shape read or texture fetch.
- Keep `PointLightGpuData` at 48 bytes. The first 16 frame UBO entries mirror `(baseRow, bias, slopeBias, transientRow)`, while every receiver reads the structured buffer.
- A 1 by 1 white R32Float texture is the transient default. Old pixels in unassigned transient rows stay unreachable through `transientRow = -1`.
- CPU skinning reuses the already deformed vertex and instance buffers. GPU skinning reuses one shared palette slot per retained caster. Do not add per-light, per-face bone copies.
- Use one live point-shadow pipeline family for the base and transient framebuffers. The second atlas adds a colour texture, depth texture, and framebuffer only.
- Put new responsibilities in focused files to satisfy KESIZE. Do not raise a file-size baseline or add an exemption without owner approval.
- GPU tests whose fully qualified name must run on all native push legs include `Golden` in the class name.
- If `Directory.Build.props` is ahead of the newest release tag when finishing, ride that staged version. Otherwise take the next free minor for this additive renderer capability. Leave the release tag to the owner.
- Do not introduce em dash or en dash glyphs or prose semicolons.

## File Structure

### New production files

- `KhaozEngine.Render3D/Internal/PointShadowCasterSphere.cs` owns construction and shell intersection for the one conservative skinned caster sphere.
- `KhaozEngine.Render3D/Internal/PointShadowTransientRows.cs` owns deterministic candidate ordering and dense row assignment.
- `KhaozEngine.Render3D/Scene3D.SkinnedPointShadows.cs` owns skinned point candidate discovery, static transient scheduling, dynamic skinned scheduling, and CPU/GPU skinned draw recording.
- `KhaozEngine.Render3D/Rendering/PointShadowRenderer.Skinned.cs` owns the point-skinned header ring, GPU-skinned pipelines, and CPU/GPU skinned draw entry points.

### Modified production files

- `KhaozEngine.Render3D/Scene3D.cs` moves point request gathering ahead of skinned compaction and carries the absolute caster sphere in both compacted draw records.
- `KhaozEngine.Render3D/Scene3D.PointShadowRequests.cs` prepares ordered requests before compaction and exposes which request spheres can retain a caster this frame.
- `KhaozEngine.Render3D/Scene3D.PointShadows.cs` keeps base residency and rigid signatures, invokes transient assignment, publishes both row arrays, and records new counters.
- `KhaozEngine.Render3D/Scene3D.PointShadowPass.cs` packs a target atlas and row independently and records rigid-only, rigid-plus-skinned, or skinned-only rows.
- `KhaozEngine.Render3D/Scene3D.PointShadowReconfigure.cs` applies base and transient layouts at `Begin`, caps transient high water on base shrink, latches refused transient layouts, and publishes resolved memory.
- `KhaozEngine.Render3D/Rendering/PointShadowAtlas.cs` exposes byte accounting and owns first-use clear state so one renderer can target either atlas.
- `KhaozEngine.Render3D/Rendering/PointShadowRenderer.cs` becomes atlas-independent for framebuffer selection, row clear, face scissor, and face packing.
- `KhaozEngine.Render3D/Rendering/ModelRenderer.PointShadowUniforms.cs` stores base and transient row mirrors, the appended transient shape vector, both default/live textures, and atomic pair rebinding.
- `KhaozEngine.Render3D/Rendering/ModelRenderer.PointLightBuffer.cs` writes `transientRow` into `PointLightGpuData.ShadowParams.w` for the complete light list.
- `KhaozEngine.Render3D/Rendering/ModelRenderer.FrameUbo.cs` appends and packs `PointShadowTransientAtlas` before the existing cluster tail.
- `KhaozEngine.Render3D/Rendering/ModelRenderer.ShadowLayoutReplacement.cs` rebuilds tracked receiver sets with cascade, base point, and transient point textures together.
- `KhaozEngine.Render3D/Rendering/ModelRenderer.cs`, `ModelRenderer.Splat.cs`, and `ModelRenderer.TileGround.cs` add the transient texture to every receiver layout and resource set.
- `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs` forwards shared palette access and the new point-skinned renderer entry points without duplicating palette uploads.
- `KhaozEngine.Render3D/Internal/ShaderSources.PointShadow.cs` adds opaque and dissolve-aware GPU-skinned point caster vertices.
- `KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs` adds the combined per-tap minimum path while preserving the base-only functions.
- `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs`, `ShaderSources.Terrain.cs`, `ShaderSources.TileGround.cs`, and `ShaderSources.Foliage.cs` append the transient shape vector and bind or forward `PointShadowTransientMap` consistently.
- `KhaozEngine.Render3D/PointShadowResolution.cs` adds live base, transient, and total byte diagnostics.
- `KhaozEngine.Render3D/ShadowPassDiagnostics.cs` adds transient demand, rows rendered, and split skinned draw counters.

### Test and generated evidence files

- Create `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowCullingTests.cs`.
- Create `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowRowsTests.cs`.
- Create `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowSchedulingTests.cs`.
- Create `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowReconfigureTests.cs`.
- Create `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowShaderTests.cs`.
- Modify `KhaozEngine.Render.Tests/Render3D/PointLightBufferTests.cs`, `PointShadowUboLayoutTests.cs`, `PointShadowAtlasBindTests.cs`, `PointShadowFilterShaderTests.cs`, and `PointShadowSettingsTests.cs`.
- Modify `KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs` to inject framebuffer creation failure beside its texture and resource-set failures.
- Create `KhaozEngine.Render.Tests/Gpu/SkinnedPointShadowScene.cs` as the shared lazy fixture.
- Create `KhaozEngine.Render.Tests/Gpu/SkinnedPointShadowGoldenTests.cs` for committed and property GPU proofs.
- Modify `KhaozEngine.Render.Tests/Gpu/PointShadowByteIdentityGpuTests.cs`, `ShaderSourceValidationTests.cs`, `D3D11RegisterNumberingTests.cs`, `D3D11BindFixtures.cs`, `D3D11HlslRegisterAgreementTests.cs`, `D3D11FxcValidationTests.cs`, `D3D11HlslByteEqualityTests.cs`, `VulkanBindBudgetTests.cs`, `VulkanDescriptorLimitTests.cs`, `VulkanLayoutCompatibilityTests.cs`, `VulkanShippedVertexLayoutTests.cs`, `VulkanShaderBindingTableTests.cs`, and `VulkanSpirvByteEqualityTests.cs`.
- Modify `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs` and `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`.
- Update `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`, `msl-hashes/metal-msl.sha256.txt`, and `spirv-hashes/vulkan-spirv.sha256.txt` through their guarded writers.
- Add `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.metal-native.txt`.
- Add `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.direct3d11-native.txt`.
- Add `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.vulkan-native.txt`.

### Documentation and release files

- Modify `KhaozEngine.Render3D/README.md`, `docs/USING-KHAOZENGINE.md`, `docs/INDEX.md`, and `docs/design/SKINNED-POINT-SHADOWS-DESIGN-2026-09-23.md`.
- Modify `Directory.Build.props`, `CHANGELOG.md`, and the current-version `PackageReference` examples in `README.md` and `docs/USING-KHAOZENGINE.md` when no staged version exists.

## Review Focus

1. A static light owns a base row that has never rendered. Expected behavior is base row `-1`, transient row `-1`, and no transient demand. Pinned by `SkinnedPointShadowSchedulingTests.NeverRenderedStaticBaseRowPublishesNeitherBaseNorTransient` in Task 6.
2. More static candidates exist than live transient capacity and several have equal eye distance. Expected behavior is deterministic distance, static-key, light-index ordering with dense rows and `-1` for every loser. Pinned by `SkinnedPointShadowRowsTests.OverflowUsesDistanceThenKeyThenLightIndexAndClearsOldMappings` in Task 2.
3. A dynamic request beyond `MaxDynamicLightsPerFrame` is the only reason an off-camera skinned draw might survive. Expected behavior is full culling with no palette or vertex upload, no row, and no point draw. Pinned by `SkinnedPointShadowSchedulingTests.DynamicPastBudgetDoesNotRetainPointOnlyCaster` in Task 6.
4. The base atlas shrinks from 60 rows to eight after transient capacity reached 60 and the compatible transient allocation is refused. Expected behavior is the new eight-row base with the white transient default, no oversized old transient atlas, and total live bytes capped to the new base plus zero transient bytes. Pinned by `SkinnedPointShadowReconfigureTests.BaseShrinkToEightDropsOversizedTransientWhenReplacementAllocationFails` in Task 4.
5. One receiver set refuses a base-plus-transient pair rebind after both candidate textures exist. Expected behavior is the complete old pair still bound, both candidates disposed, no mixed pair visible, and the refused pair latched. Pinned by `PointShadowAtlasBindTests.FailedPairRebindLeavesCompleteOldPairBoundAndDisposesCandidates` in Task 3.

---

### Task 1: Retain point-only skinned casters before compaction

**Files:**
- Create: `KhaozEngine.Render3D/Internal/PointShadowCasterSphere.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowCullingTests.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadowRequests.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.cs:1624-1743`
- Test: `KhaozEngine.Render.Tests/Render3D/Scene3DSkinnedCullingTests.cs`

**Interfaces:**
- Produces: `PointShadowCasterSphere.FromRestBounds(in MeshBounds, in Matrix4x4, float) -> PointShadowCasterSphere`.
- Produces: `PointShadowCasterSphere.TouchesShadowingShell(Vector3 lightPosition, float radius, float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax) -> bool`.
- Produces: `Scene3D.PreparePointShadowRequests(Vector3 eyeAbsolute) -> bool`, called once before skinned compaction.
- Produces: `Scene3D.PointRequestCanRetainSkinned(in PointShadowCasterSphere, PointShadowSettings) -> bool`.
- Preserves: `Scene3D.ClassifySkinnedVisibility` as the camera and key-light classifier used by the outline work.

- [ ] **Step 1: Write the pure sphere and retention failures**

```csharp
[Theory]
[InlineData(0f, 0f, 0f, true)]
[InlineData(20f, 0f, 0f, false)]
public void OuterSphereControlsRetention(float x, float y, float z, bool expected)
{
    var caster = new PointShadowCasterSphere(new Vector3(x, y, z), 1.5f);
    Assert.Equal(expected, caster.TouchesShadowingShell(Vector3.Zero, 8f, 0f, default, default));
}

[Fact]
public void WhollyInsideNearRadiusOrExclusionBoxIsRejectedButPartialOverlapIsKept()
{
    var inner = new PointShadowCasterSphere(new Vector3(0.5f, 0f, 0f), 0.25f);
    var partial = new PointShadowCasterSphere(new Vector3(1.9f, 0f, 0f), 0.25f);
    Assert.False(inner.TouchesShadowingShell(Vector3.Zero, 8f, 1f, default, default));
    Assert.True(partial.TouchesShadowingShell(Vector3.Zero, 8f, 2f, default, default));
    Assert.False(inner.TouchesShadowingShell(Vector3.Zero, 8f, 0f, new Vector3(-1f), new Vector3(1f)));
    Assert.True(partial.TouchesShadowingShell(Vector3.Zero, 8f, 0f, new Vector3(-2f), new Vector3(2f)));
}

[Fact]
public void RestBoundsProduceTheSameInflatedAbsoluteSphereAcrossSkinningModes()
{
    var bounds = new MeshBounds(new Vector3(-1f), new Vector3(1f));
    Matrix4x4 world = Matrix4x4.CreateTranslation(100_000f, 2f, -100_000f);
    PointShadowCasterSphere cpu = PointShadowCasterSphere.FromRestBounds(
        bounds, world, Scene3D.SkinnedCullSafetyFactor);
    PointShadowCasterSphere gpu = PointShadowCasterSphere.FromRestBounds(
        bounds, world, Scene3D.SkinnedCullSafetyFactor);
    Assert.Equal(cpu, gpu);
    Assert.Equal(new Vector3(100_000f, 2f, -100_000f), cpu.Center);
}
```

- [ ] **Step 2: Run the culling tests and verify the new type is missing**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowCulling"`

Expected: exit code 1 with compile errors naming `PointShadowCasterSphere`.

- [ ] **Step 3: Add the conservative sphere owner**

```csharp
namespace KhaozEngine.Render3D.Internal;

internal readonly record struct PointShadowCasterSphere(Vector3 Center, float Radius)
{
    internal static PointShadowCasterSphere FromRestBounds(
        in MeshBounds bounds, in Matrix4x4 world, float safetyFactor)
    {
        bounds.WorldSphere(world, out Vector3 center, out float radius);
        return new PointShadowCasterSphere(center, radius * safetyFactor);
    }

    internal bool TouchesShadowingShell(Vector3 lightPosition, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        float distanceSquared = (Center - lightPosition).LengthSquared();
        float reach = Radius + radius;
        if (distanceSquared > reach * reach) return false;
        if (nearRadius > 0f && MathF.Sqrt(distanceSquared) + Radius <= nearRadius) return false;
        if (!Scene3D.IsExclusionBox(exclusionMin, exclusionMax)) return true;
        return !(Center.X - Radius >= exclusionMin.X && Center.X + Radius <= exclusionMax.X
            && Center.Y - Radius >= exclusionMin.Y && Center.Y + Radius <= exclusionMax.Y
            && Center.Z - Radius >= exclusionMin.Z && Center.Z + Radius <= exclusionMax.Z);
    }
}
```

- [ ] **Step 4: Move request gathering before skinned compaction**

Call `PreparePointShadowRequests(ActiveCamera.Eye)` after camera and key-light cascade preparation and before the loop that builds `_cpuSkinnedDraws` or `_gpuSkinnedDraws`. Make `PreparePointShadows` consume that prepared list rather than call `GatherPointShadowRequests` again.

```csharp
bool hasPointShadowRequests = PreparePointShadowRequests(ActiveCamera.Eye);

PointShadowCasterSphere pointSphere = PointShadowCasterSphere.FromRestBounds(
    entry.Bounds, it.World, SkinnedCullSafetyFactor);
bool visiblePointShadow = hasPointShadowRequests
    && it.CastsShadows
    && PointRequestCanRetainSkinned(pointSphere, Post.Quality.Shadows.PointShadows);
if (!visibleMain && !visibleShadow && !visiblePointShadow)
{
    _culledSkinnedInstances++;
    continue;
}
```

`PointRequestCanRetainSkinned` must consider static requests only while a base atlas is live. It must consider only the nearest `MaxDynamicLightsPerFrame` dynamic requests. It must test the same prepared request order later scheduling uses. It must not acquire rows or allocate an atlas.

- [ ] **Step 5: Run culling and existing skinned visibility tests**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowCulling|FullyQualifiedName~Scene3DSkinnedCulling"`

Expected: exit code 0. The existing camera and key-light cases remain green, and the new outer, near-radius, exclusion, partial-overlap, safety-factor, and large-origin cases pass.

- [ ] **Step 6: Commit the retention unit**

```bash
git add KhaozEngine.Render3D/Internal/PointShadowCasterSphere.cs \
  KhaozEngine.Render3D/Scene3D.PointShadowRequests.cs \
  KhaozEngine.Render3D/Scene3D.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowCullingTests.cs \
  KhaozEngine.Render.Tests/Render3D/Scene3DSkinnedCullingTests.cs
git commit -m "render3d(point-shadows): retain skinned point casters"
```

### Task 2: Assign dense transient rows and pack every light record

**Files:**
- Create: `KhaozEngine.Render3D/Internal/PointShadowTransientRows.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowRowsTests.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadows.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.PointShadowUniforms.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.PointLightBuffer.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/PointLightBufferTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/PointShadowUboLayoutTests.cs`

**Interfaces:**
- Consumes: prepared point request indices and live transient capacity.
- Produces: `PointShadowTransientCandidate(int RequestIndex, int LightIndex, long StaticKey, float DistanceSquared)`.
- Produces: `PointShadowTransientRows.Assign(List<PointShadowTransientCandidate>, int capacity, Span<int> rowsByRequest, List<int> selectedRequestIndices) -> int`.
- Changes: `ModelRenderer.SetPointShadowUniforms(ReadOnlySpan<int> baseRows, ReadOnlySpan<int> transientRows, float bias, float slopeBias, int faceResolution, int baseRowsCount, int transientRowsCount, PointShadowFilter filter, float lightSizeMetres, float maxPenumbraTexels)`.
- Changes: `ModelRenderer.BuildPointLightRecords(ReadOnlySpan<PointLightData>, Span<PointLightGpuData>, ReadOnlySpan<int> baseRows, ReadOnlySpan<int> transientRows, float bias, float slopeBias, Vector3 renderOrigin = default)`.

- [ ] **Step 1: Write deterministic assignment and dense mapping tests**

```csharp
[Fact]
public void OverflowUsesDistanceThenKeyThenLightIndexAndClearsOldMappings()
{
    var candidates = new List<PointShadowTransientCandidate>
    {
        new(4, 19, 90, 4f),
        new(1, 3, 20, 1f),
        new(3, 8, 10, 1f),
        new(2, 7, 10, 1f),
    };
    int[] rows = { 7, 7, 7, 7, 7 };
    var selected = new List<int>();

    int count = PointShadowTransientRows.Assign(candidates, 3, rows, selected);

    Assert.Equal(3, count);
    Assert.Equal(new[] { -1, 2, 0, 1, -1 }, rows);
    Assert.Equal(new[] { 2, 3, 1 }, selected);
}

[Fact]
public void ZeroCapacityPublishesMinusOneForEveryCandidate()
{
    int[] rows = { 9, 9 };
    var selected = new List<int> { 99 };
    int count = PointShadowTransientRows.Assign(
        new List<PointShadowTransientCandidate> { new(0, 0, 1, 0f) }, 0, rows, selected);
    Assert.Equal(0, count);
    Assert.Equal(new[] { -1, -1 }, rows);
    Assert.Empty(selected);
}
```

- [ ] **Step 2: Run the row tests and verify they fail**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowRows"`

Expected: exit code 1 with missing `PointShadowTransientCandidate` and `PointShadowTransientRows`.

- [ ] **Step 3: Implement allocation-free ordering and dense assignment**

```csharp
internal readonly record struct PointShadowTransientCandidate(
    int RequestIndex, int LightIndex, long StaticKey, float DistanceSquared);

internal static class PointShadowTransientRows
{
    internal static int Assign(List<PointShadowTransientCandidate> candidates, int capacity,
        Span<int> rowsByRequest, List<int> selectedRequestIndices)
    {
        rowsByRequest.Fill(-1);
        selectedRequestIndices.Clear();
        candidates.Sort(static (a, b) =>
        {
            int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
            if (distance != 0) return distance;
            int key = a.StaticKey.CompareTo(b.StaticKey);
            return key != 0 ? key : a.LightIndex.CompareTo(b.LightIndex);
        });
        int count = Math.Min(Math.Max(0, capacity), candidates.Count);
        for (int row = 0; row < count; row++)
        {
            int requestIndex = candidates[row].RequestIndex;
            rowsByRequest[requestIndex] = row;
            selectedRequestIndices.Add(requestIndex);
        }
        return count;
    }
}
```

- [ ] **Step 4: Write the 20-light structured record failure**

```csharp
[Fact]
public void TwentiethLightCarriesIndependentBaseAndTransientRowsInStructuredRecord()
{
    var lights = Enumerable.Range(0, 20).Select(Light).ToArray();
    var records = new ModelRenderer.PointLightGpuData[20];
    int[] baseRows = Enumerable.Repeat(-1, 20).ToArray();
    int[] transientRows = Enumerable.Repeat(-1, 20).ToArray();
    baseRows[19] = 7;
    transientRows[19] = 0;

    ModelRenderer.BuildPointLightRecords(
        lights, records, baseRows, transientRows, 0.01f, 0.02f);

    Assert.Equal(48, Marshal.SizeOf<ModelRenderer.PointLightGpuData>());
    Assert.Equal(new Vector4(7f, 0.01f, 0.02f, 0f), records[19].ShadowParams);
}
```

Also change the frame UBO assertions so the first 16 entries mirror all four values and lights past index 15 remain absent from the mirror.

- [ ] **Step 5: Run host packing tests and verify the old signatures fail**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointLightBuffer|FullyQualifiedName~PointShadowUboLayout"`

Expected: exit code 1 because the old builders do not accept transient rows and write zero to `ShadowParams.w` unconditionally.

- [ ] **Step 6: Carry two row arrays without changing record size**

Add `_pointTransientSlotUniform` to `Scene3D` and `_pointShadowTransientSlots` to `ModelRenderer`. Fill both arrays with `-1` before publishing. Use this packing rule everywhere:

```csharp
float baseRow = i < baseRows.Length ? baseRows[i] : -1f;
float transientRow = i < transientRows.Length ? transientRows[i] : -1f;
records[i].ShadowParams = new Vector4(baseRow, bias, slopeBias, transientRow);
```

Initialize and clear the compatibility mirror to `new Vector4(-1f, 0f, 0f, -1f)`. Preserve `PointLightRecordBytes = 48`.

- [ ] **Step 7: Run row and packing tests**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowRows|FullyQualifiedName~PointLightBuffer|FullyQualifiedName~PointShadowUboLayout"`

Expected: exit code 0. The 20th light carries base row 7 and transient row 0 through the structured buffer.

- [ ] **Step 8: Commit row assignment and packing**

```bash
git add KhaozEngine.Render3D/Internal/PointShadowTransientRows.cs \
  KhaozEngine.Render3D/Scene3D.PointShadows.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.PointShadowUniforms.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.PointLightBuffer.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowRowsTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointLightBufferTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointShadowUboLayoutTests.cs
git commit -m "render3d(point-shadows): pack dense transient rows"
```

### Task 3: Bind the transient receiver atlas and take a per-tap minimum

**Files:**
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowShaderTests.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.PointShadowUniforms.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.FrameUbo.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.ShadowLayoutReplacement.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.PointLightBuffer.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.Splat.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.TileGround.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Terrain.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.TileGround.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Foliage.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/PointShadowAtlasBindTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/PointShadowFilterShaderTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/PointShadowUboLayoutTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/D3D11RegisterNumberingTests.cs`, `D3D11BindFixtures.cs`, and `D3D11HlslRegisterAgreementTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanBindBudgetTests.cs`, `VulkanDescriptorLimitTests.cs`, `VulkanShippedVertexLayoutTests.cs`, and `VulkanShaderBindingTableTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt` and the three HLSL, MSL, and SPIR-V pinned hash tables

**Interfaces:**
- Produces: `ModelRenderer.PointShadowTransientTailOffset = PointShadowTailOffset + PointShadowTailBytes`.
- Produces: `ModelRenderer.PointShadowTransientTailBytes = 16`.
- Changes: `ModelRenderer.ClusterTailOffset = PointShadowTransientTailOffset + PointShadowTransientTailBytes` and `UboBytes = 1344`.
- Produces: `ModelRenderer.BindPointShadowAtlases(IGpuTexture? baseAtlas, IGpuTexture? transientAtlas, IReadOnlyList<IGpuResourceSet>, Action<Func<IGpuResourceSet, IGpuResourceSet>>) -> PointShadowBindResult`.
- Produces: `ModelRenderer.BoundPointShadowTexture` and `BoundPointShadowTransientTexture` diagnostics.
- Produces GLSL: `PointShadowTransientAtlas`, `PointShadowTransientMap`, `pointShadowCombinedDepthAt`, `samplePointShadowHardCombined`, `samplePointShadowSoftCombined`, and `samplePointShadowCombined`.

- [ ] **Step 1: Write UBO, binding, and shader failures**

Add these assertions before changing the renderer:

```csharp
[Fact]
public void TransientShapeAppendsAfterTheCompatibilityTail()
{
    Assert.Equal(1296u, ModelRenderer.PointShadowTransientTailOffset);
    Assert.Equal(16u, ModelRenderer.PointShadowTransientTailBytes);
    Assert.Equal(1312u, ModelRenderer.ClusterTailOffset);
    Assert.Equal(1344u, ModelRenderer.UboBytes);
}

[Fact]
public void BaseOnlyBranchPrecedesEveryTransientRead()
{
    string source = ShaderSources.LightingCommonGlsl;
    int function = source.IndexOf("float samplePointShadowCombined(", StringComparison.Ordinal);
    Assert.True(function >= 0);
    int gate = source.IndexOf("if (params.w < 0.0)", function, StringComparison.Ordinal);
    int baseReturn = source.IndexOf("return samplePointShadow(baseAtlas, samp", function, StringComparison.Ordinal);
    int combinedCall = source.IndexOf("samplePointShadowHardCombined(", function, StringComparison.Ordinal);
    Assert.True(function < gate && gate < baseReturn && baseReturn < combinedCall);
    int bodyStart = source.IndexOf('{', function) + 1;
    Assert.True(bodyStart > function && bodyStart < gate);
    string beforeGate = source.Substring(bodyStart, gate - bodyStart);
    Assert.DoesNotContain("transientAtlas", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("PointShadowTransientAtlas", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("PointShadowTransientMap", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("samplePointShadowHardCombined", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("samplePointShadowSoftCombined", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("pointShadowCombinedDepthAt", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("pointShadowDepthAtFaceUv", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("textureLod(", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("texture(", beforeGate, StringComparison.Ordinal);
    Assert.DoesNotContain("texelFetch(", beforeGate, StringComparison.Ordinal);
}

[Fact]
public void HardBlockerAndFilterTapsTakeTheNearerStoredDistance()
{
    string source = ShaderSources.LightingCommonGlsl;
    Assert.Contains("return min(baseStored, transientStored);", source, StringComparison.Ordinal);
    Assert.Contains("samplePointShadowHardCombined", source, StringComparison.Ordinal);
    Assert.Contains("pointShadowBlockerSearchCombined", source, StringComparison.Ordinal);
    Assert.Contains("pointShadowFilterDiscCombined", source, StringComparison.Ordinal);
}
```

Add `PointShadowAtlasBindTests.FailedPairRebindLeavesCompleteOldPairBoundAndDisposesCandidates` with two live textures, two candidate textures, a failure on the second rebuilt resource set, and assertions against both bound handles.

- [ ] **Step 2: Run receiver tests and verify they fail**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointShadowUboLayout|FullyQualifiedName~PointShadowAtlasBind|FullyQualifiedName~PointShadowFilterShader|FullyQualifiedName~SkinnedPointShadowShader"`

Expected: exit code 1 because there is no transient tail, texture, pair transaction, or combined shader path.

- [ ] **Step 3: Append the transient shape without moving earlier offsets**

Pack this vector after `_pointShadowFilter` and before `_clusterDepth`:

```csharp
// (1 / (6 * faceRes), 1 / (transientRows * faceRes), transientRows, 6)
Vector4 _pointShadowTransientAtlas;

MemoryMarshal.Write(img.Slice((int)PointShadowTransientTailOffset),
    in _pointShadowTransientAtlas);
```

Every frame-block declaration must use this exact order:

```glsl
vec4 PointShadowParams[16];
vec4 PointShadowAtlas;
vec4 PointShadowFilter;
vec4 PointShadowTransientAtlas;
vec4 ClusterDepth;
vec4 ClusterCamera;
```

- [ ] **Step 4: Make receiver-set replacement atomic over both textures**

Create a second 1 by 1 white R32Float default and change `ShadowSamplingBinding` reconstruction so the trailing resources are exactly:

```csharp
resources[^4] = shadowTexture;
resources[^3] = _shadowMap.ShadowSampler;
resources[^2] = pointShadowTexture;
resources[^1] = pointShadowTransientTexture;
```

Key the bind-failure latch by the requested base and transient texture references. Build every replacement first. Commit both bound handles only after every set succeeds. Dispose all candidates and retain both old handles on any exception.

- [ ] **Step 5: Preserve the base-only shader functions and add combined helpers**

Do not edit the bodies of `pointShadowDepthAt`, `pointShadowBlockerSearch`, `pointShadowFilterDisc`, `samplePointShadowSoft`, `samplePointShadowHard`, or `samplePointShadow`. Add separate helpers. The combined fetch must select one face and local UV, map it through each atlas shape, and take the minimum:

```glsl
float pointShadowCombinedDepthAt(texture2D baseAtlas, texture2D transientAtlas, sampler samp,
                                 vec3 dir, float baseRow, float transientRow) {
    float face; vec2 uv;
    pointShadowFace(dir, face, uv);
    float baseStored = pointShadowDepthAtFaceUv(
        baseAtlas, samp, face, uv, baseRow, PointShadowAtlas);
    float transientStored = pointShadowDepthAtFaceUv(
        transientAtlas, samp, face, uv, transientRow, PointShadowTransientAtlas);
    return min(baseStored, transientStored);
}
```

Use that helper independently in all four hard taps, every six-tap blocker-search iteration, and every nine-tap filter iteration. Route at the top level before any transient data read:

```glsl
float samplePointShadowCombined(texture2D baseAtlas, texture2D transientAtlas, sampler samp,
                                vec3 toL, float dist, float radius, float ndlRaw,
                                vec4 params, vec3 worldPos) {
    if (params.w < 0.0)
        return samplePointShadow(baseAtlas, samp, toL, dist, radius, ndlRaw, params, worldPos);
    if (PointShadowFilter.x < 0.5)
        return samplePointShadowHardCombined(
            baseAtlas, transientAtlas, samp, toL, dist, radius, ndlRaw, params);
    return samplePointShadowSoftCombined(
        baseAtlas, transientAtlas, samp, toL, dist, radius, ndlRaw, params, worldPos);
}
```

- [ ] **Step 6: Bind the transient texture in every receiver family**

Declare `PointShadowTransientMap` immediately after `PointShadowMap` in model, skinned model, splat, and tile-ground layouts and shader bindings. Pass both textures to `computeLighting`. Keep the foliage vertex block shape identical even though it samples neither texture.

- [ ] **Step 7: Update backend layout expectations and regenerate evidence in this task**

Update D3D11 register and resource-set fixtures plus Vulkan binding, descriptor-budget, and vertex-layout
expectations for the appended transient texture and frame vector. The catalog program count does not change in
this task. Run the guarded writers synchronously:

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

Expected: all four commands exit 0. Review the diffs and keep only receiver programs and their changed layouts.

- [ ] **Step 8: Run receiver ABI, backend layout, corpus, and hash tests green**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~PointShadowUboLayout|FullyQualifiedName~PointShadowAtlasBind|FullyQualifiedName~PointShadowFilterShader|FullyQualifiedName~SkinnedPointShadowShader|FullyQualifiedName~ShadowLayoutReplacement|FullyQualifiedName~PointLightBuffer|FullyQualifiedName~D3D11RegisterNumbering|FullyQualifiedName~D3D11HlslRegisterAgreement|FullyQualifiedName~VulkanBindBudget|FullyQualifiedName~VulkanDescriptorLimit|FullyQualifiedName~VulkanShippedVertexLayout|FullyQualifiedName~VulkanShaderBindingTable|FullyQualifiedName~ShaderCorpus|FullyQualifiedName~ByteEquality"`

Expected: exit code 0. Earlier UBO offsets remain fixed, `UboBytes` is 1344, the base-only hard text pin still passes, pair-bind failure keeps the old pair, and generated corpus and hashes are current.

- [ ] **Step 9: Commit receiver sampling and binding**

```bash
git add KhaozEngine.Render3D/Rendering/ModelRenderer.PointShadowUniforms.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.FrameUbo.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.ShadowLayoutReplacement.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.PointLightBuffer.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.Splat.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.TileGround.cs \
  KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs \
  KhaozEngine.Render3D/Internal/ShaderSources.Model.cs \
  KhaozEngine.Render3D/Internal/ShaderSources.Terrain.cs \
  KhaozEngine.Render3D/Internal/ShaderSources.TileGround.cs \
  KhaozEngine.Render3D/Internal/ShaderSources.Foliage.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowShaderTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointShadowAtlasBindTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointShadowFilterShaderTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointShadowUboLayoutTests.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11RegisterNumberingTests.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11BindFixtures.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11HlslRegisterAgreementTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanBindBudgetTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanDescriptorLimitTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanShippedVertexLayoutTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanShaderBindingTableTests.cs \
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt \
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt
git commit -m "render3d(point-shadows): bind transient receiver atlas"
```

### Task 4: Transact transient atlas allocation, growth, shrink, and failure

**Files:**
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowReconfigureTests.cs`
- Modify: `KhaozEngine.Render3D/Rendering/PointShadowAtlas.cs`
- Modify: `KhaozEngine.Render3D/Rendering/PointShadowRenderer.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadowPass.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadowReconfigure.cs`
- Modify: `KhaozEngine.Render3D/PointShadowResolution.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/PointShadowReconfigureTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/PointShadowAtlasFailureTests.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/PointShadowSettingsTests.cs`

**Interfaces:**
- Produces: `PointShadowAtlas.ByteSize -> long`, calculated as `6L * FaceResolution * Rows * FaceResolution * 9L`.
- Produces: `PointShadowAtlas.IsCleared` internal state for first-use full clear.
- Produces: `Scene3D.RecordPointShadowTransientDemand(int requiredRows)` for render-time discovery only.
- Produces: `Scene3D.PointShadowTransientRows` internal live capacity diagnostic.
- Produces: `PointShadowResolution.BaseAtlasBytes`, `TransientAtlasRows`, `TransientAtlasBytes`, and `TotalAtlasBytes`.
- Changes: `PointShadowReplacement` carries a base atlas, one shared renderer, and an optional compatible transient atlas.
- Produces: a transient refusal latch keyed by `(faceResolution, transientRows)` independently from the base layout refusal latch.

- [ ] **Step 1: Add framebuffer failure injection**

Extend the fake factory with a counter that follows the existing texture and resource-set seams:

```csharp
internal int ThrowOnFramebufferCreate { get; set; }

public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depthTarget, params IGpuTexture[] colorTargets)
{
    if (ThrowOnFramebufferCreate == Framebuffers.Count + 1)
        throw new InvalidOperationException("the fake refused framebuffer creation");
    var framebuffer = new FakeFramebuffer(depthTarget, colorTargets);
    Framebuffers.Add(framebuffer);
    return framebuffer;
}
```

- [ ] **Step 2: Write lifecycle and memory failures**

Pin these exact sequences in `SkinnedPointShadowReconfigureTests`:

```csharp
[Fact]
public void FirstDemandIsPendingThenAllocatesExactRowsAtTheNextBoundary()
{
    using var rig = new ReconfigureRig();
    rig.WarmBase(rows: 8);
    rig.RenderStaticSkinnedCandidates(1);
    Assert.Equal(0, rig.Resolved.TransientAtlasRows);
    Assert.Equal(1, rig.LastDiagnostics.PointTransientDemand);

    rig.BeginOnly();
    Assert.Equal(1, rig.Resolved.TransientAtlasRows);
    Assert.Equal(3_538_944L, rig.Resolved.TransientAtlasBytes);
}

[Fact]
public void ExistingOneRowAtlasServesNearestWhileThreeRowGrowthIsPending()
{
    using var rig = new ReconfigureRig();
    rig.WarmTransient(rows: 1);
    rig.RenderStaticSkinnedCandidates(3);
    Assert.Equal(1, rig.LastDiagnostics.PointTransientRowsRendered);
    Assert.Equal(3, rig.LastDiagnostics.PointTransientDemand);
    Assert.Equal(1, rig.Resolved.TransientAtlasRows);
    rig.BeginOnly();
    Assert.Equal(3, rig.Resolved.TransientAtlasRows);
}

[Fact]
public void BaseShrinkToEightDropsOversizedTransientWhenReplacementAllocationFails()
{
    using var rig = new ReconfigureRig();
    rig.WarmBaseAndTransient(baseRows: 60, transientRows: 60);
    rig.FailNextTransientTextureCreate();
    rig.RequestBaseRows(8);
    rig.RenderStaticSkinnedCandidates(8);
    rig.BeginOnly();

    Assert.Equal(8, rig.Resolved.MaxShadowedLights);
    Assert.Equal(0, rig.Resolved.TransientAtlasRows);
    Assert.Equal(rig.Resolved.BaseAtlasBytes, rig.Resolved.TotalAtlasBytes);
    Assert.True(rig.Resolved.Degraded);
    Assert.Same(rig.TransientDefault, rig.BoundTransientTexture);
    Assert.True(rig.OldSixtyRowTransientDisposed);
}
```

Add separate cases for first-demand zero allocation, compatible-layout high-water retention after demand returns to zero, texture refusal, framebuffer refusal, receiver-set refusal, repeated refusal latching, changed row demand retry, changed face-resolution retry, point-shadow disable, base release, and scene disposal.

- [ ] **Step 3: Run reconfigure tests and verify failures**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowReconfigure|FullyQualifiedName~PointShadowReconfigure|FullyQualifiedName~PointShadowAtlasFailure|FullyQualifiedName~PointShadowSettings"`

Expected: exit code 1 because only the base atlas exists and resolved memory has no transient fields.

- [ ] **Step 4: Make one renderer target either atlas**

Remove the atlas field from `PointShadowRenderer`. Build its pipelines from the standard R32Float plus D32FloatS8UInt output description once. Pass the target atlas into the methods whose placement depends on it:

```csharp
public void BeginPass(IGpuCommandList cl, PointShadowAtlas atlas)
{
    cl.SetFramebuffer(atlas.Framebuffer);
    if (atlas.IsCleared) return;
    cl.ClearColorTarget(0, new Color(1f, 1f, 1f, 1f));
    cl.ClearDepthStencil(1f);
    atlas.IsCleared = true;
}

public void ClearRow(IGpuCommandList cl, PointShadowAtlas atlas, int row)
{
    uint res = (uint)atlas.FaceResolution;
    cl.SetPipeline(_clearPipeline);
    cl.SetScissorRect(0, 0, (uint)Math.Clamp(row, 0, atlas.Rows - 1) * res, atlas.Width, res);
    cl.SetGraphicsResourceSet(0, _set, 0);
    cl.Draw(3);
}
```

Apply the same explicit `atlas` argument to face scissor placement. In `Scene3D.PackPointShadowSlot`, calculate `PointShadowMath.FaceViewProjection(face, targetRow, targetAtlas.Rows, lightRender, radius)`. Never use the base row or base row count for a transient target.

- [ ] **Step 5: Add exact high-water state and byte accounting**

Use this target-capacity rule whenever the base layout changes:

```csharp
static int ResolveTransientRowsForBaseReplacement(
    int liveTransientRows, int demandRows, int newBaseRows)
{
    if (demandRows <= 0) return 0;
    return Math.Min(newBaseRows, Math.Max(liveTransientRows, demandRows));
}
```

When the base layout is unchanged, demand below live capacity does not shrink or release the transient atlas. Demand above live capacity requests the exact demand at the next boundary, capped to base rows.

Construct resolved memory from live resources:

```csharp
long baseBytes = _pointShadowAtlas?.ByteSize ?? 0L;
int transientRows = _pointShadowTransientAtlas?.Rows ?? 0;
long transientBytes = _pointShadowTransientAtlas?.ByteSize ?? 0L;
_resolvedPointShadows = new PointShadowResolution(
    enabled, faceResolution, baseRows, degraded, reason,
    baseBytes, transientRows, transientBytes);
```

`TotalAtlasBytes` returns `BaseAtlasBytes + TransientAtlasBytes`. Do not change `PointShadowSettings.AtlasBytes`.

- [ ] **Step 6: Apply build, bind, commit for independent growth and base replacement**

For independent transient growth, build the candidate atlas, bind the live base plus candidate transient to every receiver set, then commit and retire the old transient atlas.

For a base replacement, build the candidate base, shared renderer, and compatible transient candidate before touching live state. If transient allocation fails, bind the candidate base plus the white transient default and commit that degraded pair. Dispose the old transient atlas even when it was larger. If receiver binding fails, dispose every candidate and keep the complete old pair.

Latch only the refused transient `(faceResolution, rows)` shape. Clear that latch when row demand or face resolution changes.

- [ ] **Step 7: Run lifecycle and memory tests**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowReconfigure|FullyQualifiedName~PointShadowReconfigure|FullyQualifiedName~PointShadowAtlasFailure|FullyQualifiedName~PointShadowSettings|FullyQualifiedName~PointShadowAtlasBind"`

Expected: exit code 0. The 60 to eight refusal case reports no transient bytes and keeps the new base atlas live.

- [ ] **Step 8: Commit frame-boundary lifecycle**

```bash
git add KhaozEngine.Render3D/Rendering/PointShadowAtlas.cs \
  KhaozEngine.Render3D/Rendering/PointShadowRenderer.cs \
  KhaozEngine.Render3D/Scene3D.PointShadowPass.cs \
  KhaozEngine.Render3D/Scene3D.PointShadowReconfigure.cs \
  KhaozEngine.Render3D/PointShadowResolution.cs \
  KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowReconfigureTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointShadowReconfigureTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointShadowAtlasFailureTests.cs \
  KhaozEngine.Render.Tests/Render3D/PointShadowSettingsTests.cs
git commit -m "render3d(point-shadows): transact transient atlas growth"
```

### Task 5: Add shared CPU and GPU skinned point caster pipelines

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/PointShadowRenderer.Skinned.cs`
- Modify: `KhaozEngine.Render3D/Rendering/PointShadowRenderer.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.PointShadow.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs`, `VulkanShaderBindingTableTests.cs`, `VulkanDescriptorLimitTests.cs`, `VulkanLayoutCompatibilityTests.cs`, and `VulkanSpirvByteEqualityTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt` and the three HLSL, MSL, and SPIR-V pinned hash tables
- Test: `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowShaderTests.cs`

**Interfaces:**
- Produces: `PointShadowRenderer.SkinnedCasterSlotBytes = 256`.
- Produces: `PointShadowRenderer.EnsureSkinnedCasterCapacity(uint casterCount)`.
- Produces: `PointShadowRenderer.PackSkinnedCaster(uint slot, in Matrix4x4 model, float dissolveThreshold, float dissolveComplement)`.
- Produces: `PointShadowRenderer.UploadSkinnedCasters(IGpuCommandList)`.
- Produces: `PointShadowRenderer.BeginGpuSkinnedFace(IGpuCommandList, PointShadowAtlas, int packedFaceIndex, int face, int row, ShadowCastKind)`.
- Produces: `PointShadowRenderer.DrawGpuSkinnedCaster(IGpuCommandList, IGpuBuffer restVb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, uint casterSlot, IGpuResourceSet paletteSet, uint paletteOffset)`.
- Produces: `PointShadowRenderer.DrawCpuSkinnedCaster(IGpuCommandList, IGpuBuffer deformedVb, IGpuBuffer instanceBuffer, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex, uint drawIndex)`.
- Produces GLSL: `PointShadowSkinnedVert` and `PointShadowSkinnedDissolveVert` paired with the existing point distance fragments.

- [ ] **Step 1: Write shader and pipeline contract failures**

Add tests which assert both new programs compile, declare contiguous locations 0 through 6, bind face header at set 0, caster header at set 1, palette at set 2, and retain the otherwise unread normal, colour, UV, and tangent through a `1e-30` sink.

```csharp
[Fact]
public void PointSkinnedVertexUsesIndependentFaceCasterAndPaletteIndices()
{
    string source = ShaderSources.PointShadowSkinnedDissolveVert;
    Assert.Contains("layout(set=0, binding=0) uniform U", source, StringComparison.Ordinal);
    Assert.Contains("layout(set=1, binding=0) uniform Caster", source, StringComparison.Ordinal);
    Assert.Contains("layout(set=2, binding=0) uniform Palette", source, StringComparison.Ordinal);
    Assert.Contains("layout(location=4) in vec4 BoneIndices;", source, StringComparison.Ordinal);
    Assert.Contains("layout(location=5) in vec4 BoneWeights;", source, StringComparison.Ordinal);
    Assert.Contains("layout(location=6) in vec4 Tangent;", source, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run shader validation and verify missing programs**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowShader|FullyQualifiedName~ShaderSourceValidation|FullyQualifiedName~D3D11FxcValidation"`

Expected: exit code 1 because `PointShadowSkinnedVert` and `PointShadowSkinnedDissolveVert` do not exist.

- [ ] **Step 3: Add the compact caster header ring**

The ring payload is one render-relative model matrix and one dissolve vector. The face header already carries the per-light noise scale and render origin.

```csharp
[StructLayout(LayoutKind.Sequential)]
readonly struct SkinnedCasterHeader
{
    internal readonly Matrix4x4 Model;
    internal readonly Vector4 Dissolve;

    internal SkinnedCasterHeader(in Matrix4x4 model, float threshold, float complement)
    {
        Model = model;
        Dissolve = new Vector4(threshold, complement, 0f, 0f);
    }
}
```

Grow its GPU buffer geometrically, retire the outgoing buffer and set, and upload the full CPU image once before either atlas draws. A face bind uses the face dynamic offset. A caster draw uses the caster-header dynamic offset and the existing `SkinnedBonePalette.OffsetFor(casterSlot)`.

- [ ] **Step 4: Add opaque and dissolve GPU-skinned vertices**

Use the same bone blend and contiguous input sink as `SkinnedShadowDepthVert`. The opaque vertex outputs render-relative world position. The dissolve vertex additionally reconstructs absolute position from `world.xyz + Noise.yzw` and multiplies by `Noise.x` before passing the existing dissolve fields to the point fragment.

```glsl
vec4 localPos = skin * vec4(Position, 1.0);
vec4 world = Model * localPos;
float sink = Normal.x + Color.x + TexCoord.x + Tangent.x;
world.x += sink * 1e-30;
gl_Position = LightVp * world;
vWorldPos = world.xyz;
vNoisePos = (world.xyz + Noise.yzw) * Noise.x;
vDissolve = vec2(Dissolve.x, 0.0);
vDissolveComplement = Dissolve.y;
```

Reuse `PointShadowRigidFrag`, `PointShadowRigidDissolveFrag`, and `PointShadowRigidDissolveInvertedFrag`. Do not add duplicate distance fragments.

- [ ] **Step 5: Add CPU reuse entry points**

The CPU path binds the existing point rigid or dissolve pipeline and the already uploaded `_skinnedVertexBuffer` and `_skinnedInstanceBuffer`. It uses `baseVertex` and `drawIndex` exactly as `DrawCpuSkinned` does. It performs no call to `SkinningMath.SkinVertex` and no `UpdateBuffer`.

- [ ] **Step 6: Register the three shipped shader pairs and backend pipeline layouts**

Add these rows to `ShippedShaderPrograms.GraphicsPrograms()`:

```csharp
yield return new("PointShadowSkinned",
    ShaderSources.PointShadowSkinnedVert, ShaderSources.PointShadowRigidFrag);
yield return new("PointShadowSkinnedDissolve",
    ShaderSources.PointShadowSkinnedDissolveVert, ShaderSources.PointShadowRigidDissolveFrag);
yield return new("PointShadowSkinnedDissolveInverted",
    ShaderSources.PointShadowSkinnedDissolveVert, ShaderSources.PointShadowRigidDissolveInvertedFrag);
```

After the outline plan, update the graphics catalog count from 48 to 51. Add three point-skinned pipeline rows
to `VulkanDescriptorLimitTests.ShippedPipelines` and map the three program names in
`VulkanShaderBindingTableTests`. Update those pipeline counts from 42 to 45, the compatibility pair count to
`45 * 45`, and the SPIR-V emitted-stage count from 104 to 110. The point renderer still owns one pipeline family
that can bind either compatible framebuffer.

- [ ] **Step 7: Regenerate corpus and hashes for these three programs**

Run the four guarded writers synchronously:

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

Expected: all commands exit 0. Inspect the three added program keys and their generated artifact and hash rows.

- [ ] **Step 8: Run shader, pipeline, corpus, hash, and layout tests green**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowShader|FullyQualifiedName~ShaderSourceValidation|FullyQualifiedName~D3D11FxcValidation|FullyQualifiedName~D3D11HlslByteEquality|FullyQualifiedName~MetalMslByteEquality|FullyQualifiedName~VulkanSpirvByteEquality|FullyQualifiedName~VulkanShaderBindingTable|FullyQualifiedName~VulkanDescriptorLimit|FullyQualifiedName~VulkanLayoutCompatibility|FullyQualifiedName~ShaderCorpus"`

Expected: exit code 0. Source validation, FXC, corpus, pinned hashes, and backend layout tests all pass before this task commits.

- [ ] **Step 9: Commit the green caster pipeline family**

```bash
git add KhaozEngine.Render3D/Rendering/PointShadowRenderer.Skinned.cs \
  KhaozEngine.Render3D/Rendering/PointShadowRenderer.cs \
  KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs \
  KhaozEngine.Render3D/Internal/ShaderSources.PointShadow.cs \
  KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs \
  KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanShaderBindingTableTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanDescriptorLimitTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanLayoutCompatibilityTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanSpirvByteEqualityTests.cs \
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt \
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt \
  KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowShaderTests.cs
git commit -m "render3d(point-shadows): add skinned caster pipelines"
```

### Task 6: Schedule static transient and dynamic base skinned work

**Files:**
- Create: `KhaozEngine.Render3D/Scene3D.SkinnedPointShadows.cs`
- Create: `KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowSchedulingTests.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.GpuSkinning.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadows.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadowPass.cs`
- Modify: `KhaozEngine.Render3D/ShadowPassDiagnostics.cs`

**Interfaces:**
- Consumes: `PointShadowCasterSphere`, prepared point requests, base sampleability, live transient capacity, compacted CPU or GPU skinned draw records, and the shared GPU palette.
- Produces: `PointShadowCasterSet` flags with `Rigid = 1` and `Skinned = 2`.
- Produces: `Scene3D.ScheduleSkinnedPointShadows(PointShadowSlots cache, PointShadowSettings settings, int frame)`.
- Produces: `Scene3D.PrepareGpuSkinnedPointCasters(IGpuCommandList)`.
- Produces diagnostics: `PointTransientDemand`, `PointTransientRowsRendered`, `PointDynamicSkinnedDrawCalls`, and `PointStaticTransientSkinnedDrawCalls`.

- [ ] **Step 1: Write scheduling failures for both skinning modes**

```csharp
[Theory]
[InlineData(true)]
[InlineData(false)]
public void PoseOnlyStaticFramesRedrawTransientWithoutDirtyingRigidBase(bool gpuSkinning)
{
    using var rig = new SchedulingRig(gpuSkinning);
    rig.WarmStaticBaseAndTransient();
    ShadowPassDiagnostics first = rig.RenderPose(0.15f);
    ShadowPassDiagnostics second = rig.RenderPose(0.65f);

    Assert.Equal(0, first.PointStaticRebuilds);
    Assert.Equal(0, second.PointStaticRebuilds);
    Assert.Equal(1, first.PointTransientRowsRendered);
    Assert.Equal(1, second.PointTransientRowsRendered);
    Assert.True(second.PointStaticTransientSkinnedDrawCalls > 0);
}

[Theory]
[InlineData(true)]
[InlineData(false)]
public void DynamicSkinnedCasterUsesBaseRowAndNeverTransientRow(bool gpuSkinning)
{
    using var rig = new SchedulingRig(gpuSkinning);
    ShadowPassDiagnostics diagnostics = rig.RenderDynamicSkinned();
    Assert.Equal(1, diagnostics.PointDynamicRenders);
    Assert.True(diagnostics.PointDynamicSkinnedDrawCalls > 0);
    Assert.Equal(0, diagnostics.PointTransientRowsRendered);
    Assert.Equal(-1, rig.TransientRowForLight(0));
}
```

Add `DirtyPreviouslyRenderedStaticRowDeferredByRigidBudgetStillRendersCurrentTransient`. Warm a static base row, move a rigid caster so the row becomes dirty, set `MaxStaticRebuildsPerFrame = 0`, change the skinned pose, and assert the old base row remains sampleable while `PointTransientRowsRendered == 1` and `PointStaticRebuilds == 0`.

Add the three Review Focus cases from this task by exact name. Also assert that `castsShadows: false` prevents point-only retention, that a partial near-radius or exclusion overlap stays, and that a large render origin changes neither selection nor packed render-relative values.

- [ ] **Step 2: Run scheduling tests and verify no skinned work exists**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowScheduling"`

Expected: exit code 1 because the diagnostics and scheduling owner do not exist.

- [ ] **Step 3: Carry the same absolute sphere in both compacted draw records**

Add `PointShadowCasterSphere PointSphere` to `GpuSkinnedDraw` and `CpuSkinnedDraw`. Construct it before rebasing `it.World`. Keep `World` render-relative in the GPU record and `InstanceData.Model` render-relative in the CPU record.

Keep a draw when `visibleMain || visibleShadow || visiblePointShadow`. An opted-out draw may still survive for main rendering but may not survive solely for point shadows.

- [ ] **Step 4: Build static candidates after base slot acquisition**

For each static request, require a valid row and `cache.EverRendered(request.Slot)`. Scan only casting skinned records and use their stored absolute sphere against the request shell. Record one candidate per affected request. Assign dense rows through `PointShadowTransientRows.Assign`, using `_pointShadowTransientAtlas?.Rows ?? 0` as capacity. Record full demand even when live capacity is smaller.

For each dynamic request, require `cache.LastRenderedFrame(request.Slot) == frame`. Publish transient row `-1` and mark its packed base row as `Rigid | Skinned`.

- [ ] **Step 5: Pack independent target layouts**

Represent each render item with target atlas, target row, request index, and caster flags:

```csharp
[Flags]
enum PointShadowCasterSet
{
    Rigid = 1,
    Skinned = 2,
}

readonly record struct PackedPointShadowSlot(
    int PackedIndex,
    PointShadowAtlas Atlas,
    int Row,
    int RequestIndex,
    PointShadowCasterSet Casters);
```

Static base rebuilds use `Rigid`. Dynamic base renders use `Rigid | Skinned`. Static transient renders use `Skinned`. Clear each packed row once, pack faces with that item's atlas row count, and change framebuffers only when the target atlas changes.

- [ ] **Step 6: Reuse CPU deformation and the shared GPU palette**

Before point faces, pack one GPU point-caster header per retained casting draw and upload the header ring once. `PrepareGpuSkinnedFrame` must upload the palette whenever a GPU draw survives for main, key-light, or point work. Do not require key-light shadows to be active.

For each light face, draw only skinned records whose stored sphere touches that light shell. Route opaque, dissolving, and dissolving-inverted kinds through `ShadowDepthSelection`. The CPU loop uses its existing `BaseVertex` and compacted draw index. The GPU loop uses the face slot, caster-header slot, and shared palette slot independently.

- [ ] **Step 7: Publish rows and diagnostics after successful scheduling**

Write base and transient arrays together. A static row not yet rendered publishes both `-1`. A dirty static row that rendered in an earlier frame keeps its base row and may receive a current transient row. Update diagnostics without changing `PointStaticRebuilds` semantics.

- [ ] **Step 8: Run focused scheduling and regression tests**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadowScheduling|FullyQualifiedName~PointShadowReconfigure|FullyQualifiedName~PointShadowResidency|FullyQualifiedName~ShadowCasterPolicy|FullyQualifiedName~Scene3DSkinnedQueue"`

Expected: exit code 0 for CPU and GPU scheduling. Pose-only static changes leave `PointStaticRebuilds` at zero after the initial rigid render.

- [ ] **Step 9: Commit integrated scheduling and diagnostics**

```bash
git add KhaozEngine.Render3D/Scene3D.SkinnedPointShadows.cs \
  KhaozEngine.Render3D/Scene3D.cs \
  KhaozEngine.Render3D/Scene3D.GpuSkinning.cs \
  KhaozEngine.Render3D/Scene3D.PointShadows.cs \
  KhaozEngine.Render3D/Scene3D.PointShadowPass.cs \
  KhaozEngine.Render3D/ShadowPassDiagnostics.cs \
  KhaozEngine.Render.Tests/Render3D/SkinnedPointShadowSchedulingTests.cs
git commit -m "render3d(point-shadows): schedule skinned caster rows"
```

### Task 7: Prove static, dynamic, policy, coordinate, and byte-identity pixels

**Files:**
- Create: `KhaozEngine.Render.Tests/Gpu/SkinnedPointShadowScene.cs`
- Create: `KhaozEngine.Render.Tests/Gpu/SkinnedPointShadowGoldenTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/PointShadowByteIdentityGpuTests.cs`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.metal-native.txt`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.direct3d11-native.txt`
- Create: `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.vulkan-native.txt`

**Interfaces:**
- Consumes: public `Scene3D.DrawSkinned`, `UseGpuSkinning`, `LightShadow.Static`, `LightShadow.Dynamic`, `castsShadows`, dissolve overloads, `RenderOrigin`, `ResolvedPointShadows`, and `LastShadowPassDiagnostics`.
- Produces: one lazy class fixture with reusable floor, rigid box, and bent skinned tube meshes.
- Produces: committed golden name `scene3d_skinned_point_shadow_static`.
- Produces: a fresh-versus-aged scene parity test required for a fixture with many captures.
- Produces: `SkinnedPointShadowScene.ByteIdentityPair(byte[] BaseOnly, byte[] Candidate, int TransientRows, int DynamicSkinnedDrawCalls, int TransientRow)`.

- [ ] **Step 1: Build the shared scene fixture**

Create one lazy `GpuDeviceContext`, target, framebuffer, command list, and `Scene3D`. Load a neutral floor, a rigid box, and `SkinnedMeshBuilder.BuildTube(0.55f, 2.2f, 8, 16, 4, Axis.Y)`. Expose captures that warm frame-boundary allocations explicitly and return pixels, resolved memory, base and transient row diagnostics, and `ShadowPassDiagnostics`.

Use a bent pose that rotates the upper two tube bones enough that its cast silhouette differs from rest. Use a near-black environment with key, fill, HDR, and cel banding disabled so the point light owns the measured ground luminance.

- [ ] **Step 2: Write the static pixel and cache-isolation failure**

```csharp
[GpuFact]
public void StaticPixelsShowRigidAndBentSkinnedShadowsWhileRigidBaseStaysCached()
{
    IReadOnlyList<SkinnedPointShadowScene.Shot> shots = fixture.StaticRigidAndBentSkinned(frames: 3);
    Assert.Equal(1, shots[0].Diagnostics.PointStaticRebuilds);
    Assert.Equal(0, shots[1].Diagnostics.PointStaticRebuilds);
    Assert.Equal(0, shots[2].Diagnostics.PointStaticRebuilds);
    Assert.True(shots[1].Diagnostics.PointTransientRowsRendered > 0);
    Assert.True(shots[2].Diagnostics.PointStaticTransientSkinnedDrawCalls > 0);
    Assert.True(shots[2].RigidShadowLuminance < shots[2].LitLuminance * 0.8f);
    Assert.True(shots[2].SkinnedShadowLuminance < shots[2].LitLuminance * 0.8f);
}
```

Expected before implementation: the skinned probe stays bright and no transient diagnostics increment.

- [ ] **Step 3: Write the independent base-row and transient-row pixel proof**

Use two keyed static lights. Give the first light only a rigid caster and the second light only the bent skinned caster. Stable key order must give the second light base row 1. It is the only transient candidate, so it gets transient row 0.

```csharp
[GpuTheory]
[InlineData(true)]
[InlineData(false)]
public void BaseRowOneAndTransientRowZeroLandUnderTheSecondLight(bool gpuSkinning)
{
    var shot = fixture.BaseOneTransientZero(gpuSkinning);
    Assert.Equal(1, shot.SecondLightBaseRow);
    Assert.Equal(0, shot.SecondLightTransientRow);
    Assert.True(shot.FirstRigidShadow < shot.FirstLitReference * 0.8f);
    Assert.True(shot.SecondSkinnedShadow < shot.SecondLitReference * 0.8f);
}
```

- [ ] **Step 4: Write the dynamic base-row proof**

```csharp
[GpuTheory]
[InlineData(true)]
[InlineData(false)]
public void DynamicLightDrawsRigidAndBentSkinnedCastersIntoBaseOnly(bool gpuSkinning)
{
    var shot = fixture.DynamicRigidAndSkinned(gpuSkinning);
    Assert.Equal(-1, shot.DynamicTransientRow);
    Assert.Equal(0, shot.Diagnostics.PointTransientRowsRendered);
    Assert.True(shot.Diagnostics.PointDynamicSkinnedDrawCalls > 0);
    Assert.True(shot.RigidShadowLuminance < shot.LitLuminance * 0.8f);
    Assert.True(shot.SkinnedShadowLuminance < shot.LitLuminance * 0.8f);
}
```

Because the class name contains `Golden`, the static mapping and dynamic property proofs run on `metal-native`, `direct3d11-native`, and `vulkan-native` push legs.

- [ ] **Step 5: Write policy and culling pixel proofs**

For both skinning modes, render solid, half-dissolved, and opted-out variants. Require the opted-out body pixels to remain visible while its shadow disappears. Require the half dissolve to preserve between 20 and 90 percent of the solid shadow darkening.

Add an off-camera caster whose shadow reaches an on-camera receiver while key-light shadows are off. Assert the caster centre projects outside the image and the receiver darkens by at least 20 percent.

Add near-radius and exclusion-box cases with one wholly excluded caster and one partial caster. The wholly excluded case records no skinned point draw. The partial case records a draw and retains shadow fragments outside the clearance.

- [ ] **Step 6: Write large-origin and no-skinned identity proofs**

Capture the same static and dynamic scenes at the origin and translated by `(100_000, 0, -100_000)` with matching `RenderOrigin`. Compare coverage grids within `GoldenCompare.InSessionTolerance`. Repeat the half-dissolve capture so both the caster mask and receiver tap rotation are covered.

Extend `PointShadowByteIdentityGpuTests` with two byte-exact captures:

```csharp
[GpuFact]
public void LiveButUnmappedTransientAtlasMatchesBaseOnlyPixelsByteForByte()
{
    // Warm a static-light transient atlas, then render a dynamic light with a skinned caster.
    // Its skinned work lands in the base row and publishes no transient row.
    SkinnedPointShadowScene.ByteIdentityPair pair = fixture.DynamicSkinnedWithIdleTransient();
    Assert.True(pair.TransientRows > 0);
    Assert.True(pair.DynamicSkinnedDrawCalls > 0);
    Assert.Equal(-1, pair.TransientRow);
    Assert.Equal(pair.BaseOnly, pair.Candidate);
}

[GpuFact]
public void FreshSceneWithNoSkinnedCasterMatchesBaseOnlyPixelsByteForByte()
{
    SkinnedPointShadowScene.ByteIdentityPair pair = fixture.NoSkinnedColdScene();
    Assert.Equal(0, pair.TransientRows);
    Assert.Equal(0, pair.DynamicSkinnedDrawCalls);
    Assert.Equal(pair.BaseOnly, pair.Candidate);
}
```

- [ ] **Step 7: Add shared-scene parity**

Age the fixture through static, dynamic, CPU, GPU, dissolve, no-demand, one-row, and three-row configurations. Compare the final capture and diagnostics with a fresh fixture configured directly to the same final scene. Use byte equality for pixels and value equality for all public diagnostics.

- [ ] **Step 8: Run the local Metal GPU suite**

Run:

```bash
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedPointShadowGolden|FullyQualifiedName~PointShadowByteIdentity" \
  --logger "console;verbosity=detailed"
```

Expected: exit code 0, every property assertion passes, and the output reports zero skipped filtered GPU tests. The committed-grid assertion is added in Task 8 after this task's green property-test commit.

- [ ] **Step 9: Commit the green GPU property tests before adding a golden**

```bash
git add KhaozEngine.Render.Tests/Gpu/SkinnedPointShadowScene.cs \
  KhaozEngine.Render.Tests/Gpu/SkinnedPointShadowGoldenTests.cs \
  KhaozEngine.Render.Tests/Gpu/PointShadowByteIdentityGpuTests.cs
git commit -m "test(point-shadows): prove skinned caster pixels"
```

### Task 8: Verify backend contracts and bake three golden families

**Files:**
- Modify: `KhaozEngine.Render.Tests/Gpu/D3D11RegisterNumberingTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/D3D11BindFixtures.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/D3D11HlslRegisterAgreementTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanBindBudgetTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanDescriptorLimitTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/VulkanShippedVertexLayoutTests.cs`
- Modify: `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt`
- Modify: `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt`
- Create: three `scene3d_skinned_point_shadow_static.<backend>.txt` files listed in Task 7.

**Interfaces:**
- Consumes: `ShippedShaderPrograms` rows and all changed receiver layouts.
- Produces: Direct3D 11 register tables containing `PointShadowTransientMap` immediately after `PointShadowMap`.
- Produces: Direct3D 11 point-skinned vertex signatures with contiguous `TEXCOORD0` through `TEXCOORD6` inputs.
- Produces: Vulkan descriptor limits and shipped layouts that include the additional receiver texture and the point-skinned pipeline sets.
- Produces: verified MSL, HLSL, SPIR-V, and corpus records from Tasks 3 and 5, plus three backend golden records.

- [ ] **Step 1: Audit backend layout expectations from Tasks 3 and 5**

Confirm every model, skinned, splat, and tile-ground expected binding sequence already has
`PointShadowTransientMap` after `PointShadowMap`. Confirm the point-skinned programs are in D3D11 register
agreement and Vulkan shipped-layout coverage. The Vulkan texture and descriptor counts must include the added
receiver texture. A missing row is a Task 3 or Task 5 defect to fix there before continuing.

The D3D11 assertion must hold point-skinned vertex input semantics contiguous. The Vulkan assertion must show
set 0 as face UBO, set 1 as caster UBO, and set 2 as palette UBO for both point-skinned vertex programs.

- [ ] **Step 2: Run backend contract tests green before any golden bake**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~D3D11RegisterNumbering|FullyQualifiedName~D3D11HlslRegisterAgreement|FullyQualifiedName~D3D11FxcValidation|FullyQualifiedName~VulkanBindBudget|FullyQualifiedName~VulkanDescriptorLimit|FullyQualifiedName~VulkanShippedVertexLayout|FullyQualifiedName~ShaderCorpus|FullyQualifiedName~ByteEquality"`

Expected: exit code 0. Register, FXC, descriptor, vertex-layout, corpus, and all three byte-equality tables pass.

- [ ] **Step 3: Verify the live shader corpus without writing it**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~ShaderCorpusTests.TheCommittedCorpus_HasARowForEveryShippedProgramAndNothingElse"`

Expected: exit code 0 and no corpus diff. The two immutable migration tables remain byte-identical.

- [ ] **Step 4: Verify the three pinned shader hash tables without writing them**

Run each command synchronously:

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~D3D11HlslByteEqualityTests.EveryShippedProgramsEmittedHlsl_MatchesItsPinnedHash"
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~MetalMslByteEqualityTests.EveryShippedProgramsEmittedMsl_MatchesItsPinnedHash"
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~VulkanSpirvByteEqualityTests.EveryShippedProgramsSpirv_MatchesItsPinnedHash"
```

Expected: each command exits 0 and changes no hash table. Tasks 3 and 5 already committed the changed receiver
and point-skinned program rows.

- [ ] **Step 5: Confirm generated evidence is clean before the GPU bake**

Run: `git diff --exit-code -- KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt`

Expected: exit code 0 with no pending generated evidence.

- [ ] **Step 6: Add the committed-grid assertion and push a bake seed**

Add this method to `SkinnedPointShadowGoldenTests` after the property-only commit from Task 7:

```csharp
[GpuFact]
public void StaticBentCasterGoldenMatchesCommittedGrid()
{
    SkinnedPointShadowScene.Shot shot = fixture.StaticRigidAndBentSkinned(frames: 3)[2];
    GoldenCompare.AssertOrUpdate(
        "scene3d_skinned_point_shadow_static", shot.Rgba,
        SkinnedPointShadowScene.Width, SkinnedPointShadowScene.Height);
}
```

Commit and push this bake seed on the task branch only:

```bash
git add KhaozEngine.Render.Tests/Gpu/SkinnedPointShadowGoldenTests.cs
git commit -m "test(point-shadows): seed the skinned golden bake"
git push origin "$(git branch --show-current)"
```

A normal verify run on that seed reports a missing golden
until the next step supplies all three backend families. Do not integrate the seed commit or move to another task
in that state.

- [ ] **Step 7: Bake all three backend golden files through the owning CI legs**

The owning orchestrator pushes the task branch, then runs:

```bash
gpu_branch=$(git branch --show-current)
gh workflow run cross-platform-gpu.yml --ref "$gpu_branch" \
  -f bake=true \
  -f legs=all \
  -f tier=push \
  -f renderTestFilter='FullyQualifiedName~SkinnedPointShadowGolden'
gpu_run_id=$(gh run list --workflow cross-platform-gpu.yml --branch "$gpu_branch" \
  --event workflow_dispatch --limit 1 --json databaseId --jq '.[0].databaseId')
gh run watch "$gpu_run_id" --exit-status
gpu_artifact_dir=$(mktemp -d)
gh run download "$gpu_run_id" --pattern 'goldens-*' --dir "$gpu_artifact_dir"
find "$gpu_artifact_dir" -name 'scene3d_skinned_point_shadow_static.*.txt' \
  -exec cp {} KhaozEngine.Render.Tests/Gpu/goldens/ \;
```

Expected: workflow exit status 0. Exactly one new golden appears for each of `metal-native`, `direct3d11-native`, and `vulkan-native`. Inspect each bake PNG and confirm the rigid and bent skinned shadows are both visible.

- [ ] **Step 8: Verify the new golden against local Metal**

Run:

```bash
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SkinnedPointShadowGolden" \
  --logger "console;verbosity=detailed"
```

Expected: exit code 0 with zero skipped filtered tests.

- [ ] **Step 9: Commit backend evidence and finish this task green**

```bash
git add KhaozEngine.Render.Tests/Gpu/D3D11RegisterNumberingTests.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11BindFixtures.cs \
  KhaozEngine.Render.Tests/Gpu/D3D11HlslRegisterAgreementTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanBindBudgetTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanDescriptorLimitTests.cs \
  KhaozEngine.Render.Tests/Gpu/VulkanShippedVertexLayoutTests.cs \
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt \
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt \
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.metal-native.txt \
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.direct3d11-native.txt \
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_skinned_point_shadow_static.vulkan-native.txt
git commit -m "test(shaders): pin skinned point shadow programs"
```

- [ ] **Step 10: Verify all three committed golden families without rebaking**

Push the completed task branch and dispatch `cross-platform-gpu.yml` with `bake=false`, `legs=all`,
`tier=push`, and `renderTestFilter='FullyQualifiedName~SkinnedPointShadowGolden'`. Watch the run with
`gh run watch --exit-status` and require exit code 0 with zero skipped filtered GPU tests on Metal,
Direct3D 11, and Vulkan. This is the green task boundary after the temporary bake seed.

```bash
gpu_branch=$(git branch --show-current)
git push origin "$gpu_branch"
gh workflow run cross-platform-gpu.yml --ref "$gpu_branch" \
  -f bake=false -f legs=all -f tier=push \
  -f renderTestFilter='FullyQualifiedName~SkinnedPointShadowGolden'
gpu_run_id=$(gh run list --workflow cross-platform-gpu.yml --branch "$gpu_branch" \
  --event workflow_dispatch --limit 1 --json databaseId --jq '.[0].databaseId')
gh run watch "$gpu_run_id" --exit-status
```

### Task 9: Document, version, reconcile, and run the full Release gate

**Files:**
- Modify: `KhaozEngine.Render3D/README.md`
- Modify: `docs/USING-KHAOZENGINE.md`
- Modify: `docs/INDEX.md`
- Modify: `docs/design/SKINNED-POINT-SHADOWS-DESIGN-2026-09-23.md`
- Modify: `Directory.Build.props` when no version is staged.
- Modify: `CHANGELOG.md`.
- Modify: `README.md` current-version examples when the version changes.
- Modify: `docs/USING-KHAOZENGINE.md` current-version examples when the version changes.

**Interfaces:**
- Documents: default skinned caster behavior, static base plus transient atlas ownership, dynamic base behavior, live memory diagnostics, counters, failure degradation, and unchanged settings floor accounting.
- Preserves: no public draw API change and no new point-shadow quality field.
- Produces: one package version and matching newest changelog entry.

- [ ] **Step 1: Re-read version and tags after fetching current main**

```bash
git fetch origin
git merge --no-edit origin/main
. scripts/tag-standard.sh
engine_version=$(tag_props_version < Directory.Build.props)
latest_release=$(git tag --list 'v*' --sort=-version:refname | head -n 1 | sed 's/^v//')
printf 'engine=%s latest=%s\n' "$engine_version" "$latest_release"
```

Expected: merge exit code 0. Resolve overlaps in favor of both the outline feature and this plan. If `engine_version` is ahead of `latest_release`, use it. If they match at the current baseline `20.1.0`, take the next free minor, currently `20.2.0`.

- [ ] **Step 2: Update living documentation**

In `KhaozEngine.Render3D/README.md` and `docs/USING-KHAOZENGINE.md`, state these exact contracts:

- `DrawSkinned` and dissolved variants cast into point shadows by default on CPU and GPU skinning.
- Static lights keep rigid rows cached and draw current skinned poses into compact transient rows.
- Dynamic lights draw rigid and skinned casters together in the selected base row.
- `PointShadowSettings.AtlasBytes` remains the configured base floor.
- `ResolvedPointShadows.BaseAtlasBytes`, `TransientAtlasRows`, `TransientAtlasBytes`, and `TotalAtlasBytes` report live memory.
- `PointStaticRebuilds` still counts rigid static base rebuilds only.
- `PointTransientDemand`, `PointTransientRowsRendered`, `PointDynamicSkinnedDrawCalls`, and `PointStaticTransientSkinnedDrawCalls` expose current skinned work.
- Texture, framebuffer, or receiver-set refusal preserves rigid point shadows and reports degradation.
- Point MASK alpha testing remains tracked by #1098. The preceding outline round adds skinned colour and outline cutout, while key-light skinned cutout remains #1097.

Update `docs/INDEX.md` and the design status to implemented for the selected staged version and link #1054.

- [ ] **Step 3: Add the player-facing changelog and version declaration**

If no version is staged, change `Directory.Build.props` from `20.1.0` to the next free minor, currently `20.2.0`, and update every guarded current-version `PackageReference` example in `README.md` and `docs/USING-KHAOZENGINE.md`. Add the newest changelog heading for that same version.

The changelog entry must say that skinned characters now cast placed and dynamic point-light shadows, static rigid maps stay cached while poses animate, CPU and GPU skinning agree, and resolved point-shadow diagnostics expose the transient memory cost.

- [ ] **Step 4: Run the focused Release suite after reconciliation**

Run: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkinnedPointShadow|FullyQualifiedName~PointShadow|FullyQualifiedName~PointLightBuffer|FullyQualifiedName~ShaderSourceValidation|FullyQualifiedName~D3D11|FullyQualifiedName~Vulkan"`

Expected: exit code 0.

- [ ] **Step 5: Run repository guards**

Run each synchronously and record its exit code:

```bash
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: every command exits 0. No file-size baseline grows.

- [ ] **Step 6: Run the full Release verification**

```bash
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
```

Expected: both commands exit 0 with zero warnings. The full test output reports the normal GPU skips when `KE_GPU_TESTS` is unset. GPU evidence is supplied separately by Task 8.

- [ ] **Step 7: Pack through the guarded local-feed script**

Run: `scripts/pack-local-feed.sh`

Expected: exit code 0. Do not run a bare `dotnet pack` into `local-feed`.

- [ ] **Step 8: Commit release documentation and version**

```bash
. scripts/tag-standard.sh
engine_version=$(tag_props_version < Directory.Build.props)
git add Directory.Build.props CHANGELOG.md README.md \
  KhaozEngine.Render3D/README.md docs/USING-KHAOZENGINE.md docs/INDEX.md \
  docs/design/SKINNED-POINT-SHADOWS-DESIGN-2026-09-23.md
git commit -m "release(${engine_version}): add skinned point shadows"
```

If a staged version already owns `Directory.Build.props` and the newest changelog heading, extend that entry and include only the files actually changed. Do not create a release tag.

- [ ] **Step 9: Hand the verified branch to the owning orchestrator**

Report every focused and full command with its exit code, the three backend GPU run URL, the selected engine version, and the exact commits. The owning orchestrator performs branch integration and pushes `main` under the repository workflow.

## Grimhollow Acceptance Handoff

This engine plan stops before changing the consumer. After the owner creates the engine release tag and the package is available, the Grimhollow adoption task updates its engine pin and matching tool pins, runs `scripts/refresh-engine.sh`, records the swept engine range in `docs/ENGINE-INTEGRATION.md`, and completes that repository's Release verification.

The visual acceptance command is:

```bash
cd /Users/antonio/Grimhollow
dotnet run --project tools/SnapshotTool -c Release -- shadowprobe /tmp/grimhollow-shadowprobe-shots
```

Expected: exit code 0. Open `/tmp/grimhollow-shadowprobe-shots/shadowprobe-both-shadowed.png`. The rigid kit avatar and bent skinned tube both cast visible lantern shadows. The tool must report a shadowed light and save all four probe shots. The live skinned avatar switch remains gated on this result.

## Task Map

| Task | Reviewable outcome | Depends on |
| --- | --- | --- |
| 1 | Point requests exist before skinned compaction and one absolute conservative sphere owns retention | None |
| 2 | Static candidates receive deterministic dense transient rows and all structured records carry row four | Task 1 |
| 3 | Receivers bind an atomic texture pair and use per-tap minimum while preserving the base-only path | Task 2 |
| 4 | `Begin` owns exact high-water allocation, shrink capping, refusal latching, fallback, and live memory | Task 3 |
| 5 | One point renderer draws CPU and GPU skinned casters into either atlas | Task 4 |
| 6 | Static transient and dynamic base scheduling record current poses with split diagnostics | Tasks 1 through 5 |
| 7 | Pixel readback proves static, mapping, dynamic, policy, culling, origin, and byte identity | Task 6 |
| 8 | Three backend shader and golden evidence is committed | Task 7 |
| 9 | Living docs, version, changelog, full Release suite, and pack are complete | Task 8 |

## Unresolved Questions

None. The owner-approved design chooses no transient row budget. #1054 owns this implementation. #1097 and #1098 remain separate alpha-cutout work. The release tag remains an owner action.
