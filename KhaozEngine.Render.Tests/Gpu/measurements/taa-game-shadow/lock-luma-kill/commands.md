# Verification commands

Commands run from the engine worktree root. Scratch output names below are repo-relative equivalents of the
private scratch directories used for capture. No engine package was packed and no game pin was changed.

## Full engine verification

```bash
build-slot --heavy --label grain-full-build -- dotnet build KhaozEngine.slnx -c Release
env -u KE_GPU_TESTS build-slot --heavy --label grain-full-suite -- \
  dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
build-slot --label grain-required-format -- dotnet format KhaozEngine.slnx --no-restore \
  --include KhaozEngine.Render.Tests/Gpu/TemporalMovingShadeLagGpuTests.cs \
  KhaozEngine.Render.Tests/Gpu/TemporalShadowEdgeLagGpuTests.cs
build-slot --label grain-format-verify -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Full format exits 2 on unchanged files. The requested two-file format exits zero. An additional format pass
covered every owned C# file, and the final two added-test files received a project-scoped verify pass.

## Baselines and candidate filters

The first baseline ran with the two shader files from merged HEAD, recorded as `shaderBaselineCommit` in
`verification.json`. Its textured regression fails with the archive's exact values. Its initial dark-light
fixture was rejected as quantized black. After raising illumination, the second command measured the accepted
light baseline before production code changed.

```bash
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native KE_TEMPORAL_SHADE_EVIDENCE_DIR=work/before \
  build-slot --heavy --label grain-before -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~Texture_ridges_under_a_coarse_moving_shadow_do_not_hold_bright_grain|FullyQualifiedName~TemporalTextureLightGpuTests|FullyQualifiedName~TemporalMovingShadeLagGpuTests' \
  --logger 'console;verbosity=detailed'
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native build-slot --heavy --label grain-light-before -- \
  dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-restore \
  --filter FullyQualifiedName~TemporalTextureLightGpuTests --logger 'console;verbosity=detailed'
```

The candidate source variants are defined in the README. `floor02`, `pairs02` and `corners005` each used the
following filter, with the direct luma-lock class added from the pairs candidate onward. The output directory
matched the candidate name. Each was a single run of changed code.

```bash
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native KE_TEMPORAL_SHADE_EVIDENCE_DIR=work/corners005 \
  build-slot --heavy --label grain-corners005 -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --no-restore \
  --filter 'FullyQualifiedName~Texture_ridges_under_a_coarse_moving_shadow_do_not_hold_bright_grain|FullyQualifiedName~TemporalTextureLightGpuTests|FullyQualifiedName~TemporalMovingShadeLagGpuTests|FullyQualifiedName~TemporalStableDetailGpuTests|FullyQualifiedName~TemporalStablePeakGpuTests|FullyQualifiedName~TemporalResolveShaderTests|FullyQualifiedName~TemporalEntryIdentityGpuTests|FullyQualifiedName~TemporalResolveLockGpuTests|FullyQualifiedName~TemporalResolveLumaLockGpuTests' \
  --logger 'console;verbosity=detailed'
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill/compare-pan.py work/before work/corners005
```

## All temporal correctness and attribution

```bash
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native build-slot --heavy --label grain-full-temporal -- \
  dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~Temporal&FullyQualifiedName!~CostPerfGpuTests' --logger 'console;verbosity=detailed'
```

To attribute the failing existing guards, the two shader source files were temporarily restored from the merged
HEAD and the following 46-fact selection ran. The candidate source bytes were saved before the run and restored
in a Python `finally` block after it. The baseline ran with no diagnostic defines or overlays.

```bash
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native build-slot --heavy --label grain-existing-before -- \
  dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-restore \
  --filter 'FullyQualifiedName~TemporalResolveLockGpuTests|FullyQualifiedName~TemporalUpscalingGoldenTests|FullyQualifiedName~TemporalMovingShadeRimGpuTests|FullyQualifiedName~Local_light_state_leaves_the_shared_scene_equal_to_a_fresh_scene' \
  --logger 'console;verbosity=detailed'
build-slot --heavy --label grain-candidate-restored-build -- dotnet build KhaozEngine.slnx -c Release --no-restore
KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native build-slot --heavy --label grain-restored-proof -- \
  dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~Local_light_state_leaves_the_shared_scene_equal_to_a_fresh_scene|FullyQualifiedName~TemporalResolveLumaLockGpuTests|FullyQualifiedName~TemporalResolveShaderTests|FullyQualifiedName~TemporalEntryIdentityGpuTests' \
  --logger 'console;verbosity=detailed'
```

The restored render DLL matches the full GPU run's DLL byte for byte. Its SHA-256 is in `verification.json`.
The added fixture parity fact passes with both shaders. The last proof passes 74 facts. Neither timing benchmark
class was run. The full temporal correctness run is red and the final proof does not supersede that result.

## Shader hashes and repository guards

```bash
KE_UPDATE_MSL_HASHES=1 KE_UPDATE_HLSL_HASHES=1 KE_UPDATE_SPIRV_HASHES=1 \
  build-slot --heavy --label grain-shader-hashes -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --no-restore \
  --filter 'FullyQualifiedName~MetalMslByteEqualityTests|FullyQualifiedName~D3D11HlslByteEqualityTests|FullyQualifiedName~VulkanSpirvByteEqualityTests' \
  --logger 'console;verbosity=detailed'
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

## Hosted run

One dispatch on the pushed candidate, with no other dispatch active on that branch.

```bash
gh workflow run cross-platform-gpu.yml --ref fix/taa-game-shadow -f legs=all -f tier=push -f bake=false \
  -f renderTestFilter='FullyQualifiedName~Temporal&FullyQualifiedName!~CostPerfGpuTests'
gh run watch 39119425055 --interval 30 --exit-status
```

The billed T4 workflow was not dispatched.

## Archived release comparison and numeric exports

These commands read completed artifacts. They start no additional GPU job. The extractor accepts only the
passed moving-shading fact and normalizes the Windows TRX line endings before parsing its 38 summary rows.

```bash
gh run download 39076952301 -n shard-trx-metal-native-render-2 -D work/before214/metal-native
gh run download 39076952301 -n shard-trx-direct3d11-native-render-3 -D work/before214/direct3d11-native
gh run download 39076952301 -n shard-trx-vulkan-native-render-7 -D work/before214/vulkan-native
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill/extract-release-moving.py \
  work/before214 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill/summarize-guards.py \
  metal-local KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill/logs/full-temporal-metal.txt \
  KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/lock-luma-kill/guard-values
```

The summarizer pins the guard definitions to tested code commit `507f0d135`, rather than reading future tuning
from a later checkout. It exports JSON and CSV, and labels comparisons that are too close to decide from the
printed precision. The test's own result remains authoritative. Hosted logs are downloaded from the single
run's `filtered-render-measurements-<backend>` artifacts and normalized to repo-relative source paths before
entering this folder.
