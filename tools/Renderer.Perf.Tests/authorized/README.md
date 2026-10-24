# Renderer performance authorization

This directory ships no executable authorization. Implementation approval grants no paid run.

After the owner approves one exact unit, commit its UTF-8 manifest as
`<unit>.json` in an authorization commit B whose only parent is workload A. B adds only that file.
The schema is `khaozengine.renderer-perf-unit/1`. Hash the exact bytes with SHA-256.
Dispatch the trusted `gpu-hosted-bench.yml` workflow with B's immutable SHA as `ref`, one named
leg, the default diagnostic filter and the three `renderer-perf-*` inputs. Only attempt one is
admissible. A failure requires scoped correction and fresh approval, never an automatic rerun.

The isolated project stays outside the solution. Its only fact is
`Renderer.Perf.Tests.RendererPerfTests.Measure_authorized_unit`, category `RendererPerformance`.
`KE_RENDERER_PERF_UNIT` and `KE_RENDERER_PERF_REPORTS` are explicit. Discovery never opens a GPU.
The workflow checks the authorization diff and exact bytes before renting a GPU runner and again
on that runner. The entrypoint independently validates the strict manifest and runtime before
native creation. Hosted NVIDIA T4 hardware, usable timestamps and a completed matching report are
required. Partial artifacts survive failures.

The shader identity hashes an ordered map of exact literal and generated readonly Render3D and
SpriteBatch GLSL source strings, specialized outdoors FFT kernels and control shader sources.
Recipe and control definitions, frozen pass layouts and compiler
corpus hashes accompany the report. Native admission records a cold timestamp capability probe,
with no scene frames. It never contributes a steady-state sample or a throughput reference.

Task 15 owns independent integration review, the precise pilot approval, driver and native compiler
retention checks, output validation, clock and marker perturbation review, artifact-size checks and
actual billed minutes. Each full unit needs fresh approval after pilot review. Windows Vulkan,
profilers, extra variants, release and consumer adoption remain separate owner decisions.
