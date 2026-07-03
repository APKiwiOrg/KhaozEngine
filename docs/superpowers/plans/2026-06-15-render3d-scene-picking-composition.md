# Render3D Scene + Picking + Composition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade `KhaozEngine.Render3D` from a single-spinning-model demo into a real scene a game can use: many instances per frame, screen→ground tile picking, and composition into an `AppWindow` frame so a `Render2D` HUD draws on top. Ships as `5.13.0-experimental`.

**Architecture:** Immediate-mode scene (re-submit `Draw(mesh, world)` each frame, like `SpriteBatch`). `ModelRenderer.Draw` splits into a clear-once `BeginModelPass` + a per-instance `DrawInstance` so N instances share one model-FB pass. `Scene3D.RenderInternal` stops owning a `CommandList` and records into a caller-supplied `CommandList` + target `Framebuffer`; `Render3DHost` (standalone) and the new `Render3DSurface` (AppWindow composition) both drive that path. `IsoCamera3D` gains pure unproject/ground-pick math.

**Tech Stack:** C# net10.0, Veldrid (Metal), System.Numerics, xUnit. Engine repo `~/KhaozEngine`. Work in a worktree off `main` (`feature/render3d-scene-compose`), shared 5.x version line in `Directory.Build.props`.

---

## Pre-work: worktree

- [ ] Create the worktree via the native EnterWorktree tool, name `feature/render3d-scene-compose`. Run `mkdir -p local-feed` inside it. All paths below are relative to the worktree root.

---

### Task 1: `IsoCamera3D` picking (Ray + ScreenToRay + ScreenToGround)

Pure math, fully headless. The camera's `ViewProjection` is OpenGL-convention and (after Render3D's clip-Y flip cancels the Metal render-target flip) matches the displayed image, so picking inverts `ViewProjection` directly. Screen is top-left origin, y-down.

**Files:**
- Create: `KhaozEngine.Render3D/Camera/Ray.cs`
- Modify: `KhaozEngine.Render3D/Camera/IsoCamera3D.cs` (add `ScreenToRay`, `ScreenToGround`)
- Test: `KhaozEngine.Tests/Render3D/IsoCamera3DPickingTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class IsoCamera3DPickingTests
    {
        // Project a known world point to a screen pixel the way rendering does, then pick it back.
        static Vector2 WorldToScreen(IsoCamera3D cam, Vector3 world, int vw, int vh)
        {
            var clip = Vector4.Transform(new Vector4(world, 1f), cam.ViewProjection);
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            return new Vector2((ndc.X * 0.5f + 0.5f) * vw, (0.5f - ndc.Y * 0.5f) * vh);
        }

        [Fact]
        public void ScreenToGround_RoundTripsAGroundPoint()
        {
            var cam = new IsoCamera3D { Target = new Vector3(3, 0, -2), OrthoSize = 12f, AspectRatio = 16f / 9f };
            var world = new Vector3(5f, 0f, 1f);                 // on the ground plane y=0
            Vector2 screen = WorldToScreen(cam, world, 1600, 900);

            Vector3 hit = cam.ScreenToGround(screen, 1600, 900);

            Assert.Equal(world.X, hit.X, 2);
            Assert.Equal(0f, hit.Y, 4);
            Assert.Equal(world.Z, hit.Z, 2);
        }

        [Fact]
        public void ScreenCentre_MapsToTheCameraTargetGroundPoint()
        {
            var cam = new IsoCamera3D { Target = new Vector3(7, 0, 4), OrthoSize = 10f, AspectRatio = 1f };
            Vector3 hit = cam.ScreenToGround(new Vector2(400, 400), 800, 800);
            Assert.Equal(7f, hit.X, 2);
            Assert.Equal(4f, hit.Z, 2);
        }

        [Fact]
        public void ScreenToGround_RespectsACustomGroundHeight()
        {
            var cam = new IsoCamera3D();
            Vector3 hit = cam.ScreenToGround(new Vector2(500, 220), 1000, 600, groundY: 2.5f);
            Assert.Equal(2.5f, hit.Y, 4);
        }

        [Fact]
        public void ScreenToRay_DirectionMatchesCameraForward()
        {
            var cam = new IsoCamera3D { Target = Vector3.Zero };
            Ray r = cam.ScreenToRay(new Vector2(123, 456), 1000, 700);
            Vector3 d = Vector3.Normalize(r.Direction);
            Vector3 f = cam.Forward;
            Assert.Equal(f.X, d.X, 3);
            Assert.Equal(f.Y, d.Y, 3);
            Assert.Equal(f.Z, d.Z, 3);   // orthographic: every ray is parallel to Forward
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~IsoCamera3DPicking"`
Expected: FAIL — `Ray` / `ScreenToRay` / `ScreenToGround` do not exist (compile error).

- [ ] **Step 3: Create `Ray.cs`**

```csharp
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>A world-space ray: an origin and a (not necessarily normalized) direction.</summary>
    public readonly struct Ray
    {
        public Vector3 Origin { get; }
        public Vector3 Direction { get; }
        public Ray(Vector3 origin, Vector3 direction) { Origin = origin; Direction = direction; }
    }
}
```

- [ ] **Step 4: Add `ScreenToRay` + `ScreenToGround` to `IsoCamera3D`**

Add `using` is already present (`System`, `System.Numerics`). Insert these members after the `ViewProjection` property:

```csharp
        /// <summary>
        /// Unproject a screen pixel (top-left origin, y-down) into a world ray. For this orthographic camera
        /// the direction equals <see cref="Forward"/>; the math is general so it still holds if a perspective
        /// camera is added. Inverts <see cref="ViewProjection"/>, which matches the displayed image.
        /// </summary>
        public Ray ScreenToRay(Vector2 screenPixel, int viewportWidth, int viewportHeight)
        {
            float ndcX = screenPixel.X / viewportWidth * 2f - 1f;
            float ndcY = 1f - screenPixel.Y / viewportHeight * 2f;
            Matrix4x4.Invert(ViewProjection, out var inv);
            Vector3 near = Unproject(new Vector3(ndcX, ndcY, 0f), inv);
            Vector3 far = Unproject(new Vector3(ndcX, ndcY, 1f), inv);
            return new Ray(near, far - near);
        }

        /// <summary>Pick the world point under a screen pixel on the horizontal plane y = <paramref name="groundY"/>.</summary>
        public Vector3 ScreenToGround(Vector2 screenPixel, int viewportWidth, int viewportHeight, float groundY = 0f)
        {
            Ray r = ScreenToRay(screenPixel, viewportWidth, viewportHeight);
            // Intersect with the plane y = groundY. (Direction.Y is non-zero for an iso camera looking down.)
            float t = MathF.Abs(r.Direction.Y) < 1e-6f ? 0f : (groundY - r.Origin.Y) / r.Direction.Y;
            return r.Origin + r.Direction * t;
        }

        static Vector3 Unproject(Vector3 ndc, Matrix4x4 invViewProj)
        {
            var p = Vector4.Transform(new Vector4(ndc, 1f), invViewProj);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~IsoCamera3DPicking"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Camera/Ray.cs KhaozEngine.Render3D/Camera/IsoCamera3D.cs KhaozEngine.Tests/Render3D/IsoCamera3DPickingTests.cs
git commit -m "render3d: IsoCamera3D screen->ray / screen->ground picking (pure, headless-tested)"
```

---

### Task 2: Multi-instance scene plumbing

Two parts: a pure instance-queue holder (headless-tested), and the `ModelRenderer` split so N instances share one model-FB clear.

**Files:**
- Create: `KhaozEngine.Render3D/SceneInstances.cs`
- Create: `KhaozEngine.Render3D/MeshHandle.cs`
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.cs` (split `Draw` → `BeginModelPass` + `DrawInstance`)
- Test: `KhaozEngine.Tests/Render3D/SceneInstancesTests.cs`

- [ ] **Step 1: Write the failing test for the instance queue**

```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SceneInstancesTests
    {
        [Fact]
        public void Begin_Clears_DrawQueues_InOrder()
        {
            var s = new SceneInstances();
            s.Add(new MeshHandle(2), Matrix4x4.CreateTranslation(1, 0, 0));
            s.Add(new MeshHandle(5), Matrix4x4.CreateTranslation(0, 0, 3));

            Assert.Equal(2, s.Items.Count);
            Assert.Equal(2, s.Items[0].Mesh.Index);
            Assert.Equal(5, s.Items[1].Mesh.Index);
            Assert.Equal(1f, s.Items[0].World.M41, 4);   // translation X of the first instance

            s.Begin();
            Assert.Empty(s.Items);
        }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SceneInstances"`
Expected: FAIL — `SceneInstances` / `MeshHandle` do not exist.

- [ ] **Step 3: Create `MeshHandle.cs`**

```csharp
namespace KhaozEngine.Render3D
{
    /// <summary>A lightweight handle to a mesh uploaded to the GPU via <see cref="Scene3D.LoadMesh"/>.</summary>
    public readonly struct MeshHandle
    {
        public int Index { get; }
        public MeshHandle(int index) { Index = index; }
    }
}
```

- [ ] **Step 4: Create `SceneInstances.cs`**

```csharp
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The per-frame instance queue for <see cref="Scene3D"/>: <see cref="Begin"/> clears it, <see cref="Add"/>
    /// queues one (mesh, world) draw, and the renderer consumes <see cref="Items"/> in submission order. Pure /
    /// headless so the queueing is testable without a GPU.
    /// </summary>
    public sealed class SceneInstances
    {
        readonly List<Instance> _items = new();
        public IReadOnlyList<Instance> Items => _items;

        public void Begin() => _items.Clear();
        public void Add(MeshHandle mesh, Matrix4x4 world) => _items.Add(new Instance(mesh, world));

        public readonly struct Instance
        {
            public MeshHandle Mesh { get; }
            public Matrix4x4 World { get; }
            public Instance(MeshHandle mesh, Matrix4x4 world) { Mesh = mesh; World = world; }
        }
    }
}
```

- [ ] **Step 5: Run the queue test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SceneInstances"`
Expected: PASS.

- [ ] **Step 6: Split `ModelRenderer.Draw` into `BeginModelPass` + `DrawInstance`**

Replace the whole `public void Draw(...)` method (lines ~62-100) with the two methods below. `BeginModelPass` does the framebuffer bind + clear once; `DrawInstance` updates the per-instance UBO and issues the draw with no clear.

```csharp
        /// <summary>Bind + clear the model framebuffer once per frame, before drawing instances.</summary>
        public void BeginModelPass(CommandList cl, RenderResources res, PixelPostProcessSettings s)
        {
            cl.SetFramebuffer(res.ModelFB);
            // Metal MRT clear collapses to one value across attachments; clear all three to the background.
            // alpha 0 marks "background" for the starfield composite; the model writes alpha 1.
            var bg = new RgbaFloat(s.BackgroundColor.X, s.BackgroundColor.Y, s.BackgroundColor.Z, 0f);
            cl.ClearColorTarget(0, bg);
            cl.ClearColorTarget(1, bg);
            cl.ClearColorTarget(2, bg);
            cl.ClearDepthStencil(1f);
        }

        /// <summary>Draw one instance into the (already-bound, already-cleared) model pass.</summary>
        public void DrawInstance(CommandList cl, DeviceBuffer vb, DeviceBuffer ib, int indexCount,
            Matrix4x4 viewProj, Matrix4x4 model, PixelPostProcessSettings s)
        {
            // Row-major upload; flip clip Y so world-up maps to image-top on the Metal render target.
            var yFlip = Matrix4x4.Identity; yFlip.M22 = -1f;
            var ubo = new Ubo
            {
                ViewProj = viewProj * yFlip,
                Model = model,
                Dir = new Vector4(Vector3.Normalize(s.LightDirection), 0f),
                Color = s.LightColor,
                Ambient = s.AmbientColor,
                Params = new Vector4(s.CelBands, 0, 0, 0),
            };
            cl.UpdateBuffer(_ubo, 0, ref ubo);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
            cl.SetVertexBuffer(0, vb);
            cl.SetIndexBuffer(ib, IndexFormat.UInt16);
            cl.DrawIndexed((uint)indexCount, 1, 0, 0, 0);
        }
```

Note: the `_ubo` is shared, so `DrawInstance` overwrites it each call — correct for sequential draws in one CL. If a future backend batches differently this becomes per-instance UBOs, out of scope here.

- [ ] **Step 7: Build (ModelRenderer has no direct unit test; Scene3D in Task 3 consumes it)**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`
Expected: FAIL — `Scene3D.RenderInternal` still calls the old `_model.Draw(...)`. That is fixed in Task 3; do not fix here. (If you want a green build at this commit, temporarily comment the `_model.Draw` call — but Task 3 replaces that method wholesale, so it is fine to commit a known-broken Scene3D and fix it next.)

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Render3D/MeshHandle.cs KhaozEngine.Render3D/SceneInstances.cs KhaozEngine.Render3D/Rendering/ModelRenderer.cs KhaozEngine.Tests/Render3D/SceneInstancesTests.cs
git commit -m "render3d: instance queue (SceneInstances/MeshHandle) + split ModelRenderer into BeginModelPass/DrawInstance"
```

---

### Task 3: Multi-instance `Scene3D` + external-CommandList render

Rewrite `Scene3D` to hold several meshes, submit instances per frame, and record into a caller-supplied `CommandList` + target `Framebuffer` (no self-owned CL).

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.cs` (full rewrite of the body)

- [ ] **Step 1: Replace `Scene3D.cs` with the multi-instance version**

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using Veldrid;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A drawable 3D scene: an <see cref="IsoCamera3D"/>, a set of uploaded meshes, a per-frame instance queue,
    /// and the pixel post chain. Load meshes once with <see cref="LoadMesh"/>; each frame call
    /// <see cref="Begin"/>, queue instances with <see cref="Draw"/>, then have the surface/host render. Owns its
    /// Veldrid resources but records into a caller-supplied command list (see <see cref="Render3DSurface"/> /
    /// <see cref="Render3DHost"/>); the public surface stays Veldrid-free.
    /// </summary>
    public sealed class Scene3D : IDisposable
    {
        readonly GraphicsDevice _gd;
        readonly OutputDescription _targetOutput;
        readonly ModelRenderer _model;
        readonly PixelPostProcess _post;
        readonly RenderResources _res;
        readonly List<Mesh> _meshes = new();
        readonly SceneInstances _instances = new();

        public IsoCamera3D Camera { get; } = new();
        public PixelPostProcessSettings Post { get; } = new();

        internal Scene3D(GraphicsDevice gd, OutputDescription targetOutput)
        {
            _gd = gd;
            _targetOutput = targetOutput;
            _res = new RenderResources(gd, Post.RenderWidth, Post.RenderHeight);
            _model = new ModelRenderer(gd, _res.ModelFB.OutputDescription);
            _post = new PixelPostProcess(gd, _res.PingAFB.OutputDescription, targetOutput);
            _post.BindTargets(_res);
        }

        /// <summary>Upload a loaded mesh to the GPU once; returns a handle to instance it with <see cref="Draw"/>.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh)
        {
            var f = _gd.ResourceFactory;
            var vb = f.CreateBuffer(new BufferDescription((uint)(mesh.Vertices.Length * ModelVertex.SizeInBytes), BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = f.CreateBuffer(new BufferDescription((uint)(mesh.Indices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(ib, 0, mesh.Indices);
            _meshes.Add(new Mesh(vb, ib, mesh.Indices.Length));
            return new MeshHandle(_meshes.Count - 1);
        }

        /// <summary>Start a frame: clear the instance queue. Call before submitting instances.</summary>
        public void Begin() => _instances.Begin();

        /// <summary>Queue one instance: draw <paramref name="mesh"/> at world transform <paramref name="world"/>.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world) => _instances.Add(mesh, world);

        void EnsureSize(int viewportW, int viewportH)
        {
            if (_res.Width != Post.RenderWidth || _res.Height != Post.RenderHeight)
            {
                _res.Resize(Post.RenderWidth, Post.RenderHeight);
                _post.BindTargets(_res);
            }
            Camera.AspectRatio = viewportH > 0 ? (float)viewportW / viewportH : Camera.AspectRatio;
        }

        /// <summary>
        /// Record the scene (model pass over all queued instances -> post chain -> blit) into
        /// <paramref name="cl"/>, ending on <paramref name="target"/>. The caller owns Begin/End/Submit of
        /// <paramref name="cl"/>. <paramref name="viewportW"/>/<paramref name="viewportH"/> are the target size.
        /// </summary>
        internal void RenderInternal(CommandList cl, int viewportW, int viewportH, Framebuffer target)
        {
            EnsureSize(viewportW, viewportH);
            _post.PrepareUniforms(cl, _res, Post);

            _model.BeginModelPass(cl, _res, Post);
            Matrix4x4 vp = Camera.ViewProjection;
            foreach (var inst in _instances.Items)
            {
                var m = _meshes[inst.Mesh.Index];
                _model.DrawInstance(cl, m.Vb, m.Ib, m.IndexCount, vp, inst.World, Post);
            }

            _post.Run(cl, _res, target, Post);
        }

        public void Dispose()
        {
            _model.Dispose();
            _post.Dispose();
            _res.Dispose();
            foreach (var m in _meshes) { m.Vb.Dispose(); m.Ib.Dispose(); }
        }

        readonly struct Mesh
        {
            public readonly DeviceBuffer Vb, Ib;
            public readonly int IndexCount;
            public Mesh(DeviceBuffer vb, DeviceBuffer ib, int indexCount) { Vb = vb; Ib = ib; IndexCount = indexCount; }
        }
    }
}
```

Key changes from the old version: no `_cl` (caller owns it), `LoadMesh` returns a handle and appends (was `LoadModel`, replace-one), `Begin`/`Draw` queue instances, `Spin` removed, `RenderInternal` takes `(cl, viewportW, viewportH, target)` and does not `Begin/End/Submit`.

- [ ] **Step 2: Build (will fail at the call sites in Render3DHost — fixed in Task 4)**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`
Expected: FAIL — `Render3DHost` still calls `Scene.RenderInternal(framebuffer)` (old signature) and `LoadModel`/`Spin`. Fixed in Task 4.

- [ ] **Step 3: Commit (known-broken host, fixed next)**

```bash
git add KhaozEngine.Render3D/Scene3D.cs
git commit -m "render3d: Scene3D holds many meshes, queues per-frame instances, records into a caller CommandList"
```

---

### Task 4: Migrate `Render3DHost` + `Render3DSample` to the new API

`Render3DHost` owns its CL; make it drive the new render path. The sample loads a mesh once and submits instances.

**Files:**
- Modify: `KhaozEngine.Render3D/Render3DHost.cs`
- Modify: `Render3DSample/Program.cs`

- [ ] **Step 1: Read the current host render loop**

Run: `sed -n '40,70p' KhaozEngine.Render3D/Render3DHost.cs` to see the `Run` loop. The host has a `CommandList`? It currently calls `Scene.RenderInternal(_gd.MainSwapchain.Framebuffer)` and the scene owned the CL. Now the host must own a CL. Add a field `readonly CommandList _cl;` initialized in the ctor (`_cl = _gd.ResourceFactory.CreateCommandList();`), dispose it in `Dispose`, and wrap the render call:

```csharp
                _cl.Begin();
                Scene.RenderInternal(_cl, _window.Width, _window.Height, _gd.MainSwapchain.Framebuffer);
                _cl.End();
                _gd.SubmitCommands(_cl);
                _gd.SwapBuffers(_gd.MainSwapchain);
```

(Replace the existing single `Scene.RenderInternal(...)` + `SwapBuffers` lines with the block above.)

- [ ] **Step 2: Update the sample to the instance API**

In `Render3DSample/Program.cs`, replace the `LoadModel(...)` + `Spin(dt)` calls. Load the mesh once before the loop:

```csharp
var mesh = GltfLoader.Load("assets/testmodel.glb");      // existing asset path the sample already uses
var handle = host.Scene.LoadMesh(mesh);
```

In the per-frame callback, submit a small grid of instances so the multi-instance path is exercised (replace the old `Scene.Spin(dt)` line):

```csharp
    host.Scene.Begin();
    for (int gx = -1; gx <= 1; gx++)
        for (int gz = -1; gz <= 1; gz++)
            host.Scene.Draw(handle, Matrix4x4.CreateTranslation(gx * 3f, 0, gz * 3f));
```

(If the sample referenced `_spin`/`Spin` elsewhere, delete those references. Keep any camera/post toggle keys as-is.)

- [ ] **Step 3: Build the whole engine + sample**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj && dotnet build Render3DSample/Render3DSample.csproj`
Expected: PASS (0 errors).

- [ ] **Step 4: Run the full test suite (nothing regressed)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --nologo`
Expected: PASS — prior count + 5 new (4 picking + 1 instance queue).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Render3DHost.cs Render3DSample/Program.cs
git commit -m "render3d: drive Render3DHost + sample through the new instance/CommandList scene API"
```

---

### Task 5: `Render3DSurface` (compose 3D into an AppWindow frame)

**Files:**
- Create: `KhaozEngine.Render3D/Render3DSurface.cs`
- Reference: `KhaozEngine.Render2D/Render2DSurface.cs` (mirror its shape), `KhaozEngine.Windowing/AppWindow.cs` (`Frame`, `Device`, `MainSwapchain`)

- [ ] **Step 1: Add a project reference to Windowing (if absent)**

Run: `grep -q KhaozEngine.Windowing KhaozEngine.Render3D/KhaozEngine.Render3D.csproj && echo present || echo MISSING`
If MISSING, add inside an `<ItemGroup>`: `<ProjectReference Include="../KhaozEngine.Windowing/KhaozEngine.Windowing.csproj" />`

- [ ] **Step 2: Create `Render3DSurface.cs`**

```csharp
using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A 3D scene bound to an <see cref="AppWindow"/>: builds a <see cref="Scene3D"/> on the window's GPU device
    /// and renders it into the window's frames, so a Render2D HUD can draw on top. The window owns the device
    /// and the frame loop; this records into the frame's command list. Composition order, per frame:
    /// <see cref="Scene3D.Begin"/> + submit instances, <see cref="Render"/> (3D fills the frame), then the 2D
    /// surface draws the HUD over it.
    /// </summary>
    public sealed class Render3DSurface : IDisposable
    {
        readonly AppWindow _window;

        public Scene3D Scene { get; }

        public Render3DSurface(AppWindow window)
        {
            _window = window;
            Scene = new Scene3D(window.Device, window.MainSwapchain.Framebuffer.OutputDescription);
        }

        /// <summary>Record the queued 3D scene into this frame's command list, ending on the window framebuffer.</summary>
        public void Render(Frame frame) =>
            Scene.RenderInternal(frame.Commands, frame.Width, frame.Height, _window.MainSwapchain.Framebuffer);

        public void Dispose() => Scene.Dispose();
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Render3D/Render3DSurface.cs KhaozEngine.Render3D/KhaozEngine.Render3D.csproj
git commit -m "render3d: Render3DSurface composes the scene into an AppWindow frame (3D under a Render2D HUD)"
```

---

### Task 6: Visual verification (multi-instance + composition)

GPU output is verified by eye via a throwaway snapshot, the pattern used throughout the 5.x work (offscreen Metal -> RGBA -> PNG via Python stdlib). This is not committed.

- [ ] **Step 1: Multi-instance snapshot.** Write a throwaway `tools-tmp/SnapCheck` console (ProjectReference to `KhaozEngine.Render3D`) that: creates a `Render3DSnapshot`-style offscreen device (reuse `Render3DSnapshot` if it exposes a scene callback; otherwise replicate its device+framebuffer setup), loads `assets/testmodel.glb`, `Scene.Begin()`, submits a 3x3 grid of instances at distinct translations, renders, reads back RGBA, and writes a PNG (zlib via the Python snippet used in prior tasks). Read the PNG. Expected: nine models at nine distinct screen positions through the iso camera (not one model, not overlapping).

- [ ] **Step 2: Composition check.** Extend the throwaway (or a second one) to draw a `Render2D` `SpriteBatch` rectangle/text over the same framebuffer after the 3D render, confirming the HUD lands on top of the 3D and the 3D fills the background. Read the PNG; confirm 2D-over-3D ordering.

- [ ] **Step 3: Clean up.** `rm -rf tools-tmp`. No commit (throwaway).

If either looks wrong (instances overlapping, HUD under the 3D, distortion), stop and fix the relevant task before release.

---

### Task 7: Release ritual — `5.13.0-experimental`

- [ ] **Step 1: Bump the shared 5.x version.** In `Directory.Build.props`, `<KhaozEngineVersion>5.12.0-experimental</KhaozEngineVersion>` → `5.13.0-experimental`.

- [ ] **Step 2: CHANGELOG.** Add a newest-first `## 5.13.0-experimental (custom 5.x line)` entry covering: multi-instance `Scene3D` (`LoadMesh`/`Begin`/`Draw`, removed `LoadModel`/`Spin`), `IsoCamera3D.ScreenToRay`/`ScreenToGround` + `Ray`, `Render3DSurface` composition, and the `ModelRenderer` `BeginModelPass`/`DrawInstance` split (internal). Note it's the Phase A prerequisite for the Hardpoint 3D slice.

- [ ] **Step 3: ROADMAP.** Update `### Current status (as of ...)` + the two `currently` version refs to `5.13.0-experimental`. Under "Next milestones", add a line that the Render3D scene/picking/composition upgrade shipped (Phase A) and the Hardpoint 3D vertical slice (Phase B) is next.

- [ ] **Step 4: Doc guard + full suite.**

Run: `bash scripts/check-doc-versions.sh && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --nologo`
Expected: guard OK; all tests PASS.

- [ ] **Step 5: Commit the bump.**

```bash
git add Directory.Build.props CHANGELOG.md docs/ROADMAP.md
git commit -m "render3d(5.13.0-experimental): multi-instance scene + camera picking + AppWindow composition"
```

- [ ] **Step 6: Finish the branch** (use the finishing-a-development-branch skill / user's 5-option menu; expect option 2 = merge to main + push). On merge: run the suite on merged `main`, pack the 5 packages to `~/KhaozEngine/local-feed`:

```bash
for p in Render2D Render3D Audio Windowing Gui; do dotnet pack ~/KhaozEngine/KhaozEngine.$p/KhaozEngine.$p.csproj -c Release -o ~/KhaozEngine/local-feed --nologo; done
```

then `git tag v5.13.0-experimental` and push `main` + the tag. Remove the worktree.

---

## Self-review notes

- **Spec coverage:** unit 1 multi-instance scene → Tasks 2-4; unit 2 picking → Task 1; unit 3 composition → Task 5; visual verification → Task 6; release → Task 7. All covered.
- **Type consistency:** `MeshHandle.Index`, `SceneInstances.Instance{Mesh,World}`, `Scene3D.LoadMesh/Begin/Draw/RenderInternal(cl,w,h,target)`, `ModelRenderer.BeginModelPass/DrawInstance`, `Render3DSurface.Scene/Render`, `Ray{Origin,Direction}`, `IsoCamera3D.ScreenToRay/ScreenToGround` are used consistently across tasks.
- **Known intermediate breakage:** Tasks 2-3 commit a temporarily non-building `Render3DHost` (old call sites), fixed in Task 4. This is called out in each task; the build/test green-gate is Task 4 Step 3-4.
