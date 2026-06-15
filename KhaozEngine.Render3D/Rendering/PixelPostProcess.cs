using System;
using System.Numerics;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The toggleable fullscreen post chain on the low-res target:
    /// palette quantization (+ Bayer dither) -> depth/normal edge outline -> point-upscale to the swapchain.
    /// Stages ping-pong between PingA/PingB so no pass reads its own output.
    /// </summary>
    internal sealed class PixelPostProcess : IDisposable
    {
        struct EdgeUbo { public Vector4 OutlineColor; public Vector4 Texel; public Vector4 Thresh; }

        readonly GraphicsDevice _gd;
        readonly Shader[] _palFrag, _edgeFrag, _blitFrag;
        readonly ResourceLayout _palLayout, _edgeLayout, _blitLayout;
        readonly Pipeline _palPipe, _edgePipe, _blitPipe;
        readonly DeviceBuffer _palBuf, _edgeBuf;

        ResourceSet _paletteSet = null!;
        ResourceSet _edgeFromColor = null!, _edgeFromPingA = null!;
        ResourceSet _blitColorP = null!, _blitPingAP = null!, _blitPingBP = null!; // point sampler
        ResourceSet _blitColorL = null!, _blitPingAL = null!, _blitPingBL = null!; // linear sampler
        RenderResources? _bound;

        public PixelPostProcess(GraphicsDevice gd, OutputDescription pingOutput, OutputDescription swapchainOutput)
        {
            _gd = gd;
            var f = gd.ResourceFactory;

            _palBuf = f.CreateBuffer(new BufferDescription(1040, BufferUsage.UniformBuffer)); // 64 vec4 + 1 vec4
            _edgeBuf = f.CreateBuffer(new BufferDescription(48, BufferUsage.UniformBuffer));

            // Each pass is its own vert+frag pair (FullscreenVert is the shared vertex source).
            _palFrag = Pair(f, ShaderSources.PaletteFrag);
            _edgeFrag = Pair(f, ShaderSources.EdgeFrag);
            _blitFrag = Pair(f, ShaderSources.BlitFrag);

            _palLayout = f.CreateResourceLayout(new ResourceLayoutDescription(
                T("Src"), S("Samp"), U("Pal")));
            _edgeLayout = f.CreateResourceLayout(new ResourceLayoutDescription(
                T("ColorTex"), T("NormalTex"), T("DepthTex"), S("Samp"), U("Edge")));
            _blitLayout = f.CreateResourceLayout(new ResourceLayoutDescription(
                T("Src"), S("Samp")));

            _palPipe = FullscreenPipeline(f, _palFrag, _palLayout, pingOutput);
            _edgePipe = FullscreenPipeline(f, _edgeFrag, _edgeLayout, pingOutput);
            _blitPipe = FullscreenPipeline(f, _blitFrag, _blitLayout, swapchainOutput);
        }

        static ResourceLayoutElementDescription T(string n) => new(n, ResourceKind.TextureReadOnly, ShaderStages.Fragment);
        static ResourceLayoutElementDescription S(string n) => new(n, ResourceKind.Sampler, ShaderStages.Fragment);
        static ResourceLayoutElementDescription U(string n) => new(n, ResourceKind.UniformBuffer, ShaderStages.Fragment);

        static Shader[] Pair(ResourceFactory f, string frag) => f.CreateFromSpirv(
            new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(ShaderSources.FullscreenVert), "main"),
            new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(frag), "main"));

        Pipeline FullscreenPipeline(ResourceFactory f, Shader[] shaders, ResourceLayout layout, OutputDescription outputs) =>
            f.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = new ShaderSetDescription(Array.Empty<VertexLayoutDescription>(), shaders),
                Outputs = outputs,
            });

        /// <summary>Build the per-target resource sets. Call on construction and whenever the targets resize.</summary>
        public void BindTargets(RenderResources res)
        {
            if (ReferenceEquals(_bound, res) && res.Width == _boundW && res.Height == _boundH) return;
            DisposeSets();
            var f = _gd.ResourceFactory;
            var samp = _gd.PointSampler;

            var lin = _gd.LinearSampler;
            _paletteSet = f.CreateResourceSet(new ResourceSetDescription(_palLayout, res.ColorTex, samp, _palBuf));
            _edgeFromColor = f.CreateResourceSet(new ResourceSetDescription(_edgeLayout, res.ColorTex, res.NormalTex, res.DepthColorTex, samp, _edgeBuf));
            _edgeFromPingA = f.CreateResourceSet(new ResourceSetDescription(_edgeLayout, res.PingA, res.NormalTex, res.DepthColorTex, samp, _edgeBuf));
            _blitColorP = f.CreateResourceSet(new ResourceSetDescription(_blitLayout, res.ColorTex, samp));
            _blitPingAP = f.CreateResourceSet(new ResourceSetDescription(_blitLayout, res.PingA, samp));
            _blitPingBP = f.CreateResourceSet(new ResourceSetDescription(_blitLayout, res.PingB, samp));
            _blitColorL = f.CreateResourceSet(new ResourceSetDescription(_blitLayout, res.ColorTex, lin));
            _blitPingAL = f.CreateResourceSet(new ResourceSetDescription(_blitLayout, res.PingA, lin));
            _blitPingBL = f.CreateResourceSet(new ResourceSetDescription(_blitLayout, res.PingB, lin));
            _bound = res; _boundW = res.Width; _boundH = res.Height;
        }
        int _boundW, _boundH;

        /// <summary>Upload post UBOs. Call BEFORE any SetFramebuffer this frame (no active render pass).</summary>
        public void PrepareUniforms(CommandList cl, RenderResources res, PixelPostProcessSettings s)
        {
            var pal = new float[260];
            int count = Math.Min(s.ActivePalette.Colors.Length, 64);
            for (int i = 0; i < count; i++)
            {
                var c = s.ActivePalette.Colors[i];
                pal[i * 4 + 0] = c.X; pal[i * 4 + 1] = c.Y; pal[i * 4 + 2] = c.Z; pal[i * 4 + 3] = c.W;
            }
            pal[256] = count; pal[257] = s.Dither ? 1f : 0f;
            cl.UpdateBuffer(_palBuf, 0, pal);

            var edge = new EdgeUbo
            {
                OutlineColor = s.OutlineColor,
                Texel = new Vector4(1f / res.Width, 1f / res.Height, 0, 0),
                Thresh = new Vector4(s.OutlineDepthThreshold, s.OutlineNormalThreshold, 0, 0),
            };
            cl.UpdateBuffer(_edgeBuf, 0, ref edge);
        }

        public void Run(CommandList cl, RenderResources res, Framebuffer swapchainFB, PixelPostProcessSettings s)
        {
            Texture src = res.ColorTex;

            if (s.Quantize)
            {
                cl.SetFramebuffer(res.PingAFB);
                cl.SetPipeline(_palPipe);
                cl.SetGraphicsResourceSet(0, _paletteSet);
                cl.Draw(3);
                src = res.PingA;
            }

            if (s.Outline)
            {
                bool fromColor = ReferenceEquals(src, res.ColorTex);
                cl.SetFramebuffer(fromColor ? res.PingAFB : res.PingBFB);
                cl.SetPipeline(_edgePipe);
                cl.SetGraphicsResourceSet(0, fromColor ? _edgeFromColor : _edgeFromPingA);
                cl.Draw(3);
                src = fromColor ? res.PingA : res.PingB;
            }

            ResourceSet blit = s.Pixelated
                ? (ReferenceEquals(src, res.ColorTex) ? _blitColorP : ReferenceEquals(src, res.PingA) ? _blitPingAP : _blitPingBP)
                : (ReferenceEquals(src, res.ColorTex) ? _blitColorL : ReferenceEquals(src, res.PingA) ? _blitPingAL : _blitPingBL);
            cl.SetFramebuffer(swapchainFB);
            cl.ClearColorTarget(0, RgbaFloat.Black);
            cl.SetPipeline(_blitPipe);
            cl.SetGraphicsResourceSet(0, blit);
            cl.Draw(3);
        }

        void DisposeSets()
        {
            _paletteSet?.Dispose(); _edgeFromColor?.Dispose(); _edgeFromPingA?.Dispose();
            _blitColorP?.Dispose(); _blitPingAP?.Dispose(); _blitPingBP?.Dispose();
            _blitColorL?.Dispose(); _blitPingAL?.Dispose(); _blitPingBL?.Dispose();
        }

        public void Dispose()
        {
            DisposeSets();
            _palPipe.Dispose(); _edgePipe.Dispose(); _blitPipe.Dispose();
            _palLayout.Dispose(); _edgeLayout.Dispose(); _blitLayout.Dispose();
            foreach (var sh in _palFrag) sh.Dispose();
            foreach (var sh in _edgeFrag) sh.Dispose();
            foreach (var sh in _blitFrag) sh.Dispose();
            _palBuf.Dispose(); _edgeBuf.Dispose();
        }
    }
}
