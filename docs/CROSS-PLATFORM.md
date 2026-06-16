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

The Metal goldens (`scene2d.metal.txt`, `scene3d.metal.txt`) and the Direct3D11 goldens
(`scene2d.direct3d11.txt`, `scene3d.direct3d11.txt`, baked on WARP) are committed and verified on every macOS /
Windows run respectively. Linux Vulkan goldens are not committed yet (see below).

The 2D golden loads a libre font bundled in the test project (`KhaozEngine.Tests/Assets/Roboto-Regular.ttf`,
Apache-2.0) rather than an OS system-font path, so its glyph input is identical on every runner.

## CI matrix (`.github/workflows/cross-platform-gpu.yml`)

Runs the golden tests (`--filter FullyQualifiedName~Golden`, `KE_GPU_TESTS=1`) per OS with its backend,
`fail-fast: false`.

| trigger                          | behaviour                                                                     |
| -------------------------------- | ----------------------------------------------------------------------------- |
| `push` / `pull_request` on main  | **verify** committed goldens for each backend                                 |
| `workflow_dispatch` `bake=true`  | **re-bake** (`KE_UPDATE_GOLDENS=1`) and upload per-backend goldens as artifacts |

Software rasterizers on the runners (no real GPU):

- Linux Vulkan → Mesa **lavapipe** (`mesa-vulkan-drivers`, `VK_ICD_FILENAMES=.../lvp_icd.x86_64.json`). Veldrid
  4.9.0's Vulkan binding P/Invokes the bare names `libdl` / `libvulkan`, which modern Ubuntu only ships versioned,
  so the workflow symlinks `libdl.so` → `libdl.so.2` and `libvulkan.so` → `libvulkan.so.1`. Even with that, lavapipe
  currently crashes the test host at `vkEnumeratePhysicalDevices` on the hosted runner, so the **Vulkan leg is
  `continue-on-error` (informational, non-blocking)** until a working software-Vulkan setup or real GPU CI lands.
- Windows D3D11 → **WARP** software adapter (automatic fallback when no hardware adapter is present). Verified.

Net result: **Metal (macOS) and Direct3D11 (Windows/WARP) are validated and blocking; Vulkan (Linux/lavapipe) is
non-blocking and pending.** The overall workflow is green when Metal + D3D11 verify.

### Per-backend golden flow

1. **Push / PR = verify.** macOS verifies the committed `.metal.txt` goldens and Windows verifies the committed
   `.direct3d11.txt` goldens. A backend with no committed goldens **fails with "golden ... missing ... bake it"**
   (that's the current state for Vulkan, but its leg is non-blocking).
2. **Generate a new backend's goldens:** run the workflow manually with `bake = true`. The bake legs render
   with `KE_UPDATE_GOLDENS=1` and upload artifacts named `goldens-<backend>`
   (`scene2d.<backend>.txt`, `scene3d.<backend>.txt`).
3. **Commit them:** download the artifacts, drop the files into `KhaozEngine.Tests/Gpu/goldens/`, commit.
   (Metal and D3D11 goldens are already committed this way; the D3D11 set was baked on the WARP runner.)
4. After that, the push/PR legs verify those backends instead of failing.

The fast inner-loop CI (`.github/workflows/ci.yml`: build/test/pack/publish, GPU tests skipped) is separate and
untouched.

## Remaining productization gaps

This release delivers the **verification mechanism**, not a finished cross-platform product. Open items:

1. **Windowed-app native bundling (mostly resolved in 5.33.0).** The headless golden tests use `CreateHeadless`
   (no window), so they need no windowing natives. A shipped windowed game previously needed SDL2 bundled
   per-RID, and on macOS SDL2 was copied from Homebrew (Veldrid.Sdl2 lacked an osx-arm64 native). **5.33.0
   replaced Veldrid.Sdl2 with Silk.NET.Windowing (GLFW), which bundles its natives per-RID across desktop**, so
   the SDL2 problem is gone (no `brew install sdl2`). `libveldrid-spirv` still rides along per-RID via the Veldrid
   GPU packages. The remaining work is run-verifying the windowed path on Windows/Linux hardware (the headless
   matrix above does not open a window).
2. **OpenGL backend deferred.** D3D11 and Vulkan honor Veldrid's clip-space prefer-flags
   (clip-Y / 0..1 depth) like Metal, so they need no special handling. OpenGL's runtime clip-Y / depth
   derivation is the troublesome one and is out of scope here; the `gl` override parses but is unverified.
3. **Mobile (Android / iOS) is a separate project.** It needs native windowing/lifecycle, Native AOT, and
   build-time shader pre-compilation — not covered by this desktop matrix.
