using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Batched 2D sprite + text renderer. Corners are transformed to clip space on the CPU by the current
    /// camera, so there is no per-batch uniform; quads are coalesced into submission-ordered runs (consecutive
    /// same-texture draws share a run) so painter's order is preserved across textures.
    /// </summary>
    public sealed class SpriteBatch : IDisposable
    {
        // internal (not private) so the engine's device-free ShaderValidation tests can validate this 2D pair
        // without a GraphicsDevice, via the existing InternalsVisibleTo into KhaozEngine.Tests. Not public.
        internal const string VertSrc = @"#version 450
layout(location=0) in vec2 ClipPos;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 Local;
layout(location=4) in vec4 Shape;
layout(location=5) in vec2 Mode;
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vColor;
layout(location=2) out vec2 vLocal;
layout(location=3) out vec4 vShape;
layout(location=4) out vec2 vMode;
void main() {
    gl_Position = vec4(ClipPos, 0.0, 1.0);
    vUv = Uv; vColor = Color; vLocal = Local; vShape = Shape; vMode = Mode;
}";

        internal const string FragSrc = @"#version 450
layout(set=0, binding=0) uniform texture2D Tex;
layout(set=0, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vColor;
layout(location=2) in vec2 vLocal;
layout(location=3) in vec4 vShape;
layout(location=4) in vec2 vMode;
layout(location=0) out vec4 oColor;
void main() {
    vec4 base = texture(sampler2D(Tex, Samp), vUv) * vColor;
    // Compute the rounded-box SDF coverage UNCONDITIONALLY so fwidth() stays in uniform control flow
    // (derivatives in a non-uniform branch are undefined per spec; Vulkan/lavapipe is strict). The
    // plain-draw branch below still writes the literal `base`, so output is byte-identical for Mode.y=0.
    vec2 b = vShape.xy;
    float r = vShape.z;
    float soft = vShape.w;
    float stroke = vMode.x;
    vec2 q = abs(vLocal) - b + r;
    float d = min(max(q.x, q.y), 0.0) + length(max(q, vec2(0.0))) - r;
    float dStroke = abs(d) - stroke * 0.5;
    d = stroke > 0.0 ? dStroke : d;
    float aa = soft > 0.0 ? soft : max(fwidth(d), 1e-4);
    float cov = clamp(0.5 - d / aa, 0.0, 1.0);
    if (vMode.y < 0.5) {
        oColor = base;
    } else {
        base.a *= cov;
        oColor = base;
    }
}";

        struct V
        {
            public Vector2 Pos; public Vector2 Uv; public Vector4 Color;
            public Vector2 Local; public Vector4 Shape; public Vector2 Mode;
        }

        readonly IGpuDevice _gd;
        readonly IGpuResourceLayout _layout;
        readonly IGpuPipeline _pipeline;          // alpha (source-over) - the default
        readonly IGpuPipeline _additivePipeline;  // additive (glowy VFX)
        readonly IGpuShaderSet _shaders;
        readonly IGpuSampler _linearSampler;
        readonly IGpuSampler _pointSampler;
        IGpuSampler _sampler;   // the sampler for the current Begin..End pass (Linear by default)
        // Keyed by (texture, sampler): a texture drawn under both Linear and Point in one frame needs a set each.
        readonly Dictionary<(IGpuTexture Tex, IGpuSampler Samp), IGpuResourceSet> _sets = new();
        readonly QuadRunBuilder<V> _runs = new();

        // The current draw blend mode (reset to Alpha by each Begin). Read at EmitQuad time so it can change
        // per quad within a batch.
        BlendMode _blend = BlendMode.Alpha;

        // A stable per-texture wrapper used as the run key for ADDITIVE draws, so an additive quad never coalesces
        // with an alpha quad of the same texture and Flush can pick the right pipeline. The alpha path keeps using
        // the raw texture handle as the key (unchanged, so existing output is byte-identical). Cached per handle to
        // keep additive draws zero-alloc after warm-up.
        sealed class AdditiveKey { public readonly IGpuTexture Tex; public AdditiveKey(IGpuTexture t) => Tex = t; }
        readonly Dictionary<IGpuTexture, AdditiveKey> _additiveKeys = new();

        // The (texture,sampler) resource-set cache (_sets) and the per-texture _additiveKeys are created on first
        // draw of a texture and, without eviction, are only freed at Dispose. A long-lived batch (one per surface,
        // app-lifetime) that streams many distinct textures (sprite streaming, level reloads) would then leak one
        // ResourceSet per (texture,sampler) ever drawn - plus a dangling reference to each texture the game later
        // disposed. So: stamp each texture's last-drawn frame and, in NewFrame, dispose the sets for any texture not
        // drawn within SetEvictAfterFrames (recreated on next draw if it returns). Bounds the cache to the recent
        // working set. A monotonic frame counter (NewFrame); _texLastUsedFrame holds only textures still in-window.
        long _frame;
        readonly Dictionary<IGpuTexture, long> _texLastUsedFrame = new();
        /// <summary>A (texture,sampler) set unused for this many frames is disposed (recreated on next draw). ~10s
        /// at 60fps. Settable for tests.</summary>
        internal int SetEvictAfterFrames = 600;
        readonly List<IGpuTexture> _evictScratch = new();
        /// <summary>Live (texture,sampler) resource sets currently cached. For tests.</summary>
        internal int CachedSetCount => _sets.Count;

        const uint VertexSizeBytes = 64;       // V = Pos(8)+Uv(8)+Color(16)+Local(8)+Shape(16)+Mode(8)
        // One growable vertex buffer PER (frame-ring-slot, flush-within-frame). Two hazards drive this shape:
        //  - WITHIN a frame, a buffer referenced by an already-recorded Draw must not be overwritten before the GPU
        //    runs that Draw, so every flush (each SetScissor/ClearScissor forces one) gets its own buffer, never one
        //    reused at offset 0.
        //  - ACROSS frames, the loop submits+presents with NO WaitForIdle, so the CPU runs ahead and a later frame's
        //    write to a reused buffer can race the GPU still reading it for an earlier, in-flight present: a 1-frame
        //    tear that only surfaces when the buffer contents change frame-to-frame (a moving/resizing widget; static
        //    content writes identical bytes so the race is invisible). So the per-flush buffers are RING-BUFFERED:
        //    NewFrame advances to the next of RingDepth slots and a slot is not rewritten until RingDepth frames
        //    later, by which point its prior GPU reads have retired (this also makes the grow-time Dispose safe).
        // Buffers persist across frames (only grow); _flushIndex resets each NewFrame; the ring slot is _frame % RingDepth.
        const int RingDepth = 3;               // triple-buffered: safe while the CPU runs up to RingDepth-1 frames ahead
        readonly List<IGpuBuffer>[] _vbRing;
        readonly List<uint>[] _vbCapRing;
        int _flushIndex;

        IGpuCommandList _cl = null!;
        int _vw, _vh;
        Matrix4x4 _vp;
        IDesignViewport? _viewport;   // active design viewport (set by Begin(IDesignViewport)), else null

        // Device-pixel snapping frame for the active pass: device px per authoring unit (per axis) + the device
        // offset of the authoring origin. Vector2.Zero means the current space is NOT device-pixel-snappable
        // (world/camera, screen space, a transformed pass, or a fractional design viewport), so SnapRect/SnapLength
        // and text-origin snapping are all no-ops there. It is non-zero only inside a point-space UiViewport pass
        // (IDesignViewport.SnapsToDevicePixels), which confines snapping to the DPI-aware UI path and leaves
        // design-space / world rendering byte-identical.
        Vector2 _deviceScale;
        Vector2 _deviceOffset;

        internal SpriteBatch(IGpuDevice gd, GpuOutputDescription output)
        {
            _gd = gd;
            _vbRing = new List<IGpuBuffer>[RingDepth];
            _vbCapRing = new List<uint>[RingDepth];
            for (int i = 0; i < RingDepth; i++) { _vbRing[i] = new List<IGpuBuffer>(); _vbCapRing[i] = new List<uint>(); }
            var f = gd.Factory;
            _linearSampler = gd.LinearSampler;
            _pointSampler = gd.PointSampler;
            _sampler = _linearSampler;
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            _shaders = f.CreateShadersFromSpirv(VertSrc, FragSrc);
            var vl = new GpuVertexLayoutDescription(
                new GpuVertexElement("ClipPos", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Local", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Shape", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Mode", GpuVertexElementFormat.Float2));
            GpuPipelineDescription Describe(GpuBlendAttachment blend) => new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { blend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vl },
                Outputs = output,
            };
            _pipeline = f.CreateGraphicsPipeline(Describe(GpuBlendAttachment.AlphaBlend));
            _additivePipeline = f.CreateGraphicsPipeline(Describe(GpuBlendAttachment.Additive));
        }

        /// <summary>
        /// Compositing mode for subsequent draws (default <see cref="BlendMode.Alpha"/>). Each <c>Begin</c> resets
        /// this to <see cref="BlendMode.Alpha"/>; set it mid-batch to switch (e.g. <see cref="BlendMode.Additive"/>
        /// for glowy VFX) without a new <c>Begin</c>. Painter's order is preserved across blend modes.
        /// </summary>
        public BlendMode BlendMode { get => _blend; set => _blend = value; }

        /// <summary>
        /// Device pixels per authoring unit for the active pass, per axis (X, Y), or <see cref="Vector2.Zero"/> when
        /// the current space is not device-pixel-snappable (world/camera, screen, a transformed pass, or a fractional
        /// design viewport). Non-zero only inside a point-space <c>UiViewport</c> <see cref="Begin(IDesignViewport, SamplerMode)"/>.
        /// Drives <see cref="SnapRect"/> / <see cref="SnapLength"/> and the DPI-aware text-origin snapping in
        /// <see cref="DrawString(KhaozEngine.Render2D.SpriteFont, string, System.Numerics.Vector2, KhaozEngine.Primitives.Color)"/>.
        /// </summary>
        public Vector2 DeviceScale => _deviceScale;

        /// <summary>The device-pixel offset of the authoring origin for the active pass (0 for point-space UI). Pairs with <see cref="DeviceScale"/>.</summary>
        public Vector2 DeviceOffset => _deviceOffset;

        /// <summary>
        /// Snap a rect's edges to whole device pixels for the active pass, keeping it in authoring units. A no-op
        /// when <see cref="DeviceScale"/> is zero (a non-snappable pass), so it is always safe to call.
        /// </summary>
        public Rect SnapRect(Rect rect) => ViewportMath.SnapRectToDevice(rect, _deviceScale, _deviceOffset);

        /// <summary>
        /// Snap a length (e.g. a border thickness) to a whole number of device pixels, in authoring units, floored
        /// at <paramref name="minDevicePixels"/> (pass 1 so a hairline never rounds away). A no-op when
        /// <see cref="DeviceScale"/> is zero.
        /// </summary>
        public float SnapLength(float length, float minDevicePixels = 0f) =>
            ViewportMath.SnapLengthToDevice(length, _deviceScale.X, minDevicePixels);

        /// <summary>
        /// Convert a clip rect (in viewport points, top-left origin) to framebuffer pixels, scaling for DPI
        /// (e.g. 2x Retina) and clamping to the framebuffer. Pure function, unit-tested headlessly.
        /// </summary>
        public static (uint X, uint Y, uint Width, uint Height) ComputeScissor(
            Rect rect, int viewportW, int viewportH, int framebufferW, int framebufferH)
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
        /// As <see cref="ComputeScissor(Rect,int,int,int,int)"/>, but first maps a clip rect given in
        /// design space through <paramref name="viewport"/> (scale + letterbox offset) into window points. Pass
        /// a null viewport to treat <paramref name="rect"/> as already in window points. Pure / headless.
        /// </summary>
        public static (uint X, uint Y, uint Width, uint Height) ComputeScissor(
            Rect rect, IDesignViewport? viewport, int viewportW, int viewportH, int framebufferW, int framebufferH)
        {
            if (viewport != null)
            {
                var tl = viewport.DesignToScreen(new Vector2(rect.X, rect.Y));
                var br = viewport.DesignToScreen(new Vector2(rect.Right, rect.Bottom));
                rect = new Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
            }
            return ComputeScissor(rect, viewportW, viewportH, framebufferW, framebufferH);
        }

        /// <summary>
        /// Flush pending draws, then clip subsequent draws to <paramref name="rect"/>. When a design viewport is
        /// active (<see cref="Begin(IDesignViewport, SamplerMode)"/>) <paramref name="rect"/> is in design space and
        /// is mapped through it; otherwise it is in window points. Pair with <see cref="ClearScissor"/>. The
        /// current transform is preserved, so no <see cref="Begin(Camera2D, SamplerMode)"/> is needed around it.
        /// </summary>
        public void SetScissor(Rect rect)
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
            _frame++;
            EvictStaleSets();
        }

        // Dispose the cached resource set(s) for any texture not drawn within SetEvictAfterFrames, so the cache
        // tracks the recent working set instead of growing once per distinct texture ever drawn. Disposing a set
        // releases only the descriptor binding, never the texture (the game owns that), so it is safe even after the
        // game has disposed the texture. A returning texture rebuilds its set on the next Flush.
        void EvictStaleSets()
        {
            if (_texLastUsedFrame.Count == 0) return;
            long cutoff = _frame - SetEvictAfterFrames;
            _evictScratch.Clear();
            foreach (var kv in _texLastUsedFrame)
                if (kv.Value <= cutoff) _evictScratch.Add(kv.Key);
            foreach (IGpuTexture tex in _evictScratch)
            {
                if (_sets.Remove((tex, _linearSampler), out IGpuResourceSet? s1)) s1.Dispose();
                if (_sets.Remove((tex, _pointSampler), out IGpuResourceSet? s2)) s2.Dispose();
                _additiveKeys.Remove(tex);
                _texLastUsedFrame.Remove(tex);
            }
        }

        /// <summary>Begin a batch in world space through <paramref name="camera"/>, sampled per <paramref name="sampler"/>
        /// (default <see cref="SamplerMode.Linear"/>; pass <see cref="SamplerMode.Point"/> for crisp pixel art).</summary>
        public void Begin(Camera2D camera, SamplerMode sampler = SamplerMode.Linear) { _sampler = Resolve(sampler); _vp = Clip(camera.GetViewProjection(_vw, _vh)); _viewport = null; ResetBatches(); }

        /// <summary>Begin a batch in screen space (pixels, top-left origin), sampled per <paramref name="sampler"/>.</summary>
        public void Begin(SamplerMode sampler = SamplerMode.Linear) { _sampler = Resolve(sampler); _vp = Clip(Matrix4x4.CreateOrthographicOffCenter(0, _vw, _vh, 0, -1, 1)); _viewport = null; ResetBatches(); }

        /// <summary>
        /// Begin a batch in design space through <paramref name="viewport"/>: subsequent draws use design
        /// coordinates and are scaled, centered, and letterboxed to the current window for the viewport's mode.
        /// A scissor set while this is active (<see cref="SetScissor"/>) is mapped through the viewport too.
        /// Sampled per <paramref name="sampler"/> (default <see cref="SamplerMode.Linear"/>).
        /// </summary>
        public void Begin(IDesignViewport viewport, SamplerMode sampler = SamplerMode.Linear) { _sampler = Resolve(sampler); _vp = Clip(viewport.GetClipProjection(_vw, _vh)); _viewport = viewport; ResetBatches(); SetDeviceSpace(viewport); }

        /// <summary>
        /// Begin a batch in screen space (pixels, top-left origin) with a <paramref name="transform"/> applied to
        /// every draw before projection - so a whole composed group (a sprite + its overlaid text) rotates,
        /// scales, or translates as one. <see cref="DrawString(KhaozEngine.Render2D.SpriteFont, string, System.Numerics.Vector2, KhaozEngine.Primitives.Color)"/> has no rotation of its own, so a model transform
        /// here is how text tilts with its panel. Build <paramref name="transform"/> in screen space (e.g.
        /// rotate-about-a-pivot = <c>Translate(-p) * RotationZ(a) * Translate(p)</c>).
        /// </summary>
        public void Begin(Matrix4x4 transform, SamplerMode sampler = SamplerMode.Linear) { _sampler = Resolve(sampler); _vp = Clip(ComposeModelViewProjection(transform, Matrix4x4.CreateOrthographicOffCenter(0, _vw, _vh, 0, -1, 1))); _viewport = null; ResetBatches(); }

        /// <summary>
        /// As <see cref="Begin(Camera2D, SamplerMode)"/>, with a model <paramref name="transform"/> (in world
        /// space) applied to every draw before the camera's view-projection.
        /// </summary>
        public void Begin(Camera2D camera, Matrix4x4 transform, SamplerMode sampler = SamplerMode.Linear) { _sampler = Resolve(sampler); _vp = Clip(ComposeModelViewProjection(transform, camera.GetViewProjection(_vw, _vh))); _viewport = null; ResetBatches(); }

        /// <summary>
        /// As <see cref="Begin(IDesignViewport, SamplerMode)"/>, with a model <paramref name="transform"/>
        /// (in design space) applied to every draw before the viewport projection - so a HUD card tilts about its
        /// pivot while its panel, icon, and text move together. The viewport's scale/letterbox still applies on top.
        /// Note: a <see cref="SetScissor"/> set during this pass is mapped through the viewport but NOT through
        /// <paramref name="transform"/> (the GPU scissor is axis-aligned in framebuffer space), so clip a rotated
        /// card by its un-rotated design bounds.
        /// </summary>
        public void Begin(IDesignViewport viewport, Matrix4x4 transform, SamplerMode sampler = SamplerMode.Linear) { _sampler = Resolve(sampler); _vp = Clip(ComposeModelViewProjection(transform, viewport.GetClipProjection(_vw, _vh))); _viewport = viewport; ResetBatches(); }

        // Compose a model transform with a view-projection so a point maps as (p * model) * viewProjection. Pure /
        // headless-testable: getting the multiplication order backwards is the easy bug, so it has its own test.
        internal static Matrix4x4 ComposeModelViewProjection(Matrix4x4 model, Matrix4x4 viewProjection) => model * viewProjection;

        // Apply the live backend's clip-space-Y convention to the CPU-baked view-projection (corners are
        // transformed to clip space on the CPU at draw time, so the correction lands on _vp here). Identity on
        // Metal/D3D, flips clip-Y on inverted-Y backends (Vulkan). _vp is render-only; CPU world<->screen math
        // uses Camera2D.GetView, so it is unaffected. See KhaozEngine.Gpu.GpuClip.
        Matrix4x4 Clip(Matrix4x4 viewProjection) => GpuClip.Correct(viewProjection, _gd.Capabilities);

        IGpuSampler Resolve(SamplerMode mode) => mode == SamplerMode.Point ? _pointSampler : _linearSampler;

        public void Draw(Texture2D tex, Vector2 position, Color color) =>
            Draw(tex, new Vector4(position.X, position.Y, tex.Width, tex.Height), new Vector4(0, 0, 1, 1), color);

        /// <summary>dest = (x, y, w, h) in world units; whole-texture UV.</summary>
        public void Draw(Texture2D tex, Vector4 destRect, Color color) =>
            Draw(tex, destRect, new Vector4(0, 0, 1, 1), color);

        /// <summary>dest = (x, y, w, h) in world units; src = (u0, v0, u1, v1) in 0..1.</summary>
        public void Draw(Texture2D tex, Vector4 destRect, Vector4 srcUV, Color color)
        {
            float x = destRect.X, y = destRect.Y, w = destRect.Z, h = destRect.W;
            // Color drops to the vertex Vector4 layout via the implicit operator at the EmitQuad boundary.
            EmitQuad(tex, new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h), srcUV, color);
        }

        /// <summary>
        /// Vertical 2-tone fill: <paramref name="top"/> on the upper edge, <paramref name="bottom"/> on the lower
        /// edge, interpolated by the per-vertex colour. dest = (x, y, w, h); whole-texture UV. Plain (non-rounded).
        /// </summary>
        public void Draw(Texture2D tex, Vector4 destRect, Color top, Color bottom)
        {
            float x = destRect.X, y = destRect.Y, w = destRect.Z, h = destRect.W;
            EmitQuad(tex, new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h),
                new Vector4(0, 0, 1, 1), (Vector4)top, (Vector4)bottom,
                Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector4.Zero, Vector2.Zero);
        }

        /// <summary>
        /// Rounded-rect draw with optional vertical gradient, soft edge, and stroke. <paramref name="cornerRadius"/>
        /// in draw units; <paramref name="softness"/> 0 = crisp fwidth AA, &gt;0 = soft falloff (shadow/glow);
        /// <paramref name="strokeWidth"/> 0 = filled, &gt;0 = ring (border, needs roughly &gt;=1 draw-unit to be
        /// visible). <paramref name="inset"/> shrinks the SDF box by that many draw units on every side WITHOUT
        /// shrinking the quad, so the rasterised quad has fragments beyond the shape's <c>d=0</c> edge for a soft
        /// falloff to fade across; pass <c>inset &gt;= softness/2</c> for a glow/shadow bloom that resolves to zero
        /// before the quad edge instead of truncating at ~50% coverage on the flat edge (a hard rim). Default 0 =
        /// SDF box == quad (today's behaviour). Alpha-shaped by an SDF in the shared shader; batches with
        /// everything. Use the white texture for solid fills. Note: even <paramref name="cornerRadius"/> 0 takes
        /// the SDF path (square corners with AA), which is not byte-identical to and a touch costlier than the
        /// plain <see cref="Draw(Texture2D, Vector4, Color)"/> overloads; use those for a flat quad.
        /// </summary>
        public void DrawRounded(Texture2D tex, Vector4 destRect, Vector4 srcUV, Color top, Color bottom,
            float cornerRadius, float softness = 0f, float strokeWidth = 0f, float inset = 0f)
        {
            float x = destRect.X, y = destRect.Y, w = destRect.Z, h = destRect.W;
            var (lTL, lTR, lBR, lBL) = RoundedLocals(w, h);
            Vector4 shape = RoundedShape(w, h, cornerRadius, softness, inset);
            Vector2 mode = RoundedMode(strokeWidth);
            EmitQuad(tex, new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h),
                srcUV, (Vector4)top, (Vector4)bottom, lTL, lTR, lBR, lBL, shape, mode);
        }

        /// <summary>Rounded-rect convenience: single colour, whole-texture UV.</summary>
        public void DrawRounded(Texture2D tex, Vector4 destRect, Color color,
            float cornerRadius, float softness = 0f, float strokeWidth = 0f, float inset = 0f) =>
            DrawRounded(tex, destRect, new Vector4(0, 0, 1, 1), color, color, cornerRadius, softness, strokeWidth, inset);

        // Typed Rect overloads: a Color and a destination rect can no longer be swapped at a call site. Reuses
        // Rect (x, y, w, h) for the rect; forwards to the Vector4-dest forms so the batch path is identical.
        public void Draw(Texture2D tex, Rect destRect, Color color) =>
            Draw(tex, new Vector4(destRect.X, destRect.Y, destRect.Width, destRect.Height), color);

        /// <summary>dest in world units; src = (u0, v0, u1, v1) in 0..1.</summary>
        public void Draw(Texture2D tex, Rect destRect, Vector4 srcUV, Color color) =>
            Draw(tex, new Vector4(destRect.X, destRect.Y, destRect.Width, destRect.Height), srcUV, color);

        /// <summary>
        /// Draw a rotated quad. <paramref name="position"/> is the world point where the pivot
        /// (<paramref name="originNormalized"/>, in [0,1] of the quad) lands; <paramref name="size"/> is the
        /// unrotated (w, h); <paramref name="rotation"/> is in radians (clockwise in screen space, y-down,
        /// matching <c>atan2</c> of a screen-space edge); src = (u0, v0, u1, v1) in 0..1. At rotation 0 with
        /// origin (0, 0) and size (w, h) this is identical to <c>Draw(tex, (x, y, w, h), srcUV, color)</c>.
        /// </summary>
        public void Draw(Texture2D tex, Vector2 position, Vector2 size, Vector2 originNormalized, float rotation, Vector4 srcUV, Color color)
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
            // Plain path: zero SDF fields, single colour on all four corners, Mode.y = 0 (disabled).
            EmitQuad(tex, worldTL, worldTR, worldBR, worldBL, srcUV, color, color,
                Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector4.Zero, Vector2.Zero);
        }

        // Full emit: per-corner colour (top vs bottom for gradients) + per-corner Local + shared Shape/Mode.
        void EmitQuad(Texture2D tex, Vector2 worldTL, Vector2 worldTR, Vector2 worldBR, Vector2 worldBL,
            Vector4 srcUV, Vector4 colorTop, Vector4 colorBottom,
            Vector2 localTL, Vector2 localTR, Vector2 localBR, Vector2 localBL, Vector4 shape, Vector2 mode)
        {
            Vector2 tl = Clip(worldTL.X, worldTL.Y), tr = Clip(worldTR.X, worldTR.Y), br = Clip(worldBR.X, worldBR.Y), bl = Clip(worldBL.X, worldBL.Y);
            var uTL = new Vector2(srcUV.X, srcUV.Y); var uTR = new Vector2(srcUV.Z, srcUV.Y);
            var uBR = new Vector2(srcUV.Z, srcUV.W); var uBL = new Vector2(srcUV.X, srcUV.W);
            // Alpha keeps the raw handle as the key (unchanged path); additive uses a stable per-texture wrapper so
            // it never merges with an alpha run of the same texture and Flush can choose the additive pipeline.
            object key = _blend == BlendMode.Alpha ? tex.Handle : AdditiveKeyFor(tex.Handle);
            V vtl = new V { Pos = tl, Uv = uTL, Color = colorTop, Local = localTL, Shape = shape, Mode = mode };
            V vtr = new V { Pos = tr, Uv = uTR, Color = colorTop, Local = localTR, Shape = shape, Mode = mode };
            V vbr = new V { Pos = br, Uv = uBR, Color = colorBottom, Local = localBR, Shape = shape, Mode = mode };
            V vbl = new V { Pos = bl, Uv = uBL, Color = colorBottom, Local = localBL, Shape = shape, Mode = mode };
            _runs.Add(key, vtl); _runs.Add(key, vtr); _runs.Add(key, vbr);
            _runs.Add(key, vtl); _runs.Add(key, vbr); _runs.Add(key, vbl);
        }

        /// <summary>The four rect-local corner offsets (TL, TR, BR, BL) from the centre of a w x h rect. Pure / headless.</summary>
        internal static (Vector2 TL, Vector2 TR, Vector2 BR, Vector2 BL) RoundedLocals(float w, float h)
        {
            float hx = w * 0.5f, hy = h * 0.5f;
            return (new Vector2(-hx, -hy), new Vector2(hx, -hy), new Vector2(hx, hy), new Vector2(-hx, hy));
        }

        /// <summary>
        /// Packs the SDF Shape attribute = (halfX, halfY, radius, softness). <paramref name="inset"/> shrinks the
        /// half-extents on every side so the SDF box sits inside a larger quad (used by soft glows/shadows so the
        /// falloff has room to fade to zero within the quad). Pure / headless.
        /// </summary>
        internal static Vector4 RoundedShape(float w, float h, float radius, float softness, float inset = 0f) =>
            new Vector4(w * 0.5f - inset, h * 0.5f - inset, radius, softness);

        /// <summary>Packs the Mode attribute = (strokeWidth, 1). Pure / headless. modeFlag 1 enables the SDF branch.</summary>
        internal static Vector2 RoundedMode(float strokeWidth) => new Vector2(strokeWidth, 1f);

        AdditiveKey AdditiveKeyFor(IGpuTexture tex)
        {
            if (!_additiveKeys.TryGetValue(tex, out var k)) { k = new AdditiveKey(tex); _additiveKeys[tex] = k; }
            return k;
        }

        /// <summary>Draw <paramref name="text"/> with its top-left at <paramref name="position"/>.</summary>
        public void DrawString(SpriteFont font, string text, Vector2 position, Color color) =>
            DrawString(font, text, position, color, 1f);

        /// <summary>
        /// Draw <paramref name="text"/> with its top-left at <paramref name="position"/>, uniformly scaled by
        /// <paramref name="scale"/> about that top-left corner. The whole layout (glyph size, offsets and
        /// advances, and the ascent baseline) scales together, so this matches a layout computed with
        /// <c>font.Measure(text) * scale</c> - the caller measures at <paramref name="scale"/> for positioning and
        /// draws at the same <paramref name="scale"/>. <c>scale = 1</c> is the unscaled path.
        /// </summary>
        public void DrawString(SpriteFont font, string text, Vector2 position, Color color, float scale)
        {
            // atlas texels -> logical pixels (glyphs are baked at the bake density), then the caller's scale.
            float k = font.RenderScale * scale;
            float penX = position.X, baseline = position.Y + font.Ascent * scale;
            // In a point-space UI pass, snap each glyph's top-left to a whole device pixel: with a DpiFont atlas baked
            // 1:1 for the device (integer texel offsets), a device-aligned origin lands the whole glyph on the pixel
            // grid, so text is crisp instead of bilinear-blurred across a fractional phase. No-op elsewhere.
            bool snap = _deviceScale.X > 0f;
            foreach (char c in text)
            {
                if (!font.Glyphs.TryGetValue(c, out var g)) continue;
                if (g.W > 0 && g.H > 0)
                {
                    float gx = penX + g.XOff * k, gy = baseline + g.YOff * k;
                    if (snap)
                    {
                        gx = ViewportMath.SnapToDevicePixel(gx, _deviceScale.X, _deviceOffset.X);
                        gy = ViewportMath.SnapToDevicePixel(gy, _deviceScale.Y, _deviceOffset.Y);
                    }
                    var dest = new Vector4(gx, gy, g.W * k, g.H * k);
                    var uv = new Vector4((float)g.Ax / font.AtlasW, (float)g.Ay / font.AtlasH,
                                         (float)(g.Ax + g.W) / font.AtlasW, (float)(g.Ay + g.H) / font.AtlasH);
                    Draw(font.Atlas, dest, uv, color);
                }
                penX += g.Advance * scale;
            }
        }

        /// <summary>Flush the current batch.</summary>
        public void End() => Flush();

        /// <summary>
        /// Draw the accumulated runs (in submission order) and clear them, keeping the current transform and
        /// scissor. Used by <see cref="End"/> and by the scissor calls so a clip can be applied mid-batch
        /// without a <see cref="Begin(KhaozEngine.Render2D.Camera2D, KhaozEngine.Render2D.SamplerMode)"/> that would reset the design-viewport transform.
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

            uint byteOffset = 0;
            uint vertexStart = 0;
            foreach (var (key, verts) in _runs.Runs)
            {
                if (verts.Count == 0) continue;
                // An additive run is keyed by an AdditiveKey wrapper; everything else is a raw texture handle (alpha).
                IGpuTexture tex; IGpuPipeline pipeline;
                if (key is AdditiveKey ak) { tex = ak.Tex; pipeline = _additivePipeline; }
                else { tex = (IGpuTexture)key; pipeline = _pipeline; }
                _texLastUsedFrame[tex] = _frame;   // stamp for the unused-set eviction sweep in NewFrame
                _cl.SetPipeline(pipeline);
                // Upload directly from the run's backing List<V>, no ToArray() copy.
                _gd.UpdateBuffer(vb, byteOffset, (ReadOnlySpan<V>)CollectionsMarshal.AsSpan(verts));
                var setKey = (tex, _sampler);
                if (!_sets.TryGetValue(setKey, out var set))
                {
                    set = f.CreateResourceSet(new GpuResourceSetDescription(_layout, tex, _sampler));
                    _sets[setKey] = set;
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
        // buffer within this frame's ring slot (see _vbRing); slots rotate per frame and only grow. A grow disposes
        // the slot's old buffer, which is safe because that buffer was last used RingDepth frames ago (reads retired).
        IGpuBuffer AcquireFlushBuffer(uint bytesNeeded)
        {
            List<IGpuBuffer> vbs = _vbRing[(int)(_frame % RingDepth)];
            List<uint> caps = _vbCapRing[(int)(_frame % RingDepth)];
            int i = _flushIndex++;
            while (vbs.Count <= i) { vbs.Add(null!); caps.Add(0); }
            if (vbs[i] == null || caps[i] < bytesNeeded)
            {
                vbs[i]?.Dispose();
                uint cap = Math.Max(bytesNeeded, caps[i] == 0 ? 4096u : caps[i] * 2);
                vbs[i] = _gd.Factory.CreateBuffer(new GpuBufferDescription(cap, GpuBufferUsage.VertexBuffer));
                caps[i] = cap;
            }
            return vbs[i];
        }

        /// <summary>The number of frame slots the per-flush vertex buffers rotate through (triple-buffered).</summary>
        internal int VertexBufferRingDepth => RingDepth;

        /// <summary>The vertex buffer backing flush <paramref name="flushIndex"/> of the CURRENT frame's ring slot
        /// (null before it has been allocated). Lets a test assert the cross-frame ring rotation.</summary>
        internal IGpuBuffer? CurrentFlushBuffer(int flushIndex)
        {
            List<IGpuBuffer> vbs = _vbRing[(int)(_frame % RingDepth)];
            return flushIndex >= 0 && flushIndex < vbs.Count ? vbs[flushIndex] : null;
        }

        Vector2 Clip(float x, float y)
        {
            var v = Vector4.Transform(new Vector4(x, y, 0, 1), _vp);
            return new Vector2(v.X, v.Y);
        }

        void ResetBatches() { _runs.Reset(); _blend = BlendMode.Alpha; _deviceScale = Vector2.Zero; _deviceOffset = Vector2.Zero; }

        // Arm device-pixel snapping for this pass iff the viewport is a point-space one (UiViewport). A fractional
        // design viewport, or any other Begin, leaves the frame cleared (Vector2.Zero) so snapping is a no-op.
        void SetDeviceSpace(IDesignViewport viewport)
        {
            if (viewport.SnapsToDevicePixels)
            {
                _deviceScale = new Vector2(viewport.ScaleX, viewport.ScaleY);
                _deviceOffset = new Vector2(viewport.OffsetX, viewport.OffsetY);
            }
        }

        public void Dispose()
        {
            foreach (List<IGpuBuffer> vbs in _vbRing)
                foreach (var vb in vbs) vb?.Dispose();
            foreach (var s in _sets.Values) s.Dispose();
            _pipeline.Dispose(); _additivePipeline.Dispose(); _layout.Dispose();
            _shaders.Dispose();
        }
    }
}
