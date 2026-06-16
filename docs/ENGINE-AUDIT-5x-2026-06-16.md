# KhaozEngine 5.x engine audit (2026-06-16, at 5.22.0-experimental)

Five parallel read-only assessments — architecture/layering, public API, performance/scalability,
cross-platform readiness, testability/correctness/gaps. This is the synthesis.

## Verdict

- **Cohesive?** Mostly yes at the package level (clean layering, Veldrid contained by *reference*, Particles is
  exemplary), but two cohesion holes: there's no app/game-loop framework (every game hand-wires composition),
  and the Gui has two parallel paradigms (retained `ScreenStack` + immediate `GuiSurface`) with duplicated
  styling.
- **Scalable?** Not yet, for a *populated* game. The 3D path uploads one UBO + issues one draw **per instance**
  (no instancing) and re-binds pipeline/resource-set redundantly; SpriteBatch **creates and disposes GPU
  buffers every frame**. Both are CPU/driver ceilings around a few hundred objects. The simulation side
  (particles) is excellent; the *submission* side is the bottleneck.
- **Well-architected?** The big bets are right (Veldrid + author-once GLSL→SPIR-V, System.Numerics,
  reflection-free, real per-OS data paths, bundled OpenAL, headless-test seams, snapshot verification). The
  main architectural debt is that **Veldrid types are the public renderer API** — there is no
  `IGraphicsBackend` seam despite the roadmap naming one — so the promised multi-backend future is currently a
  rewrite, not a swap.
- **What'll catch us out as a FULL engine?** The P0/P1 list below. Headline: GPU backend coupling + Metal
  assumptions, no game-loop facade, the two submission-perf ceilings, no GPU/correctness CI net, music-only
  audio, and a couple of resource leaks.

Net: **a strong, honestly-built foundation that has been engineered for "tens of objects, one platform, verified
by eye." Going to a full engine needs a deliberate hardening pass — but the gaps are bounded and mostly known,
not surprises.**

## Strengths (keep doing this)

- **Veldrid contained at the package boundary**; Audio/Particles/Gui never reference it. `Particles` is a
  textbook pure leaf (SoA pool, swap-remove, seeded xorshift, zero per-frame alloc).
- **Headless-testability is structural**, not bolted on: `ITextMeasurer`, `IDesignViewport`, `IIsoCamera3D`,
  `IMusicBackend`, pure math in cameras/primitives/particles, null-batch `GuiSurface`. 978 green tests.
- **Surface composition spine** (`Render2DSurface`/`Render3DSurface` borrow one window/device) is what makes
  3D-under-2D-HUD work on a single swapchain.
- **Press-origin click-through invariant** carried cleanly from 4.x into `Pointer`/`GuiSurface`.
- **Cross-platform bets already made**: GLSL→SPIR-V→MSL/HLSL/GLSL at runtime, System.Numerics, no reflection,
  correct per-OS data paths (`AppDataPaths` w/ XDG), openal-soft bundled across 8 RIDs, the R32F point-sample
  trap already avoided.
- **`MeshPrimitives` breadth (12 shapes) + `MeshBuilder`** with a correct normal matrix.

## Risk register

### P0 — fix before building more on top

1. **No `IGraphicsBackend` seam; Veldrid is the public API; Metal is hard-wired.** (architecture + cross-platform)
   `AppWindow` exposes `GraphicsDevice`/`Swapchain`/`CommandList` as public Veldrid types; `GraphicsBackend.Metal`
   is a literal in `AppWindow.cs:49` + `Render3DHost.cs:35`; snapshots call `CreateMetal()`. The longer games
   are written against these, the more Veldrid is pinned as the contract. **Fix:** introduce the seam now (even
   Metal-only) — an opaque device/frame abstraction + a backend factory (platform probe + `KE_GRAPHICS_BACKEND`
   override). Funnel the 2 device-creation sites + 2 snapshot factories through it. Stop exposing raw Veldrid on
   `AppWindow`.
2. **Two submission-perf ceilings.** (performance)
   - 3D: `ModelRenderer.DrawInstance` does UBO-upload + pipeline/set bind + draw **per instance** into a single
     shared UBO. Ceiling ~150-300 instances. **Fix:** group instances by `MeshHandle`; dynamic instance UBO/SSBO
     or GPU instancing; hoist the invariant `SetPipeline`/`SetGraphicsResourceSet` out of the loop (trivial,
     do first).
   - 2D: `SpriteBatch.Flush` calls `CreateBuffer` + `verts.ToArray()` per run **every frame** and disposes them
     next frame. **Fix:** one persistent growable vertex buffer (the Line/Billboard renderers already do this);
     drop `ToArray()` (use `CollectionsMarshal.AsSpan`).
3. **No GPU/correctness CI net + winding invisible under `FaceCullMode.None`.** (testability)
   Shader/UBO-std140/blend/winding regressions ship green; only a human eyeing a Mac snapshot catches them.
   The UBO field order is hand-mirrored against the shader with nothing enforcing it. **Fix:** golden-snapshot
   hash test gated behind a `[Trait]` so dev-Macs run it in `dotnet test` (PNG-diff fails on regression);
   extend the Torus-style winding-vs-normal check to every primitive + `GltfLoader` output.

### P1 — before more games adopt the stack

4. **No app/game-loop framework.** (architecture + API) Every game hand-writes the `window.Run` body in the
   right order (clock.Update → pointer.Update(viewport) → 3D submit → `Render3DSurface.Render` → 2D `NewFrame`
   → HUD). **Fix:** a `KhaozEngine.App` (5.x) `GameApp`/`FrameContext` facade owning the sequence + standard
   hooks; port GuiSample + Hardpoint onto it.
5. **POC debt about to be pinned by a real consumer.** (API) `Render3DHost` (a second window/loop) and
   `KhaozEngine.Render3D.Input.Key` (a second `Key` enum) are public. **Fix:** make them `internal`/`[Obsolete]`
   and route 3D through `Render3DSurface` + `Windowing.Key`/`InputState` **before** Hardpoint pins them.
6. **Dual Gui model with duplicated styling.** (API) Retained `Button` (hardcoded color fields) vs immediate
   `GuiSurface.Button` (`GuiStyle`), colors hand-duplicated in two files and documented as "matching" (they
   will drift); retained `Button` doesn't auto-reserve its rect (click-through bug in the retained path).
   **Fix:** retained widgets consume `GuiStyle`; document when to use which paradigm; auto-`BlockRegion`.
7. **Two resource leaks.** (testability) `ModelRenderer` never disposes its `ResourceLayout` (task already
   filed — the lone offender; every other renderer does it right). `Scene3D` has no `UnloadMesh` — `MeshHandle`
   is a raw append-only index, so per-level mesh churn leaks GPU buffers. **Fix:** dispose the layout (few
   lines); add mesh eviction (slot-map/generation handle) or loudly document the preload-only constraint.
8. **Clip-Y / depth conventions assume Metal, scattered across ~4 files.** (cross-platform) `Camera2D` comment
   "no clip-Y flip needed *in the Metal render target*" is a Metal truth posing as general; on OpenGL this is
   silently upside-down. **Fix:** derive flip/depth from `GraphicsDevice.IsClipSpaceYInverted`/
   `IsDepthRangeZeroToOne` at runtime, centralized — one change across `Camera2D`/`ModelRenderer`/snapshots.
   Same for the Metal MRT-clear-collapse hack (`ModelRenderer.cs:71`) — gate behind a backend capability.
9. **Ecs + Serialization (and Diagnostics) stranded on the retiring 4.x line but load-bearing for 5.x.**
   (architecture) **Fix:** decide their permanent home (graduate to a non-retiring "core" line) and document it
   before 4.x is frozen.

### P2 — cheap wins + later

10. **Per-frame allocations (trivial):** `PixelPostProcess.PrepareUniforms` allocates a 260-float palette array
    **every frame** (cache it, refill on change); `ScreenStack.Update` does `_screens.ToArray()` every frame
    (reuse a buffer / dirty flag).
11. **`GltfLoader` truncates indices >65535 silently and welds by position only** (breaks UV/hard-edge seams).
    **Fix:** throw or use uint indices; weld on (position, normal, uv).
12. **No typed `Color`/`Rect` on `SpriteBatch.Draw`** (untyped `Vector4` rect+color, swappable foot-gun).
    Cheaper to add typed overloads now than migrate every call site later.
13. **Snapshot plumbing duplicated** across Render2D/Render3D — extract a shared headless-capture helper.

## Capability gaps for a full engine

| Capability | Present? | Architecture-ready? | Effort |
|---|---|---|---|
| Textured-mesh rendering (sampler in 3D) | No (UVs flow, unused) | Yes — add texture to model layout/shader | M |
| SFX / audio mixing (one-shots, voices) | **No — music only** | Partial (OpenAL backend exists) | M |
| Shadows | No | Partial (MRT/depth infra exists) | L |
| Depth-sorted transparency (3D) | No (unsorted billboards) | Partial | M |
| 3D animation / skinning | No | No (vertex has no joints/weights) | L |
| Touch input on 5.x | Plumbed, not populated | Partial (needs SDL touch events) | S-M |
| Gamepad on 5.x | **Yes** (wired) | — | done |
| Scene / level management | No (`Scene3D` is a renderer) | No | M |
| Save/load, localization, netcode on 5.x | Packages exist, not 5.x-wired | Yes | S |
| Asset pipeline + hot-reload | No (runtime loaders only) | Partial | M-L |
| Profiling / on-screen diagnostics | No (logging only) | Partial (Gui makes it easy) | S |

## Recommended sequence

1. **Cheap correctness + perf now (low risk, high value):** dispose `ModelRenderer._layout`; hoist
   pipeline/set bind out of the 3D instance loop; cache the palette array; drop `ScreenStack.ToArray()`. (P0#2
   partial, P1#7 partial, P2#10.)
2. **The two seams that decouple the future:** `IGraphicsBackend` (P0#1) + the `GameApp` facade (P1#4). These
   are the highest-leverage architectural moves and should land before more games/features pile on raw Veldrid
   and hand-wired loops.
3. **Submission perf for real:** 3D instancing + persistent SpriteBatch buffer (P0#2). Needed before a
   populated Hardpoint board.
4. **Correctness net:** golden-snapshot test + winding checks everywhere (P0#3).
5. **Then capability work** as games demand it: SFX/mixer and textured meshes are the two highest-value gaps.
6. **Demote POC debt** (P1#5) opportunistically — ideally before Hardpoint re-pins.

Cross-platform backends (P1#8 + the Vulkan/D3D/GL bring-up) stay correctly deferred to real hardware/CI — but
the seam (P0#1) and the runtime clip-Y derivation should be built now so that day is *verification*, not
*construction*.
