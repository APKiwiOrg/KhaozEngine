# Renderer baseline implementation verification

This record covers current-main reconciliation and headless implementation correctness only.
It records no native baseline, hardware duration, artifact size, clock stability, floor, spend,
promotion, release or consumer acceptance. The [living evidence contract](../RENDERER-PERFORMANCE.md)
and [hosted admission contract](../CROSS-PLATFORM.md#paid-hosted-renderer-performance-mode) own those rules.

## Source and dependency boundary

The accepted implementation input is `db3e39ad64bc2a3e4950cef629cd90fb6400ca75`.
The reconciled main input is `989eba495ae7c0f9ff15973889481e3db91fd27a`, also the fetched main snapshot.
Their merge base is `10659a82c766d2d4ec354d9f215af3c890c7c477`.
The implementation contributes 47 unique commits and main contributes 159 before reconciliation.
The exact incoming dependency range is
`10659a82c766d2d4ec354d9f215af3c890c7c477..989eba495ae7c0f9ff15973889481e3db91fd27a`.
The generic timing extraction already exists at the merge base. No active equipment, filtered-exact,
coverage-DQ or consumer branch is an additional merge parent.

The current-main version 21.12.1 is released as `v21.12.1` at
`f2e5ef6a64046bbe39cf5ea866f9440ae476b89b`. No newer version was staged and no 21.13 tag
was present at selection. The package-bearing observer and optional Vulkan diagnostic additions stage
one additive version, 21.13.0. Released history remains intact. This is not a release or pack proof.

Seven content conflicts were reconciled. Relocated model-pass and cascade owners retain their profiling
scopes and main's visibility clear and dissolve documentation. Fused and split temporal entries retain
main's checked/certified selection, resource layouts, visibility history copy and fully-lit decision.
Their original command order remains inside the observer scopes. Scene temporal wiring retains both
outline ownership and main's receiver visibility argument. Released changelog history comes from main.

Two new leaves cover main's added commands, `opaque.visibility-clear` and `temporal.visibility-store`.
Executable identity uses the canonical `ShippedShaderPrograms.GraphicsPrograms` corpus, including lit
and destination-preserving visibility exports and backend-selected
checked/certified temporal sources. These are source/ownership corrections, not workload retuning.
Frozen authored recipes, D2 platform-family derived digests, quality, periodic camera/pose sequence,
continuous effect clock and the two named clipmap snaps are unchanged.

These source-byte identities describe the implementation submitted to local verification. Runtime
shader hashes are generated from composed sources, not from these C# file bytes.

| Reconciled source | SHA-256 |
| --- | --- |
| `KhaozEngine.Render3D/Rendering/ModelRenderer.FramePass.cs` | `2d15de4acc08ca7959c435b20d4d6b28f3c3f41528a1aee28bca8bbcf476bb5a` |
| `KhaozEngine.Render3D/Rendering/TemporalResolveRenderer.cs` | `872d110962f7b9c20c57210a0e15cbdbf5ad2542735cde1ed8156464ccd85420` |
| `KhaozEngine.Render3D/Rendering/TemporalSplitResolve.cs` | `7b5cbab01c23ef489823558133e62270f5f677c67a68e09e761b2c3b11e5cfa2` |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfExecutable.cs` | `880ec9b7b8964cf709bddce56f9e2edf8b15209fffa1e6c9a0cc58ed9d9edbf5` |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfShaderVariants.cs` | `c73f4fa5885b3b4eedbf4eff24a028e70bf01d0e4e8a08a68a5d0472ade0a590` |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfReconciliationTests.cs` | `aca05a5560f32bcaaed0b229f1a3d20acbb0ef3ede7da3c861ae0141f5a496fb` |

## Coverage inventory

Harness types below live under `KhaozEngine.Render.Tests/Gpu/RendererPerf` unless explicitly named
otherwise. Shared timing types live under its sibling `Performance` directory.
The complete public pass registry remains in the [consumer reference](../USING-KHAOZENGINE.md#gpu-timestamps-without-waiting).
The inventory here maps concerns to executable checks rather than duplicating that registry.

| Concern | Implementation and named correctness checks |
| --- | --- |
| S0 empty/1 | `RendererPerf/RendererPerfRecipe.cs`, `RendererPerfRecipeTests.Fixed_topology_counts_and_seed_have_stable_digest`, `RendererPerfEntrypointTests.Frozen_layout_records_cold_and_cached_routes_without_native_discovery` (S0) |
| S1 village/1 | `RendererPerfVillageScene.cs`, same topology and frozen-layout checks (S1), `RendererPerfRecipeIdentityTests` authored/family refusal cases |
| S2 outdoors/1 | `RendererPerfOutdoorsScene.cs`, `RendererPerfEffectsRecipeTests.Foliage_water_and_fft_routes_match_fixed_counts`, `Every_measured_ocean_frame_has_no_prime_submission_or_wait`, `Frozen_clipmap_schedule_names_both_events_and_the_affected_planes`, frozen-layout case S2 |
| S3 crowd/1 | `RendererPerfCrowdScene.cs`, `RendererPerfRecipeTests.Crowd_uses_32_visible_gpu_actors_and_declared_shadow_rows`, frozen-layout case S3 including unavailable transient rows |
| S4 effects-ui/1 | `RendererPerfEffectsScene.cs`, `RendererPerfUiScene.cs`, `RendererPerfEffectsRecipeTests.Ui_quads_textures_and_clips_are_fixed`, frozen-layout case S4 |
| Sequence and source identity | `RendererPerfFrozenRecipe.cs`, `RendererPerfConstructionInputs.cs`, `RendererPerfRecipeIdentityTests`, `Frozen_sequence_includes_absolute_effect_times`, `RendererPerfShaderIdentityTests`, `RendererPerfReconciliationTests.Actual_recorded_shader_sources_are_present_in_executable_identity` |
| Total-only and per-pass blocks | `RendererPerfManifest.cs`, `RendererPerfDriverTests.Executes_only_manifest_order_and_exact_counts`, `Full_schedule_keeps_final_short_batches_and_all_iterations`, `RendererPerfPostTraceTests.Null_observer_preserves_all_feature_command_trace` (two outer queries, no pass rows for total-only) |
| Initial clear, native End and completion ownership | `Performance/GpuPerfTimestampRing.cs`, `GpuPerfTimestampRingTests.Tail_contains_deferred_clear_and_native_finalization`, `Retains_before_begin_and_never_reuses_unconsumed_fence`, `RendererPerfFrameBoundaryTests`, `CoveragePerfTimestampEndTests` |
| Geometry, shadows, atmosphere and cached skips | `KhaozEngine.Render3D/Scene3D.*` owners, `RendererPerfGeometryTraceTests.Nests_every_cascade_face_and_geometry_route_once`, `Cached_shadow_or_lut_emits_NotExecuted`, `RendererPerfPointTraceTests`, `RendererPerfNullTraceTests` |
| Effects, FFT, copies, temporal, post and UI | Production owners under `KhaozEngine.Render3D/Rendering`, `RendererPerfPostTraceTests.Attributes_every_post_copy_and_effect` (effects-ui/outdoors/legacy/msaa-blob/split), `Prime_is_startup_and_column_next_row_are_distinct`, `Fused_or_skipped_work_is_not_zero_duration`, `RendererPerfOutlineTraceTests` |
| Added visibility clear and copy | `ModelRenderer.FramePass.cs`, `TemporalResolveRenderer.Visibility.cs`, `RendererPerfReconciliationTests.Visibility_clear_retains_its_leaf_and_native_order`, `Visibility_history_copy_has_one_leaf_after_resolve` (fused and split) |
| Device water geometry and bathymetry | `RendererPerfWaterUploadTraceTests.Initial_water_indices_have_a_device_upload_leaf`, `Revised_bathymetry_has_a_leaf_even_when_surface_is_culled`, `Inactive_water_skips_device_owners_without_gpu_markers` |
| Scene begin and cold retirement | `RendererPerfFrameBoundaryTraceTests.Cold_retirement_wait_is_owned_before_all_feature_msaa_recording` (both layouts), `Frame_begin_observer_failure_survives_preparation_and_resets_next_frame`. The warmed post fixture separately asserts zero retirement waits and exclusive leaf ownership |
| Requested commands and device operations | `RendererPerfCommandList.cs`, `RendererPerfDevice.cs`, `RendererPerfCounterTests.Counts_each_forwarded_update_once`, `Owns_every_draw_dispatch_copy_clear_and_bind`, `Preserves_optional_interfaces_and_disabled_sequence`. Full-device completeness checks also reject counters on nonleaf parents |
| Twelve controls | `RendererPerfControls.cs`, `RendererPerfControlWorkload.cs`, `RendererPerfControlTests.Controls_are_finite_matched_and_hash_all_work`, `Records_only_fixed_work_without_submit_wait_or_readback`, `Output_validation_rejects_removed_work_and_hashes_checked_outputs`, `RendererPerfControlRetentionTests`, `RendererPerfArithmeticSeedTests` |
| Resource dependencies and honest models | `RendererPerfDependencyTests.Every_declared_dependency_has_a_same_frame_resource_path`, `Ocean_column_reads_the_prior_row_set_in_both_ping_pong_directions`, `RendererPerfWorkModelTests`, `RendererPerfFloorModelTests`, `RendererPerfFloorEvidenceTests`. Cross-frame FFT dependencies are not fabricated same-frame edges |
| Raw publication and analysis | `RendererPerfRawValidationTests`, `RendererPerfCompactReportTests`, `RendererPerfDeviceReportTests`, `RendererPerfCpuIntervalsTests`, `RendererPerfMemoryEvidenceTests`, `RendererPerfPublicationCompletionTests`, `RendererPerfSidecarAccountingTests`, `RendererPerfAnalysisTests`, `RendererPerfComparisonTests` |
| Workflow admission and refused states | `RendererPerfWorkflowContractTests`, `RendererPerfWorkflowTrustTests`, `RendererPerfEntrypointTests.Changed_work_is_refused_before_native_creation`. Trusted runner selection, exact sole-manifest authorization ancestry and completion wait remain required |

The full preset expands 30 blocks. For each of three iterations, scenes rotate by
`(ordinal + iteration) % 5`. Each scene has both layouts. Total-only runs first when
`(scene + iteration) % 2 == 0`, otherwise per-pass runs first. Each block has 192 warm-up and 256
measured frames. The pilot has only S3, total-only then per-pass, one iteration, 64 warm-up and 128
measured frames per block. Conditioning is 1,200 full or 300 pilot frames. Full controls each have
64 warm-up and 128 measured frames. Pilot controls are IDs 1, 4, 5, 7, 9 and 11 with 32 warm-up and
64 measured frames each. Totals are exactly 16,944 and 1,260 retained frames.

Controls 1 through 12 are empty-frame, empty-main-mrt, empty-depth-only, observable-dispatch,
dependent-dispatch-36, independent-dispatch-36, storage-256-mib, storage-32-mib,
fp32-independent-fma, dependent-divide-root, fullscreen-write and fullscreen-blend-depth-8.
Their fixed seed bytes, work hashes, invocation counts, outputs and instruction classes remain separate.
The headless compiler corpus covers SPIR-V, HLSL and MSL retention. Native Windows FXC and actual control
readback remain later gates.

## Reproducible local verification

Run from this revision's repository root. These commands build the isolated performance project but
never execute its outside-solution fact. Headless test selection explicitly removes `KE_GPU_TESTS`.
Retain complete stdout/stderr, actual integer exits and per-assembly TRX files in a unique scratch directory.

```sh
build-slot --heavy --label renderer-task15a-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --label renderer-task15a-tools-build -- dotnet build tools/Renderer.Perf.Tests/Renderer.Perf.Tests.csproj -c Release
build-slot --heavy --label renderer-task15a-tests -- env -u KE_GPU_TESTS dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket' --logger trx --results-directory /tmp/khaozengine-renderer-task15a-trx
build-slot --heavy --label renderer-task15a-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Also run whole-tree dashes, prose, file-size and agent-instruction guards, doc-version checks and Git
whitespace checks through build-slot. The renderer workflow's static admission tests remain headless.
No ratchet growth, exemption, weaker assertion, decoder tolerance or workload/authorization edit is used.

The single full Release selection completed across 41 assemblies with 39,108 passed, zero failed and
1,527 skipped out of 40,635 results. The renderer assembly has 11,781 passed and 1,110 skipped.
The renderer baseline and shared timing subset has 539 passed and zero skipped. Both Release builds
completed with zero warnings and errors. Seven new integration regressions failed before correction,
then all 59 focused integration cases passed with zero skips. TRX result elements and counters agree,
and no result belongs to the excluded outside-solution performance assembly.

Formatting verification required zero changes across 9,016 files. Whole-tree guards additionally
check dashes, prose, source size, contributor instruction size, documentation versions, whitespace
and the hosted workflow. All completed with exit 0. The size guard retains only the existing
Scene3D shrink advisory. These checks establish consistency only, without supplying native acceptance.

## Outstanding native and preparation gates

Task10 adds D3D11 optional cold compiler/memory contracts and separate explicit invocation blocks.
Its headless evidence still requires root's independent review and later native proof. Task11 remains
held, and Metal does not implement these diagnostic interfaces. Vulkan also implements both contracts.
The timed entrypoint rejects captured diagnostic pipelines, and the driver currently constructs
reports with an empty pipeline array. There is no separate
cold compiler-collection session in this entrypoint. A later preparation task must supply or explicitly
scope that diagnostic stage before claiming compiler coverage. Native committed/resident/budget fields
and achieved occupancy remain unsupported where unavailable. Process observations cannot replace them.

Root must independently review the reconciled dependency diff and source identities, then integrate and
push accepted implementation before preparing precise pilot inputs. Each pilot needs its own approval
and sole-manifest authorization commit with exact workload/workflow/manifest/shader hashes and runtime
identity. This record supplies no executable manifest or workflow dispatch authorization.

Later native evidence must verify selected T4 vendor/device/driver, all five scenes and required rows,
actual counts, valid outer timestamps and End tails, completion pacing, clock telemetry, perturbation,
compiler/control retention, output readbacks, model completeness, final sidecar availability and artifact
sizes under the unchanged 256 MiB cap. Confirm two-stage publication completion/stamp and failed-final
recovery on the native sink. Actual billed minutes and full-unit budgets come from those runs, not from
synthetic fixtures. Full units need fresh approval after independent pilot review.

The existing [partial-write accounting gap #1547](https://github.com/APKiwiOrg/KhaozEngine/issues/1547)
remains open. Sidecar preparation is accounted, but a failed measurement's partial write can occur after
its accounting snapshot. Headless success does not close or waive this limit. No optimization, consumer
adoption, private content, Windows Vulkan expansion, release or other active lane is part of this proof.
