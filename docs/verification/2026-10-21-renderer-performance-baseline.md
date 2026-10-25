# Renderer baseline implementation verification

This record covers Task15b reconciliation, the subsequent correction batch, headless implementation
correctness and the first authorized pilot's early failure. It records no valid native baseline,
hardware performance, clock stability, floor, promotion, release or consumer acceptance. The failed attempt's evidence size and
observed runner charge are recorded below. The [living evidence contract](../RENDERER-PERFORMANCE.md)
and [hosted admission contract](../CROSS-PLATFORM.md#paid-hosted-renderer-performance-mode) own those rules.

## Source and dependency boundary

The accepted implementation input is `ce8f50785f12d320781041ad7933980d30781f81`, including the
accepted cold publication amendment and its final r3 correction. Local main and origin/main both name
`cc31c09f37a74332b716ec81b29265a755bcbb04` at reconciliation. The merge base is
`989eba495ae7c0f9ff15973889481e3db91fd27a`, with 65 unique implementation commits and 50 main commits.
The actual merge retains these implementation and main inputs as its ordered parents. The incoming
main range is `989eba495ae7c0f9ff15973889481e3db91fd27a..cc31c09f37a74332b716ec81b29265a755bcbb04`.
Generic timing infrastructure is already shared. No active equipment, filtered-exact, coverage-DQ or
consumer lane is an additional merge parent. Earlier Task15a proofs remain historical in its ledger.

Main's 21.13.0 is released. Annotated `v21.13.0` has object
`5d92bba5c42a07723f1f7307bb479ff1cd8ed97d` and resolves to the main input above. No newer staged
version or 21.14.0 tag exists at selection. The additive package-bearing batch stages 21.14.0 and moves
its unreleased entries there. Main's released history from 21.13.0 onward is preserved byte for byte.
This is neither release authorization nor a package/feed proof.

The two content conflicts retain both sets of newline attributes and the released changelog. Semantic
reconciliation also retains main's deferred Metal drawable acquisition, renderer-owned swapchain option,
1536-byte model uniform layout and composed directional-indirect shaders, foliage point-caster index,
and owned-thread D3D11 timestamp contention correction. The harness calls the legacy FramePhases
overload, retaining its initial bind and clear. Main's opt-in directional lighting remains outside the
fixed workload. Its default uniform branch adds no new render pass or command. Existing atmosphere,
opaque upload and point-shadow scopes own the changed CPU/command work. Actual resource bytes and
shader/native identities still include the new upstream implementation.

Five frozen-recipe checks exposed the two new settings fields changing the v1 serialized settings.
The harness now explicitly selects uniform indirect lighting and zero irradiance coefficients. Its
recipe-only JSON contract refuses any other value before preserving the original v1 encoding for
these two post-v1 fields. All other settings remain serialized. The original recipe files and every
platform-family derived digest stay unchanged. New refusal tests exercise actual Freeze with
directional, unknown-mode and nonzero-coefficient settings. This adds no recipe or authorization
migration. Source identity still follows the actual composed shaders, including the indirect-light tail.

Both warmed and cold completeness paths, water uploads, continuous effect time, both clipmap snaps,
outer timestamps, completed submissions and final publication recovery keep their accepted owners.
The immutable compiler inventory, effective settings and bounded Request/Counts contracts are unchanged.

These SHA-256 values identify reconciled source bytes, separately from runtime shader/artifact hashes.

| Reconciled source | SHA-256 |
| --- | --- |
| `KhaozEngine.Render2D/Internal/Render2DCore.cs` | `b1113394908fab62cb678fe5dae6a823baf083fe9f6dbac2b414d689bd80ef40` |
| `KhaozEngine.Windowing/FramePhases.cs` | `64f38d8399c96e37cace0a10ac2c5d11e68dcfd346beb400a4701e20791aeb6c` |
| `KhaozEngine.Gpu.Metal/Internal/MetalPresentBoundary.cs` | `8564170e18b00112fff9da5c6e6301e9a79475370f8d29f9b88d7bc2cda74ccf` |
| `KhaozEngine.Render3D/Scene3D.Atmosphere.cs` | `db494ee7022c72d510bc5a5a22d027001e62e24cf20047b7609573819e226a5d` |
| `KhaozEngine.Render3D/Scene3D.FoliagePointShadows.cs` | `1ac5853b731e5db828814a609aa77e4b1a936458a4a16465bbf63e7dfe91ee56` |
| `KhaozEngine.Render3D/Rendering/ModelRenderer.FrameUbo.cs` | `488b943da8c794a355a9dd5cb7d668e15a61207b65912938cfe77e7b7d4ccd7b` |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfRecipe.cs` | `95d5b70de940b05ae1e048f7f81313915b75181714f0a4a6499317f29e7c4d4e` |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfRecipeSettings.cs` | `50d1024e40cd1dca18895cf72ec67d23fd2439c6042d0ba1bea84058da61f6de` |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfExecutable.cs` | `880ec9b7b8964cf709bddce56f9e2edf8b15209fffa1e6c9a0cc58ed9d9edbf5` |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfPublication.Typed.cs` | `45c1592795ef82a95913abe0afaa6689a9745e94d582002c7abe3c2eac0d9246` |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfArtifactVerification.cs` | `fda57f05c421cc1bd1e417bb977a310b7910e3ff365147c65b3e7fae868b5f5e` |

## Coverage inventory

Harness types below live under `KhaozEngine.Render.Tests/Gpu/RendererPerf` unless explicitly named
otherwise. Shared timing types live under its sibling `Performance` directory.
The complete public pass registry remains in the [consumer reference](../USING-KHAOZENGINE.md#gpu-timestamps-without-waiting).
The inventory here maps concerns to executable checks rather than duplicating that registry.

| Concern | Implementation and named correctness checks |
| --- | --- |
| S0 empty/1, no scene content | `RendererPerf/RendererPerfRecipe.cs`, `RendererPerfRecipeTests.Fixed_topology_counts_and_seed_have_stable_digest`, `RendererPerfEntrypointTests.Frozen_layout_records_cold_and_cached_routes_without_native_discovery` (S0) |
| S1 village/1, 64 terrain chunks, 144 building components, 1024 props | `RendererPerfVillageScene.cs`, same topology and frozen-layout checks (S1), `RendererPerfRecipeIdentityTests` authored/family refusal cases |
| S2 outdoors/1, 16 chunks, 64 foliage patches/4096 instances, two clipmap water planes, two 256-point FFT cascades | `RendererPerfOutdoorsScene.cs`, `RendererPerfEffectsRecipeTests.Foliage_water_and_fft_routes_match_fixed_counts`, `Every_measured_ocean_frame_has_no_prime_submission_or_wait`, `Frozen_clipmap_schedule_names_both_events_and_the_affected_planes`, frozen-layout case S2 |
| S3 crowd/1, 16 chunks, 128 props, 32 GPU actors, two shadowed point lights | `RendererPerfCrowdScene.cs`, `RendererPerfRecipeTests.Crowd_uses_32_visible_gpu_actors_and_declared_shadow_rows`, frozen-layout case S3 including unavailable transient rows |
| S4 effects-ui/1, 16 chunks, 64 props, four GPU actors, 32 decals, 256 particles, 64 distortions/billboards, eight beams/trails/overlays, four silhouettes/outlines, 1024 sprite plus 2048 glyph quads over four textures/16 clips | `RendererPerfEffectsScene.cs`, `RendererPerfUiScene.cs`, `RendererPerfEffectsRecipeTests.Ui_quads_textures_and_clips_are_fixed`, frozen-layout case S4 |
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
| Actual capture and warm allocation | `RendererPerfConstructionTests.Mutable_inputs_and_disposed_originals_do_not_change_replay_values`, `Lazy_temporal_and_specialized_ocean_are_actual_factory_inputs`, `Warmed_bind_observer_has_no_growth`, `Measured_first_creation_keeps_frame_and_refuses_promotion` |
| Strict cold identity, settings and lifecycle | `RendererPerfCompilerProtocolTests`, `RendererPerfDiagnosticConfigurationTests`, `RendererPerfColdLifecycleTests`, `RendererPerfConstructionTests` correspondence cases. Actual backend witnesses retain stage digests and typed statistics. One budget owns production/replay cleanup and publication |
| Registered files and finite exclusion | `RendererPerfCompilerPublicationTests` full, content, identity, exclusion, process and return cases. Actual producer registry, bounded account/rehash, no-follow native identities, compatible-writer exclusion before first read through final validation, unsupported-filesystem/protocol refusal and terminal writer correction are retained |
| New main APIs | `IndirectFrameTailTests`, `AtmosphereSurfaceLightingTests`, `FoliagePointIndexedFrameTests`, `D3D11TimestampTests.ContendedImmediateContextReturnsPendingWithoutPolling`, `FencePollExceptionReleasesTheGateForAnotherThread`, `MetalDeferredDrawableTests`, `RendererSwapchainOptionsTests`. Renderer boundary and all-feature ownership tests remain independent |
| Ordinary offscreen refusal and window teardown | `NestedRecordingSiteTests` retains exact named refusal and target cleanup. `AppWindowDisposalTests` retains step order and the original staged Error through the accepted transport. Neither changes baseline frame scheduling |
| Frozen indirect mode | `RendererPerfRecipeIdentityTests.Frozen_recipe_matches_the_running_platform_family` (all five), `RendererPerfRecipeSettingsTests.Frozen_construction_refuses_new_indirect_lighting_work` |
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

This section retains Task15b's original local result. The later hosted run and correction-batch
verification are separate evidence below.

Run from the reconciled worktree root. The shared setup builds the outside-solution verifier required
by ordinary process tests. The measurement project is built separately and its fact is never executed.
The full ordinary suite removes both opt-in variables and retains per-assembly TRX in scratch.

```sh
build-slot --heavy --label renderer-task15b-build -- sh scripts/build-release-tests.sh KhaozEngine.slnx
build-slot --label renderer-task15b-tool-build -- dotnet build tools/Renderer.Perf.Tests/Renderer.Perf.Tests.csproj -c Release
build-slot --heavy --label renderer-task15b-tests -- env -u KE_GPU_TESTS -u KE_RENDERER_PERF_UNIT dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket' --logger trx --results-directory /tmp/khaozengine-renderer-perf-task15b/trx
build-slot --heavy --label renderer-task15b-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Also verify outside-solution tool formatting, whole-tree dashes/prose/size/contributor instructions,
doc versions, whitespace, the shared setup/selective CI orchestration and changed workflow contracts
through build-slot. No ratchet increase, exemption, suppressed diagnostic or workload change is used.
The solution and verifier builds and the separate measurement-tool build completed with zero warnings
and errors. The 94-case focused reconciliation selection passed without skips. The single ordinary full
run returned exit 1 with 39,602 Passed, 1,530 NotExecuted and ten Failed across 41 assemblies. Its
renderer assembly had 12,192 Passed, 1,113 NotExecuted and ten Failed. This is a retained red full run.

Four Metal cases assumed eager acquisition before main's current-frame bind. They now assert no
pre-bind allocation/acquisition and drive the actual first/repeated bind before checking unchanged
orphan byte/count/peak, native identity, release order and dead-device policies. Four window disposal
cases now require the accepted transport's single staged event, exact original Error reference and
original stack, retaining the existing teardown order. Two nested-capture cases exposed a real defect.
2D capture created a SpriteBatch before its recording claim, then safe teardown masked refusal while
the caller's frame remained open. Both capture methods now construct the batch after the real claim
succeeds. Refused claims preserve the named original exception and release target/framebuffer/list
without constructing a batch. The safe drain guard and failure transport remain intact.

The affected correction selection passed 155 cases, zero failed/skipped. It covers all ten prior
failures, cold lifecycle/cleanup, frame submission and sprite ordering/scissor cases. The unchanged
full suite was not repeated. Semantic corrections after its snapshot are exactly Render2DCore and the two
adapted test files. Two indirect-light wrapper tests later move from UboLayoutTests to their existing
IndirectFrameTailTests helper owner to meet formatting and the 800-line cap together. Both original
full-run Passed cases are discovered and pass under their new class. All eight owner cases pass. The earlier full results still establish the unchanged raw/compiler/file paths.
All three full-size fixtures pass there with 16,944 retained frames each. Combined bytes are
199,834,765 complete-small, 199,829,805 complete-Unsupported and 200,047,436 held-Invalid. Actual
files, FULL_PROOF rows, per-assembly TRX, source deltas and integer command/log/hash records remain
under `/tmp/khaozengine-renderer-perf-task15b`. These synthetic sizes are not native payload proof.

Final source builds repeat the shared solution/verifier setup and measurement-tool build after the
capture correction and test relocation. All build summaries have zero warnings and errors. No second
full test selection is run. Whole-solution formatting reports zero changes across 9,181 files. The
relocated files also pass focused formatting. The verifier and measurement tool report zero changes
across three and four files. The initial format refusal and size/version guard refusals are retained.

Whole-tree dash, prose, size and contributor-instruction checks pass. The size check retains the
pre-existing Scene3D advisory at 2,148 lines against a 2,228 baseline. No baseline or exemption changes.
Instruction bytes are 8,320 of 16,384. Version/catalog checks pass for 21.14.0, including the three
aligned README examples missed by the first edit. Shared full/selective setup fixtures, shell syntax,
renderer workflow actionlint, PowerShell syntax and whitespace checks pass. Source/merge/tag checks
confirm the unchanged timing, raw, compiler, file-verification and frozen-workload owners.

Ordinary ci.yml actionlint is raw exit 1 on both main and this branch, with five identical unknown
ubuntu-26.04 runner-label messages. Main lines 37/72/207/300/394 map to branch lines
37/72/208/301/395, all column 14. The accepted shared setup fixture adds one CI line. No diagnostic is
suppressed and neither raw run is called green. The separate renderer workflow check exits zero.

Task15a's 39,108 passes are historical and are not current reconciliation evidence.

## First authorized Linux pilot, attempt 1

The integrated workload and trusted workflow are `d092cc35e487ac5d684d09a44920ca3cd2a201f0`.
Authorization `a58e0d4153030f02c4af68cbfc9ea7aece5e1939` has that workload as its sole parent and adds
only `tools/Renderer.Perf.Tests/authorized/pilot-linux.json`. Its exact manifest SHA-256 is
`0c1bd0a14eaa3c58959f10c8073e5f7bb998cf3434e8776c666661393541aada`.

The owner approved one Linux Vulkan T4 attempt with the fixed 1,260-frame, nine-operation pilot,
600-second unit limit, 12-minute job limit and USD 0.624 GPU runner component cap. The attempt was
consumed by [run 40133376096](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40133376096),
created on 2026-10-24 UTC. It was not retried. CPU and GPU authorization passed, and both Release
builds completed with zero warnings and errors. The one fixed measurement fact executed and failed,
with zero skipped facts and zero measured frames.

`RendererPerfEntrypoint.Run` read the manifest relative to the process working directory. VSTest
started its host in `tools/Renderer.Perf.Tests/bin/Release/net10.0`, causing the lookup to repeat
`tools/Renderer.Perf.Tests/authorized/pilot-linux.json` below that directory. The resulting
`DirectoryNotFoundException` occurred at line 27, before runtime admission, coldStart, source-work
validation or the entrypoint's device creation. The authorized manifest exists at the checkout root,
and the retained artifact matches its dispatched hash. [Issue #1567](https://github.com/APKiwiOrg/KhaozEngine/issues/1567)
tracks validated checkout-root resolution and an actual-entrypoint regression from a nested directory.
Copying another manifest into the output directory or changing the process-wide working directory is
not the intended correction.

Artifact `renderer-perf-linux-vulkan`, ID `12575394993`, is 9,562 compressed bytes. GitHub reports
archive SHA-256 `e008d07d5c787966ef05f276f998d3bc0cdb336719e6064e8abfbe69d092217f`.
Its six files are the exact manifest, authorization, both build logs and measurement log/TRX.
There is no runtime-admission file or final measurement report. The testhost banner confirms
.NET 10.0.13 but establishes no complete runtime environment or native correspondence.
The measurement TRX SHA-256 is `26762567e3dae766ae4d5979a69463c81fa2d6326e49bf683b296c0511d12fe7`.

The paid job ran from 21:16:51 to 21:20:23 UTC, 212 seconds. The GitHub billing usage API reported
four `actions_linux_4_core_gpu` minutes at USD 0.052 per minute, USD 0.208 net runner charges.
The repository's same-day GPU SKU was absent before the attempt, and the workflow history contains
only this GPU run on that date. This is observed provider usage, separate from a duration-based
estimate. Storage, tax and currency conversion are outside the runner component. The attempt produced
no performance, compiler, payload, deadline or baseline acceptance. Another attempt requires a corrected
workload, independently reviewed preparation and fresh owner approval.

The automatic main CI run [40101482323](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40101482323)
passed 39,612 tests with zero failures and 1,530 skips across 41 normal assembly summaries. Its two
six-case Foundation determinism checks are separate. This hosted result does not rewrite the earlier
local failed run. The Metal push job in [40101482316](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40101482316)
failed 52 process cases from a missing verifier build prerequisite, tracked by
[#1565](https://github.com/APKiwiOrg/KhaozEngine/issues/1565), and one managed staging allocation
case, tracked by [#1566](https://github.com/APKiwiOrg/KhaozEngine/issues/1566). The latter has a
Metal-specific lease owner absent from Vulkan. Both have accepted source corrections in the batch
below and remain open until integration. They are separate from this pilot's entrypoint failure.
No native gate is waived.

## Correction batch preparation

The correction candidate retains failed-pilot proof `62cf4b977b5bb72590130438a8bfcc34acf23afb`
and merges these exact independently accepted tips in order, without conflicts:

| Correction | Accepted tip | Preserved contract |
| --- | --- | --- |
| Native CI prerequisite, #1565 | `36103e6165e6c174fcbcb7c49db396cd47eebed6` | Native matrix, sync and reduced shards share Release setup. Canonical selection uses actual file identity, other solutions inspect resolved roots, unrelated shards retain their slice, and Release propagates through shard references |
| Metal staging accounting, #1566 | `1a7ad6e685cec763f2a09a35b40c3657d326a0cb` | Warmed accounting stores capacity inline by native handle. Resource leases, native retention limits, release order and device-lifetime policy remain unchanged |
| Entrypoint checkout root, #1567 | `287a26ad86209ef7834beff79961e26ff5646bea` | The explicit absolute checkout owns manifest reads, authorization Git facts and source-file recipes. Runtime equality and every authorization/native gate remain required |

The three ordered merge commits are `e50cb197b7227fb94908f4706664eb6bd60f9c8b`,
`36b003b257f47d4c029e1e1243ceaea159adfa51` and
`68b08598bc379c37ca0bcf4b147ef2976598af64`. Accepted code, scripts and workflow files match their tips byte for byte.
Main and fetched origin/main remain `d092cc35e487ac5d684d09a44920ca3cd2a201f0` at preparation.
Version 21.14.0 remains staged above latest tag v21.13.0, so the package-bearing Metal fix rides it.
No version bump, released-history rewrite or active equipment, DQ, filtered or consumer lane is added.
Authorization commit `a58e0d4153030f02c4af68cbfc9ea7aece5e1939` is not a merge ancestor.
Its manifest and workload d092 remain untouched. The consumed pilot grants no retry authority.

The single correction-batch ordinary full selection exits 0 across 41 assembly TRX files, with
39,630 Passed, zero Failed and 1,530 NotExecuted result elements. Render.Tests has 12,220 Passed,
zero Failed and 1,113 NotExecuted. The run explicitly unsets `KE_GPU_TESTS` and `KE_RENDERER_PERF_UNIT`,
uses `Category!=LiveSocket` and never includes the outside-solution performance fact. All eight new
Metal cases and ten new actual-entrypoint cases pass in that full run. The accepted 71-case Metal
selection and 49 existing related entrypoint cases also pass there without skips. No focused unit
selection or unchanged full-suite rerun is used to replace the result.

Shared Release setup builds both the solution and outside verifier with zero warnings and errors.
The isolated measurement project also builds with zero warnings and errors, without executing its
fact. Per-assembly TRX, every named outcome and skip, the actual full log, original command vectors,
source and binary identities, compact full-fixture proofs and log hashes are retained under
`/tmp/khaozengine-renderer-corrections-integration`. Full-fixture raw payloads are removed only after
their checked counts, sizes and file hashes are retained. Formatting and guard outcomes are recorded
with the candidate in the Task15g integration report.

Full-solution and both isolated-project format checks exit 0 without changes. Whole-tree dash, prose,
file-size, contributor-instruction and doc-version guards pass, as do shell syntax, Shellcheck and
whitespace checks. No baseline growth, exemption or suppression is added.

The setup fixture first exits 1 in this long-lived worktree because the native shard guard finds
171 existing Debug directories, including paths dated before this task. Their 42 files and directories
remain untouched. The identical fixture then exits 0 in a fresh bounded source copy, matching the
wrapper's documented fresh-CI condition. Its script, workflow, solution and project inputs are hashed
against this candidate. No real build or test is performed by that command-stub fixture. The original
refusal remains a separate result, not a green worktree-fixture claim.

Task15g R-1 now makes the fixture supply its own disposable source layout from tracked working-tree
scripts, workflows, solutions and projects before installing command stubs. Contributors can run it
in an existing checkout. Its once-only corrected worktree run exits 0 with all 17 route/status messages,
including the new real-wrapper refusal of a sandbox Debug directory. The worktree retains the same
171 Debug directory paths before and after. The production guard and every existing route assertion
are unchanged. Evidence is retained under `/tmp/khaozengine-renderer-corrections-integration/fix-r1`.
Native Windows aliases remain explicitly unexecuted on macOS.

Raw native-workflow actionlint exits 1 on both current main and this candidate. Both retain the same
three findings at the same locations, two unknown ubuntu-26.04 labels and existing SC2012. The corrected
comparison exits 0 without suppressing either raw result. Its first comparison attempt used a greedy
location parser that consumed SC2012's embedded coordinates and failed. That failed audit and the
corrected parser are both retained. No workflow run is dispatched.

This is preparation for root's independent evidence audit and fresh integration review. Native
Windows file identity, native Metal allocation behavior, real runtime admission, compiler retention,
measurement and paid execution remain unproved here. The earlier local red full suite, later hosted
39,612-pass suite and failed paid pilot retain their separate results above. No manual validation is
needed for this preparation. Root owns main integration, push and the post-main pack.

## Outstanding native and preparation gates

Accepted Tasks10/11 and the cold amendment implement optional D3D11, Vulkan and Metal diagnostics.
Metal retains truthful source/configuration, cold counter/compute-limit and unified-memory provenance.
It supplies no native compiler artifact, physical registers, spills, achieved occupancy or residency
through this path. Shared typed identities do not turn Metal source into a primary artifact.
The timed entrypoint supplies compiler metadata and invocations off through immutable instance
settings. Actual construction is captured without changing native returns. After both device lifecycles,
the typed publication path verifies registered inventory and noncompiler files before completion.
Report/2 retains exact typed scalar and stage evidence, bounded read requests and actual byte commits.
The root and combined root/compiler cap remain 256 MiB, depth 32 and two 32-byte accounting slots.
The workflow and offline directory reader call the same managed verifier. Parse alone never certifies.

These are headless implementation boundaries. Native committed/resident/budget fields and achieved
occupancy remain unsupported where unavailable. Process observations cannot replace them. Actual
primary-backend correspondence, compiler payload admission and cold-stage cost still need the native
gates below. No synthetic fixture closes the original consumer or armour acceptance.

The reconciled source was independently reviewed, integrated and pushed before the first pilot.
Corrections still require review and verification before preparing another exact workload. Each pilot
needs its own approval and sole-manifest authorization commit with exact
workload/workflow/manifest/shader hashes and runtime identity. This record grants no further dispatch.

Later native evidence must verify selected T4 vendor/device/driver, all five scenes and required rows,
actual counts, valid outer timestamps and End tails, completion pacing, clock telemetry, perturbation,
compiler/control retention, output readbacks, model completeness, final sidecar availability and artifact
sizes under the unchanged 256 MiB cap. Confirm two-stage publication completion/stamp and failed-final
recovery on the native sink. Actual billed minutes and full-unit budgets come from those runs, not from
synthetic fixtures. Full units need fresh approval after independent pilot review.

The existing [partial-write accounting gap #1547](https://github.com/APKiwiOrg/KhaozEngine/issues/1547)
remains open. #1546 closed after main integration. #1530 remains separate D1 portable-math work.
Sidecar preparation is accounted, but a failed measurement's partial write can occur after
its accounting snapshot. Headless success does not close or waive this limit. No optimization, consumer
adoption, private content, Windows Vulkan expansion, release or other active lane is part of this proof.


Cold publication correction uses the shared ordinary Release setup in
[contributor rules](../CONTRIBUTOR-RULES.md#build-and-ci), including its outside-solution verifier
prerequisite. The living [directory verification contract](../RENDERER-PERFORMANCE.md) owns
platform file IDs, finite compatible-writer exclusion, final rehash and terminal publication failure ownership. macOS filesystem
API tests are headless implementation evidence. Windows/Linux paths require their own execution
proof. No native GPU, native deadline/cost, billed-minute or armour gate is closed by these tests.
