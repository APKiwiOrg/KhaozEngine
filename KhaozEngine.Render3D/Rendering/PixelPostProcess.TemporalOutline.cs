using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE EDGE OUTLINE AHEAD OF THE TEMPORAL RESOLVE. Under <see cref="AntiAliasing.Temporal"/> the post chain runs at
    /// display resolution after the resolve, where the edge pass would read the jittered internal normal and depth and
    /// its one-pixel lines would move with the jitter. So under temporal anti-aliasing the outline runs here instead,
    /// in one draw over the internal lit colour and the opaque-only copy, before the resolve reads them, and the display
    /// chain skips it. <see cref="ShaderSources.TemporalEdgeFrag"/> runs the edge test once per pixel and writes both
    /// outlined images, addressing pixels by <c>gl_FragCoord</c>, so they keep the orientation the resolve reads and no
    /// chain parity counts the pass. A later render inside a resolving frame, which runs the internal chain without a
    /// resolve, and every other mode run the outline in the chain exactly as before, and nothing here is built. The
    /// scene releases the pass on the first resolving frame without the outline and on the first frame without the
    /// resolve (<see cref="ReleaseTemporalOutline"/>).
    /// </summary>
    internal sealed partial class PixelPostProcess
    {
        /// <summary>The draws <see cref="RunTemporalOutline"/> records: one, writing both outlined images.</summary>
        internal const int TemporalOutlineDrawCalls = 1;

        IGpuResourceLayout? _temporalEdgeLayout;
        IGpuPipeline? _temporalEdgePipe;
        IGpuResourceSet? _temporalEdgeSet;
        IGpuFramebuffer? _temporalEdgeTargets;
        GpuOutputDescription _temporalEdgeOutput;
        IGpuTexture? _temporalEdgeColor, _temporalEdgeOpaque, _temporalEdgeNormal, _temporalEdgeDepth;
        IGpuTexture? _temporalEdgePingA, _temporalEdgePingB;

        /// <summary>Whether the outline runs ahead of the resolve this frame: it is on and the effective anti-aliasing
        /// mode is <see cref="AntiAliasingMode.Temporal"/>, which <see cref="PixelPostProcessSettings.Pixelated"/>
        /// refuses.</summary>
        internal static bool TemporalOutlineRuns(PixelPostProcessSettings s) =>
            s.Outline && s.EffectiveAaMode == AntiAliasingMode.Temporal;

        /// <summary>Whether a chain over <paramref name="res"/> runs its own outline pass: the outline is on, unless the
        /// targets are the display targets after the resolve (<see cref="TemporalPostTargets"/>) and the outline ran
        /// ahead of it. The scene hands those targets only to the render that resolves, so a later render's internal
        /// chain keeps its outline.</summary>
        internal static bool OutlineRunsInChain(PixelPostProcessSettings s, IPostChainTargets res) =>
            s.Outline && !(res is TemporalPostTargets && TemporalOutlineRuns(s));

        /// <summary>Whether the pass ahead of the resolve is built: from its first run until
        /// <see cref="ReleaseTemporalOutline"/>. Internal, for the tests.</summary>
        internal bool TemporalOutlineBuilt => _temporalEdgePipe != null;

        /// <summary>
        /// Outline the internal lit colour into <c>res.PingA</c> and the opaque-only copy into <c>res.PingB</c> in one
        /// draw with two colour outputs, the two images the resolve reads as its scene and opaque colour this frame.
        /// Reads the edge block <see cref="PrepareUniforms"/> uploaded this frame, whose texel size is the internal
        /// normal target's. Outlining both with the same edge keeps the reactive estimate from reading the lines as
        /// transparent content. Call after every internal-resolution colour writer and before the resolve. It binds
        /// its own framebuffer. What a new target or input replaces goes to <paramref name="retired"/>.
        /// </summary>
        internal void RunTemporalOutline(IGpuCommandList cl, RenderResources res, IGpuTexture opaque,
            GpuRetireQueue retired)
        {
            if (!res.PingsAllocated)
                throw new InvalidOperationException("The outline ahead of the temporal resolve writes the internal "
                    + "ping pair, which is not allocated. The scene holds it while this pass runs.");
            IGpuResourceFactory f = _gd.Factory;
            _temporalEdgeLayout ??= f.CreateResourceLayout(new GpuResourceLayoutDescription(
                T("ColorTex"), T("NormalTex"), T("DepthTex"), S("Samp"), U("Edge"), T("OpaqueTex")));
            // Keyed on the textures themselves, as the resolve's own sets are, so a change elsewhere in the internal
            // targets, such as the bloom pair coming or going, rebuilds nothing.
            if (!ReferenceEquals(res.PingA, _temporalEdgePingA) || !ReferenceEquals(res.PingB, _temporalEdgePingB))
            {
                retired.Retire(_temporalEdgeTargets);
                _temporalEdgeTargets = f.CreateFramebuffer(null, res.PingA, res.PingB);
                _temporalEdgePingA = res.PingA;
                _temporalEdgePingB = res.PingB;
            }
            GpuOutputDescription output = _temporalEdgeTargets!.Outputs;
            if (_temporalEdgePipe is null || !SamePingFormat(output, _temporalEdgeOutput))
            {
                retired.Retire(_temporalEdgePipe);
                _temporalEdgePipe = TemporalResolveRenderer.Fullscreen(f, Pair(f, ShaderSources.TemporalEdgeFrag),
                    _temporalEdgeLayout, output);
                _temporalEdgeOutput = output;
            }
            if (!ReferenceEquals(res.ColorTex, _temporalEdgeColor) || !ReferenceEquals(opaque, _temporalEdgeOpaque)
                || !ReferenceEquals(res.NormalTex, _temporalEdgeNormal)
                || !ReferenceEquals(res.DepthColorTex, _temporalEdgeDepth))
            {
                retired.Retire(_temporalEdgeSet);
                _temporalEdgeSet = f.CreateResourceSet(new GpuResourceSetDescription(_temporalEdgeLayout,
                    res.ColorTex, res.NormalTex, res.DepthColorTex, _gd.PointSampler, _edgeBuf, opaque));
                _temporalEdgeColor = res.ColorTex;
                _temporalEdgeOpaque = opaque;
                _temporalEdgeNormal = res.NormalTex;
                _temporalEdgeDepth = res.DepthColorTex;
            }

            cl.SetFramebuffer(_temporalEdgeTargets);
            cl.SetPipeline(_temporalEdgePipe);
            cl.SetGraphicsResourceSet(0, _temporalEdgeSet!);
            cl.Draw(3);
        }

        /// <summary>Let go of the pass: its set, framebuffer, pipeline and layout go to <paramref name="retired"/>,
        /// since a frame the device has not finished may still bind them, and <see cref="TemporalOutlineBuilt"/> turns
        /// false. The compiled program stays in the chain's shader cache. Does nothing when the pass is not built. The
        /// scene calls it on every resolving render without the outline and on every frame without the resolve.</summary>
        internal void ReleaseTemporalOutline(GpuRetireQueue retired)
        {
            if (_temporalEdgePipe is null && _temporalEdgeLayout is null) return;
            retired.Retire(_temporalEdgeSet, _temporalEdgeTargets, _temporalEdgePipe);
            retired.Retire(_temporalEdgeLayout);
            ForgetTemporalOutline();
        }

        // From Dispose, after the device drained. The shader set belongs to _shaderCache, which Dispose already empties.
        void DisposeTemporalOutline()
        {
            _temporalEdgeSet?.Dispose();
            _temporalEdgeTargets?.Dispose();
            _temporalEdgePipe?.Dispose();
            _temporalEdgeLayout?.Dispose();
            ForgetTemporalOutline();
        }

        void ForgetTemporalOutline()
        {
            _temporalEdgeSet = null;
            _temporalEdgeTargets = null;
            _temporalEdgePipe = null;
            _temporalEdgeLayout = null;
            _temporalEdgeColor = _temporalEdgeOpaque = _temporalEdgeNormal = _temporalEdgeDepth = null;
            _temporalEdgePingA = _temporalEdgePingB = null;
        }
    }
}
