using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using StbImageSharp;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>Blend mode for <see cref="Scene3D.DrawBillboard"/>: standard <see cref="Alpha"/> transparency,
    /// or <see cref="Additive"/> (source-alpha/one) for glowy accumulation (sparks, muzzle flashes).</summary>
    public enum BillboardBlend { Alpha, Additive }

    /// <summary>
    /// A drawable 3D scene: an <see cref="IsoCamera3D"/>, a set of uploaded meshes, a per-frame instance queue,
    /// and the pixel post chain. Load meshes once with <see cref="LoadMesh"/>; each frame call
    /// <see cref="Begin"/>, queue instances with <see cref="Draw"/>, then have the surface/host render. Owns its
    /// GPU resources (via the KhaozEngine.Gpu seam) but records into a caller-supplied command list (see
    /// <see cref="Render3DSurface"/>); the public surface stays backend-free.
    /// </summary>
    public sealed class Scene3D : IDisposable
    {
        readonly IGpuDevice _gd;
        readonly GpuOutputDescription _targetOutput;
        readonly ModelRenderer _model;
        readonly PixelPostProcess _post;
        readonly LineRenderer _lines;
        readonly BillboardRenderer _billboards;
        readonly RenderResources _res;
        // Slot-indexed GPU mesh storage parallel to _slots; a freed slot's entry is null until reused.
        readonly List<Mesh?> _meshes = new();
        readonly MeshSlotMap _slots = new();
        // Loaded albedo textures, indexed by TextureHandle.Index. Shared across meshes; disposed in Dispose.
        readonly List<IGpuTexture> _textures = new();
        readonly SceneInstances _instances = new();
        readonly List<LineRenderer.LineVertex> _lineVerts = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAlpha = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAdditive = new();
        // Reused per-frame grouping buffers (cleared, not realloc) for GPU instancing.
        readonly List<ModelRenderer.InstanceData> _instanceData = new();
        readonly List<MeshRun> _runs = new();
        Vector3 _billboardRight, _billboardUp;
        bool _billboardBasisValid;

        public IsoCamera3D Camera { get; } = new();
        public PixelPostProcessSettings Post { get; } = new();

        internal Scene3D(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            _gd = gd;
            _targetOutput = targetOutput;
            _res = new RenderResources(gd, Post.RenderWidth, Post.RenderHeight);
            _model = new ModelRenderer(gd, _res.ModelFB.Outputs);
            _post = new PixelPostProcess(gd, _res.PingAFB.Outputs, targetOutput);
            _post.BindTargets(_res);
            _lines = new LineRenderer(gd, targetOutput);
            _billboards = new BillboardRenderer(gd, targetOutput);
        }

        /// <summary>An opaque handle to an albedo texture loaded with <see cref="LoadTexture(string)"/> /
        /// <see cref="LoadTexture(byte[],int,int)"/>. Pass it to <see cref="LoadMesh(GltfMesh,TextureHandle)"/> to
        /// texture a mesh. Wraps an index into Scene3D's internal texture list; the GPU texture stays internal.</summary>
        public readonly struct TextureHandle
        {
            /// <summary>Index into the owning scene's texture list (0-based). Internal detail; do not interpret.</summary>
            internal readonly int Index;
            internal TextureHandle(int index) { Index = index + 1; } // store +1 so default == Invalid (Index 0)
            /// <summary>An invalid handle (the same as <c>default</c>). Loading a mesh with this is untextured.</summary>
            public static TextureHandle Invalid => default;
            /// <summary>True when this handle refers to a loaded texture (not the <c>default</c>/Invalid handle).</summary>
            public bool IsValid => Index != 0;
            /// <summary>The 0-based list index this handle refers to. Only meaningful when <see cref="IsValid"/>.</summary>
            internal int ListIndex => Index - 1;
        }

        /// <summary>Upload a loaded mesh to the GPU once; returns a handle to instance it with <see cref="Draw"/>.
        /// Reuses a slot freed by <see cref="UnloadMesh"/> when one is available. The mesh is untextured (samples the
        /// renderer's 1x1 white default, so its colour is the baked vertex colour times any per-instance tint).</summary>
        public MeshHandle LoadMesh(GltfMesh mesh) => LoadMeshInternal(mesh, null);

        /// <summary>Upload a loaded mesh to the GPU once and bind <paramref name="texture"/> as its albedo. The
        /// fragment shader multiplies the sampled texel into the lit albedo (<c>texRgb * vColor * vTint</c>). An
        /// invalid/<c>default</c> <paramref name="texture"/> handle falls back to untextured (no throw).</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, TextureHandle texture)
        {
            IGpuResourceSet? material = null;
            if (texture.IsValid)
                material = _model.CreateMaterialSet(_textures[texture.ListIndex]);
            return LoadMeshInternal(mesh, material);
        }

        MeshHandle LoadMeshInternal(GltfMesh mesh, IGpuResourceSet? material)
        {
            var f = _gd.Factory;
            var vb = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Vertices.Length * ModelVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Indices.Length * sizeof(ushort)), GpuBufferUsage.IndexBuffer));
            _gd.UpdateBuffer(ib, 0, mesh.Indices);

            int index = _slots.Alloc(out int generation);
            var slot = new Mesh(vb, ib, mesh.Indices.Length, material);
            if (index < _meshes.Count) _meshes[index] = slot;   // reused freed slot
            else _meshes.Add(slot);                              // fresh appended slot
            return new MeshHandle(index, generation);
        }

        /// <summary>Decode a PNG/JPG file into an albedo texture (RGBA8) and return a handle for
        /// <see cref="LoadMesh(GltfMesh,TextureHandle)"/>. The texture is owned by the scene and freed in
        /// <see cref="Dispose"/>; it may be shared across several meshes.</summary>
        public TextureHandle LoadTexture(string pngPath)
        {
            ImageResult img = ImageResult.FromMemory(File.ReadAllBytes(pngPath), ColorComponents.RedGreenBlueAlpha);
            return LoadTexture(img.Data, img.Width, img.Height);
        }

        /// <summary>Create an albedo texture from raw RGBA8 bytes (row-major, <paramref name="width"/> *
        /// <paramref name="height"/> * 4 bytes) and return a handle. For procedural textures and tests. The texture
        /// is owned by the scene and freed in <see cref="Dispose"/>.</summary>
        public TextureHandle LoadTexture(byte[] rgba, int width, int height)
        {
            var tex = _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            _gd.UpdateTexture(tex, rgba, 0, 0, (uint)width, (uint)height);
            _textures.Add(tex);
            return new TextureHandle(_textures.Count - 1);
        }

        /// <summary>
        /// Free the GPU buffers backing <paramref name="h"/> and release its slot for reuse. A <c>default</c>
        /// handle is a no-op. A stale or bogus handle (its generation no longer matches the slot, e.g. a
        /// double-free) throws <see cref="ArgumentException"/>.
        /// </summary>
        public void UnloadMesh(MeshHandle h)
        {
            if (h.Generation == 0) return;          // default handle: no-op
            _slots.Free(h.Index, h.Generation);     // throws on stale/invalid
            var m = _meshes[h.Index];
            // Dispose the per-mesh material set alongside the buffers, but NOT the texture: it is owned in
            // _textures and may be shared by other meshes (freed in Dispose).
            if (m is { } mesh) { mesh.Vb.Dispose(); mesh.Ib.Dispose(); mesh.MaterialSet?.Dispose(); }
            _meshes[h.Index] = null;
        }

        /// <summary>Start a frame: clear the instance queue, the debug-line queue, and the billboard queues.
        /// Call before submitting.</summary>
        public void Begin()
        {
            _instances.Begin();
            _lineVerts.Clear();
            _billboardAlpha.Clear();
            _billboardAdditive.Clear();
            _billboardBasisValid = false;
        }

        /// <summary>Queue one instance: draw <paramref name="mesh"/> at world transform <paramref name="world"/> (no tint).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world) => _instances.Add(mesh, world, Vector4.One);

        /// <summary>Queue one instance with a per-instance RGBA <paramref name="tint"/> that multiplies the lit color.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Vector4 tint) => _instances.Add(mesh, world, tint);

        /// <summary>Queue one instance with a per-instance <paramref name="tint"/> and <paramref name="material"/>
        /// (emissive glow + specular).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Vector4 tint, Material material) => _instances.Add(mesh, world, tint, material);

        // ---- Debug line overlay (immediate-mode; queued this frame, drawn on top after post). ----

        /// <summary>Queue a single debug line from <paramref name="a"/> to <paramref name="b"/> in colour
        /// <paramref name="color"/> (RGBA). Cleared in <see cref="Begin"/>; drawn over the post image.</summary>
        public void DebugLine(Vector3 a, Vector3 b, Vector4 color)
        {
            _lineVerts.Add(new LineRenderer.LineVertex(a, color));
            _lineVerts.Add(new LineRenderer.LineVertex(b, color));
        }

        /// <summary>Queue a ray from <paramref name="origin"/> along <paramref name="direction"/> for
        /// <paramref name="length"/> units.</summary>
        public void DebugRay(Vector3 origin, Vector3 direction, float length, Vector4 color)
        {
            if (direction.LengthSquared() < 1e-12f) return;   // degenerate direction: nothing to draw
            DebugLine(origin, origin + Vector3.Normalize(direction) * length, color);
        }

        /// <summary>Queue the 12 edges of an axis-aligned box centred at <paramref name="center"/> with full
        /// extents <paramref name="size"/>.</summary>
        public void DebugBox(Vector3 center, Vector3 size, Vector4 color)
        {
            _scratch.Clear();
            DebugShapes.Box(_scratch, center, size);
            AppendScratch(color);
        }

        /// <summary>Queue an XZ-plane grid through <paramref name="center"/>.Y: <c>cells+1</c> lines each way,
        /// spanning <c>cells*cellSize</c>.</summary>
        public void DebugGrid(Vector3 center, float cellSize, int cells, Vector4 color)
        {
            _scratch.Clear();
            DebugShapes.Grid(_scratch, center, cellSize, cells);
            AppendScratch(color);
        }

        /// <summary>Queue 3 axis lines from <paramref name="origin"/> (X red, Y green, Z blue), each
        /// <paramref name="scale"/> long.</summary>
        public void DebugAxes(Vector3 origin, float scale)
        {
            DebugLine(origin, origin + new Vector3(scale, 0, 0), new Vector4(1f, 0.2f, 0.2f, 1f));
            DebugLine(origin, origin + new Vector3(0, scale, 0), new Vector4(0.2f, 1f, 0.2f, 1f));
            DebugLine(origin, origin + new Vector3(0, 0, scale), new Vector4(0.3f, 0.5f, 1f, 1f));
        }

        /// <summary>Queue a circle of <paramref name="segments"/> segments at <paramref name="radius"/> from
        /// <paramref name="center"/> in the plane perpendicular to <paramref name="normal"/>
        /// (use <see cref="Vector3.UnitY"/> for a ground ring).</summary>
        public void DebugCircle(Vector3 center, Vector3 normal, float radius, Vector4 color, int segments = 32)
        {
            _scratch.Clear();
            DebugShapes.Circle(_scratch, center, normal, radius, segments);
            AppendScratch(color);
        }

        readonly List<Vector3> _scratch = new();

        void AppendScratch(Vector4 color)
        {
            foreach (var p in _scratch)
                _lineVerts.Add(new LineRenderer.LineVertex(p, color));
        }

        // ---- Camera-facing billboard overlay (immediate-mode; queued this frame, drawn on top after lines). ----

        /// <summary>Queue a camera-facing soft-disc billboard centred at <paramref name="worldPos"/> with half-size
        /// <paramref name="size"/> (the quad spans 2*size across), tinted by <paramref name="color"/> (RGBA), using
        /// the given <paramref name="blend"/>. Cleared in <see cref="Begin"/>; drawn over the post image and the
        /// debug lines. The game loops its particle system's <c>Active</c> span and calls this per particle.</summary>
        public void DrawBillboard(Vector3 worldPos, float size, Vector4 color, BillboardBlend blend = BillboardBlend.Alpha)
        {
            // Camera basis is constant across a frame's billboards; compute it once (on the first call) and reuse.
            if (!_billboardBasisValid)
            {
                BillboardGeometry.CameraBasis(Camera.Forward, out _billboardRight, out _billboardUp);
                _billboardBasisValid = true;
            }
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            BillboardGeometry.Triangles(worldPos, size, _billboardRight, _billboardUp, pos, uv);

            var list = blend == BillboardBlend.Additive ? _billboardAdditive : _billboardAlpha;
            for (int i = 0; i < 6; i++)
                list.Add(new BillboardRenderer.BillboardVertex(pos[i], uv[i], color));
        }

        // Current internal render-target size (physical pixels). Exposed for tests to assert MatchViewport resizes
        // and FixedInternal stays put; not part of the public surface.
        internal int RenderTargetWidth => _res.Width;
        internal int RenderTargetHeight => _res.Height;

        /// <summary>
        /// The internal render-target size for a given post config + viewport. <see cref="RenderScale.FixedInternal"/>
        /// returns <see cref="PixelPostProcessSettings.RenderWidth"/>/<c>RenderHeight</c> unchanged (the historical
        /// path). <see cref="RenderScale.MatchViewport"/> tracks the viewport, clamped to
        /// <see cref="PixelPostProcessSettings.MaxRenderWidth"/>/<c>MaxRenderHeight</c> with aspect preserved, each
        /// dimension at least 1. Pure + headless-testable (no GPU). Stable once the viewport is at/over the cap for a
        /// fixed aspect, so <see cref="EnsureSize"/> doesn't thrash.
        /// </summary>
        internal static (int W, int H) ComputeTargetSize(PixelPostProcessSettings s, int viewportW, int viewportH)
        {
            if (s.RenderScale == RenderScale.FixedInternal)
                return (s.RenderWidth, s.RenderHeight);

            // MatchViewport: render at the framebuffer size, capped (aspect-preserving downscale) so a huge window
            // doesn't allocate an unbounded target. Guard against a zero/negative viewport during startup/minimise.
            int vw = Math.Max(1, viewportW);
            int vh = Math.Max(1, viewportH);
            int maxW = Math.Max(1, s.MaxRenderWidth);
            int maxH = Math.Max(1, s.MaxRenderHeight);
            if (vw <= maxW && vh <= maxH) return (vw, vh);
            float scale = MathF.Min((float)maxW / vw, (float)maxH / vh);
            int w = Math.Max(1, (int)MathF.Round(vw * scale));
            int h = Math.Max(1, (int)MathF.Round(vh * scale));
            return (w, h);
        }

        void EnsureSize(int viewportW, int viewportH)
        {
            var (tw, th) = ComputeTargetSize(Post, viewportW, viewportH);
            if (_res.Width != tw || _res.Height != th)
            {
                _res.Resize(tw, th);
                _post.BindTargets(_res);
            }
            // Aspect uses the true viewport (the post target is blit-stretched to fill it), not the clamped target.
            Camera.AspectRatio = viewportH > 0 ? (float)viewportW / viewportH : Camera.AspectRatio;
        }

        /// <summary>
        /// Record the scene (model pass over all queued instances -> post chain -> blit) into
        /// <paramref name="cl"/>, ending on <paramref name="target"/>. The caller owns Begin/End/Submit of
        /// <paramref name="cl"/>. <paramref name="viewportW"/>/<paramref name="viewportH"/> are the target size.
        /// </summary>
        internal void RenderInternal(IGpuCommandList cl, int viewportW, int viewportH, IGpuFramebuffer target)
        {
            EnsureSize(viewportW, viewportH);
            _post.PrepareUniforms(cl, _res, Post);

            _model.BeginModelPass(cl, _res, Post);
            Matrix4x4 vp = Camera.ViewProjection;
            Vector3 eye = Camera.Eye;
            _model.SetFrameUniforms(cl, vp, eye, Post);
            _model.BindPass(cl);

            // GPU instancing: group queued instances by mesh into a flat instance array (ordered by mesh) + a
            // run per unique mesh. Reuses member buffers (cleared, not realloc) to stay per-frame alloc-free.
            GroupInstances(_instances.Items, _instanceData, _runs);
            if (_instanceData.Count > 0)
            {
                _model.UploadInstances(cl, CollectionsMarshal.AsSpan(_instanceData));
                foreach (var run in _runs)
                {
                    // Skip a run whose mesh was unloaded (stale handle): a destroyed entity may linger a frame.
                    // The instance data was uploaded contiguously, so skipping a run just leaves its slice undrawn.
                    if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _meshes[run.Mesh.Index];
                    if (m is not { } mesh) continue;
                    _model.DrawMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, run.Start, run.Count, mesh.MaterialSet);
                }
            }

            _post.Run(cl, _res, target, Post);

            // Debug overlay: rebind `target` and draw the accumulated lines on top of the post image, with
            // depth disabled and alpha blend. Camera.ViewProjection matches the model pass (unflipped, so
            // lines line up with rendered geometry and with ScreenToGround picking).
            if (_lineVerts.Count > 0)
                _lines.Draw(cl, Camera.ViewProjection, CollectionsMarshal.AsSpan(_lineVerts), target);

            // Billboards: after the line pass, additive first (glow) then alpha, same overlay framebuffer +
            // ViewProjection. Each rebinds `target` (no clear) and uploads its own vertex span.
            if (_billboardAdditive.Count > 0)
                _billboards.Draw(cl, Camera.ViewProjection, CollectionsMarshal.AsSpan(_billboardAdditive), target, additive: true);
            if (_billboardAlpha.Count > 0)
                _billboards.Draw(cl, Camera.ViewProjection, CollectionsMarshal.AsSpan(_billboardAlpha), target, additive: false);
        }

        public void Dispose()
        {
            _model.Dispose();
            _post.Dispose();
            _lines.Dispose();
            _billboards.Dispose();
            _res.Dispose();
            foreach (var m in _meshes)
                if (m is { } mesh) { mesh.Vb.Dispose(); mesh.Ib.Dispose(); mesh.MaterialSet?.Dispose(); }
            foreach (var t in _textures) t.Dispose();
            _textures.Clear();
        }

        readonly struct Mesh
        {
            public readonly IGpuBuffer Vb, Ib;
            public readonly int IndexCount;
            /// <summary>Per-mesh material resource set (UBO + albedo + sampler), or null => the renderer's white
            /// default. The texture itself is owned in Scene3D's <c>_textures</c> list, not here, so a texture can
            /// be shared by several meshes; only the set is owned per mesh.</summary>
            public readonly IGpuResourceSet? MaterialSet;
            public Mesh(IGpuBuffer vb, IGpuBuffer ib, int indexCount, IGpuResourceSet? materialSet = null)
            {
                Vb = vb; Ib = ib; IndexCount = indexCount; MaterialSet = materialSet;
            }
        }

        /// <summary>A contiguous run of instances of one mesh handle inside the flat instance array.</summary>
        internal readonly struct MeshRun
        {
            public readonly MeshHandle Mesh;
            public readonly uint Start;
            public readonly uint Count;
            public MeshRun(MeshHandle mesh, uint start, uint count) { Mesh = mesh; Start = start; Count = count; }
            public MeshRun(int meshIndex, uint start, uint count) : this(new MeshHandle(meshIndex), start, count) { }
        }

        /// <summary>Two handles name the same mesh slot occupant (index AND generation match).</summary>
        internal static bool SameHandle(MeshHandle a, MeshHandle b) =>
            a.Index == b.Index && a.Generation == b.Generation;

        /// <summary>
        /// Group queued <paramref name="items"/> by mesh handle into <paramref name="instanceData"/> (a flat array
        /// ordered so all instances of one mesh are contiguous) and <paramref name="runs"/> (one
        /// <see cref="MeshRun"/> per unique mesh handle, in first-seen order). Pure + headless-testable; both output
        /// lists are Cleared and refilled (no realloc on the caller's reused buffers).
        /// </summary>
        internal static void GroupInstances(IReadOnlyList<SceneInstances.Instance> items,
            List<ModelRenderer.InstanceData> instanceData, List<MeshRun> runs)
        {
            instanceData.Clear();
            runs.Clear();
            if (items.Count == 0) return;

            // First-seen mesh order. Instances are usually already mesh-coherent (one mesh per kind), so the run
            // list stays short; we append each instance into its mesh's bucket by stable two-pass grouping.
            // Pass 1: collect distinct mesh indices in first-seen order + count per mesh.
            // Use the runs list as scratch for (meshIndex, count) accumulation.
            for (int i = 0; i < items.Count; i++)
            {
                MeshHandle mesh = items[i].Mesh;
                int slot = -1;
                for (int r = 0; r < runs.Count; r++)
                    if (SameHandle(runs[r].Mesh, mesh)) { slot = r; break; }
                if (slot < 0) runs.Add(new MeshRun(mesh, 0, 1));
                else runs[slot] = new MeshRun(mesh, 0, runs[slot].Count + 1);
            }

            // Assign each run a start offset (prefix sum), and record per-mesh write cursors.
            // runs currently holds (meshIndex, 0, count) in first-seen order.
            uint cursor = 0;
            Span<uint> writeCursor = runs.Count <= 64 ? stackalloc uint[runs.Count] : new uint[runs.Count];
            for (int r = 0; r < runs.Count; r++)
            {
                uint start = cursor;
                writeCursor[r] = start;
                cursor += runs[r].Count;
                runs[r] = new MeshRun(runs[r].Mesh, start, runs[r].Count);
            }

            // Size the flat array, then scatter each instance into its mesh's contiguous slot.
            int total = (int)cursor;
            for (int i = 0; i < total; i++) instanceData.Add(default);
            for (int i = 0; i < items.Count; i++)
            {
                var inst = items[i];
                MeshHandle mesh = inst.Mesh;
                int slot = -1;
                for (int r = 0; r < runs.Count; r++)
                    if (SameHandle(runs[r].Mesh, mesh)) { slot = r; break; }
                uint dst = writeCursor[slot]++;
                instanceData[(int)dst] = new ModelRenderer.InstanceData
                {
                    Model = inst.World,
                    Tint = inst.Tint,
                    Emissive = inst.Material.Emissive,
                    SpecParams = new Vector4(inst.Material.Specular, inst.Material.Shininess, 0f, 0f),
                };
            }
        }
    }
}
