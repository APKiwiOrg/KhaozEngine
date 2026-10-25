# Physical atmosphere phase 1 evidence

## Local Metal proof

Task 12 ran the atmosphere GPU facts on the real Apple M2 Max Metal device on
2026-10-13. The tested HEAD was `ac34cf93394ffef3e74f8852fa4de68de2cca1fe` with the
Task 12 source changes delivered alongside this document. This was a working-tree
measurement, not a measurement of the unchanged parent commit.

The final Release run selected all atmosphere GPU facts, including the Task 6
transmittance endpoint, plus CPU mapping and Vulkan binding/descriptor checks.
It executed 101 tests. 97 passed and four failed only because their new grids are
absent. All 22 selected GPU facts executed with zero skips. The four absent grids
are `scene3d_atmosphere_noon`, `scene3d_atmosphere_sunset`,
`scene3d_atmosphere_moonlit` and `scene3d_atmosphere_fog`.

The passing assertions cover the transmittance, multi-scattering and sky-view CPU
oracles, positive scattering, static-cache invalidation, independent moon and seam,
sky and water reflections, all opaque receiver paths with AA off and Temporal
Native, effect blend modes, absolute-height origin rebasing, shared/fresh parity,
and byte-identical atmosphere-disabled rendering against the existing scene grid.
The ordinary filter leaves the measurement body inactive. Its separate opted-in
run executed and passed once.

Shader emission tables were regenerated once. The subsequent verification without
update variables passed all nine selected byte-equality and corpus tests. Only
the 30 atmosphere fragment entries changed in each backend table. Vertex entries,
Line, Distortion, shadows and historical corpus files stayed unchanged.

## Defects exposed by the device

- The three LUT producers used vertically reversed fullscreen vertex UVs on Metal.
  The upright transmittance oracle measured a maximum channel error of 0.407175.
  Producers now derive their UVs from upper-left fragment coordinates.
- View integration reused a conservative source-shadow penetration margin and
  could miss shallow ground hits. Its horizon sky texel exceeded the 0.02 oracle
  tolerance. View termination now compares against the factored tangent projection
  with a local float-roundoff margin. Source-shadow handling stays separate.
- Bilinear sky sampling crossed the sky/ground boundary at the exact horizon. A
  textured alpha fog row exceeded the 0.03 post-tonemap tolerance, reaching 0.052720
  blue-channel error. Each half of the 108-row sky LUT now owns a horizon endpoint,
  mirrored in the CPU mapping. The upper endpoint constructs its tangent direction
  directly to avoid acos/cos roundoff. The numerical GPU rows pass at the original
  tolerances.

## Captures inspected

All 53 local atmosphere `.got.png` captures were inspected, including contact
sheets for receiver/control pairs. Noon is blue with a bright small solar disc.
Sunset has a warm horizon and red solar disc. Moonlit night is very dim blue with
a visible moon. Distant terrain loses contrast toward the horizon colour. Water
changes from blue reflection to the fog horizon colour. The untextured additive
controls remain visible without fog and lose energy with fog.

The captures and test logs were local inspection evidence, not committed reference grids.

## One completed-frame measurement before calibration

| Field | Value |
| --- | --- |
| Adapter/backend | Apple M2 Max, `metal-native` |
| OS | macOS 27.0.1, build 26A434 |
| Tested HEAD | `ac34cf93394ffef3e74f8852fa4de68de2cca1fe` plus the Task 12 changes |
| Build/target | Release, 1600 by 900, AA off, identical floor and box draws |
| Atmosphere off | 1.127700 ms |
| Atmosphere on | 1.931700 ms |
| On minus off | 0.804000 ms |
| Cold enabled frame | 44.289400 ms |
| Samples | One prime, one off frame, one on frame |

`Golden_OneShotFrameMeasurement` ran exactly once with
`KE_MEASURE_ATMOSPHERE=1`. Timing covers command recording and `RenderInternal`
through `Submit` and `WaitForIdle`. Construction, draw submission,
`PrepareFrame`, initial CPU lighting preparation and readback are excluded.
The cold number is the first enabled completed render after preparation,
including first-use render resources and GPU LUT production.

This is completed-frame wall time on the shared development Mac. The single delta
is over the 0.3 ms target. It is neither isolated GPU cost nor statistical proof.
Byte-equality verification was also active during part of the measurement tool
run. No retry, loop or stress run was performed.

## Hosted provenance pending

No local golden update variable was set and no grid was generated or committed.
The controller owns the hosted Metal, Direct3D 11 and Vulkan bake, inspection of
the twelve new grids and their bake PNGs, recording the exact head and workflow
run ID, and the subsequent non-bake native/FXC/Vulkan validation run. Local Metal
success does not establish those hosted results. The full suite belongs to Task 13.

## Brightness investigation before ruling R13

This section preserves the initial investigation. R13 resolved its model question,
and the calibration below supersedes its blocked status and initial defaults.

The controller rejected the absolute brightness of the Task 12 captures. The
earlier visual descriptions above do not approve these images for a golden bake.
This investigation ran against `7326863b098b92f58813a5b3f981b485cf2e5fca`.
No production source, default, shader or reference grid was changed.

The three requested CPU physical checks are now pinned in
`AtmosphereLightingTests`, with the Earth preset, solar elevation 60 degrees,
camera altitude 0.002 km, unit solar illuminance and no moon contribution.
Rec. 709 weights are `(0.2126, 0.7152, 0.0722)`.

| Check | Observed result |
| --- | --- |
| Zenith divided by sunlit white luminance | 0.05848644, fails the required 0.06 to 0.20 interval |
| Zenith sky luminance | 0.04844365 |
| Horizon at elevation 2 degrees, azimuth 90 degrees from sun | 0.25615588, brighter than zenith |
| Single-scattered zenith radiance luminance | 0.010281946 |
| Single plus multiple-scattered zenith radiance luminance | 0.015420092, greater than single scattering |

The RGB zenith value is `(0.024287932, 0.04929854, 0.111104146)`.
The white receiver value `Key * cos(30 degrees) + Ambient` is
`(0.8867151, 0.8151537, 0.78635734)`.

Diagnostic isolation removed the night ambient floor without changing transport.
The ratio rose only to 0.059451744. Refining the view integral from 32 to 128
segments, also without that floor, produced 0.059600525. Neither passes the
specified lower bound. These were separate focused diagnostic runs, not a test
loop. The temporary diagnostic code was removed after recording its results.

The unit conversion is present once in CPU `ApplySkyControls` and GLSL
`atmosphereSkyValue`. The GPU UBO receives the configured solar illuminance.
Both view integrals add the multiple-scattering source times scattering, and the
GPU sky-view producer defines `ATMO_MULTI_SCATTERING`. The hemisphere quadrature
uses `mu * dmu * dphi`, without an extra Lambert divisor. The sky fragment writes
its physical value into HDR before the existing ACES pass. There is no evidenced
missing or doubled pi, source-unit mismatch, missing multiple-scattering term or
extra sky tonemap in these paths. Increasing the art scale cannot repair a
unit-source sky-to-white ratio.

The ground boundary in both CPU `DirectGroundBounce` and GLSL
`AtmoDirectGroundBounce` reflects direct source illumination only. It does not
include diffuse sky irradiation. The geometric-series response also contains
volume scattering only. This matches the relevant portions of the
[Hillaire reference ray marcher](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyRayMarching.hlsl)
already used by Task 3, whose inspected source SHA-256 is
`023871e742842d8b347967331e72d78036bd3750884d3d94707f6db3c971673e`.
The sunset background floor therefore does not receive the ambient term used by
scene receivers. The chosen approximation needs a controller ruling before a
diffuse ground boundary or its feedback is added to the transport. This omission
has not been demonstrated to account for the complete noon ratio shortfall.
An additional pi or arbitrary scattering gain would violate the declared units.

All four existing PNGs were inspected individually. Pixel coordinates below have
an upper-left origin and were measured from the existing 480 by 320 captures.
These are original Task 12 captures, not refreshed proof.

| Capture | Upper centre `(240,24)` RGB bytes | Lower centre `(240,240)` RGB bytes |
| --- | --- | --- |
| Noon | `(17,26,49)` | `(6,15,37)` |
| Moonlit | `(1,2,3)` | `(1,2,2)` |
| Sunset | `(5,4,5)` | `(3,3,3)` |
| Fog | `(11,26,60)` | `(107,99,87)` |

No calibration value was chosen. Existing values remain `SunIlluminance = 1`,
`MoonIlluminanceFraction = 0.08`, `NightAmbientFloor = 0.08`,
`NightSkyFloor = 0.02`, and `NightTint = (0.65,0.8,1)`.
The required GPU rerun and new one-shot measurement were not executed because
the physical acceptance gate remains red. The earlier measurement above is
historical evidence only. Status is `NEEDS_CONTEXT`, with goldens still blocked.

## Brightness calibration under ruling R13

The physical model stands. R13 accepts clear-sky zenith to sunlit-white luminance
ratios of roughly 0.04 to 0.20, depending on turbidity and solar elevation. The
original 0.06 lower bound was a rough estimate. The unit-source test now documents
that reason and accepts 0.05848644. Its solar scale and original floors are pinned
explicitly so art calibration cannot change the recorded physical measurement.
The horizon remains about 5.29 times the zenith, and multiple scattering increases
zenith radiance. CPU and GPU still apply `S * pi` once.

The old noon upper-centre pixel is reproduced by CPU transport and the existing
ACES pass as `(17.383,26.102,48.913)`. CPU candidate solar scales 1, 2, 3 and 4 give
blue values 48.9, 115.3, 169.3 and 210.0 at exposure 1. Interpolating with the same
tonemap arithmetic selects S 2.7, predicting blue 154.65. Only this solar scale was
rendered. No extra gamma transform or scattering gain was added.

| Default | Before | Calibrated | Reason and evidence |
| --- | --- | --- | --- |
| `SunIlluminance` | 1 | 2.7 | Noon upper-centre blue 155, within 150 to 210. Existing per-channel ACES white probes are `(240,240,239)`, below 250. |
| `MoonIlluminanceFraction` | 0.08 | 0.04 | Moon source scale is 0.108 after solar calibration. The moon remains visible, both reflected bodies pass, and the independent moon control passes without raising night to daylight. |
| `NightAmbientFloor` | 0.08 | 0.05 | Night receivers remain visible. The moonlit floor is `(9,12,18)`, distinct from the sky at `(9,14,23)`. |
| `NightSkyFloor` | 0.02 | 0.09 | CPU moonlit zenith blue 23.264 and captured upper-centre blue 23, within 15 to 40. Moonless directions stay above black. |
| `NightTint` | `(0.65,0.8,1)` | `(0.45,0.65,1)` | Captured night sky `(9,14,23)` reads dark blue with the same tint applied once to sky, fog and lighting. |

Every changed default has an XML reason in `AtmosphereSettings`, and the Task 1
exact-default tests cover all five values. A CPU exposure test and GPU capture
assertions pin the requested brightness bands. The white probe retains its
existing `ChromaPreservation = 0`, matching the CPU fog oracle. The named sky and
fog captures retain the engine default chroma preservation of 0.75. Both use the
engine's ACES pass at exposure 1. The white value does not assert a universal
ceiling for all surface orientations and chroma settings.

The original 8-unit floor with grey tint missed most of the downward sunset
view. The fixture now draws an 80-unit white floor, exposing actual surface
lighting instead of treating the direct-only background ground boundary as a
receiver. Its sampled sunset floor is `(15,13,13)`, above pure black. The same
floor and box draws are used in both timed configurations. The independent moon
assertion now captures only sky, preventing a bright floor in the downward
opposite-sun control from masking celestial direction. Its 0.1 threshold and the
0.01 seam tolerance are unchanged.

Final self-review preserves the floor's original height by scaling only X and Z
with `(10,1,10)`. Uniform scaling had also thickened the floor and obscured the
box. A focused final capture run refreshes sunset and moonlit night, and checks
shared/fresh parity and disabled/toggle parity. Both parity checks pass, and only
the two expected missing grids fail. The tabulated capture pixels are unchanged.

The four refreshed 480 by 320 captures were inspected individually, followed by
five contact sheets covering 52 refreshed captures. The sky is visibly blue at
noon, night is dark blue, sunset retains a warm horizon and visible floor, and
terrain and all receiver families fade toward the physical fog colour. The old
unsuffixed display-billboard RED capture is excluded from this current evidence.
Coordinates below use an upper-left origin.

| Capture | Upper centre `(240,24)` RGB bytes | Lower centre `(240,240)` RGB bytes |
| --- | --- | --- |
| Noon | `(62,90,155)` | `(32,60,134)` |
| Moonlit | `(9,14,23)` | `(9,12,18)` |
| Sunset | `(28,28,28)` | `(15,13,13)` |
| Fog | `(41,90,186)` | `(195,182,163)` |

The specified complete local atmosphere filter ran once. All 22 GPU facts
executed, zero skipped. Of 71 tests, 66 passed and five failed. Four failures are
the expected absent grids. The fifth exposed the floor contamination of the
moon-direction control described above. After isolating that control, its single
focused GPU test passed. The complete set was not repeated and no tolerance was
loosened. Hosted grids and cross-backend validation still belong to the controller.

The CPU settings, lighting, cycle and sky filter executed 287 checks. 286 passed,
and one cycle scaling test retained an implicit S 1 baseline. Changing its
operation to triple the current solar scale preserved its 1.5 output multiplier
and cache assertions. Its focused rerun passed. The three original unit-source
physics checks and the new exposure check pass. All nine shader-emission and
corpus parity checks pass without update variables.

## Phase 1 limitations retained by R13

- The ground boundary reflects direct source illumination only. Diffuse sky
  irradiation of that boundary and its feedback into the multi-scattering source
  remain omitted, matching Hillaire's reference simplification. R13 explicitly
  retains this model. Scene surfaces still receive the separate ambient term.
- The aerial perspective volume remains deferred to
  [#1364](https://github.com/APKiwiOrg/KhaozEngine/issues/1364). Phase 1 uses its
  existing local-fog approximation. Brightness calibration does not extend it.

## One completed-frame measurement after calibration

The opted-in measurement ran exactly once after `build-slot status` showed no
atmosphere heavy job running. One unrelated quick format command was active.
No local shader-parity verification overlapped the measurement.

This measurement precedes the final fixture height correction. It used uniform
floor scale 10 in both configurations. The final fixture uses `(10,1,10)` to keep
the box above the ground. Atmosphere defaults are identical. The recorded times
are evidence for the earlier fixture geometry, not a measurement of the final
geometry, and the requested one-shot limit was preserved.

| Field | Value |
| --- | --- |
| Adapter/backend | Apple M2 Max, `metal-native` |
| OS | macOS 27.0.1, build 26A434 |
| Tested HEAD | `7326863b098b92f58813a5b3f981b485cf2e5fca` plus this calibration working tree |
| Build/target | Release, 1600 by 900, AA off, identical floor and box draws |
| Atmosphere off | 8.225700 ms |
| Atmosphere on | 4.473500 ms |
| On minus off | -3.752200 ms |
| Cold enabled frame | 463.707700 ms |
| Samples | One prime, one off frame, one on frame |

The negative single-sample delta is inconclusive. It does not show that atmosphere
improves performance or establish the 0.3 ms target. The timing boundary remains
`RenderInternal` through `Submit` and `WaitForIdle`, excluding construction,
preparation and readback. No retry or stress run was performed.

CPU, GPU, shader-parity, formatting logs, the GPU TRX, measured pixels and five
contact sheets were retained only in local scratch.
No golden update variable was set. No grid, shader source, emission table, engine
version or release file changed in this calibration.

## R14 night floor hue correction

The calibrated scalar floors previously replaced dim twilight scattering with
neutral grey before the dusk tint. Floors now carry `NightTint` at every time of
day and are not tinted again. The CPU lighting, horizon, directional sky and
cached zenith/ground paths use the same per-channel formulas as the GPU sky
consumer:

```text
Sky = max(S * pi * L * Tint, NightSkyFloor * NightTint)
Ambient = max(S * E * AmbientScale * Tint, NightAmbientFloor * NightTint)
```

The GPU atmosphere uniform block appends `NightTint`, growing from 304 to 320
bytes without moving existing members or resource bindings. All four emission
tables were regenerated. Only the 30 atmosphere producer/consumer fragment
programs moved. Layouts, vertex stages and unrelated programs remain identical.

Defaults stay S 2.7, moon fraction 0.04, ambient floor 0.05, sky floor 0.09 and
night tint `(0.45,0.65,1)`. The real Metal captures were inspected individually.
The sunset sky above the glow is dark blue-violet rather than neutral grey.
Physical sunset scattering can keep red above green while blue remains dominant.
Moonlit night retains its previous measured pixels.

| Capture | Upper centre `(240,24)` sRGB bytes | Lower centre `(240,240)` sRGB bytes |
| --- | --- | --- |
| Sunset before R14 | `(28,28,28)` | `(15,13,13)` |
| Sunset after R14 | `(23,19,26)` | `(9,8,12)` |
| Moonlit after R14 | `(9,14,23)` | `(9,12,18)` |

The selected capture bodies ran once. Shared/fresh parity and the seven fog
checks passed. Moonlit and fog failed only for absent grids. Sunset reached an
initial overstrict hue assertion requiring green above red. The final assertion
checks blue above both red and green, which the saved capture satisfies. No GPU
capture was repeated after that test-only correction. Full final GPU execution
and the three-family grid bake remain controller work.

CPU floor, cache, uniform layout and binding checks are covered by the focused
R14 runs. Emission-table regeneration and verification without update variables
each passed 9/9. Logs, the GPU TRX, pixel values and before/after PNGs were local inspection evidence.
No golden update variable, local grid bake or new frame timing was used.

## Hosted provenance

The first hosted bake, run 38276133410, produced the twelve atmosphere grids
committed as `4984b18c1`. Its other grids matched the committed set exactly on
every backend, so the branch was then pixel-identical to main with the atmosphere
off.

Hosted verification runs 38280436247 and 38286143455 failed on two defects that
local Metal had not shown.

- Disabled consumers read a stale atmosphere uniform. Uniform writes land in the
  recording's own ring segment, and the disabled path stopped uploading after its
  transition clear. A later frame could therefore bind a segment last written by
  an enabled frame. Every consumer now gates on a flag in its own frame upload,
  with no added command (`d0dc48245`).
- On Direct3D 11 the sky-view horizon row fell on a float32 split between a
  ground hit and the tangent. The CPU mirror and every producer now share one
  analytic tangent and factored sphere offset (`1ae06b617`). The horizon GPU
  diagnostic writes its own uniforms (`39990c87f`).

Hosted bake 38302256595 on `1ae06b617` passed every atmosphere test on all three
backends, including the Direct3D 11 LUT oracle. Its grids are committed as
`6bd9caef7`. Atmosphere sunset moved by at most 0.0090 on every backend from the
horizon fix. Splat, tile ground, sky discs, skinned normal map, water focus and
temporal debug grids moved by at most 0.0065 on Metal and Vulkan. The flag
values are unchanged with the atmosphere off, so this is recompiled shader
arithmetic, and every delta is inside the 0.01 golden tolerance. Direct3D 11
moved only the atmosphere grids.

Failures outside this branch on the hosted Windows leg are tracked separately.
They are the point shadow seam (#1190), native map fixtures (#1316), the WARP
crash probes (#1215) and the racing TileWorld writers (#1376). The Metal spawn
search allocation bound is #1373.
