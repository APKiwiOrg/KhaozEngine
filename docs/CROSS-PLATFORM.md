# Cross-platform desktop GPU

KhaozEngine's custom stack (Render2D / Render3D) runs on Veldrid. Each desktop OS gets a native graphics
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
  Metal). No window - so the golden tests need no SDL2.

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

- Linux Vulkan → Mesa **lavapipe** (`mesa-vulkan-drivers`). The lavapipe ICD manifest's name/path drifts across
  Ubuntu runner images (it is now `lvp_icd.json`, was `lvp_icd.x86_64.json`), so the workflow **discovers it at
  runtime** and points `VK_ICD_FILENAMES` + `VK_DRIVER_FILES` at it rather than hardcoding. Veldrid 4.9.0's Vulkan
  binding P/Invokes the bare names `libdl` / `libvulkan`, which modern Ubuntu only ships versioned, so the workflow
  also symlinks `libdl.so` → `libdl.so.2` and `libvulkan.so` → `libvulkan.so.1`.
- Windows D3D11 → **WARP** software adapter (automatic fallback when no hardware adapter is present). Verified.

Net result: **all three desktop backends are validated and blocking** - Metal (macOS), Direct3D11 (Windows/WARP),
and Vulkan (Linux/lavapipe). The overall workflow is green only when all three verify.

### Per-backend golden flow

1. **Push / PR = verify.** Each leg verifies its committed goldens (`.metal.txt`, `.direct3d11.txt`,
   `.vulkan.txt`). A backend with no committed goldens **fails with "golden ... missing ... bake it"**.
2. **Generate a new backend's goldens:** run the workflow manually with `bake = true`. The bake legs render
   with `KE_UPDATE_GOLDENS=1` and upload artifacts named `goldens-<backend>`
   (`scene2d.<backend>.txt`, `scene3d.<backend>.txt`).
3. **Commit them:** download the artifacts, drop the files into `KhaozEngine.Tests/Gpu/goldens/`, commit.
   (Metal and D3D11 goldens are already committed this way; the D3D11 set was baked on the WARP runner.)
4. After that, the push/PR legs verify those backends instead of failing.

The fast inner-loop CI (`.github/workflows/ci.yml`: build/test/pack/publish, GPU tests skipped) is separate and
untouched.

## Authoring shaders that pass on all three backends

The GLSL shaders cross-compile through SPIRV-Cross to MSL (Metal), HLSL→FXC (Direct3D11), and SPIR-V (Vulkan).
The three backends do NOT fail the same way, so a shader that renders correctly on Metal can be silently wrong on
D3D11 or Vulkan. The golden matrix above is the net; these are the traps it has caught. (Per-shader specifics live
in the `ShaderSources.cs` source comments; this is the consolidated checklist.)

- **Keep a fragment shader's input interpolants CONTIGUOUS from location 0.** If a fragment declares an `in`
  interpolant it never reads, SPIRV-Cross drops it and leaves a HOLE in the pixel-input signature (e.g.
  `vWorldPos`@3 then `vTint`@5, with location 4 absent). On D3D11/WARP that gap misaligns the interpolant
  registers and the highest live interpolant reads garbage: `SplatFrag`'s declared-but-unused `vUv`@4 corrupted
  `vEmissive` and rendered the whole terrain flat white (since fixed), while Metal and Vulkan tolerated it.
  Declare only the interpolants the fragment uses, contiguous from 0; if the paired vertex shader emits extras (a
  shared interpolant layout), number those ABOVE the fragment's live block. (`ModelFrag` is unaffected because it
  reads all of its interpolants.) Note: this is NOT an FXC optimizer miscompile - disabling FXC optimization does
  not fix it; the gapped signature is the cause.
- **Sample all textures up front, in binding order.** SPIRV-Cross assigns MSL texture indices in the order
  textures are first SAMPLED, not by `binding=`, so sampling a higher-binding texture first makes a lower one read
  the wrong texture on Metal (untextured meshes came out flat-normal coloured). See the `ModelFrag` / `EdgeFrag` /
  `SplatFrag` comments.
- **One uniform buffer per pipeline.** A second fragment UBO reads the first UBO's bytes on Metal; fold extra
  per-material data into the frame UBO (the splat pipeline appends its params after the light arrays in one
  combined UBO). See `SplatVert` / `SplatFrag`.
- **A new render feature needs a pixel-READBACK assertion, not just "it did not throw"** - and name the regression
  test `*Golden*` so the `cross-platform-gpu` matrix actually runs it per backend. The original splat tests lacked
  `Golden` in their names, so the D3D11 leg never exercised them and the white-terrain bug shipped.

When a backend-specific render bug reproduces ONLY on the CI rasterizer (WARP / lavapipe), dump the SPIRV-Cross
output locally to read what that backend's compiler receives:
`Veldrid.SPIRV.SpirvCompilation.CompileVertexFragment(vsGlslBytes, fsGlslBytes, CrossCompileTarget.HLSL, new CrossCompileOptions())`
is a pure cross-compile that runs on any OS and is byte-identical to what FXC sees on Windows - diff the broken
shader's signature against a working sibling. Bisect with many cheap Windows-only `cross-platform-gpu`
`workflow_dispatch` runs (each ~3 min); keep the macOS leg OUT of the bisect loop (it bills 10×) and restore the
full three-backend matrix only to verify the final fix.

## Remaining productization gaps

This release delivers the **verification mechanism**, not a finished cross-platform product. Open items:

1. **Windowed-app native bundling (mostly resolved).** The headless golden tests use `CreateHeadless`
   (no window), so they need no windowing natives. The engine windows through Silk.NET.Windowing (GLFW), which
   bundles its natives per-RID across desktop, so a shipped windowed game needs no hand-bundled SDL2 (no
   `brew install sdl2` on macOS). `libveldrid-spirv` still rides along per-RID via the Veldrid
   GPU packages. The remaining work is run-verifying the windowed path on Windows/Linux hardware (the headless
   matrix above does not open a window).
2. **OpenGL backend deferred.** D3D11 and Vulkan honor Veldrid's clip-space prefer-flags
   (clip-Y / 0..1 depth) like Metal, so they need no special handling. OpenGL's runtime clip-Y / depth
   derivation is the troublesome one and is out of scope here; the `gl` override parses but is unverified.
   (Clip-space-Y itself is not a baked Metal assumption: `GpuClip` derives the clip-Y sign
   from `GpuCapabilities` - identity on Metal / D3D11, flipped on Vulkan. The non-Metal path is still
   unvalidated on real hardware, since the cross-platform-gpu Vulkan leg is the known-red one.)
3. **Deferred port-hardening.** Two items are scoped but not yet built: GPU device-lost / device-removed
   handling (recreate the device + resources on a lost swapchain) and a central `Platform` OS-info seam
   (one place that answers OS / RID / capability questions). See
   `docs/superpowers/specs/2026-06-20-post-6.0.0-deferred-scope.md`.
4. **Mobile (Android / iOS) is a separate project.** It needs native windowing/lifecycle, Native AOT, and
   build-time shader pre-compilation - not covered by this desktop matrix.
