using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;

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

        readonly GraphicsDevice _gd;
        readonly ResourceLayout _layout;
        readonly Pipeline _pipeline;
        readonly Shader[] _shaders;
        readonly Sampler _sampler;
        readonly Dictionary<Texture, ResourceSet> _sets = new();
        readonly QuadRunBuilder<V> _runs = new();
        readonly List<DeviceBuffer> _frameBuffers = new();

        CommandList _cl = null!;
        int _vw, _vh;
        Matrix4x4 _vp;

        internal SpriteBatch(GraphicsDevice gd, OutputDescription output)
        {
            _gd = gd;
            var f = gd.ResourceFactory;
            _sampler = gd.LinearSampler;
            _layout = f.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Tex", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("Samp", ResourceKind.Sampler, ShaderStages.Fragment)));
            _shaders = f.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(VertSrc), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragSrc), "main"));
            var vl = new VertexLayoutDescription(
                new VertexElementDescription("ClipPos", VertexElementSemantic.Position, VertexElementFormat.Float2),
                new VertexElementDescription("Uv", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Float4));
            _pipeline = f.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleAlphaBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = new ShaderSetDescription(new[] { vl }, _shaders),
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
        /// Clip subsequent draws to <paramref name="rect"/> (viewport points). Call between an <see cref="End"/>
        /// and the next <see cref="Begin"/>; pair with <see cref="ClearScissor"/> to restore the full viewport.
        /// </summary>
        public void SetScissor(Windowing.Rect rect)
        {
            var fb = _gd.MainSwapchain?.Framebuffer;
            int fbw = fb != null ? (int)fb.Width : _vw;
            int fbh = fb != null ? (int)fb.Height : _vh;
            var (x, y, w, h) = ComputeScissor(rect, _vw, _vh, fbw, fbh);
            _cl.SetScissorRect(0, x, y, w, h);
        }

        /// <summary>Reset the scissor to the full framebuffer (undo <see cref="SetScissor"/>).</summary>
        public void ClearScissor()
        {
            var fb = _gd.MainSwapchain?.Framebuffer;
            uint fbw = fb != null ? fb.Width : (uint)Math.Max(0, _vw);
            uint fbh = fb != null ? fb.Height : (uint)Math.Max(0, _vh);
            _cl.SetScissorRect(0, 0, 0, fbw, fbh);
        }

        // Called by the host/snapshot each frame before the user's draw callback.
        internal void NewFrame(CommandList cl, int viewportW, int viewportH)
        {
            _cl = cl; _vw = viewportW; _vh = viewportH;
            foreach (var b in _frameBuffers) b.Dispose();
            _frameBuffers.Clear();
        }

        /// <summary>Begin a batch in world space through <paramref name="camera"/>.</summary>
        public void Begin(Camera2D camera) { _vp = camera.GetViewProjection(_vw, _vh); ResetBatches(); }

        /// <summary>Begin a batch in screen space (pixels, top-left origin).</summary>
        public void Begin() { _vp = Matrix4x4.CreateOrthographicOffCenter(0, _vw, _vh, 0, -1, 1); ResetBatches(); }

        public void Draw(Texture2D tex, Vector2 position, Vector4 color) =>
            Draw(tex, new Vector4(position.X, position.Y, tex.Width, tex.Height), new Vector4(0, 0, 1, 1), color);

        public void Draw(Texture2D tex, Vector4 destRect, Vector4 color) =>
            Draw(tex, destRect, new Vector4(0, 0, 1, 1), color);

        /// <summary>dest = (x, y, w, h) in world units; src = (u0, v0, u1, v1) in 0..1.</summary>
        public void Draw(Texture2D tex, Vector4 destRect, Vector4 srcUV, Vector4 color)
        {
            float x = destRect.X, y = destRect.Y, w = destRect.Z, h = destRect.W;
            Vector2 tl = Clip(x, y), tr = Clip(x + w, y), br = Clip(x + w, y + h), bl = Clip(x, y + h);
            var uTL = new Vector2(srcUV.X, srcUV.Y); var uTR = new Vector2(srcUV.Z, srcUV.Y);
            var uBR = new Vector2(srcUV.Z, srcUV.W); var uBL = new Vector2(srcUV.X, srcUV.W);
            var key = tex.Handle;
            _runs.Add(key, new V { Pos = tl, Uv = uTL, Color = color }); _runs.Add(key, new V { Pos = tr, Uv = uTR, Color = color }); _runs.Add(key, new V { Pos = br, Uv = uBR, Color = color });
            _runs.Add(key, new V { Pos = tl, Uv = uTL, Color = color }); _runs.Add(key, new V { Pos = br, Uv = uBR, Color = color }); _runs.Add(key, new V { Pos = bl, Uv = uBL, Color = color });
        }

        /// <summary>Draw <paramref name="text"/> with its top-left at <paramref name="position"/>.</summary>
        public void DrawString(SpriteFont font, string text, Vector2 position, Vector4 color)
        {
            float penX = position.X, baseline = position.Y + font.Ascent;
            foreach (char c in text)
            {
                if (!font.Glyphs.TryGetValue(c, out var g)) continue;
                if (g.W > 0 && g.H > 0)
                {
                    var dest = new Vector4(penX + g.XOff, baseline + g.YOff, g.W, g.H);
                    var uv = new Vector4((float)g.Ax / font.AtlasW, (float)g.Ay / font.AtlasH,
                                         (float)(g.Ax + g.W) / font.AtlasW, (float)(g.Ay + g.H) / font.AtlasH);
                    Draw(font.Atlas, dest, uv, color);
                }
                penX += g.Advance;
            }
        }

        /// <summary>Flush the current batch.</summary>
        public void End()
        {
            var f = _gd.ResourceFactory;
            _cl.SetPipeline(_pipeline);
            foreach (var (key, verts) in _runs.Runs)
            {
                if (verts.Count == 0) continue;
                var tex = (Texture)key;
                var vb = f.CreateBuffer(new BufferDescription((uint)(verts.Count * 32), BufferUsage.VertexBuffer));
                _gd.UpdateBuffer(vb, 0, verts.ToArray());
                _frameBuffers.Add(vb);
                if (!_sets.TryGetValue(tex, out var set))
                {
                    set = f.CreateResourceSet(new ResourceSetDescription(_layout, tex, _sampler));
                    _sets[tex] = set;
                }
                _cl.SetGraphicsResourceSet(0, set);
                _cl.SetVertexBuffer(0, vb);
                _cl.Draw((uint)verts.Count);
            }
        }

        Vector2 Clip(float x, float y)
        {
            var v = Vector4.Transform(new Vector4(x, y, 0, 1), _vp);
            return new Vector2(v.X, v.Y);
        }

        void ResetBatches() => _runs.Reset();

        public void Dispose()
        {
            foreach (var b in _frameBuffers) b.Dispose();
            foreach (var s in _sets.Values) s.Dispose();
            _pipeline.Dispose(); _layout.Dispose();
            foreach (var sh in _shaders) sh.Dispose();
        }
    }
}
