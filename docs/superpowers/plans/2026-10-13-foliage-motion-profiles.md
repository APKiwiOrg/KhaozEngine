# Foliage Motion Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-instance foliage motion profiles, movement-driven leaf shiver and animated cascade shadows while preserving existing grass rendering.
**Architecture:** Render3D owns the default-safe profile, retained instance stream, agitation helper and one GLSL displacement function shared by colour, temporal motion and cascade depth. Terrain resolves model policies before multipart expansion, and TileWorld resolves validated archetype styles into those policies. Cascade foliage stays in a separate per-frame caster set, while point shadows consume immutable rest transforms through the existing rigid depth renderer.
**Tech Stack:** C#/.NET 10, System.Numerics, xUnit, native Metal/Direct3D11/Vulkan GPU seams, GLSL 450 and SPIRV-Cross.
**Spec:** docs/design/FOLIAGE-MOTION-PROFILES-DESIGN-2026-10-13.md

## Resolved question (orchestrator ruling S7)

The drafted spec gave each of the four interactors its own shiver amplitude, so four colocated interactors could sum to four times the `Shiver * height` allowance the bounds use. Ruling S7, recorded in the spec: the shiver intensity is the strongest single interactor's `falloff * strength * agitation`, one oscillation per vertex is scaled by it, and the total shiver therefore never exceeds `Shiver * height`. Two players in one shrub do not make it shake harder than one fully agitated player. The bounds formula in Task 5 stands unchanged.

## Global Constraints

- `FoliageMotionProfile.Blade = (BendScale: 1, MaxLean: 0.65, WindScale: 1, Shiver: 0)`.
- Valid finite ranges are `BendScale 0..4`, `MaxLean 0..1`, `WindScale 0..4`, `Shiver 0..0.25`.
- `FoliageInteractor.Agitation` is finite, `0..1`, default `0`.
- `FoliageAgitation` defaults are reference speed `2 m/s` and settle time `0.6 s`.
- Shiver angular frequency is the documented constant `14 rad/s`, about `2.2 Hz`.
- Retained instance stride grows from `80` to `96` bytes. Profile occupies byte offset `80` and vertex location `10`.
- Every existing foliage golden remains unchanged on `metal-native`, `direct3d11-native` and `vulkan-native`.
- This additive work rides staged `20.34.0`. Do not bump the pin, release or tag from an implementation task.
- Warnings are errors. Do not suppress them or change the KESIZE baseline upward.
- No em-dash or en-dash glyphs, and no prose semicolons.
- All local builds, tests, shader-table generation and renders use `build-slot --label <what> [--heavy] -- <cmd>`. No loops or local stress runs.
- Every shell starts with `cd /Users/antonio/KhaozEngine/.worktrees/foliage-motion-profiles && test "$(git branch --show-current)" = feature/foliage-motion-profiles || exit 1`.
- Line ranges below refer to `3428b8bbb`. Preserve unrelated changes and stage explicit paths. The orchestrator owns reconciliation, integration, pushes and release work.

## Review Focus

- Default-constructed profiles and deliberately authored zero coefficients must remain distinguishable, with field-named rejection of NaN and infinities. Task 1 adds `DefaultIsBladeButAuthoredZeroStaysZero` and `InvalidCoefficientNamesItsField`.
- In-place model-policy edits, removal back to defaults and multipart models must invalidate one retained snapshot without leaking policy to another model. Task 4 adds `PolicyMutationAndRemovalRefreshOnlyTheEffectiveSnapshot`.
- A paused frame, invalid frame time or vertical relocation must neither divide by zero nor generate false horizontal agitation. Task 3 adds `ZeroTimeRebasesWithoutAgitatingAndInvalidInputPreservesState` and `VerticalMotionDoesNotAgitate`.
- A caster visible only to a shadow, then disposed or removed, must upload on its first shadow frame and leave no cached ghost. Task 5 adds `OffCameraCasterUploadsAndItsRemovalClearsTheAtlasOnce`.
- Two archetypes sharing a mesh, including one with an omitted style, must compare effective styles and reject conflicts before changing the live split. Task 7 adds `SharedMeshComparesImplicitBladeAndExplicitStyles`.

---

### Task 1: Default-safe FoliageMotionProfile and instance carrier

**Files:**
- Create: `KhaozEngine.Render3D/FoliageMotionProfile.cs`.
- Modify: `KhaozEngine.Render3D/FoliageInstance.cs:5-6`.
- Test, create: `KhaozEngine.Render.Tests/Render3D/FoliageMotionProfileTests.cs`.

**Interfaces:**
- Consumes: `MeshHandle`, `Matrix4x4`, `Vector4`.
- Produces: `public readonly record struct FoliageMotionProfile`, constructor `FoliageMotionProfile(float BendScale, float MaxLean, float WindScale, float Shiver)`, get-only `float BendScale`, `MaxLean`, `WindScale`, `Shiver`, `static FoliageMotionProfile Blade`, `internal Vector4 ToVector4()`.
- Produces: `FoliageInstance(MeshHandle Mesh, Matrix4x4 Transform, float ThinningRank, FoliageMotionProfile Motion = default, bool CastsShadows = false)` and the existing three-output `void Deconstruct(out MeshHandle Mesh, out Matrix4x4 Transform, out float ThinningRank)` compatibility overload.

- [ ] Write `DefaultIsBladeButAuthoredZeroStaysZero`, `PackingPreservesTheFourCoefficients`, `InvalidCoefficientNamesItsField` and `ExistingInstanceConstructionKeepsBladeAndNoCasting`.

```csharp
Assert.Equal(new Vector4(1f, .65f, 1f, 0f), default(FoliageMotionProfile).ToVector4());
Assert.Equal(default, FoliageMotionProfile.Blade);
Assert.Equal(Vector4.Zero, new FoliageMotionProfile(0f, 0f, 0f, 0f).ToVector4());
Assert.Equal(new Vector4(.3f, .15f, .4f, .035f),
    new FoliageMotionProfile(.3f, .15f, .4f, .035f).ToVector4());
Assert.Equal(new Vector4(4f, 1f, 4f, .25f),
    new FoliageMotionProfile(4f, 1f, 4f, .25f).ToVector4());
var instance = new FoliageInstance(new MeshHandle(4), Matrix4x4.Identity, .1f);
Assert.Equal(FoliageMotionProfile.Blade, instance.Motion);
Assert.False(instance.CastsShadows);
var (mesh, transform, rank) = instance;
Assert.Equal((instance.Mesh, instance.Transform, instance.ThinningRank), (mesh, transform, rank));
```

The validation theory substitutes each coefficient independently with `-.01f`, its upper bound plus `.01f`, `float.NaN`, `float.PositiveInfinity` and `float.NegativeInfinity`. Assert `ArgumentOutOfRangeException` and `ParamName` exactly `BendScale`, `MaxLean`, `WindScale` or `Shiver`. Include zero and each inclusive upper bound as accepted cases.

- [ ] Run the red test.

```bash
build-slot --label foliage-profile-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FoliageMotionProfileTests"
```

Expected: missing `FoliageMotionProfile`, `Motion` and the optional carrier members prevent compilation.

- [ ] Implement the profile in its new file using a nullable coefficient carrier inside the value type, `private readonly Vector4? _coefficients`. Absence resolves to the exact Blade vector in every property and `ToVector4()`. Validate constructor inputs, store their exact float values, and normalize an explicitly supplied Blade vector to the absent representation. `Blade` returns `default`, so value equality also agrees for both Blade constructions. An explicitly constructed all-zero vector remains present and therefore stays zero. This mechanism makes both an omitted instance profile and `default(FoliageMotionProfile)` safe without coefficient roundoff or a public nullable value that downstream code must unwrap.
- [ ] Extend `FoliageInstance` with the two optional parameters and preserve its old deconstruction overload. Casting is carried now so Terrain can produce the complete instance before the shadow implementation arrives.
- [ ] Run the same filtered command with label `foliage-profile-green`. Expected: every profile test passes without a GPU.
- [ ] Commit only these paths.

```bash
task_files=(KhaozEngine.Render3D/FoliageMotionProfile.cs KhaozEngine.Render3D/FoliageInstance.cs KhaozEngine.Render.Tests/Render3D/FoliageMotionProfileTests.cs)
git add -- "${task_files[@]}"
git commit --only -m "feat(foliage): add default-safe motion profiles" -- "${task_files[@]}"
```

### Task 2: Instance layout and one shared displacement function

**Files:**
- Create: `KhaozEngine.Render3D/Internal/FoliageVertexLayouts.cs`, `KhaozEngine.Render3D/Internal/ShaderSources.FoliageDisplacement.cs`.
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.Foliage.cs:12-18,143-159`, `KhaozEngine.Render3D/Scene3D.Foliage.cs:53-70,153-173`.
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Foliage.cs:20-38,50-93`, `KhaozEngine.Render3D/Internal/ShaderSources.MotionFoliage.cs:15-38,51-102`.
- Modify: `KhaozEngine.Render.Tests/Render3D/MotionShaderTextTests.cs:111-150`, `KhaozEngine.Render.Tests/Render3D/FoliageUniformsTests.cs:14-33`, `KhaozEngine.Render.Tests/Gpu/FoliageWindMirror.cs:15-57`, `KhaozEngine.Render.Tests/Gpu/FoliageGpuTests.cs:16-32`.
- Test, create: `KhaozEngine.Render.Tests/Render3D/FoliageInstanceDataTests.cs`, `KhaozEngine.Render.Tests/Render3D/FoliageDisplacementTests.cs`.
- Regenerate: `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`, `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt`, `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`, `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt`.
- Existing corpus owners: `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs:59,80`, `KhaozEngine.Render.Tests/Gpu/ShaderCorpusTests.cs:33-64`. The `Foliage` and `FoliageMotion` entries keep their names, and their emitted vertex/layout rows change. Task 5 adds the new depth entries.

**Interfaces:**
- Consumes: Task 1 `FoliageInstance.Motion` and `FoliageMotionProfile.ToVector4()`.
- Produces: `ModelRenderer.FoliageInstanceData.Motion : Vector4`, `SizeInBytes = 96u`, sequential offset `80`.
- Produces: `internal static GpuVertexLayoutDescription[] FoliageVertexLayouts.Create()` with the existing model stream and the six-vec4 instance stream. Colour, motion and Task 5 depth all use it.
- Produces: shader constants `ShaderSources.FoliageUniformBlockGlsl` and `ShaderSources.FoliageDisplacementGlsl`. GLSL signature `vec3 foliageWorld(vec3 focus, float windTime, float metresPerPixel, mat4 fadeViewProj, bool previous, out mat4 Model, out bool rejected)`.
- Produces: oracle `FoliageWindMirror.VertexWorld(in FoliageUniforms u, in ModelRenderer.FoliageInstanceData instance, Vector3 localPosition, in Matrix4x4 fadeViewProjection, Vector3 renderOrigin, bool previous, out bool rejected) -> Vector3`. Preserve both `TopOffset` overloads, adding optional `FoliageMotionProfile motion = default` after their existing parameters.

- [ ] Write `InstanceStrideAndLocationTenPackTheProfile`, `ProgramsIncludeOneIdenticalDisplacementFunction` and `LeanAndWindRespectTheProfile`. Use a fully faded, upright 1 m mesh and wind fade disabled for the numeric oracle assertions.

```csharp
Assert.Equal(96, Marshal.SizeOf<ModelRenderer.FoliageInstanceData>());
Assert.Equal(80, (int)Marshal.OffsetOf<ModelRenderer.FoliageInstanceData>("Motion"));
GpuVertexLayoutDescription layout = FoliageVertexLayouts.Create()[1];
Assert.Equal(96u, layout.Stride);
Assert.Equal(1u, layout.InstanceStepRate);
Assert.Equal(6, layout.Elements.Length);
Assert.Equal("FoliageMotion", layout.Elements[5].Name);
Assert.Equal(GpuVertexElementFormat.Float4, layout.Elements[5].Format);
Assert.Contains("layout(location=10) in vec4 FoliageMotion;", ShaderSources.FoliageVert);
Assert.Contains(ShaderSources.FoliageDisplacementGlsl, ShaderSources.FoliageVert);
Assert.Contains(ShaderSources.FoliageDisplacementGlsl, ShaderSources.FoliageMotionVert);
// u has no wind and an interactor at (-.1, 0, 0), radius 2, strength 1.
Vector3 capped = FoliageWindMirror.TopOffset(u, Vector3.Zero, 1f,
    new FoliageMotionProfile(.3f, .15f, .4f, 0f));
Assert.Equal(.15f, capped.X, 1e-6f);
Assert.Equal(-(1f - MathF.Sqrt(1f - .15f * .15f)), capped.Y, 1e-6f);
Assert.Equal(0f, capped.Z);
Assert.Equal(Vector3.Zero, FoliageWindMirror.TopOffset(u, Vector3.Zero, 1f,
    new FoliageMotionProfile(0f, 0f, 0f, 0f)));
```

Add a wind-only assertion below the cap with `WindStrength = .1f`, `MaxLean = 1`, comparing `WindScale = .4f` against Blade: horizontal displacement is `.4f` of Blade within `1e-6f`. Add a `MaxLean = 1` case whose vertical result remains finite. In the existing GPU upload test, two instances must upload `192` bytes once, then `0`, retaining one patch and two candidates.

- [ ] Run red.

```bash
build-slot --label foliage-stream-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FoliageInstanceDataTests|FullyQualifiedName~FoliageDisplacementTests|FullyQualifiedName~MotionShaderTextTests|FullyQualifiedName~FoliageUniformsTests"
```

Expected: missing layout/oracle members, or the old `80` byte stride assertion fails.

- [ ] Implement the shared layout and packing in `Scene3D.CreateFoliageBatch`. The existing checked size multiplication, upload byte counters and pending-buffer lifetime follow `SizeInBytes`, with no separate hard-coded `80` left in foliage tests.
- [ ] Move `foliageWorld` from the motion shader into `ShaderSources.FoliageDisplacement.cs`. Move the common foliage UBO declaration there too, keeping its current 256-byte layout until Task 3. Both vertex strings concatenate the same constants and declare location 10. The base calls the function with current focus/time/scale and `previous = false`, retaining its existing fragment outputs and main tail.
- [ ] Replace the displacement core inside that shared function with these exact changes, leaving existing height fade, render-origin rebasing, wind phase and interactor falloff in their current order.

```glsl
vec2 bend = WindTime.xy * (sin(phase) * 0.7 + sin(phase * 0.43 + 1.7) * 0.3)
    * FadeWind.y * bladeHeight * FoliageMotion.z;
// Apply the existing pixel wind fade here, before adding interactor pushes.
for (int i = 0; i < 4; i++) {
    vec4 interactor = previous ? PrevInteractors[i] : Interactors[i];
    float strength = previous ? PrevStrengths[i] : Strengths[i];
    if (interactor.w <= 0.0 || strength <= 0.0) continue;
    vec3 delta = root - (interactor.xyz - RenderOrigin.xyz);
    float falloff = 1.0 - smoothstep(0.0, interactor.w, length(delta));
    bend += delta.xz / max(length(delta.xz), 0.05)
        * falloff * strength * bladeHeight * FoliageMotion.x;
}
float bendLength = length(bend);
float cap = bladeHeight * FoliageMotion.y;
if (bendLength > cap && bendLength > 0.0) bend *= cap / bendLength;
world.xz += bend * rootWeight;
world.y -= (bladeHeight - sqrt(max(0.0, bladeHeight * bladeHeight - dot(bend, bend)))) * rootWeight;
```

- [ ] Replace `TheFoliageVariantRestatesFoliageVertLineForLine` with a check that each program contains exactly one copy of the shared function and calls it with the correct current/previous arguments. Retain input, output and main-tail parity assertions. Update `FoliageUniformsTests` to expect the unified current/history GLSL declaration, retaining its 256-byte offset/size checks until Task 3. Extend the independent CPU oracle with the same profile coefficients and full vertex/fade parameters.
- [ ] Refresh the four shader tables through their existing writers. Run one command per writer, serialized, with no GPU or golden update flag.

```bash
build-slot --label foliage-msl-table -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~MetalMslByteEquality" --environment KE_UPDATE_MSL_HASHES=1
build-slot --label foliage-hlsl-table -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~D3D11HlslByteEquality" --environment KE_UPDATE_HLSL_HASHES=1
build-slot --label foliage-spirv-table -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~VulkanSpirvByteEquality" --environment KE_UPDATE_SPIRV_HASHES=1
build-slot --label foliage-corpus -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~ShaderCorpus" --environment KE_WRITE_SHADER_CORPUS=1
```

Inspect changes only in the `Foliage` and `FoliageMotion` rows. Historical corpus files remain untouched.

- [ ] Run the red command again with label `foliage-stream-green` and append `|FullyQualifiedName~ByteEquality|FullyQualifiedName~ShaderCorpus` to its filter. Expected: headless layout, numerical oracle, shared-source parity and emitted-table checks pass.
- [ ] Commit only these paths.

```bash
task_files=(
  KhaozEngine.Render3D/Internal/FoliageVertexLayouts.cs
  KhaozEngine.Render3D/Internal/ShaderSources.FoliageDisplacement.cs
  KhaozEngine.Render3D/Rendering/ModelRenderer.Foliage.cs
  KhaozEngine.Render3D/Scene3D.Foliage.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Foliage.cs
  KhaozEngine.Render3D/Internal/ShaderSources.MotionFoliage.cs
  KhaozEngine.Render.Tests/Render3D/MotionShaderTextTests.cs
  KhaozEngine.Render.Tests/Render3D/FoliageUniformsTests.cs
  KhaozEngine.Render.Tests/Gpu/FoliageWindMirror.cs
  KhaozEngine.Render.Tests/Gpu/FoliageGpuTests.cs
  KhaozEngine.Render.Tests/Render3D/FoliageInstanceDataTests.cs
  KhaozEngine.Render.Tests/Render3D/FoliageDisplacementTests.cs
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt
)
git add -- "${task_files[@]}"
git commit --only -m "feat(foliage): pack profiles and share vertex displacement" -- "${task_files[@]}"
```

### Task 3: Agitation, temporal slot growth and leaf shiver

**Files:**
- Create: `KhaozEngine.Render3D/FoliageAgitation.cs`.
- Modify: `KhaozEngine.Render3D/FoliageInteractor.cs:7-18`, `KhaozEngine.Render3D/Rendering/ModelRenderer.Foliage.cs:20-100,164-191`, `KhaozEngine.Render3D/Scene3D.Foliage.cs:34-43,182-195`.
- Modify: Task 2 `KhaozEngine.Render3D/Internal/ShaderSources.FoliageDisplacement.cs` and `KhaozEngine.Render.Tests/Gpu/FoliageWindMirror.cs`.
- Test, create: `KhaozEngine.Render.Tests/Render3D/FoliageAgitationTests.cs`, `KhaozEngine.Render.Tests/Render3D/FoliageShiverTests.cs`, `KhaozEngine.Render.Tests/Gpu/FoliageProfileGpuTests.cs`, `KhaozEngine.Render.Tests/Gpu/FoliageShiverMotionGpuTests.cs`.
- Modify tests: `KhaozEngine.Render.Tests/Render3D/FoliageUniformsTests.cs:14-33`, `KhaozEngine.Render.Tests/Render3D/FoliageMotionUniformsTests.cs:15-77`, `KhaozEngine.Render.Tests/Gpu/FoliageGpuTests.cs:32`.
- Regenerate: the same four emitted shader tables as Task 2.

**Interfaces:**
- Consumes: Task 2 shared GLSL, `VertexWorld` oracle and `FoliageInstanceData`.
- Produces: `FoliageInteractor(Vector3 Position, float Radius, float Strength = 1f, float Agitation = 0f)`, existing `void Validate()` and a three-output deconstruction compatibility overload.
- Produces: mutable value type `public struct FoliageAgitation`, explicit parameterless constructor, `FoliageAgitation(float referenceSpeed = 2f, float settleTime = .6f, float teleportResetDistance = 5f)`, get-only `float ReferenceSpeed`, `SettleTime`, `TeleportResetDistance`, `float Value`, `float Update(Vector3 position, float deltaTime)`, `void Reset()`.
- Produces: `FoliageUniforms.Agitation`, `PrevAgitation : Vector4`, payload `SizeInBytes = 288u`, dynamic `SlotBytes = 512u`.
- Produces: test-only `FoliageShiverMotionGpuTests.ExpectedMotion(int x, int y, Vector4[] rasterClip, Vector4[] currentClip, Vector4[] previousClip, int width, int height) -> Vector2`, returning motion in pixels for one triangle.

- [ ] Write `AgitationDefaultsAndRanges`, `RiseSettleAndTeleportReset`, `ZeroTimeRebasesWithoutAgitatingAndInvalidInputPreservesState`, `VerticalMotionDoesNotAgitate` and `UpdateAllocatesNothing` in the `AllocSensitive` collection. Warm the helper once, then measure one update with `GC.GetAllocatedBytesForCurrentThread`, outside assertion/delegate setup.

```csharp
var agitation = new FoliageAgitation();
Assert.Equal((2f, .6f, 5f), (agitation.ReferenceSpeed, agitation.SettleTime, agitation.TeleportResetDistance));
Assert.Equal(0f, agitation.Update(Vector3.Zero, .1f));
Assert.Equal(1f, agitation.Update(new Vector3(.2f, 0f, 0f), .1f));
Assert.Equal(.5f, agitation.Update(new Vector3(.2f, 0f, 0f), .3f), 1e-6f);
Assert.Equal(0f, agitation.Update(new Vector3(.2f, 0f, 0f), .3f));
Assert.Equal(0f, agitation.Update(new Vector3(6f, 0f, 0f), .1f));
Assert.Equal(1f, agitation.Update(new Vector3(6.2f, 0f, 0f), .1f), 1e-5f);
agitation.Reset();
Assert.Equal(0f, agitation.Update(Vector3.Zero, 0f));
Assert.Equal(0f, agitation.Update(new Vector3(2f, 0f, 0f), 0f));
Assert.Equal(0f, agitation.Update(new Vector3(2f, 0f, 0f), .1f));
Assert.Equal(0f, new FoliageInteractor(Vector3.Zero, 1f).Agitation);
Assert.Equal("Agitation", Assert.Throws<ArgumentOutOfRangeException>(() =>
    new FoliageInteractor(Vector3.Zero, 1f, 1f, 1.01f).Validate()).ParamName);
```

Reject negative/non-finite `deltaTime` and non-finite position without changing state. Constructor parameters must be finite and strictly positive with their parameter names in exceptions. A 1 m vertical move over `.1f` returns zero agitation. `default(FoliageAgitation)` must use the documented settings too.

- [ ] Write `AgitationOffsetsAndHistoryAreIndependent` and extend temporal propagation tests.

```csharp
Assert.Equal(160, (int)Marshal.OffsetOf<FoliageUniforms>("Agitation"));
Assert.Equal(176, (int)Marshal.OffsetOf<FoliageUniforms>("PrevFocus"));
Assert.Equal(192, (int)Marshal.OffsetOf<FoliageUniforms>("PrevInteractor0"));
Assert.Equal(256, (int)Marshal.OffsetOf<FoliageUniforms>("PrevStrengths"));
Assert.Equal(272, (int)Marshal.OffsetOf<FoliageUniforms>("PrevAgitation"));
Assert.Equal(288, Marshal.SizeOf<FoliageUniforms>());
Assert.Equal(512u, FoliageUniforms.SlotBytes);
// Build last/current with four actors and agitation [.2, .4, .6, 1] / [1, 0, .3, .5].
Assert.Equal(new Vector4(1f, 0f, .3f, .5f), current.WithPrevious(last).Agitation);
Assert.Equal(new Vector4(.2f, .4f, .6f, 1f), current.WithPrevious(last).PrevAgitation);
```

Assert unused slots are zero. Extend `ASceneFoldsEachBatchsLastFrameIntoItsSubmissionOnlyWhileTemporal` to assert previous agitation is zero while temporal is off, equals current on first temporal submission and carries last frame thereafter. Cover a skipped batch frame and temporal off/on within a submitted frame, using the existing motion-history reset paths.

- [ ] Run red with filter `FullyQualifiedName~FoliageAgitationTests|FullyQualifiedName~FoliageShiverTests|FullyQualifiedName~FoliageUniformsTests|FullyQualifiedName~FoliageMotionUniformsTests`.

```bash
build-slot --label foliage-agitation-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FoliageAgitationTests|FullyQualifiedName~FoliageShiverTests|FullyQualifiedName~FoliageUniformsTests|FullyQualifiedName~FoliageMotionUniformsTests"
```

Expected: missing agitation/helper members and old uniform sizes.

- [ ] Implement `FoliageAgitation.Update`. The first sample seeds position and returns zero. Zero time rebases position and preserves `Value`. A full 3D jump greater than `TeleportResetDistance` resets value to zero and seeds the new position. Otherwise compute horizontal speed, `target = clamp(speed / ReferenceSpeed, 0, 1)`, then `Value = max(target, max(0, Value - deltaTime / SettleTime))`. Use double intermediates for finite-coordinate distance and division. `5 m` is safely above ordinary per-frame movement at the default 2 m/s and catches respawns, while remaining configurable for other movement scales. Default-initialized state resolves configuration to `2`, `.6`, `5`.
- [ ] Extend interactor validation and packing. Put current agitation immediately after `WindFade` and previous agitation after `PrevStrengths`. This gives 176 current bytes plus 112 history bytes. Keep 16-byte std140 members and 256-byte dynamic-offset alignment, documented by `D3D11UniformRing.cs:30-33`. `512` is the next aligned slot, not `288`. `WithPrevious` copies agitation. Whole-buffer upload and resource windows use 512, and four initial slots now upload 2048 bytes. Update the existing upper-bound upload assertion from 1024 to 2048.
- [ ] Add the shiver in the shared GLSL function after the capped bend and tip drop (ruling S7). Inside the existing interactor loop, for each accepted interactor select agitation from the corresponding current/previous vec4 and keep the strongest intensity. After the loop, apply one oscillation scaled by it.

```glsl
// Inside the interactor loop:
float agitation = previous ? PrevAgitation[i] : Agitation[i];
shiverIntensity = max(shiverIntensity, falloff * strength * agitation);
// After the loop, the lean cap and tip lowering:
float leafPhase = windTime * 14.0
    + dot(IModel3.xz, vec2(0.73, 1.17))
    + dot(Position, vec3(3.1, 2.7, 4.3));
vec2 shiver = vec2(sin(leafPhase), cos(leafPhase))
    * FoliageMotion.w * bladeHeight * shiverIntensity;
world.xz += shiver * rootWeight;
```

`shiverIntensity` starts at `0.0`, and `rootWeight` remains squared normalized height. Root phase uses absolute instance coordinates, so render-origin rebasing does not change it. Preserve a zero-shiver/zero-agitation fast path without changing the Blade arithmetic. Extend the independent CPU oracle with these exact constants.

- [ ] Add `ZeroAgitationHasNoShiver`, `ShiverLeavesTheRootFixedAndMovesUpperVertices`, `FullLeanStillShivers` and `FourInteractorShiverFitsTheApprovedBounds` to the oracle tests. For the first three use height 1, `(BendScale, MaxLean, WindScale, Shiver) = (0, 0, 0, .035)`, root `(0,0,0)`, actor at root with radius 2 and strength 1, agitation 0 then 1, time 0. Assert root offset is zero, the top horizontal offset has length `.035f` within `1e-6f`, and agitation zero is exactly rest. The full-lean case uses `.15f` cap and shows the additional shiver survives saturation. The four-interactor case places four colocated interactors with strength and agitation 1 on a height-1 mesh with `Shiver = .25`, zero lean and zero wind, and asserts the top horizontal offset length equals `.25f` within `1e-6f`, never more (ruling S7).
- [ ] Add GPU facts using `FoliageGpuScene` as a class fixture. Compare actual colour readback against a rigid reference mesh whose vertices are displaced by `VertexWorld`, rather than asserting successful draw calls. Assert zero-agitation captures are equal across two times, root-region pixels remain equal, upper-region pixels differ at agitation 1, and the shrub with `.15f` MaxLean agrees with its capped CPU reference. Add `AgedProfileFixtureMatchesFresh`, comparing shared-fixture aging against a fresh fixture, and restore mutable scene settings after each fact.
- [ ] Add `ShiveringShrubMotionMatchesCurrentAndPreviousOracle` using `TemporalFixture(320, 180, ...)`, one visible triangle above an unused root vertex, profile `(.3, .15, .4, .035)`, times `0` and `.1`, and agitation `.2` then `1`. Build both frames' vertex positions with `VertexWorld` and each frame's camera/scale. `ExpectedMotion` computes screen barycentrics at pixel centres from jittered raster clips, perspective-corrects them with reciprocal clip w, interpolates current/previous clip values and applies `MotionMath.UvMotion` times the viewport. Assert more than `100` drawn motion pixels and error at most `.05f` pixels. This handles the spatially varying leaf phase, which the existing uniform-translation plate oracle cannot prove.
- [ ] Refresh the four shader tables using Task 2 commands, inspect only foliage rows, then run the red command with label `foliage-agitation-green`. Expected: headless checks pass.
- [ ] Run one focused local GPU proof.

```bash
build-slot --label foliage-shiver-gpu --heavy -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FoliageProfileGpuTests|FullyQualifiedName~FoliageShiverMotionGpuTests|FullyQualifiedName~FoliageAndGroundMotionGpuTests"
```

Expected: pixel and motion assertions pass, zero selected GPU facts skipped. Do not use a golden update flag.

- [ ] Commit only these paths.

```bash
task_files=(
  KhaozEngine.Render3D/FoliageAgitation.cs
  KhaozEngine.Render3D/FoliageInteractor.cs
  KhaozEngine.Render3D/Rendering/ModelRenderer.Foliage.cs
  KhaozEngine.Render3D/Scene3D.Foliage.cs
  KhaozEngine.Render3D/Internal/ShaderSources.FoliageDisplacement.cs
  KhaozEngine.Render.Tests/Gpu/FoliageWindMirror.cs
  KhaozEngine.Render.Tests/Render3D/FoliageAgitationTests.cs
  KhaozEngine.Render.Tests/Render3D/FoliageShiverTests.cs
  KhaozEngine.Render.Tests/Gpu/FoliageProfileGpuTests.cs
  KhaozEngine.Render.Tests/Gpu/FoliageShiverMotionGpuTests.cs
  KhaozEngine.Render.Tests/Render3D/FoliageUniformsTests.cs
  KhaozEngine.Render.Tests/Render3D/FoliageMotionUniformsTests.cs
  KhaozEngine.Render.Tests/Gpu/FoliageGpuTests.cs
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt
)
git add -- "${task_files[@]}"
git commit --only -m "feat(foliage): add movement agitation and temporal leaf shiver" -- "${task_files[@]}"
```

### Task 4: Terrain model policies and retained snapshots

**Files:**
- Modify: `KhaozEngine.Terrain.Render3D/GroundCoverRenderer.cs:20-47,179-193`, `KhaozEngine.Terrain.Render3D/GroundCoverGpuBindings.cs:10-50`, `KhaozEngine.Terrain.Render3D/GroundCoverGpuCache.cs:24-30,41-43`.
- Modify tests: `KhaozEngine.Render.Tests/Terrain/GroundCoverGpuBindingsTests.cs:18-96`, `KhaozEngine.Render.Tests/Terrain/GroundCoverGpuOptionsTests.cs:12-57,114-148`.

**Interfaces:**
- Consumes: Task 1 `FoliageInstance(..., Motion, CastsShadows)`.
- Produces: `GroundCoverRenderOptions.MotionProfiles : IReadOnlyDictionary<string, FoliageMotionProfile>` and `ModelCastsShadows : IReadOnlyDictionary<string, bool>`, both get/set, default empty ordinal maps. Existing global `bool CastsShadows` remains the fallback.
- Produces: `GroundCoverGpuBindings.Refresh(IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes, GroundCoverRenderOptions? options = null) -> bool`. Existing `FoliageInstance[] Expand()` consumes its detached per-model snapshot.
- Preserves: `GroundCoverGpuCache.CanRetain(IReadOnlyList<GroundCoverInstance> cover, GroundCoverRenderOptions options) -> bool`, `GroundCoverRenderer.Queue(...) -> int` and CPU fallback fade/casting behavior.

- [ ] Extend `MultipartExpansionPreservesAuthoredTransformsAndRanks` with a shrub profile and casting true for one two-part model, and default policy for the other model. Add `PolicyMutationAndRemovalRefreshOnlyTheEffectiveSnapshot`.

```csharp
var profiles = new Dictionary<string, FoliageMotionProfile> { ["grass"] = new(.3f, .15f, .4f, .035f) };
var casting = new Dictionary<string, bool> { ["grass"] = true };
var options = new GroundCoverRenderOptions { MotionProfiles = profiles, ModelCastsShadows = casting };
Assert.True(bindings.Refresh(meshes, options));
FoliageInstance[] before = bindings.Expand();
Assert.All(before.Take(2), item => { Assert.Equal(profiles["grass"], item.Motion); Assert.True(item.CastsShadows); });
Assert.Equal(FoliageMotionProfile.Blade, before[2].Motion);
Assert.False(before[2].CastsShadows);
Assert.False(bindings.Refresh(meshes, options));
profiles["grass"] = new(.2f, .1f, .3f, .02f);
Assert.Equal(.3f, before[0].Motion.BendScale);
Assert.True(bindings.Refresh(meshes, options));
profiles.Remove("grass");
casting.Remove("grass");
Assert.True(bindings.Refresh(meshes, options));
Assert.All(bindings.Expand(), item => { Assert.Equal(FoliageMotionProfile.Blade, item.Motion); Assert.False(item.CastsShadows); });
```

Add a same-mesh/two-model case proving the policy stays per instance. Equal replacement maps and edits to unrelated model IDs do not rebuild. Check null maps are refused by `ArgumentNullException` naming `MotionProfiles` or `ModelCastsShadows`. Keep `RepeatedRefreshesReadOnlyUniqueModelsAndAllocateNothing` passing for unchanged policies.

- [ ] Change the casting-enabled HeightScale retention theory row to expected `true`, rename it `RetentionRequiresImmutableHeightScaledCover`, and retain all other refusal rows. Assert CPU `Queue` still uses global `CastsShadows` and performs its existing rooted HeightScale transform without wind or shiver, even when new maps are populated.
- [ ] Run red.

```bash
build-slot --label foliage-terrain-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~GroundCoverGpuBindingsTests|FullyQualifiedName~GroundCoverGpuOptionsTests"
```

Expected: missing policy properties, then the old caster refusal fails.

- [ ] Add the two maps to options and null validation. Snapshot each unique model's effective profile and casting alongside its mesh parts in `Refresh`. Missing profile resolves to Blade, missing casting resolves to the global bool. Compare values rather than map references, so in-place mutation, removals and equal replacement maps behave correctly without per-frame allocation.
- [ ] Apply the effective pair once per placement before expanding its parts in `Expand`. Pass options from `GroundCoverGpuCache.Draw`, rebuilding and retiring the old batch only on effective snapshot change. Remove only the `!options.CastsShadows` retention restriction. Preserve the CPU emitter unchanged.
- [ ] Run the red command with label `foliage-terrain-green`. Expected: all snapshot, retention and CPU compatibility tests pass.
- [ ] Commit.

```bash
task_files=(KhaozEngine.Terrain.Render3D/GroundCoverRenderer.cs KhaozEngine.Terrain.Render3D/GroundCoverGpuBindings.cs KhaozEngine.Terrain.Render3D/GroundCoverGpuCache.cs KhaozEngine.Render.Tests/Terrain/GroundCoverGpuBindingsTests.cs KhaozEngine.Render.Tests/Terrain/GroundCoverGpuOptionsTests.cs)
git add -- "${task_files[@]}"
git commit --only -m "feat(terrain): carry model foliage motion and casting policies" -- "${task_files[@]}"
```

### Task 5: Per-frame animated cascade casters

**Files:**
- Create: `KhaozEngine.Render3D/Internal/FoliageShadowSelection.cs`, `KhaozEngine.Render3D/Internal/ShaderSources.FoliageShadow.cs`, `KhaozEngine.Render3D/Rendering/FoliageShadowRenderer.cs`, `KhaozEngine.Render3D/Scene3D.FoliageShadows.cs`, `KhaozEngine.Render3D/Scene3D.CascadeShadowPass.cs`.
- Modify: `KhaozEngine.Render3D/Internal/FoliagePatch.cs:7-11`, `KhaozEngine.Render3D/Internal/FoliagePatchLayout.cs:43-82,139`, `KhaozEngine.Render3D/FoliageBatch.cs:11-29`, `KhaozEngine.Render3D/Scene3D.Foliage.cs:46,100-118,122-173,216-227`.
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.Foliage.cs:103-110`, `KhaozEngine.Render3D/Rendering/ShadowMapRenderer.cs:110-123`, `KhaozEngine.Render3D/Scene3D.cs:1572-1622`, `KhaozEngine.Render3D/Scene3D.ShadowCasters.cs:347-412`, `KhaozEngine.Render3D/Scene3D.ShadowDirtyState.cs:33-64`.
- Test, create: `KhaozEngine.Render.Tests/Render3D/FoliageShadowSelectionTests.cs`, `KhaozEngine.Render.Tests/Gpu/FoliageShadowGpuTests.cs`, `KhaozEngine.Render.Tests/Gpu/FoliageShadowGpuScene.cs`.
- Modify tests: `KhaozEngine.Render.Tests/Render3D/FoliagePatchLayoutTests.cs:18-125`, `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs:59-80`, `KhaozEngine.Render.Tests/Gpu/VulkanShippedVertexLayoutTests.cs:19-61,141-170`, `KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs:58-80,89-170`.
- Regenerate: the same four emitted shader tables as Task 2.

**Interfaces:**
- Consumes: Tasks 1-3 instance/profile data, `FoliageVertexLayouts.Create()`, shared `foliageWorld`, current foliage uniform slots and camera pixel scale.
- Produces: `FoliagePatch.CastsShadows : bool`, `MaxLeanHeight`, `ShiverHeight`, `WindScaleHeight : float`, and `MeshBounds AnimatedBounds(float windStrength)`.
- Produces: `FoliageShadowSelection.NeedsDepthPass(bool anyCurrent, bool anyPrevious) -> bool` and `IntersectsCascade(in MeshBounds bounds, in FrustumPlanes cascade) -> bool`.
- Produces: `ShaderSources.FoliageDepthVert`, `FoliageDepthCutoutFrag`. Shipped pairs `FoliageDepth` with existing `ShadowDepthFrag` and `FoliageDepthCutout` with the new cutout fragment.
- Produces: `FoliageShadowRenderer(IGpuDevice device, ShadowMapRenderer shadows, IGpuResourceLayout foliageLayout, GpuRetireQueue retired)`, `void UploadCascades(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> lightViewProjections, in Matrix4x4 cameraViewProjection, Vector3 renderOrigin)`, `void Draw(IGpuCommandList cl, int cascade, IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, IGpuBuffer instances, uint start, uint count, IGpuResourceSet foliageSet, uint foliageSlot, IGpuResourceSet? cutoutMaterial)`, `void Dispose()`.
- Produces: `ModelRenderer.FoliageLayout : IGpuResourceLayout`, `FoliageSet : IGpuResourceSet`, `ShadowMapRenderer.Outputs : GpuOutputDescription`, `CutoutMaterialLayout : IGpuResourceLayout`, internal get-only seams.
- Produces: private Scene3D helpers `float RenderCascadeShadowPass(IGpuCommandList cl, bool shadowMapActive, int skinnedCasterCount)` and `void DrawFoliageShadowCascade(IGpuCommandList cl, int cascade)`. Task 6 consumes the per-frame casting patch prefixes this task collects.

Boundary refinement: move the complete cascade orchestration block out of `Scene3D.cs` into `Scene3D.CascadeShadowPass.cs`. This is one cohesive existing responsibility, not a line-count split. `Scene3D.cs` is already exactly its frozen 2372-line KESIZE baseline, so adding the foliage dirty-state decision there would fail the ratchet. The standalone renderer owns new shader/pipeline resources instead of growing `ShadowMapRenderer` with another rendering concern.

- [ ] Write `PatchesSeparateCastingButDoNotSeparateProfiles`, `AnimatedBoundsUseTheApprovedAllowance` and `FoliagePresenceAndRemovalForceAFramePass`.

```csharp
// Three same-cell/same-mesh instances: two casting with different profiles, one non-casting.
Assert.Equal(2, layout.Patches.Length);
Assert.Equal(2, Assert.Single(layout.Patches.Where(p => p.CastsShadows)).Count);
Assert.Equal(1, Assert.Single(layout.Patches.Where(p => !p.CastsShadows)).Count);
// One height-2 shrub, profile (.3, .15, .4, .035), WindStrength .5.
float padding = (.15f + .035f + .4f * .5f) * 2f;
Assert.Equal(.77f, padding, 1e-6f);
Assert.Equal(patch.Bounds.Min - new Vector3(padding), patch.AnimatedBounds(.5f).Min);
Assert.Equal(patch.Bounds.Max + new Vector3(padding), patch.AnimatedBounds(.5f).Max);
Assert.True(FoliageShadowSelection.NeedsDepthPass(true, false));
Assert.True(FoliageShadowSelection.NeedsDepthPass(true, true));
Assert.True(FoliageShadowSelection.NeedsDepthPass(false, true));
Assert.False(FoliageShadowSelection.NeedsDepthPass(false, false));
```

For mixed profiles, aggregate each `coefficient * world height` conservatively within the patch. Expand the AABB and derive its sphere, rather than adding a horizontal displacement to the existing sphere radius. Include height-zero meshes, scaled/tilted instances and the four-interactor case, which ruling S7 keeps inside the same allowance.

- [ ] Run red.

```bash
build-slot --label foliage-shadow-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FoliageShadowSelectionTests|FullyQualifiedName~FoliagePatchLayoutTests"
```

Expected: missing caster helpers, patch policy and bounds members.

- [ ] Add casting to `PatchKey`, its sort order and patch records, preserving stable rank ordering. Profile differences never create another patch. Aggregate motion-height maxima and implement the approved bounds allowance. Use the padded bounds for both colour and cascade culling when animated, keeping the legacy non-casting Blade culling behavior.
- [ ] Preserve an independent `_foliageShadowDraws` list before `PrepareFoliageFrame` removes camera-invisible colour draws. Collect only casting patch prefixes using the existing focus/density candidate selection. Upload pending batch instances and uniform slots when either colour or shadow needs them, including the first frame of an entirely off-camera caster. Apply the final camera projection/render-height pixel scale to those slots. Filter by each cascade's absolute-space frustum afterward. Disposed batches and stale mesh generations are excluded from both lists.
- [ ] Implement `FoliageDepthVert` using the shared UBO and displacement function, passing `CameraViewProj` as its fade matrix and current focus/time/scale. Its set 0 block is `mat4 LightViewProj`, `mat4 CameraViewProj`, `vec4 RenderOrigin`, payload 144 bytes in 256-byte cascade slots. Set 1 is the 512-byte foliage window. Project the displaced world with the cascade's existing GPU/atlas-correct depth matrix, and retain the existing near-plane pancake. Keep inputs 0..10 live with the established `1e-30` sink for unused normal, colour and tangent inputs.
- [ ] Implement the cutout fragment with contiguous inputs `vLightDepth` at 0, `vUv` at 1, `vCutoff` at 2. Sample albedo/sampler at set 2 and discard `alpha < vCutoff` when cutoff is positive. The opaque pair uses `ShadowDepthFrag` without a texture sample. `FoliageShadowRenderer` borrows the shadow output/material layout, sets the owner's cascade scissor, binds its own depth pipelines and shared foliage stream, and restores the next pass's normal bindings. Build pipelines lazily only when a casting patch is present, with two-sided cutout cards as in the existing shadow policy. Retire replaced buffers through the scene queue and dispose owned resources.
- [ ] Extend the extracted cascade coordinator with current/last animated-caster presence. The existing `Scene3D.ShadowDirtyState.cs` is the owner of cached cascade content through its last-rendered signature and persistent atlas. Animated data never enters `_lastShadowCasterRuns` or `_lastShadowCasterModels`. Presence forces rendering every frame, removal forces one clearing render, then a static scene may skip again. Commit the last-presence bool only after the pass renders. Draw foliage within each cascade, count its actual depth draws, and preserve existing rigid/skinned diagnostic meanings.
- [ ] Add GPU facts `AnimatedCascadeShadowMatchesDisplacedRigidReference`, `CameraWindFadeAlsoControlsTheDepthPass`, `NonCastingGrassAddsNoDepthDraws`, `CutoutFoliageCastsItsAlphaSilhouette` and `OffCameraCasterUploadsAndItsRemovalClearsTheAtlasOnce`. Use a lazily created `FoliageShadowGpuScene` class fixture owning `Render3DPreview`, with `Scene3D Scene`, `byte[] Capture(Action<Scene3D> draw)` and `Dispose()`. Test actual colour and `DebugReadShadowMap` depth readbacks against oracle-displaced rigid reference geometry. Zero casting gives exactly zero additional depth draws. The off-camera case checks a first instance upload of `96` bytes, a shadowed receiver, then removal restores the reference receiver and the following unchanged frame skips. Add `AgedShadowFixtureMatchesFresh` for aged-fixture/fresh-fixture pixel parity.
- [ ] Add `FoliageDepth` and `FoliageDepthCutout` to `ShippedShaderPrograms.GraphicsPrograms()`. Make `VulkanShippedVertexLayoutTests` instantiate both new pipelines with a real retained caster, and include both in the D3D11 contiguous-signature incident checks. Refresh the four tables with Task 2 commands, now allowing the new depth rows as well as the two existing foliage rows.
- [ ] Run the red command with label `foliage-shadow-green`, then the corpus, byte-equality and D3D11 source-contract filters. Expected: headless caster policy and shader coverage pass.
- [ ] Run one GPU proof.

```bash
build-slot --label foliage-cascade-gpu --heavy -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FoliageShadowGpuTests|FullyQualifiedName~VulkanShippedVertexLayoutTests|FullyQualifiedName~FoliageGpuTests"
```

Expected: readback and actual-pipeline assertions pass with zero selected rendering facts skipped. No existing golden files change.

- [ ] Commit only these paths.

```bash
task_files=(
  KhaozEngine.Render3D/Internal/FoliageShadowSelection.cs
  KhaozEngine.Render3D/Internal/ShaderSources.FoliageShadow.cs
  KhaozEngine.Render3D/Rendering/FoliageShadowRenderer.cs
  KhaozEngine.Render3D/Scene3D.FoliageShadows.cs
  KhaozEngine.Render3D/Scene3D.CascadeShadowPass.cs
  KhaozEngine.Render3D/Internal/FoliagePatch.cs
  KhaozEngine.Render3D/Internal/FoliagePatchLayout.cs
  KhaozEngine.Render3D/FoliageBatch.cs
  KhaozEngine.Render3D/Scene3D.Foliage.cs
  KhaozEngine.Render3D/Rendering/ModelRenderer.Foliage.cs
  KhaozEngine.Render3D/Rendering/ShadowMapRenderer.cs
  KhaozEngine.Render3D/Scene3D.cs
  KhaozEngine.Render3D/Scene3D.ShadowCasters.cs
  KhaozEngine.Render3D/Scene3D.ShadowDirtyState.cs
  KhaozEngine.Render.Tests/Render3D/FoliageShadowSelectionTests.cs
  KhaozEngine.Render.Tests/Gpu/FoliageShadowGpuTests.cs
  KhaozEngine.Render.Tests/Gpu/FoliageShadowGpuScene.cs
  KhaozEngine.Render.Tests/Render3D/FoliagePatchLayoutTests.cs
  KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs
  KhaozEngine.Render.Tests/Gpu/VulkanShippedVertexLayoutTests.cs
  KhaozEngine.Render.Tests/Gpu/D3D11FxcValidationTests.cs
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt
)
git add -- "${task_files[@]}"
git commit --only -m "feat(foliage): cast displaced cascade shadows from animated foliage" -- "${task_files[@]}"
```

### Task 6: Rest-pose point-shadow proxies for casting foliage

**Files:**
- Create: `KhaozEngine.Render3D/Internal/FoliagePointShadowStream.cs`, `KhaozEngine.Render3D/Scene3D.FoliagePointShadows.cs`.
- Modify: `KhaozEngine.Render3D/Scene3D.PointShadows.cs:316-346`, `KhaozEngine.Render3D/Scene3D.PointShadowPass.cs:220-264`.
- Read-only point integration references: `KhaozEngine.Render3D/Scene3D.PointCasters.cs:48-81`, `KhaozEngine.Render3D/Scene3D.PointShadowRequests.cs:10-11`, `KhaozEngine.Render3D/Rendering/PointShadowRenderer.cs:220-258`, `KhaozEngine.Render3D/Internal/PointShadowCasterSphere.cs:8-28`.
- Test, create: `KhaozEngine.Render.Tests/Render3D/FoliagePointShadowTests.cs`.
- Modify tests: Task 5 `KhaozEngine.Render.Tests/Gpu/FoliageShadowGpuTests.cs`.

**Interfaces:**
- Consumes: Task 1 `FoliageInstance.CastsShadows`, Task 5 per-frame casting patch prefixes and `FoliagePatch.CastsShadows`, the existing 128-byte `ModelRenderer.InstanceData` format and `PointShadowCasterSphere`.
- Produces: `FoliagePointShadowStream.Build(ReadOnlySpan<FoliageInstance> instances, Func<MeshHandle, MeshBounds?> boundsForMesh) -> FoliagePointShadowStream`, `long EnsureUploaded(IGpuDevice device, IGpuCommandList cl, Vector3 renderOrigin, GpuRetireQueue retired)`, get-only `IGpuBuffer? Buffer` and `ReadOnlySpan<FoliagePointShadowCaster> Casters`, `void Query(ReadOnlySpan<FoliagePointShadowRange> candidates, Vector3 lightPosition, float radius, float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax, List<FoliagePointShadowRun> runs)`, `void Dispose()`. Its records are `FoliagePointShadowCaster(MeshHandle Mesh, Matrix4x4 Transform, Vector4 Sphere, uint Slot, int SourceIndex)`, `FoliagePointShadowRun(MeshHandle Mesh, uint Start, uint Count)` and `FoliagePointShadowRange(int Start, int Count)`, defined in the same file. It stores only casting instances.
- Produces: private Scene3D helpers `void MixFoliagePointSignature(ref ulong hash, Vector3 lightPosition, float radius, float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax)` and `int DrawFoliagePointShadowFace(IGpuCommandList cl, PointShadowRenderer renderer, PointShadowAtlas atlas, int packedIndex, int face, int row, Vector3 lightPosition, float radius, float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax)`.

- [ ] Write `PointProxyUsesRestTransformsAndIgnoresClockAgitationAndProfile`. Build a stream with a casting shrub and a non-casting blade, asserting exactly one proxy with the original transform. Assert `stream.Casters.Length == 1`, its `Transform` equals the authored matrix and its `SourceIndex` identifies the casting source item. Query `[new FoliagePointShadowRange(0, 2)]` and assert one rigid run for the touching light and zero for an excluded or wholly near-radius-contained caster. Test light shell, near-radius and exclusion-box selection with the existing `PointShadowCasterSphere` predicate. Signature stays equal after changes to time, agitation and motion coefficients, changes after transform/mesh generation/casting changes, and clears when the batch disappears.
- [ ] Run red.

```bash
build-slot --label foliage-point-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FoliagePointShadowTests"
```

Expected: missing stream, records and query members.

- [ ] Implement lazy point-shadow rest streams for casting instances only when point shadows are requested. Upload the rest stream before any depth render pass, with matrices rebased by the same frame origin as rigid instances. Reuse it until the immutable batch or render origin changes. Translate each submitted casting patch prefix into `FoliagePointShadowRange`, using original layout indices. `Query` filters proxies by `SourceIndex` and the existing light-shell predicate, then coalesces consecutive proxy slots of the same mesh into rigid runs. Query submitted candidates independently of camera rejection. Share that query between `MixFoliagePointSignature` and the point face draw helper. Mix only mesh identity and rest transforms. In `Scene3D.PointShadows.cs`, fold it into static signatures, and in `Scene3D.PointShadowPass.cs` draw it only for `PointShadowCasterSet.Rigid` through `PointShadowRenderer.BeginFace` and `DrawCasterRun`. Do not add it to cascade rigid spans or the colour stream. The extra 128-byte proxy stream is lazy and caster-only, with upload bytes accounted and storage retired with its batch. `EnsureUploaded` retains the supplied retire queue, so `Dispose` retires any live buffer rather than freeing an in-flight resource.
- [ ] Add GPU fact `PointShadowStaysAtRestWhileCascadeMoves` to `FoliageShadowGpuTests`, with enabled point shadows and no ordinary rigid draws, static point-cache reuse across a shiver-only change and point-cache invalidation after proxy removal.
- [ ] Run the red command with label `foliage-point-green`. Expected: proxy selection, signature and lifetime tests pass.
- [ ] Run one GPU proof.

```bash
build-slot --label foliage-point-gpu --heavy -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~FoliageShadowGpuTests|FullyQualifiedName~PointShadow"
```

Expected: readback assertions pass with zero selected rendering facts skipped. No existing golden files change.

- [ ] Commit only these paths.

```bash
task_files=(
  KhaozEngine.Render3D/Internal/FoliagePointShadowStream.cs
  KhaozEngine.Render3D/Scene3D.FoliagePointShadows.cs
  KhaozEngine.Render3D/Scene3D.PointShadows.cs
  KhaozEngine.Render3D/Scene3D.PointShadowPass.cs
  KhaozEngine.Render.Tests/Render3D/FoliagePointShadowTests.cs
  KhaozEngine.Render.Tests/Gpu/FoliageShadowGpuTests.cs
)
git add -- "${task_files[@]}"
git commit --only -m "feat(foliage): draw casting foliage into point shadows at rest" -- "${task_files[@]}"
```

Concurrent shadow evidence, after fetch on 2026-10-13: inspect `git diff --stat origin/main...origin/<branch>` again before reconciliation. At observed `origin/main dfec28111`, none of these branches changes `Scene3D.ShadowCasters.cs`, `Rendering/ShadowMapRenderer.cs`, `Scene3D.PointShadow*.cs` or `Scene3D.ShadowDirtyState.cs`.

| Branch and observed tip | Shadow code/test paths in the fetched diff | Other relevant path | Intersection with this plan |
|---|---|---|---|
| `fix/wa-shadow-control-cpu` at `ac7b58a8d` | `KhaozEngine.Render.Tests/Gpu/PointShadowFilterGpuTests.cs`, `PointShadowScene.cs`, `PointShadowSeamEvidence.cs`, `PointShadowSeamEvidenceOptions.cs`, `PointShadowSeamEvidenceRun.cs`, `PointShadowSeamEvidenceTests.cs`, all in the same `Gpu/` directory | `docs/INDEX.md` | `docs/INDEX.md` in Task 8 |
| `fix/wa-shadow-diagnostics` at `7cd1fab67` | The same six `KhaozEngine.Render.Tests/Gpu/` paths | `docs/INDEX.md` | `docs/INDEX.md` in Task 8 |
| `fix/wa-shadow-proof` at `8632b5739` | The same six `KhaozEngine.Render.Tests/Gpu/` paths | `.github/workflows/ci.yml` | None |

The control branch also changes its design, plans, proof artifacts and `tools/shadow-seam-analysis/`. Diagnostics changes its design, plans and proof artifacts. Proof changes its diagnostics plan and CPU proof artifact. This plan neither edits those artifacts nor changes their decisions. Future commits can change the overlap, so the orchestrator rechecks rather than assuming this snapshot stays current.

### Task 7: TileWorld styles, conflict validation and split invalidation

**Files:**
- Create: `KhaozEngine.TileWorld.Render3D/AnimatedFoliageStyle.cs`, `KhaozEngine.TileWorld.Render3D/AnimatedFoliageStyleSnapshot.cs`.
- Modify: `KhaozEngine.TileWorld.Render3D/TileWorldView.cs:39-41,176-179,201-212`, `KhaozEngine.TileWorld.Render3D/TileWorldView.AnimatedFoliage.cs:19-57,77-115`.
- Modify tests: `KhaozEngine.Render.Tests/TileWorld/TileWorldViewAnimatedFoliageTests.cs:23-61,156-181`, `KhaozEngine.Render.Tests/TileWorld/RecordingTileWorldScene.Foliage.cs:21-30`.
- Test, create: `KhaozEngine.Render.Tests/TileWorld/AnimatedFoliageStyleTests.cs`.

**Interfaces:**
- Consumes: Tasks 1/4 `FoliageMotionProfile`, `GroundCoverRenderOptions.MotionProfiles`, `ModelCastsShadows`, `TileObjectArchetype.MeshRef` and loaded `_propMeshes` part handles.
- Produces: `public readonly record struct AnimatedFoliageStyle(FoliageMotionProfile Motion, bool CastsShadows)`.
- Produces: `TileWorldViewOptions.AnimatedFoliageStyles : IReadOnlyDictionary<string, AnimatedFoliageStyle>`, get/set, default empty ordinal map.
- Produces: `internal sealed class AnimatedFoliageStyleSnapshot` with constructor `(IReadOnlySet<string> selection, IReadOnlyDictionary<string, AnimatedFoliageStyle> styles, TileWorldCatalogs catalogs, IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes)`, `bool Matches(IReadOnlySet<string> selection, IReadOnlyDictionary<string, AnimatedFoliageStyle> styles)`, `IReadOnlyDictionary<string, FoliageMotionProfile> MotionProfiles`, `IReadOnlyDictionary<string, bool> ModelCastsShadows`. It validates before publishing detached maps.
- Extends: `AnimatedPropSplit` consumes a style snapshot, and `Matches` compares the live selection and styles before retaining its `GroundCoverBatch`.

- [ ] Write `StyleOutsideSelectionNamesTheId`, `SharedMeshComparesImplicitBladeAndExplicitStyles`, `SelectedIdWithoutStyleKeepsLegacyBehavior` and `StyleMutationReleasesAndRebuildsTheAnimatedSplit`.

```csharp
// selection contains only "bush", styles contains "tree".
ArgumentException outside = Assert.Throws<ArgumentException>(() => CreateSnapshot(selection, styles, catalogs, meshes));
Assert.Equal("AnimatedFoliageStyles", outside.ParamName);
Assert.Contains("tree", outside.Message);
// "bush" and "bush_alias" have the same MeshRef. Alias has no style and therefore means Blade/false.
ArgumentException conflict = Assert.Throws<ArgumentException>(() => CreateSnapshot(sharedSelection, shrubOnly, catalogs, meshes));
Assert.Contains("bush_alias", conflict.Message);
Assert.Contains("bush", conflict.Message);
// Explicit Blade/false for one and omitted style for the other is compatible.
Assert.Equal(FoliageMotionProfile.Blade, compatible.MotionProfiles["bush"]);
Assert.False(compatible.ModelCastsShadows["bush_alias"]);
// After a valid in-place style change to new(.3, .15, .4, .035), casting true:
Assert.NotSame(before.Instances, after.Instances);
Assert.Single(scene.FoliageReleases, batch => ReferenceEquals(before.Instances, batch));
Assert.Equal(new FoliageMotionProfile(.3f, .15f, .4f, .035f), after.Options.MotionProfiles["bush"]);
Assert.True(after.Options.ModelCastsShadows["bush"]);
```

Define `CreateSnapshot(...) -> AnimatedFoliageStyleSnapshot` as a local test helper forwarding the exact constructor. Also test two different MeshRefs resolving to the same `MeshHandle`, matching styles accepted, a casting-only conflict rejected, null maps rejected, and an outside-selection style rejected when the selection or ground list is empty. A rejected live mutation must preserve the previous split until a valid option set is supplied. Existing no-style assertions still require full density, `PropDrawRadius`, Blade and no casting.

- [ ] Make `RecordingTileWorldScene.DrawGroundCover` record a detached options snapshot, including copied style maps and interactor values, so later draws cannot rewrite earlier evidence. Preserve its CPU `Queue` witness and return count.
- [ ] Run red.

```bash
build-slot --label foliage-tile-styles-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AnimatedFoliageStyleTests|FullyQualifiedName~TileWorldViewAnimatedFoliageTests"
```

Expected: missing style/snapshot members and option map.

- [ ] Implement the style and snapshot in their own files. `AnimatedFoliageArchetypes` remains the only selector. Resolve omitted styles to `(Blade, false)` independently of short-cover casting. Reject every style key outside the selection with `ArgumentException` naming its id and parameter `AnimatedFoliageStyles`. Compare effective styles for selected ids sharing non-empty ordinal `MeshRef` or any loaded part handle. Include both ids in conflict text, in ordinal order for deterministic diagnostics.
- [ ] Validate catalog sharing before uploads in the view constructor and loaded-handle sharing after resolution. On each draw check styles before the empty-selection/empty-ground early return. Compare detached snapshot contents, including in-place mutations and removals, before replacing a split. Validate a replacement before retiring the old split. A style change releases the old animated batch and builds a new split, while wind/interactor-only updates keep the existing split.
- [ ] Pass snapshot maps into `_animatedFoliageOptions.MotionProfiles` and `ModelCastsShadows`, keeping its global casting false. Preserve authored transforms, full density, prop radius and existing rebuild/unload/disposal ownership.
- [ ] Run the red command with label `foliage-tile-styles-green`. Expected: validation, legacy behavior, casting routing and snapshot lifetime tests pass.
- [ ] Commit.

```bash
task_files=(KhaozEngine.TileWorld.Render3D/AnimatedFoliageStyle.cs KhaozEngine.TileWorld.Render3D/AnimatedFoliageStyleSnapshot.cs KhaozEngine.TileWorld.Render3D/TileWorldView.cs KhaozEngine.TileWorld.Render3D/TileWorldView.AnimatedFoliage.cs KhaozEngine.Render.Tests/TileWorld/AnimatedFoliageStyleTests.cs KhaozEngine.Render.Tests/TileWorld/TileWorldViewAnimatedFoliageTests.cs KhaozEngine.Render.Tests/TileWorld/RecordingTileWorldScene.Foliage.cs)
git add -- "${task_files[@]}"
git commit --only -m "feat(tileworld): validate animated foliage styles and refresh splits" -- "${task_files[@]}"
```

### Task 8: Living documentation, staged version and complete verification

**Files:**
- Modify: `CHANGELOG.md:8-33`, `KhaozEngine.Render3D/README.md:1184-1209`, `KhaozEngine.Terrain.Render3D/README.md:231-238`, `KhaozEngine.TileWorld.Render3D/README.md:534-545`, `docs/USING-KHAOZENGINE.md:10810-10862`, `docs/INDEX.md:47`.
- Read/verify: `Directory.Build.props`, `scripts/check-doc-versions.sh:58-93` and `.github/workflows/cross-platform-gpu.yml:294-321,367-377`. Current package examples already declare `20.34.0`. There is no new package or dependency declaration, and the doc-version script itself needs no edit.
- Test: `KhaozEngine.slnx`, including `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj` and existing committed `KhaozEngine.Render.Tests/Gpu/goldens/*.{metal-native,direct3d11-native,vulkan-native}.txt`.

**Interfaces:**
- Consumes: final public signatures from Tasks 1/3/4/7 and verified GPU behavior from Tasks 5 and 6.
- Produces: living usage examples for profile/casting maps, `FoliageAgitation.Update(position, deltaTime)` feeding `FoliageInteractor.Agitation`, and explicit cascade-motion versus rest-pose point-shadow behavior.
- Produces: a `20.34.0` changelog extension and the existing design index row marked implemented only after the checks below pass. No release tag or consumer pin change.

- [ ] Add the changelog entry to `20.34.0`, covering profile motion, moving-only shiver and opt-in animated cascade shadows. State the one behavior change explicitly: retained GPU ground cover no longer refuses `CastsShadows`, so a consumer with `UseGpuBatches` and global casting now gets retained, animated, casting cover instead of the CPU path. No current consumer sets that combination (Grimhollow keeps casting off), but the line must exist. Keep the approved design immutable as history. Update the three package READMEs and the consumer guide with actual API names, exact defaults/ranges, the 14 rad/s constant, default Blade behavior, 96-byte instances, 288/512-byte uniforms and caster-only lazy point-proxy cost. Remove the stale retained-path requirement that casting must be disabled. Explain style conflicts, snapshots and map removal back to defaults.
- [ ] Sweep all Markdown for the changed names and old behavior.

```bash
rg -n --glob '*.md' 'FoliageMotionProfile|FoliageAgitation|FoliageInteractor|FoliageInstance|AnimatedFoliageStyles|AnimatedFoliageArchetypes|GroundCoverRenderOptions|UseGpuBatches|no-shadow|casts no dynamic shadow|shadow casting is disabled' .
```

Correct live-reference descriptions. Historical changelog/design records remain history. If the sweep discovers another live reference requiring an edit, name that exact path in the task handoff and stage it explicitly. Verify declarations still match staged `20.34.0`.

- [ ] Fetch current `main` and hand reconciliation to the orchestrator. It merges current `main` into this feature branch, resolves concurrent shadow/docs work there and reruns final verification on that combined tree. Recheck the three branch stats listed in Task 5. No implementation task merges into `main`, pushes `main`, packs the shared feed or tags.
- [ ] Run the whole-tree guards synchronously, with no bypass.

```bash
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

Expected: every exit code is zero, no KESIZE growth or version declaration drift.

- [ ] Run the full Release build once.

```bash
build-slot --label foliage-release-build -- dotnet build KhaozEngine.slnx -c Release
```

Expected: success, zero warnings and errors.

- [ ] Run the full headless suite once.

```bash
build-slot --label foliage-release-suite --heavy -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
```

Expected: all selected headless tests pass. Ordinary GPU skips are not evidence of rendering verification.

- [ ] Run one final local GPU proof on the reconciled tree, including legacy foliage, new profiles, temporal shiver, shadows and actual pipeline-layout capture.

```bash
build-slot --label foliage-final-gpu --heavy -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Foliage|FullyQualifiedName~VulkanShippedVertexLayoutTests" --logger "console;verbosity=detailed"
```

Expected: zero selected rendering facts skipped, readback/oracle assertions pass, and existing foliage golden comparisons use unchanged committed files. A Blade regression triggers the spec's fallback: reserve `WindFade.w` as a batch-uniform all-Blade flag, and branch inside the one shared function to the legacy expression/order for that flag. Compute the flag from retained effective profiles without regrouping draws. Update shared-source/oracle tests and emitted tables, then rerun only affected checks before the final combined proof. Never resolve Blade identity by rebaking old goldens.

- [ ] Commit the living documentation, preserving the index's pending status until backend acceptance.

```bash
task_files=(CHANGELOG.md KhaozEngine.Render3D/README.md KhaozEngine.Terrain.Render3D/README.md KhaozEngine.TileWorld.Render3D/README.md docs/USING-KHAOZENGINE.md)
git add -- "${task_files[@]}"
git commit --only -m "docs(foliage): document motion profiles and animated shadow contracts" -- "${task_files[@]}"
```

The orchestrator pushes the verified implementation branch and records its exact SHA before backend CI. Do not claim full backend acceptance yet.
- [ ] Have the orchestrator dispatch `.github/workflows/cross-platform-gpu.yml` on this branch with `legs=all`, `bake=false`, `tier=push`, then inspect that exact run's three backend results. This runs the full suite on Metal, Direct3D11/WARP and Vulkan/lavapipe, including non-Golden GPU facts that ordinary push filters omit on the latter two. Do not overlap dispatches on the same ref.

```bash
gh workflow run cross-platform-gpu.yml --ref feature/foliage-motion-profiles -f legs=all -f bake=false -f tier=push
```

All existing foliage grids must confirm unchanged. This plan adds behavioral readback facts, so it requires no new committed grids. If the orchestrator elects to add a new foliage golden scene, its new `.metal-native.txt`, `.direct3d11-native.txt` and `.vulkan-native.txt` files must be baked by their own hosted backend legs with `bake=true`, inspected via every bake evidence PNG, committed with explicit paths, then verified by a later `bake=false` full-matrix run. Existing Blade files cannot be replaced. GPU matrix verification is one ordinary proof, not a stress run.

- [ ] After all local and backend evidence is green, mark the index row implemented in staged `20.34.0`, commit only `docs/INDEX.md` with subject `docs(foliage): record motion profile implementation acceptance` and return its SHA to the orchestrator. The orchestrator verifies branch containment, merges/pushes `main` and runs the guarded local-feed packaging ritual. Release/tag and Grimhollow adoption remain separate owner-authorized lanes.
