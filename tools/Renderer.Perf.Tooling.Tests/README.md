# Renderer performance tooling tests

This non-packable project owns the renderer measurement implementation, offline analysis and their
contract tests. It stays outside `KhaozEngine.slnx` and no engine project references it. Declared
`KhaozEngine.Tests.*` namespaces and embedded control resource names are preserved. The separately
authorized one-fact project remains `tools/Renderer.Perf.Tests`.

Run the complete headless suite from the repository root with `KE_GPU_TESTS` and
`KE_RENDERER_PERF_UNIT` unset. Create `local-feed` before restoring.

```sh
mkdir -p local-feed
sh scripts/build-release-tests.sh tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj
dotnet test tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj -c Release --no-build
```

The setup also builds Renderer.Perf.Verify for verifier success and refusal process tests. Engine
solution builds and Render.Tests shards have no such prerequisite. AllocationMeasurement.props
imports its allocation-isolation contract tests in addition to the moved renderer tests.

## References and shared sources

Direct engine references are Diagnostics, Gpu, Gpu.D3D11, Gpu.Metal, Gpu.Vulkan, Primitives, Render2D,
Render3D, Windowing and TestSupport.Gpu. The seven internal-access grants are Gpu, Gpu.D3D11,
Gpu.Metal, Gpu.Vulkan, Render2D, Render3D and Windowing. Windowing is required because
RendererPerfNativeSession uses Frame's internal setters, BeginSubmission, DisposeSubmission,
HasSubmission and FramePhases.Run. These remain internal and keep the production host phase path.
The tooling assembly grants its internal implementation access only to Renderer.Perf.Verify and
Renderer.Perf.Tests.

[RendererPerfShared.props](../../test-shared/RendererPerfShared.props) is the explicit 53-file helper
source-link inventory. Sources retain canonical physical paths and existing ratchet allowances.
The engine test assembly remains the owner of these helpers. UI recipes also copy its unchanged
`Assets/Roboto-Regular.ttf` into the tooling output, retaining the authored font identity. The free
workflow watches that asset explicitly. Source links import no native provider
module initializers. The offline verifier therefore loads no providers, while the authorized
entrypoint registers its chosen backend only after admission. NativeDeviceLifecycle,
GpuBackendProvidersSerial and AllocSensitive have assembly-local nonparallel definitions.

## Free validation and paid admission

`tooling-validation.yml` watches this project, both executable consumers, renderer scripts, exact
shared helper paths, allocation support, build inputs and the living contract docs. Push and PR runs
use an explicit diff selector. Manual and reusable calls select this suite explicitly. The free
Ubuntu lavapipe, Windows D3D11 WARP and macOS Metal matrix first runs every test headlessly, then
requires `RendererPerfWaterOpticsRecipeTests.Fft_surface_stays_within_its_bound` to pass with zero skips.
No authorized measurement fact runs in this workflow.

After the existing free admission job resolves authorization B, `gpu-hosted-bench.yml` calls this
validation with both B and the trusted workflow SHA. Equal refs are tested once. `renderer-bench`
requires successful admission and tooling validation, so failed, skipped or cancelled validation
prevents matrix allocation. The reusable workflow also refuses completion if its required renderer matrix was skipped, failed or
cancelled, rather than accepting a successful selector alone. The existing first-attempt and non-empty-unit conditions remain.
The generic bench mode retains its original dependency on admission alone.

Passing this suite validates measurement-system correctness and source identity. It proves no
hardware throughput, performance baseline or paid evidence, and grants no campaign permission.
Old receipts and manifest hashes remain immutable. A new campaign records the changed assembly
identity and follows the [authorization contract](../Renderer.Perf.Tests/authorized/README.md).
