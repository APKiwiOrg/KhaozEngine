# Cross-platform desktop bring-up — verification infra + CI (5.32.0)

Audit milestone 4, desktop scope (Windows D3D11, Linux Vulkan; macOS Metal already works). The `KhaozEngine.Gpu`
seam + `GpuBackendSelector` (probe + `KE_GRAPHICS_BACKEND` override) already exist; D3D11 + Vulkan both honor
Veldrid's clip-space-normalization options like Metal (OpenGL — the troublesome one — is OUT of scope). So this
release is the **verification mechanism**, not a rendering rewrite: make the golden-snapshot net per-backend, and
author a CI matrix that runs it on real Windows/Linux runners with software rasterizers. What CI surfaces as
different gets fixed as a follow-up.

## Part A — backend-aware golden net (`KhaozEngine.Tests/Gpu`)
- `GoldenCompare.GoldenPath(name)` resolves `goldens/<name>.<backend>.txt` where `<backend>` =
  `KhaozEngine.Gpu.GpuBackendSelector.Select().ToString().ToLowerInvariant()` (metal / vulkan / direct3d11 /
  opengl). (GoldenCompare already references KhaozEngine.Gpu via the test project.) So each backend has its own
  reference grid (software-rasterizer output won't match Metal pixel-for-pixel; per-backend goldens handle that).
- **Rename** the existing `goldens/scene2d.txt` → `scene2d.metal.txt`, `scene3d.txt` → `scene3d.metal.txt`
  (they were baked on Metal). The Metal leg verifies against these.
- `AssertOrUpdate` unchanged otherwise (`KE_UPDATE_GOLDENS=1` writes the per-backend file; else compares; a
  missing per-backend golden fails with a clear "bake it" message — that's the first-run signal for a new
  backend).
- Keep the `GpuFact` gate (`KE_GPU_TESTS=1`).

## Part B — CI workflow `.github/workflows/cross-platform-gpu.yml`
A matrix that runs the golden tests on each desktop OS with its backend. Triggers: `push`/`pull_request` on
main (verify) + `workflow_dispatch` with a `bake` boolean input (re-bake + upload goldens as artifacts).
```
strategy.matrix.include:
  - os: macos-14      backend: metal       # arm64 runner -> real Metal; verifies committed .metal goldens
  - os: windows-latest backend: direct3d11 # D3D11 (WARP software fallback if no GPU)
  - os: ubuntu-latest  backend: vulkan      # Mesa lavapipe (software Vulkan)
```
Each job:
- checkout, `actions/setup-dotnet@v4` (10.0.x), `mkdir -p local-feed`, `dotnet restore`, `dotnet build -c Release`.
- **Linux only**: `sudo apt-get update && sudo apt-get install -y mesa-vulkan-drivers vulkan-tools libsdl2-2.0-0`
  and set `VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.x86_64.json` (lavapipe). (`vulkaninfo` step for
  diagnostics, `continue-on-error`.)
- env: `KE_GPU_TESTS=1`, `KE_GRAPHICS_BACKEND=${{ matrix.backend }}`, and `KE_UPDATE_GOLDENS=1` ONLY when the
  dispatch `bake` input is true.
- `dotnet test -c Release --no-build --filter FullyQualifiedName~Golden` (the GPU goldens for that backend).
- **When baking** (`bake` input): `actions/upload-artifact@v4` the `KhaozEngine.Tests/Gpu/goldens/*.${{ matrix.backend }}.txt`
  files (so you download + commit the Win/Linux goldens after the first bake run). When verifying and the
  backend golden is absent, the job fails with the "bake it" message — that's expected until the goldens are
  committed.
- Headless note: the golden path uses `Render3DSnapshot`/`Render2DSnapshot` -> `CreateHeadless` (offscreen, NO
  window), so SDL2 is NOT needed for these tests; only the Vulkan/D3D runtime + a (software) rasterizer.

Leave the existing `.github/workflows/ci.yml` (ubuntu build/test/pack/publish, GPU tests skipped) untouched —
it's the fast inner CI; the new workflow is the GPU matrix.

## Part C — confirm backend selection + docs
- Confirm `GpuBackendSelector` honors `KE_GRAPHICS_BACKEND` (metal/vulkan/d3d11/gl) and that `CreateHeadless`
  maps Vulkan/Direct3D11 (it does — `GraphicsDevice.CreateVulkan`/`CreateD3D11`). No code change expected;
  add a headless test that `Select("vulkan", <any os>)==Vulkan` etc. if not already covered.
- New `docs/CROSS-PLATFORM.md`: the platform→backend table (desktop scope), the CI matrix + software
  rasterizers (lavapipe / D3D11-WARP), the per-backend golden flow (verify on push; `workflow_dispatch bake=true`
  to generate + commit a new backend's goldens), and the REMAINING productization gaps explicitly:
  (1) windowed-app distribution needs SDL2 + libveldrid-spirv bundled per-RID (the headless tests don't need it,
  but a shipped game does); (2) OpenGL backend + the clip-Y/depth runtime derivation deferred (D3D11/Vulkan
  honor the prefer-flags); (3) mobile (Android/iOS) is a separate project (native windowing/lifecycle + AOT).

## Files / Release
- Modify `KhaozEngine.Tests/Gpu/GoldenCompare.cs`; rename the two golden files to `*.metal.txt`.
- New `.github/workflows/cross-platform-gpu.yml`, `docs/CROSS-PLATFORM.md`.
- (Maybe) a `GpuBackendSelector` test for the override.
- Bump 5.31.0 → 5.32.0, CHANGELOG, pack 8 pkgs.

## Verification (what's checkable HERE on Metal)
- Default `dotnet test` green (goldens skipped).
- **`KE_GPU_TESTS=1 dotnet test --filter Golden`** on this Mac: resolves `scene*.metal.txt`, PASSES (proves the
  rename + backend-aware path works on the verifiable backend). Report it.
- `dotnet build` clean. The Windows/Linux legs are authored but verified by the USER pushing to CI (no non-Metal
  GPU here) — that's the whole point of this release.
