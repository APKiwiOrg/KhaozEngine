using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The toggleable fullscreen post chain at the size of its targets. Two orders, selected by <see cref="HdrSettings"/>:
    /// HDR (the default) runs bloom (over-range, pre-tonemap) -> ACES tonemap -> palette quantize -> edge outline ->
    /// FXAA -> point-upscale to the swapchain, so the float16 scene is compressed to LDR after the highlights have
    /// bloomed. Legacy (<c>Hdr.Enabled = false</c>) keeps the historical quantize -> outline -> bloom -> FXAA ->
    /// upscale order, byte-identical to the pre-HDR output. Stages ping-pong between PingA/PingB (and, for bloom, the
    /// half-res BloomA/BloomB pair) so no pass reads its own output. The targets arrive as an
    /// <see cref="IPostChainTargets"/>: the internal <see cref="RenderResources"/>, or the display-resolution
    /// <see cref="TemporalPostTargets"/> after the temporal resolve, whose source alternates between two history targets.
    /// <para>On the display targets alone the chain adds the temporal sharpen (<see cref="SharpenRuns"/>): directly
    /// after the tonemap in the HDR order, and first in the legacy order, which has no tonemap. Either way it precedes
    /// quantize and the outline. No other chain builds or runs it, so their output is unchanged.</para>
    /// <para>On those display targets the chain also leaves out its outline, which ran ahead of the resolve on the
    /// internal images instead (PixelPostProcess.TemporalOutline.cs).</para>
    /// </summary>
    internal sealed partial class PixelPostProcess : IDisposable
    {
        // The typed post UBOs (internal so UboLayoutTests can size-check them against the GPU allocations).
        internal struct EdgeUbo { public Vector4 OutlineColor; public Vector4 Texel; public Vector4 Thresh; public Vector4 Fade; }
        internal struct FinalUbo { public Vector4 Params; }   // .x=transparentBg, .y=flipV
        internal struct BrightUbo { public Vector4 Params; }       // .x=threshold, .y=knee
        internal struct CompositeUbo { public Vector4 Params; }    // .x=intensity
        internal struct ToneUbo { public Vector4 Params; }         // .x=exposure, .y=operator, .z=chroma preservation
        internal struct ApplyUbo { public Vector4 Params; }        // .x=strength->UV scale, .y=max UV excursion clamp

        // Palette-quantize UBO sizing. The GLSL block is `vec4 Colors[MaxPaletteColors]; vec4 Info;` and the CPU
        // scratch mirrors it flat as floats. Named so UboLayoutTests can assert the scratch, the buffer, and the
        // GLSL array length all agree. (internal for the same reason.)
        internal const int MaxPaletteColors = 64;                         // GLSL: Colors[64]
        internal const int PaletteScratchFloats = (MaxPaletteColors + 1) * 4; // 64 colour vec4 + 1 info vec4 = 260 floats
        internal const uint PaletteBufferBytes = (uint)PaletteScratchFloats * sizeof(float); // 1040 bytes
        internal const uint EdgeBufferBytes = 64;                         // 4 vec4 (EdgeUbo)
        internal const uint FinalBufferBytes = 16;                        // 1 vec4 (FinalUbo)
        internal const uint FxaaBufferBytes = 16;                         // 1 vec4 (rcpFrame)
        internal const uint BrightBufferBytes = 16;                       // 1 vec4 (BrightUbo)
        internal const uint CompositeBufferBytes = 16;                    // 1 vec4 (CompositeUbo)
        internal const uint ToneBufferBytes = 16;                         // 1 vec4 (ToneUbo)
        internal const uint ApplyBufferBytes = 16;                        // 1 vec4 (ApplyUbo)

        // The stored distortion offset field is in world-ish units (per-sprite Strength baked in). This fixed scale
        // converts it to a UV excursion, and MaxExcursion clamps the total so stacked sprites cannot smear the whole
        // screen (D-S7). The host tunes magnitude per sprite via DistortionSprite.Strength, not these constants.
        internal const float DistortionUvScale = 0.04f;
        internal const float DistortionMaxExcursion = 0.05f;

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
        readonly IGpuShaderSet _palFrag, _edgeFrag, _blitFrag, _fxaaFrag, _toneFrag;
        readonly IGpuShaderSet _brightFrag, _blurFrag, _compositeFrag, _applyFrag;
        readonly IGpuResourceLayout _palLayout, _edgeLayout, _blitLayout, _fxaaLayout, _toneLayout;
        readonly IGpuResourceLayout _brightLayout, _blurLayout, _compositeLayout, _applyLayout;
        // The ping-output pipelines are rebuilt on a ping colour-format change (the HDR float16 <-> legacy UNorm
        // toggle), so they are NOT readonly. The blit pipeline targets the swapchain (format-fixed) and stays readonly.
        IGpuPipeline _palPipe, _edgePipe, _fxaaPipe, _tonePipe;
        IGpuPipeline _brightPipe, _blurPipe, _compositePipe, _applyPipe;
        readonly IGpuPipeline _blitPipe;
        readonly IGpuBuffer _palBuf, _edgeBuf, _finalBuf, _fxaaBuf, _toneBuf;
        readonly IGpuBuffer _brightBuf, _blurBufH, _blurBufV, _compositeBuf, _applyBuf;
        readonly float[] _palScratch = new float[PaletteScratchFloats]; // reused per frame: 64 vec4 palette + count/dither
        readonly float[] _blurScratchH = new float[BlurScratchFloats];  // reused per frame: Texel + Params(dir=horizontal) + Weights
        readonly float[] _blurScratchV = new float[BlurScratchFloats];  // reused per frame: Texel + Params(dir=vertical) + Weights

        // The temporal sharpen, built on the first frame temporal anti-aliasing sharpens (TemporalSharpenPass), so a
        // chain that never runs it owns none of its objects.
        TemporalSharpenPass? _sharpen;

        /// <summary>Whether the chain has built the temporal sharpen. Internal, for the tests.</summary>
        internal bool SharpenBuilt => _sharpen != null;

        /// <summary>The temporal sharpen, null until the chain builds it. Internal, for the tests.</summary>
        internal TemporalSharpenPass? SharpenForTests => _sharpen;

        /// <summary>Whether the sharpen runs on a chain over <paramref name="res"/> this frame: the targets are the
        /// display targets after the temporal resolve (<see cref="TemporalPostTargets"/>), temporal anti-aliasing is
        /// the effective mode and <see cref="TemporalSettings.ResolvedSharpness"/> is above zero, which a NaN
        /// sharpness is not. The scene hands the display targets only to the first render of a resolving frame, so
        /// the internal chain, which a frame without the resolve and a later render inside a resolving frame both
        /// run, never sharpens and stays byte-identical to a chain without the pass.</summary>
        internal static bool SharpenRuns(PixelPostProcessSettings s, IPostChainTargets res) =>
            res is TemporalPostTargets && s.EffectiveAaMode == AntiAliasingMode.Temporal
            && s.Temporal.ResolvedSharpness > 0f;

        public PixelPostProcess(IGpuDevice gd, GpuOutputDescription pingOutput, GpuOutputDescription swapchainOutput)
        {
            _gd = gd;
            var f = gd.Factory;

            _palBuf = f.CreateBuffer(new GpuBufferDescription(PaletteBufferBytes, GpuBufferUsage.UniformBuffer)); // 64 vec4 + 1 vec4
            _edgeBuf = f.CreateBuffer(new GpuBufferDescription(EdgeBufferBytes, GpuBufferUsage.UniformBuffer)); // 4 vec4
            _finalBuf = f.CreateBuffer(new GpuBufferDescription(FinalBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4
            _fxaaBuf = f.CreateBuffer(new GpuBufferDescription(FxaaBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4 (rcpFrame)
            _brightBuf = f.CreateBuffer(new GpuBufferDescription(BrightBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4
            _blurBufH = f.CreateBuffer(new GpuBufferDescription(BlurBufferBytes, GpuBufferUsage.UniformBuffer)); // Texel+Params(H)+Weights
            _blurBufV = f.CreateBuffer(new GpuBufferDescription(BlurBufferBytes, GpuBufferUsage.UniformBuffer)); // Texel+Params(V)+Weights
            _compositeBuf = f.CreateBuffer(new GpuBufferDescription(CompositeBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4
            _toneBuf = f.CreateBuffer(new GpuBufferDescription(ToneBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4
            _applyBuf = f.CreateBuffer(new GpuBufferDescription(ApplyBufferBytes, GpuBufferUsage.UniformBuffer)); // 1 vec4

            // Each pass is its own vert+frag pair (FullscreenVert is the shared vertex source), compiled through
            // the (vert,frag) cache so each unique pair compiles + disposes once.
            _palFrag = Pair(f, ShaderSources.PaletteFrag);
            _edgeFrag = Pair(f, ShaderSources.EdgeFrag);
            _blitFrag = Pair(f, ShaderSources.BlitFrag);
            _fxaaFrag = Pair(f, ShaderSources.FxaaFrag);
            _brightFrag = Pair(f, ShaderSources.BloomBrightFrag);
            _blurFrag = Pair(f, ShaderSources.BloomBlurFrag);
            _compositeFrag = Pair(f, ShaderSources.BloomCompositeFrag);
            _toneFrag = Pair(f, ShaderSources.TonemapFrag);
            _applyFrag = Pair(f, ShaderSources.DistortionApplyFrag);

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
            _toneLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), S("Samp"), U("Tone")));
            _applyLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("Src"), T("OffsetTex"), S("Samp"), U("Apply")));

            _palPipe = FullscreenPipeline(f, _palFrag, _palLayout, pingOutput);
            _edgePipe = FullscreenPipeline(f, _edgeFrag, _edgeLayout, pingOutput);
            _blitPipe = FullscreenPipeline(f, _blitFrag, _blitLayout, swapchainOutput);
            _fxaaPipe = FullscreenPipeline(f, _fxaaFrag, _fxaaLayout, pingOutput); // FXAA writes a ping (pre-blit)
            _tonePipe = FullscreenPipeline(f, _toneFrag, _toneLayout, pingOutput); // tonemap writes a ping (HDR mode)
            // Bloom bright-pass + blur write the half-res BloomA/BloomB pair, which share PingA/PingB's format
            // (R8G8B8A8UNorm, no depth) - GpuOutputDescription carries only format/sample-count (not size), so the
            // same pingOutput description is valid for a differently-sized framebuffer of the same format. The
            // composite pass writes back to a full-res ping, like palette/edge/fxaa.
            _brightPipe = FullscreenPipeline(f, _brightFrag, _brightLayout, pingOutput);
            _blurPipe = FullscreenPipeline(f, _blurFrag, _blurLayout, pingOutput);
            _compositePipe = FullscreenPipeline(f, _compositeFrag, _compositeLayout, pingOutput);
            _applyPipe = FullscreenPipeline(f, _applyFrag, _applyLayout, pingOutput); // apply writes a full-res ping (first chain pass)
            _pingOutput = pingOutput;
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

        // The bloom blur weights for the radius they were last built for. Rebuilt only when the radius changes, so a
        // steady frame with bloom on uploads them without allocating.
        readonly float[] _blurWeights = new float[2 * BloomMath.MaxRadius + 1];
        int _blurWeightsRadius = -1;

        /// <summary>Upload post UBOs. Call BEFORE any SetFramebuffer this frame (no active render pass).
        /// <paramref name="runFxaa"/> is the caps-resolved FXAA decision from the scene (so an MSAA request the device
        /// can't honour can fall back to FXAA); it must match the value passed to <see cref="Run"/> so the flip parity
        /// lines up.</summary>
        public void PrepareUniforms(IGpuCommandList cl, IPostChainTargets res, PixelPostProcessSettings s, in CameraDepth cam, bool runFxaa, bool distortionActive)
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

            bool bloomRuns = s.Bloom.Enabled && res.BloomAllocated;
            // The apply pass runs iff a distortion sprite was queued AND its offset field is allocated (mirrors the
            // bloomRuns pattern). It is the chain's FIRST pass in both modes, so it precedes the outline pass and the
            // blit and is counted in BOTH parities below.
            bool distortionRuns = distortionActive && res.DistortAllocated;
            // The temporal sharpen runs directly after the tonemap in the HDR order and first in the legacy order.
            // Either way it precedes the outline pass, so it joins both parities below. Its uniform write sits here
            // with the chain's own, before any framebuffer is bound this frame, and its draw comes later in the same
            // recording, in Run.
            bool sharpenRuns = SharpenRuns(s, res);
            if (sharpenRuns)
            {
                _sharpen ??= new TemporalSharpenPass(_gd, _pingOutput);
                _sharpen.Prepare(cl, res.PingA.Width, res.PingA.Height, s.Temporal.ResolvedSharpness);
            }
            // Under the temporal resolve the outline ran ahead of it (PixelPostProcess.TemporalOutline.cs), not in the
            // chain, so it joins neither parity. Every other chain keeps s.Outline exactly.
            bool outlineInChain = OutlineRunsInChain(s, res);

            // MRT-flip parity for the edge pass: every fullscreen chain pass flips the image vertically, but the
            // NormalTex/DepthColorTex the edge pass ALSO reads are raw MRT attachments that never pass through the
            // chain. When an ODD number of chain passes precede the outline pass (mirrors Run's per-mode order:
            // bloom + tonemap + sharpen + quantize in HDR, sharpen + quantize in legacy), the chain content arrives
            // vertically flipped relative to those raw textures, so the edge pass must flip its normal/depth sampling
            // to match (Fade.z, consumed by EdgeFrag). The historical golden-covered configs (legacy, quantize off)
            // compute 0 here and stay byte-identical. This also fixes the latent legacy quantize+outline mirror, where
            // the edge field rendered upside down relative to the palette-quantized image.
            int passesBeforeOutline = (distortionRuns ? 1 : 0) + (sharpenRuns ? 1 : 0) + (s.Hdr.Enabled
                ? (bloomRuns ? 1 : 0) + 1 + (s.Quantize ? 1 : 0)
                : (s.Quantize ? 1 : 0));
            float mrtFlip = (passesBeforeOutline & 1) == 1 ? 1f : 0f;

            var edge = new EdgeUbo
            {
                OutlineColor = s.OutlineColor,
                // Texel.xy = 1/size; .z = isPerspective (gates the Fix C linearization); .w = distance-fade on.
                Texel = new Vector4(1f / res.NormalTex.Width, 1f / res.NormalTex.Height,   // the steps cross the normal/depth attachments
                                    cam.IsPerspective ? 1f : 0f,
                                    (cam.IsPerspective && s.OutlineDistanceFade) ? 1f : 0f),
                // Thresh.x = depth threshold; .y = normal threshold; .z = near; .w = far.
                Thresh = new Vector4(s.OutlineDepthThreshold, s.OutlineNormalThreshold, cam.Near, cam.Far),
                // Fade.x = fade start (view depth), .y = fade end, .z = MRT-flip parity (see above).
                Fade = new Vector4(s.OutlineFadeStart, s.OutlineFadeEnd, mrtFlip, 0f),
            };
            cl.UpdateBuffer(_edgeBuf, 0, in edge);

            // FXAA reads the chain's own texel size (1/size) to place its neighbourhood taps.
            var rcp = new Vector4(1f / res.Width, 1f / res.Height, 0f, 0f);
            cl.UpdateBuffer(_fxaaBuf, 0, in rcp);

            if (bloomRuns)
            {
                float knee = MathF.Max(0f, s.Bloom.Knee);
                var bright = new BrightUbo { Params = new Vector4(s.Bloom.Threshold, knee, 0f, 0f) };
                cl.UpdateBuffer(_brightBuf, 0, in bright);

                int radius = Math.Clamp(s.Bloom.Radius, 0, BloomMath.MaxRadius);
                if (radius != _blurWeightsRadius)
                {
                    BloomMath.GaussianWeights(radius, _blurWeights);
                    _blurWeightsRadius = radius;
                }
                float[] weights = _blurWeights; // the first 2*radius+1 entries, symmetric about the centre
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

            if (s.Hdr.Enabled)
            {
                // .x = exposure (>= 0), .y = operator index (0 aces, 1 reinhard, 2 clamp), .z = chroma preservation [0,1].
                var tone = new ToneUbo { Params = new Vector4(MathF.Max(s.Hdr.Exposure, 0f), (float)(int)s.Hdr.Operator, Math.Clamp(s.Hdr.ChromaPreservation, 0f, 1f), 0f) };
                cl.UpdateBuffer(_toneBuf, 0, in tone);
            }

            if (distortionRuns)
            {
                var apply = new ApplyUbo { Params = new Vector4(DistortionUvScale, DistortionMaxExcursion, 0f, 0f) };
                cl.UpdateBuffer(_applyBuf, 0, in apply);
            }

            // Bug A: each fullscreen post pass flips vertically; the on-screen orientation depends on the parity of
            // how many ran. The blit cancels it so EVERY config is upright: flip the sampled V iff the number of
            // preceding post passes is EVEN. This rule is fully generic in the pass COUNT and order-independent - it
            // does not assume any particular default or chain order. The engine default (outline OFF, quantize off,
            // bloom off, fxaa off) with HDR off has 0 preceding passes (even) => flipV=1: the blit un-flips the single
            // scene render so the bare-default frame is upright (the same even-parity branch bloom-on already
            // exercises). That path is guarded on-device by DefaultPost_RendersUprightWithoutOutline and by
            // Golden3D_OutlineToggle_DoesNotFlip's outline-off branch. Pinning outline ON (as the committed 3D
            // goldens now do explicitly) restores 1 preceding pass (odd) => no flip => byte-identical to those
            // outline-on reference PNGs. Tonemap contributes exactly ONE main-chain pass whenever HDR is on (it runs
            // once, directly after bloom). Bloom contributes exactly ONE net pass (the composite pass that writes back
            // into the main ping chain) - the bright-pass + separable blur are an off-chain branch (see
            // BloomCompositeFrag's fixed internal un-flip) and do not add to the main chain's parity. This rule
            // depends only on the settings, matching Run's pass sequence exactly in BOTH the HDR and legacy orders.
            // The distortion apply pass adds exactly one net main-chain pass (the FIRST pass, before either mode's
            // branch), so it joins the blit flip parity too, and so does the temporal sharpen.
            int precedingPasses = (distortionRuns ? 1 : 0) + (sharpenRuns ? 1 : 0) + (s.Hdr.Enabled ? 1 : 0)
                                + (s.Quantize ? 1 : 0) + (outlineInChain ? 1 : 0) + (bloomRuns ? 1 : 0) + (runFxaa ? 1 : 0);
            float flipV = (precedingPasses % 2) == 0 ? 1f : 0f;

            var final = new FinalUbo
            {
                Params = new Vector4(s.TransparentBackground ? 1f : 0f, flipV, 0, 0),
            };
            cl.UpdateBuffer(_finalBuf, 0, in final);
        }

        public void Run(IGpuCommandList cl, IPostChainTargets res, IGpuFramebuffer swapchainFB, PixelPostProcessSettings s, bool runFxaa, bool distortionActive)
        {
            // Every set belongs to the targets BindTargets last bound, at the generation it bound them.
            if (!ReferenceEquals(res, _bound) || res.Generation != _boundGen)
                throw new InvalidOperationException(
                    "PixelPostProcess.Run was handed post chain targets BindTargets has not bound at their current "
                    + "generation. Call BindTargets with the targets this run reads first.");
            // The chain source this frame and the sets built over it (IPostChainTargets explains the slot).
            int slot = res.SourceSlot;
            IGpuTexture color = res.Source(slot);
            SourceSets fromSource = _fromSource[slot] ?? throw new InvalidOperationException(
                "PixelPostProcess.Run was handed a post chain source slot BindTargets did not build.");
            IGpuTexture src = color;
            bool bloomRuns = s.Bloom.Enabled && res.BloomAllocated;
            bool distortionRuns = distortionActive && res.DistortAllocated;
            bool sharpenRuns = SharpenRuns(s, res);
            bool outlineInChain = OutlineRunsInChain(s, res);   // under the resolve it ran ahead of it

            // Shared free-ping ping-pong for the single-input passes (tonemap / quantize / outline / FXAA): each
            // writes to the ping NOT holding src so no pass reads its own output. Source/PingB -> PingA, PingA ->
            // PingB. The resource set already carries the right textures per source, so the pass logic is uniform.
            void Simple(IGpuPipeline pipe, IGpuResourceSet fromColor, IGpuResourceSet fromPingA, IGpuResourceSet fromPingB)
            {
                bool fromA = ReferenceEquals(src, res.PingA);
                IGpuResourceSet set = ReferenceEquals(src, color) ? fromColor : fromA ? fromPingA : fromPingB;
                cl.SetFramebuffer(fromA ? res.PingBFB : res.PingAFB);
                cl.SetPipeline(pipe);
                cl.SetGraphicsResourceSet(0, set);
                cl.Draw(3);
                src = fromA ? res.PingB : res.PingA;
            }

            void RunQuantize() => Simple(_palPipe, fromSource.Palette, _paletteFromPingA, _paletteFromPingB);
            void RunOutline() => Simple(_edgePipe, fromSource.Edge, _edgeFromPingA, _edgeFromPingB);
            void RunTonemap() => Simple(_tonePipe, fromSource.Tone, _toneFromPingA, _toneFromPingB);
            void RunFxaa() => Simple(_fxaaPipe, fromSource.Fxaa, _fxaaFromPingA, _fxaaFromPingB);

            // The temporal sharpen picks its target as Simple does. Its input is the chain source (a history colour,
            // so at most two textures) or a ping, and TemporalSharpenPass caches one set per texture it is handed.
            void RunSharpen()
            {
                bool fromA = ReferenceEquals(src, res.PingA);
                _sharpen!.Draw(cl, src, fromA ? res.PingBFB : res.PingAFB);
                src = fromA ? res.PingB : res.PingA;
            }

            // Bloom: bright-pass -> separable blur (half-res) -> additive composite back into a full-res ping. Runs
            // only when the targets report BloomAllocated (Scene3D requests the half-res targets only while
            // Bloom.Enabled), so bloom off costs exactly zero extra passes. In HDR mode this runs FIRST, reading the
            // raw float16 scene so over-range cores halo before the tonemap compresses them. In legacy mode it runs
            // third, reading the already-LDR post src.
            void RunBloom()
            {
                IGpuResourceSet brightSet = ReferenceEquals(src, color) ? fromSource.Bright!
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

                bool compFromColor = ReferenceEquals(src, color);
                bool compFromPingA = ReferenceEquals(src, res.PingA);
                IGpuResourceSet compositeSet = compFromColor ? fromSource.Composite!
                                             : compFromPingA ? _compositePingABloomA! : _compositePingBBloomA!;
                // Write to the ping NOT currently holding src (mirrors the FXAA ping-pong), so composite never reads
                // its own output.
                bool toPingB = compFromPingA;                 // PingA->PingB, Source/PingB->PingA
                cl.SetFramebuffer(toPingB ? res.PingBFB : res.PingAFB);
                cl.SetPipeline(_compositePipe);
                cl.SetGraphicsResourceSet(0, compositeSet);
                cl.Draw(3);
                src = toPingB ? res.PingB : res.PingA;
            }

            // Distortion apply: the chain's FIRST pass in BOTH modes. Re-samples the chain source through the
            // accumulated offset field, so every camera-response pass that follows (bloom, tonemap, quantize, outline,
            // fxaa) sees the warped image. Writes Source -> PingA, then src follows the ping-pong like any other pass.
            // The FinalUbo/EdgeUbo parities already counted this pass in PrepareUniforms.
            if (distortionRuns)
            {
                cl.SetFramebuffer(res.PingAFB);
                cl.SetPipeline(_applyPipe);
                cl.SetGraphicsResourceSet(0, fromSource.Apply!);
                cl.Draw(3);
                src = res.PingA;
            }

            if (s.Hdr.Enabled)
            {
                // HDR order: bloom the over-range cores FIRST (float16, pre-tonemap) so hot values halo, then tonemap
                // the scene to LDR, then run the retro/AA passes on the tonemapped [0,1] result. The bloom-before-
                // quantize swap vs legacy is deliberate (see HdrSettings / the design record): retro palette games
                // that need bloom AFTER quantize stay on legacy mode. The temporal sharpen reads the tonemapped
                // image, which every operator leaves in 0 to 1, the display-referred range RCAS works in, so its final
                // clamp changes nothing. It runs ahead of quantize and outline, so it never sharpens a palette step or
                // a line.
                if (bloomRuns) RunBloom();
                RunTonemap();
                if (sharpenRuns) RunSharpen();
                if (s.Quantize) RunQuantize();
                if (outlineInChain) RunOutline();
                if (runFxaa) RunFxaa();
            }
            else
            {
                // Legacy order (byte-identical to the pre-HDR chain): quantize -> outline -> bloom -> fxaa. Pass-order
                // rationale (unchanged, see BloomSettings/docs):
                //  - bloom AFTER quantize so the glow composites on top of the (possibly posterized) palette colour
                //    instead of being posterized itself, which would band the halo.
                //  - bloom AFTER outline so the dark outline colour never blooms and the glow reads as sitting outside
                //    the silhouette line.
                //  - bloom BEFORE fxaa so fxaa also polishes the bloom composite's soft edges instead of adding an
                //    unaliased halo on top of an already-anti-aliased image.
                // The temporal sharpen runs first, since this order has no tonemap to follow: the scene colour is 8-bit
                // and already display-referred. The resolve's half-float output is floored at 0 but not clamped at 1,
                // so it can overshoot 1 slightly. The limiter gives no lobe to a pixel with a tap above 1 in its cross,
                // and the final clamp cuts that overshoot to 1, as the 8-bit ping it writes would anyway. Every pass
                // after it then reads an image in 0 to 1, as it does without the resolve.
                if (sharpenRuns) RunSharpen();
                if (s.Quantize) RunQuantize();
                if (outlineInChain) RunOutline();
                if (bloomRuns) RunBloom();
                if (runFxaa) RunFxaa();
            }

            IGpuResourceSet blit = s.Pixelated
                ? (ReferenceEquals(src, color) ? fromSource.BlitPoint : ReferenceEquals(src, res.PingA) ? _blitPingAP : _blitPingBP)
                : (ReferenceEquals(src, color) ? fromSource.BlitLinear : ReferenceEquals(src, res.PingA) ? _blitPingAL : _blitPingBL);

            // Downscale: the blit source carries a mip chain (RenderResources.Mipped) only when Scene3D.WantsMipDownsample
            // decided this frame is a genuine downscale with a non-pixelated blit - a MatchViewport supersample, or a
            // FixedInternal target on a window smaller than it with PixelPostProcessSettings.MipFilterFixedInternalDownscale
            // opted in. Regenerating it here lets the trilinear LinearSampler auto-pick LOD ~= log2(downscale ratio) - a
            // correct multi-tap box at ANY factor, where the single bilinear tap under-samples above 2:1. GenerateMipmaps
            // ends the current render pass, and the blit re-binds the swapchain below. Never fires for Pixelated, a 1:1-or-
            // upscale blit, a FixedInternal downscale with the opt-in flag off, or the temporal display chain (all
            // single-mip), so those stay byte-identical.
            if (src.MipLevels > 1) cl.GenerateMipmaps(src);

            cl.SetFramebuffer(swapchainFB);
            // Transparent clear when compositing offscreen, else opaque black. (The fullscreen blit overwrites
            // every pixel via OverrideBlend, so this mainly documents intent. The alpha is set in the shader.)
            cl.ClearColorTarget(0, s.TransparentBackground ? Color.Transparent : Color.Black);
            cl.SetPipeline(_blitPipe);
            cl.SetGraphicsResourceSet(0, blit);
            cl.Draw(3);
        }

        public void Dispose()
        {
            DisposeSets();
            _sharpen?.Dispose();
            DisposeTemporalOutline();
            _palPipe.Dispose(); _edgePipe.Dispose(); _blitPipe.Dispose(); _fxaaPipe.Dispose(); _tonePipe.Dispose();
            _brightPipe.Dispose(); _blurPipe.Dispose(); _compositePipe.Dispose(); _applyPipe.Dispose();
            _palLayout.Dispose(); _edgeLayout.Dispose(); _blitLayout.Dispose(); _fxaaLayout.Dispose(); _toneLayout.Dispose();
            _brightLayout.Dispose(); _blurLayout.Dispose(); _compositeLayout.Dispose(); _applyLayout.Dispose();
            // Dispose each UNIQUE compiled shader set once (the cache is the single owner; _palFrag/_edgeFrag/... are
            // aliases into it, so disposing them again would double-dispose a shared set).
            foreach (var set in _shaderCache.Values) set.Dispose();
            _shaderCache.Clear();
            _palBuf.Dispose(); _edgeBuf.Dispose(); _finalBuf.Dispose(); _fxaaBuf.Dispose(); _toneBuf.Dispose();
            _brightBuf.Dispose(); _blurBufH.Dispose(); _blurBufV.Dispose(); _compositeBuf.Dispose(); _applyBuf.Dispose();
        }
    }
}
