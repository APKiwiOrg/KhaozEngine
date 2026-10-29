# Renderer performance authorization

This directory ships no executable authorization. Implementation approval grants no native run, including a free rehearsal.

After the owner approves one exact unit, commit its UTF-8 manifest as
`<unit>.json` in an authorization commit B whose only parent is workload A. B adds only that file.
The schema is `khaozengine.renderer-perf-unit/1`. Hash the exact bytes with SHA-256.
Dispatch the trusted `gpu-hosted-bench.yml` workflow with B's immutable SHA as `ref`, one named
leg, the default diagnostic filter and the three `renderer-perf-*` inputs. Only attempt one is
admissible. A failure requires scoped correction and fresh approval, never an automatic rerun.

The isolated project stays outside the solution. Its only fact is
`Renderer.Perf.Tests.RendererPerfTests.Measure_authorized_unit`, category `RendererPerformance`.
`KE_RENDERER_PERF_UNIT` and `KE_RENDERER_PERF_REPORTS` are explicit. Discovery never opens a GPU.
The entrypoint validates the absolute `GITHUB_WORKSPACE` as the Git top level and resolves the
manifest and authorization Git facts there, including from a nested VSTest host directory. Frozen
recipe source paths must belong to that checkout before startup. No working-directory change,
output-directory manifest copy or alternate-checkout search is used.
The workflow checks the authorization diff and exact bytes before selecting a hosted runner and
again on that runner. The entrypoint independently validates the strict manifest and runtime before
native creation. Performance presets require hosted NVIDIA T4 hardware. The
[renderer reference](../../../docs/RENDERER-PERFORMANCE.md#software-correctness-rehearsal)
owns the supported software rehearsal legs and schedules, including `rehearsal-controls`, separate
approvals, limits and correctness verdict. The same reference's
[paid authorization contract](../../../docs/RENDERER-PERFORMANCE.md#evidence-and-authorization) owns
per-leg job caps, cleanup billing allowance and the campaign envelope. Every execution class requires
usable timestamps, complete compiler evidence and certified publication. Partial artifacts survive failures.

The trusted free `legs` job admits paid `water-optics-v2` only on `linux-vulkan` or `windows-d3d11`,
with the full T4 job caps of 35 and 50 minutes. It checks every paid campaign maximum before a paid
runner is allocated. The cap must cover `budget.maximumCostUsd` and stay at or below USD 7.382 for
v2, or USD 22 for v1 `pilot` and `full`. The
[water optics contract](../../../docs/RENDERER-PERFORMANCE.md#water-optics-campaign) owns the schedules.
`rehearsal-water-optics-v2` selects only Linux lavapipe on `ubuntu-24.04`, while
`rehearsal-water-optics-v2-single` selects only Windows D3D11 WARP on `windows-2025-vs2026`. Both retain
19,800 unit seconds and 350 job minutes, with zero unit cost and zero campaign maximum. Opposite
legs and Windows Vulkan refuse these v2 rehearsals.

Every manifest must explicitly include `execution.lockedGraphicsClockMHz`. Paid T4 presets require
an integer matching the fixed [graphics-clock policy](../../../docs/RENDERER-PERFORMANCE.md#drift-perturbation-and-validity).
Every software rehearsal class requires explicit JSON null. Missing, duplicate, string, fractional, overflowing
or cross-class values refuse. The trusted selector carries this admitted value to the runner matrix.
There is no dispatch clock override. Older authorizations are never edited to add the field, and
another run needs fresh exact owner approval.

Every manifest must explicitly include `limits.coldFrameMilliseconds` as well as
`limits.frameMilliseconds`. The cold limit is 120,000 ms for every preset and execution class. Only
conditioning frame 0 uses it for immediate and retained CPU timing and retained GPU duration. The
renderer reference owns the fixed limits and rationale. The Windows T4 cold frame took 44.9 seconds,
and the valid Linux T4 pilot took 9.42 seconds against its old 10-second limit. A thin margin turns
ordinary hosted-runner CPU variance into a paid refusal. The 120-second bound is about 2.7 times the
worst observation and remains inside the 600-second and 1,500-second paid unit deadlines. It stays
counted inside the original unit and job caps. Later conditioning, warm-up, measured and control frames
retain the steady limit. An older manifest missing the cold field refuses strict parsing. Existing
authorizations are never rewritten to supply it, and another run requires fresh exact owner approval.

The shader identity hashes an ordered map of exact literal and generated readonly Render3D and
SpriteBatch GLSL source strings, specialized outdoors FFT kernels and control shader sources.
Recipe and control definitions, frozen pass layouts and compiler
corpus hashes accompany the report. Native admission records a cold timestamp capability probe,
with no scene frames. It never contributes a steady-state sample or a throughput reference.

Task 15 owns independent integration review, the precise pilot approval, driver and native compiler
retention checks, output validation, clock and marker perturbation review, artifact-size checks and
actual billed minutes. Each full unit needs fresh approval after pilot review and the separately
approved all-controls software checks described in the renderer reference. Windows Vulkan,
profilers, extra variants, release and consumer adoption remain separate owner decisions.
