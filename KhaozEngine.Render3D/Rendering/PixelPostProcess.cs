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
    /// palette quantization (+ Bayer dither) -> depth/normal edge outline -> bloom (bright-pass + separable blur +
    /// additive composite) -> FXAA -> point-upscale to the swapchain. Stages ping-pong between PingA/PingB (and,
    /// for bloom, the half-res BloomA/BloomB pair) so no pass reads its own output.
    /// </summary>
    internal sealed class PixelPostProcess : IDisposable
    {
        // The typed post UBOs (internal so UboLayoutTests can size-check them against the GPU allocations).
        internal struct EdgeUbo { public Vector4 OutlineColor; public Vector4 Texel; public Vector4 Thresh; public Vector4 Fade; }
        internal struct FinalUbo { public Vector4 BgColor; public Vector4 Params; }
        internal struct BrightUbo { public Vector4 Params; }       // .x=threshold, .y=knee
        internal struct CompositeUbo { public Vector4 Params; }    // .x=intensity

        // Palette-quantize UBO sizing. The GLSL block is `vec4 Colors[MaxPaletteColors]; vec4 Info;` and the CPU
        // scratch mirrors it flat as floats. Named so UboLayoutTests can assert the scratch, the buffer, and the
        // GLSL array length all agree. (internal for the same reason.)
        internal const int MaxPaletteColors = 64;                         // GLSL: Colors[64]
        internal const int PaletteScratchFloats = (MaxPaletteColors + 1) * 4; // 64 colour vec4 + 1 info vec4 = 260 floats
        internal const uint PaletteBufferBytes = (uint)PaletteScratchFloats * sizeof(float); // 1040 bytes
        internal const uint EdgeBufferBytes = 64;                         // 4 vec4 (EdgeUbo)
        internal const uint FinalBufferBytes = 32;                        // 2 vec4 (FinalUbo)
        internal const uint FxaaBufferBytes = 16;                         // 1 vec4 (rcpFrame)
        internal const uint BrightBufferBytes = 16;                       // 1 vec4 (BrightUbo)
        internal const uint CompositeBufferBytes = 16;                    // 1 vec4 (CompositeUbo)

        // Bloom blur UBO sizing. GLSL: `vec4 Texel; vec4 Params; vec4 Weights[BlurWeightSlots];` (BloomBlurFrag).
        // The CPU scratch mirrors it flat as floats (Texel + Params + Weights), like the palette scratch above.
        // TWO buffers (H and V) exist because the blur direction differs per axis and both draws happen inside
        // Run's active render pass, where UpdateBuffer must not be called (PrepareUniforms uploads everything
        // BEFORE any SetFramebuffer this frame) - so both directions are pre-baked into separate buffers here.
        internal const int BlurWeightSlots = BloomMath.MaxRadius + 1;         // GLSL: Weights[9] (radius 0..8)
        internal const int BlurScratchFloats = 4 + 4 + BlurWeightSlots * 4;   // Texel + Params + Weights = 44 floats
        internal const uint BlurBufferBytes = (uint)BlurScratchFloats * sizeof(float); // 176 bytes

        readonly IGpuDevice _gd;
        // Each fullscreen pass is a (FullscreenVert, <pass frag>) shader pair. They are compiled through a
        // (vert,frag)-keyed cache so an identical pair is cross-compiled and disposed exactly once. The post passes
        // have distinct frags, but the cache keeps the shared vertex source from being recompiled if any pair ever
        // recurs, and gives a single owner list for correct one-time disposal. The public Gpu API
        // (CreateShadersFromSpirv compiles a PAIR and returns an opaque IGpuShaderSet) is unchanged.
        readonly Dictionary<(string vert, string frag), IGpuShaderSet> _shaderCache = new();
        readonly IGpuShaderSet _palFrag, _edgeFrag, _blitFrag, _fxaaFrag;
        readonly IGpuShaderSet _brightFrag, _blurFrag, _compositeFrag;
        readonly IGpuResourceLayout _palLayout, _edgeLayout, _blitLayout, _fxaaLayout;
        readonly IGpuResourceLayout _brightLayout, _blurLayout, _compositeLayout;
        readonly IGpuPipeline _palPipe, _edgePipe, _blitPipe, _fxaaPipe;
        readonly IGpuPipeline _brightPipe, _blurPipe, _compositePipe;
        readonly IGpuBuffer _palBuf, _edgeBuf, _finalBuf, _fxaaBuf;
        readonly IGpuBuffer _brightBuf, _blurBufH, _blurBufV, _compositeBuf;

        IGpuResourceSet _paletteSet = null!;
        IGpuResourceSet _edgeFromColor = null!, _edgeFromPingA = null!;
        IGpuResourceSet _blitColorP = null!, _blitPingAP = null!, _blitPingBP = null!; // point sampler
        IGpuResourceSet _blitColorL = null!, _blitPingAL = null!, _blitPingBL = null!; // linear sampler
        IGpuResourceSet _fxaaFromColor = null!, _fxaaFromPingA = null!, _fxaaFromPingB = null!; // FXAA reads (linear)
        // Bloom resource sets, only built while RenderResources.BloomAllocated (the half-res targets exist).
        IGpuResourceSet? _brightFromColor, _brightFromPingA, _brightFromPingB;   // bright-pass reads the full-res src (linear)
        IGpuResourceSet? _blurHFromBloomA, _blurVFromBloomB;                     // horizontal BloomA->BloomB (via _blurBufH), vertical BloomB->BloomA (via _blurBufV)
        IGpuResourceSet? _compositeColorBloomA, _compositePingABloomA, _compositePingBBloomA; // composite reads (full-res src, BloomA)
        RenderResources? _bound;
        readonly float[] _palScratch = new float[PaletteScratchFloats]; // reused per frame: 64 vec4 palette + count/dither
        readonly float[] _blurScratchH = new float[BlurScratchFloats];  // reused per frame: Texel + Params(dir=horizontal) + Weights
        readonly float[] _blurScratchV = new float[BlurScratchFloats];  // reused per frame: Texel + Params(dir=vertical) + Weights

        public PixelPostProcess(IGpuDevice gd, GpuOutputDescription pingOutput, GpuOutputDescription swapchainOutput)
        {
            _gd = gd;
            var f = gd.Factory;

            _palBuf = f.CreateBuffer(new GpuBufferDescription(PaletteBufferBytes, GpuBufferUsage.UniformBuffer)); // 64 vec4 + 1 vec4
            _edgeBuf = f.CreateBuffer(new GpuBufferDescription(EdgeBufferBytes, GpuBufferUsage.UniformBuffer)); // 4 vec4
            _finalBuf = f.CreateBuffer(new GpuBufferDescription(FinalBufferBytes, GpuBufferUsage.UniformBuffer)); // 2 vec4
            _fxaaBuf = f.CreateBuffer(new GpuBufferDescription(FxaaBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4 (rcpFrame)
            _brightBuf = f.CreateBuffer(new GpuBufferDescription(BrightBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4
            _blurBufH = f.CreateBuffer(new GpuBufferDescription(BlurBufferBytes, GpuBufferUsage.UniformBuffer)); // Texel+Params(H)+Weights
            _blurBufV = f.CreateBuffer(new GpuBufferDescription(BlurBufferBytes, GpuBufferUsage.UniformBuffer)); // Texel+Params(V)+Weights
            _compositeBuf = f.CreateBuffer(new GpuBufferDescription(CompositeBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4

            // Each pass is its own vert+frag pair (FullscreenVert is the shared vertex source), compiled through
            // the (vert,frag) cache so each unique pair compiles + disposes once.
            _palFrag = Pair(f, ShaderSources.PaletteFrag);
            _edgeFrag = Pair(f, ShaderSources.EdgeFrag);
            _blitFrag = Pair(f, ShaderSources.BlitFrag);
            _fxaaFrag = Pair(f, ShaderSources.FxaaFrag);
            _brightFrag = Pair(f, ShaderSources.BloomBrightFrag);
            _blurFrag = Pair(f, ShaderSources.BloomBlurFrag);
            _compositeFrag = Pair(f, ShaderSources.BloomCompositeFrag);

            _palLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Pal")));
            _edgeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("ColorTex"), T("NormalTex"), T("DepthTex"), S("Samp"), U("Edge")));
            _blitLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Final")));
            _fxaaLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Fxaa")));
            _brightLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Bright")));
            _blurLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Blur")));
            _compositeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), T("Bloom"), S("Samp"), U("Composite")));

            _palPipe = FullscreenPipeline(f, _palFrag, _palLayout, pingOutput);
            _edgePipe = FullscreenPipeline(f, _edgeFrag, _edgeLayout, pingOutput);
            _blitPipe = FullscreenPipeline(f, _blitFrag, _blitLayout, swapchainOutput);
            _fxaaPipe = FullscreenPipeline(f, _fxaaFrag, _fxaaLayout, pingOutput); // FXAA writes a ping (pre-blit)
            // Bloom bright-pass + blur write the half-res BloomA/BloomB pair, which share PingA/PingB's format
            // (R8G8B8A8UNorm, no depth) - GpuOutputDescription carries only format/sample-count (not size), so the
            // same pingOutput description is valid for a differently-sized framebuffer of the same format. The
            // composite pass writes back to a full-res ping, like palette/edge/fxaa.
            _brightPipe = FullscreenPipeline(f, _brightFrag, _brightLayout, pingOutput);
            _blurPipe = FullscreenPipeline(f, _blurFrag, _blurLayout, pingOutput);
            _compositePipe = FullscreenPipeline(f, _compositeFrag, _compositeLayout, pingOutput);
        }

        static GpuResourceLayoutElement T(string n) => new(n, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement S(string n) => new(n, GpuResourceKind.Sampler, GpuShaderStages.Fragment);
        static GpuResourceLayoutElement U(string n) => new(n, GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment);

        // Compile (or reuse) the (FullscreenVert, frag) pair. Memoized on the source strings so a repeated pair is
        // cross-compiled once and, via _shaderCache, disposed once. The shared FullscreenVert source is the vert of
        // every pass, so this is where "compile the shared fullscreen VS once per unique pair" lives without
        // reaching into the opaque IGpuShaderSet or changing the public Gpu API.
        IGpuShaderSet Pair(IGpuResourceFactory f, string frag)
        {
            var key = (ShaderSources.FullscreenVert, frag);
            if (_shaderCache.TryGetValue(key, out var cached)) return cached;
            var set = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, frag);
            _shaderCache[key] = set;
            return set;
        }

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

        /// <summary>Build the per-target resource sets. Call on construction and whenever the targets resize (incl.
        /// a bloom enable/disable toggle, which (re)allocates or frees <see cref="RenderResources.BloomA"/>/
        /// <see cref="RenderResources.BloomB"/>).</summary>
        public void BindTargets(RenderResources res)
        {
            if (ReferenceEquals(_bound, res) && res.Width == _boundW && res.Height == _boundH
                && res.BloomAllocated == _boundBloom) return;
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

            if (res.BloomAllocated)
            {
                // Bright-pass reads whichever full-res target holds the current chain source, bilinearly (a soft
                // half-res downsample tap, matching the FXAA/blit-linear precedent) - built for all three possible
                // sources (ColorTex/PingA/PingB) so Run can pick the right one without a runtime resource-set build.
                _brightFromColor = f.CreateResourceSet(new GpuResourceSetDescription(_brightLayout, res.ColorTex, lin, _brightBuf));
                _brightFromPingA = f.CreateResourceSet(new GpuResourceSetDescription(_brightLayout, res.PingA, lin, _brightBuf));
                _brightFromPingB = f.CreateResourceSet(new GpuResourceSetDescription(_brightLayout, res.PingB, lin, _brightBuf));
                // Separable blur ping-pongs within the half-res pair: horizontal BloomA->BloomB (direction baked
                // into _blurBufH), vertical BloomB->BloomA (direction baked into _blurBufV) - two buffers because
                // both draws happen inside Run's active render pass, where UBOs cannot be re-uploaded mid-pass.
                _blurHFromBloomA = f.CreateResourceSet(new GpuResourceSetDescription(_blurLayout, res.BloomA!, lin, _blurBufH));
                _blurVFromBloomB = f.CreateResourceSet(new GpuResourceSetDescription(_blurLayout, res.BloomB!, lin, _blurBufV));
                // Composite reads the full-res chain source (Src) + the blurred half-res bloom (BloomA, the blur's
                // final write target) and writes to a full-res ping; built for all three possible Src sources.
                _compositeColorBloomA = f.CreateResourceSet(new GpuResourceSetDescription(_compositeLayout, res.ColorTex, res.BloomA!, lin, _compositeBuf));
                _compositePingABloomA = f.CreateResourceSet(new GpuResourceSetDescription(_compositeLayout, res.PingA, res.BloomA!, lin, _compositeBuf));
                _compositePingBBloomA = f.CreateResourceSet(new GpuResourceSetDescription(_compositeLayout, res.PingB, res.BloomA!, lin, _compositeBuf));
            }

            _bound = res; _boundW = res.Width; _boundH = res.Height; _boundBloom = res.BloomAllocated;
        }
        int _boundW, _boundH;
        bool _boundBloom;

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

            bool bloomRuns = s.Bloom.Enabled && res.BloomAllocated;
            if (bloomRuns)
            {
                float knee = MathF.Max(0f, s.Bloom.Knee);
                var bright = new BrightUbo { Params = new Vector4(s.Bloom.Threshold, knee, 0f, 0f) };
                cl.UpdateBuffer(_brightBuf, 0, in bright);

                int radius = Math.Clamp(s.Bloom.Radius, 0, BloomMath.MaxRadius);
                float[] weights = BloomMath.GaussianWeights(radius); // length 2*radius+1, symmetric about the centre
                // Weights[i].x = weight for tap i (i=0 = centre = weights[radius] in the symmetric array).
                const int weightsBase = 8; // Texel (4 floats) + Params (4 floats)
                void FillBlurScratch(float[] scratch, float dirX, float dirY)
                {
                    Array.Clear(scratch, 0, scratch.Length);
                    scratch[0] = 1f / res.BloomWidth; scratch[1] = 1f / res.BloomHeight; // Texel.xy
                    scratch[4] = radius; scratch[5] = dirX; scratch[6] = dirY;           // Params.xyz
                    for (int i = 0; i <= radius; i++) scratch[weightsBase + i * 4] = weights[radius + i];
                }
                FillBlurScratch(_blurScratchH, 1f, 0f);
                FillBlurScratch(_blurScratchV, 0f, 1f);
                cl.UpdateBuffer<float>(_blurBufH, 0, _blurScratchH);
                cl.UpdateBuffer<float>(_blurBufV, 0, _blurScratchV);

                var composite = new CompositeUbo { Params = new Vector4(s.Bloom.Intensity, 0f, 0f, 0f) };
                cl.UpdateBuffer(_compositeBuf, 0, in composite);
            }

            // Bug A: each fullscreen post pass flips vertically; the on-screen orientation depends on the parity of
            // (quantize + outline + bloom-composite + fxaa). The blit cancels it so EVERY config is upright: flip
            // the sampled V iff the number of preceding post passes is EVEN. This rule is fully generic in the pass
            // count - it does not assume any particular default. The engine default (outline OFF, quantize off,
            // bloom off, fxaa off) has 0 preceding passes (even) => flipV=1: the blit un-flips the single scene
            // render so the bare-default frame is upright (the same even-parity branch bloom-on already exercises).
            // That outline-off default path is guarded on-device by DefaultPost_RendersUprightWithoutOutline and by
            // Golden3D_OutlineToggle_DoesNotFlip's outline-off branch. Pinning outline ON (as the committed 3D
            // goldens now do explicitly) restores 1 preceding pass (odd) => no flip => byte-identical to those
            // outline-on reference PNGs. Bloom contributes exactly ONE net pass to this count (the composite pass
            // that writes back into the main ping chain) - the bright-pass + separable blur are an off-chain branch
            // (see BloomCompositeFrag's fixed internal un-flip) and do not themselves add to the main chain's parity.
            // This rule depends only on the settings, matching Run's pass sequence exactly.
            int precedingPasses = (s.Quantize ? 1 : 0) + (s.Outline ? 1 : 0) + (bloomRuns ? 1 : 0) + (runFxaa ? 1 : 0);
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

            // Bloom: bright-pass -> separable blur (half-res) -> additive composite back into a full-res ping.
            // Pass-order decisions (see BloomSettings/docs for the full rationale):
            //  - AFTER Quantize: bloom composites a smooth glow on top of the (possibly posterized) palette colour
            //    instead of being posterized itself, which would band the halo.
            //  - AFTER Outline: the dark outline colour never blooms, and the glow reads as sitting on top of /
            //    outside the silhouette line, matching how an outline+glow stylized look is normally composed.
            //  - BEFORE FXAA: FXAA's edge-smoothing then also polishes the bloom composite's soft edges (the halo
            //    blending into the background) instead of anti-aliasing a pre-bloom image and adding an unaliased
            //    bloom on top of it.
            // Runs only when RenderResources.BloomAllocated (Scene3D only requests the half-res targets while
            // Bloom.Enabled), so bloom off costs exactly zero extra passes - the historical chain, byte-identical.
            if (s.Bloom.Enabled && res.BloomAllocated)
            {
                IGpuResourceSet brightSet = ReferenceEquals(src, res.ColorTex) ? _brightFromColor!
                                          : ReferenceEquals(src, res.PingA) ? _brightFromPingA! : _brightFromPingB!;
                cl.SetFramebuffer(res.BloomAFB!);
                cl.SetPipeline(_brightPipe);
                cl.SetGraphicsResourceSet(0, brightSet);
                cl.Draw(3);

                // Separable gaussian blur: horizontal (BloomA -> BloomB), then vertical (BloomB -> BloomA). Always
                // both passes run (even Radius=0, a 1-tap no-op blur) so the bloom branch is a FIXED 3 fullscreen
                // passes from Src regardless of the Radius knob - the composite shader's fixed vertical-unflip
                // correction (see BloomCompositeFrag) assumes exactly this count.
                cl.SetFramebuffer(res.BloomBFB!);
                cl.SetPipeline(_blurPipe);
                cl.SetGraphicsResourceSet(0, _blurHFromBloomA!);
                cl.Draw(3);

                cl.SetFramebuffer(res.BloomAFB!);
                cl.SetPipeline(_blurPipe);
                cl.SetGraphicsResourceSet(0, _blurVFromBloomB!);
                cl.Draw(3);

                bool compFromColor = ReferenceEquals(src, res.ColorTex);
                bool compFromPingA = ReferenceEquals(src, res.PingA);
                IGpuResourceSet compositeSet = compFromColor ? _compositeColorBloomA!
                                             : compFromPingA ? _compositePingABloomA! : _compositePingBBloomA!;
                // Write to the ping NOT currently holding src (mirrors the FXAA ping-pong below), so composite never
                // reads its own output.
                bool toPingB = compFromPingA;                 // PingA->PingB; ColorTex/PingB->PingA
                cl.SetFramebuffer(toPingB ? res.PingBFB : res.PingAFB);
                cl.SetPipeline(_compositePipe);
                cl.SetGraphicsResourceSet(0, compositeSet);
                cl.Draw(3);
                src = toPingB ? res.PingB : res.PingA;
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
            _brightFromColor?.Dispose(); _brightFromPingA?.Dispose(); _brightFromPingB?.Dispose();
            _blurHFromBloomA?.Dispose(); _blurVFromBloomB?.Dispose();
            _compositeColorBloomA?.Dispose(); _compositePingABloomA?.Dispose(); _compositePingBBloomA?.Dispose();
            _brightFromColor = _brightFromPingA = _brightFromPingB = null;
            _blurHFromBloomA = _blurVFromBloomB = null;
            _compositeColorBloomA = _compositePingABloomA = _compositePingBBloomA = null;
        }

        public void Dispose()
        {
            DisposeSets();
            _palPipe.Dispose(); _edgePipe.Dispose(); _blitPipe.Dispose(); _fxaaPipe.Dispose();
            _brightPipe.Dispose(); _blurPipe.Dispose(); _compositePipe.Dispose();
            _palLayout.Dispose(); _edgeLayout.Dispose(); _blitLayout.Dispose(); _fxaaLayout.Dispose();
            _brightLayout.Dispose(); _blurLayout.Dispose(); _compositeLayout.Dispose();
            // Dispose each UNIQUE compiled shader set once (the cache is the single owner; _palFrag/_edgeFrag/... are
            // aliases into it, so disposing them again would double-dispose a shared set).
            foreach (var set in _shaderCache.Values) set.Dispose();
            _shaderCache.Clear();
            _palBuf.Dispose(); _edgeBuf.Dispose(); _finalBuf.Dispose(); _fxaaBuf.Dispose();
            _brightBuf.Dispose(); _blurBufH.Dispose(); _blurBufV.Dispose(); _compositeBuf.Dispose();
        }
    }
}
