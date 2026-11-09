# Resolve cost scenes, visibility paths and R8 footprints

Measured on 2026-10-22 with MetalNative on Apple M2 Max, Release, 2560x1440 display. Source base
`1b7ba67c4`. This is a readback, not a timing. No cost, timing, bench or stress case was run.

## Method and frame identity

[The new fact](../../TemporalResolveCostPathShareGpuTests.cs) uses the cost fixture's own
[BoxesScene and FieldScene](../../TemporalResolveCostPerfGpuTests.cs) and [FrontStage](../../TemporalAcceptanceScenes.cs),
with the same 4.5 orthographic size. Each case advances once through four split frames, four fused frames,
then its requested entry. Scene3D frame 9 is draw callback 8, the render whose counts the cost fixture
requests. The count probe reported `CountsFrameIndex=9` in every case. The second sample is Scene3D
frame 90, draw callback 89, with established history and continuing authored motion. History was ready
at both samples. Changing entries preserves history.

The eight cases cover both scenes, Native and Quality, fused and split. Each renders an exporting and
unmodified production scene one frame each in lockstep. The only extra assertions are complete path
coverage, a named reason for every slow pixel and zero bit differences in stored locks, as in
[the existing yard fact](../../TemporalShadowedFramePathShareGpuTests.cs). Both facts share
[the readback and assertions](../../TemporalVisibilityPathShareReadback.cs). All sixteen cost-scene
samples and all four yard cases had zero stored-lock differences across 3,686,400 display pixels each.

[The footprint export](../../TemporalVisibilityFootprintExport.cs) extends
[the yard's production path export](../../TemporalVisibilityProductionPaths.cs) only in test shader text.
It reads the whole geometric footprint before depth rejection, including zero-weight corners: the
clamped current 3x3, the fast path's full reprojected 4x4 previous block, or the slow path's union of
nine clamped bilinear previous footprints using D's position chain. This is a conservative whole
footprint, including texels a subsequent depth rejection may discard. R8 equality compares the sampled
UNorm values, which preserve distinct stored bytes. For uniform pairs it also runs D's unchanged
`temporalVisibilityChange` and strict threshold. Metadata uses the signs of three half-float colour
channels as `-x-1`, then decodes back to the original path word and all 32 signed-change bits before
common counting. Production state writes and production source files are unchanged.

## Scene facts and stored R8 histograms

These scenes do have a directional key light without explicitly configuring a sun.
[PixelPostProcessSettings](../../../../KhaozEngine.Render3D/PixelPostProcessSettings.cs) supplies direction
`(-0.5, -0.85, -0.35)` and colour `(1, 0.95, 0.86, 1)`. `UseSmoothPreset` leaves lighting and shadows
untouched. [ShadowSettings.Mode](../../../../KhaozEngine.Render3D/ShadowMode.cs) defaults to `Off`.
FrontStage never changes it. Every sample reported requested and effective shadows `Off` and key shadow
pass active `False`. The key shades objects, but it casts no shadow in these fixtures. The scene does
allocate the R8 target for temporal rendering. The current target, current stored history and previous
stored history all hold full visibility, byte 255, with no other populated bin.

The following histograms apply separately to both scenes, both entries and both sampled frames.

| Preset | Internal size | Current R8 target | Current history write | Previous history read |
| --- | --- | --- | --- | --- |
| Native | 2560x1440 | 255 = 3,686,400 (100%) | 255 = 3,686,400 (100%) | 255 = 3,686,400 (100%) |
| Quality | 1707x960 | 255 = 1,638,720 (100%) | 255 = 1,638,720 (100%) | 255 = 1,638,720 (100%) |

## Visibility path shares

Every percentage below uses the full display, 3,686,400 pixels, as denominator. Fused and split have
identical exact counts for each scene, preset and frame, so each row reports both entries.
Not reaching the call site is zero throughout. Fast change computed, fast with nothing accepted,
released and fast released are also exactly zero throughout. Thus every fast pixel is an R4 range skip.
Other gating retains the yard's category for no usable history, background or first-frame state.

| Scene | Preset | Scene frame | Entries | Zero lock R1 | Gated otherwise | Check runs | Fast = range skip R4 | Slow |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Boxes | Native | 9 | Fused, split | 100.00% | 0.00% | 0.00% | 0.00% | 0.00% |
| Boxes | Native | 90 | Fused, split | 100.00% | 0.00% | 0.00% | 0.00% | 0.00% |
| Boxes | Quality | 9 | Fused, split | 100.00% | 0.00% | 0.00% | 0.00% | 0.00% |
| Boxes | Quality | 90 | Fused, split | 100.00% | 0.00% | 0.00% | 0.00% | 0.00% |
| Field | Native | 9 | Fused, split | 70.88% | 0.22% | 28.90% | 28.18% | 0.71% |
| Field | Native | 90 | Fused, split | 68.57% | 0.26% | 31.16% | 30.64% | 0.52% |
| Field | Quality | 9 | Fused, split | 53.23% | 0.41% | 46.36% | 46.14% | 0.22% |
| Field | Quality | 90 | Fused, split | 54.66% | 0.42% | 44.92% | 44.73% | 0.20% |

Every slow pixel computes a change. The boxes have no slow pixels. The field's slow reasons are below,
still as shares of the full display, with both entries identical. Base deviation is exactly zero.

| Preset | Scene frame | Slow edge | Slow base deviation | Slow border | Slow binade | Slow change computed |
| --- | --- | --- | --- | --- | --- | --- |
| Native | 9 | 0.04% | 0.00% | 0.04% | 0.63% | 0.71% |
| Native | 90 | 0.05% | 0.00% | 0.04% | 0.43% | 0.52% |
| Quality | 9 | 0.14% | 0.00% | 0.08% | 0.00% | 0.22% |
| Quality | 90 | 0.12% | 0.00% | 0.07% | 0.00% | 0.20% |

Exact field counts, each applying separately to fused and split. Every other path count is zero.
All boxes rows have exactly 3,686,400 R1 pixels and zero for every other path.

| Preset | Scene frame | Zero lock R1 | Gated otherwise | Checks | Fast = R4 | Slow | Edge | Border | Binade |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Native | 9 | 2,612,958 | 8,173 | 1,065,269 | 1,038,973 | 26,296 | 1,653 | 1,453 | 23,190 |
| Native | 90 | 2,527,874 | 9,684 | 1,148,842 | 1,129,638 | 19,204 | 1,691 | 1,529 | 15,984 |
| Quality | 9 | 1,962,145 | 15,173 | 1,709,082 | 1,700,957 | 8,125 | 5,339 | 2,786 | 0 |
| Quality | 90 | 2,014,943 | 15,342 | 1,656,115 | 1,648,814 | 7,301 | 4,600 | 2,701 | 0 |

## Uniform footprints and D's release rule

Here the denominator is pixels that run the check, rather than the whole display. Uniform-and-equal
means that all current footprint values and all previous footprint values are one and the same R8 byte.
Uniform-but-different means each frame's footprint is uniform but their two bytes differ.
Fused and split again have identical exact counts.

| Scene | Preset | Scene frame | Checked pixels | Both uniform | Uniform and equal | Uniform but different | D releases among uniform pairs |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Boxes | Native | 9 | 0 | n/a (0 checks) | n/a (0 checks) | 0 (n/a) | 0 / 0 (n/a) |
| Boxes | Native | 90 | 0 | n/a (0 checks) | n/a (0 checks) | 0 (n/a) | 0 / 0 (n/a) |
| Boxes | Quality | 9 | 0 | n/a (0 checks) | n/a (0 checks) | 0 (n/a) | 0 / 0 (n/a) |
| Boxes | Quality | 90 | 0 | n/a (0 checks) | n/a (0 checks) | 0 (n/a) | 0 / 0 (n/a) |
| Field | Native | 9 | 1,065,269 | 100% | 100% | 0 (0%) | 0 / 1,065,269 (0%) |
| Field | Native | 90 | 1,148,842 | 100% | 100% | 0 (0%) | 0 / 1,148,842 (0%) |
| Field | Quality | 9 | 1,709,082 | 100% | 100% | 0 (0%) | 0 / 1,709,082 (0%) |
| Field | Quality | 90 | 1,656,115 | 100% | 100% | 0 (0%) | 0 / 1,656,115 (0%) |

There is no uniform-but-different sample and no uniform-and-equal release counterexample. D rejects
empty current count or zero previous weight. Otherwise it independently normalizes current and previous
accepted sums. When every stored value is 255, both means are 1, regardless of which surfaces depth
qualification rejects. Its strict `abs(meanCurrent - meanPrevious) > 0.1` release rule cannot be met.
The export's separate D evaluation reported zero releases for every field sample, including slow pixels
that do not take R4. See [the frozen D rule](../../TemporalVisibilityFrozenD.cs) and
[the visibility contract](../../../../docs/design/TEMPORAL-RESOLVE.md).

## Yard control

The existing yard fact also passed all four cases after extracting its common reader. Its existing
`CoverageFrame=90` refers to draw callback 90, Scene3D frame 91. This keeps the yard's prior sample intact.
Both entries have identical exact counts in this local Metal run. Percentages use display pixels.

| Preset | Entries | Zero lock R1 | Gated otherwise | Check runs | Fast | R4 | Fast computed | Fast no acceptance | Slow | Edge | Base deviation | Border | Binade | Released |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Native | Fused, split | 99.00% | 0.00% | 1.00% | 0.99% | 0.91% | 0.08% | 0.00% | 0.01% | 0.00% | 0.00% | 0.01% | 0.00% | 0.02% |
| Quality | Fused, split | 95.92% | 0.00% | 4.07% | 3.39% | 3.11% | 0.27% | 0.01% | 0.69% | 0.01% | 0.00% | 0.05% | 0.63% | 0.04% |

The Quality yard has 19 gated-otherwise pixels, which round to 0.00%. Its 251 fast-no-acceptance
pixels round to 0.01%. Native has 57 fast-no-acceptance pixels, rounding to 0.00%.

## Reading

The field exercises the check over 1.07 to 1.71 million display pixels per sampled frame, compared with
about 37 thousand at Native and 150 thousand at Quality in the yard. The field's R8 signal has no shadow
variation at all. Between 97.53% and 99.56% of its checked pixels take the fast R4 path, after establishing
that the previous block's range cannot release. The remaining 0.44% to 2.47% of checked pixels take D's
slow path, mainly binade guards at Native and edges or borders at Quality. None release.

The dominant measured population is therefore fast checks proving that a fully lit uniform footprint
cannot release, rather than computing changing shadow means. This readback identifies workload and
path populations. It does not assign milliseconds to instructions or explain the T4 boxes regression,
whose sampled pixels here all gate off under R1. These are local Metal observations, not a T4 timing
or proof of identical populations on another backend. No production change is proposed here.

## Verification

All commands ran synchronously through build-slot, one command at a time.

| Gate | Exit | Result |
| --- | --- | --- |
| Release solution build | 0 | Zero warnings, zero errors |
| Metal readback facts, GPU tests on | 0 | 12 passed, zero failed, zero skipped |
| Device-free Temporal, GPU facts off, CostPerf excluded | 0 | 820 passed, 232 GPU-gated skips, zero failed |
| Solution format, verify only, no restore | 0 | Completed without formatting errors, workspace-loading warning |
| check-dashes --tree | 0 | Passed |
| check-prose --tree | 0 | Passed |
| check-file-size --tree | 0 | Passed |
| git diff --check | 0 | No whitespace errors |

Format emitted "Warnings were encountered while loading the workspace" and completed with exit 0.

The first build failed on a removed namespace import in the extracted yard fact. Restoring the import
was the only correction. The subsequent full Release solution build passed. Each GPU case ran once.

Reproduction commands from the worktree root follow. The GPU filter selects only the two readback classes.

```bash
build-slot --label taa-field-probe-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --label taa-field-probe-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~TemporalResolveCostPathShareGpuTests|FullyQualifiedName~TemporalShadowedFramePathShareGpuTests' --logger 'console;verbosity=detailed'
build-slot --label taa-field-probe-temporal --heavy -- env KE_GPU_TESTS=0 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~Temporal&FullyQualifiedName!~CostPerf'
build-slot --label taa-field-probe-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
build-slot --label taa-field-probe-dashes -- sh scripts/check-dashes.sh --tree
build-slot --label taa-field-probe-prose -- sh scripts/check-prose.sh --tree
build-slot --label taa-field-probe-size -- sh scripts/check-file-size.sh --tree
build-slot --label taa-field-probe-diff -- git diff --check
```
