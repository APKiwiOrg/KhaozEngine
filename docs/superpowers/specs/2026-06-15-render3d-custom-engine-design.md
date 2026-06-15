# KhaozEngine.Render3D + custom-engine pivot — design

Date: 2026-06-15
Status: approved (pending spec review)
Worktree/branch: `feature/render3d`

## 1. Why this exists

Two things at once:

1. **A make-or-break proof-of-concept for real-time pixel/retro 3D**, judged by eye before further 3D
   investment (the original ask: iso camera, glTF lit model, pixel post-process chain).
2. **The seed of a strategic pivot**: KhaozEngine becomes a full, self-contained, cross-platform game
   framework with **no MonoGame dependency anywhere** — desktop (Win/Mac/Linux) now, mobile (iOS/Android)
   later, covering 2D / iso-lite games as well as 3D.

This spec covers **sub-project 1 only**: the Render3D POC built on the new custom foundation. The broader
MonoGame-removal program is decomposed in `docs/ROADMAP.md`; later sub-projects each get their own
spec → plan → build cycle.

## 2. Decisions already made (with evidence)

These were settled in the brainstorming dialogue and de-risked with throwaway spikes.

- **Stay .NET, not C++.** Renderer heavy-lifting is on the GPU; modern .NET (SIMD `System.Numerics`,
  `Span<T>`, pooling) handles the CPU side; iOS ships via AOT; one language for engine + 3 games + tools;
  ~21 existing C# packages + 3 games would be thrown away by a C++ rewrite. .NET wins on every axis that
  affects shipping these games.
- **Drop MonoGame, go full custom** (user decision: "commit to full custom now").
- **GPU foundation: Veldrid, behind a KhaozEngine-owned seam.** Thin cross-platform GPU abstraction:
  native **Metal** on Mac/iOS, **Vulkan** on Android, **D3D11/Vulkan** on Windows. Shaders authored once
  in GLSL `#version 450` → SPIR-V → cross-compiled to MSL/HLSL/GLSL at load (`Veldrid.SPIRV`). MIT, forkable.
  Staleness risk (low maintenance upstream) is neutralised by the seam: a `Silk.NET`-Vulkan backend can
  replace Veldrid later without touching game/scene code.
- **Math baseline: `System.Numerics`** (`Vector3`, `Matrix4x4`, `Quaternion`). No `Microsoft.Xna.Framework`.

### Spike evidence (this Mac, Apple M2 Max, net10.0)

- **MonoGame GL is a dead end on Apple.** A MonoGame DesktopGL context here reports `GL_VERSION
  "2.1 Metal - 90.5"`, `GLSL 1.20`; `#version 130+` shaders fail to compile. That is Apple's deprecated
  GL-on-Metal layer — the reason custom shaders + modern + iOS are not reachable via MonoGame/OpenGL.
- **Veldrid works headless on Metal.** A headless spike created a `GraphicsDevice` (`BackendType = Metal`,
  `Apple M2 Max`), cross-compiled a GLSL `#version 450` shader through SPIR-V → MSL, drew a vertex-coloured
  triangle into an offscreen target, read it back (4608 non-black px of 16384). **No Wine, no window, no
  offline shader step.** `Veldrid.SPIRV`'s `libveldrid-spirv.dylib` is a universal binary (x86_64 **+ arm64**).

## 3. Scope

### In scope (sub-project 1)

The original five deliverables, re-targeted onto Veldrid:

1. `IsoCamera3D` — orthographic camera, configurable angle/zoom/target, `View` + `Projection`, unit-tested.
2. glTF load + lit draw — SharpGLTF → Veldrid buffers + base color; `ModelRenderer` with one directional
   sun, diffuse or cel shading. One committed test model.
3. `PixelPostProcess` — low-res render target → toggleable fullscreen chain: palette quantization
   (swappable) → optional Bayer dither → optional depth/normal edge outline → point-sampled upscale.
4. GLSL `#version 450` shaders compiled via SPIR-V (replaces the original "HLSL via MGFX" — MGFX/Wine is
   the dead end we are routing around).
5. A standalone windowed sample that spins the model and toggles the post-process + palettes.

### Out of scope (unchanged from the ask)

Gameplay, terrain, animation, multiple models, shadows, perf tuning, PBR.

### Deferred to later sub-projects (NOT this spec)

- The deeper multi-implementation `IGraphicsBackend` (multiple GPU backends behind one interface).
- 2D layer on Veldrid (sprite batcher, textured quads, the iso toolkit rebuilt).
- Porting Input / Screens / UI / Sprites / Effects off MonoGame onto the custom stack.
- Audio backend (OpenAL via `Silk.NET.OpenAL` or custom) to replace MonoGame audio.
- Windowing/input re-pipe onto SDL2 / `Silk.NET` feeding the existing `IRawInput` seam.
- Migrating Hardpoint / Nullwake / SpaceGame.
- Mobile platform layers (iOS/Android lifecycle, touch, packaging).

## 4. Package, dependencies, versioning

- **New package `KhaozEngine.Render3D`**, net10.0, following engine csproj conventions (README, XML docs,
  `InternalsVisibleTo KhaozEngine.Tests`).
- **Dependencies confined to this package**: `Veldrid`, `Veldrid.SPIRV`, `SharpGLTF.Core`. No other engine
  package references them. No MonoGame reference.
- **Known issue:** `Veldrid.SPIRV` pulls a transitive `Newtonsoft.Json 9.0.1` flagged `NU1903`
  (high-severity CVE). Action: confirm it is build/tooling-only and not shipped at runtime; pin a patched
  version if it leaks into runtime. Noted as a release-time check.
- **Independent version line.** This package ships at **`5.0.0-experimental`**, versioned via its **own
  csproj `<Version>` override**, decoupled from the shared `Directory.Build.props` `<Version>` (which stays
  on the 4.x line). This deliberately amends the one-shared-version rule for the transition:
  - **4.x line** — existing MonoGame-based packages; continue shipping 4.9.0, 4.10.0… in parallel.
  - **5.x experimental line** — new custom-stack packages, starting with `KhaozEngine.Render3D`.
  - The doc-version guard (`scripts/check-doc-versions.sh`) exempts the independent 5.x version like it
    already exempts consumer pins. `CLAUDE.md` and `ROADMAP.md` record the two-line scheme.

## 5. Architecture

The seam for the POC is the **package boundary**: Veldrid types are internal to `KhaozEngine.Render3D`;
the public API exposes only engine-native types. Consumers never reference Veldrid.

### Components

- **`IIsoCamera3D` / `IsoCamera3D`** (headless, pure `System.Numerics`)
  - Config: `Azimuth` (default 45°), `Elevation` (default `atan(0.5)` ≈ 26.57°, the 2:1 iso look),
    `Target`, `OrthoSize`, `Zoom`, `NearPlane`, `FarPlane`, `AspectRatio`.
  - Exposes `View`, `Projection`, `ViewProjection` as `Matrix4x4`.
  - View = look at `Target` from a unit direction derived from azimuth+elevation at a fixed distance;
    Projection = `Matrix4x4.CreateOrthographic(OrthoSize*Aspect/Zoom, OrthoSize/Zoom, Near, Far)`.
  - Interface so tests and consumers can fake it (mirrors the existing `IDesignViewport`/`IIsometricProjection`
    pattern in the engine).

- **`GltfMesh` + `GltfLoader`**
  - `GltfLoader.Load(path)` uses SharpGLTF to read a `.glb`/`.gltf`, flattens the first mesh's primitives
    into a CPU `GltfMesh` (interleaved position/normal/base-color vertices + index list, plus optional base
    color texture bytes).
  - `GltfMesh.Upload(backend)` (internal) creates the Veldrid vertex/index buffers + texture.
  - **Test model:** a single committed low-poly `.glb` (a shape that shows shading and silhouette — not a
    bare cube; e.g. a faceted/rounded low-poly object), packaged with the sample (and/or embedded).

- **`ModelRenderer` + lit shader** (`model.vert` / `model.frag`, GLSL `#version 450`)
  - Directional "sun" (`uniform vec3 LightDir`, `LightColor`, `AmbientColor`), Lambert diffuse.
  - `CelBands` uniform: 0 = smooth diffuse; N>0 = quantize N·L into N bands (cel shading).
  - Base color from material/vertex (and base color texture if present).
  - Renders into the **low-res** offscreen color target; also writes **view-space normal** and **depth** to
    additional targets (MRT) so the edge pass has real depth/normal data.

- **`PixelPostProcess`** — chain of fullscreen passes on the low-res target, each independently toggleable
  via `PixelPostProcessSettings`:
  1. **Palette quantization** (`palette.frag`): map each pixel to the nearest color in a swappable palette
     (uniform color array + count). Toggle `Quantize`.
  2. **Bayer dither** (optional, folded into the quantize pass or its own pass): 4×4 ordered-dither offset
     before nearest-color snap. Toggle `Dither`.
  3. **Depth/normal edge outline** (optional, `edge.frag`): detect depth and normal discontinuities vs.
     neighbours, draw outline color. Toggle `Outline`.
  4. **Point-sampled upscale**: final blit of the low-res result to the swapchain with a point sampler
     (crisp pixelation). Always on (it is what makes it a pixel image); low-res dimensions configurable.

- **`Scene3D`** (consumer API) — owns an `IsoCamera3D`, a loaded model, and `PixelPostProcessSettings`.
  - `Scene3D.Render()` draws the lit model into the low-res RT, runs the enabled post passes, presents to
    the swapchain.
  - `ModelRotation` (or a `Spin(dt)` helper) so the consumer can rotate the model each frame.
  - Construction takes the engine's render context (window/swapchain handle), hiding Veldrid.

- **`PixelPostProcessSettings`** — `bool Quantize, Dither, Outline`; `Palette ActivePalette`;
  `int LowResWidth, LowResHeight`; outline color; cel-bands count. Plus a small built-in `Palette` set
  (e.g. a couple of retro palettes) and `Palette` as a swappable `Rgba[]`.

### Sample harness (`Render3DSample`, `IsPackable=false`)

Standalone app, **not** hosted in Hardpoint (a Veldrid-into-MonoGame bridge would be throwaway work
pointing back at the stack we are leaving). Opens an SDL2/Metal window (Veldrid.StartupUtilities or
`Silk.NET.Windowing`), constructs a `Scene3D`, spins the model, and binds hotkeys:

- toggle palette quantization, dither, outline, cel vs. diffuse
- cycle palettes
- adjust azimuth/elevation (to try true-iso 30–35° by eye)
- adjust low-res resolution

This is the eyeball go/no-go harness.

## 6. Test strategy

- **`IsoCamera3D` math → headless unit tests** in `KhaozEngine.Tests` (CI-safe, pure `System.Numerics`):
  - A known world point projects to the expected clip/normalised-device coordinate.
  - The camera forward/view direction matches the configured azimuth + elevation.
  - Zoom / ortho-size scale the projection as expected.
- **Rendering is eyeball-judged** via the sample — by design, this is a look POC. GPU rendering needs a
  Metal device + display and cannot run on the headless CI test runner, so there is **no GPU render test in
  the CI suite**; this is an explicit, documented exception to the "every behaviour ships a headless test"
  rule for this module. (The headless Veldrid spike already demonstrated the pipeline renders; it is kept in
  history as evidence.)

## 7. Release plan (sub-project 1)

Per `KhaozEngine/CLAUDE.md` ritual, adapted for the independent version:

1. `KhaozEngine.Render3D` csproj pins `<Version>5.0.0-experimental</Version>` (overrides shared).
2. Add to `KhaozEngine.slnx` and `KhaozEngine.Tests` project references.
3. `CHANGELOG.md` entry (newest-first) describing the new experimental package + the pivot.
4. `dotnet pack -c Release -o ~/KhaozEngine/local-feed`.
5. Update `docs/USING-KHAOZENGINE.md` (how a consumer drives a spinning model with post-process on) and
   `docs/CONSUMERS.md` (new package row; no consumer adopts yet — Hardpoint deferred).
6. **Robustly update `docs/ROADMAP.md`** with the full post-MonoGame phased plan and the two-version-line
   scheme.
7. Amend `CLAUDE.md` to record the two-version-line exception.
8. Commit, `git tag v5.0.0-experimental`, finish per the finishing-a-development-branch options.

## 8. Risks / open items

- **Windowed swapchain on Metal** (sample) is a second small bring-up beyond the headless spike; standard
  Veldrid path but verified during the sample build.
- **MRT for depth/normal** on the Metal backend — verify Veldrid MRT + sampling the depth target works on
  Metal; if depth-target sampling is awkward, write linear depth to a color target from the model pass.
- **`Newtonsoft.Json` NU1903** transitive — confirm build-only / pin.
- **Veldrid maintenance** — mitigated by the package-boundary seam; a Silk.NET-Vulkan backend is the escape
  hatch (later sub-project).

## 9. Consumer usage (target shape, for USING-KHAOZENGINE.md)

```csharp
// Standalone (sample) — engine owns the window + Veldrid; consumer sees only engine types.
var scene = new Scene3D(renderContext);            // renderContext from the window/swapchain
scene.Model = GltfLoader.Load("crate.glb");
scene.Camera.Azimuth = 45f; scene.Camera.Elevation = MathF.Atan(0.5f);
scene.Post.LowResWidth = 320; scene.Post.LowResHeight = 180;
scene.Post.Quantize = true; scene.Post.Dither = true; scene.Post.Outline = true;
scene.Post.ActivePalette = Palettes.Retro8;

// each frame:
scene.Spin(dt);          // rotate the model
scene.Render();          // lit model -> low-res RT -> post chain -> swapchain
```
