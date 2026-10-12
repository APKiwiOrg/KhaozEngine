# Physical Atmosphere Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in physical atmosphere that supplies a coherent sky, scene lighting, reflected sky and analytic distance fog.
**Architecture:** A public CPU model computes the same transport as three internal fullscreen LUT passes, with cached lighting and no GPU readback. SunCycle supplies the celestial geometry and existing key handover, while one shared fog chunk and fixed resource bindings cover the existing geometry and effect pipelines. The legacy branch remains the default.
**Tech Stack:** C#/.NET 10, System.Numerics, xUnit, GLSL 450, Silk.NET.Shaderc and SPIR-V Cross, native Metal, Direct3D11 and Vulkan.
**Spec:** docs/design/PHYSICAL-ATMOSPHERE-DESIGN-2026-10-12.md

## Global Constraints

- Phase 1 only. Clouds, cloud shadows, stars, eye adaptation, the aerial perspective volume and Grimhollow adoption stay with their existing issues.
- `AtmosphereSettings.Enabled` defaults to false, so every existing scene renders as before.
- Planet radius 6360 km, atmosphere top 6460 km, atmosphere height 100 km.
- Rayleigh scattering `(5.802, 13.558, 33.1) e-3` per km with scale height 8 km.
- Mie scattering `3.996e-3` per km, scale height 1.2 km, anisotropy 0.8. Use extinction `4.440e-3` per km, checked in the reference, rather than the request's rounded `4.40e-3`.
- Ozone absorption `(0.650, 1.881, 0.085) e-3` per km, a tent centred at 25 km with 30 km full width, supported on [10, 40] km.
- Ground albedo 0.3 is the requested preset. The reference demo initializes it to zero, which this plan deliberately does not adopt.
- World units per kilometre, default 1000 (one unit is one metre). Atmospheric altitude uses absolute world Y, with Y = 0 at the planet surface.
- `LightColor = S * T_sun`, `Ambient = S * E_sky(up)`, `SkyValue = S * pi * L_sky(direction)`. The moon adds the same terms multiplied by its illuminance fraction. Do not insert `1/pi` into the engine's surface lighting.
- Transmittance 256 by 64, multi-scattering 32 by 32, sky-view 192 by 108. All are half-float fullscreen fragment targets. Static LUTs rebuild on physical-settings changes, sky-view every enabled frame before scene draws.
- Shader mirror comments name the corresponding C# method. Tints multiply sky, fog colour, key, ambient and fill exactly once. Ambient and fill scales affect lighting only.
- Keep shadows, the surface lighting model, TAA, background depth state, existing scene pass order and starfield unchanged. Do not fix #1153 here.
- Warnings are errors in every configuration. Fix warnings at their source, without blanket suppressions.
- No em or en dash characters and no prose semicolons in Markdown. Preserve unrelated work and stage explicit paths.
- Ride the unreleased version at merge. At plan time the pin and top changelog heading are 20.33.0, newest local release tag v20.32.0. Re-check current main and tags before integrating.
- Every local build, test, render, bake or pack uses `build-slot --label <label> -- <command>`. Full suites, GPU runs and packaging also use `--heavy`.
- No repeated test runs, local stress or CPU burners. Run each task's failing and passing test once, then the full suite once at the finish.
- GPU facts require `KE_GPU_TESTS=1`. New golden grids come from the owning hosted CI legs, never a dev-Mac rebake substituted for another backend.
- Work only in `/Users/antonio/KhaozEngine/.worktrees/physical-atmosphere`, branch `feature/physical-atmosphere`. Delegated implementers commit and stop. The owning orchestrator handles current-main reconciliation, push, package production and issue updates. No automatic release tag.

## Review Focus

- Invalid or vacuum inputs must not create NaNs, divide by zero or poison caches. Tasks 1/3 pin InvalidSettings_AreRejected/Vacuum_HasNoScatteredRadiance.
- The moon can have a different azimuth from the sun. Both scattering lobes must survive without mirroring the moon about the solar plane. Task 3 `IndependentMoonAzimuth_IsNotFolded`, Task 7 `Golden_IndependentMoonAndAzimuthSeam`.
- Mutating the existing settings object or disabling and re-enabling the atmosphere must refresh the right data without stale lighting or extra static passes. Task 4 `SettingsMutation_RebuildsOnlyAffectedCaches`, Task 6 `ToggleAndArtChanges_DoNotRebakeStaticLuts`.
- A render-origin step with a nonzero Y component must preserve absolute fog height and relative segment length. Task 9 `RebaseY_LeavesFogIdentical`, Task 12 `Golden_FogSurvivesOriginStep`.
- Alpha, premultiplied particles, additive effects and billboards drawn after tonemapping must retain coverage and use the correct colour space. Task 11 `BlendModes_DoNotInjectAdditiveHaze` and `Golden_DisplayBillboardFogUsesTonemappedHorizon`.

---

## File Structure

Paths and line ranges below refer to the inspected branch before implementation. Re-find the named symbol after preceding tasks move lines. New tests keep the namespace `KhaozEngine.Tests.Render3D` or `KhaozEngine.Tests.Gpu`, as appropriate.

| Create | One responsibility |
| --- | --- |
| `KhaozEngine.Render3D/AtmosphereSettings.cs` | Validated preset and controls |
| `KhaozEngine.Render3D/AtmosphereModel.cs` | CPU optical core |
| `KhaozEngine.Render3D/Internal/AtmosphereLutMapping.cs` | LUT coordinates |
| `KhaozEngine.Render3D/AtmosphereModel.Scattering.cs` | CPU scattering |
| `KhaozEngine.Render3D/Internal/AtmosphereTables.cs` | CPU static tables |
| `KhaozEngine.Render3D/AtmosphereModel.Lighting.cs` | CPU lighting |
| `KhaozEngine.Render3D/AtmosphereLighting.cs` | Public frame/results |
| `KhaozEngine.Render3D/Internal/AtmosphereLightingCache.cs` | CPU lighting cache |
| `KhaozEngine.Render3D/Internal/SunCycleAtmosphereSample.cs` | Cycle geometry/fades |
| `KhaozEngine.Render3D/Rendering/AtmosphereBindings.cs` | Shared resource suffix |
| `KhaozEngine.Render3D/Rendering/AtmosphereRenderer.cs` | LUT renderer |
| `KhaozEngine.Render3D/Internal/AtmosphereUpdatePolicy.cs` | LUT invalidation |
| `KhaozEngine.Render3D/Internal/ShaderSources.Atmosphere.cs` | Transport shaders |
| `KhaozEngine.Render3D/Scene3D.Atmosphere.cs` | Scene atmosphere wiring |
| `KhaozEngine.Render3D/AtmosphereFog.cs` | CPU fog |
| `KhaozEngine.Render3D/Internal/ShaderSources.AtmosphereFog.cs` | Fog shaders |
| `KhaozEngine.Render3D/Internal/ShaderSources.TonemapCommon.cs` | Shared tonemap |
| `KhaozEngine.Render.Tests/Render3D/AtmosphereSettingsTests.cs` | Settings tests |
| `KhaozEngine.Render.Tests/Render3D/AtmosphereModelTests.cs` | Core oracle tests |
| `KhaozEngine.Render.Tests/Render3D/AtmosphereLutMappingTests.cs` | Mapping tests |
| `KhaozEngine.Render.Tests/Render3D/AtmosphereScatteringTests.cs` | Scattering tests |
| `KhaozEngine.Render.Tests/Render3D/AtmosphereLightingTests.cs` | Lighting tests |
| `KhaozEngine.Render.Tests/Render3D/SunCycleAtmosphereTests.cs` | Cycle tests |
| `KhaozEngine.Render.Tests/Render3D/AtmosphereRendererTests.cs` | Renderer tests |
| `KhaozEngine.Render.Tests/Render3D/AtmosphereFogTests.cs` | Fog tests |
| `KhaozEngine.Render.Tests/Gpu/AtmosphereLutGoldenTests.cs` | LUT GPU tests |
| `KhaozEngine.Render.Tests/Gpu/AtmosphereGoldenScene.cs` | GPU fixture/timing |
| `KhaozEngine.Render.Tests/Gpu/AtmosphereSkyGoldenTests.cs` | Sky GPU tests |
| `KhaozEngine.Render.Tests/Gpu/AtmosphereFogGoldenTests.cs` | Fog GPU tests |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_noon.metal-native.txt` | noon metal-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_noon.direct3d11-native.txt` | noon direct3d11-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_noon.vulkan-native.txt` | noon vulkan-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_sunset.metal-native.txt` | sunset metal-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_sunset.direct3d11-native.txt` | sunset direct3d11-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_sunset.vulkan-native.txt` | sunset vulkan-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_moonlit.metal-native.txt` | moonlit metal-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_moonlit.direct3d11-native.txt` | moonlit direct3d11-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_moonlit.vulkan-native.txt` | moonlit vulkan-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_fog.metal-native.txt` | fog metal-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_fog.direct3d11-native.txt` | fog direct3d11-native grid |
| `KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_fog.vulkan-native.txt` | fog vulkan-native grid |
| `docs/design/PHYSICAL-ATMOSPHERE-PHASE-1-EVIDENCE.md` | Verification evidence |

| Modify | One responsibility |
| --- | --- |
| `KhaozEngine.Render3D/PixelPostProcessSettings.cs` | Atmosphere/background settings |
| `KhaozEngine.Render3D/SunCycle.cs` | Physical Apply |
| `KhaozEngine.Render3D/SkySettings.cs` | Sky precedence docs |
| `KhaozEngine.Render3D/Internal/SkyMath.cs` | CPU reflected sky |
| `KhaozEngine.Render3D/Rendering/SkyRenderer.cs` | Physical sky draw |
| `KhaozEngine.Render3D/Scene3D.cs` | Atmosphere lifetime/recording |
| `KhaozEngine.Render3D/Scene3D.TemporalResolve.cs` | Background draw |
| `KhaozEngine.Render3D/Internal/ShaderSources.Sky.cs` | Sky shader |
| `KhaozEngine.Render3D/Internal/ShaderSources.Water.cs` | Water shader |
| `KhaozEngine.Render3D/Rendering/WaterRenderer.cs` | Water bindings |
| `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs` | Model fog |
| `KhaozEngine.Render3D/Internal/ShaderSources.Terrain.cs` | Splat fog |
| `KhaozEngine.Render3D/Internal/ShaderSources.TileGround.cs` | Tile fog |
| `KhaozEngine.Render3D/Rendering/ModelRenderer.cs` | Model bindings |
| `KhaozEngine.Render3D/Rendering/ModelRenderer.Splat.cs` | Splat bindings |
| `KhaozEngine.Render3D/Rendering/ModelRenderer.TileGround.cs` | Ground bindings |
| `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs` | Skinned bindings |
| `KhaozEngine.Render3D/Rendering/ModelRenderer.ShadowLayoutReplacement.cs` | Replacement bindings |
| `KhaozEngine.Render3D/Internal/ShaderSources.Effects.cs` | Effect shaders |
| `KhaozEngine.Render3D/Internal/ShaderSources.Decal.cs` | Decal fog |
| `KhaozEngine.Render3D/Rendering/GroundDecalRenderer.cs` | Decal bindings |
| `KhaozEngine.Render3D/Rendering/ParticleRenderer.cs` | Particle bindings |
| `KhaozEngine.Render3D/Rendering/TexturedBillboardRenderer.cs` | Textured billboard bindings |
| `KhaozEngine.Render3D/Rendering/BillboardRenderer.cs` | Billboard fog |
| `KhaozEngine.Render3D/Rendering/OverlayRenderer.cs` | Optional fog bindings |
| `KhaozEngine.Render3D/Rendering/TrailRenderer.cs` | Trail bindings |
| `KhaozEngine.Render3D/Rendering/BeamRenderer.cs` | Beam bindings |
| `KhaozEngine.Render3D/Internal/ShaderSources.Post.cs` | Tonemap reuse |
| `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs` | UBO tripwires |
| `KhaozEngine.Render.Tests/Render3D/SkyMathTests.cs` | Mirror tests |
| `KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs` | Shader validation |
| `KhaozEngine.Render.Tests/Gpu/GoldenSnapshotTests.cs` | Disabled regression |
| `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs` | Shader catalog |
| `KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs` | Program count |
| `KhaozEngine.Render.Tests/Gpu/VulkanSpirvByteEqualityTests.cs` | Stage count |
| `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt` | Emission hashes |
| `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt` | Emission hashes |
| `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt` | Emission hashes |
| `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt` | Shader corpus |
| `CHANGELOG.md` | Staged changelog |
| `docs/USING-KHAOZENGINE.md` | Public API docs |
| `KhaozEngine.Render3D/README.md` | Package docs |
| `docs/INDEX.md` | Program status |
| `NOTICE.md` | Attribution |

`Directory.Build.props` and declarations found by `scripts/check-doc-versions.sh` change only if the re-check finds no staged version. Do not preselect a fresh version in this plan.

### Task 1: Settings, preset and world mapping

**Files:**
- Create: `KhaozEngine.Render3D/AtmosphereSettings.cs`
- Modify: `KhaozEngine.Render3D/PixelPostProcessSettings.cs:55-83`
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereSettingsTests.cs`

**Interfaces:**
- Consumes: existing `PixelPostProcessSettings.Sky : SkySettings`, `.Water : WaterSettings`, `.Hdr : HdrSettings`.
- Produces: `public sealed class AtmosphereSettings`, `public static AtmosphereSettings Earth()`, `public void Validate()`, `public float HeightKm(float absoluteWorldY)`.
- Produces: public mutable properties `bool Enabled`, `float PlanetRadiusKm`, `AtmosphereHeightKm`, `RayleighScaleHeightKm`, `MieScatteringPerKm`, `MieExtinctionPerKm`, `MieScaleHeightKm`, `MieAnisotropy`, `OzoneCenterKm`, `OzoneWidthKm`, `GroundAlbedo`, `SunIlluminance`, `MoonIlluminanceFraction`, `SunAngularRadiusRadians`, `WorldUnitsPerKilometre`, `AerialDistanceScale`, `AmbientScale`, `FillScale`, `NightAmbientFloor`, `NightSkyFloor`, `HazeDensityPerKm`, `HazeScaleHeightKm`, and `Vector3 RayleighScatteringPerKm`, `OzoneAbsorptionPerKm`, `DayTint`, `DuskTint`, `NightTint`.
- Produces: `PixelPostProcessSettings.Atmosphere : AtmosphereSettings = new()`.

- [ ] **Step 1: Write the failing test** `EarthPreset_IsDisabledAndExact` and `InvalidSettings_AreRejected`.

```csharp
var s = AtmosphereSettings.Earth();
Assert.False(new PixelPostProcessSettings().Atmosphere.Enabled);
Assert.Equal(6360f, s.PlanetRadiusKm);
Assert.Equal(100f, s.AtmosphereHeightKm);
Assert.Equal(new Vector3(.005802f, .013558f, .0331f), s.RayleighScatteringPerKm);
Assert.Equal(.004440f, s.MieExtinctionPerKm);
Assert.Equal(.003996f, s.MieScatteringPerKm);
Assert.Equal(.8f, s.MieAnisotropy);
Assert.Equal(new Vector3(.000650f, .001881f, .000085f), s.OzoneAbsorptionPerKm);
Assert.Equal(8f, s.RayleighScaleHeightKm);
Assert.Equal(1.2f, s.MieScaleHeightKm);
Assert.Equal(25f, s.OzoneCenterKm);
Assert.Equal(30f, s.OzoneWidthKm);
Assert.Equal(.3f, s.GroundAlbedo);
Assert.Equal(.08f, s.MoonIlluminanceFraction);
Assert.Equal(.08f, s.NightAmbientFloor);
Assert.Equal(.02f, s.NightSkyFloor);
Assert.Equal(1f, s.HeightKm(1000f));
s.WorldUnitsPerKilometre = 0f;
Assert.Throws<ArgumentOutOfRangeException>(s.Validate);
s.WorldUnitsPerKilometre = 1000f;
s.MieExtinctionPerKm = .003f;
Assert.Throws<ArgumentOutOfRangeException>(s.Validate);
```

Theory rows NaN illuminance, infinite density, negative RGB, g=1, H=0 and albedo=1.01 all throw ArgumentOutOfRangeException. Zero coefficients/illuminance/art scales are valid.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-settings-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereSettingsTests"`

Expected: compile failure for the new type/property.

- [ ] **Step 3: Implement** `AtmosphereSettings.Earth() -> AtmosphereSettings` and `Validate() -> void` in `KhaozEngine.Render3D/AtmosphereSettings.cs`, plus the Post bag. Validate finite values, positive radii/heights/world-units/falloffs, nonnegative aerial scale, nonnegative RGB/scales/floors, extinction >= scattering, g in (-1,1), albedo [0,1], angular radius (0,.1]. Compare settings by value.

Choose `SunIlluminance = 1`, `MoonIlluminanceFraction = .08`, `SunAngularRadiusRadians = .004675`, `AerialDistanceScale = 20`, ambient/fill scales 1, floors `.08` and `.02`, haze density 0 and haze height `.25` km. Day/dusk tints are `Vector3.One`, night tint `(.65,.8,1)`. These keep the normalized noon scale, approximate the existing night-light scale and keep moonless sky above RGBA8 black at exposure 1, use the checked solar radius, expose kilometre-scale fog, and keep mist opt-in. `HeightKm` divides absolute Y by 1000 by default, with no XZ-derived planet curvature or render-origin subtraction. CPU transport clamps underground density to surface density.

Source for checked values: [SetupEarthAtmosphere](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Application/SkyAtmosphereCommon.cpp). Retain the requested albedo despite the demo's zero default.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-settings-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereSettingsTests"`

Expected: all preset/default/validation rows pass, zero warnings.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/AtmosphereSettings.cs
  KhaozEngine.Render3D/PixelPostProcessSettings.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereSettingsTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): add Earth settings and world mapping" -- "${atmo_commit_paths[@]}"
```

### Task 2: CPU transmittance and invertible LUT coordinates

**Files:**
- Create: `KhaozEngine.Render3D/AtmosphereModel.cs`, `KhaozEngine.Render3D/Internal/AtmosphereLutMapping.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereModelTests.cs`, `KhaozEngine.Render.Tests/Render3D/AtmosphereLutMappingTests.cs`

**Interfaces:**
- Consumes: `AtmosphereSettings.Validate() -> void`, `HeightKm(float) -> float`.
- Produces: `public sealed partial class AtmosphereModel`, constructor `(AtmosphereSettings settings)`, `public bool UpdateSettings(AtmosphereSettings settings)`, `public Vector3 Density(float heightKm)`, `public Vector3 Transmittance(Vector3 positionKm, Vector3 direction, int steps = 256)`, `public static bool IntersectSphere(Vector3 originKm, Vector3 direction, float radiusKm, out Vector2 distancesKm)`.
- Produces: internal `AtmosphereLutMapping.TransmittanceUv(float radiusKm, float mu, float bottomKm, float topKm) -> Vector2`, inverse `TransmittanceParameters(Vector2 uv, float bottomKm, float topKm) -> Vector2` (radius, mu), `MultiUv(float heightKm, float sunMu, float heightLimitKm) -> Vector2`, inverse `MultiParameters(Vector2 uv, float heightLimitKm) -> Vector2` (height, sunMu), `SkyUv(Vector3 direction, float radiusKm, float bottomKm) -> Vector2`, inverse `SkyDirection(Vector2 uv, float radiusKm, float bottomKm) -> Vector3`.

- [ ] **Step 1: Write the failing test** `SphereAndDensity_IncludeGroundAndTent`, `EarthTransmittance_MatchesIndependentOracle`, and `EveryLutMapping_RoundTrips`.

```csharp
Assert.True(AtmosphereModel.IntersectSphere(new Vector3(0, 2, 0), -Vector3.UnitY, 1f, out var t));
Assert.Equal(new Vector2(1, 3), t);
var m = new AtmosphereModel(AtmosphereSettings.Earth());
Assert.Equal(new Vector3(1, 1, 0), m.Density(0));
Assert.Equal(1f, m.Density(25).Z);
Assert.Equal(0f, m.Density(10).Z);
Assert.Equal(0f, m.Density(40).Z);
Assert.Equal(Vector3.Zero, m.Transmittance(new Vector3(0, 6361, 0), -Vector3.UnitY));
Assert.Equal(Vector3.One, m.Transmittance(new Vector3(0, 6461, 0), Vector3.UnitY));
Assert.InRange(Vector3.Distance(m.Transmittance(new Vector3(0, 6360, 0), Vector3.UnitY),
    IndependentTransmittance(0, 1, 32768)), 0, .002f);
```

Define test-only IndependentTransmittance(double heightKm,double mu,int intervals) -> Vector3: independent double sphere roots, literal densities, Simpson integration, no production helpers. Rows (0,1),(0,.1),(2,.5),(25,0), 32768 intervals, RGB distance <=.002. Ground-outward T>0, inward T=0, zero direction throws ArgumentException, non-occluded tangent mu ±1e-5 differs <=.005.

Round trips: transmittance/multi UV (.1,.1),(.5,.5),(.9,.9), errors <=2e-4/1e-5. Sky UV (.1,.1),(.4,.49),(.7,.51),(.9,.9), heights 0/2 km, error <=2e-4. Poles compare vectors, not undefined longitude.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-core-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereModelTests|FullyQualifiedName~AtmosphereLutMappingTests"`

Expected: compile failure for model/mapping methods.

- [ ] **Step 3: Implement** `AtmosphereModel.Transmittance(Vector3 positionKm, Vector3 direction, int steps = 256) -> Vector3` in `KhaozEngine.Render3D/AtmosphereModel.cs` and the mappings in `Internal/AtmosphereLutMapping.cs`. Use double intermediates, normalized finite nonzero directions, both roots, atmosphere entry/exit and planet occlusion. Use 256 quadratic midpoint segments. Density returns `(exp(-max(h,0)/8), exp(-max(h,0)/1.2), max(0,1-abs(h-25)/15))` for Earth. `UpdateSettings` returns false for a value-identical snapshot and true for a changed snapshot.

Use these mappings, with radius r, bottom R, top A. Encode normalized coordinates at texel centres with `sub(u,N) = (u*(N-1)+.5)/N`, inverse `(uv*N-.5)/(N-1)`, for the bounded axes. At degenerate endpoints return the canonical outward pole.

```text
Transmittance: H=sqrt(A*A-R*R), rho=sqrt(max(r*r-R*R,0))
 d=-r*mu+sqrt(max(r*r*(mu*mu-1)+A*A,0))
 x=(d-(A-r))/(rho+H-(A-r)), y=rho/H
Inverse: rho=H*y, r=sqrt(R*R+rho*rho), d=(A-r)+x*(rho+H-(A-r))
 mu=(H*H-rho*rho-d*d)/(2*r*d), or 1 when d=0
Multi: x=(sunMu+1)/2, y=height/(A-R), both 32-centre mapped
Sky: u=wrap(atan2(direction.z,direction.x)/(2*pi)), theta=acos(direction.y)
 thetaH=acos(-sqrt(max(r*r-R*R,0))/r)
 v=.5*(1-sqrt(1-theta/thetaH)) for theta<=thetaH
 v=.5+.5*sqrt((theta-thetaH)/(pi-thetaH)) otherwise
Inverse: theta=thetaH*(1-(1-2*v)^2) below v=.5
 theta=thetaH+(pi-thetaH)*(2*v-1)^2 otherwise
 direction=(sin(theta)*cos(2*pi*u), cos(theta), sin(theta)*sin(2*pi*u))
```

Sky V uses 108-centre mapping, U stays periodic. Full azimuth preserves independent moon geometry. Clamp root/cosine domains. Planet-blocked transmittance returns zero before LUT lookup.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-core-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereModelTests|FullyQualifiedName~AtmosphereLutMappingTests"`

Expected: all geometry, oracle and round-trip rows pass.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/AtmosphereModel.cs
  KhaozEngine.Render3D/Internal/AtmosphereLutMapping.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereModelTests.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereLutMappingTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): implement transmittance and LUT mappings" -- "${atmo_commit_paths[@]}"
```

### Task 3: Single scattering and the multi-scattering approximation

**Files:**
- Create: `KhaozEngine.Render3D/AtmosphereModel.Scattering.cs`, `KhaozEngine.Render3D/Internal/AtmosphereTables.cs`
- Modify: `KhaozEngine.Render3D/AtmosphereModel.cs` (Task 2 snapshot update)
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereScatteringTests.cs`

**Interfaces:**
- Consumes: `Transmittance(Vector3, Vector3, int = 256) -> Vector3`, `Density(float) -> Vector3`, Task 2 mappings.
- Produces: public `SingleScattering(Vector3 positionKm, Vector3 direction, Vector3 lightDirection) -> Vector3`, `MultiScattering(float heightKm, float lightMu) -> Vector3`, `SkyRadiance(float cameraHeightKm, Vector3 direction, Vector3 lightDirection) -> Vector3`, all for unit source illuminance, without art tint or engine `pi` conversion.
- Produces: internal `AtmosphereTables` with 256x64 transmittance and 32x32 multi-scattering arrays, bilinear samplers `SampleTransmittance(float radiusKm, float mu) -> Vector3`, `SampleMulti(float heightKm, float mu) -> Vector3` and `int BuildCount` on the model for cache tests.

- [ ] **Step 1: Write the failing test** `Vacuum_HasNoScatteredRadiance`, `Scattering_IsPositiveAndForwardMieIsBrighter`, `IndependentMoonAzimuth_IsNotFolded`.

```csharp
var vacuum = AtmosphereSettings.Earth();
vacuum.RayleighScatteringPerKm = vacuum.OzoneAbsorptionPerKm = Vector3.Zero;
vacuum.MieScatteringPerKm = vacuum.MieExtinctionPerKm = vacuum.GroundAlbedo = 0;
var v = new AtmosphereModel(vacuum);
Assert.Equal(Vector3.Zero, v.SkyRadiance(0, Vector3.UnitY, Vector3.UnitY));
Assert.Equal(Vector3.Zero, v.MultiScattering(0, 1));
var m = new AtmosphereModel(AtmosphereSettings.Earth());
Assert.True(m.SkyRadiance(.002f, Vector3.UnitY, Vector3.UnitY).Z > 0);
Assert.True(m.MultiScattering(1, .5f).X > 0);
var moon = Vector3.Normalize(new Vector3(1, .1f, 0));
var mirrored = Vector3.Normalize(new Vector3(-1, .1f, 0));
Assert.True(m.SkyRadiance(.002f, moon, moon).Length() >
    m.SkyRadiance(.002f, mirrored, moon).Length() * 1.2f);
```

Also implement/test internal static RayleighPhase(float cosine)->float, MiePhase(float cosine,float g)->float and SegmentIntegral(Vector3 source,Vector3 extinction,float distanceKm)->Vector3. Assert pR(0)=3/(16*pi), pR(1)=3/(8*pi) within 1e-6, phase integrals=1 within .002, and zero-extinction integral=source*distance.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-scatter-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereScatteringTests"`

Expected: missing scattering methods.

- [ ] **Step 3: Implement** `AtmosphereModel.SkyRadiance(float cameraHeightKm, Vector3 direction, Vector3 lightDirection) -> Vector3` in `KhaozEngine.Render3D/AtmosphereModel.Scattering.cs`. Use 32 quadratic view segments, exact segment integration, table-sampled source transmittance and planet visibility. SingleScattering excludes MS. Build transmittance first, then MS with the recursive source zero: 64 equal-solid-angle directions, 20 quadratic segments, isotropic phase, direct planet bounce. Tables are immutable, bilinear and physical-settings keyed.

```text
cosTheta = dot(viewToward, lightToward)
 pR = 3*(1+cosTheta^2)/(16*pi)
 pM = 3*(1-g*g)*(1+cosTheta^2)/(8*pi*(2+g*g)*(1+g*g-2*g*cosTheta)^1.5)
For each view segment:
 sigmaE = betaR*dR + betaMext*dM + betaO*dO
 sigmaS = betaR*dR + betaMsca*dM
 source = Tlight*visible*(betaR*dR*pR + betaMsca*dM*pM) + PsiMS(height,lightMu)*sigmaS
 q = (1-exp(-sigmaE*ds))/sigmaE, using ds when sigmaE=0
 L += throughput*source*q, throughput *= exp(-sigmaE*ds)
For the MS build, average directional unit-source radiance L2 and response fMS over 4*pi:
 PsiMS = mean(L2)/(1-min(mean(fMS), .999)), per RGB channel
```

Sample 8x8 midpoint z/azimuth cells over [-1,1] and [0,2*pi]. Ground-hit rays add `Tview*Tlight*max(dot(Nground,light),0)*albedo/pi`. SkyRadiance integrates phase-aware single plus isotropic MS.

The transport algorithm follows [Hillaire's reference ray marcher](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyRayMarching.hlsl). Port only the relevant math, not its compute/threading, shadows or volume machinery. Track ported source paths for Task 13 attribution.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-scatter-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereScatteringTests"`

Expected: finite vacuum results, normalized phases and positive scattering, independent azimuth assertion passes.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/AtmosphereModel.cs
  KhaozEngine.Render3D/AtmosphereModel.Scattering.cs
  KhaozEngine.Render3D/Internal/AtmosphereTables.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereScatteringTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): derive single and multiple scattering" -- "${atmo_commit_paths[@]}"
```

### Task 4: Derived lighting, tint and settings-only CPU caches

**Files:**
- Create: `KhaozEngine.Render3D/AtmosphereModel.Lighting.cs`, `KhaozEngine.Render3D/AtmosphereLighting.cs`, `KhaozEngine.Render3D/Internal/AtmosphereLightingCache.cs`
- Modify: `KhaozEngine.Render3D/AtmosphereModel.cs` (Task 2 UpdateSettings)
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereLightingTests.cs`

**Interfaces:**
- Consumes: Task 3 `SkyRadiance(float, Vector3, Vector3) -> Vector3`, `MultiScattering(float, float) -> Vector3`, Task 2 transmittance.
- Produces: frame directions point toward bodies, never light-travel directions. Public readonly record struct `AtmosphereFrame(Vector3 SunDirection, Vector3 MoonDirection, float MoonFraction, Vector3 Tint, KeyLightSource KeySource = KeyLightSource.Sun, float KeyVisibility = 1f, float SunOpacity = 1f, float MoonOpacity = 1f)`.
- Produces: public readonly record struct `AtmosphereLighting(Vector3 Key, Vector3 Ambient, Vector3 Fill, Vector3 Horizon, Vector3 SunTransmittance, Vector3 MoonTransmittance)`.
- Produces: public `EvaluateLighting(float cameraHeightKm, in AtmosphereFrame frame) -> AtmosphereLighting`, `SkyValue(float cameraHeightKm, Vector3 direction, in AtmosphereFrame frame) -> Vector3`, internal `Tint(Vector3 dayDuskNightWeights) -> Vector3`, `int LightingBuildCount`.

- [ ] **Step 1: Write the failing test** `Lighting_UsesLambertUnitsAndTintOnce`, `SunKey_ReddensAndDisappears`, `NightFloors_AndHorizonAreContinuous`, `SettingsMutation_RebuildsOnlyAffectedCaches`.

```csharp
var s = AtmosphereSettings.Earth();
var m = new AtmosphereModel(s);
var f = new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, 0, Vector3.One);
var l = m.EvaluateLighting(.002f, f);
Assert.InRange(Vector3.Distance(l.Key, l.SunTransmittance * s.SunIlluminance), 0, 1e-6f);
var twice = f with { Tint = new Vector3(2, 1, .5f) };
Assert.InRange(Vector3.Distance(m.SkyValue(.002f, Vector3.UnitY, twice),
    m.SkyValue(.002f, Vector3.UnitY, f) * twice.Tint), 0, 1e-6f);
var floorPixel = TonemapMath.Map(.08f * .65f, .08f * .8f, .08f, 1, 0, .75f);
Assert.True(floorPixel.R > .03f && floorPixel.G > .03f && floorPixel.B > .03f);
var skyFloorPixel = TonemapMath.Map(.02f * .65f, .02f * .8f, .02f, 1, 0, .75f);
Assert.True(skyFloorPixel.R > 1f / 255 && skyFloorPixel.G > 1f / 255 && skyFloorPixel.B > 1f / 255);
var count = m.LightingBuildCount;
Assert.False(m.UpdateSettings(s));
Assert.Equal(count, m.LightingBuildCount);
s.MieScatteringPerKm *= .5f;
Assert.True(m.UpdateSettings(s));
Assert.Equal(count + 1, m.LightingBuildCount);
```

Assert key R/B strictly increases at elevations 60,20,5,1. KeySource.None at -1 gives zero. Vacuum moonless night equals floor*Tint. AmbientScale 2 doubles above-floor ambient, not SkyValue. Tint/floor/illuminance changes preserve both build counts.

At moon elevation 20, sun ±.001 and Sun/Moon handover, horizon RGB distance <=.005. Camera height .002 to 2 changes lighting, not build counts.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-light-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereLightingTests"`

Expected: missing frame/result and lighting methods.

- [ ] **Step 3: Implement** `AtmosphereModel.EvaluateLighting(float cameraHeightKm, in AtmosphereFrame frame) -> AtmosphereLighting` in `KhaozEngine.Render3D/AtmosphereModel.Lighting.cs`. Cache unit-source ambient, bounce and horizon at 181 elevations [-90,90], step 1 degree, in eight altitude slices `h=AtmosphereHeightKm*(i/7)^2` km (Earth top 100). Bilinear elevation/height interpolation preserves camera-altitude dependence with settings-only builds.

Use 4 cosine x 8 azimuth midpoint bins for hemisphere E, weighted mu*dmu*dphi, and 16 geometric-horizon directions for its mean L. Sum independent sun/moon table queries. Fill is (direct sun + direct moon + Esky)*albedo*FillScale. Preserve fill direction.

Key is S*T*selectedFraction*KeyVisibility*Tint. Ambient is max(S*E*AmbientScale, ambientFloor)*Tint. Sky/horizon are max(S*pi*L, skyFloor)*Tint. Floors are untinted engine RGB values and precede tint, so the tested minimum is floor*Tint. Memoize lighting by frame/height so Apply and packing share two transmittance integrals. Task 12 measures cold cache time.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-light-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereLightingTests"`

Expected: units, reddening, floors, continuity and mutation/cache tests pass.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/AtmosphereModel.cs
  KhaozEngine.Render3D/AtmosphereModel.Lighting.cs
  KhaozEngine.Render3D/AtmosphereLighting.cs
  KhaozEngine.Render3D/Internal/AtmosphereLightingCache.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereLightingTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): derive and cache physical lighting" -- "${atmo_commit_paths[@]}"
```

### Task 5: SunCycle integration without losing the real sun

**Files:**
- Create: `KhaozEngine.Render3D/Internal/SunCycleAtmosphereSample.cs`
- Modify: `KhaozEngine.Render3D/SunCycle.cs:209-321,336-491,535-570`, `KhaozEngine.Render3D/PixelPostProcessSettings.cs:55-83`
- Test: `KhaozEngine.Render.Tests/Render3D/SunCycleAtmosphereTests.cs`, existing `KhaozEngine.Render.Tests/Render3D/SunCycleTests.cs`

**Interfaces:**
- Consumes: existing `SunCycle.Evaluate(float timeOfDay, SunCycleSettings settings) -> SunCycleState`, `SunCycle.Apply(in SunCycleState state, PixelPostProcessSettings post) -> void`, `SolarDirection(float timeOfDay, float latitudeDegrees, float declinationDegrees, float headingDegrees, out float elevationDegrees) -> Vector3`, Task 4 lighting/frame types.
- Produces: additive public `SunCycle.Apply(in SunCycleState state, PixelPostProcessSettings post, Vector3 cameraWorldPosition) -> SunCycleState` and `AtmosphereModel.CreateFrame(in SunCycleState state) -> AtmosphereFrame`.
- Produces: internal readonly record struct `SunCycleAtmosphereSample(Vector3 SunDirection, Vector3 PaletteWeights, float KeyVisibility, NightKeyMode NightKey, float SunOpacity, float MoonOpacity)` and `SunCycleState.WithAtmosphereSample(in SunCycleAtmosphereSample sample) -> SunCycleState`, nullable internal state property `AtmosphereSample`.
- Produces: internal `PixelPostProcessSettings.ResolveAtmosphereModel() -> AtmosphereModel`, nullable `SunCycleState? AtmosphereCycle` to retain the last evaluated cycle, including its untinted sample.

- [ ] **Step 1: Write the failing test** `ApplyPhysical_IgnoresPalettesAndReturnsHorizon`, `ApplyPhysical_PreservesAllNightKeyDirectionsAndFades`.

```csharp
var c = new SunCycleSettings { LatitudeDegrees = 0, SolarDeclinationDegrees = 0, NightKey = NightKeyMode.Moon };
var p = new PixelPostProcessSettings();
p.Atmosphere.Enabled = true;
var raw = SunCycle.Evaluate(.5f, c);
var physical = SunCycle.Apply(raw, p, new Vector3(0, 2, 0));
Assert.Equal(raw.LightDirection, physical.LightDirection);
Assert.Equal(raw.ActiveSource, physical.ActiveSource);
Assert.Equal(physical.HorizonColor, p.Sky.HorizonColor);
Assert.Equal(physical.LightColor, p.LightColor);
var before = p.LightColor;
c.DayPalette.LightColor = new Color(99, 0, 99);
SunCycle.Apply(SunCycle.Evaluate(.5f, c), p, new Vector3(0, 2, 0));
Assert.Equal(before, p.LightColor);
```

Assert key zero at equatorial .25/.75. All modes at .24/.25/.26/.74/.75/.76 preserve legacy direction/ActiveSource. None sets moon fraction/key zero, AntiSolarMoon retains opposite-sun source/dip, Moon retains min(moonDip,handoverDip). Weights at +10/0/-12 are (1,0,0)/(0,1,0)/(0,0,1), +5/-6 use legacy SmoothStep.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-cycle-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SunCycleAtmosphereTests|FullyQualifiedName~SunCycleTests"`

Expected: missing returning Apply overload and physical branch.

- [ ] **Step 3: Implement** `SunCycle.Apply(in SunCycleState state, PixelPostProcessSettings post, Vector3 cameraWorldPosition) -> SunCycleState` in `KhaozEngine.Render3D/SunCycle.cs`. Evaluate retains true sun, untinted fade, weights and disc opacities before palette selection. CreateFrame uses sample.SunDirection and -state.MoonDirection, or the opposite sun for AntiSolarMoon. Attach the sample with a private state copy constructor, preserving the public constructor. Physical Apply rejects a manually constructed unsampled state with InvalidOperationException instructing the caller to use Evaluate.

Void Apply delegates at world zero. Disabled writes stay identical. Enabled Apply stores post.AtmosphereCycle, resolves the model, writes physical lighting and returns the physical HorizonColor in the existing property. The caller copies it to Water.HorizonColor.

Physical primary stays the true sun, moon stays ExtraDiscs. Disc RGB is S*T/sin(radius)^2 times source fraction and tint once, using cached transmittances, retained opacities and atmosphere radius. Ignore palette/MoonKeyColor/MoonDiscColor, preserve fades/fill direction. Document both Apply overloads.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-cycle-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SunCycleAtmosphereTests|FullyQualifiedName~SunCycleTests"`

Expected: new physical tests and existing legacy cycle tests pass.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/Internal/SunCycleAtmosphereSample.cs
  KhaozEngine.Render3D/SunCycle.cs
  KhaozEngine.Render3D/PixelPostProcessSettings.cs
  KhaozEngine.Render.Tests/Render3D/SunCycleAtmosphereTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): integrate physical SunCycle lighting" -- "${atmo_commit_paths[@]}"
```

### Task 6: LUT renderer, shared resources and layout tripwires

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/AtmosphereRenderer.cs`, `KhaozEngine.Render3D/Rendering/AtmosphereBindings.cs`, `KhaozEngine.Render3D/Internal/AtmosphereUpdatePolicy.cs`, `KhaozEngine.Render3D/Internal/ShaderSources.Atmosphere.cs`, `KhaozEngine.Render3D/Scene3D.Atmosphere.cs`
- Modify: `KhaozEngine.Render3D/Scene3D.cs:402-446,1420-1464,2116-2143`
- Modify: `KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs:49-55,153-160`, `KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs:118-128`, `KhaozEngine.Render.Tests/Gpu/VulkanSpirvByteEqualityTests.cs:137-148`
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereRendererTests.cs`, `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs:623-697`, `KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs:104-108`

**Interfaces:**
- Consumes: Task 5 ResolveAtmosphereModel/frame and Task 4 EvaluateLighting.
- Produces: internal `AtmosphereRenderer(IGpuDevice gd)`, `AtmosphereBindings Bindings`, internal `IGpuTexture? TransmittanceTexture`, `IGpuTexture? MultiScatteringTexture`, `int StaticBuildCount`, `int SkyViewDrawCount`, `Record(IGpuCommandList cl, PixelPostProcessSettings post, Vector3 cameraRelative, Vector3 renderOrigin, Matrix4x4 view, Matrix4x4 projection, int width, int height) -> void`, `Dispose() -> void`.
- Produces: internal `static AtmosphereBindings.AppendLayout(params GpuResourceLayoutElement[] existing) -> GpuResourceLayoutElement[]`, `AppendResources(params IGpuBindableResource[] existing) -> IGpuBindableResource[]`, properties `IGpuBuffer Uniforms`, `IGpuTexture SkyView`, `IGpuSampler SkySampler`, `Dispose() -> void`.
- Produces: `AtmosphereUpdatePolicy.Decide(AtmosphereSettings settings) -> (bool RebuildStatic, bool DrawSkyView)` and `ShaderSources.AtmosphereBindingsGlsl`, `AtmosphereTransportGlsl`, `AtmosphereTransmittanceFrag`, `AtmosphereMultiScatteringFrag`, `AtmosphereSkyViewFrag` strings. Define consumer bindings here before sky and fog consume them.
- Produces: internal static `AtmosphereRenderer.PackUbo(AtmosphereSettings settings, in AtmosphereFrame frame, in AtmosphereLighting lighting, Vector3 cameraRelative, Vector3 renderOrigin, Matrix4x4 view, Matrix4x4 projection, int width, int height, HdrSettings hdr) -> AtmosphereUbo`.

- [ ] **Step 1: Write the failing test** `ToggleAndArtChanges_DoNotRebakeStaticLuts`, `AtmosphereUbo_Is304BytesAndAllOffsetsMatch`, `BindingSuffix_PreservesExistingSlots`.

```csharp
var s = AtmosphereSettings.Earth();
var policy = new AtmosphereUpdatePolicy();
Assert.Equal((false, false), policy.Decide(s));
s.Enabled = true;
Assert.Equal((true, true), policy.Decide(s));
Assert.Equal((false, true), policy.Decide(s));
s.NightTint = Vector3.One;
Assert.Equal((false, true), policy.Decide(s));
s.Enabled = false;
Assert.Equal((false, false), policy.Decide(s));
s.Enabled = true;
Assert.Equal((false, true), policy.Decide(s));
s.MieExtinctionPerKm *= 2;
Assert.Equal((true, true), policy.Decide(s));
Assert.Equal(304, Marshal.SizeOf<AtmosphereRenderer.AtmosphereUbo>());
Assert.Equal(160, (int)Marshal.OffsetOf<AtmosphereRenderer.AtmosphereUbo>("Origin"));
Assert.Equal(288, (int)Marshal.OffsetOf<AtmosphereRenderer.AtmosphereUbo>("Tone"));
```

Binding tests construct prefixes of lengths 1, 3, 4, 6, 7 and 11 and assert resulting length 14, original element order identical, suffix kinds UniformBuffer/TextureReadOnly/Sampler at 11/12/13, all Fragment. Add validation facts calling `ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.AtmosphereTransmittanceFrag, "AtmosphereTransmittance")`, and the corresponding two fragments.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-luts-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereRendererTests|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~ShaderSourceValidationTests"`

Expected: missing renderer, policy, UBO and source names.

- [ ] **Step 3: Implement** `AtmosphereRenderer.Record(IGpuCommandList cl, PixelPostProcessSettings post, Vector3 cameraRelative, Vector3 renderOrigin, Matrix4x4 view, Matrix4x4 projection, int width, int height) -> void` in `KhaozEngine.Render3D/Rendering/AtmosphereRenderer.cs`, using `GpuTextureDescription.Texture2D(uint,uint,GpuPixelFormat,GpuTextureUsage)`, `IGpuResourceFactory.CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)`, `CreateGraphicsPipeline(in GpuPipelineDescription)`, `CreateResourceLayout(in GpuResourceLayoutDescription)`, `CreateResourceSet(in GpuResourceSetDescription)` and `cl.Draw(3)`.

Use R16G16B16A16Float, RenderTarget|Sampled, one mip/sample, no depth, OverrideBlend. Follow PixelPostProcess framebuffer handoffs and OceanFftProducer value keys/stable maps. Record distinct LUT targets in the scene list, never nested lists or sampling the current output.

Allocate stable sky-view/UBO plus 1x1 black padding texture at construction, static LUTs at first enable. Consumer set 0 bindings 11/12/13 are UBO/sky-view/sampler. Preserve the prefix and fill holes with Fragment TextureReadOnly padding. GLSL must declare matching unused texture2D AtmoPadN bindings: reflection sorts densely (SpirvCrossReflect.cs:166-182). Define AtmospherePadding1Glsl/3Glsl/4Glsl/6Glsl/7Glsl, declaring [prefix,10], never sampled. Prefix 11 needs none. Do not add set 4: skinned TAA uses set 3 and Vulkan guarantees four sets (SkinnedMotionPalette.cs:9-13).

Initialize the shared UBO to zero with the enabled flag zero, including standalone borrowed/owned binding paths. Disabled Record only uploads that no-op flag and returns before resolving CPU tables or drawing LUTs. Producer set 0 is independently `[Atmosphere UBO, transmittance texture, multi-scattering texture, clamp sampler]` at bindings 0-3. Use the padding texture for an unread input, never the output texture. GLSL producer bindings and consumer bindings are separate chunks. Static keys include physical coefficients, heights, radii, ozone and albedo. Directions, altitude, mapping, art scales and tints are frame inputs.

The 304-byte std140 UBO contains these 19 vec4s, in this exact order. Pack names/offsets are also asserted against every declaring shader.

| Offset | Member | Components |
| --- | --- | --- |
| 0 | Planet | radius, top radius, world units/km, aerial scale |
| 16 | Rayleigh | scattering RGB, scale height |
| 32 | Mie | scattering, extinction, scale height, g |
| 48 | Ozone | absorption RGB, centre |
| 64 | GroundHaze | albedo, haze extinction, haze scale height, ozone half width |
| 80 | Sun | direction toward sun, illuminance |
| 96 | Moon | direction toward moon, illuminance fraction |
| 112 | Tint | RGB tint, enabled flag |
| 128 | Floors | ambient scale, fill scale, ambient floor, sky floor |
| 144 | Camera | relative eye XYZ, unscaled physical camera height km |
| 160 | Origin | render origin XYZ, 0 |
| 176 | SunT | sun transmittance RGB, solar opacity |
| 192 | MoonT | moon transmittance RGB, lunar opacity |
| 208 | Right | world camera right XYZ, 0 |
| 224 | Up | world camera up XYZ, 0 |
| 240 | Back | world camera back XYZ, 0 |
| 256 | Ray | 1/P11, 1/P22, P31/P11, P32/P22 |
| 272 | Res | 1/width, 1/height, orthographic flag, angular radius |
| 288 | Tone | HDR flag, exposure, operator, chroma preservation |

Mirror Task 3 counts/phases. Sky-view stores Lsun+MoonFraction*Lmoon with ground, before tint/floors/S*pi. Static tables use clamp sampling, sky uses linear U-wrap/V-clamp.

Implement Scene3D.RecordAtmosphere(IGpuCommandList cl,Vector3 eye)->void and internal AtmosphereRenderer AtmosphereForTests. Renderer counters increment only for recorded passes. Construct before borrowers, record just after relative eye at :1451, before any shadow/model draw. Height=(eye.Y+_frameOrigin.Y)/units. Re-create current frame from AtmosphereCycle and current settings, refresh lighting before model upload. Without cycle use resolved sky sun, opposite moon and 10/-12 tint weights. Dispose after consumer sets and existing idle drain.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-luts-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereRendererTests|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~ShaderSourceValidationTests"`

Expected: layout/policy facts pass, GLSL compiles to SPIR-V, HLSL and MSL. Add the three producer pairs to ShippedShaderPrograms.GraphicsPrograms with names AtmosphereTransmittance, AtmosphereMultiScattering and AtmosphereSkyView. Update the D3D11 program-count assertion 79 to 82 and the Vulkan stage count 166 to 172. Rebuild the emitted hash/corpus tables once in Task 12 after all source edits. Runtime texel and lifetime tests also arrive there.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/Rendering/AtmosphereRenderer.cs
  KhaozEngine.Render3D/Rendering/AtmosphereBindings.cs
  KhaozEngine.Render3D/Internal/AtmosphereUpdatePolicy.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Atmosphere.cs
  KhaozEngine.Render3D/Scene3D.Atmosphere.cs
  KhaozEngine.Render3D/Scene3D.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereRendererTests.cs
  KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs
  KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs
  KhaozEngine.Render.Tests/Gpu/ShippedShaderPrograms.cs
  KhaozEngine.Render.Tests/Gpu/D3D11HlslByteEqualityTests.cs
  KhaozEngine.Render.Tests/Gpu/VulkanSpirvByteEqualityTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): add fragment LUT passes and shared bindings" -- "${atmo_commit_paths[@]}"
```

### Task 7: Physical background sky and celestial discs

**Files:**
- Create: `KhaozEngine.Render.Tests/Gpu/AtmosphereGoldenScene.cs`, `KhaozEngine.Render.Tests/Gpu/AtmosphereSkyGoldenTests.cs`
- Modify: `KhaozEngine.Render3D/Rendering/SkyRenderer.cs:25-145`, `KhaozEngine.Render3D/Internal/ShaderSources.Sky.cs:80-132`, `KhaozEngine.Render3D/SkySettings.cs:7-20,75-98`, `KhaozEngine.Render3D/PixelPostProcessSettings.cs:148-165`, `KhaozEngine.Render3D/Scene3D.TemporalResolve.cs:194-218`, `KhaozEngine.Render3D/Scene3D.cs:434`
- Modify: `KhaozEngine.Render3D/AtmosphereModel.Lighting.cs` (Task 4, SunDiscValue), `KhaozEngine.Render3D/Internal/ShaderSources.Atmosphere.cs` (Task 6, sky/disc consumers)
- Test: `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs:623-644`, `KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs:104-108`, `KhaozEngine.Render.Tests/Gpu/AtmosphereSkyGoldenTests.cs`

**Interfaces:**
- Consumes: AtmosphereBindings, frame UBO, Task 2 SkyUv, Task 4 SkyValue.
- Produces: additive `SkyRenderer(IGpuDevice gd, GpuOutputDescription output, AtmosphereBindings atmosphere)` alongside its existing internal construction path, `PackUbo(SkySettings sky, Matrix4x4 view, Matrix4x4 projection, Vector3 lightDirection, int renderWidth, int renderHeight, bool atmosphereEnabled = false) -> SkyUbo`.
- Produces: additive SkyRenderer.Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 view, Matrix4x4 projection, Vector3 lightDirection, SkySettings sky, bool atmosphereEnabled = false) -> void. DrawBackground passes Post.Atmosphere.Enabled into Draw and PackUbo.
- Produces: shared GLSL `atmosphereSkyValue(vec3 direction) -> vec3`, `atmosphereSunDisc(vec3 direction) -> vec3` and internal CPU `AtmosphereModel.SunDiscValue(float cameraHeightKm, Vector3 direction, in AtmosphereFrame frame) -> Vector3`.
- Produces: test fixture `AtmosphereGoldenScene.Capture(float timeOfDay, bool atmosphere = true, Vector3 origin = default) -> byte[]`, `CaptureFresh(float timeOfDay, bool atmosphere = true, Vector3 origin = default) -> byte[]`, `SunCycleSettings Cycle`, `Dispose() -> void`.

- [ ] **Step 1: Write the failing test** `PhysicalSky_OverridesBackgroundAndAnchor`, `SkyUbo_AppendsDirectionsAt480`, `Golden_IndependentMoonAndAzimuthSeam`.

```csharp
var p = new PixelPostProcessSettings();
p.Atmosphere.Enabled = true;
Assert.Equal(BackgroundMode.Sky, p.Background);
p.Background = BackgroundMode.Solid;
Assert.False(p.Atmosphere.Enabled);
Assert.Equal(608, Marshal.SizeOf<SkyRenderer.SkyUbo>());
Assert.Equal(480, (int)Marshal.OffsetOf<SkyRenderer.SkyUbo>("DiscDirection"));
```

Add [GpuFact] tests `Golden_AtmosphereNoon`, `Golden_AtmosphereSunset`, `Golden_AtmosphereMoonlitNight`, capturing times `.5`, `.747`, `0` with equatorial sun/declination zero and Moon mode. Use camera `(0,2,9)`, yaw -pi/2 toward sunset west, pitch -.08, FieldOfView pi/3, 480x320 output, fixed 480x320 internal size, ACES exposure 1, AA off, no starfield, an 8-unit floor and a white box. For the night grid, use MoonHourOffset 7.5 and yaw pi/2 so the moon at 22.5 degrees is visible. Other grid rows use offset 12. Use names `scene3d_atmosphere_noon`, `scene3d_atmosphere_sunset`, `scene3d_atmosphere_moonlit` in GoldenCompare.

Independent moon: time .4, MoonHourOffset 6, MoonDeclinationDegrees 20, heading 35. Separately aim at actual/mirrored directions, actual patch brighter. Seam azimuth ±1e-4 differs <=.01 after tonemap. Screen/StylizedBackdrop equals World/World bytes. Define fixture Capture(Action<Scene3D> configure)->byte[], resetting post/camera/cycle before configure, then Apply. Classes use IClassFixture<AtmosphereGoldenScene>(fixture).

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-sky-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~UboLayoutTests|FullyQualifiedName~SunCycleAtmosphereTests"`

Expected: layout/background fail. Goldens finish in Task 12.

- [ ] **Step 3: Implement** `atmosphereSkyValue(vec3 direction) -> vec3` in `KhaozEngine.Render3D/Internal/ShaderSources.Atmosphere.cs` and the SkyFrag branch, preserving SkyVert far-plane z=1, read-only Equal depth and colour-only output. `Background` getter treats Atmosphere.Enabled as Sky precedence even if Sky.Enabled is false. Setting Solid/Starfield disables Atmosphere, setting Sky retains its current opt-in value.

Append `SkyDiscVec4s DiscDirection` to SkyUbo at byte 480, bringing it to 608 bytes. Preserve the first 480 bytes' legacy packing and shader loop. Prepend AtmospherePadding1Glsl and AtmosphereBindingsGlsl to SkyFrag, keeping bindings 0 through 13 declared densely. Physical packing forces world directions and angular disc radii regardless of Anchor/Horizon. Use the shared camera basis and projection, including jitter/off-centre terms. Orthographic views use the constant camera forward ray and hide directional discs, rather than inventing screen elevation. Below the geometric horizon, the sky-view ground radiance replaces SkyGround.

The primary sun adds `S*T/sin(radius)^2` times normalized limb profile `(1-.6+.6*sqrt(max(0,1-(angle/radius)^2)))/(1-.6/3)`, feathered over one pixel using fwidth. Planet intersection clips the solar limb. Mark the physical primary in `DiscHalo[i].Y = 1`, retaining zero there when disabled. Other discs use their transmittance-coloured ExtraDiscs values and world angular positions, without duplicating the sun halo. Bloom receives the HDR disc as part of the unchanged HDR chain. CPU SunDiscValue mirrors the same limb profile and planet occlusion.

Use the lazy OceanFocusScene fixture pattern. Golden_SharedFixtureMatchesFreshScene ages noon/night/sunset then compares noon to CaptureFresh at InSessionTolerance .06.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-sky-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~UboLayoutTests|FullyQualifiedName~SunCycleAtmosphereTests|FullyQualifiedName~ShaderSourceValidationTests"`

Expected: layout/background/sources pass. GPU/grids finish in Task 12.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/Rendering/SkyRenderer.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Sky.cs
  KhaozEngine.Render3D/SkySettings.cs
  KhaozEngine.Render3D/PixelPostProcessSettings.cs
  KhaozEngine.Render3D/Scene3D.TemporalResolve.cs
  KhaozEngine.Render3D/Scene3D.cs
  KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs
  KhaozEngine.Render.Tests/Gpu/AtmosphereGoldenScene.cs
  KhaozEngine.Render.Tests/Gpu/AtmosphereSkyGoldenTests.cs
  KhaozEngine.Render3D/AtmosphereModel.Lighting.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Atmosphere.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): render world sky and physical discs" -- "${atmo_commit_paths[@]}"
```

### Task 8: Water reflects the physical sky

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Water.cs:16-54,211-222,323-350`, `KhaozEngine.Render3D/Internal/SkyMath.cs:259-285`, `KhaozEngine.Render3D/Rendering/WaterRenderer.cs:188-223,279-288,322-423`, `KhaozEngine.Render3D/Scene3D.cs:441,1770-1773`
- Test: `KhaozEngine.Render.Tests/Render3D/SkyMathTests.cs`, `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs:647-697`, `KhaozEngine.Render.Tests/Gpu/AtmosphereSkyGoldenTests.cs`

**Interfaces:**
- Consumes: Task 7 atmosphereSkyValue and Task 4 `SkyValue(float,Vector3,in AtmosphereFrame) -> Vector3`.
- Produces: additive CPU `SkyMath.ShadeDirectionDiscs(Vector3 direction, Vector3 horizon, Vector3 zenith, ReadOnlySpan<SkyDisc> discs, float sunStrength, SkyGround ground = default, AtmosphereModel? atmosphere = null, AtmosphereFrame atmosphereFrame = default, float cameraHeightKm = 0f) -> Vector3`.
- Produces: WaterRenderer.Draw(IGpuCommandList cl, RenderResources res, ReadOnlySpan<WaterPlane> planes, Matrix4x4 viewProj, Matrix4x4 cullViewProj, Vector3 lightDirection, Color lightColor, Vector3 cameraPos, WaterSettings settings, SkySettings sky, float timeSeconds, Vector3 renderOrigin = default, bool atmosphereEnabled = false) -> void. Scene3D.cs:1770-1773 passes Post.Atmosphere.Enabled through Draw to PackUbo.
- Produces: WaterRenderer constructor appends AtmosphereBindings atmosphere. Full PackUbo(Matrix4x4 clipViewProj, Matrix4x4 rawViewProj, Vector3 lightDirection, Color lightColor, Vector3 cameraPos, WaterSettings settings, SkySettings sky, float timeSeconds, in OceanMaps ocean, Vector3 renderOrigin = default, ShoreMaps shore = default, float planeSurfaceY = 0f, bool atmosphereEnabled = false) -> WaterUbo. Preserve the short overload.

- [ ] **Step 1: Write the failing test** `WaterSkyMirror_UsesPhysicalRadianceAndKeepsDiscStrength`, `WaterBlock_Preserves1056Bytes`.

```csharp
var m = new AtmosphereModel(AtmosphereSettings.Earth());
var f = new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, 0, Vector3.One);
var actual = SkyMath.ShadeDirectionDiscs(Vector3.UnitY, Vector3.Zero, Vector3.Zero,
    ReadOnlySpan<SkyDisc>.Empty, 0, atmosphere: m, atmosphereFrame: f, cameraHeightKm: .002f);
Assert.InRange(Vector3.Distance(actual, m.SkyValue(.002f, Vector3.UnitY, f)), 0, 1e-6f);
Assert.Equal(1056, Marshal.SizeOf<WaterRenderer.WaterUbo>());
Assert.Equal(1280u, WaterRenderer.SlotBytes);
```

Golden_PhysicalSkyReflectsBothBodies uses WaterSceneTuning.ApplyCalmMirror, Y=0 surface and two visible bodies. Each reflected patch is >=15 percent closer to its sky colour than the open-water control, using SkyWorldHorizonGoldenTests sampling. No new grid.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-water-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkyMathTests|FullyQualifiedName~UboLayoutTests"`

Expected: new CPU overload unavailable.

- [ ] **Step 3: Implement** `skyAlongDirection(vec3 dir, float sunStrength) -> vec3` in `KhaozEngine.Render3D/Internal/ShaderSources.Water.cs` and the CPU overload. Sample the same sky-view texture at the reflected world direction, then add attenuated reflected discs at the existing sunStrength to retain the water's specular double-count avoidance. The legacy branch stays unchanged. Prepend AtmospherePadding7Glsl and AtmosphereBindingsGlsl to WaterFrag. Append Task 6 resources to water set 0, keeping its original seven elements and dynamic Water UBO offset intact. Record `SkyParams.Y = atmosphereEnabled ? 1 : 0`, formerly unused, preserving WaterUbo size 1056, bound range/stride 1280. The shared atmosphere UBO remains the source of mapping and tint.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-water-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~SkyMathTests|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~ShaderSourceValidationTests"`

Expected: mirror, existing sky tests, water tripwires and source validation pass.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/Internal/ShaderSources.Water.cs
  KhaozEngine.Render3D/Internal/SkyMath.cs
  KhaozEngine.Render3D/Rendering/WaterRenderer.cs
  KhaozEngine.Render3D/Scene3D.cs
  KhaozEngine.Render.Tests/Render3D/SkyMathTests.cs
  KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs
  KhaozEngine.Render.Tests/Gpu/AtmosphereSkyGoldenTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): match water reflections to physical sky" -- "${atmo_commit_paths[@]}"
```

### Task 9: Closed-form fog and absolute height

**Files:**
- Create: `KhaozEngine.Render3D/AtmosphereFog.cs`
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereFogTests.cs`

**Interfaces:**
- Consumes: AtmosphereSettings world mapping/extinction, model SkyValue and AtmosphereFrame.
- Produces: public static `AtmosphereFog.OpticalDepthTerm(double distanceKm, double startHeightKm, double endHeightKm, double scaleHeightKm) -> double`, `Transmittance(AtmosphereSettings settings, float distanceWorld, float startAbsoluteHeightWorld, float endAbsoluteHeightWorld) -> Vector3`, `Compose(Vector3 colour, Vector3 transmittance, Vector3 haze, bool additive = false) -> Vector3`, `Shade(AtmosphereSettings settings, AtmosphereModel model, in AtmosphereFrame frame, Vector3 colour, Vector3 cameraRelative, Vector3 fragmentRelative, Vector3 renderOrigin, bool additive = false) -> Vector3`.

- [ ] **Step 1: Write the failing test** `Fog_HasExactSegmentLimits`, `Fog_ConvergesToHorizon`, `RebaseY_LeavesFogIdentical`.

```csharp
var s = AtmosphereSettings.Earth();
s.Enabled = true;
s.AerialDistanceScale = 1;
Assert.Equal(Vector3.One, AtmosphereFog.Transmittance(s, 0, 0, 0));
Assert.Equal(Vector3.Zero, AtmosphereFog.Transmittance(s, float.PositiveInfinity, 0, 0));
s.AerialDistanceScale = 0;
Assert.Equal(Vector3.One, AtmosphereFog.Transmittance(s, float.PositiveInfinity, 0, 0));
s.AerialDistanceScale = 1;
Assert.Equal(1d, AtmosphereFog.OpticalDepthTerm(1, 0, 0, 8), 10);
Assert.Equal(8 * (1 - Math.Exp(-1d / 8)), AtmosphereFog.OpticalDepthTerm(1, 0, 1, 8), 10);
Assert.InRange(Math.Abs(AtmosphereFog.OpticalDepthTerm(1, 2, 2 + 1e-9, 8) - Math.Exp(-2d / 8)), 0, 1e-8);
Assert.Equal(new Vector3(.2f, .3f, .4f), AtmosphereFog.Compose(Vector3.One, Vector3.Zero, new Vector3(.2f, .3f, .4f)));
```

Noon rebase: eye (100000,2000,100000), fragment (101000,2000,100000), origins zero/(100000,1900,100000), RGB error <=1e-6. Downward fog samples horizon. Surface distance 1e9 converges to horizon within 1e-6. Disabled returns input. Scale 20 transforms both height/distance. Infinite vacuum T=1.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-fog-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereFogTests"`

Expected: missing AtmosphereFog.

- [ ] **Step 3: Implement** `AtmosphereFog.Transmittance(AtmosphereSettings settings, float distanceWorld, float startAbsoluteHeightWorld, float endAbsoluteHeightWorld) -> Vector3` in `KhaozEngine.Render3D/AtmosphereFog.cs` with Rayleigh extinction, Mie extinction and grey haze, excluding ozone from this exponential local-fog approximation. Clamp underground endpoint heights to zero, reject negative distance and invalid finite heights, and return one for aerial scale zero before infinite multiplication. Skip zero coefficients before multiplying infinite integrals.

```text
k = AerialDistanceScale / WorldUnitsPerKilometre
 D = distanceWorld*k, h0 = max(startAbsoluteHeightWorld,0)*k, h1 = max(endAbsoluteHeightWorld,0)*k
For one exponential term of scale height H:
 x = (h1-h0)/H
 I = D*exp(-h0/H)*(1-exp(-x))/x
 For abs(x)<1e-4: use D*exp(-h0/H)*(1-x/2+x*x/6)
 To avoid overflow on descending rays, evaluate the same expression from the lower endpoint using abs(x).
 tau = betaR*IR + betaMext*IM + vec3(hazeDensity)*IH
 T = exp(-tau)
 output = colour*T + skyValue(clampDirectionToGeometricHorizon(view))*(1-T)
 additive output = colour*T
```

For height clamping, use the same endpoint convention on CPU and GPU. Fog height is `fragmentRelative.Y + renderOrigin.Y`, camera height likewise. Segment displacement stays `fragmentRelative-cameraRelative`, so the origin cancels without reintroducing large-coordinate subtraction. Sky altitude is unscaled physical camera height, while aerial scale compresses distance and fog scale heights together. Preserve azimuth when clamping downward elevation, use +X at the nadir where horizontal azimuth is undefined.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-fog-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereFogTests"`

Expected: limits, convergence, scale and rebasing pass without nonfinite output.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/AtmosphereFog.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereFogTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): add analytic absolute-height fog" -- "${atmo_commit_paths[@]}"
```

### Task 10: Shared fog in opaque geometry and foliage

**Files:**
- Create: `KhaozEngine.Render3D/Internal/ShaderSources.AtmosphereFog.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Model.cs:93-219,225-314,441-610`, `KhaozEngine.Render3D/Internal/ShaderSources.Terrain.cs:98-245`, `KhaozEngine.Render3D/Internal/ShaderSources.TileGround.cs:124-244`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.cs:230-330`, `KhaozEngine.Render3D/Rendering/ModelRenderer.Splat.cs:38-45`, `KhaozEngine.Render3D/Rendering/ModelRenderer.TileGround.cs:45-55`, `KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs:92-108`, `KhaozEngine.Render3D/Rendering/ModelRenderer.ShadowLayoutReplacement.cs:119-143`, `KhaozEngine.Render3D/Scene3D.cs:402-406`
- Test: `KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs`, `KhaozEngine.Render.Tests/Render3D/AtmosphereRendererTests.cs`

**Interfaces:**
- Consumes: Task 6 AppendLayout/AppendResources, shared UBO, Task 9 optical-depth formula, Task 7 atmosphereSkyValue.
- Produces: `ShaderSources.AtmosphereFogGlsl`, GLSL `atmosphereTransmittance(vec3 fragmentRelative) -> vec3`, `atmosphereFog(vec3 colour, vec3 fragmentRelative, bool additive) -> vec3`.
- Produces: ModelRenderer constructor trailing dependency `AtmosphereBindings atmosphere`, while preserving standalone test constructors with an owned disabled binding instance.

- [ ] **Step 1: Write the failing test** `EveryOpaqueReceiver_UsesSharedFogAndAbsoluteHeight` in AtmosphereRendererTests.

```csharp
foreach (var src in new[] { ShaderSources.ModelFrag, ShaderSources.ModelDissolveFrag,
    ShaderSources.SkinnedModelFrag, ShaderSources.SkinnedModelDissolveFrag,
    ShaderSources.SplatFrag, ShaderSources.TileGroundFrag })
{
    Assert.Contains("layout(set=0, binding=11) uniform Atmosphere", src);
    Assert.Contains("atmosphereFog(", src);
    Assert.Contains("AtmoOrigin.y", src);
}
Assert.DoesNotContain("atmosphereFog(", ShaderSources.ShadowDepthFrag);
```

Add headless `ConsumerReflection_PinsSharedSlots` using SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Vertex/Fragment, label) and SpirvCrossCompile.VertexFragmentToMsl(vertexBytes, fragmentBytes, label), as the existing emitted-hash tests do. For Model, SkinnedModel, Splat, TileGround and Sky, assert reflection.ResourceLayouts[0].Elements.Length == 14 and elements[11].Kind == UniformBuffer, [12].Kind == TextureReadOnly, [13].Kind == Sampler. Task 11 extends the same rows to all effects.

Add source validation for `FoliageVert + ModelFrag` as well as existing rigid, skinned, dissolve, splat, tile-ground and all their motion variants. Assert a disabled fog shader returns before computing exponentials or sampling sky. Runtime receiver assertions are in Task 12, so a source-string assertion is not the only proof.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-opaque-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereRendererTests|FullyQualifiedName~ShaderSourceValidationTests"`

Expected: shared fog/absolute-height assertion fails.

- [ ] **Step 3: Implement** `atmosphereFog(vec3 colour, vec3 fragmentRelative, bool additive) -> vec3` in `KhaozEngine.Render3D/Internal/ShaderSources.AtmosphereFog.cs` with `Atmo`-prefixed block members to avoid existing UBO identifiers. Splice bindings, padding and function chunks using constant string concatenation beside LightingCommonGlsl. Rigid ModelFrag/Dissolve need no padding, GPU-skinned fragments use AtmospherePadding4Glsl, splat/tile use AtmospherePadding3Glsl. Apply fog to final RGB after diffuse/specular/emissive and dissolve edge colour, before writing oColor. Preserve alpha, normal, depth and motion outputs, cutout/discard behaviour and every shadow fragment.

Append to rigid set 0 prefix 11, skinned main prefix 4, splat/tile frame prefix 3. Foliage already uses ModelFrag/rigid resources, including motion, so no new foliage shader. Preserve its sets 1/2, frame origin offset 992 and frame size 1344. Height is vWorldPos.y+RenderOrigin.y.

BuildShadowSamplingSet appends only for rigid set 0, after shadows/lights. Track that choice during shadow/point-buffer replacement. Material set 1 stays unchanged. Standalone constructors own disabled bindings, scene renderers borrow.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-opaque-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereRendererTests|FullyQualifiedName~ShaderSourceValidationTests|FullyQualifiedName~UboLayoutTests"`

Expected: sources validate, offsets unchanged.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/Internal/ShaderSources.AtmosphereFog.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Model.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Terrain.cs
  KhaozEngine.Render3D/Internal/ShaderSources.TileGround.cs
  KhaozEngine.Render3D/Rendering/ModelRenderer.cs
  KhaozEngine.Render3D/Rendering/ModelRenderer.Splat.cs
  KhaozEngine.Render3D/Rendering/ModelRenderer.TileGround.cs
  KhaozEngine.Render3D/Rendering/ModelRenderer.Skinned.cs
  KhaozEngine.Render3D/Rendering/ModelRenderer.ShadowLayoutReplacement.cs
  KhaozEngine.Render3D/Scene3D.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereRendererTests.cs
  KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): fog models terrain and foliage" -- "${atmo_commit_paths[@]}"
```

### Task 11: Fog water, decals and effects with correct blending

**Files:**
- Create: `KhaozEngine.Render3D/Internal/ShaderSources.TonemapCommon.cs`
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.Effects.cs:33-85,158-352,507-595`, `KhaozEngine.Render3D/Internal/ShaderSources.Decal.cs:77-109,489-511`, `KhaozEngine.Render3D/Internal/ShaderSources.Water.cs:645-657`, `KhaozEngine.Render3D/Internal/ShaderSources.AtmosphereFog.cs` (Task 10), `KhaozEngine.Render3D/Internal/ShaderSources.Post.cs:256-311`, `KhaozEngine.Render3D/AtmosphereFog.cs` (Task 9)
- Modify: `KhaozEngine.Render3D/Rendering/GroundDecalRenderer.cs:135-150,217-225,243-265,477-490`, `KhaozEngine.Render3D/Rendering/ParticleRenderer.cs:85-105,188-203`, `KhaozEngine.Render3D/Rendering/TexturedBillboardRenderer.cs:32-45,109-111`, `KhaozEngine.Render3D/Rendering/BillboardRenderer.cs:31-51`, `KhaozEngine.Render3D/Rendering/OverlayRenderer.cs:34-69,74-93`, `KhaozEngine.Render3D/Rendering/TrailRenderer.cs:35-57,111-134`, `KhaozEngine.Render3D/Rendering/BeamRenderer.cs:49-61`, `KhaozEngine.Render3D/Scene3D.cs:409-427`
- Modify: `KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs:623-697`, `KhaozEngine.Render.Tests/Render3D/AtmosphereRendererTests.cs` (reflection rows)
- Test: `KhaozEngine.Render.Tests/Render3D/AtmosphereFogTests.cs`, `KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs`, `KhaozEngine.Render.Tests/Gpu/AtmosphereFogGoldenTests.cs` (create)

**Interfaces:**
- Consumes: Task 10 bindings/functions, `TonemapMath.Map(float r, float g, float b, float exposure, int op, float chroma) -> (float R,float G,float B)`.
- Produces: trailing borrowed AtmosphereBindings constructor dependencies on effect renderers, optional on OverlayRenderer before its params blend list. Existing standalone constructors use owned disabled bindings.
- Produces: GLSL `atmosphereFogPremultiplied(vec3 rgb, float coverage, vec3 fragmentRelative, float additivity) -> vec3`, `atmosphereFogDisplay(vec3 rgb, vec3 fragmentRelative, bool additive) -> vec3`, `ShaderSources.TonemapCommonGlsl`, `ShaderSources.AtmosphereDisplayFogGlsl`, GLSL `tonemapColour(vec3 rgb, vec3 params) -> vec3`.
- Produces: CPU `AtmosphereFog.ComposePremultiplied(Vector3 rgb, float coverage, Vector3 transmittance, Vector3 haze, float additivity) -> Vector3`, `ComposeDisplay(Vector3 rgb, Vector3 transmittance, Vector3 haze, HdrSettings hdr, bool additive = false) -> Vector3`.

- [ ] **Step 1: Write the failing test** `BlendModes_DoNotInjectAdditiveHaze`, `DisplayBillboard_UsesExistingTonemap`.

```csharp
var t = new Vector3(.25f), haze = new Vector3(.8f, .6f, .4f);
Assert.Equal(new Vector3(.25f), AtmosphereFog.Compose(Vector3.One, t, haze, true));
Assert.InRange(Vector3.Distance(new Vector3(.725f, .575f, .425f),
    AtmosphereFog.ComposePremultiplied(new Vector3(.5f), 1, t, haze, 0)), 0, 1e-6f);
Assert.Equal(new Vector3(.125f), AtmosphereFog.ComposePremultiplied(new Vector3(.5f), 1, t, haze, 1));
var hdr = new HdrSettings { Exposure = 1, Operator = TonemapOperator.AcesFilmic, ChromaPreservation = .75f };
var mapped = TonemapMath.Map(.8f, .6f, .4f, 1, 0, .75f);
Assert.InRange(Vector3.Distance(AtmosphereFog.ComposeDisplay(Vector3.Zero, Vector3.Zero, haze, hdr),
    new Vector3(mapped.R, mapped.G, mapped.B)), 0, 1e-6f);
```

Add `Golden_DisplayBillboardFogUsesTonemappedHorizon` and `Golden_AdditiveEffectsAttenuateWithoutHaze` with far black alpha billboard and black additive particle/trail/beam controls. Black additive input remains black. A fully fogged alpha billboard matches the display horizon within `.02` RGB. Preserve coverage 0 and 1 exactly. Verify decal alpha and additive variants separately and the mixed particle additivity values 0, .5, 1. Existing tonemap tests must remain green.

- [ ] **Step 2: Run it and see it fail**

`build-slot --label atmo-effects-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereFogTests"`

Expected: missing premultiplied/display functions.

- [ ] **Step 3: Implement** `atmosphereFogPremultiplied(vec3 rgb, float coverage, vec3 fragmentRelative, float additivity) -> vec3` and `atmosphereFogDisplay(vec3 rgb, vec3 fragmentRelative, bool additive) -> vec3` in `KhaozEngine.Render3D/Internal/ShaderSources.AtmosphereFog.cs`, plus CPU mirrors and bindings. Water and decal apply full fog to their own straight RGB before alpha blending. Particle premultiplied output is `rgb*T + coverage*(1-additivity)*haze*(1-T)`, leaving its existing output alpha intact. Beam is always additive. Trails and overlay billboards carry a per-run blend flag rather than creating atmosphere pipeline permutations.

Use AtmospherePadding4Glsl for decal, Padding6 for particle, Padding3 for textured billboard and Padding1 for billboard/trail/beam, with AtmosphereBindingsGlsl in each. Add render-relative position varyings to BillboardVert at location 2, TrailVert at 2 and BeamVert at 5, making the corresponding fragments consume them and keeping live interpolants contiguous. ParticleFrag already has vWorld at location 4. DecalFrag fogs its reconstructed receiver world position, including its VoidFallback position. Debug LineVert/LineFrag and DistortionVert/DistortionFrag remain untouched.

BillboardRenderer runs after tonemapping (Scene3D.cs:1860-1880). Factor existing arithmetic/zero-chroma branch into TonemapCommonGlsl. Put atmosphereFogDisplay in AtmosphereDisplayFogGlsl, included only by BillboardFrag after TonemapCommonGlsl. Fog only tonemaps haze with the current exposure/operator/chroma, never the billboard. HDR-off clamps haze. Preserve pass order.

Beam is always additive. Trail/billboard append FogBlend to 80-byte blocks: two buffers/sets with immutable X=0/1, upload matrix prefix to both, select by blend. Decal PackInstance writes reserved Misc.Z=additivity. Keep strides/offsets aligned. Trail SetFrameUniforms uploads both matrix prefixes, Draw selects its blend set. Overlay fog construction uses two existing billboard pipeline indices 0/1, leaving line/fill layouts 64 bytes. Tripwire 80-byte blocks. MotionPreserve generates fragments without new input varyings, so preserve source-mirror tests.

- [ ] **Step 4: Run tests and see them pass**

`build-slot --label atmo-effects-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereFogTests|FullyQualifiedName~Tonemap|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~ShaderSourceValidationTests|FullyQualifiedName~MotionShader"`

Expected: blends/tonemap/layouts/sources pass. Pixels finish in Task 12.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render3D/AtmosphereFog.cs
  KhaozEngine.Render3D/Internal/ShaderSources.AtmosphereFog.cs
  KhaozEngine.Render3D/Internal/ShaderSources.TonemapCommon.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Post.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Effects.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Decal.cs
  KhaozEngine.Render3D/Internal/ShaderSources.Water.cs
  KhaozEngine.Render3D/Rendering/GroundDecalRenderer.cs
  KhaozEngine.Render3D/Rendering/ParticleRenderer.cs
  KhaozEngine.Render3D/Rendering/TexturedBillboardRenderer.cs
  KhaozEngine.Render3D/Rendering/BillboardRenderer.cs
  KhaozEngine.Render3D/Rendering/OverlayRenderer.cs
  KhaozEngine.Render3D/Rendering/TrailRenderer.cs
  KhaozEngine.Render3D/Rendering/BeamRenderer.cs
  KhaozEngine.Render3D/Scene3D.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereFogTests.cs
  KhaozEngine.Render.Tests/Render3D/UboLayoutTests.cs
  KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs
  KhaozEngine.Render.Tests/Gpu/AtmosphereFogGoldenTests.cs
  KhaozEngine.Render.Tests/Render3D/AtmosphereRendererTests.cs
)
git add "${atmo_commit_paths[@]}"
git commit -m "feat(atmosphere): fog water decals and blended effects" -- "${atmo_commit_paths[@]}"
```

### Task 12: Runtime proof, CI goldens and one frame-time measurement

**Files:**
- Create: `KhaozEngine.Render.Tests/Gpu/AtmosphereLutGoldenTests.cs`, `docs/design/PHYSICAL-ATMOSPHERE-PHASE-1-EVIDENCE.md`
- Modify: `KhaozEngine.Render.Tests/Gpu/AtmosphereGoldenScene.cs`, `KhaozEngine.Render.Tests/Gpu/AtmosphereSkyGoldenTests.cs`, `KhaozEngine.Render.Tests/Gpu/AtmosphereFogGoldenTests.cs`, `KhaozEngine.Render.Tests/Gpu/GoldenSnapshotTests.cs:26-72`
- Modify: `KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt`, `KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt`, `KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt`, `KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt`
- Create: the twelve literal grid paths in Step 5, only after hosted CI bake.

**Interfaces:**
- Consumes: Task 7 fixture capture methods, existing `GoldenCompare.AssertOrUpdate(string name, byte[] rgba, int w, int h) -> void`, `GpuReadback.ToRgba(IGpuDevice, IGpuTexture, int, int) -> byte[]` and GPU recording/submission APIs.
- Produces: test-only `AtmosphereGoldenScene.MeasureSingleFrame(bool enabled) -> double` (milliseconds), fixture `CaptureFog(Vector3 origin = default) -> byte[]` and `CaptureReceiver(string receiver) -> byte[]`.

- [ ] **Step 1: Write the failing test** `Golden_AtmosphereFogDistantTerrain`, `Golden_FogSurvivesOriginStep`, `Golden_AtmosphereOffMatchesExistingScene`, `Golden_LutTexelsMatchCpu`.

```csharp
byte[] fog = fixture.CaptureFog();
GoldenCompare.AssertOrUpdate("scene3d_atmosphere_fog", fog, 480, 320);
byte[] rebased = fixture.CaptureFog(new Vector3(100000, 1900, 100000));
Assert.InRange(GoldenCompare.Downsample(fog, 480, 320)
    .Zip(GoldenCompare.Downsample(rebased, 480, 320), (a, b) => MathF.Abs(a - b)).Max(), 0, .01f);
```

Fog fixture: eye (100000,2,100000), yaw 0, pitch -.03, FarPlane 10000. Clearing splat regions at distances/sizes 100/1000/5000, lod 2, white layers, specular 0, aerial scale 20. Follow SplatTerrainGridGoldenTests.cs:62-98. Origin overrides change coordinates, never scene placement.

Add the disabled row inside GoldenSnapshotTests using its existing CaptureScene3dScene helper and AssertOrUpdate("scene3d",rgba,W,H). Preserve those old grids. Shared-fixture default-off and enable/disable-off captures must match bytes.

LUT GPU oracle: visualize transmittance directly and multi/sky as c/(1+c) into RGBA8. UV rows (.1,.1),(.5,.5),(.9,.9), CPU error <=.01/.02 respectively. Define visualization GLSL in AtmosphereLutGoldenTests. Require a non-clear pixel and positive noon scattering. Static build count is 1 through frame/tint/toggle, 2 after Mie mutation.

CaptureReceiver tests rigid/dissolve/GPU-skinned/CPU-skinned/splat/tile/foliage, AA off and Temporal Native, near/far white patches, CPU fog error <=.03 post-tonemap. Add Task 11 water/decal/particle/billboard/textured/trail/beam blend rows. Names include Golden for push CI.

- [ ] **Step 2: Run it and see it fail**

`build-slot --heavy --label atmo-gpu-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereLutGoldenTests|FullyQualifiedName~AtmosphereSkyGoldenTests|FullyQualifiedName~AtmosphereFogGoldenTests|FullyQualifiedName~Golden_AtmosphereOffMatchesExistingScene"`

Expected: missing new grids and any unwired receiver assertion fail, zero skipped selected GPU facts. Inspect `.got.png` evidence before baking. Fix a disproved producer/consumer in its owning task, never bless its output by baking.

- [ ] **Step 3: Implement** `AtmosphereGoldenScene.CaptureFog(Vector3 origin = default) -> byte[]` and `MeasureSingleFrame(bool enabled) -> double` in `KhaozEngine.Render.Tests/Gpu/AtmosphereGoldenScene.cs`. Rebuild current emission tables once, inspect changed rows against edited pairs. Line/Distortion/shadows and historical corpus files stay unchanged.

```bash
build-slot --heavy --label atmo-shader-tables -- env KE_UPDATE_HLSL_HASHES=1 KE_UPDATE_MSL_HASHES=1 KE_UPDATE_SPIRV_HASHES=1 KE_WRITE_SHADER_CORPUS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~D3D11HlslByteEquality|FullyQualifiedName~MetalMslByteEquality|FullyQualifiedName~VulkanSpirvByteEquality|FullyQualifiedName~ShaderCorpusTests"
```

Reuse one lazy scene/class, NativeDeviceLifecycle collection. Pin fresh parity after mutation/toggle. Readback stays test-only.

Golden_OneShotFrameMeasurement times only with KE_MEASURE_ATMOSPHERE=1. Prime once, then one off/on frame at 1600x900, identical draws. Time RenderInternal through Submit+WaitForIdle, excluding construction/preparation/readback. Record values/delta, adapter/OS/SHA/size and cold cache time. No loops/retries. It measures completed-frame wall time, not isolated GPU cost or statistical proof of .3 ms.

The orchestrator pushes and bakes all three owning hosted families via cross-platform-gpu.yml. Delegated workers do not dispatch:

```bash
git push origin feature/physical-atmosphere
gh workflow run cross-platform-gpu.yml --ref feature/physical-atmosphere -f bake=true -f legs=all -f tier=push -f renderTestFilter='FullyQualifiedName~AtmosphereSkyGoldenTests.Golden_Atmosphere|FullyQualifiedName~AtmosphereFogGoldenTests.Golden_AtmosphereFogDistantTerrain'
gh run list --workflow cross-platform-gpu.yml --branch feature/physical-atmosphere --event workflow_dispatch --limit 5
```

Select the exact head SHA run, put its numeric ID in atmo_bake_run, download:

```bash
gh run download "$atmo_bake_run" -n goldens-metal-native -D .artifacts/atmo-metal
gh run download "$atmo_bake_run" -n goldens-direct3d11-native -D .artifacts/atmo-d3d11
gh run download "$atmo_bake_run" -n goldens-vulkan-native -D .artifacts/atmo-vulkan
```

Copy only four new grids/family, inspect .bake.png and record provenance. Keep tolerance .01. Only CI sets KE_UPDATE_GOLDENS. Commit/push grids, then dispatch bake=false, legs=all, tier=push, empty renderTestFilter. Require all native legs, FXC and Vulkan validation green.

- [ ] **Step 4: Run tests and see them pass**

```bash
build-slot --heavy --label atmo-gpu-green -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~AtmosphereLutGoldenTests|FullyQualifiedName~AtmosphereSkyGoldenTests|FullyQualifiedName~AtmosphereFogGoldenTests|FullyQualifiedName~Golden_AtmosphereOffMatchesExistingScene"
build-slot --heavy --label atmo-one-frame -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native KE_MEASURE_ATMOSPHERE=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Golden_OneShotFrameMeasurement" --logger "console;verbosity=detailed"
```

Expected: grids/pixels/texels/lifetime green, zero selected GPU skips. Run the second command once, record an inconclusive/over-budget result without retry.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  KhaozEngine.Render.Tests/Gpu/AtmosphereLutGoldenTests.cs
  KhaozEngine.Render.Tests/Gpu/AtmosphereGoldenScene.cs
  KhaozEngine.Render.Tests/Gpu/AtmosphereSkyGoldenTests.cs
  KhaozEngine.Render.Tests/Gpu/AtmosphereFogGoldenTests.cs
  KhaozEngine.Render.Tests/Gpu/GoldenSnapshotTests.cs
  docs/design/PHYSICAL-ATMOSPHERE-PHASE-1-EVIDENCE.md
  KhaozEngine.Render.Tests/Gpu/hlsl-hashes/d3d11-hlsl.sha256.txt
  KhaozEngine.Render.Tests/Gpu/msl-hashes/metal-msl.sha256.txt
  KhaozEngine.Render.Tests/Gpu/spirv-hashes/vulkan-spirv.sha256.txt
  KhaozEngine.Render.Tests/Gpu/shader-corpus/corpus.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_noon.metal-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_noon.direct3d11-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_noon.vulkan-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_sunset.metal-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_sunset.direct3d11-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_sunset.vulkan-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_moonlit.metal-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_moonlit.direct3d11-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_moonlit.vulkan-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_fog.metal-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_fog.direct3d11-native.txt
  KhaozEngine.Render.Tests/Gpu/goldens/scene3d_atmosphere_fog.vulkan-native.txt
)
git add "${atmo_commit_paths[@]}"
git commit -m "test(atmosphere): prove LUT sky fog and disabled parity" -- "${atmo_commit_paths[@]}"
```

Worker stops at its commit. Orchestrator owns CI/artifact integration.

### Task 13: Public docs, staged release entry and full verification

**Files:**
- Modify: `CHANGELOG.md:8-31`, `docs/USING-KHAOZENGINE.md:4463-4585`, `KhaozEngine.Render3D/README.md:489-580`, `docs/INDEX.md:47`, `NOTICE.md` (append attribution)
- Modify only if no version remains staged: `Directory.Build.props:25` and the exact declarations reported by the doc-version guard.
- Test: `scripts/check-doc-versions.sh`, full solution verification and the Task 12 native matrix.

**Interfaces:**
- Consumes: `AtmosphereSettings`, `AtmosphereModel`, `AtmosphereFog`, returning SunCycle.Apply and evidence from all preceding tasks.
- Produces: living API documentation, staged package notes and verified integration handoff, no new product interface.

- [ ] **Step 1: Write the failing test** as the doc contract checklist, without adding a redundant test project.

```text
[ ] USING-KHAOZENGINE contains Post.Atmosphere.Enabled = true and returning camera-aware SunCycle.Apply.
[ ] The example assigns returned state.HorizonColor to Post.Water.HorizonColor.
[ ] Package README distinguishes legacy palette sky from physical sky and height fog.
[ ] docs/INDEX records phase 1 implemented, phases 2-4 open, not the whole program complete.
[ ] The newest staged CHANGELOG heading contains the opt-in physical sky/lighting/fog feature.
[ ] NOTICE includes the upstream copyright and MIT licence if reference code was ported.
```

- [ ] **Step 2: Run it and see it fail**

`rg -n 'AtmosphereSettings|cameraWorldPosition|physical atmosphere|analytic.*fog' docs/USING-KHAOZENGINE.md KhaozEngine.Render3D/README.md CHANGELOG.md`

Expected: missing public feature documentation. Inspect `Directory.Build.props`, the newest CHANGELOG heading and release tags again after the orchestrator fetches current main.

- [ ] **Step 3: Implement** the docs under the current staged version: API/defaults, night modes, palette precedence, fixed exposure, mapping/altitude interpolation and compressed fog. Record low-sun valley error (#1364).

Example: `state = SunCycle.Apply(SunCycle.Evaluate(timeOfDay, cycle), scene.Post, scene.ActiveCamera.Eye)`, then `scene.Post.Water.HorizonColor = state.HorizonColor`, Moon mode. Explain void Apply sea-level compatibility, evaluated-state requirement and GPU-free model use. NOTICE names ported paths, Epic copyright and verbatim [MIT licence](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/LICENSE).

Sweep with `rg -n 'SunCycle|SkyHorizon|SkySettings|Atmosphere|fog|one uniform buffer' -g '*.md'` and correct living references. The floating-origin design's one-UBO restriction is historical, retired by CROSS-PLATFORM.md:822-830. Preserve the design history.

- [ ] **Step 4: Run tests and see them pass** after current-main reconciliation in the task branch. Do not resolve integration conflicts on main.

```bash
mkdir -p local-feed
build-slot --label atmo-release-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label atmo-full-tests -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
build-slot --label atmo-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: zero warnings, suite/format/guards green. Plain tests skip GPU facts, Task 12 matrix is separately required. Format repair: `build-slot --label atmo-format-fix -- dotnet format KhaozEngine.slnx --no-restore`, inspect diff, verify again. Never inflate size baselines.

- [ ] **Step 5: Commit**

```bash
atmo_commit_paths=(
  CHANGELOG.md
  docs/USING-KHAOZENGINE.md
  KhaozEngine.Render3D/README.md
  docs/INDEX.md
  NOTICE.md
)
git add "${atmo_commit_paths[@]}"
git commit -m "docs(atmosphere): document physical sky and fog" -- "${atmo_commit_paths[@]}"
```

 If version declarations must change, list every changed path explicitly and include them in the same staged-release commit as the matching newest changelog entry, using `release(<selected-version>): stage physical atmosphere`.

Orchestrator reconciles current main in the task branch, re-verifies changed integration, merges/pushes main. From its main session after origin/main contains the commit, run `build-slot --heavy --label atmo-pack -- bash scripts/pack-local-feed.sh` then `bash scripts/check-local-feed.sh`. Workers never enter that checkout.

At merge, orchestrator closes #45/#1366 with pushed SHA/evidence and updates #50 with phase 1 and remaining phases, using discovered gateway tools. No new issues or deferred/consumer closures. Release/adoption stay owner-started.
