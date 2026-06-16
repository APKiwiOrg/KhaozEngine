# Cross-platform desktop GPU

KhaozEngine's 5.x custom stack (Render2D / Render3D) runs on Veldrid. Each desktop OS gets a native graphics
backend; the GPU golden-snapshot net verifies rendering on each one through a CI matrix.

## Platform → backend (desktop scope)

| OS      | Backend     | `GpuBackendKind` | golden file suffix | software rasterizer (CI) |
| ------- | ----------- | ---------------- | ------------------ | ------------------------ |
| macOS   | Metal       | `Metal`          | `.metal.txt`       | native (Apple GPU)       |
| Windows | Direct3D11  | `Direct3D11`     | `.direct3d11.txt`  | WARP (auto fallback)     |
| Linux   | Vulkan      | `Vulkan`         | `.vulkan.txt`      | Mesa lavapipe            |

Backend selection is centralized in `KhaozEngine.Gpu.GpuBackendSelector`:

- `Select()` reads the `KE_GRAPHICS_BACKEND` env override (`metal` / `vulkan` / `d3d11` / `gl`,
  case-insensitive), otherwise probes the OS (macOS → Metal, Windows → Direct3D11, Linux/other → Vulkan).
- `CreateHeadless` builds the matching offscreen device (`GraphicsDevice.CreateVulkan` / `CreateD3D11` /
  Metal). No window — so the golden tests need no SDL2.

## Backend-aware goldens

`GoldenCompare.GoldenPath(name)` resolves `KhaozEngine.Tests/Gpu/goldens/<name>.<backend>.txt` where
`<backend>` = `GpuBackendSelector.Select().ToString().ToLowerInvariant()`. Each backend has its own reference
grid because a software rasterizer (lavapipe, WARP) does not match Apple Metal pixel-for-pixel; per-backend
goldens absorb that while still catching real shader / UBO / blend / winding / orientation regressions (coarse
32×18 grid, per-channel tolerance).

The Metal goldens (`scene2d.metal.txt`, `scene3d.metal.txt`) are committed and verified on every macOS run.

## CI matrix (`.github/workflows/cross-platform-gpu.yml`)

Runs the golden tests (`--filter FullyQualifiedName~Golden`, `KE_GPU_TESTS=1`) per OS with its backend,
`fail-fast: false`.

| trigger                          | behaviour                                                                     |
| -------------------------------- | ----------------------------------------------------------------------------- |
| `push` / `pull_request` on main  | **verify** committed goldens for each backend                                 |
| `workflow_dispatch` `bake=true`  | **re-bake** (`KE_UPDATE_GOLDENS=1`) and upload per-backend goldens as artifacts |

Software rasterizers on the runners (no real GPU):

- Linux Vulkan → Mesa **lavapipe** (`mesa-vulkan-drivers`, `VK_ICD_FILENAMES=.../lvp_icd.x86_64.json`).
- Windows D3D11 → **WARP** software adapter (automatic fallback when no hardware adapter is present).

### Per-backend golden flow

1. **Push / PR = verify.** macOS verifies the committed `.metal.txt` goldens immediately. Windows (D3D11) and
   Linux (Vulkan) **fail with "golden ... missing ... bake it"** until their goldens exist. That failure is
   expected on first run.
2. **Generate a new backend's goldens:** run the workflow manually with `bake = true`. The bake legs render
   with `KE_UPDATE_GOLDENS=1` and upload artifacts named `goldens-<backend>`
   (`scene2d.<backend>.txt`, `scene3d.<backend>.txt`).
3. **Commit them:** download the artifacts, drop the files into `KhaozEngine.Tests/Gpu/goldens/`, commit.
4. After that, the push/PR legs verify those backends instead of failing.

The fast inner-loop CI (`.github/workflows/ci.yml`: build/test/pack/publish, GPU tests skipped) is separate and
untouched.

## Remaining productization gaps

This release delivers the **verification mechanism**, not a finished cross-platform product. Open items:

1. **Windowed-app distribution needs native bundling.** The headless golden tests use `CreateHeadless` (no
   window) so they need no SDL2. A shipped game opens a window and needs **SDL2 + libveldrid-spirv bundled
   per-RID** (win-x64, linux-x64, osx-arm64, ...). On macOS SDL2 is still copied from Homebrew (Veldrid.SDL2
   lacks an osx-arm64 native); clean per-RID SDL2 bundling is still pending.
2. **OpenGL backend deferred.** D3D11 and Vulkan honor Veldrid's clip-space prefer-flags
   (clip-Y / 0..1 depth) like Metal, so they need no special handling. OpenGL's runtime clip-Y / depth
   derivation is the troublesome one and is out of scope here; the `gl` override parses but is unverified.
3. **Mobile (Android / iOS) is a separate project.** It needs native windowing/lifecycle, Native AOT, and
   build-time shader pre-compilation — not covered by this desktop matrix.
