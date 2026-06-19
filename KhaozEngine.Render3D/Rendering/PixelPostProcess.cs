using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
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
        struct FinalUbo { public Vector4 BgColor; public Vector4 Params; }

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _palFrag, _edgeFrag, _blitFrag;
        readonly IGpuResourceLayout _palLayout, _edgeLayout, _blitLayout;
        readonly IGpuPipeline _palPipe, _edgePipe, _blitPipe;
        readonly IGpuBuffer _palBuf, _edgeBuf, _finalBuf;

        IGpuResourceSet _paletteSet = null!;
        IGpuResourceSet _edgeFromColor = null!, _edgeFromPingA = null!;
        IGpuResourceSet _blitColorP = null!, _blitPingAP = null!, _blitPingBP = null!; // point sampler
        IGpuResourceSet _blitColorL = null!, _blitPingAL = null!, _blitPingBL = null!; // linear sampler
        RenderResources? _bound;
        readonly float[] _palScratch = new float[260]; // reused per frame: 64 vec4 palette + count/dither (+ pad)

        public PixelPostProcess(IGpuDevice gd, GpuOutputDescription pingOutput, GpuOutputDescription swapchainOutput)
        {
            _gd = gd;
            var f = gd.Factory;

            _palBuf = f.CreateBuffer(new GpuBufferDescription(1040, GpuBufferUsage.UniformBuffer)); // 64 vec4 + 1 vec4
            _edgeBuf = f.CreateBuffer(new GpuBufferDescription(48, GpuBufferUsage.UniformBuffer));
            _finalBuf = f.CreateBuffer(new GpuBufferDescription(32, GpuBufferUsage.UniformBuffer)); // 2 vec4

            // Each pass is its own vert+frag pair (FullscreenVert is the shared vertex source).
            _palFrag = Pair(f, ShaderSources.PaletteFrag);
            _edgeFrag = Pair(f, ShaderSources.EdgeFrag);
            _blitFrag = Pair(f, ShaderSources.BlitFrag);

            _palLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Pal")));
            _edgeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("ColorTex"), T("NormalTex"), T("DepthTex"), S("Samp"), U("Edge")));
            _blitLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Final")));

            _palPipe = FullscreenPipeline(f, _palFrag, _palLayout, pingOutput);
            _edgePipe = FullscreenPipeline(f, _edgeFrag, _edgeLayout, pingOutput);
            _blitPipe = FullscreenPipeline(f, _blitFrag, _blitLayout, swapchainOutput);
        }

        static GpuResourceLayoutElement T(string n) => new(n, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement S(string n) => new(n, GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement U(string n) => new(n, GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment);

        static IGpuShaderSet Pair(IGpuResourceFactory f, string frag) =>
            f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, frag);

        IGpuPipeline FullscreenPipeline(IGpuResourceFactory f, IGpuShaderSet shaders, IGpuResourceLayout layout, GpuOutputDescription outputs) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });

        /// <summary>Build the per-target resource sets. Call on construction and whenever the targets resize.</summary>
        public void BindTargets(RenderResources res)
        {
            if (ReferenceEquals(_bound, res) && res.Width == _boundW && res.Height == _boundH) return;
            DisposeSets();
            var f = _gd.Factory;
            var samp = _gd.PointSampler;

            var lin = _gd.LinearSampler;
            _paletteSet = f.CreateResourceSet(new GpuResourceSetDescription(_palLayout, res.ColorTex, samp, _palBuf));
            _edgeFromColor = f.CreateResourceSet(new GpuResourceSetDescription(_edgeLayout, res.ColorTex, res.NormalTex, res.DepthColorTex, samp, _edgeBuf));
            _edgeFromPingA = f.CreateResourceSet(new GpuResourceSetDescription(_edgeLayout, res.PingA, res.NormalTex, res.DepthColorTex, samp, _edgeBuf));
            _blitColorP = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, res.ColorTex, samp, _finalBuf));
            _blitPingAP = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, res.PingA, samp, _finalBuf));
            _blitPingBP = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, res.PingB, samp, _finalBuf));
            _blitColorL = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, res.ColorTex, lin, _finalBuf));
            _blitPingAL = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, res.PingA, lin, _finalBuf));
            _blitPingBL = f.CreateResourceSet(new GpuResourceSetDescription(_blitLayout, res.PingB, lin, _finalBuf));
            _bound = res; _boundW = res.Width; _boundH = res.Height;
        }
        int _boundW, _boundH;

        /// <summary>Upload post UBOs. Call BEFORE any SetFramebuffer this frame (no active render pass).</summary>
        public void PrepareUniforms(IGpuCommandList cl, RenderResources res, PixelPostProcessSettings s)
        {
            var pal = _palScratch;
            // Zero the 256-float palette region so stale colors from a larger previous palette don't leak.
            // (Indices 258..259 are pad and stay 0; they're never written.)
            Array.Clear(pal, 0, 256);
            int count = Math.Min(s.ActivePalette.Colors.Length, 64);
            for (int i = 0; i < count; i++)
            {
                var c = s.ActivePalette.Colors[i];
                pal[i * 4 + 0] = c.X; pal[i * 4 + 1] = c.Y; pal[i * 4 + 2] = c.Z; pal[i * 4 + 3] = c.W;
            }
            pal[256] = count; pal[257] = s.Dither ? 1f : 0f;
            cl.UpdateBuffer<float>(_palBuf, 0, pal);

            var edge = new EdgeUbo
            {
                OutlineColor = s.OutlineColor,
                Texel = new Vector4(1f / res.Width, 1f / res.Height, 0, 0),
                Thresh = new Vector4(s.OutlineDepthThreshold, s.OutlineNormalThreshold, 0, 0),
            };
            cl.UpdateBuffer(_edgeBuf, 0, in edge);

            var final = new FinalUbo
            {
                BgColor = s.BackgroundColor,
                Params = new Vector4(s.Starfield ? 1f : 0f, s.TransparentBackground ? 1f : 0f, 0, 0),
            };
            cl.UpdateBuffer(_finalBuf, 0, in final);
        }

        public void Run(IGpuCommandList cl, RenderResources res, IGpuFramebuffer swapchainFB, PixelPostProcessSettings s)
        {
            IGpuTexture src = res.ColorTex;

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

            IGpuResourceSet blit = s.Pixelated
                ? (ReferenceEquals(src, res.ColorTex) ? _blitColorP : ReferenceEquals(src, res.PingA) ? _blitPingAP : _blitPingBP)
                : (ReferenceEquals(src, res.ColorTex) ? _blitColorL : ReferenceEquals(src, res.PingA) ? _blitPingAL : _blitPingBL);
            cl.SetFramebuffer(swapchainFB);
            // Transparent clear when compositing offscreen, else opaque black. (The fullscreen blit overwrites
            // every pixel via OverrideBlend, so this mainly documents intent; the alpha is set in the shader.)
            cl.ClearColorTarget(0, s.TransparentBackground ? Vector4.Zero : new Vector4(0f, 0f, 0f, 1f));
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
            _palFrag.Dispose();
            _edgeFrag.Dispose();
            _blitFrag.Dispose();
            _palBuf.Dispose(); _edgeBuf.Dispose(); _finalBuf.Dispose();
        }
    }
}
