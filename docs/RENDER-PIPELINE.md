# Render pipeline: how C# becomes rendered triangles

A high-level (container / "Level 2") map of the path from a C# draw call to pixels on screen. Not line-by-line:
the boxes name the real types so you can jump into the code, but the detail stops at the layer boundaries. For
the GPU strategy rationale (Veldrid, author-once GLSL) see [ROADMAP.md](ROADMAP.md) "The post-MonoGame pivot"
and [CROSS-PLATFORM.md](CROSS-PLATFORM.md).

## The flow

```mermaid
flowchart TD
    subgraph game["Your game code"]
        GC["scene.Draw(mesh, world, tint)  (3D)<br/>spriteBatch.Draw(tex, rect)  (2D)"]
    end

    subgraph win["KhaozEngine.Windowing - AppWindow (Silk.NET / GLFW)"]
        LOOP["Run(onFrame): the per-frame loop<br/>pump input -> build Frame -> onFrame(frame) -> Present()"]
    end

    subgraph r3["KhaozEngine.Render3D"]
        S3["Scene3D<br/>LoadMesh -> MeshHandle;  Begin / Draw accumulate instances"]
        MR["ModelRenderer: instanced DrawIndexed<br/>+ PixelPostProcess chain"]
    end
    subgraph r2["KhaozEngine.Render2D"]
        SB["SpriteBatch: batch quads into one<br/>persistent growable vertex buffer"]
    end

    subgraph gpu["KhaozEngine.Gpu - the backend seam (nothing above touches Veldrid)"]
        DEV["GpuDeviceContext<br/>opaque device / buffers / pipelines / command list"]
        SEL["GpuBackendSelector<br/>OS probe + KE_GRAPHICS_BACKEND override"]
        CAP["GpuCapabilities -> GpuClip<br/>clip-Y flip + depth range from the live device"]
    end

    VEL["Veldrid (contained behind the seam)"]

    subgraph back["Backend, chosen at runtime"]
        MTL["Metal (Mac / iOS)"]
        D3D["D3D11 (Windows)"]
        VK["Vulkan (Win / Linux / Android)"]
    end

    subgraph hw["GPU"]
        VS["Vertex shader: world x view x proj"]
        RAS["Rasterizer -> triangles -> fragments"]
        FS["Fragment shader: lit / cel / textured"]
        FB["Framebuffer -> swapchain"]
    end

    SCREEN(["Pixels on screen"])

    GC --> S3
    GC --> SB
    S3 --> MR
    LOOP -. drives each frame .-> S3
    LOOP -. drives each frame .-> SB
    MR --> DEV
    SB --> DEV
    SEL --> DEV
    CAP --> DEV
    DEV --> VEL
    VEL --> MTL & D3D & VK
    MTL & D3D & VK --> VS
    VS --> RAS --> FS --> FB --> SCREEN
    LOOP -. Present() .-> FB
```

## Two side-flows that feed it

**Geometry upload (CPU mesh -> GPU buffers), done once at load, not per frame:**

```mermaid
flowchart LR
    PRIM["MeshPrimitives / MeshBuilder<br/>(procedural shapes)"] --> GM["GltfMesh<br/>(CPU vertices + indices)"]
    GLTF["GltfLoader<br/>(runtime .gltf / .glb)"] --> GM
    GM --> LM["Scene3D.LoadMesh"] --> VB["GPU vertex + index buffers<br/>handle = MeshHandle"]
```

**Shaders are authored once in GLSL and cross-compiled at load** (the reason there is no per-platform shader
build step). Source lives inline in `Render3D/Internal/ShaderSources.cs` and `Render2D/SpriteBatch.cs`:

```mermaid
flowchart LR
    GLSL["GLSL #version 450<br/>(authored once)"] --> SPV["SPIR-V<br/>via Veldrid.SPIRV"]
    SPV --> MSL["MSL (Metal)"]
    SPV --> HLSL["HLSL (D3D11)"]
    SPV --> GLO["GLSL (Vulkan / GL)"]
```

## The same path in words

1. Your game calls `Scene3D.Draw(...)` (3D) or `SpriteBatch.Draw(...)` (2D) inside the `Frame` callback that
   `AppWindow.Run` invokes once per frame.
2. `Scene3D` accumulates draws as instances keyed by `MeshHandle` (geometry was uploaded once via `LoadMesh`);
   `SpriteBatch` packs quads into one persistent vertex buffer. Nothing here references Veldrid.
3. At frame end, `Render3DSurface` / `Render2DSurface` flush through `ModelRenderer` / the sprite shader, which
   record commands on `GpuDeviceContext` - the opaque seam (device, buffers, pipelines, command list).
4. `GpuBackendSelector` picked the backend at startup (OS probe + `KE_GRAPHICS_BACKEND`), and `GpuCapabilities`
   /`GpuClip` fold per-backend clip-Y and depth-range differences in so the renderers stay backend-agnostic.
5. The seam drives **Veldrid**, which targets the chosen native backend (Metal / D3D11 / Vulkan). Shaders were
   cross-compiled from one SPIR-V blob at load, so the same GLSL runs everywhere.
6. The GPU runs the vertex shader (world x view x proj), rasterizes to triangles + fragments, runs the fragment
   shader (lit / cel / textured), and writes the framebuffer. `AppWindow` calls `Present()` to put the
   swapchain image on screen.

## Where to look in the code

| Box | Type / file |
|---|---|
| Frame loop + window + swapchain | `KhaozEngine.Windowing/AppWindow.cs` (`Run`, `GpuDeviceContext.CreateForWindow`, `Present`) |
| 3D submission API | `KhaozEngine.Render3D/Scene3D.cs` (`LoadMesh`, `Begin`, `Draw`) + `Render3DSurface.cs` |
| 3D instanced draws + post | `KhaozEngine.Render3D/Internal/` (`ModelRenderer`), `PixelPostProcessSettings.cs` |
| 2D batching | `KhaozEngine.Render2D/SpriteBatch.cs` + `Render2DSurface.cs` |
| The backend seam | `KhaozEngine.Gpu/GpuDeviceContext.cs`, `GpuBackendSelector.cs`, `GpuCapabilities.cs`, `GpuClip.cs` |
| Veldrid binding | `KhaozEngine.Gpu/Internal/VeldridGpuDevice.cs` |
| Shader source | `KhaozEngine.Render3D/Internal/ShaderSources.cs`, `KhaozEngine.Render2D/SpriteBatch.cs` |
| Geometry sources | `KhaozEngine.Render3D/MeshPrimitives.cs`, `MeshBuilder.cs`, `GltfLoader` |
