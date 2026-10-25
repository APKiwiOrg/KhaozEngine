# Local TAA shadow visibility correctness gate

Tested commit: `44a9879b7b844effd030c3b881d5059f7341ecfe` on `fix/taa-shadow-visibility`.

Backend: `MetalNative`. Device: `Apple M2 Max`. Device class: `hardware`.

Every requested local test process ran once through build-slot. No unchanged failure was retried. No bounds, shader sources, pipeline state, output layouts, golden references or version declarations changed in this fix.

## Allocation correction

`ModelRenderer.Foliage.cs:129` enters `EnsureFoliagePipeline` from the per-frame `UploadFoliageUniforms` call at line 160. Before the fix, the lambda at line 151 captured the local shader set. Its display-class allocation preceded the cached-pipeline return at line 131. One 24-byte object per frame accounts for the reported 384 bytes across 16 frames. The other 16 production selector expressions run during pipeline creation or output changes.

The single API is `bool TrySelect(IGpuResourceFactory factory, in GpuOutputDescription outputs, string vertex, string fragment, bool preserve, out IGpuShaderSet shaders)`. All 17 production callers use a conditional expression. Only the false branch evaluates the fallback. Missing visibility still disposes cached visibility shader sets, and fallback disposal stays with its original owner.

The existing four steady-frame allocation combinations now pass their zero-byte assertions. All seven ModelVisibilityShaderTests cases retain their lazy fallback, cache and ownership meanings. MotionTargetWiringTests and MotionTargetLifecycleGpuTests expectations were preserved.

## Commands and results

Commands run from the worktree root.

```bash
TAA_CORRECTNESS='((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~TemporalResolveCostPerfGpuTests&FullyQualifiedName!~TemporalOutlineCostPerfGpuTests)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests)'
build-slot --label taa-alloc-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --label taa-alloc-cpu -- env KE_GPU_TESTS=0 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~MotionFrameAllocationTests|FullyQualifiedName~ModelVisibilityShaderTests|FullyQualifiedName~MotionTargetWiringTests"
build-slot --heavy --label taa-final-headless -- env KE_GPU_TESTS=0 dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
build-slot --label taa-final-metal-inventory -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$TAA_CORRECTNESS" --list-tests
build-slot --heavy --label taa-final-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$TAA_CORRECTNESS" --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=taa-final-metal.trx' --results-directory /tmp/grimhollow-taa-final/alloc-fix
build-slot --label taa-final-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
build-slot --label taa-final-dashes -- sh scripts/check-dashes.sh --tree
build-slot --label taa-final-prose -- sh scripts/check-prose.sh --tree
build-slot --label taa-final-size -- sh scripts/check-file-size.sh --tree
build-slot --label taa-final-instructions -- sh scripts/check-agent-instructions.sh --tree
build-slot --label taa-final-doc-versions -- bash scripts/check-doc-versions.sh
build-slot --label taa-final-diff -- git diff --check
```

| Command label | Exit code | Result |
| --- | ---: | --- |
| taa-alloc-build | 0 | Zero warnings, zero errors |
| taa-alloc-cpu | 0 | 22 passed, 0 failed, 0 skipped |
| taa-final-headless | 0 | 41 assembly summaries, 36,970 passed, 0 failed, 1,475 skipped, 38,445 total |
| taa-final-metal-inventory | 0 | 1,555 unique cases in 201 classes |
| taa-final-metal | 0 | 1,555 executed, 1,555 passed, 0 failed, 0 skipped |
| taa-final-format | 0 | No format changes required, workspace-loading warning recorded below |
| taa-final-dashes | 0 | Whole tree passed |
| taa-final-prose | 0 | Whole tree passed |
| taa-final-size | 0 | Whole tree passed, no baseline changes |
| taa-final-instructions | 0 | 8,310 / 16,384 bytes |
| taa-final-doc-versions | 0 | Declarations match existing 21.8.0 |
| taa-final-diff | 0 | No whitespace errors |

Part A precommit commands also exited 0: `sh scripts/check-dashes.sh staged`, `sh scripts/check-prose.sh staged`, `sh scripts/check-file-size.sh staged`, `bash scripts/check-doc-versions.sh` and `git diff --cached --check`. The commit and branch push exited 0. `git ls-remote origin refs/heads/fix/taa-shadow-visibility` returned the tested commit above.

`dotnet format` printed "Warnings were encountered while loading the workspace. Set the verbosity option to the 'diagnostic' level to log warnings." Its workspace-loading warning details were not expanded at the requested verbosity. It exited 0 and was not retried. The Release build had zero warnings.

Headless skips are not Metal proof. Render.Tests accounts for 1,067 of the headless skips with GPU facts disabled. The selected Metal skipped list is empty: `[]`.

## Known flake outcome

[KhaozEngine #1512](https://github.com/APKiwiOrg/KhaozEngine/issues/1512) did not recur. `PhysicsRebaseCostTests.Rebase_StaysWithinASmallMultipleOfOneStep_AtAResidentStreamingScale` is an ordinary Fact included in the successful Game.Tests pass (6,329 passed, 0 failed, 1 unrelated skip). No ratio was printed for the passing fact at the requested console verbosity. It was not retried or changed.

## Metal inventory and per-class results

The complete [local Metal inventory](local-metal-inventory.jsonl) records every discovered and executed case with its class and outcome. Comparing name multisets against discovery found zero missing cases, zero extra cases and zero duplicates.

Discovered-name SHA-256 (sorted names, newline terminated): `22508ff92205537f85c5460fbb8a0cfad8445d3855707f5002fd881ca5cf96ea`.

Committed JSONL SHA-256: `29f5f20da04162f7b38d2faf163fcc279ea4a113eaec5f53a5cf6798def80cb9`.

Reference supplied by the orchestrator: `df821a2ad` ran the plan selection at 1,553 of 1,553 passed, zero skipped. This run adds the two explicitly requested MotionTargetLifecycleGpuTests cases and executes 1,555 of 1,555.

Required classes are present and passed: TemporalMovingShadeRimGpuTests (2), TemporalEntryIdentityGpuTests (8), MotionTargetLifecycleGpuTests (2).

| Class | Inventory | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: |
| `KhaozEngine.Tests.Gpu.AlphaCutoutShadowGpuTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.AtmosphereFogGoldenTests` | 8 | 8 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.AtmosphereFogPhaseGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.AtmosphereLutGoldenTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.AtmosphereSkyGoldenTests` | 9 | 9 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.BareLocalGpuRunTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.BlobDecalGhostHoleGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.BlobShadowReceiverGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ButtonStateTintGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.CascadeHandoffBlendGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.CascadeShadowGpuTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.CollisionOverlayGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.CrossBackendGoldenTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.D3D11FxcValidationTests` | 16 | 16 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.D3D11ShaderPathTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.FarFromOriginGoldenTests` | 9 | 9 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.FoliageShadowGpuTests` | 8 | 8 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.FrameCommandCountShapeTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.FrameUniformUploadShapeGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.FrustumCullingGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.GoldenEvidenceTests` | 10 | 10 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.GoldenImageTests` | 50 | 50 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.GoldenParticleAttractorTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.GoldenSnapshotTests` | 32 | 32 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.GpuBackendKindAppendAuditTests` | 8 | 8 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.GpuBackendKindMetalAppendAuditTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.GpuBackendKindVulkanAppendAuditTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.GroundDecalVoidGoldenTests` | 9 | 9 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.HlodCrossfadeShadowCoverageGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.InteriorCutShadowGpuTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.LowSunShadowGoldenTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.MotionTargetLifecycleGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.MsaaResolveTargetGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.NestedRecordingSiteTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.OceanFftGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.PointShadowByteIdentityGpuTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.PointShadowExclusionBoxGpuTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.PointShadowFilterGpuTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.PointShadowGpuTests` | 16 | 16 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.PointShadowNearRadiusGpuTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.PointShadowPassGpuTests` | 16 | 16 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.PropLodCrossfadeGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.Render3DGpuSkinningGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.RigidAttachmentSetGoldenTests` | 8 | 8 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.RigidEarlyCullingGpuTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShaderSourceValidationTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShadowCascadeCullGpuTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShadowCasterPolicyGpuTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShadowContactGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShadowDepthDirtySkipGpuTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShadowLightHoldGpuTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShadowOnlyCasterGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShadowPassDiagnosticsGpuTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.ShadowReconfigureGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SilhouetteContinuityGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SilhouetteGoldenTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SkinnedAttachmentSetGoldenTests` | 63 | 63 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SkinnedFrustumCullingGpuTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SkinnedPointShadowGoldenTests` | 21 | 21 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SkinnedShadowCasterPolicyGpuTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SkinnedTargetOutlineGoldenTests` | 17 | 17 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SkinnedTargetOutlinePoseGoldenTests` | 8 | 8 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SkyWorldHorizonGoldenTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.SplatTerrainGridGoldenTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TargetOutlineGoldenTests` | 12 | 12 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TargetOutlinePerspectiveGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalAcceptanceRigGpuTests` | 18 | 18 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalAccumulateAtDisplaySizeTests` | 11 | 11 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalConvergedStabilityGpuTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalConvergenceGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalCountProbeAllocationGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalCountProbeGpuTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalDebugViewAllocationGpuTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalDebugViewGoldenTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalDiagnosticsSizesGpuTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalDiagonalDetailGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalDisocclusionGpuTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalEntryFlipMemoryGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests` | 8 | 8 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalFastEdgeGpuTests` | 17 | 17 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalFollowCameraGpuTests` | 40 | 40 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalFollowLinesGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalFollowPhaseGpuTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalFollowStopGpuTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalFollowedHistoryGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalFormatsTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalGameShadowGrainGpuTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalGhostingGpuTests` | 14 | 14 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalHeldBackgroundGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalHistoryTargetsGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalLitVisibilityGpuTests` | 39 | 39 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalLongHoldFollowGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalMaterialLodGpuTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalMipBiasGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalNarrowCrossingGpuTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalOffByteIdentityGoldenTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalOutlineGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalPassingBoxGpuTests` | 13 | 13 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalPerspectiveFollowGpuTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalPostTargetsAllocationGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalPostTargetsGpuTests` | 12 | 12 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResetGpuTests` | 16 | 16 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResolveCaptureAllocationGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResolveEdgeCaseGpuTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResolveLockGpuTests` | 42 | 42 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResolvePolicyTests` | 12 | 12 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResolvePrecisionTests` | 17 | 17 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResolveRendererGpuTests` | 11 | 11 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResolveSceneGpuTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalResolveSmokeGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalShadowEdgeLagGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalShadowFilterBaselineTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalShadowFilterGpuTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalShadowGatherCompileTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalSharpenChainGpuTests` | 11 | 11 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalSharpenPassGpuTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalStabilityGpuTests` | 19 | 19 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalStableDetailGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalStablePeakGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalStarfieldGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalStaticOrbitGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalSteadyFrameAllocationTests` | 14 | 14 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalStoredLockGpuTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalTextureLightGpuTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalUpscalingGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalVisibilityComparisonGpuTests` | 48 | 48 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TemporalVisibilityHistoryGpuTests` | 8 | 8 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TerrainShadowCastingGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.TileWorldPropHlodGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Gpu.WaterGridFocusGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Imaging.GoldenGridTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.NameplateTierTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.AtmosphereRendererTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.AtmosphereScatteringTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.FoliagePointShadowTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.FoliageShadowSelectionTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.InteriorCutShadowTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.LightShadowTests` | 12 | 12 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointLightBufferTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowAllocationTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowAtlasBindTests` | 9 | 9 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowAtlasFailureTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowFilterShaderTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowMathTests` | 14 | 14 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowReconfigureTests` | 20 | 20 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowResidencyTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowSettingsTests` | 47 | 47 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowSlotsTests` | 15 | 15 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.PointShadowUboLayoutTests` | 14 | 14 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.RigidAttachmentSubmissionTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.RigidInstanceDrawTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.Scene3DMotionHistoryTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.Scene3DPassTimingsGpuTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.Scene3DSkinnedCullingTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.Scene3DUnloadDrainTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowBlobQueueTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowCascadeCullTests` | 18 | 18 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowCascadeStabilityTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowCasterPolicyTests` | 20 | 20 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowCasterSignatureTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowDepthDirtyTests` | 19 | 19 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowDepthSelectionTests` | 24 | 24 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowDissolveNoiseTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowLayoutReplacementTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowLightHoldTests` | 21 | 21 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowMapDetailTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowMapMathTests` | 40 | 40 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowPassDiagnosticsAllocTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowPassDiagnosticsTests` | 11 | 11 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowReconfigureRequestTests` | 11 | 11 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowSettingsTests` | 21 | 21 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.ShadowShaderContractTests` | 34 | 34 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedAttachmentSubmissionTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedColorCutoutContractTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedDrawFlagTests` | 58 | 58 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedMeshMaterialLifetimeTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedPointShadowCullingTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedPointShadowReconfigureTests` | 14 | 14 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedPointShadowRowsTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedPointShadowSchedulingTests` | 22 | 22 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkinnedPointShadowShaderTests` | 6 | 6 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.SkyMathTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.TemporalCpuIsolationTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.UboLayoutTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.Render3D.WaterFlatPlaneGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Snapshot.DiffCommandsTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.Terrain.GroundCoverGpuOptionsTests` | 2 | 2 | 0 | 0 |
| `KhaozEngine.Tests.Terrain.GroundCoverRendererTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Terrain.PropBlobShadowGpuTests` | 5 | 5 | 0 | 0 |
| `KhaozEngine.Tests.Terrain.PropClusterRendererTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Terrain.PropLayerCastsShadowsTests` | 7 | 7 | 0 | 0 |
| `KhaozEngine.Tests.Terrain.SplatTerrainDistanceGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Terrain.SplatTerrainGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.Terrain.TerrainFarTierGoldenTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.TileWorld.GoldenTileWorldTests` | 4 | 4 | 0 | 0 |
| `KhaozEngine.Tests.TileWorld.SteepWallGoldenTests` | 3 | 3 | 0 | 0 |
| `KhaozEngine.Tests.TileWorld.TileGroundGeometryGoldenTests` | 17 | 17 | 0 | 0 |
| `KhaozEngine.Tests.TileWorld.TileWorldRoofModeTests` | 1 | 1 | 0 | 0 |
| `KhaozEngine.Tests.TileWorld.TileWorldSceneDescriptorTests` | 2 | 2 | 0 | 0 |

## Tail measurements

All values below are the exact printed `TAIL` values on hardware. Controls are bilinear-only captures in the same process. S 1.0 rows are the paired denominators. Each S 8.0 textured tail also stays at or below 1.5 times its matching S 1.0 tail. Its absolute textured and flat bounds are the paired control plus 0.0000005, except the owner-accepted Native retreating textured baseline of 0.007221076276167364 plus 0.0000005.

| Case | Textured tail | Paired textured control | Textured absolute bound | Flat tail | Paired flat control | Flat absolute bound |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Quality S 1.0 px advancing | 0.00331335880246998 | 0.003274129809061437 | Denominator | 0.0005198812313742143 | 0.0005198812313742143 | Denominator |
| Quality S 1.0 px retreating | 0.008684358840454827 | 0.007222394455541695 | Denominator | 0 | 0 | Denominator |
| Quality S 8.0 px advancing | 0.004021039177650219 | 0.004305140606911194 | 0.004305640606911194 | 0.0001030753092393778 | 0.0001030753092393778 | 0.0001035753092393778 |
| Quality S 8.0 px retreating | 0.005836947537498854 | 0.005979106915913975 | 0.005979606915913975 | 0.00035064034392796005 | 0.00035064034392796005 | 0.00035114034392796005 |
| Native S 1.0 px advancing | 0.002999879928125934 | 0.0031476987540463596 | Denominator | 0.0005198812313742143 | 0.0005198812313742143 | Denominator |
| Native S 1.0 px retreating | 0.005606684118436591 | 0.005600123603749221 | Denominator | 0 | 0 | Denominator |
| Native S 8.0 px advancing | 0.004308605299682656 | 0.004456876628838288 | 0.004457376628838288 | 0.0001030753092393778 | 0.0001030753092393778 | 0.0001035753092393778 |
| Native S 8.0 px retreating | 0.007221076276167364 | 0.007138470657410495 | 0.007221576276167364 | 0.00035064034392796005 | 0.00035064034392796005 | 0.00035114034392796005 |

## Rim measurements

Every printed `RIM-EXACT` row is retained below. Tuples are error / raw change / temporal error / spatial-reference change. Raw change is diagnostic. Error and temporal error use `(gate baseline + 0.0000005) * 1.1`. Spatial-reference change must stay within 0.000001 of its frozen control.

The Quality M 1.0 leading textured row retains its historical bilinear control (error 0.002465, temporal error 0.003928) alongside the accepted D gate baselines (0.003011, 0.004819). These give bounds 0.00331265 and 0.00530145. All other hardware gate baselines are their frozen bilinear controls.

| Case | Measured error / change / temporal / reference | Bilinear control error / change / temporal / reference | Error / temporal bounds |
| --- | --- | --- | --- |
| Quality M 0.5 leading textured | 0.015129552346530495 / 0.009893521760577517 / 0.010963221373407564 / 0.0185085224564494 | 0.015187 / 0.009878 / 0.010978 / 0.018509 | 0.01670625 / 0.01207635 |
| Quality M 0.5 leading flat | 0.010485128443557899 / 0.005813892488413778 / 0.005813892488413778 / 0 | 0.010485 / 0.005814 / 0.005814 / 0.000000 | 0.01153405 / 0.00639595 |
| Quality M 0.5 trailing textured | 0.026527313989671793 / 0.01623093407064735 / 0.016549124149881172 / 0.018950977986145773 | 0.026839 / 0.016335 / 0.016683 / 0.018951 | 0.02952345 / 0.01835185 |
| Quality M 0.5 trailing flat | 0.007356933926368926 / 0.005763357760167921 / 0.005763357760167921 / 0 | 0.007357 / 0.005763 / 0.005763 / 0.000000 | 0.00809325 / 0.00633985 |
| Quality M 1.0 leading textured | 0.0030106876217401947 / 0.03180997173537265 / 0.004819472651560228 / 0.0318161181303171 | 0.002465 / 0.032098 / 0.003928 / 0.031816 | 0.00331265 / 0.00530145 |
| Quality M 1.0 leading flat | 0.008109476973231023 / 0.005947087217163254 / 0.005947087217163254 / 0 | 0.008109 / 0.005947 / 0.005947 / 0.000000 | 0.00892045 / 0.00654225 |
| Quality M 1.0 trailing textured | 0.008126322864913023 / 0.037305900588795377 / 0.007894227737253838 / 0.03561466357865176 | 0.008816 / 0.038484 / 0.008000 / 0.035615 | 0.00969815 / 0.00880055 |
| Quality M 1.0 trailing flat | 0.0002227330207824707 / 0.00022264139992850168 / 0.00022264139992850168 / 0 | 0.000223 / 0.000223 / 0.000223 / 0.000000 | 0.00024585 / 0.00024585 |
| Quality M 2.0 leading textured | 0.007762411947433765 / 0.05803456442696708 / 0.013219369518887865 / 0.06221152322632926 | 0.008198 / 0.059204 / 0.013613 / 0.062212 | 0.00901835 / 0.01497485 |
| Quality M 2.0 leading flat | 0.004856045286242778 / 0.01205665938801818 / 0.005180999970698094 / 0.00692838103561611 | 0.004856 / 0.012057 / 0.005181 / 0.006928 | 0.00534215 / 0.00569965 |
| Quality M 2.0 trailing textured | 0.011359777576648272 / 0.05266491850326349 / 0.015293600316886063 / 0.05619411896545808 | 0.011890 / 0.053527 / 0.015196 / 0.056194 | 0.01307955 / 0.01671615 |
| Quality M 2.0 trailing flat | 0.0009692100378183218 / 0.0009736075244107089 / 0.0009736075244107089 / 0 | 0.000969 / 0.000974 / 0.000974 / 0.000000 | 0.00106645 / 0.00107195 |
| Quality M 4.0 leading textured | 0.012142290802529225 / 0.057529378562838164 / 0.020986373264055985 / 0.0649292155281528 | 0.012072 / 0.057556 / 0.020889 / 0.064929 | 0.01327975 / 0.02297845 |
| Quality M 4.0 leading flat | 0.003665259572175833 / 0.04649389489666446 / 0.014419680205020276 / 0.03227104141161992 | 0.003665 / 0.046494 / 0.014420 / 0.032271 | 0.00403205 / 0.01586255 |
| Quality M 4.0 trailing textured | 0.013994696017068166 / 0.05194854790365303 / 0.02125283936222831 / 0.0661768608204611 | 0.013996 / 0.051954 / 0.021253 / 0.066177 | 0.01539615 / 0.02337885 |
| Quality M 4.0 trailing flat | 0.0002721452254515428 / 0.0002950437776334993 / 0.0002950437776334993 / 0 | 0.000272 / 0.000295 / 0.000295 / 0.000000 | 0.00029975 / 0.00032505 |
| Quality M 8.0 leading textured | 0.012903680884494232 / 0.063362527519137 / 0.013807758698751638 / 0.0662942255787797 | 0.012904 / 0.063363 / 0.013808 / 0.066294 | 0.01419495 / 0.01518935 |
| Quality M 8.0 leading flat | 0.002258471118716093 / 0.09012640261388087 / 0.0022047265348853645 / 0.08830137039933886 | 0.002258 / 0.090126 / 0.002205 / 0.088301 | 0.00248435 / 0.00242605 |
| Quality M 8.0 trailing textured | 0.012221117566984433 / 0.05411454623217111 / 0.018634401055482717 / 0.06315998425850501 | 0.012221 / 0.054115 / 0.018634 / 0.063160 | 0.01344365 / 0.02049795 |
| Quality M 8.0 trailing flat | 0.0009655225620819972 / 0.0009697597236423703 / 0.0009697597236423703 / 0 | 0.000966 / 0.000970 / 0.000970 / 0.000000 | 0.00106315 / 0.00106755 |
| Quality MS 1.0 leading textured | 0.0024672967166854787 / 0.031010798982211523 / 0.00490280868260415 / 0.03180289488899839 | 0.003170 / 0.032814 / 0.006352 / 0.031803 | 0.00348755 / 0.00698775 |
| Quality MS 1.0 leading flat | 0.005432146701675195 / 0.010037535952997731 / 0.007805869684114561 / 0.008074408049111838 | 0.005432 / 0.010038 / 0.007806 / 0.008074 | 0.00597575 / 0.00858715 |
| Quality MS 1.0 trailing textured | 0.006061943187736548 / 0.0394190803530452 / 0.008105561494172275 / 0.03863740777248865 | 0.006920 / 0.040068 / 0.009264 / 0.038637 | 0.00761255 / 0.01019095 |
| Quality MS 1.0 trailing flat | 0.00029172807931900026 / 0.00038162708282470705 / 0.00038162708282470705 / 0 | 0.000292 / 0.000382 / 0.000382 / 0.000000 | 0.00032175 / 0.00042075 |
| Quality MS 2.0 leading textured | 0.007423273688898637 / 0.05824997174215841 / 0.01321294692846445 / 0.06222318578552414 | 0.007026 / 0.058983 / 0.012407 / 0.062223 | 0.00772915 / 0.01364825 |
| Quality MS 2.0 leading flat | 0.0036768599599599837 / 0.017124405608072386 / 0.005665650989983108 / 0.014040695782546159 | 0.003677 / 0.017124 / 0.005666 / 0.014041 | 0.00404525 / 0.00623315 |
| Quality MS 2.0 trailing textured | 0.011196310537365767 / 0.05415238575293468 / 0.015120780949409192 / 0.05851817683859186 | 0.011942 / 0.054947 / 0.015760 / 0.058518 | 0.01313675 / 0.01733655 |
| Quality MS 2.0 trailing flat | 0.0010778054594993592 / 0.001087822232927595 / 0.001087822232927595 / 0 | 0.001078 / 0.001088 / 0.001088 / 0.000000 | 0.00118635 / 0.00119735 |
| Native M 0.5 leading textured | 0.012502522292462263 / 0.011522170098928305 / 0.009797194761754963 / 0.0185085224564494 | 0.012530 / 0.011513 / 0.009807 / 0.018509 | 0.01378355 / 0.01078825 |
| Native M 0.5 leading flat | 0.006141368075684234 / 0.004394708565000952 / 0.004394708565000952 / 0 | 0.006141 / 0.004395 / 0.004395 / 0.000000 | 0.00675565 / 0.00483505 |
| Native M 0.5 trailing textured | 0.01731463847095733 / 0.015353607696424104 / 0.013121853424952581 / 0.018950977986145773 | 0.017748 / 0.015495 / 0.013315 / 0.018951 | 0.01952335 / 0.01464705 |
| Native M 0.5 trailing flat | 0.0009036727926947854 / 0.000990890539609469 / 0.000990890539609469 / 0 | 0.000904 / 0.000991 / 0.000991 / 0.000000 | 0.00099495 / 0.00109065 |
| Native M 1.0 leading textured | 0.003264778766494531 / 0.03331318164264763 / 0.003478085477273543 / 0.0318161181303171 | 0.003317 / 0.033344 / 0.003553 / 0.031816 | 0.00364925 / 0.00390885 |
| Native M 1.0 leading flat | 0.004479863764001773 / 0.0040710398980549406 / 0.0040710398980549406 / 0 | 0.004480 / 0.004071 / 0.004071 / 0.000000 | 0.00492855 / 0.00447865 |
| Native M 1.0 trailing textured | 0.006544453848439913 / 0.038519167248364336 / 0.0068704642077068705 / 0.03561466357865176 | 0.007682 / 0.040229 / 0.008234 / 0.035615 | 0.00845075 / 0.00905795 |
| Native M 1.0 trailing flat | 5.6624412536621094E-05 / 5.6624412536621094E-05 / 5.6624412536621094E-05 / 0 | 0.000057 / 0.000057 / 0.000057 / 0.000000 | 0.00006325 / 0.00006325 |
| Native M 2.0 leading textured | 0.003080951330753473 / 0.06417254021207056 / 0.004923808112904266 / 0.06221152322632926 | 0.003057 / 0.064270 / 0.004850 / 0.062212 | 0.00336325 / 0.00533555 |
| Native M 2.0 leading flat | 0.0027365261488235914 / 0.01004501570057083 / 0.003134996471824227 / 0.00692838103561611 | 0.002737 / 0.010045 / 0.003135 / 0.006928 | 0.00301125 / 0.00344905 |
| Native M 2.0 trailing textured | 0.0051258244566046276 / 0.05877806615370971 / 0.007150619337846944 / 0.05619411896545808 | 0.005520 / 0.059573 / 0.007617 / 0.056194 | 0.00607255 / 0.00837925 |
| Native M 2.0 trailing flat | 5.6624412536621094E-05 / 5.6624412536621094E-05 / 5.6624412536621094E-05 / 0 | 0.000057 / 0.000057 / 0.000057 / 0.000000 | 0.00006325 / 0.00006325 |
| Native M 4.0 leading textured | 0.005191230601989306 / 0.06319130719690533 / 0.007711995096652062 / 0.0649292155281528 | 0.005172 / 0.063215 / 0.007680 / 0.064929 | 0.00568975 / 0.00844855 |
| Native M 4.0 leading flat | 0.0035028121104607214 / 0.0352992973943333 / 0.003170470267861754 / 0.03227104141161992 | 0.003503 / 0.035299 / 0.003170 / 0.032271 | 0.00385385 / 0.00348755 |
| Native M 4.0 trailing textured | 0.0058796966591706645 / 0.06341228479539955 / 0.008769847940612625 / 0.0661768608204611 | 0.005880 / 0.063413 / 0.008770 / 0.066177 | 0.00646855 / 0.00964755 |
| Native M 4.0 trailing flat | 3.5390257835388184E-05 / 3.2356807163783484E-05 / 3.2356807163783484E-05 / 0 | 0.000035 / 0.000032 / 0.000032 / 0.000000 | 0.00003905 / 0.00003575 |
| Native M 8.0 leading textured | 0.006689144685291327 / 0.06606906707797731 / 0.007587622354973803 / 0.0662942255787797 | 0.006689 / 0.066069 / 0.007588 / 0.066294 | 0.00735845 / 0.00834735 |
| Native M 8.0 leading flat | 0.0027838592575146603 / 0.09081749570893717 / 0.0028368982377943103 / 0.08830137039933886 | 0.002784 / 0.090817 / 0.002837 / 0.088301 | 0.00306295 / 0.00312125 |
| Native M 8.0 trailing textured | 0.005697030634261095 / 0.06149271945704471 / 0.008226742616721561 / 0.06315998425850501 | 0.005697 / 0.061493 / 0.008227 / 0.063160 | 0.00626725 / 0.00905025 |
| Native M 8.0 trailing flat | 7.74681568145752E-05 / 5.6624412536621094E-05 / 5.6624412536621094E-05 / 0 | 0.000077 / 0.000057 / 0.000057 / 0.000000 | 0.00008525 / 0.00006325 |
| Native MS 1.0 leading textured | 0.003028685891857514 / 0.03333993944492969 / 0.00360251797752066 / 0.03180289488899839 | 0.003149 / 0.033374 / 0.003771 / 0.031803 | 0.00346445 / 0.00414865 |
| Native MS 1.0 leading flat | 0.00263874409863582 / 0.008888596774457575 / 0.0042224929686431045 / 0.008074408049111838 | 0.002639 / 0.008889 / 0.004222 / 0.008074 | 0.00290345 / 0.00464475 |
| Native MS 1.0 trailing textured | 0.005455167004122184 / 0.0409329246197428 / 0.0070517181498663765 / 0.03863740777248865 | 0.006426 / 0.041604 / 0.008439 / 0.038637 | 0.00706915 / 0.00928345 |
| Native MS 1.0 trailing flat | 0.0001399993896484375 / 0.00012808867863246373 / 0.00012808867863246373 / 0 | 0.000140 / 0.000128 / 0.000128 / 0.000000 | 0.00015455 / 0.00014135 |
| Native MS 2.0 leading textured | 0.003327289352623316 / 0.06386261937709956 / 0.0055415720566288455 / 0.06222318578552414 | 0.003204 / 0.064626 / 0.005626 / 0.062223 | 0.00352495 / 0.00618915 |
| Native MS 2.0 leading flat | 0.002445279606259786 / 0.016089220007697306 / 0.0041877655799572285 / 0.014040695782546159 | 0.002445 / 0.016089 / 0.004188 / 0.014041 | 0.00269005 / 0.00460735 |
| Native MS 2.0 trailing textured | 0.005118541302016148 / 0.06096252861586246 / 0.007697754302522638 / 0.05851817683859186 | 0.005290 / 0.061438 / 0.008059 / 0.058518 | 0.00581955 / 0.00886545 |
| Native MS 2.0 trailing flat | 0.0001399993896484375 / 0.00012808867863246373 / 0.00012808867863246373 / 0 | 0.000140 / 0.000128 / 0.000128 / 0.000000 | 0.00015455 / 0.00014135 |

## Grain measurements

Exact textured-grain values are paired with the printed spatial-reference grain, error grain and temporal change from the same test process. Native and Quality are bounded. Balanced remains report-only. The two facts that call TexturedShadowGrainCase printed identical values.

| Preset | Exact output grain | Reference grain | Error grain | Temporal change | Hardware bound |
| --- | ---: | ---: | ---: | ---: | --- |
| Native | 0.010346265407577604 | 0.009240 | 0.006087 | 0.008786 | 0.011300 |
| Quality | 0.008031049526854536 | 0.009240 | 0.008329 | 0.008446 | 0.008032 |
| Balanced | 0.007526431887194493 | 0.009240 | 0.006882 | 0.008367 | report only |

The coarse-receiver diagnostic has no grain acceptance bound. It requires nonempty band and control regions, reference contrast above 0.04 and finite transition width. Its printed output retains the paired reference and FXAA controls below. Luma units are 0 to 1.

```text
rate hold mode bandErr p95 grainOut grainRef grainFXAA grainError change changeRef changeErr farErr farGrain farGrainRef farGrainFXAA farChange farChangeRef farChangeErr width
0    1 Quality 0.00135 0.0084 0.00005 0.00002 0.00003 0.00024 0.00027 0.00020 0.00027 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 12.50
0    1 Native  0.00101 0.0067 0.00003 0.00002 0.00003 0.00021 0.00026 0.00020 0.00026 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 12.50
0    1 FXAA    0.00011 0.0008 0.00003 0.00002 0.00003 0.00009 0.00020 0.00020 0.00009 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 12.47
0    0 Quality 0.00135 0.0084 0.00005 0.00002 0.00003 0.00024 0.00027 0.00020 0.00027 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 12.50
0    0 Native  0.00101 0.0067 0.00003 0.00002 0.00003 0.00021 0.00026 0.00020 0.00026 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 12.50
0    0 FXAA    0.00011 0.0008 0.00003 0.00002 0.00003 0.00009 0.00020 0.00020 0.00009 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 0.00000 12.47
1    1 Quality 0.01042 0.0630 0.00068 0.00002 0.00002 0.00101 0.00321 0.00363 0.00474 0.00018 0.00001 0.00000 0.00000 0.00003 0.00010 0.00012 12.16
1    1 Native  0.00923 0.0562 0.00004 0.00002 0.00002 0.00019 0.00301 0.00363 0.00458 0.00018 0.00000 0.00000 0.00000 0.00003 0.00010 0.00012 12.29
1    1 FXAA    0.00529 0.0386 0.00002 0.00002 0.00002 0.00015 0.00306 0.00363 0.00496 0.00000 0.00000 0.00000 0.00000 0.00010 0.00010 0.00000 12.46
1    0 Quality 0.00574 0.0339 0.00097 0.00002 0.00002 0.00143 0.00374 0.00363 0.00275 0.00018 0.00001 0.00000 0.00000 0.00003 0.00010 0.00012 12.10
1    0 Native  0.00435 0.0255 0.00004 0.00002 0.00002 0.00020 0.00358 0.00363 0.00159 0.00018 0.00000 0.00000 0.00000 0.00003 0.00010 0.00012 12.29
1    0 FXAA    0.00010 0.0003 0.00002 0.00002 0.00002 0.00009 0.00363 0.00363 0.00016 0.00000 0.00000 0.00000 0.00000 0.00010 0.00010 0.00000 12.47
4    1 Quality 0.00592 0.0375 0.00116 0.00002 0.00002 0.00158 0.01435 0.01439 0.00543 0.00011 0.00000 0.00000 0.00000 0.00008 0.00017 0.00012 12.05
4    1 Native  0.00457 0.0263 0.00004 0.00002 0.00002 0.00019 0.01430 0.01439 0.00340 0.00011 0.00000 0.00000 0.00000 0.00008 0.00017 0.00012 12.30
4    1 FXAA    0.00009 0.0003 0.00002 0.00002 0.00002 0.00008 0.01439 0.01439 0.00017 0.00000 0.00000 0.00000 0.00000 0.00017 0.00017 0.00000 12.47
4    0 Quality 0.00592 0.0375 0.00116 0.00002 0.00002 0.00158 0.01435 0.01439 0.00543 0.00011 0.00000 0.00000 0.00000 0.00008 0.00017 0.00012 12.05
4    0 Native  0.00457 0.0263 0.00004 0.00002 0.00002 0.00019 0.01430 0.01439 0.00340 0.00011 0.00000 0.00000 0.00000 0.00008 0.00017 0.00012 12.30
4    0 FXAA    0.00009 0.0003 0.00002 0.00002 0.00002 0.00008 0.01439 0.01439 0.00017 0.00000 0.00000 0.00000 0.00000 0.00017 0.00017 0.00000 12.47
8    1 Quality 0.00610 0.0378 0.00116 0.00002 0.00002 0.00156 0.02870 0.02863 0.00792 0.00014 0.00000 0.00000 0.00000 0.00021 0.00011 0.00026 12.09
8    1 Native  0.00462 0.0263 0.00004 0.00002 0.00002 0.00018 0.02868 0.02863 0.00574 0.00014 0.00000 0.00000 0.00000 0.00021 0.00011 0.00026 12.27
8    1 FXAA    0.00009 0.0003 0.00002 0.00002 0.00002 0.00009 0.02863 0.02863 0.00018 0.00000 0.00000 0.00000 0.00000 0.00011 0.00011 0.00000 12.45
8    0 Quality 0.00610 0.0378 0.00116 0.00002 0.00002 0.00156 0.02870 0.02863 0.00792 0.00014 0.00000 0.00000 0.00000 0.00021 0.00011 0.00026 12.09
8    0 Native  0.00462 0.0263 0.00004 0.00002 0.00002 0.00018 0.02868 0.02863 0.00574 0.00014 0.00000 0.00000 0.00000 0.00021 0.00011 0.00026 12.27
8    0 FXAA    0.00009 0.0003 0.00002 0.00002 0.00002 0.00009 0.02863 0.02863 0.00018 0.00000 0.00000 0.00000 0.00000 0.00011 0.00011 0.00000 12.45
```

## Evidence scope

TRX and full process logs remain under `/tmp/grimhollow-taa-final/alloc-fix`. Only the compact inventory and this result record are committed. Local Metal passed. Hosted Direct3D 11, Vulkan and hosted Metal on this fixed commit remain for the orchestrator. No workflow, main merge, tag or release was started.

## Hosted

Run [39265183977](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39265183977) tested `058eaea98295a529b9f98cafdff92dbb51f40886` with `legs=all` and `renderTestFilter` equal to the Metal selection above, including `MotionTargetLifecycleGpuTests`. The run concluded success.

| Leg | Device class | Selected facts | Passed | Failed | Skipped | Quality grain | Bound |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| macos-26 metal-native | hardware | 1555 | 1555 | 0 | 0 | 0.008031 | 0.008032 |
| windows-latest direct3d11-native | WARP | 1555 | 1555 | 0 | 0 | 0.008013 | 0.008014 |
| ubuntu-latest vulkan-native | lavapipe | 1555 | 1555 | 0 | 0 | 0.008040 | 0.008040 |

The Vulkan synchronization validation job also passed. Every backend executed the same 1555 selected facts as the local Metal inventory, so no selected GPU fact was skipped on any backend. The earlier run [39238918090](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39238918090) on `df821a2ad` passed the same plan selection on all legs before the allocation correction.


## 7f local gate and byte-identical shipping capture

Tested source `8fe6c5927c6fd0fa4960bcd6dda88e2c209f8ff5` on `fix/taa-shadow-visibility`. The guarded engine and probe worktrees started clean. Every build, test, pack, capture and archive replay ran once through build-slot. No unchanged test or capture was retried. No source, test, bound, baseline or golden changed.

| Command label | Exit | Result |
| --- | ---: | --- |
| taa-7f5-build | 0 | Release solution, 0 warnings, 0 errors |
| taa-7f5-headless | 0 | 41 assemblies, 37,110 passed, 0 failed, 1,484 skipped, 38,594 total |
| taa-7f5-metal-inventory | 0 | 1,687 cases, 209 classes, no cost, perf or bench class |
| taa-7f5-metal | 0 | 1,687 executed and passed, 0 failed, 0 skipped |
| taa-7f5-pack | 0 | 222 private package files, no compiler or package warning or error |
| taa-7f5-probe-build | 0 | SnapshotTool Release, 0 warnings, 0 errors |
| taa-7f5-back15 | 0 | 3 continuous mode histories, 16 measured frames each, 160 intermediate images deleted |
| taa-7f5-ground15 | 0 | 3 continuous mode histories, 16 measured frames each, 160 intermediate images deleted |
| taa-7f5-analysis | 0 | 12 full-precision rows, RGB and masks identical to shipping D |

```bash
TAA_CORRECTNESS='((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~CostPerf)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests)'
build-slot --label taa-7f5-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label taa-7f5-headless -- env KE_GPU_TESTS=0 dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
build-slot --label taa-7f5-metal-inventory -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$TAA_CORRECTNESS" --list-tests
build-slot --heavy --label taa-7f5-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$TAA_CORRECTNESS" --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=taa-7f5-metal.trx' --results-directory /tmp/grimhollow-taa-7f5/metal
```

Metal used MetalNative on Apple M2 Max hardware. The [inventory](7f-metal-inventory.jsonl) retains every result. Discovery and TRX name multisets match with zero missing or extra cases. There are 1,687 distinct TRX test IDs. Three theory cases have one shared truncated display name, retained with multiplicity three. The discovered-name multiset SHA256 is `0fa46ceab93cdffe8371b00a579a257bddd80b06845013884a8bf2852695c25c`. The selected skipped list is `[]`. TemporalMovingShadeRimGpuTests passed 2 cases, TemporalEntryIdentityGpuTests 8, MotionTargetLifecycleGpuTests 2, TemporalVisibilityEquivalenceGpuTests 6 and TemporalVisibilityResolveAbGpuTests 12.

Headless GPU and unavailable-service skips are not Metal evidence. The known [PhysicsRebaseCostTests flake](https://github.com/APKiwiOrg/KhaozEngine/issues/1512) did not recur. The [local results](7f-local-results.json) retain all assembly counts and raw log and TRX hashes. Full logs, TRX and capture sheets remain under `/tmp/grimhollow-taa-7f5/`.

Private package `21.8.0-svcut.1` was packed from a detached scratch worktree at the tested source, with only Directory.Build.props changed. The version did not exist in the private feed before packing and no same-version NuGet extraction existed before refresh. The feed was `~/.cache/khaozengine-probe-feed/taa-shadow-visibility`. All 222 vendored packages match it. The scratch worktree was removed after packing. The pin and both tool pins were committed and pushed on `probe/taa-shadow-visibility` at `c480240d588a023dc8ac3f00311ddd478913f20a`, with the remote SHA verified before capture. The copied renderer matched the vendored nupkg before capture and both capture provenance records name that renderer.

| Identity | SHA256 |
| --- | --- |
| Render3D nupkg | `61f8905cc09cc26b5656e89e99c492d614d0c62518c977bb806a6b8090978a80` |
| Render3D symbols | `fd5c33cd1cf704ef8332d9bf8f1f74eb51b25a2d3dd2581394a5d99541631a87` |
| Loaded renderer, build and both captures | `d831e1f1b591df59e49a70028b791351eb6e6e48dd0cd514d44d746350db6a90` |

The [7f real-scene record](real-scene.md#7f-shipping-capture) contains every full-precision metric row and the RGB and mask hashes. Every row and retained pixel is identical to shipping D. The verifier extension adds the 7f source, all 222 package hashes, local gate and capture checks. An AST comparison confirms every historical verifier statement is unchanged. The three historical builds, shipping build, D reference and owner acceptance records are unchanged. The artifact hash list also repairs the preexisting ledger drift described below.

Hosted correctness, the parallel reviewer verdict and any billed performance proof remain with the orchestrator. This worker ran no timing, perf or bench class, workflow dispatch, merge, tag or release. No manual validation is needed for this verification round.


Every numeric cell in this 7f comparison is shipping D / 7f, at full printed precision.

| Condition | Region | Mode | Grain mean | Grain p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| back15 | character | taa-native | 0.005474607155653376 / 0.005474607155653376 | 0.029102745098039118 / 0.029102745098039118 | 0.017496411969800285 / 0.017496411969800285 | 364 / 364 |
| back15 | ground | taa-native | 0.00526385609353138 / 0.00526385609353138 | 0.020472941176470583 / 0.020472941176470583 | 0.012355875438918482 / 0.012355875438918482 | 45 / 45 |
| back15 | character | taa-quality | 0.008704202242702883 / 0.008704202242702883 | 0.04507215686274513 / 0.04507215686274513 | 0.017329273445767134 / 0.017329273445767134 | 759 / 759 |
| back15 | ground | taa-quality | 0.0062244609736071465 / 0.0062244609736071465 | 0.024378823529411675 / 0.024378823529411675 | 0.01227338604965782 / 0.01227338604965782 | 821 / 821 |
| back15 | character | fxaa | 0.0026071029228602943 / 0.0026071029228602943 | 0.015403137254901944 / 0.015403137254901944 | 0.018394583895005687 / 0.018394583895005687 | 0 / 0 |
| back15 | ground | fxaa | 0.0016548521211947336 / 0.0016548521211947336 | 0.0075599999999999 / 0.0075599999999999 | 0.013100857420120631 / 0.013100857420120631 | 0 / 0 |
| ground15 | character | taa-native | 0.00293740035335003 / 0.00293740035335003 | 0.01121411764705893 / 0.01121411764705893 | 0.0011689773847883102 / 0.0011689773847883102 | 371 / 371 |
| ground15 | ground | taa-native | 0.004815313021091819 / 0.004815313021091819 | 0.017416470588235367 / 0.017416470588235367 | 0.003688204569631017 / 0.003688204569631017 | 262 / 262 |
| ground15 | character | taa-quality | 0.0030111794193593325 / 0.0030111794193593325 | 0.011816470588235206 / 0.011816470588235206 | 0.0011939310203571874 / 0.0011939310203571874 | 330 / 330 |
| ground15 | ground | taa-quality | 0.005046020977145357 / 0.005046020977145357 | 0.020724705882352867 / 0.020724705882352867 | 0.0037393807392348626 / 0.0037393807392348626 | 782 / 782 |
| ground15 | character | fxaa | 0.002280812938047255 / 0.002280812938047255 | 0.012598431372548957 / 0.012598431372548957 | 0.0013507738528787314 / 0.0013507738528787314 | 0 / 0 |
| ground15 | ground | fxaa | 0.0022932411255083716 / 0.0022932411255083716 | 0.007575686274509774 / 0.007575686274509774 | 0.003328692573683128 / 0.003328692573683128 | 0 / 0 |


The final formatting check exited 0. It emitted the same workspace-loading warning as the historical run. Warning details were not expanded and the unchanged format command was not retried. Release builds had zero warnings. Whole-tree dashes, prose, file-size and instruction checks, doc-version checks and git diff --check each exited 0. Their commands and exit codes are retained in the local results JSON.


The guarded source already had three stale artifact hashes (filter-baseline.md, non-finite-depths.md and reset-tuning.md) and four omitted artifact entries (benchmark-request.md, benchmark-result.md, local-metal-inventory.jsonl and verification.md). Their bytes were checked against the source commit. The three stale documents are unchanged. The artifact ledger now hashes the current source documents and includes the omitted entries. The [drift record](7f-preexisting-manifest-drift.json) preserves the old hashes. Every historical source, package, capture, baseline, D-reference and owner-acceptance check remains unchanged.


The manifest verifier exited 0 with 72 historical rows, 12 shipping rows and 12 new 7f rows checked. The final whole-tree dashes, prose and file-size checks also exited 0 after the evidence additions. Local and capture checkboxes in 7f.5 are complete. Hosted correctness and review checkboxes remain open.


## 7g.3a barrier lock release local gate and byte-identical shipping capture

Tested source `485c030a4` on `fix/taa-shadow-visibility`, packed and captured at `ef97639b6d2b33973715652a35858d55e0665a0f`, which adds only the benchmark-request note on V4. Production's `temporalReleaseLock` is the probe's B helper (`ShaderSources.TemporalLockReleaseGlsl`), with no `precise` in any temporal program. `LockMotionReleaseScale` is `0x3EAAAAAB`, written as its exact decimal, and `TemporalResolveMath.ReleaseLock` is its C# twin. `Sizes.x` is documented as the barrier source and `BuildUniforms` holds it at 1 or more, which a device-free theory checks down to `int.MinValue`. The guarded engine and probe worktrees started clean. Every build, test, pack, capture and replay ran through build-slot. No bound, tolerance, baseline, golden or accepted-cost row changed.

| Command label | Exit | Result |
| --- | ---: | --- |
| taa-7g3a-writer | 0 | SPIR-V, HLSL, MSL and corpus writers. Exactly 10 fragment rows per table and 30 corpus rows moved, TemporalPrepare and every vertex row unchanged |
| taa-7g3a-build | 0 | Release solution, 0 warnings, 0 errors |
| taa-7g3a-headless | 1 | 41 assemblies, 37,158 passed, 1 failed, 1,491 skipped, 38,650 total |
| taa-7g3a-catalog-diag | 0 | The failing class alone, 4 passed in 6 s |
| taa-7g3a-metal-inventory | 0 | 1,692 cases, 211 classes, no cost, perf or bench class |
| taa-7g3a-metal | 0 | 1,692 executed and passed, 0 failed, 0 skipped |
| taa-7g3a-alloc | 0 | MotionFrameAllocationTests 5 passed, 0 bytes a steady frame |
| taa-7g3a-format | 0 | Formatting verification, the historical workspace-loading warning only |
| taa-7g3a-pack | 0 | 222 private package files, no compiler or package warning or error |
| taa-7g3a-probe-build | 0 | SnapshotTool Release, 0 warnings, 0 errors |
| taa-7g3a-back15 | 0 | 3 continuous mode histories, 16 measured frames each, 160 intermediate images deleted |
| taa-7g3a-ground15 | 0 | 3 continuous mode histories, 16 measured frames each, 160 intermediate images deleted |
| taa-7g3a-analysis | 0 | 12 full-precision rows, RGB and masks identical to shipping D |

```bash
TAA_CORRECTNESS='((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~CostPerf)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests|FullyQualifiedName~TemporalLockReleaseProbeGpuTests)'
build-slot --label taa-7g3a-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label taa-7g3a-headless -- env KE_GPU_TESTS=0 dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
build-slot --label taa-7g3a-metal-inventory -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$TAA_CORRECTNESS" --list-tests
build-slot --heavy --label taa-7g3a-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter "$TAA_CORRECTNESS" --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=taa-7g3a-metal.trx' --results-directory /tmp/grimhollow-taa-7g3a/metal
```

The one headless failure is `ContentUpgradePublishCleanupTests.AnOlderPublishLeavesTheNextRunnersFrozenDraftOwnedByItsPublish(sqlite: True, sweepFails: False)` in KhaozEngine.Catalog.Tests. It hit its own 20 s deadline (`OperationCanceledException` after 43 s) while the full suite ran in parallel, and passed alone. The change touches no catalog code. It is not the known [PhysicsRebaseCostTests flake](https://github.com/APKiwiOrg/KhaozEngine/issues/1512), which did not recur. The ledger holds no issue for it. The headless gate is therefore not clean, and that decision stays with the orchestrator.

Metal used MetalNative on Apple M2 Max hardware. The [inventory](7g3a-metal-inventory.jsonl) retains every result. Discovery and TRX name multisets match with zero missing or extra cases, and the selected skipped list is `[]`. The discovered-name multiset SHA256 is `01adc742168e5e8f3d184d461252d6ac6af9b4dc4ad802d48e24ffab13370b02`. TemporalVisibilityResolveAbGpuTests passed 12 cases, TemporalEntryIdentityGpuTests 8, TemporalVisibilityEquivalenceGpuTests 6, MotionTargetLifecycleGpuTests 2, TemporalMovingShadeRimGpuTests 2, TemporalVisibilityFallbackGpuTests 4 and TemporalLockReleaseProbeGpuTests 1. The probe printed 4,194,304 cases written by both programs, with P vs B, P vs C and B vs C bit mismatches all 0.

Frozen D is split. `TemporalVisibilityFrozenD.LockReleaseGlsl` freezes D's `precise` helper, its comment and the blank line before it as a fourth pinned text (SHA256 `e2ae2ac8655f07847f1000a363bfd301d3cea93c8377ebe6739b4698d0c96695` at 583a3ae7b). `ToShippedD` is `ToD` with production's helper replaced by it, and the unchanged whole-program pins now hold `ToShippedD`. `ToD`, D prime, keeps production's helper, and a device-free theory proves the two differ in that helper alone. Every other rule, the entry identity, the equivalence probe and its counters, the NVIDIA Direct3D 11 rule and the DXBC D listings, compares against D prime. The resolve A/B adds a shipped D rig. On Metal production equalled it byte for byte in colour, state, R8 and depth on every frame of all 12 cases. Off Metal the differences are printed only.

Device-free facts in TemporalLockReleaseCompiledTests: no `precise` in the GLSL of any of the shipped temporal programs, zero NoContraction decorations in each of their vertex and fragment SPIR-V modules, zero `precise` in their emitted HLSL and zero `spvF` wrapper names in their emitted MSL. The program D shipped is the control that has all three. Exactly ten programs include the release, TemporalResolve, TemporalResolveHalf, TemporalAccumulate, TemporalAccumulateHalf, TemporalAccumulateAtDisplaySize, TemporalAccumulateAtDisplaySizeHalf, TemporalCountProbe, TemporalCountProbeHalf, TemporalDebugView and TemporalDebugViewHalf. Each compiles exactly 12 barrier ORs, an `OpBitwiseOr` of an unsigned shift right by 31, and TemporalPrepare compiles none. The barrier-free rewrite compiles none either.

V4 is redefined as V5 with the barriers made the identity (`WithoutVisibilityOrBarriers`), and a device-free fact proves it removes exactly the 12 barrier ORs from each visibility-free program. The fallback control renders it through both entries on Metal. The [benchmark request](benchmark-request.md) records that run 39519219228 measured the earlier precise V4. No cost, perf or bench class ran.

The six visibility programs' DXBC fixtures are stale on both sides, since D here is D prime. `EachFixtureWasCompiledFromTheCurrentGlsl` lists them in `StaleUntil7g3b` and passes only while both of each program's source pins differ, so a recapture that leaves one listed fails. The fixtures, their chain-form pins and `TheRealProgramPassesTheCheck` still read the 7g.2 listings. No pin was moved without a Windows FXC dump. Task 7g.3b recaptures them and empties the list.

Private package `21.8.0-svbar.1` was packed from a detached scratch worktree at the source, with only Directory.Build.props changed. The feed was `~/.cache/khaozengine-probe-feed/taa-shadow-visibility`. All 222 vendored packages match it. The pin and both tool pins were committed and pushed on `probe/taa-shadow-visibility` at `bd396fd8bdbf6e811d35848fd2983337eec21a4c`, with the remote SHA verified before capture. The [7g.3a real-scene record](real-scene.md#7g3a-barrier-lock-release-shipping-capture) contains every full-precision row and the RGB and mask hashes. Every row and retained pixel is identical to shipping D.

The guarded source already had one stale artifact hash (benchmark-request.md) and two omitted artifact entries (benchmark-result-2.md and mask-withdrawal.md). The [drift record](7g3a-preexisting-manifest-drift.json) preserves the old hash. The artifact ledger now hashes the current documents and includes the omitted and new entries. The verifier extension adds the 7g.3a source, all 222 package hashes, the local gate and capture checks, and leaves every historical check unchanged.

Hosted WARP, lavapipe and NVIDIA runs, the DXBC recapture and the re-measured accepted-cost rows remain with 7g.3b and the orchestrator. This worker ran no workflow dispatch, timing, perf or bench class, merge, tag or release.

The manifest verifier exited 0 with 72 historical rows, 12 shipping rows, 12 7f rows and 12 new 7g.3a rows checked.
