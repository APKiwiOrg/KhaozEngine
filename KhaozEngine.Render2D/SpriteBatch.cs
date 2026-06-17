using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Batched 2D sprite + text renderer. Corners are transformed to clip space on the CPU by the current
    /// camera, so there is no per-batch uniform; quads are coalesced into submission-ordered runs (consecutive
    /// same-texture draws share a run) so painter's order is preserved across textures.
    /// </summary>
    public sealed class SpriteBatch : IDisposable
    {
        const string VertSrc = @"#version 450
layout(location=0) in vec2 ClipPos;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 Color;
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vColor;
void main() { gl_Position = vec4(ClipPos, 0.0, 1.0); vUv = Uv; vColor = Color; }";

        const string FragSrc = @"#version 450
layout(set=0, binding=0) uniform texture2D Tex;
layout(set=0, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vColor;
layout(location=0) out vec4 oColor;
void main() { oColor = texture(sampler2D(Tex, Samp), vUv) * vColor; }";

        struct V { public Vector2 Pos; public Vector2 Uv; public Vector4 Color; }

        readonly IGpuDevice _gd;
        readonly IGpuResourceLayout _layout;
        readonly IGpuPipeline _pipeline;
        readonly IGpuShaderSet _shaders;
        readonly IGpuSampler _sampler;
        readonly Dictionary<IGpuTexture, IGpuResourceSet> _sets = new();
        readonly QuadRunBuilder<V> _runs = new();

        const uint VertexSizeBytes = 32;       // V = Pos(8) + Uv(8) + Color(16)
        // One growable vertex buffer PER flush-within-a-frame. A frame can flush several times (every
        // SetScissor/ClearScissor forces one), and a buffer referenced by an already-recorded Draw must not be
        // overwritten before the GPU runs that Draw — so each flush gets its own buffer instead of reusing one at
        // offset 0. Buffers persist across frames (only grow); _flushIndex resets each NewFrame.
        readonly List<IGpuBuffer> _vbs = new();
        readonly List<uint> _vbCaps = new();
        int _flushIndex;

        IGpuCommandList _cl = null!;
        int _vw, _vh;
        Matrix4x4 _vp;
        Windowing.IDesignViewport? _viewport;   // active design viewport (set by Begin(IDesignViewport)), else null

        internal SpriteBatch(IGpuDevice gd, GpuOutputDescription output)
        {
            _gd = gd;
            var f = gd.Factory;
            _sampler = gd.LinearSampler;
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            _shaders = f.CreateShadersFromSpirv(VertSrc, FragSrc);
            var vl = new GpuVertexLayoutDescription(
                new GpuVertexElement("ClipPos", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));
            _pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vl },
                Outputs = output,
            });
        }

        /// <summary>
        /// Convert a clip rect (in viewport points, top-left origin) to framebuffer pixels, scaling for DPI
        /// (e.g. 2x Retina) and clamping to the framebuffer. Pure function — unit-tested headlessly.
        /// </summary>
        public static (uint X, uint Y, uint Width, uint Height) ComputeScissor(
            Windowing.Rect rect, int viewportW, int viewportH, int framebufferW, int framebufferH)
        {
            float sx = viewportW > 0 ? (float)framebufferW / viewportW : 1f;
            float sy = viewportH > 0 ? (float)framebufferH / viewportH : 1f;
            float x0 = Math.Clamp(rect.X * sx, 0, framebufferW);
            float x1 = Math.Clamp((rect.X + rect.Width) * sx, 0, framebufferW);
            float y0 = Math.Clamp(rect.Y * sy, 0, framebufferH);
            float y1 = Math.Clamp((rect.Y + rect.Height) * sy, 0, framebufferH);
            return ((uint)MathF.Round(x0), (uint)MathF.Round(y0),
                    (uint)MathF.Round(x1 - x0), (uint)MathF.Round(y1 - y0));
        }

        /// <summary>
        /// As <see cref="ComputeScissor(Windowing.Rect,int,int,int,int)"/>, but first maps a clip rect given in
        /// design space through <paramref name="viewport"/> (scale + letterbox offset) into window points. Pass
        /// a null viewport to treat <paramref name="rect"/> as already in window points. Pure / headless.
        /// </summary>
        public static (uint X, uint Y, uint Width, uint Height) ComputeScissor(
            Windowing.Rect rect, Windowing.IDesignViewport? viewport, int viewportW, int viewportH, int framebufferW, int framebufferH)
        {
            if (viewport != null)
            {
                var tl = viewport.DesignToScreen(new Vector2(rect.X, rect.Y));
                var br = viewport.DesignToScreen(new Vector2(rect.Right, rect.Bottom));
                rect = new Windowing.Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
            }
            return ComputeScissor(rect, viewportW, viewportH, framebufferW, framebufferH);
        }

        /// <summary>
        /// Flush pending draws, then clip subsequent draws to <paramref name="rect"/>. When a design viewport is
        /// active (<see cref="Begin(Windowing.IDesignViewport)"/>) <paramref name="rect"/> is in design space and
        /// is mapped through it; otherwise it is in window points. Pair with <see cref="ClearScissor"/>. The
        /// current transform is preserved, so no <see cref="Begin"/> is needed around it.
        /// </summary>
        public void SetScissor(Windowing.Rect rect)
        {
            Flush();
            var fb = _gd.SwapchainFramebuffer;
            int fbw = fb != null ? (int)fb.Width : _vw;
            int fbh = fb != null ? (int)fb.Height : _vh;
            var (x, y, w, h) = ComputeScissor(rect, _viewport, _vw, _vh, fbw, fbh);
            _cl.SetScissorRect(0, x, y, w, h);
        }

        /// <summary>Flush pending (clipped) draws, then reset the scissor to the full framebuffer (undo <see cref="SetScissor"/>).</summary>
        public void ClearScissor()
        {
            Flush();
            var fb = _gd.SwapchainFramebuffer;
            uint fbw = fb != null ? fb.Width : (uint)Math.Max(0, _vw);
            uint fbh = fb != null ? fb.Height : (uint)Math.Max(0, _vh);
            _cl.SetScissorRect(0, 0, 0, fbw, fbh);
        }

        // Called by the host/snapshot each frame before the user's draw callback.
        internal void NewFrame(IGpuCommandList cl, int viewportW, int viewportH)
        {
            _cl = cl; _vw = viewportW; _vh = viewportH; _flushIndex = 0;
        }

        /// <summary>Begin a batch in world space through <paramref name="camera"/>.</summary>
        public void Begin(Camera2D camera) { _vp = camera.GetViewProjection(_vw, _vh); _viewport = null; ResetBatches(); }

        /// <summary>Begin a batch in screen space (pixels, top-left origin).</summary>
        public void Begin() { _vp = Matrix4x4.CreateOrthographicOffCenter(0, _vw, _vh, 0, -1, 1); _viewport = null; ResetBatches(); }

        /// <summary>
        /// Begin a batch in design space through <paramref name="viewport"/>: subsequent draws use design
        /// coordinates and are scaled, centered, and letterboxed to the current window for the viewport's mode.
        /// A scissor set while this is active (<see cref="SetScissor"/>) is mapped through the viewport too.
        /// </summary>
        public void Begin(Windowing.IDesignViewport viewport) { _vp = viewport.GetClipProjection(_vw, _vh); _viewport = viewport; ResetBatches(); }

        public void Draw(Texture2D tex, Vector2 position, Vector4 color) =>
            Draw(tex, new Vector4(position.X, position.Y, tex.Width, tex.Height), new Vector4(0, 0, 1, 1), color);

        public void Draw(Texture2D tex, Vector4 destRect, Vector4 color) =>
            Draw(tex, destRect, new Vector4(0, 0, 1, 1), color);

        /// <summary>dest = (x, y, w, h) in world units; src = (u0, v0, u1, v1) in 0..1.</summary>
        public void Draw(Texture2D tex, Vector4 destRect, Vector4 srcUV, Vector4 color)
        {
            float x = destRect.X, y = destRect.Y, w = destRect.Z, h = destRect.W;
            EmitQuad(tex, new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h), srcUV, color);
        }

        /// <summary>
        /// Draw a rotated quad. <paramref name="position"/> is the world point where the pivot
        /// (<paramref name="originNormalized"/>, in [0,1] of the quad) lands; <paramref name="size"/> is the
        /// unrotated (w, h); <paramref name="rotation"/> is in radians (clockwise in screen space, y-down,
        /// matching <c>atan2</c> of a screen-space edge); src = (u0, v0, u1, v1) in 0..1. At rotation 0 with
        /// origin (0, 0) and size (w, h) this is identical to <c>Draw(tex, (x, y, w, h), srcUV, color)</c>.
        /// </summary>
        public void Draw(Texture2D tex, Vector2 position, Vector2 size, Vector2 originNormalized, float rotation, Vector4 srcUV, Vector4 color)
        {
            float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);
            EmitQuad(tex,
                RotatedCorner(0f, 0f, position, size, originNormalized, cos, sin),
                RotatedCorner(1f, 0f, position, size, originNormalized, cos, sin),
                RotatedCorner(1f, 1f, position, size, originNormalized, cos, sin),
                RotatedCorner(0f, 1f, position, size, originNormalized, cos, sin),
                srcUV, color);
        }

        // One quad corner in world space: rotate the local offset of normalized corner (cx, cy) about the pivot.
        // Internal so the rotated-corner geometry is unit-testable without a GPU.
        internal static Vector2 RotatedCorner(float cx, float cy, Vector2 position, Vector2 size, Vector2 origin, float cos, float sin)
        {
            float lx = (cx - origin.X) * size.X, ly = (cy - origin.Y) * size.Y;
            return new Vector2(position.X + (lx * cos - ly * sin), position.Y + (lx * sin + ly * cos));
        }

        // Add two triangles (tl, tr, br + tl, br, bl) for the given world-space corners with the matching UVs.
        // Both the axis-aligned and rotated Draw overloads funnel through here so z-order + scissor are shared.
        void EmitQuad(Texture2D tex, Vector2 worldTL, Vector2 worldTR, Vector2 worldBR, Vector2 worldBL, Vector4 srcUV, Vector4 color)
        {
            Vector2 tl = Clip(worldTL.X, worldTL.Y), tr = Clip(worldTR.X, worldTR.Y), br = Clip(worldBR.X, worldBR.Y), bl = Clip(worldBL.X, worldBL.Y);
            var uTL = new Vector2(srcUV.X, srcUV.Y); var uTR = new Vector2(srcUV.Z, srcUV.Y);
            var uBR = new Vector2(srcUV.Z, srcUV.W); var uBL = new Vector2(srcUV.X, srcUV.W);
            var key = tex.Handle;
            _runs.Add(key, new V { Pos = tl, Uv = uTL, Color = color }); _runs.Add(key, new V { Pos = tr, Uv = uTR, Color = color }); _runs.Add(key, new V { Pos = br, Uv = uBR, Color = color });
            _runs.Add(key, new V { Pos = tl, Uv = uTL, Color = color }); _runs.Add(key, new V { Pos = br, Uv = uBR, Color = color }); _runs.Add(key, new V { Pos = bl, Uv = uBL, Color = color });
        }

        /// <summary>Draw <paramref name="text"/> with its top-left at <paramref name="position"/>.</summary>
        public void DrawString(SpriteFont font, string text, Vector2 position, Vector4 color)
        {
            float k = font.RenderScale; // atlas texels -> logical pixels (glyphs are baked at oversample density)
            float penX = position.X, baseline = position.Y + font.Ascent;
            foreach (char c in text)
            {
                if (!font.Glyphs.TryGetValue(c, out var g)) continue;
                if (g.W > 0 && g.H > 0)
                {
                    var dest = new Vector4(penX + g.XOff * k, baseline + g.YOff * k, g.W * k, g.H * k);
                    var uv = new Vector4((float)g.Ax / font.AtlasW, (float)g.Ay / font.AtlasH,
                                         (float)(g.Ax + g.W) / font.AtlasW, (float)(g.Ay + g.H) / font.AtlasH);
                    Draw(font.Atlas, dest, uv, color);
                }
                penX += g.Advance;
            }
        }

        /// <summary>Flush the current batch.</summary>
        public void End() => Flush();

        /// <summary>
        /// Draw the accumulated runs (in submission order) and clear them, keeping the current transform and
        /// scissor. Used by <see cref="End"/> and by the scissor calls so a clip can be applied mid-batch
        /// without a <see cref="Begin"/> that would reset the design-viewport transform.
        /// </summary>
        void Flush()
        {
            var f = _gd.Factory;

            // Total vertex count across all non-empty runs; size the one persistent buffer for the whole frame.
            int totalCount = 0;
            foreach (var (_, verts) in _runs.Runs)
                totalCount += verts.Count;
            if (totalCount == 0) { _runs.Reset(); return; }

            // A dedicated buffer for THIS flush so a prior flush's pending Draw isn't overwritten.
            IGpuBuffer vb = AcquireFlushBuffer((uint)totalCount * VertexSizeBytes);

            _cl.SetPipeline(_pipeline);
            uint byteOffset = 0;
            uint vertexStart = 0;
            foreach (var (key, verts) in _runs.Runs)
            {
                if (verts.Count == 0) continue;
                var tex = (IGpuTexture)key;
                // Upload directly from the run's backing List<V> — no ToArray() copy.
                _gd.UpdateBuffer(vb, byteOffset, (ReadOnlySpan<V>)CollectionsMarshal.AsSpan(verts));
                if (!_sets.TryGetValue(tex, out var set))
                {
                    set = f.CreateResourceSet(new GpuResourceSetDescription(_layout, tex, _sampler));
                    _sets[tex] = set;
                }
                _cl.SetGraphicsResourceSet(0, set);
                _cl.SetVertexBuffer(0, vb);
                // Draw(vertexCount, instanceCount, vertexStart, instanceStart): the run's offset is vertexStart.
                _cl.Draw((uint)verts.Count, 1, vertexStart, 0);
                byteOffset += (uint)verts.Count * VertexSizeBytes;
                vertexStart += (uint)verts.Count;
            }
            _runs.Reset();
        }

        // The vertex buffer for the current flush index this frame, grown to fit. Each flush in a frame gets its own
        // buffer (see _vbs); they persist across frames and only grow.
        IGpuBuffer AcquireFlushBuffer(uint bytesNeeded)
        {
            int i = _flushIndex++;
            while (_vbs.Count <= i) { _vbs.Add(null!); _vbCaps.Add(0); }
            if (_vbs[i] == null || _vbCaps[i] < bytesNeeded)
            {
                _vbs[i]?.Dispose();
                uint cap = Math.Max(bytesNeeded, _vbCaps[i] == 0 ? 4096u : _vbCaps[i] * 2);
                _vbs[i] = _gd.Factory.CreateBuffer(new GpuBufferDescription(cap, GpuBufferUsage.VertexBuffer));
                _vbCaps[i] = cap;
            }
            return _vbs[i];
        }

        Vector2 Clip(float x, float y)
        {
            var v = Vector4.Transform(new Vector4(x, y, 0, 1), _vp);
            return new Vector2(v.X, v.Y);
        }

        void ResetBatches() => _runs.Reset();

        public void Dispose()
        {
            foreach (var vb in _vbs) vb?.Dispose();
            foreach (var s in _sets.Values) s.Dispose();
            _pipeline.Dispose(); _layout.Dispose();
            _shaders.Dispose();
        }
    }
}
