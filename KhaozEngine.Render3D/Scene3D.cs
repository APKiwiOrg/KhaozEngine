using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
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
    /// Veldrid resources but records into a caller-supplied command list (see <see cref="Render3DSurface"/> /
    /// <see cref="Render3DHost"/>); the public surface stays Veldrid-free.
    /// </summary>
    public sealed class Scene3D : IDisposable
    {
        readonly GraphicsDevice _gd;
        readonly OutputDescription _targetOutput;
        readonly ModelRenderer _model;
        readonly PixelPostProcess _post;
        readonly LineRenderer _lines;
        readonly BillboardRenderer _billboards;
        readonly RenderResources _res;
        readonly List<Mesh> _meshes = new();
        readonly SceneInstances _instances = new();
        readonly List<LineRenderer.LineVertex> _lineVerts = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAlpha = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAdditive = new();
        Vector3 _billboardRight, _billboardUp;
        bool _billboardBasisValid;

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
            _lines = new LineRenderer(gd, targetOutput);
            _billboards = new BillboardRenderer(gd, targetOutput);
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
            Vector3 eye = Camera.Eye;
            foreach (var inst in _instances.Items)
            {
                var m = _meshes[inst.Mesh.Index];
                _model.DrawInstance(cl, m.Vb, m.Ib, m.IndexCount, vp, inst.World, Post, inst.Tint, eye, inst.Material);
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
