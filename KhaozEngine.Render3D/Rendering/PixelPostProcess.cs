using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
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
        // The two typed post UBOs (internal so UboLayoutTests can size-check them against the GPU allocations).
        internal struct EdgeUbo { public Vector4 OutlineColor; public Vector4 Texel; public Vector4 Thresh; public Vector4 Fade; }
        internal struct FinalUbo { public Vector4 BgColor; public Vector4 Params; }

        // Palette-quantize UBO sizing. The GLSL block is `vec4 Colors[MaxPaletteColors]; vec4 Info;` and the CPU
        // scratch mirrors it flat as floats. Named so UboLayoutTests can assert the scratch, the buffer, and the
        // GLSL array length all agree. (internal for the same reason.)
        internal const int MaxPaletteColors = 64;                         // GLSL: Colors[64]
        internal const int PaletteScratchFloats = (MaxPaletteColors + 1) * 4; // 64 colour vec4 + 1 info vec4 = 260 floats
        internal const uint PaletteBufferBytes = (uint)PaletteScratchFloats * sizeof(float); // 1040 bytes
        internal const uint EdgeBufferBytes = 64;                         // 4 vec4 (EdgeUbo)
        internal const uint FinalBufferBytes = 32;                        // 2 vec4 (FinalUbo)
        internal const uint FxaaBufferBytes = 16;                         // 1 vec4 (rcpFrame)

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _palFrag, _edgeFrag, _blitFrag, _fxaaFrag;
        readonly IGpuResourceLayout _palLayout, _edgeLayout, _blitLayout, _fxaaLayout;
        readonly IGpuPipeline _palPipe, _edgePipe, _blitPipe, _fxaaPipe;
        readonly IGpuBuffer _palBuf, _edgeBuf, _finalBuf, _fxaaBuf;

        IGpuResourceSet _paletteSet = null!;
        IGpuResourceSet _edgeFromColor = null!, _edgeFromPingA = null!;
        IGpuResourceSet _blitColorP = null!, _blitPingAP = null!, _blitPingBP = null!; // point sampler
        IGpuResourceSet _blitColorL = null!, _blitPingAL = null!, _blitPingBL = null!; // linear sampler
        IGpuResourceSet _fxaaFromColor = null!, _fxaaFromPingA = null!, _fxaaFromPingB = null!; // FXAA reads (linear)
        RenderResources? _bound;
        readonly float[] _palScratch = new float[PaletteScratchFloats]; // reused per frame: 64 vec4 palette + count/dither

        public PixelPostProcess(IGpuDevice gd, GpuOutputDescription pingOutput, GpuOutputDescription swapchainOutput)
        {
            _gd = gd;
            var f = gd.Factory;

            _palBuf = f.CreateBuffer(new GpuBufferDescription(PaletteBufferBytes, GpuBufferUsage.UniformBuffer)); // 64 vec4 + 1 vec4
            _edgeBuf = f.CreateBuffer(new GpuBufferDescription(EdgeBufferBytes, GpuBufferUsage.UniformBuffer)); // 4 vec4
            _finalBuf = f.CreateBuffer(new GpuBufferDescription(FinalBufferBytes, GpuBufferUsage.UniformBuffer)); // 2 vec4
            _fxaaBuf = f.CreateBuffer(new GpuBufferDescription(FxaaBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4 (rcpFrame)

            // Each pass is its own vert+frag pair (FullscreenVert is the shared vertex source).
            _palFrag = Pair(f, ShaderSources.PaletteFrag);
            _edgeFrag = Pair(f, ShaderSources.EdgeFrag);
            _blitFrag = Pair(f, ShaderSources.BlitFrag);
            _fxaaFrag = Pair(f, ShaderSources.FxaaFrag);

            _palLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Pal")));
            _edgeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("ColorTex"), T("NormalTex"), T("DepthTex"), S("Samp"), U("Edge")));
            _blitLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Final")));
            _fxaaLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Fxaa")));

            _palPipe = FullscreenPipeline(f, _palFrag, _palLayout, pingOutput);
            _edgePipe = FullscreenPipeline(f, _edgeFrag, _edgeLayout, pingOutput);
            _blitPipe = FullscreenPipeline(f, _blitFrag, _blitLayout, swapchainOutput);
            _fxaaPipe = FullscreenPipeline(f, _fxaaFrag, _fxaaLayout, pingOutput); // FXAA writes a ping (pre-blit)
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
            // FXAA samples its input bilinearly (the diagonal blend taps land between texels).
            _fxaaFromColor = f.CreateResourceSet(new GpuResourceSetDescription(_fxaaLayout, res.ColorTex, lin, _fxaaBuf));
            _fxaaFromPingA = f.CreateResourceSet(new GpuResourceSetDescription(_fxaaLayout, res.PingA, lin, _fxaaBuf));
            _fxaaFromPingB = f.CreateResourceSet(new GpuResourceSetDescription(_fxaaLayout, res.PingB, lin, _fxaaBuf));
            _bound = res; _boundW = res.Width; _boundH = res.Height;
        }
        int _boundW, _boundH;

        /// <summary>Upload post UBOs. Call BEFORE any SetFramebuffer this frame (no active render pass).
        /// <paramref name="runFxaa"/> is the caps-resolved FXAA decision from the scene (so an MSAA request the device
        /// can't honour can fall back to FXAA); it must match the value passed to <see cref="Run"/> so the flip parity
        /// lines up.</summary>
        public void PrepareUniforms(IGpuCommandList cl, RenderResources res, PixelPostProcessSettings s, in CameraDepth cam, bool runFxaa)
        {
            var pal = _palScratch;
            // Zero the colour region (MaxPaletteColors vec4 = 256 floats) so stale colors from a larger previous
            // palette don't leak. The two Info floats that follow (count, dither) are always rewritten below.
            const int colourFloats = MaxPaletteColors * 4; // 256
            Array.Clear(pal, 0, colourFloats);
            int count = Math.Min(s.ActivePalette.Colors.Length, MaxPaletteColors);
            for (int i = 0; i < count; i++)
            {
                var c = s.ActivePalette.Colors[i];
                pal[i * 4 + 0] = c.R; pal[i * 4 + 1] = c.G; pal[i * 4 + 2] = c.B; pal[i * 4 + 3] = c.A;
            }
            pal[colourFloats] = count; pal[colourFloats + 1] = s.Dither ? 1f : 0f; // Info.x = count, Info.y = ditherOn
            cl.UpdateBuffer<float>(_palBuf, 0, pal);

            var edge = new EdgeUbo
            {
                OutlineColor = s.OutlineColor,
                // Texel.xy = 1/size; .z = isPerspective (gates the Fix C linearization); .w = distance-fade on.
                Texel = new Vector4(1f / res.Width, 1f / res.Height,
                                    cam.IsPerspective ? 1f : 0f,
                                    (cam.IsPerspective && s.OutlineDistanceFade) ? 1f : 0f),
                // Thresh.x = depth threshold; .y = normal threshold; .z = near; .w = far.
                Thresh = new Vector4(s.OutlineDepthThreshold, s.OutlineNormalThreshold, cam.Near, cam.Far),
                // Fade.x = fade start (view depth); .y = fade end.
                Fade = new Vector4(s.OutlineFadeStart, s.OutlineFadeEnd, 0f, 0f),
            };
            cl.UpdateBuffer(_edgeBuf, 0, in edge);

            // FXAA reads the internal target's texel size (1/size) to place its neighbourhood taps.
            var rcp = new Vector4(1f / res.Width, 1f / res.Height, 0f, 0f);
            cl.UpdateBuffer(_fxaaBuf, 0, in rcp);

            // Bug A: each fullscreen post pass flips vertically; the on-screen orientation depends on the parity of
            // (quantize + outline + fxaa + blit). The blit cancels it so EVERY config is upright: flip the sampled V
            // iff the number of preceding post passes (quantize + outline + fxaa) is EVEN. The default (outline on,
            // quantize off, fxaa off) has 1 preceding pass (odd) => no flip => byte-identical to the committed
            // outline-on goldens. This rule depends only on the settings, matching Run's pass sequence exactly.
            int precedingPasses = (s.Quantize ? 1 : 0) + (s.Outline ? 1 : 0) + (runFxaa ? 1 : 0);
            float flipV = (precedingPasses % 2) == 0 ? 1f : 0f;

            var final = new FinalUbo
            {
                BgColor = s.BackgroundColor,
                Params = new Vector4(s.Starfield ? 1f : 0f, s.TransparentBackground ? 1f : 0f, flipV, 0),
            };
            cl.UpdateBuffer(_finalBuf, 0, in final);
        }

        public void Run(IGpuCommandList cl, RenderResources res, IGpuFramebuffer swapchainFB, PixelPostProcessSettings s, bool runFxaa)
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

            // FXAA (fast approximate AA): one cheap fullscreen pass on the near-final colour, before the blit. Writes
            // to the ping NOT currently holding src (the consumed one is free), so it never reads its own output.
            // runFxaa is the caps-resolved decision from the scene (AntiAliasingMode.Fxaa, or an MSAA request the
            // device can't honour falling back to FXAA); never set under the Pixelated retro path.
            if (runFxaa)
            {
                bool fromPingA = ReferenceEquals(src, res.PingA);
                bool toPingB = fromPingA;                    // PingA->PingB; ColorTex/PingB->PingA
                IGpuResourceSet set = ReferenceEquals(src, res.ColorTex) ? _fxaaFromColor
                                    : fromPingA ? _fxaaFromPingA : _fxaaFromPingB;
                cl.SetFramebuffer(toPingB ? res.PingBFB : res.PingAFB);
                cl.SetPipeline(_fxaaPipe);
                cl.SetGraphicsResourceSet(0, set);
                cl.Draw(3);
                src = toPingB ? res.PingB : res.PingA;
            }

            IGpuResourceSet blit = s.Pixelated
                ? (ReferenceEquals(src, res.ColorTex) ? _blitColorP : ReferenceEquals(src, res.PingA) ? _blitPingAP : _blitPingBP)
                : (ReferenceEquals(src, res.ColorTex) ? _blitColorL : ReferenceEquals(src, res.PingA) ? _blitPingAL : _blitPingBL);

            // Supersample downscale: the blit source carries a mip chain (RenderResources.Mipped) ONLY under a
            // MatchViewport downscale with a non-pixelated blit. Regenerating it here lets the trilinear LinearSampler
            // auto-pick LOD ~= log2(downscale ratio) - a correct multi-tap box at ANY factor, where the single
            // bilinear tap under-samples above 2:1. GenerateMipmaps ends the current render pass; the blit re-binds the
            // swapchain below. Never fires for FixedInternal / Pixelated / a 1:1-or-upscale blit (all single-mip), so
            // those stay byte-identical.
            if (src.MipLevels > 1) cl.GenerateMipmaps(src);

            cl.SetFramebuffer(swapchainFB);
            // Transparent clear when compositing offscreen, else opaque black. (The fullscreen blit overwrites
            // every pixel via OverrideBlend, so this mainly documents intent; the alpha is set in the shader.)
            cl.ClearColorTarget(0, s.TransparentBackground ? Color.Transparent : Color.Black);
            cl.SetPipeline(_blitPipe);
            cl.SetGraphicsResourceSet(0, blit);
            cl.Draw(3);
        }

        void DisposeSets()
        {
            _paletteSet?.Dispose(); _edgeFromColor?.Dispose(); _edgeFromPingA?.Dispose();
            _blitColorP?.Dispose(); _blitPingAP?.Dispose(); _blitPingBP?.Dispose();
            _blitColorL?.Dispose(); _blitPingAL?.Dispose(); _blitPingBL?.Dispose();
            _fxaaFromColor?.Dispose(); _fxaaFromPingA?.Dispose(); _fxaaFromPingB?.Dispose();
        }

        public void Dispose()
        {
            DisposeSets();
            _palPipe.Dispose(); _edgePipe.Dispose(); _blitPipe.Dispose(); _fxaaPipe.Dispose();
            _palLayout.Dispose(); _edgeLayout.Dispose(); _blitLayout.Dispose(); _fxaaLayout.Dispose();
            _palFrag.Dispose();
            _edgeFrag.Dispose();
            _blitFrag.Dispose();
            _fxaaFrag.Dispose();
            _palBuf.Dispose(); _edgeBuf.Dispose(); _finalBuf.Dispose(); _fxaaBuf.Dispose();
        }
    }
}
