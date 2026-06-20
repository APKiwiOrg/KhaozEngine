using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Primitives;
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
        readonly FillRenderer _fills;
        readonly BillboardRenderer _billboards;
        readonly TexturedBillboardRenderer _texBillboards;
        readonly RenderResources _res;
        // Slot-indexed GPU mesh storage parallel to _slots; a freed slot's entry is null until reused.
        readonly List<Mesh?> _meshes = new();
        readonly MeshSlotMap _slots = new();
        // Loaded albedo textures, indexed by TextureHandle.Index. Shared across meshes; disposed in Dispose.
        readonly List<IGpuTexture> _textures = new();
        // Per-texture billboard resource sets, parallel to _textures (ListIndex), created lazily the first time a
        // texture is used for a textured billboard. Disposed in Dispose.
        readonly List<IGpuResourceSet?> _texBillboardSets = new();
        readonly SceneInstances _instances = new();
        // Per-frame dynamic point lights, cleared each Begin() like the instance queue. The host adds them
        // (already N-nearest-culled); the renderer clamps to MaxPointLights and zero-fills the rest.
        readonly List<ModelRenderer.PointLightData> _lights = new();
        readonly List<LineRenderer.LineVertex> _lineVerts = new();
        readonly List<FillRenderer.FillVertex> _fillVerts = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAlpha = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAdditive = new();
        // Textured depth-interleaved billboards: queued in submission order (NOT split by blend, so additive and
        // alpha quads stay correctly ordered against each other), coalesced into same-texture+blend runs at render.
        readonly List<TexturedBillboardItem> _texBillboardItems = new();
        readonly List<TexturedBillboardRun> _texBillboardRuns = new();
        readonly List<BillboardRenderer.BillboardVertex> _texBillboardVerts = new();
        // Reused per-frame grouping buffers (cleared, not realloc) for GPU instancing.
        readonly List<ModelRenderer.InstanceData> _instanceData = new();
        readonly List<MeshRun> _runs = new();
        Vector3 _billboardRight, _billboardUp;
        bool _billboardBasisValid;

        public IsoCamera3D Camera { get; } = new();
        public PixelPostProcessSettings Post { get; } = new();

        /// <summary>Maximum dynamic point lights consumed in one frame. <see cref="AddLight"/> accepts any number,
        /// but only the first <see cref="MaxPointLights"/> queued are uploaded (extras are dropped); the host is
        /// expected to pick the N nearest per frame so a dense bullet-hell stays within budget.</summary>
        public const int MaxPointLights = ModelRenderer.MaxPointLights;

        internal Scene3D(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            _gd = gd;
            _targetOutput = targetOutput;
            _res = new RenderResources(gd, Post.RenderWidth, Post.RenderHeight);
            _model = new ModelRenderer(gd, _res.ModelFB.Outputs);
            _post = new PixelPostProcess(gd, _res.PingAFB.Outputs, targetOutput);
            _post.BindTargets(_res);
            _lines = new LineRenderer(gd, targetOutput);
            _fills = new FillRenderer(gd, targetOutput);
            _billboards = new BillboardRenderer(gd, targetOutput);
            // Textured billboards draw INTO the model MRT (depth-interleaved with meshes), so they target the model
            // framebuffer's output description, not the final target like the overlay renderers above.
            _texBillboards = new TexturedBillboardRenderer(gd, _res.ModelFB.Outputs);
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
            ImageRgba img = ImageRgba.Load(pngPath);
            return LoadTexture(img.Pixels, img.Width, img.Height);
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

        /// <summary>Start a frame: clear the instance queue, the point-light queue, the debug-line queue, the
        /// filled-overlay queue, and the billboard queues. Call before submitting.</summary>
        public void Begin()
        {
            _instances.Begin();
            _lights.Clear();
            _lineVerts.Clear();
            _fillVerts.Clear();
            _billboardAlpha.Clear();
            _billboardAdditive.Clear();
            _texBillboardItems.Clear();
            _billboardBasisValid = false;
        }

        /// <summary>Queue one instance: draw <paramref name="mesh"/> at world transform <paramref name="world"/> (no tint).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world) => _instances.Add(mesh, world, Color.White);

        /// <summary>Queue one instance with a per-instance RGBA <paramref name="tint"/> that multiplies the lit color.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint) => _instances.Add(mesh, world, tint);

        /// <summary>Queue one instance with a per-instance <paramref name="tint"/> and <paramref name="material"/>
        /// (emissive glow + specular).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material) => _instances.Add(mesh, world, tint, material);

        // ---- Dynamic point/effect lights (muzzle flashes, explosions, thrusters, key projectiles). ----

        /// <summary>
        /// Queue a dynamic point light at <paramref name="worldPos"/> for this frame: it adds diffuse (and cheap
        /// specular) to the lit mesh pass, on top of the global key+fill+ambient term, falling smoothly to zero at
        /// <paramref name="radius"/> world units and scaled by <paramref name="intensity"/>. <paramref name="color"/>
        /// is the light's RGB (alpha ignored). Cleared each <see cref="Begin"/> like the instance queue.
        /// </summary>
        /// <remarks>
        /// Presentation only - never feed simulation/collision state from a light. Only the first
        /// <see cref="MaxPointLights"/> lights queued in a frame are uploaded (extras are dropped); pick the
        /// N nearest to the camera/action per frame so a dense scene stays within the GPU budget. Zero lights ==
        /// the historical key+fill+ambient render, bit-identical.
        /// </remarks>
        public void AddLight(Vector3 worldPos, Color color, float radius, float intensity)
        {
            Vector4 c = color;
            _lights.Add(new ModelRenderer.PointLightData
            {
                PosRadius = new Vector4(worldPos, radius),
                ColorIntensity = new Vector4(c.X, c.Y, c.Z, intensity),
            });
        }

        /// <summary>Count of point lights queued this frame (before the renderer's <see cref="MaxPointLights"/>
        /// clamp). Internal: lets tests assert <see cref="Begin"/> clears the queue and <see cref="AddLight"/>
        /// enqueues.</summary>
        internal int LightCount => _lights.Count;

        // ---- Debug line overlay (immediate-mode; queued this frame, drawn on top after post). ----

        /// <summary>Queue a single debug line from <paramref name="a"/> to <paramref name="b"/> in colour
        /// <paramref name="color"/> (RGBA). Cleared in <see cref="Begin"/>; drawn over the post image.</summary>
        public void DebugLine(Vector3 a, Vector3 b, Color color)
        {
            _lineVerts.Add(new LineRenderer.LineVertex(a, color));
            _lineVerts.Add(new LineRenderer.LineVertex(b, color));
        }

        /// <summary>Queue a ray from <paramref name="origin"/> along <paramref name="direction"/> for
        /// <paramref name="length"/> units.</summary>
        public void DebugRay(Vector3 origin, Vector3 direction, float length, Color color)
        {
            if (direction.LengthSquared() < 1e-12f) return;   // degenerate direction: nothing to draw
            DebugLine(origin, origin + Vector3.Normalize(direction) * length, color);
        }

        /// <summary>Queue the 12 edges of an axis-aligned box centred at <paramref name="center"/> with full
        /// extents <paramref name="size"/>.</summary>
        public void DebugBox(Vector3 center, Vector3 size, Color color)
        {
            _scratch.Clear();
            DebugShapes.Box(_scratch, center, size);
            AppendScratch(color);
        }

        /// <summary>Queue an XZ-plane grid through <paramref name="center"/>.Y: <c>cells+1</c> lines each way,
        /// spanning <c>cells*cellSize</c>.</summary>
        public void DebugGrid(Vector3 center, float cellSize, int cells, Color color)
        {
            _scratch.Clear();
            DebugShapes.Grid(_scratch, center, cellSize, cells);
            AppendScratch(color);
        }

        /// <summary>Queue 3 axis lines from <paramref name="origin"/> (X red, Y green, Z blue), each
        /// <paramref name="scale"/> long.</summary>
        public void DebugAxes(Vector3 origin, float scale)
        {
            DebugLine(origin, origin + new Vector3(scale, 0, 0), new Color(1f, 0.2f, 0.2f, 1f));
            DebugLine(origin, origin + new Vector3(0, scale, 0), new Color(0.2f, 1f, 0.2f, 1f));
            DebugLine(origin, origin + new Vector3(0, 0, scale), new Color(0.3f, 0.5f, 1f, 1f));
        }

        /// <summary>Queue a circle of <paramref name="segments"/> segments at <paramref name="radius"/> from
        /// <paramref name="center"/> in the plane perpendicular to <paramref name="normal"/>
        /// (use <see cref="Vector3.UnitY"/> for a ground ring).</summary>
        public void DebugCircle(Vector3 center, Vector3 normal, float radius, Color color, int segments = 32)
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

        // ---- Filled (alpha-blended) overlay: flat, world-space translucent shapes painted on a plane (ground
        //      tiles, range/zone/AoE highlights). Queued this frame, drawn after post UNDER the debug lines so an
        //      outline reads crisp on top of a fill. The mesh pass is opaque, so a tinted plane mesh can't blend;
        //      these live here in the overlay pass. ----

        readonly List<Vector3> _fillScratch = new();

        /// <summary>Queue a flat translucent quad centred at <paramref name="center"/>, lying in the plane with the
        /// given <paramref name="normal"/>, its first in-plane axis along <paramref name="uAxis"/>.
        /// <paramref name="halfExtents"/>.X scales that axis, .Y the perpendicular one. <paramref name="color"/> is
        /// RGBA; its alpha is blended over the post image. Cleared in <see cref="Begin"/>; drawn under the debug
        /// lines.</summary>
        public void DebugFilledQuad(Vector3 center, Vector3 normal, Vector3 uAxis, Vector2 halfExtents, Color color)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledQuad(_fillScratch, center, normal, uAxis, halfExtents);
            AppendFillScratch(color);
        }

        /// <summary>Queue a flat translucent quad on the XZ ground plane (normal +Y, u axis +X) centred at
        /// <paramref name="center"/>, with the given <paramref name="halfExtents"/> (X along world X, Y along world
        /// Z) and RGBA <paramref name="color"/>.</summary>
        public void DebugFilledQuad(Vector3 center, Vector2 halfExtents, Color color) =>
            DebugFilledQuad(center, Vector3.UnitY, Vector3.UnitX, halfExtents, color);

        /// <summary>Queue a square translucent ground tile centred at <paramref name="center"/> on the XZ plane,
        /// half a <paramref name="halfSize"/> across each way, in RGBA <paramref name="color"/>. The board-tile
        /// convenience (range/coverage/AoE highlights).</summary>
        public void DebugFilledQuad(Vector3 center, float halfSize, Color color) =>
            DebugFilledQuad(center, Vector3.UnitY, Vector3.UnitX, new Vector2(halfSize, halfSize), color);

        /// <summary>Queue a flat translucent disc of <paramref name="segments"/> triangles at
        /// <paramref name="radius"/> from <paramref name="center"/>, in the plane perpendicular to
        /// <paramref name="normal"/> (use <see cref="Vector3.UnitY"/> for a ground disc), in RGBA
        /// <paramref name="color"/>.</summary>
        public void DebugFilledCircle(Vector3 center, Vector3 normal, float radius, Color color, int segments = 32)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledCircle(_fillScratch, center, normal, radius, segments);
            AppendFillScratch(color);
        }

        /// <summary>Queue a flat translucent triangle fan from <paramref name="center"/> out to an arbitrary,
        /// already-ordered boundary <paramref name="rim"/> (e.g. a turret's star-shaped line-of-sight area), in RGBA
        /// <paramref name="color"/>. When <paramref name="closed"/> (the default) the loop is sealed with a wrap
        /// triangle (center, rim[last], rim[0]); pass <c>false</c> for an open arc. Wind the rim CCW about the
        /// desired facing normal (use <see cref="Vector3.UnitY"/> for a ground fan, as with
        /// <see cref="DebugFilledCircle"/>). Cleared in <see cref="Begin"/>; drawn under the debug lines.</summary>
        public void DebugFilledFan(Vector3 center, IReadOnlyList<Vector3> rim, Color color, bool closed = true)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledFan(_fillScratch, center, rim, closed);
            AppendFillScratch(color);
        }

        void AppendFillScratch(Vector4 color)
        {
            foreach (var p in _fillScratch)
                _fillVerts.Add(new FillRenderer.FillVertex(p, color));
        }

        /// <summary>Count of queued filled-overlay vertices this frame (3 per triangle). Internal: lets tests
        /// assert <see cref="Begin"/> clears the queue and the builders queue the expected geometry.</summary>
        internal int FillVertexCount => _fillVerts.Count;

        // ---- Camera-facing billboard overlay (immediate-mode; queued this frame, drawn on top after lines). ----

        /// <summary>Queue a camera-facing soft-disc billboard centred at <paramref name="worldPos"/> with half-size
        /// <paramref name="size"/> (the quad spans 2*size across), tinted by <paramref name="color"/> (RGBA), using
        /// the given <paramref name="blend"/>. Cleared in <see cref="Begin"/>; drawn over the post image and the
        /// debug lines. The game loops its particle system's <c>Active</c> span and calls this per particle.</summary>
        public void DrawBillboard(Vector3 worldPos, float size, Color color, BillboardBlend blend = BillboardBlend.Alpha)
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

        /// <summary>
        /// Queue a camera-facing TEXTURED billboard: a quad at <paramref name="worldPos"/> with half-size
        /// <paramref name="size"/> (spans 2*size across), sampling the sub-rect <paramref name="sourceUv"/>
        /// (<c>(u0,v0,u1,v1)</c> - bottom-left to top-right; pass <c>(0,0,1,1)</c> for the whole texture, or a frame
        /// rect for a sprite sheet) of the texture loaded as <paramref name="texture"/>, multiplied by
        /// <paramref name="tint"/> (RGBA), using <paramref name="blend"/>. Cleared in <see cref="Begin"/>.
        /// </summary>
        /// <remarks>
        /// Unlike the colour-only <see cref="DrawBillboard(Vector3,float,Color,BillboardBlend)"/> (an overlay drawn
        /// after the post chain), textured billboards draw INTO the model pass with the depth test on (no depth
        /// write): a nearer mesh occludes the quad and the quad draws over a farther mesh, so meshes and sprites
        /// interleave correctly. Depth write is off, so overlapping quads blend in SUBMISSION order - submit
        /// back-to-front for correct transparency. An invalid/<c>default</c> <paramref name="texture"/> draws
        /// nothing (no throw). Presentation only.
        /// </remarks>
        public void DrawBillboard(TextureHandle texture, Vector3 worldPos, float size, Vector4 sourceUv, Color tint,
            BillboardBlend blend = BillboardBlend.Alpha)
        {
            if (!texture.IsValid) return;   // nothing to sample: no-op, like the untextured-mesh fallback
            Vector4 c = tint;
            _texBillboardItems.Add(new TexturedBillboardItem
            {
                TexIndex = texture.ListIndex,
                Blend = blend,
                Center = worldPos,
                Size = size,
                SourceUv = sourceUv,
                Color = c,
            });
        }

        /// <summary>Queue a textured billboard sampling the WHOLE texture (source rect <c>(0,0,1,1)</c>); see
        /// <see cref="DrawBillboard(TextureHandle,Vector3,float,Vector4,Color,BillboardBlend)"/>.</summary>
        public void DrawBillboard(TextureHandle texture, Vector3 worldPos, float size, Color tint,
            BillboardBlend blend = BillboardBlend.Alpha) =>
            DrawBillboard(texture, worldPos, size, new Vector4(0f, 0f, 1f, 1f), tint, blend);

        /// <summary>Count of textured billboards queued this frame. Internal: lets tests assert
        /// <see cref="Begin"/> clears the queue and the overloads enqueue.</summary>
        internal int TexturedBillboardCount => _texBillboardItems.Count;

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
            float scale = ViewportMath.Fit(vw, vh, maxW, maxH);
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
            _model.SetFrameUniforms(cl, vp, eye, Post, CollectionsMarshal.AsSpan(_lights));
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

            // Textured billboards: drawn into the SAME model framebuffer (still bound), after the meshes, with the
            // depth test on (no write). This is what gives mesh/sprite depth interleaving; then the whole MRT
            // (meshes + textured billboards) goes through the post chain together.
            DrawTexturedBillboards(cl);

            _post.Run(cl, _res, target, Post);

            // Filled overlay: rebind `target` and draw the accumulated translucent triangles on top of the post
            // image, BEFORE the lines so an outline drawn on top of a fill reads crisp. Depth disabled + alpha
            // blend; same Camera.ViewProjection as the model pass (so fills line up with geometry and picking).
            if (_fillVerts.Count > 0)
                _fills.Draw(cl, Camera.ViewProjection, CollectionsMarshal.AsSpan(_fillVerts), target);

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

        /// <summary>Coalesce the queued textured billboards into same-(texture,blend) runs (submission order
        /// preserved), then draw each run into the model framebuffer. The model FB is still bound from the mesh
        /// pass; the depth buffer holds the meshes' depth so the quads interleave. No-op when nothing is queued.</summary>
        void DrawTexturedBillboards(IGpuCommandList cl)
        {
            if (_texBillboardItems.Count == 0) return;

            CoalesceTexturedBillboards(_texBillboardItems, _texBillboardRuns);

            // Camera basis is constant across the frame; compute once and reuse for every quad.
            BillboardGeometry.CameraBasis(Camera.Forward, out Vector3 right, out Vector3 up);
            _texBillboards.SetViewProj(cl, Camera.ViewProjection);

            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            foreach (var run in _texBillboardRuns)
            {
                _texBillboardVerts.Clear();
                for (int i = run.Start; i < run.Start + run.Count; i++)
                {
                    var it = _texBillboardItems[i];
                    BillboardGeometry.Triangles(it.Center, it.Size, right, up, it.SourceUv, pos, uv);
                    for (int v = 0; v < 6; v++)
                        _texBillboardVerts.Add(new BillboardRenderer.BillboardVertex(pos[v], uv[v], it.Color));
                }
                IGpuResourceSet set = GetTexBillboardSet(run.TexIndex);
                _texBillboards.Draw(cl, CollectionsMarshal.AsSpan(_texBillboardVerts), _res.ModelFB, set,
                    run.Blend == BillboardBlend.Additive);
            }
        }

        /// <summary>Get (creating on first use) the textured-billboard resource set for the texture at
        /// <paramref name="texListIndex"/>. Cached parallel to <c>_textures</c>; disposed in <see cref="Dispose"/>.</summary>
        IGpuResourceSet GetTexBillboardSet(int texListIndex)
        {
            while (_texBillboardSets.Count <= texListIndex) _texBillboardSets.Add(null);
            var set = _texBillboardSets[texListIndex];
            if (set is null)
            {
                set = _texBillboards.CreateTextureSet(_textures[texListIndex]);
                _texBillboardSets[texListIndex] = set;
            }
            return set;
        }

        public void Dispose()
        {
            _model.Dispose();
            _post.Dispose();
            _lines.Dispose();
            _fills.Dispose();
            _billboards.Dispose();
            _texBillboards.Dispose();
            _res.Dispose();
            foreach (var m in _meshes)
                if (m is { } mesh) { mesh.Vb.Dispose(); mesh.Ib.Dispose(); mesh.MaterialSet?.Dispose(); }
            foreach (var s in _texBillboardSets) s?.Dispose();
            _texBillboardSets.Clear();
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

        /// <summary>One queued textured billboard (resolved texture list index + blend + transform + source rect +
        /// tint). Stored in submission order; coalesced into runs at render time.</summary>
        internal struct TexturedBillboardItem
        {
            public int TexIndex;          // ListIndex into _textures
            public BillboardBlend Blend;
            public Vector3 Center;
            public float Size;
            public Vector4 SourceUv;      // (u0,v0,u1,v1)
            public Vector4 Color;
        }

        /// <summary>A contiguous run of textured-billboard items sharing one texture + blend, drawn as one call.</summary>
        internal readonly struct TexturedBillboardRun
        {
            public readonly int TexIndex;
            public readonly BillboardBlend Blend;
            public readonly int Start;
            public readonly int Count;
            public TexturedBillboardRun(int texIndex, BillboardBlend blend, int start, int count)
            {
                TexIndex = texIndex; Blend = blend; Start = start; Count = count;
            }
        }

        /// <summary>
        /// Coalesce <paramref name="items"/> (in submission order) into <paramref name="runs"/>: each run is a
        /// maximal span of consecutive items sharing the same texture index AND blend. Submission order is
        /// preserved (a texture/blend change starts a new run rather than merging non-adjacent items), so
        /// alpha-blended quads keep the host's back-to-front ordering across textures. Pure + headless-testable;
        /// <paramref name="runs"/> is Cleared and refilled.
        /// </summary>
        internal static void CoalesceTexturedBillboards(IReadOnlyList<TexturedBillboardItem> items, List<TexturedBillboardRun> runs)
        {
            runs.Clear();
            if (items.Count == 0) return;

            int start = 0;
            for (int i = 1; i <= items.Count; i++)
            {
                bool boundary = i == items.Count
                    || items[i].TexIndex != items[start].TexIndex
                    || items[i].Blend != items[start].Blend;
                if (boundary)
                {
                    runs.Add(new TexturedBillboardRun(items[start].TexIndex, items[start].Blend, start, i - start));
                    start = i;
                }
            }
        }

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
