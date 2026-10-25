# Indirect Diffuse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Opt-in directional indirect diffuse light, stored as L2 spherical harmonics irradiance and derived from the physical sky and ground bounce, through one shader slot that later stages can fill.

**Architecture:** A public `IrradianceSH` value type holds nine pre-convolved RGB coefficients. The atmosphere projects sky radiance into a sun-aligned SH table, rotates it to the live sun and moon, adds an exact ground hemisphere and writes the result to `PixelPostProcessSettings.AmbientIrradiance`. The frame uniform buffer gains an appended indirect tail, and the six lit fragments call `indirectDiffuse(N, worldPos)` instead of reading `Ambient.rgb`.

**Tech Stack:** C# (.NET), GLSL 450 cross-compiled to SPIR-V, MSL and HLSL, xUnit, GPU goldens.

**Spec:** `docs/design/INDIRECT-DIFFUSE-DESIGN-2026-10-24.md` (approved 2026-10-24). Issue [#1553](https://github.com/APKiwiOrg/KhaozEngine/issues/1553).

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/indirect-diffuse`, branch `feature/indirect-diffuse`. Run `mkdir -p local-feed` once before the first restore.
- `IndirectDiffuseMode.Uniform` is the default. In `Uniform` mode bytes `[0, 1376)` of the frame image are identical to today, the indirect tail is all zero and every existing golden is unchanged.
- SH basis, Y up, unit normal `n = (x, y, z)`. The GLSL and C# copies use exactly these constants:
  `Y0 = 0.282095`, `Y1 = 0.488603*y`, `Y2 = 0.488603*z`, `Y3 = 0.488603*x`, `Y4 = 1.092548*x*z`, `Y5 = 1.092548*y*z`,
  `Y6 = 0.315392*(3*y*y - 1)`, `Y7 = 1.092548*x*y`, `Y8 = 0.546274*(x*x - z*z)`.
- Stored coefficients are pre-convolved irradiance: `e_i = A_l * c_i` with `A_0 = π`, `A_1 = 2π/3`, `A_2 = π/4`, where `c_i = ∫ L(ω) Y_i(ω) dω`. `Evaluate(n) = max(Σ e_i Y_i(n), 0)` per channel.
- Azimuth is `atan2(dir.Z, dir.X)`. The table stores the source at azimuth 0, direction `(cos e, sin e, 0)`.
- D10 anchors: for source elevations of 5 degrees and above, `Evaluate(up)` matches `AtmosphereLighting.Ambient` within 0.1 percent per channel, and `|Evaluate(down) - Fill| <= 0.02 * (Ambient + Fill)` per channel.
- No allocation per frame in `Directional` mode.
- KESIZE cap 800 lines per file. New behaviour goes in new types. `ModelRenderer.cs` (671 lines) gains at most a constant change.
- Warnings are errors. No em or en dash glyphs anywhere, no prose semicolons in Markdown.
- Every build, test or render runs through `build-slot` (`--heavy` for GPU tests and full suites), one at a time. Never loop tests.
- Commit subjects follow the repository form, for example `feat(render3d): ...`, `test(render3d): ...`, `docs(render3d): ...`.
- Focused test command (replace the filter):
  `build-slot --label indirect -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~<Name>"`

## Spec decisions fixed by this plan

- D3: the two new `PixelPostProcessSettings` members are public fields, matching every other member of that class, so `AtmosphereGoldenScene` resets them from defaults through its field reflection.
- D8: the SH table lives in a new `AtmosphereIrradianceCache` type beside `AtmosphereLightingCache`, per the new-type rule. It uses the same 181 elevations, 8 altitude slices and interpolation as `AtmosphereLightingCache.Sample`, and each cell is normalised so its upward irradiance equals that cell's `UnitLighting.Ambient`. Linear interpolation then preserves the upward anchor exactly.
- D9: the indirect block is a tail appended at offset 1376 after the interior-cut tail: `vec4 IndirectParams` (x = 1 in `Directional`) then `vec4 IrradianceSH[9]` (xyz used), 160 bytes. `UboBytes` becomes 1536.
- `Directional` mode with the atmosphere off and `AmbientIrradiance` left at its default uploads `IrradianceSH.Uniform(AmbientColor)`, so switching the mode alone never removes indirect light.
- Release: 21.12.2 is staged with a fix. Raise that staged version to 21.13.0, since an additive feature is a minor, keeping its fix entry. Re-read `main` at integration.

## Review Focus

1. `Directional` with the atmosphere off and no SH set should still look lit. Pinned in Task 4.
2. Night, keyless night and moon-only frames should stay at or above the night floor at both poles. Pinned in Task 2.
3. A sun at azimuth near plus or minus π, or at the zenith where azimuth is undefined, should give continuous finite SH. Pinned in Task 2.
4. A camera between altitude slices or above the atmosphere top should keep the anchors. Pinned in Task 2.
5. Switching from `Directional` back to `Uniform` at runtime should restore the fill and zero the tail on the next frame. Pinned in Task 3.

---

### Task 1: `IrradianceSH`, `IndirectDiffuseMode` and SH projection

**Files:**
- Create: `KhaozEngine.Render3D/IndirectDiffuseMode.cs`, `KhaozEngine.Render3D/IrradianceSH.cs`, `KhaozEngine.Render3D/Internal/IrradianceProjection.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/IrradianceSHTests.cs`

**Interfaces:**
- Produces:
  - `public enum IndirectDiffuseMode { Uniform, Directional }`
  - `public readonly struct IrradianceSH : IEquatable<IrradianceSH>` with `public const int CoefficientCount = 9`, `public IrradianceSH(ReadOnlySpan<Vector3> coefficients)` (throws `ArgumentException` unless length 9), `public static IrradianceSH Zero` (equals `default`), `public static IrradianceSH Uniform(Vector3 irradiance)`, `public static IrradianceSH SkyAndGround(Vector3 skyIrradiance, Vector3 groundIrradiance)`, `public Vector3 Coefficient(int index)`, `public Vector3 Evaluate(Vector3 unitNormal)`, `public IrradianceSH Scale(Vector3 rgb)`, `public static IrradianceSH operator +(IrradianceSH a, IrradianceSH b)`, `internal IrradianceSH RotateY(float radians)`. Coefficients are nine private `Vector3` fields, not an array.
  - `internal struct IrradianceProjection` with `void Add(Vector3 unitDirection, Vector3 radiance, float solidAngle)` and `IrradianceSH ToIrradiance()` (applies the `A_l` weights).

- [ ] **Step 1: Write the failing tests** in `IrradianceSHTests` (namespace `KhaozEngine.Tests.Render3D`), with a local `AssertClose(Vector3 expected, Vector3 actual, float tolerance)`:

```csharp
[Fact] public void UniformEvaluatesToItsIrradianceInEveryDirection()      // (0.2,0.3,0.4) at up, down, +X, -Z, normalize(1,1,1), tol 1e-5
[Fact] public void SkyAndGroundHitsBothPolesAndAveragesTheHorizon()       // S=(1,.5,.25) G=(.1,.2,.3): up==S, down==G, +X==(S+G)/2, tol 1e-5
[Fact] public void ProjectingUniformRadianceMatchesUniform()               // 32x64 sphere grid, L=(.1,.2,.3): Evaluate(any)==π*L, rel tol 1e-3
[Fact] public void ProjectingTwoHemispheresMatchesSkyAndGround()          // upper L=S/π, lower L=G/π: matches SkyAndGround(S,G) at up, down, +X, rel tol 1e-2
[Fact] public void RotateYMatchesProjectingTheRotatedLobe()               // lobe max(dot(ω,d),0)^4, d=(cos30°, sin30°, 0), φ=1.1 rad, d'=(cos30°cosφ, sin30°, cos30°sinφ): all nine coefficients equal within 1e-4
[Fact] public void EvaluateNeverGoesNegative()                            // SkyAndGround((1,1,1), 0) + Uniform((-.2,-.2,-.2)): Evaluate(down)==Vector3.Zero
[Fact] public void TheSpanConstructorRejectsTheWrongLength()              // 8 coefficients throws ArgumentException
```

- [ ] **Step 2: Run** with filter `IrradianceSHTests`. Expected: build fails, the types do not exist.

- [ ] **Step 3: Implement** the three files. `Uniform` sets only `e0 = E / 0.282095`. `SkyAndGround` sets `e0 = ((S + G) / 2) / 0.282095` and `e1 = ((S - G) / 2) / 0.488603`, all others zero, which is exact for uniform hemispheres. `RotateY` keeps the zonal terms (indices 0, 1, 6), rotates the pair (3, 2) by φ, the pair (7, 5) by φ and the pair (8, 4) by 2φ, each as a 2D rotation of the two coefficients. The rotation test fixes the signs.

- [ ] **Step 4: Run** the same filter. Expected: 7 passed.

- [ ] **Step 5: Commit** `feat(render3d): add IrradianceSH and the indirect diffuse mode`.

### Task 2: Atmosphere irradiance table and `EvaluateIrradiance`

**Files:**
- Create: `KhaozEngine.Render3D/Internal/AtmosphereIrradianceCache.cs`, `KhaozEngine.Render3D/AtmosphereModel.Irradiance.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereIrradianceTests.cs`, `KhaozEngine.Render.Tests/Render3D/AtmosphereIrradianceAllocationTests.cs`

**Interfaces:**
- Consumes: Task 1 `IrradianceSH`, `IrradianceProjection`. Existing `AtmosphereModel.SkyRadiance`, `AtmosphereLightingCache`, `EvaluateLighting`.
- Produces: `public IrradianceSH EvaluateIrradiance(float cameraHeightKm, in AtmosphereFrame frame)` on `AtmosphereModel`, memoised on height and frame like `EvaluateLighting`, and `internal int IrradianceBuildCount { get; }`.

- [ ] **Step 1: Write the failing tests** in `AtmosphereIrradianceTests` using `IClassFixture<AtmosphereLightingFixture>` and the `Direction(elevationDegrees, azimuth)` helper pattern from `AtmosphereLightingTests`. The grid is elevations {5, 20, 45, 70, 89}, azimuths {0, 2.0, -3.1} and heights {0, 0.7, 2.0, 150} km (0.7 sits between slices, 150 is above the atmosphere top), with `new AtmosphereFrame(sun, -sun, 0, Vector3.One)`:

```csharp
[Fact] public void DaylightUpwardIrradianceMatchesTheAmbient()        // Evaluate(UnitY) vs EvaluateLighting(h, f).Ambient, rel 1e-3 per channel
[Fact] public void DownwardIrradianceIsTheFillPlusLittleSky()         // |Evaluate(-UnitY) - Fill| <= 0.02*(Ambient+Fill) per channel
[Fact] public void TheSkyFollowsTheSunAzimuth()                       // elevation 20: azimuth 0 gives Evaluate(+X) luminance > Evaluate(-X), azimuth π mirrors it within 1e-3
[Fact] public void AzimuthWrapAndZenithAreContinuous()               // azimuth π-1e-4 vs -π+1e-4 equal within 1e-4, elevation 90 finite and equal to elevation 89.99 within 1e-3
[Fact] public void NightFloorHoldsAtBothPoles()                       // sun -30 degrees, moon fraction 0 and 0.04: up and down >= NightAmbientFloor*NightTint - 1e-6 per channel
[Fact] public void AmbientScaleScalesTheSkyAndFillScaleTheGround()   // AmbientScale 2: up doubles. FillScale 0: down <= 0.02*Ambient
[Fact] public void TheTableBuildsOnceAndOnlyOnDemand()               // new model: after EvaluateLighting IrradianceBuildCount==0, after two EvaluateIrradiance frames ==1
```

`AtmosphereIrradianceAllocationTests` (`[Collection("AllocSensitive")]`): `EvaluatingChangingFramesAllocatesNothing` warms up, then wraps 200 `EvaluateIrradiance` calls with a moving sun in `AllocAssert.NoPerCallAllocation`.

- [ ] **Step 2: Run** with filter `AtmosphereIrradiance`. Expected: build fails, `EvaluateIrradiance` missing.

- [ ] **Step 3: Implement `AtmosphereIrradianceCache`.** Same elevation and height grid as `AtmosphereLightingCache`. Each cell projects `SkyRadiance(heightKm, direction, source)` over the dome on an 8 cosine by 16 azimuth grid (solid angle `π/64` per sample) with `IrradianceProjection`, stores the six coefficients that are even across the source plane (indices 0, 1, 3, 6, 7, 8), then scales the cell per channel so its upward evaluation equals the matching `UnitLighting.Ambient` (skip channels whose evaluation is zero). `Sample(heightKm, sourceMu)` mirrors `AtmosphereLightingCache.Sample` interpolation exactly.

- [ ] **Step 4: Implement `EvaluateIrradiance`** in `AtmosphereModel.Irradiance.cs`. Build the cache lazily, then:
  `sky = (Sample(h, sun.Y).RotateY(azimuth(sun)) + Sample(h, moon.Y).RotateY(azimuth(moon)).Scale(MoonFraction)).Scale(SunIlluminance * AmbientScale * Tint)`,
  `total = sky + IrradianceSH.SkyAndGround(Vector3.Zero, EvaluateLighting(h, frame).Fill)`.
  Night floor (D7): `floor = NightAmbientFloor * NightTint`, `shortfall = max(floor - min(total.Evaluate(up), total.Evaluate(down)), 0)` per channel, `total += IrradianceSH.Uniform(shortfall)`. Use the azimuth of a direction with zero horizontal length as 0.

- [ ] **Step 5: Run** with filter `AtmosphereIrradiance`. Expected: 8 passed. Record the one-time table build time from a temporary stopwatch in the commit message, then remove the stopwatch.

- [ ] **Step 6: Commit** `feat(render3d): derive SH irradiance from the physical sky and ground`.

### Task 3: Post fields and the atmosphere writers

**Files:**
- Modify: `KhaozEngine.Render3D/PixelPostProcessSettings.cs` (near `:223-229`), `KhaozEngine.Render3D/SunCycle.cs:613-616`, `KhaozEngine.Render3D/Scene3D.Atmosphere.cs:25-27`
- Create: `KhaozEngine.Render3D/Internal/AtmosphereSurfaceLighting.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereSurfaceLightingTests.cs`

**Interfaces:**
- Consumes: Task 2 `EvaluateIrradiance`.
- Produces: public fields `IndirectDiffuseMode IndirectDiffuse = IndirectDiffuseMode.Uniform` and `IrradianceSH AmbientIrradiance` on `PixelPostProcessSettings`. `internal static void AtmosphereSurfaceLighting.Write(PixelPostProcessSettings post, AtmosphereModel model, float heightKm, in AtmosphereFrame frame, Color ambient, Color fill)`, where each caller converts with its own colour function.

- [ ] **Step 1: Write the failing tests** (use `SunCycleAtmosphereTests` helpers such as `Equatorial` and `SunCycle.Evaluate` at noon for the state):

```csharp
[Fact] public void UniformModeWritesTodaysAmbientAndFill()            // SunCycle.Apply: post AmbientColor and FillLightColor equal the returned state's, fill rgb non-zero at noon, AmbientIrradiance == IrradianceSH.Zero
[Fact] public void DirectionalModeMovesTheBounceIntoTheIrradiance()  // FillLightColor rgb == 0, AmbientColor unchanged, AmbientIrradiance == model.EvaluateIrradiance(h, frame)
[Fact] public void SwitchingBackToUniformRestoresTheFill()           // Directional Apply then Uniform Apply: FillLightColor equals the Uniform value, AmbientIrradiance == Zero
```

- [ ] **Step 2: Run** with filter `AtmosphereSurfaceLightingTests`. Expected: build fails.

- [ ] **Step 3: Implement.** Add the two fields beside the ambient and fill fields with summaries naming D1 and D3. `Write` sets `AmbientColor = ambient` in both modes. In `Uniform` it sets `FillLightColor = fill` and `AmbientIrradiance = IrradianceSH.Zero`. In `Directional` it sets `FillLightColor` to zero RGB with the alpha of `fill`, and `AmbientIrradiance = model.EvaluateIrradiance(heightKm, frame)`. `SunCycle.Apply` passes `PhysicalColor(lighting.Ambient)` and `PhysicalColor(lighting.Fill)`, and `RecordAtmosphere` passes the `AtmosphereColor` equivalents, replacing their direct ambient and fill writes. Leave the returned `SunCycleState` unchanged.

- [ ] **Step 4: Run** the same filter plus `SunCycleAtmosphereTests` and `SceneAtmosphereRefreshTests`. Expected: all pass.

- [ ] **Step 5: Commit** `feat(render3d): write directional indirect light from the atmosphere`.

### Task 4: Frame tail, shader slot and the six lit fragments

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/IndirectUniforms.cs` (offset constants and tail writer, following `PointShadowUniforms.cs` and `InteriorCut.cs`)
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.FrameUbo.cs` (write the tail beside the other tails), `KhaozEngine.Render3D/Rendering/ModelRenderer.cs:60` (`UboBytes` 1536)
- Modify GLSL: the frame blocks of the six lit fragments only (`ModelFrag`, `ModelDissolveFrag`, `SkinnedModelFrag`, `SkinnedModelDissolveFrag`, `SplatFrag`, `TileGroundFrag`), which must already declare every member up to the interior-cut tail so the new members land at 1376. Vertex blocks stay unchanged, since trailing members they never read may be omitted. Also `ShaderSources.Lighting.cs` (function) and the ambient lines at `ShaderSources.Model.cs:199, 318, 524, 620`, `ShaderSources.Terrain.cs:242`, `ShaderSources.TileGround.cs:301`
- Test: `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs`, new `KhaozEngine.Render.Tests/Render3D/IndirectFrameTailTests.cs`, `FrameUboMaterialLodTests.cs:33` (compare `[0, 1376)` and a zero tail)

**Interfaces:**
- Consumes: Task 3 fields.
- Produces: `IndirectUniforms.TailOffset = 1376`, `IndirectUniforms.TailBytes = 160`, GLSL `vec3 indirectDiffuse(vec3 N, vec3 worldPos)`.

- [ ] **Step 1: Write the failing tests:**

```csharp
// UboLayoutTests
[Fact] public void UboBytes_Is1536_WithTheAppendedIndirectTail()        // replaces the 1376 assertion: tail offset 1376, tail bytes 160, UboBytes 1536
[Fact] public void LitFragments_DeclareTheIndirectTailAfterTheInteriorCut() // each of the six fragments declares the interior-cut members, then "vec4 IndirectParams;", then "vec4 IrradianceSH[9];"
[Fact] public void LitFragments_ReadIndirectDiffuse()                   // the six fragments contain "indirectDiffuse(" and none contains "Ambient.rgb + diffuse"
// IndirectFrameTailTests
[Fact] public void UniformModeLeavesTheHeaderUnchangedAndTheTailZero()
[Fact] public void DirectionalModeUploadsTheCoefficients()             // IndirectParams.x == 1, slot i xyz == AmbientIrradiance.Coefficient(i)
[Fact] public void DirectionalWithoutAtmosphereFallsBackToUniformAmbient() // atmosphere off, AmbientIrradiance default: slot 0 == Uniform(AmbientColor).Coefficient(0), slots 1 to 8 zero
```

- [ ] **Step 2: Run** with filter `UboLayoutTests|IndirectFrameTailTests|FrameUboMaterialLodTests`. Expected: failures on the new assertions.

- [ ] **Step 3: Implement the tail and the GLSL.** Append the two members after the interior-cut members in the six lit fragment blocks. If one of them lacks the interior-cut members, add them before the new tail so the offsets match. Add to `LightingCommonGlsl`:

```glsl
vec3 indirectDiffuse(vec3 N, vec3 worldPos) {
    if (IndirectParams.x < 0.5) return Ambient.rgb;
    float x = N.x, y = N.y, z = N.z;
    vec3 e = IrradianceSH[0].xyz * 0.282095
        + IrradianceSH[1].xyz * (0.488603 * y) + IrradianceSH[2].xyz * (0.488603 * z)
        + IrradianceSH[3].xyz * (0.488603 * x) + IrradianceSH[4].xyz * (1.092548 * x * z)
        + IrradianceSH[5].xyz * (1.092548 * y * z) + IrradianceSH[6].xyz * (0.315392 * (3.0 * y * y - 1.0))
        + IrradianceSH[7].xyz * (1.092548 * x * y) + IrradianceSH[8].xyz * (0.546274 * (x * x - z * z));
    return max(e, vec3(0.0));
}
```

  Replace `Ambient.rgb` with `indirectDiffuse(N, vWorldPos)` on the six lines, using `Ngeo` in `TileGroundFrag`. Keep the rest of each line unchanged.

- [ ] **Step 4: Run** the Step 2 filter plus `ShaderSourceValidationTests` and `MotionUboLayoutTests`. Expected: all pass, including `Model`, `Foliage`, `Splat`, `TileGround`, `ModelDissolve`, `SkinnedModel` and `SkinnedModelDissolve` on every cross-compile path.

- [ ] **Step 5: Commit** `feat(render3d): evaluate directional indirect light in the lit fragments`.

### Task 5: Golden proof on every backend

**Files:**
- Create: `KhaozEngine.Render.Tests/Gpu/IndirectDiffuseGoldenTests.cs`, goldens `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_indirect_directional.{metal-native,vulkan-native,direct3d11-native}.txt`

**Interfaces:**
- Consumes: Tasks 1 to 4. `AtmosphereGoldenScene` fixture, `GoldenCompare.AssertOrUpdate`.

- [ ] **Step 1: Write the tests.** `[GpuFact] Golden_IndirectDirectionalNoon` renders `scene3d_indirect_directional` at noon with the atmosphere on and `Directional` mode: a 6 by 0.3 by 6 m slab on four thin posts 3 m above lit ground, the camera 1.5 m above the ground under the slab edge, pitched 35 degrees up so the underside fills the upper half. `[GpuFact] TheUndersideIsLitByTheGround` renders the same scene in `Uniform` and `Directional` and asserts the underside centre pixel luminance in `Directional` is at least twice the `Uniform` value.

- [ ] **Step 2: Bake and check on Metal:**
  `build-slot --heavy --label indirect-golden -- env KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~IndirectDiffuseGoldenTests"`
  Inspect the captured image under `/tmp/khaozengine-indirect/`. Then run every golden without `KE_UPDATE_GOLDENS` (filter `FullyQualifiedName~Golden`). Expected: all existing goldens pass unchanged.

- [ ] **Step 3: Bake Vulkan and D3D11** through the hosted backend legs described in `docs/CROSS-PLATFORM.md`, commit the two returned golden files and confirm both legs pass with no other golden changed.

- [ ] **Step 4: Commit** `test(render3d): pin directional indirect light with a golden per backend`.

### Task 6: Contract docs, version and full verification

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md` (physical atmosphere bullets near `:5279-5281`, formulas near `:5319-5327`, ambient note near `:5374`), `KhaozEngine.Render3D/README.md` (near `:626-628`, `:655-658`, `:683`), `CHANGELOG.md`, `Directory.Build.props:25`, `README.md` and `docs/USING-KHAOZENGINE.md` PackageReference examples, `docs/design/INDIRECT-DIFFUSE-DESIGN-2026-10-24.md` status line, `docs/INDEX.md:54` status

- [ ] **Step 1: Document** `IndirectDiffuse`, `AmbientIrradiance`, `IrradianceSH`, the zero fill in `Directional`, the floor and the fallback without the atmosphere, and the unchanged default. Sweep every Markdown file for `AmbientColor`, `FillLightColor` and "ambient" claims the feature makes stale.

- [ ] **Step 2: Version.** Re-read `main`, the version and tags. If 21.12.2 is still unreleased, raise it to 21.13.0, keeping its fix entry and adding the feature entry with `Closes #1553`. Mark the design doc "Accepted implementation contract for 21.13.0" and update the index status.

- [ ] **Step 3: Full verification** from the worktree root, each through `build-slot` (`--heavy` for the test run):

```sh
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

  Expected: zero warnings, all tests pass, every guard exits 0.

- [ ] **Step 4: Commit** `docs(render3d): document directional indirect diffuse for 21.13.0`, then push the branch. Merging to `main`, packing and tagging belong to the orchestrator and the owner-approved release.
